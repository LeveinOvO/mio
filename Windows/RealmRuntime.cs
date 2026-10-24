using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class RealmRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<Guid, RealmProcess> Processes = [];
    private static readonly Dictionary<Guid, string> Errors = [];
    private static bool _stopping;
    internal static string? TestExecutable { get; set; }
    private static Task<RealmProcess> StartProcessAsync(ForwardRule rule) => RealmProcess.StartAsync(rule, TestExecutable);

    public static async Task<IReadOnlyList<RealmRuntimeState>> ReadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            return RealmStore.Read().Rules.Select(r => new RealmRuntimeState(r,
                Processes.TryGetValue(r.Id, out var p) && p.Running, Errors.GetValueOrDefault(r.Id) ?? "")).ToList();
        }
        finally { Gate.Release(); }
    }

    public static async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping) return;
            var config = RealmStore.Read();
            foreach (var entry in config.Rules.Where(r => r.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Processes.TryGetValue(entry.Id, out var existing) && existing.Running) continue;
                if (existing is not null) { await existing.DisposeAsync(); Processes.Remove(entry.Id); }
                try { Processes[entry.Id] = await StartProcessAsync(entry.Applied); Errors.Remove(entry.Id); }
                catch (Exception error) { Errors[entry.Id] = error.Message; }
            }
        }
        finally { Gate.Release(); }
    }

    public static async Task CheckDnsAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping) return;
            var config = RealmStore.Read();
            var baseline = config.Rules.ToList();
            foreach (var original in config.Rules.Where(r => r.Enabled && DnsTargets.IsHostname(r.Desired)).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ForwardRule resolved;
                try { resolved = await DnsTargets.ResolveRuleAsync(original.Desired, original.Applied.ConnectAddress, new SystemTargetResolver(), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Replace(config, original with { Message = "解析失败，保留旧 IP：" + error.Message, LastChecked = DateTimeOffset.UtcNow });
                    continue;
                }
                if (!resolved.Matches(original.Applied))
                {
                    try
                    {
                        await RestartAsync(original.Id, resolved, original.Applied);
                        Replace(config, original with { Applied = resolved, Message = "目标 IP 已更新", LastChecked = DateTimeOffset.UtcNow });
                    }
                    catch (Exception error) { Replace(config, original with { Message = "目标更新失败：" + error.Message, LastChecked = DateTimeOffset.UtcNow }); }
                }
                else Replace(config, original with { Message = "解析正常，IP 未变化", LastChecked = DateTimeOffset.UtcNow });
            }
            if (config.Rules.Count > 0)
            {
                await using var lease = await ConfigurationStore.AcquireWriteLockAsync(cancellationToken);
                try { RealmStore.Save(config); }
                catch
                {
                    foreach (var old in baseline.Where(r => r.Enabled))
                    {
                        var changed = config.Rules.FirstOrDefault(r => r.Id == old.Id);
                        if (changed is not null && !changed.Applied.Matches(old.Applied))
                        {
                            try { await RestartAsync(old.Id, old.Applied, changed.Applied); }
                            catch (Exception error) { Errors[old.Id] = error.Message; }
                        }
                    }
                    throw;
                }
            }
        }
        finally { Gate.Release(); }
    }

    public static async Task ExecuteAsync(RuleOperation operation, ForwardRule? original, ForwardRule? desired,
        RealmRule? expected)
    {
        await Gate.WaitAsync();
        try
        {
            if (_stopping) throw new InvalidOperationException("程序正在退出。");
            await using var lease = await ConfigurationStore.AcquireWriteLockAsync(CancellationToken.None);
            var config = RealmStore.Read();
            var entry = original is null ? null : config.Rules.FirstOrDefault(r => r.Applied.Matches(original));
            if (operation != RuleOperation.Add && entry is null)
                throw new InvalidOperationException("转发规则已变化，请刷新后重试。");
            if (expected is not null && entry is not null && (expected.Id != entry.Id || expected.Enabled != entry.Enabled ||
                !expected.Desired.Matches(entry.Desired) || !expected.Applied.Matches(entry.Applied)))
                throw new InvalidOperationException("转发配置已变化，请刷新后重试。");
            if (operation == RuleOperation.Delete)
            {
                if (Processes.Remove(entry!.Id, out var p)) await p.DisposeAsync();
                config.Rules.Remove(entry); Errors.Remove(entry.Id);
                try { RealmStore.Save(config); }
                catch { if (entry.Enabled) Processes[entry.Id] = await StartProcessAsync(entry.Applied); throw; }
                return;
            }
            if (operation == RuleOperation.Disable)
            {
                if (Processes.Remove(entry!.Id, out var p)) await p.DisposeAsync();
                Replace(config, entry with { Enabled = false, Message = "转发已停用" });
                try { RealmStore.Save(config); }
                catch { if (entry.Enabled) Processes[entry.Id] = await StartProcessAsync(entry.Applied); throw; }
                return;
            }
            if (operation is not (RuleOperation.Add or RuleOperation.Edit or RuleOperation.Enable)) throw new ArgumentException("不支持此操作。");
            desired ??= entry!.Desired;
            RuleValidation.Validate(desired);
            if (config.Rules.Any(r => r.Id != entry?.Id &&
                (r.Desired.HasSameListener(desired) || (r.Enabled && r.Desired.ConflictsWith(desired)))))
                throw new InvalidOperationException("监听地址、端口和协议与其他转发规则冲突。");
            var enabled = operation != RuleOperation.Edit || entry!.Enabled;
            var applied = enabled ? await DnsTargets.ResolveRuleAsync(desired, entry?.Applied.ConnectAddress, new SystemTargetResolver(), CancellationToken.None)
                : desired with { ConnectAddress = entry!.Applied.ConnectAddress };
            var next = new RealmRule(entry?.Id ?? Guid.NewGuid(), desired, applied, enabled, enabled ? "转发运行中" : "转发已停用", DateTimeOffset.UtcNow);
            if (enabled)
            {
                await RestartAsync(next.Id, applied, entry?.Applied);
            }
            if (entry is not null) config.Rules.Remove(entry);
            config.Rules.Add(next);
            try { RealmStore.Save(config); }
            catch
            {
                if (Processes.Remove(next.Id, out var p)) await p.DisposeAsync();
                if (entry?.Enabled == true) Processes[entry.Id] = await StartProcessAsync(entry.Applied);
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    private static void Replace(RealmConfiguration config, RealmRule updated)
    { config.Rules.RemoveAll(r => r.Id == updated.Id); config.Rules.Add(updated); }

    private static async Task RestartAsync(Guid id, ForwardRule next, ForwardRule? previous)
    {
        var hadPrevious = Processes.Remove(id, out var process);
        if (process is not null) await process.DisposeAsync();
        try { Processes[id] = await StartProcessAsync(next); Errors.Remove(id); }
        catch (Exception error)
        {
            if (hadPrevious && previous is not null)
            {
                try { Processes[id] = await StartProcessAsync(previous); }
                catch (Exception rollback) { Errors[id] = rollback.Message; }
            }
            throw new InvalidOperationException("转发启动失败：" + error.Message, error);
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync();
        try
        {
            _stopping = true;
            foreach (var process in Processes.Values) await process.DisposeAsync();
            Processes.Clear();
        }
        finally { Gate.Release(); }
    }
}

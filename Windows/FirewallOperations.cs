using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class FirewallOperations
{
    public static async Task ConfigureAsync(RealmRule expectedRule, FirewallScope scope,
        OwnedFirewallRule? expected)
    {
        var snapshot = expectedRule.Applied;
        FirewallValidation.Validate(scope);
        if (scope.ListenerKey != snapshot.ListenerKey) throw new ArgumentException("防火墙的协议、监听地址和端口与转发规则不一致。");
        await using var lease = await ConfigurationStore.AcquireWriteLockAsync(CancellationToken.None);
        var actual = RealmStore.Read().Rules.FirstOrDefault(r => r.Applied.HasSameListener(snapshot));
        if (actual is null || actual.Id != expectedRule.Id || !actual.Applied.Matches(snapshot) || actual.Enabled != expectedRule.Enabled)
            throw new InvalidOperationException("转发配置已变化，请刷新后重试。");
        var config = FirewallStore.Read();
        var previous = config.Rules.FirstOrDefault(r => r.Scope.ListenerKey == scope.ListenerKey);
        if (!FirewallOwnership.Matches(previous, expected)) throw new InvalidOperationException("防火墙配置已变化，请重新打开配置窗口。");
        var backend = new WindowsFirewallBackend();
        if (previous is not null) FirewallOwnership.EnsureUnchanged(previous, backend);
        var entry = previous is null ? new OwnedFirewallRule(Guid.NewGuid(), scope, null) : previous with { Scope = scope };
        // Reserve the unique name before writing. A crash leaves a record, not an orphan allow rule.
        config.Rules.RemoveAll(r => r.Id == entry.Id);
        config.Rules.Add(entry);
        FirewallStore.Save(config);
        OwnedFirewallRule applied;
        try { applied = FirewallOwnership.Apply(entry, actual.Enabled, backend); }
        catch
        {
            // If creation never reached the system collection, don't leave a misleading pending grant.
            if (previous is null && backend.Find(FirewallOwnership.Name(entry.Id)).Count == 0)
            {
                config.Rules.RemoveAll(r => r.Id == entry.Id);
                FirewallStore.Save(config);
            }
            throw;
        }
        config.Rules.RemoveAll(r => r.Id == entry.Id);
        config.Rules.Add(applied);
        FirewallStore.Save(config);
    }

    public static async Task RemoveAsync(OwnedFirewallRule expected)
    {
        await using var lease = await ConfigurationStore.AcquireWriteLockAsync(CancellationToken.None);
        var config = FirewallStore.Read();
        var current = config.Rules.FirstOrDefault(r => r.Id == expected.Id);
        if (current is null || !FirewallOwnership.Matches(current, expected)) throw new InvalidOperationException("防火墙配置已变化，请刷新后重试。");
        var backend = new WindowsFirewallBackend();
        FirewallOwnership.EnsureUnchanged(current, backend);
        if (backend.Find(FirewallOwnership.Name(current.Id)).Count > 0) backend.Remove(FirewallOwnership.Name(current.Id));
        if (backend.Find(FirewallOwnership.Name(current.Id)).Count != 0) throw new IOException("防火墙规则删除后仍存在。");
        config.Rules.Remove(current);
        FirewallStore.Save(config);
    }

    // Preflight before changing a forwarding rule. Foreign changes cause the entire item to be rejected.
    public static async Task<OwnedFirewallRule?> PrepareAsync(ForwardRule original, RuleOperation operation, ForwardRule? updated,
        RealmRule? expected)
    {
        await using var lease = await ConfigurationStore.AcquireWriteLockAsync(CancellationToken.None);
        var config = FirewallStore.Read();
        var entry = config.Rules.FirstOrDefault(r => r.Scope.ListenerKey == original.ListenerKey);
        if (entry is null) return null;
        var actual = RealmStore.Read().Rules.FirstOrDefault(r => r.Applied.HasSameListener(original));
        if (actual is null || !actual.Applied.Matches(original))
            throw new InvalidOperationException("端口转发已被修改，未更改对应防火墙规则。请刷新后重试。");
        if (expected is not null && (expected.Id != actual.Id || expected.Enabled != actual.Enabled || !expected.Desired.Matches(actual.Desired)))
            throw new InvalidOperationException("转发配置已变化，请刷新后重试。");
        if (updated is not null && config.Rules.Any(r => r.Id != entry.Id && r.Scope.ListenerKey == updated.ListenerKey))
            throw new InvalidOperationException("新的监听地址和端口已有其他防火墙规则配置，不能覆盖。");
        var backend = new WindowsFirewallBackend();
        FirewallOwnership.EnsureUnchanged(entry, backend);
        if (operation is RuleOperation.Delete or RuleOperation.Disable ||
            operation == RuleOperation.Edit && updated is not null && !original.HasSameListener(updated))
        {
            entry = FirewallOwnership.Apply(entry, false, backend);
            config.Rules.RemoveAll(r => r.Id == entry.Id);
            config.Rules.Add(entry);
            FirewallStore.Save(config);
        }
        return entry;
    }

    public static async Task CompleteAsync(OwnedFirewallRule? expected, RuleOperation operation, ForwardRule? updated)
    {
        if (expected is null) return;
        if (operation == RuleOperation.Delete) { await RemoveAsync(expected); return; }
        await using var lease = await ConfigurationStore.AcquireWriteLockAsync(CancellationToken.None);
        var config = FirewallStore.Read();
        var current = config.Rules.FirstOrDefault(r => r.Id == expected.Id);
        if (current is null || !FirewallOwnership.Matches(current, expected)) throw new InvalidOperationException("转发已更新，但防火墙设置已被修改，请重新打开“防火墙”检查。");
        var scope = updated is null ? current.Scope : current.Scope with
            { ListenAddress = updated.ListenAddress, ListenPort = updated.ListenPort, Protocol = updated.Protocol };
        var entry = RealmStore.Read().Rules.FirstOrDefault(r => r.Desired.ListenerKey == scope.ListenerKey);
        var backend = new WindowsFirewallBackend();
        var applied = FirewallOwnership.Apply(current with { Scope = scope }, entry?.Enabled == true, backend);
        config.Rules.RemoveAll(r => r.Id == expected.Id);
        config.Rules.Add(applied);
        FirewallStore.Save(config);
    }
}

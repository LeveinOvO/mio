using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PortForwarder.Core;

namespace PortForwarder.Windows;

public sealed record NativeHostState(List<RealmRuntimeState> Rules, TrayMonitorStatus Monitor,
    FirewallConfiguration FirewallConfig, Dictionary<string, List<FirewallSnapshot>> FirewallActual, string FirewallError);

public static class NativeHostClient
{
    internal static string? TestPipeName { get; set; }
    internal static string? TestHostPath { get; set; }
    public static string PipeName => TestPipeName ?? "Mio.Native." + WindowsIdentity.GetCurrent().User!.Value;
    public static string HostPath => TestHostPath ?? Path.Combine(AppContext.BaseDirectory, "Mio.Host.exe");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    public static async Task EnsureRunningAsync()
    {
        if (await IsRunningAsync()) return;
        if (Environment.GetCommandLineArgs().Contains("--host-ui"))
        {
            // Native host may still be restoring rules when it opens the initial window.
            for (var i = 0; i < 40; i++) { await Task.Delay(100); if (await IsRunningAsync()) return; }
        }
        if (!File.Exists(HostPath)) throw new FileNotFoundException("未找到原生托盘宿主，请使用完整发布目录。", HostPath);
        var start = new ProcessStartInfo(HostPath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.Arguments = "--no-ui";
        using var process = Process.Start(start);
        for (var i = 0; i < 40; i++) { if (await IsRunningAsync()) return; await Task.Delay(100); }
        throw new IOException("原生宿主未能启动，请重新打开程序。");
    }

    public static async Task<bool> IsRunningAsync()
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(100);
            VerifyServer(pipe);
            // A probe sends a valid request; an empty connection must not stall the host.
            await WriteRequestAsync(pipe, new { Command = "State" });
            return (await ReadResponseAsync(pipe, TimeSpan.FromSeconds(15))).GetProperty("Success").GetBoolean();
        }
        catch { return false; }
    }

    public static Task<NativeHostState> StateAsync() => SendAsync<NativeHostState>(new { Command = "State" }, data: true);
    public static Task<RouteState> RoutesAsync() => SendAsync<RouteState>(new { Command = "Routes" }, data: true);
    public static Task<RuleResult> RouteOperationAsync(string operation, RouteEntry? expected, RouteDraft? updated) =>
        SendAsync<RuleResult>(new { Command = "RouteOperation", Operation = operation, Expected = expected, Updated = updated });
    public static Task<RuleResult> OperationAsync(RuleOperation operation, ForwardRule? original, ForwardRule? updated,
        RealmRule? expected = null, List<RuleBatchItem>? items = null) => items is null
        ? SendAsync<RuleResult>(new { Command = "Operation", Operation = operation.ToString(), Original = original, Updated = updated, Expected = expected })
        : SendAsync<RuleResult>(new { Command = "Batch", Items = items.Select(i => new { Operation = i.Operation.ToString(), i.Original, i.Updated, i.Expected }) });
    public static Task<RuleResult> ConfigureFirewallAsync(RealmRule rule, FirewallScope scope, OwnedFirewallRule? expected) =>
        SendAsync<RuleResult>(new { Command = "ConfigureFirewall", Rule = rule, Scope = scope, Expected = expected });
    public static Task<RuleResult> RemoveFirewallAsync(OwnedFirewallRule expected) => SendAsync<RuleResult>(new { Command = "RemoveFirewall", Expected = expected });
    public static Task<RuleResult> ShutdownAsync() => SendAsync<RuleResult>(new { Command = "Shutdown" });

    private static async Task<T> SendAsync<T>(object request, bool data = false)
    {
        await Gate.WaitAsync();
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000);
            VerifyServer(pipe);
            await WriteRequestAsync(pipe, request);
            var response = await ReadResponseAsync(pipe, TimeSpan.FromMinutes(15));
            if (data && !response.GetProperty("Success").GetBoolean()) throw new IOException(response.GetProperty("Message").GetString());
            return (data ? response.GetProperty("Data") : response).Deserialize<T>() ?? throw new IOException("宿主响应不完整。");
        }
        finally { Gate.Release(); }
    }

    private static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("无法核验原生宿主身份。");
        using var process = Process.GetProcessById((int)pid);
        var path = process.MainModule?.FileName;
        if (!string.Equals(path, HostPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("宿主进程路径不匹配。");
    }

    private static async Task WriteRequestAsync(Stream pipe, object request)
    {
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request)); await writer.FlushAsync();
    }
    private static async Task<JsonElement> ReadResponseAsync(Stream pipe, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadLineAsync(cancellation.Token);
        if (text is null || text.Length > 4 * 1024 * 1024) throw new IOException("宿主响应无效。");
        await pipe.WriteAsync(new byte[] { 1 }, cancellation.Token);
        await pipe.FlushAsync(cancellation.Token);
        using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
    }

    public static FirewallStatusSnapshot FirewallStatus(NativeHostState state)
    {
        var entries = state.FirewallConfig.Rules.ToDictionary(r => r.Scope.ListenerKey,
            r => FirewallRowStatus.Evaluate(r, state.FirewallActual.GetValueOrDefault(FirewallOwnership.Name(r.Id)) ?? []));
        return new FirewallStatusSnapshot(entries, string.IsNullOrEmpty(state.FirewallError) ? null : state.FirewallError);
    }
}

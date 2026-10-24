using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PortForwarder.Core;
using PortForwarder.Windows;

internal static class NativeHostTests
{
    public static async Task RunAsync(string hostPath, string corePath)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "native-host-test-" + Guid.NewGuid().ToString("N"));
        var pipe = "PortForwarder.Test." + Guid.NewGuid().ToString("N");
        ConfigurationStore.TestDirectory = folder;
        ConfigurationStore.EnsureProtectedDirectory();
        ConfigurationStore.TestDirectory = null;
        // Copy only firewall metadata for read-only comparison; the test never invokes firewall writes.
        NativeHostClient.TestPipeName = pipe; NativeHostClient.TestHostPath = hostPath;
        using var stop = new CancellationTokenSource();
        var tcpTarget = new TcpListener(IPAddress.Loopback, 0); tcpTarget.Start();
        using var udpTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var echoes = new[] { EchoTcp(tcpTarget, stop.Token), EchoUdp(udpTarget, stop.Token) };
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var tcp = new ForwardRule("127.0.0.1", port, "127.0.0.1", ((IPEndPoint)tcpTarget.LocalEndpoint).Port);
        var udp = tcp with { Protocol = ForwardProtocol.Udp, ConnectPort = ((IPEndPoint)udpTarget.Client.LocalEndPoint!).Port };
        var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "--headless", "--config-directory", folder, "--core", corePath, "--pipe", pipe }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start)!;
        var stderr = host.StandardError.ReadToEndAsync(); var stdout = host.StandardOutput.ReadToEndAsync();
        var checks = 0;
        void Check(bool condition, string text)
        { if (!condition) throw new Exception("FAIL: " + text); checks++; Console.WriteLine("PASS: " + text); }
        async Task Success(Task<RuleResult> call)
        { var result = await call; if (!result.Success) throw new Exception(result.Message); }
        try
        {
            for (var i = 0; i < 60; i++)
            {
                if (host.HasExited) throw new Exception("Host exited: " + await stderr);
                if (await NativeHostClient.IsRunningAsync()) break;
                await Task.Delay(100);
            }
            var state = await NativeHostClient.StateAsync();
            Check(state.Rules.Count == 0, "Native host starts with isolated empty configuration");
            Check(state.FirewallError == "", "Native Windows firewall read-only status enumeration succeeds");
            Check(state.FirewallConfig.Rules.Count == 0, "Fresh SQLite host configuration has no firewall metadata");
            // Avoid matching real user listener scope in later simulated operations.
            // The host keeps firewall metadata but these randomly allocated loopback entries don't overlap it.
            await Success(NativeHostClient.OperationAsync(RuleOperation.Add, null, tcp));
            await Success(NativeHostClient.OperationAsync(RuleOperation.Add, null, udp));
            state = await NativeHostClient.StateAsync();
            Check(state.Rules.Count == 2 && state.Rules.All(r => r.Running && r.Rule.Enabled), "Native host manages same-port TCP and UDP cores");
            Check(await RequestTcp(port) == 42 && await RequestUdp(port) == 42, "Native-host managed TCP/UDP forward data correctly");
            // Each request uses a new connection, like closing/reopening the GUI client.
            await Task.Delay(1000);
            Check(await RequestTcp(port) == 42 && await RequestUdp(port) == 42, "Closing configuration client connections does not stop forwarding");
            var tcpEntry = state.Rules.Single(r => r.Rule.Applied.Protocol == ForwardProtocol.Tcp).Rule;
            await Success(NativeHostClient.OperationAsync(RuleOperation.Edit, tcpEntry.Applied, tcp with { ConnectAddress = "localhost." }, tcpEntry));
            state = await NativeHostClient.StateAsync();
            tcpEntry = state.Rules.Single(r => r.Rule.Applied.Protocol == ForwardProtocol.Tcp).Rule;
            Check(tcpEntry.Desired.ConnectAddress == "localhost." && tcpEntry.Applied.ConnectAddress == "127.0.0.1", "Native resolver retains domain and supplies final IPv4");
            await Success(NativeHostClient.OperationAsync(RuleOperation.Disable, tcpEntry.Applied, null, tcpEntry));
            state = await NativeHostClient.StateAsync();
            tcpEntry = state.Rules.Single(r => r.Rule.Id == tcpEntry.Id).Rule;
            Check(!tcpEntry.Enabled && await RequestUdp(port) == 42, "Native host disables one rule without affecting the other");
            Check(tcpEntry.Desired.ConnectAddress == "localhost." && tcpEntry.Applied.ConnectAddress == "127.0.0.1",
                "Disabled domain keeps final IPv4 in applied configuration");
            await Success(NativeHostClient.OperationAsync(RuleOperation.Enable, tcpEntry.Applied, null, tcpEntry));
            Check(await RequestTcp(port) == 42, "Native host re-enables domain forwarding");
            var duplicate = await NativeHostClient.OperationAsync(RuleOperation.Add, null, tcp);
            Check(!duplicate.Success && await RequestTcp(port) == 42, "Duplicate listener rejected without disrupting active forwarding");
            state = await NativeHostClient.StateAsync();
            tcpEntry = state.Rules.Single(r => r.Rule.Applied.Protocol == ForwardProtocol.Tcp).Rule;
            var stale = await NativeHostClient.OperationAsync(RuleOperation.Disable, tcpEntry.Applied, null,
                tcpEntry with { Enabled = false });
            Check(!stale.Success && await RequestTcp(port) == 42, "Native host refuses stale state snapshot");
            var invalidDns = await NativeHostClient.OperationAsync(RuleOperation.Edit, tcpEntry.Applied,
                tcpEntry.Desired with { ConnectAddress = "no-such-host.invalid" }, tcpEntry);
            Check(!invalidDns.Success && await RequestTcp(port) == 42, "Invalid DNS edit keeps original core running");
            state = await NativeHostClient.StateAsync();
            var udpEntry = state.Rules.Single(r => r.Rule.Applied.Protocol == ForwardProtocol.Udp).Rule;
            await Success(NativeHostClient.OperationAsync(RuleOperation.Delete, udpEntry.Applied, null, udpEntry));
            await Success(NativeHostClient.OperationAsync(RuleOperation.Edit, tcpEntry.Applied,
                udp with { ConnectAddress = "localhost." }, tcpEntry));
            state = await NativeHostClient.StateAsync();
            udpEntry = state.Rules.Single().Rule;
            Check(udpEntry.Id == tcpEntry.Id && udpEntry.Applied.Protocol == ForwardProtocol.Udp && await RequestUdp(port) == 42,
                "Native host switches TCP to UDP preserving rule identity");
            await Success(NativeHostClient.OperationAsync(RuleOperation.Edit, udpEntry.Applied, tcpEntry.Desired, udpEntry));
            Check(await RequestTcp(port) == 42 && !SocketOwners.IsOccupied(udp), "Native host switches UDP back to TCP and removes UDP listener");
            await Success(NativeHostClient.OperationAsync(RuleOperation.Add, null, udp));
            state = await NativeHostClient.StateAsync();
            var items = state.Rules.Select(r => new RuleBatchItem(RuleOperation.Disable, r.Rule.Applied, null, r.Rule)).ToList();
            await Success(NativeHostClient.OperationAsync(RuleOperation.Batch, null, null, items: items));
            Check((await NativeHostClient.StateAsync()).Rules.All(r => !r.Rule.Enabled && !r.Running), "Native batch disable retains configuration and stops cores");
            state = await NativeHostClient.StateAsync();
            items = state.Rules.Select(r => new RuleBatchItem(RuleOperation.Enable, r.Rule.Applied, null, r.Rule)).ToList();
            await Success(NativeHostClient.OperationAsync(RuleOperation.Batch, null, null, items: items));
            Check(await RequestTcp(port) == 42 && await RequestUdp(port) == 42, "Native batch re-enable forwards both protocols");
            var beforeMemory = host.PrivateMemorySize64;
            for (var i = 0; i < 100; i++) await NativeHostClient.StateAsync();
            host.Refresh();
            Check(host.PrivateMemorySize64 - beforeMemory < 8 * 1024 * 1024,
                "100 state refreshes have bounded native private-memory growth");
            host.Refresh();
            Console.WriteLine($"MEMORY: Native host private {host.PrivateMemorySize64 / 1024.0 / 1024:F2} MiB; working set {host.WorkingSet64 / 1024.0 / 1024:F2} MiB; threads {host.Threads.Count}; EXE {new FileInfo(hostPath).Length} bytes.");
            await Success(NativeHostClient.ShutdownAsync());
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(host.ExitCode == 0, "Native host exits cleanly on explicit shutdown");
            Check(!SocketOwners.IsOccupied(tcp) && !SocketOwners.IsOccupied(udp), "Native host shutdown removes Realm listeners");
            using var restoredHost = Process.Start(start)!;
            var restoredErrors = restoredHost.StandardError.ReadToEndAsync(); _ = restoredHost.StandardOutput.ReadToEndAsync();
            try
            {
                for (var i = 0; i < 60; i++) { if (await NativeHostClient.IsRunningAsync()) break; await Task.Delay(100); }
                for (var i = 0; i < 50 && (!SocketOwners.IsOccupied(tcp) || !SocketOwners.IsOccupied(udp)); i++) await Task.Delay(100);
                Check(await RequestTcp(port) == 42 && await RequestUdp(port) == 42, "Native host restart restores saved enabled rules without UI");
                restoredHost.Kill(entireProcessTree: false); // Only parent: Job Objects must clean the cores.
                await restoredHost.WaitForExitAsync();
                for (var i = 0; i < 50 && (SocketOwners.IsOccupied(tcp) || SocketOwners.IsOccupied(udp)); i++) await Task.Delay(100);
                Check(!SocketOwners.IsOccupied(tcp) && !SocketOwners.IsOccupied(udp), "Unexpected native host termination cleans Realm children through Job Objects");
            }
            finally { if (!restoredHost.HasExited) { restoredHost.Kill(true); await restoredHost.WaitForExitAsync(); } Console.WriteLine("RESTORED STDERR: " + await restoredErrors); }
        }
        finally
        {
            if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); }
            stop.Cancel(); tcpTarget.Stop(); udpTarget.Close();
            try { await Task.WhenAll(echoes); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
            if (Directory.Exists(folder))
            {
                for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
                {
                    try { Directory.Delete(folder, recursive: true); }
                    catch (IOException) when (attempt < 19) { await Task.Delay(100); }
                    catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(100); }
                }
                if (Directory.Exists(folder)) throw new IOException("无法清理原生宿主测试目录：" + folder);
            }
            NativeHostClient.TestPipeName = null; NativeHostClient.TestHostPath = null;
            Console.WriteLine("HOST STDERR: " + await stderr); _ = await stdout;
        }
        Console.WriteLine($"NATIVE HOST COMPLETE: {checks} checks passed, temporary loopback/config cleaned; no system forwarding/firewall modifications.");
    }
    private static async Task<int> RequestTcp(int port)
    { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port); await tcp.GetStream().WriteAsync(new byte[] { 42 }); var response = new byte[1]; await tcp.GetStream().ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); return response[0]; }
    private static async Task<int> RequestUdp(int port)
    { using var udp = new UdpClient(); udp.Connect(IPAddress.Loopback, port); await udp.SendAsync(new byte[] { 42 }); return (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer[0]; }
    private static async Task EchoTcp(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var tcp = await listener.AcceptTcpClientAsync(token);
            _ = Task.Run(async () => { using (tcp) try { var buffer = new byte[4096]; int n; while ((n = await tcp.GetStream().ReadAsync(buffer, token)) > 0) await tcp.GetStream().WriteAsync(buffer.AsMemory(0, n), token); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } });
        }
    }
    private static async Task EchoUdp(UdpClient udp, CancellationToken token)
    { while (!token.IsCancellationRequested) { var packet = await udp.ReceiveAsync(token); await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, token); } }
}

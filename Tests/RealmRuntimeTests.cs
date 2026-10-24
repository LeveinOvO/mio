using System.Net;
using System.Net.Sockets;
using PortForwarder.Core;
using PortForwarder.Windows;

internal static class RealmRuntimeTests
{
    public static async Task RunAsync(string executable)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "realm-runtime-test-" + Guid.NewGuid().ToString("N"));
        ConfigurationStore.TestDirectory = folder;
        RealmRuntime.TestExecutable = executable;
        using var token = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var echoes = new[] { EchoTcp(server, token.Token), EchoUdp(udp, token.Token) };
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var tcp = new ForwardRule("127.0.0.1", port, "127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port);
        var udpRule = tcp with { Protocol = ForwardProtocol.Udp, ConnectPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port };
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Add, null, tcp);
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Add, null, udpRule);
            var states = await RealmRuntime.ReadAsync();
            Check(states.Count == 2 && states.All(s => s.Running), "Full application operations start same-port TCP/UDP Realm rules");
            Check(states.All(s => s.Rule.Enabled), "New rules are enabled immediately after saving");
            Check(await RequestTcp(port) == 42 && await RequestUdp(port) == 42, "Full application runtime forwards both protocols");
            var ids = states.Select(s => s.Rule.Id).ToList();
            await RealmRuntime.ReconcileAsync(CancellationToken.None);
            Check((await RealmRuntime.ReadAsync()).Select(s => s.Rule.Id).SequenceEqual(ids), "Reconcile does not duplicate running rules");
            var hostname = tcp with { ConnectAddress = "localhost." };
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Edit, tcp, hostname);
            var domain = RealmStore.Read().Rules.Single(s => s.Applied.Protocol == ForwardProtocol.Tcp);
            Check(domain.Desired.ConnectAddress == "localhost." && domain.Applied.ConnectAddress == "127.0.0.1", "Domain saved separately while Realm receives final IPv4");
            await RealmRuntime.CheckDnsAsync(CancellationToken.None);
            Check(RealmStore.Read().Rules.Single(s => s.Id == domain.Id).LastChecked is not null, "In-process DNS monitor updates saved check state");
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Disable, domain.Applied, null);
            var off = RealmStore.Read().Rules.Single(s => s.Id == domain.Id);
            Check(!off.Enabled && !(await RealmRuntime.ReadAsync()).Single(s => s.Rule.Id == domain.Id).Running, "Disable saves configuration and stops only requested core");
            Check(await RequestUdp(port) == 42, "UDP remains running while same-port TCP disabled");
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Enable, off.Applied, null, off);
            Check(await RequestTcp(port) == 42, "Re-enable domain rule restarts core and forwards correctly");
            var current = RealmStore.Read().Rules.Single(s => s.Id == off.Id);
            var blocker = new TcpListener(IPAddress.Loopback, 0); blocker.Start();
            try
            {
                var occupied = current.Desired with { ListenPort = ((IPEndPoint)blocker.LocalEndpoint).Port };
                try { await DesktopRuleOperations.ExecuteAsync(RuleOperation.Edit, current.Applied, occupied); throw new Exception("Expected conflict"); }
                catch (InvalidOperationException) { Check(await RequestTcp(port) == 42, "Failed edit rolls back original Realm listener"); }
            }
            finally { blocker.Stop(); }
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Delete, udpRule, null);
            var asUdp = current.Desired with { Protocol = ForwardProtocol.Udp, ConnectPort = udpRule.ConnectPort };
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Edit, current.Applied, asUdp, current);
            var switched = RealmStore.Read().Rules.Single();
            Check(switched.Id == current.Id && switched.Enabled && switched.Applied.Protocol == ForwardProtocol.Udp,
                "TCP to UDP switch retains rule identity and enabled state");
            Check(await RequestUdp(port) == 42 && !SocketOwners.IsOccupied(current.Applied),
                "Protocol switch starts UDP and stops original TCP listener");
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Edit, switched.Applied, current.Desired, switched);
            var back = RealmStore.Read().Rules.Single();
            Check(back.Applied.Protocol == ForwardProtocol.Tcp && await RequestTcp(port) == 42 && !SocketOwners.IsOccupied(asUdp),
                "UDP to TCP switch forwards correctly and leaves no UDP listener");
            using (var occupiedUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
            {
                try
                {
                    await DesktopRuleOperations.ExecuteAsync(RuleOperation.Edit, back.Applied, asUdp, back);
                    throw new Exception("Expected UDP conflict");
                }
                catch (InvalidOperationException)
                {
                    Check(RealmStore.Read().Rules.Single().Applied.Protocol == ForwardProtocol.Tcp && await RequestTcp(port) == 42,
                        "Occupied UDP switch fails and restores original TCP rule");
                }
            }
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Disable, back.Applied, null, back);
            var disabledEntry = RealmStore.Read().Rules.Single();
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Edit, disabledEntry.Applied, asUdp, disabledEntry);
            var changedOff = RealmStore.Read().Rules.Single();
            Check(!changedOff.Enabled && changedOff.Applied.Protocol == ForwardProtocol.Udp && !SocketOwners.IsOccupied(asUdp),
                "Editing disabled protocol keeps rule disabled and starts no listener");
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Enable, changedOff.Applied, null, changedOff);
            Check(await RequestUdp(port) == 42, "Disabled rule protocol change is used when re-enabled");
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Delete, changedOff.Applied, null);
            Check(RealmStore.Read().Rules.Count == 0 && (await RealmRuntime.ReadAsync()).Count == 0, "Delete removes runtime and configuration entries");
            await DesktopRuleOperations.ExecuteAsync(RuleOperation.Add, null, tcp);
            await RealmRuntime.StopAsync();
            Check(!SocketOwners.IsOccupied(tcp) && RealmStore.Read().Rules.Single().Enabled, "Program exit stops cores but keeps enabled configuration for next launch");
        }
        finally
        {
            await RealmRuntime.StopAsync();
            token.Cancel(); server.Stop(); udp.Close();
            try { await Task.WhenAll(echoes); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
            ConfigurationStore.TestDirectory = null;
            RealmRuntime.TestExecutable = null;
        }
        Console.WriteLine($"REALM RUNTIME COMPLETE: {checks} checks passed, isolated config cleaned, no Windows rule/firewall edits.");
    }
    private static async Task<int> RequestTcp(int port)
    {
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port);
        await tcp.GetStream().WriteAsync(new byte[] { 42 });
        var reply = new byte[1]; await tcp.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); return reply[0];
    }
    private static async Task<int> RequestUdp(int port)
    {
        using var udp = new UdpClient(); udp.Connect(IPAddress.Loopback, port); await udp.SendAsync(new byte[] { 42 });
        return (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer[0];
    }
    private static async Task EchoTcp(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            _ = Task.Run(async () =>
            {
                using (client)
                try { var b = new byte[4096]; int n; while ((n = await client.GetStream().ReadAsync(b, token)) > 0) await client.GetStream().WriteAsync(b.AsMemory(0, n), token); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
    }
    private static async Task EchoUdp(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested) { var p = await udp.ReceiveAsync(token); await udp.SendAsync(p.Buffer, p.RemoteEndPoint, token); }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PortForwarder.Core;
using PortForwarder.Windows;

internal static class RealmValidation
{
    public static async Task RunAsync(string executable)
    {
        var processes = new List<Process>();
        var temp = Path.Combine(AppContext.BaseDirectory, "realm-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var stop = new CancellationTokenSource();
        var tcpA = new TcpListener(IPAddress.Loopback, 0);
        var tcpB = new TcpListener(IPAddress.Loopback, 0);
        tcpA.Start(); tcpB.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var workers = new[] { EchoTcp(tcpA, (byte)'A', stop.Token), EchoTcp(tcpB, (byte)'B', stop.Token), EchoUdp(udp, stop.Token) };
        var checks = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }
        int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        async Task<Process> Start(int port, int target, bool useUdp, bool noTcp, int udpTimeout = 2)
        {
            var file = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                log = new { level = "warn" },
                network = new { use_udp = useUdp, no_tcp = noTcp, udp_timeout = udpTimeout },
                endpoints = new[] { new { listen = $"127.0.0.1:{port}", remote = $"127.0.0.1:{target}" } }
            }));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(file);
            var p = Process.Start(start)!; processes.Add(p);
            _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
            await Task.Delay(500);
            Check(!p.HasExited, "Realm process starts with JSON configuration");
            return p;
        }
        async Task Kill(Process process) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        try
        {
            var p1 = FreePort(); var p2 = FreePort(); var pu = FreePort();
            var r1 = await Start(p1, ((IPEndPoint)tcpA.LocalEndpoint).Port, false, false);
            var r2 = await Start(p2, ((IPEndPoint)tcpA.LocalEndpoint).Port, false, false);
            var ru = await Start(pu, ((IPEndPoint)udp.Client.LocalEndPoint!).Port, true, true);
            var replies = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => TcpRequest(p1, Encoding.UTF8.GetBytes("tcp-" + i))));
            Check(replies.All(r => r[0] == 'A'), "TCP simultaneous clients receive correct replies");
            var large = Enumerable.Range(0, 262144).Select(i => (byte)(i % 251)).ToArray();
            var largeReply = await TcpRequest(p1, large);
            Check(largeReply[0] == 'A' && largeReply.AsSpan(1).SequenceEqual(large), "TCP 256 KiB binary transfer is intact");
            using var persistent = new TcpClient();
            await persistent.ConnectAsync(IPAddress.Loopback, p2);
            var first = await Exchange(persistent.GetStream(), [(byte)'x']);
            Check(first[0] == 'A', "Independent rule has persistent TCP connection");
            var udpReplies = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => UdpRequest(pu, Encoding.UTF8.GetBytes("udp-client-" + i))));
            Check(udpReplies.Select((r, i) => r.SequenceEqual(Encoding.UTF8.GetBytes("udp-client-" + i))).All(v => v), "UDP 16 concurrent clients have isolated bidirectional replies");
            var binary = Enumerable.Range(0, 1200).Select(i => (byte)(i % 251)).ToArray();
            Check((await UdpRequest(pu, binary)).SequenceEqual(binary), "UDP binary datagram boundary and content preserved");
            using var sameUdp = new UdpClient(); sameUdp.Connect(IPAddress.Loopback, pu);
            await sameUdp.SendAsync(binary); var beforeTimeout = await sameUdp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(3000);
            await sameUdp.SendAsync(binary); var afterTimeout = await sameUdp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(beforeTimeout.Buffer.SequenceEqual(binary) && afterTimeout.Buffer.SequenceEqual(binary), "UDP resumes after association idle timeout");
            await Kill(r1);
            Check(!r2.HasExited && !ru.HasExited, "Stopping one rule leaves other TCP/UDP rules running");
            Check((await Exchange(persistent.GetStream(), [(byte)'y']))[0] == 'A', "Unchanged TCP rule keeps its established connection");
            r1 = await Start(p1, ((IPEndPoint)tcpB.LocalEndpoint).Port, false, false);
            Check((await TcpRequest(p1, [(byte)'z']))[0] == 'B', "Target update restarts only changed rule and uses new destination");
            Check((await UdpRequest(pu, binary)).SequenceEqual(binary), "UDP rule remains usable during unrelated TCP update");
            r1.Refresh(); ru.Refresh();
            Console.WriteLine($"OBSERVATION: TCP private memory {r1.PrivateMemorySize64 / 1024 / 1024} MiB; UDP {ru.PrivateMemorySize64 / 1024 / 1024} MiB; EXE {new FileInfo(executable).Length} bytes.");
            // Binding conflicts must be detected by our host; observe how Realm behaves.
            var conflict = await Start(p1, ((IPEndPoint)tcpA.LocalEndpoint).Port, false, false);
            Console.WriteLine($"OBSERVATION: Port-conflict process exited={conflict.HasExited}; host must verify socket ownership, not only process existence.");
            await Kill(conflict);
            var hostPort = FreePort();
            var hostRule = new ForwardRule("127.0.0.1", hostPort, "127.0.0.1", ((IPEndPoint)tcpA.LocalEndpoint).Port);
            await using (var hosted = await RealmProcess.StartAsync(hostRule, executable, temp))
            {
                Check(SocketOwners.HasListeners(hostRule, hosted.Id), "GUI host verifies actual TCP socket ownership");
                Check((await TcpRequest(hostPort, [(byte)'h']))[0] == 'A', "Integrated Realm process wrapper forwards TCP");
            }
            Check(!SocketOwners.IsOccupied(hostRule), "Job-object cleanup stops listener on wrapper disposal");
            hostRule = hostRule with { Protocol = ForwardProtocol.Udp, ConnectPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port };
            await using (var hosted = await RealmProcess.StartAsync(hostRule, executable, temp))
            {
                Check(SocketOwners.HasListeners(hostRule, hosted.Id), "GUI host verifies actual UDP socket ownership");
                Check((await UdpRequest(hostPort, binary)).SequenceEqual(binary), "Integrated Realm process wrapper forwards UDP");
                try { await RealmProcess.StartAsync(hostRule, executable, temp); throw new Exception("Expected occupied port rejection"); }
                catch (InvalidOperationException) { Check(true, "Integrated host rejects occupied UDP port before starting core"); }
            }
            Check(!SocketOwners.IsOccupied(hostRule), "Integrated UDP cleanup leaves no listener");
        }
        finally
        {
            foreach (var p in processes) { await Kill(p); p.Dispose(); }
            stop.Cancel(); tcpA.Stop(); tcpB.Stop(); udp.Close();
            try { await Task.WhenAll(workers); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
            foreach (var file in Directory.GetFiles(temp)) File.Delete(file);
            Directory.Delete(temp);
        }
        Console.WriteLine($"REALM VALIDATION COMPLETE: {checks} checks passed. Only temporary loopback listeners, no portproxy/firewall edits.");
    }

    private static async Task<byte[]> TcpRequest(int port, byte[] body)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return await Exchange(tcp.GetStream(), body);
    }
    private static async Task<byte[]> Exchange(NetworkStream stream, byte[] body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(BitConverter.GetBytes(body.Length), timeout.Token);
        await stream.WriteAsync(body, timeout.Token);
        var output = new byte[body.Length + 1];
        await stream.ReadExactlyAsync(output, timeout.Token);
        return output;
    }
    private static async Task<byte[]> UdpRequest(int port, byte[] body)
    {
        using var udp = new UdpClient(); udp.Connect(IPAddress.Loopback, port);
        await udp.SendAsync(body);
        return (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer;
    }
    private static async Task EchoTcp(TcpListener listener, byte marker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = Task.Run(async () =>
            {
                using (client)
                try
                {
                    var stream = client.GetStream();
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var size = new byte[4]; await stream.ReadExactlyAsync(size, cancellationToken);
                        var body = new byte[BitConverter.ToInt32(size)]; await stream.ReadExactlyAsync(body, cancellationToken);
                        await stream.WriteAsync(new[] { marker }, cancellationToken); await stream.WriteAsync(body, cancellationToken);
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
    }
    private static async Task EchoUdp(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await udp.ReceiveAsync(cancellationToken);
            await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, cancellationToken);
        }
    }
}

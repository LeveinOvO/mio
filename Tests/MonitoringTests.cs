using PortForwarder.Core;
using PortForwarder.Windows;

internal static class MonitoringTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string text)
        { if (!condition) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        Check(DnsTargets.SelectIPv4(["192.0.2.11", "192.0.2.10"], "192.0.2.10") == "192.0.2.10", "DNS answer reorder preserves current IP");
        Check(DnsTargets.SelectIPv4(["10.0.0.12", "10.0.0.2", "10.0.0.12"], null) == "10.0.0.2", "New target stable numeric order");
        try { DnsTargets.SelectIPv4([], null); throw new Exception("Expected no-IP error"); }
        catch (InvalidOperationException) { Check(true, "No IPv4 rejected"); }
        var tcp = new ForwardRule("127.0.0.1", 18080, "service.example.com", 8080);
        var resolver = new Resolver(["192.0.2.11"]);
        var result = await DnsTargets.ResolveRuleAsync(tcp with { Protocol = ForwardProtocol.Udp }, null, resolver, CancellationToken.None);
        Check(result.ConnectAddress == "192.0.2.11" && result.Protocol == ForwardProtocol.Udp, "Domain resolution retains selected protocol");
        result = await DnsTargets.ResolveRuleAsync(tcp with { ConnectAddress = "192.0.2.10" }, null, resolver, CancellationToken.None);
        Check(result.ConnectAddress == "192.0.2.10" && resolver.Calls == 1, "Fixed IP bypasses resolver");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await DnsTargets.ResolveRuleAsync(tcp, null, resolver, stop.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Check(true, "Cancelled DNS resolution stops"); }
        var ips = await new SystemTargetResolver().ResolveAsync("localhost.", CancellationToken.None);
        Check(ips.Count > 0 && ips.All(ip => System.Net.IPAddress.Parse(ip).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork),
            "Read-only Windows DNS integration returns IPv4");
        Console.WriteLine($"READ ONLY: {RealmStore.Read().Rules.Count} Realm rules.");
        return count;
    }
    private sealed class Resolver(IReadOnlyList<string> addresses) : ITargetResolver
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<string>> ResolveAsync(string hostname, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(addresses); }
    }
}

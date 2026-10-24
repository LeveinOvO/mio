using PortForwarder.Core;

internal static class RouteModelTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool valid, string name) { if (!valid) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var route = new RouteDraft("192.0.2.0/24", "192.168.2.1", 7, 65535, true);
        Check(RouteValidation.Normalize(route) == route, "Route valid CIDR and metric boundary");
        Check(RouteValidation.Normalize(route with { DestinationPrefix = "0.0.0.0/0", NextHop = "0.0.0.0" }).NextHop == "0.0.0.0", "Route default and on-link supported");
        foreach (var bad in new[] {
            route with { DestinationPrefix = "192.0.2.1/24" }, route with { DestinationPrefix = "::/0" },
            route with { DestinationPrefix = "192.0.2.0/33" }, route with { NextHop = "127.0.0.1" },
            route with { NextHop = "224.0.0.1" }, route with { InterfaceIndex = 0 }, route with { RouteMetric = 65536 } })
        {
            var rejected = false;
            try { RouteValidation.Normalize(bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Route invalid input rejected");
        }
        var nic = new RouteInterface(7, "LAN", 25, "Connected", ["10.4.8.5/21"]);
        Check(RouteInterfaceSelection.Candidates("10.4.15.254", [nic]).Count == 1, "Auto interface matches non-/24 subnet");
        Check(RouteInterfaceSelection.Candidates("10.4.16.1", [nic]).Count == 0, "Auto interface rejects outside subnet");
        Check(RouteInterfaceSelection.Candidates("10.4.15.255", [nic]).Count == 0, "Auto interface rejects broadcast");
        Check(RouteInterfaceSelection.Candidates("10.4.8.5", [nic]).Count == 0, "Auto interface rejects own IP");
        Check(RouteInterfaceSelection.Candidates("0.0.0.0", [nic]).Count == 0, "On-link requires manual interface");
        Check(RouteInterfaceSelection.Candidates("10.4.8.1", [nic, nic with { Index = 8 }]).Count == 2, "Overlapping interfaces remain ambiguous");
        Check(RouteInterfaceSelection.Candidates("10.4.8.1", [nic with { State = "Disconnected" }]).Count == 0, "Disconnected interface excluded");
        return count;
    }
}

using System.Collections.ObjectModel;
using System.Text.Json;
using PortForwarder.Core;
using PortForwarder.Services;
using PortForwarder.Windows;

internal static class RealmModelTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var tcp = new ForwardRule("127.0.0.1", 18080, "192.0.2.10", 8080);
        var udp = tcp with { Protocol = ForwardProtocol.Udp };
        Check(!tcp.HasSameListener(udp) && !tcp.ConflictsWith(udp), "TCP and UDP may share port without logical collision");
        Check(tcp.ConflictsWith(tcp with { ListenAddress = "0.0.0.0" }), "TCP wildcard address overlaps TCP listener");
        Check(!udp.ConflictsWith(tcp with { ListenAddress = "0.0.0.0" }), "Wildcard TCP does not overlap UDP socket");
        using var udpConfig = JsonDocument.Parse(RealmSettings.Serialize(udp));
        Check(udpConfig.RootElement.GetProperty("network").GetProperty("use_udp").GetBoolean() &&
            udpConfig.RootElement.GetProperty("network").GetProperty("no_tcp").GetBoolean(), "UDP config disables unintended TCP listener");
        using var tcpConfig = JsonDocument.Parse(RealmSettings.Serialize(tcp));
        Check(!tcpConfig.RootElement.GetProperty("network").GetProperty("use_udp").GetBoolean(), "TCP config has no unintended UDP listener");
        Check(udpConfig.RootElement.GetProperty("endpoints")[0].GetProperty("remote").GetString() == tcp.ConnectEndpoint,
            "Realm config contains resolved target IP and selected port");
        try { RealmSettings.Serialize(tcp with { ConnectAddress = "service.example.com" }); throw new Exception("Expected IP-only validation"); }
        catch (ArgumentException) { Check(true, "Realm core config never receives unresolved domain"); }
        var udpScope = FirewallValidation.Create(udp, FirewallSourceMode.Cidr, "192.168.1.0/24", FirewallProfiles.Private);
        var tcpScope = FirewallValidation.Create(tcp, FirewallSourceMode.Cidr, "192.168.1.0/24", FirewallProfiles.Private);
        Check(udpScope.ProtocolNumber == 17 && udpScope.ListenerKey != tcpScope.ListenerKey, "UDP firewall metadata separated from same-port TCP");
        var draft = FirewallDraftHelper.Verify(udpScope);
        Check(draft.Protocol == 17 && draft.LocalPorts == "18080" && !draft.Enabled, "Windows accepts unsaved UDP firewall draft");
        var rows = new ObservableCollection<RuleRow>();
        var entries = new[] { tcp, udp }.Select(r => new RealmRuntimeState(new RealmRule(Guid.NewGuid(), r, r, true), true)).ToArray();
        RuleListUpdater.Apply(rows, entries);
        Check(rows.Count == 2 && rows.Select(r => r.ProtocolText).SequenceEqual(["TCP", "UDP"]), "List displays same-port TCP and UDP separately");
        RuleListUpdater.Apply(rows, [entries[1]]);
        Check(rows.Count == 1 && rows[0].Actual.Protocol == ForwardProtocol.Udp, "Removing TCP does not remove same-port UDP row");
        var historical = JsonSerializer.Deserialize<ForwardRule>("{\"ListenAddress\":\"127.0.0.1\",\"ListenPort\":18080,\"ConnectAddress\":\"192.0.2.10\",\"ConnectPort\":8080}");
        Check(historical?.Protocol == ForwardProtocol.Tcp, "Existing rule JSON defaults to TCP");
        return count;
    }
}

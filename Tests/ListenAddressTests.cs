using PortForwarder.Core;
using PortForwarder.Services;

internal static class ListenAddressTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var options = new[] { new ListenAddressOption("192.168.1.10", "LAN"), new ListenAddressOption("0.0.0.0", "所有 IPv4 接口") };
        var wildcard = NetworkAddresses.PrepareForEdit(options, "0.0.0.0");
        Check(wildcard.Count == 2 && ReferenceEquals(wildcard.Single(o => o.Address == "0.0.0.0"), options[1]),
            "Editing wildcard address retains matching dropdown item without duplication");
        var lan = NetworkAddresses.PrepareForEdit(options, "192.168.1.10");
        Check(lan.Single(o => o.Address == "192.168.1.10").ToString() == "LAN · 192.168.1.10",
            "Editable selection and edit initialization use the same adapter-name/IP label");
        var vpn = NetworkAddresses.PrepareForEdit(options, "100.64.1.10");
        Check(vpn.Count == 3 && vpn[0].Address == "100.64.1.10" && vpn[0].Name == "当前规则地址",
            "Disconnected or manually entered address remains selectable on edit");
        var noAdapters = NetworkAddresses.PrepareForEdit([], "127.0.0.1");
        Check(noAdapters.Count == 1 && noAdapters[0].Address == "127.0.0.1", "Original address retained even with empty adapter discovery");
        Check(NetworkAddresses.PrepareForEdit(options, null).Count == 2 && NetworkAddresses.PrepareForEdit(options, "").Count == 2,
            "New rule adds no artificial selected listener");
        Check(options.Length == 2, "Editor does not mutate discovered adapter list");
        var desired = new ForwardRule("100.64.1.10", 8080, "service.example.com", 80, ForwardProtocol.Udp);
        var row = new RuleRow(new RealmRuntimeState(new RealmRule(Guid.NewGuid(), desired,
            desired with { ConnectAddress = "192.0.2.10" }, false), false));
        Check(row.Editable.ListenAddress == desired.ListenAddress && row.Editable.Protocol == ForwardProtocol.Udp,
            "Disabled domain rule supplies original listener and protocol to editor");
        Check(NetworkAddresses.ResolveEditorAddress("LAN · 192.168.1.10", options) == "192.168.1.10",
            "Known display label saves only IPv4, never adapter name");
        Check(NetworkAddresses.ResolveEditorAddress("所有 IPv4 接口 · 0.0.0.0", options) == "0.0.0.0",
            "All-interface label saves wildcard IPv4 correctly");
        Check(NetworkAddresses.ResolveEditorAddress(vpn[0].DisplayName, vpn) == "100.64.1.10",
            "Original disconnected-address label saves original IP");
        Check(NetworkAddresses.ResolveEditorAddress(" 192.0.2.20 ", options) == "192.0.2.20",
            "Manual address change is respected even after dropdown selection");
        foreach (var input in new[] { "", "LAN · 192.0.2.20", "invalid", "LAN · 192.168.1.10/24" })
        {
            var rejected = false;
            try { NetworkAddresses.ResolveEditorAddress(input, options); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid visible address rejected without using stale selection: " + input);
        }
        return count;
    }
}

using PortForwarder.Core;
using PortForwarder.Windows;

internal static class FirewallTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string text)
        { if (!value) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        void Reject(Action action, string text)
        {
            try { action(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Check(true, text); return; }
            throw new Exception("FAIL: " + text);
        }
        var rule = new ForwardRule("192.168.1.10", 8080, "192.0.2.10", 80);
        foreach (var text in new[] { "", "  \r\n ", ",;", "*", "192.168.1.1", "::/64", "0.0.0.0/0", "1.2.3.4/33", "1.2.3.4/-1", "1.2.3.999/24", "a;whoami/24" })
            Reject(() => FirewallValidation.Create(rule, FirewallSourceMode.Cidr, text, FirewallProfiles.Private), "Reject unsafe/empty CIDR: " + text);
        var scope = FirewallValidation.Create(rule, FirewallSourceMode.Cidr, "192.168.1.25/24\n100.64.1.11/32,192.168.1.0/24", FirewallProfiles.Private);
        Check(scope.SourceCidrs.SequenceEqual(["100.64.1.11/32", "192.168.1.0/24"]), "Normalize host bits, deduplicate multiple CIDRs");
        Check(scope.LocalAddresses == "192.168.1.10" && scope.ListenPort == 8080 && scope.Profiles == FirewallProfiles.Private, "Firewall scope limits exact listener and private profile");
        Reject(() => FirewallValidation.Create(rule, FirewallSourceMode.Cidr, "192.168.1.0/24", 0), "Reject no network profiles");
        Reject(() => FirewallValidation.Create(rule, FirewallSourceMode.Cidr, "192.168.1.0/24", (FirewallProfiles)8), "Reject unknown profile bits");
        var any = FirewallValidation.Create(rule with { ListenAddress = "0.0.0.0" }, FirewallSourceMode.Any, "", FirewallProfiles.Public);
        Check(any.RemoteAddresses == "*" && any.LocalAddresses == "*" && any.SourceCidrs.Length == 0, "Explicit any-source mode works without CIDR");
        Check(FirewallValidation.NormalizeWindowsAddresses("192.168.1.0/255.255.255.0,100.64.1.11") == scope.RemoteAddresses, "Windows netmask representation compared semantically");
        Reject(() => FirewallValidation.NormalizeWindowsAddresses("1.2.3.4/255.0.255.0"), "Reject noncontiguous system netmask");
        var owned = new OwnedFirewallRule(Guid.NewGuid(), scope, null);
        var backend = new FakeFirewall();
        var applied = FirewallOwnership.Apply(owned, true, backend);
        Check(backend.PutCount == 1 && applied.Expected!.Protocol == 6 && applied.Expected.Enabled, "Create TCP allow only for owned unique name");
        var clone = applied with { Scope = scope with { SourceCidrs = scope.SourceCidrs.ToArray() } };
        Check(FirewallOwnership.Matches(applied, clone), "Persisted array snapshots compare by value, not reference");
        applied = FirewallOwnership.Apply(clone, false, backend);
        Check(!applied.Expected!.Enabled, "Disable sync only changes owned rule");
        backend.Entry = applied.Expected with { RemoteAddresses = "*" };
        var writes = backend.PutCount;
        Reject(() => FirewallOwnership.Apply(applied, true, backend), "Reject externally broadened remote scope");
        Check(backend.PutCount == writes, "External changes rejected before write");
        backend.Entry = applied.Expected with { Group = "OtherSoftware" };
        Reject(() => FirewallOwnership.EnsureUnchanged(applied, backend), "Reject foreign grouping");
        backend.Entry = applied.Expected;
        backend.Duplicate = true;
        Reject(() => FirewallOwnership.EnsureUnchanged(applied, backend), "Reject duplicate system names");
        backend.Duplicate = false;
        backend.Entry = null;
        FirewallOwnership.EnsureUnchanged(applied, backend);
        Check(true, "Missing owned rule can be explicitly recreated");
        backend.BroadenOnPut = true;
        Reject(() => FirewallOwnership.Apply(applied, true, backend), "Verify backend did not broaden actual source scope");
        // Read-only integration: query a random nonexistent unique name, never create a firewall rule.
        var actual = new WindowsFirewallBackend().Find(FirewallOwnership.Name(Guid.NewGuid()));
        Check(actual.Count == 0, "Read-only Windows Firewall COM enumeration succeeds");
        Check(FirewallStore.Read().Version == 1, "Read-only firewall metadata parse succeeds");
        var draft = FirewallDraftHelper.Verify(any);
        Check(!draft.Enabled && draft.Protocol == 6 && draft.LocalPorts == "8080" &&
            draft.ApplicationName == "" && draft.ServiceName == "", "Unsaved COM draft omits optional program/service restrictions");
        var limitedDraft = FirewallDraftHelper.Verify(scope);
        Check(FirewallValidation.NormalizeWindowsAddresses(limitedDraft.RemoteAddresses) == scope.RemoteAddresses &&
            !limitedDraft.Enabled, "Unsaved COM CIDR draft preserves intended scope without system writes");
        var switchingBackend = new FakeFirewall();
        var initial = FirewallOwnership.Apply(new OwnedFirewallRule(Guid.NewGuid(), scope, null), true, switchingBackend);
        var udpChanged = FirewallOwnership.Apply(initial with { Scope = scope with { Protocol = ForwardProtocol.Udp } }, true, switchingBackend);
        Check(udpChanged.Id == initial.Id && udpChanged.Expected!.Protocol == 17 && udpChanged.Expected.Enabled,
            "Firewall protocol update retains managed identity, source range and enabled state");
        var tcpChanged = FirewallOwnership.Apply(udpChanged with { Scope = scope }, false, switchingBackend);
        Check(tcpChanged.Expected!.Protocol == 6 && !tcpChanged.Expected.Enabled, "Firewall switches back to TCP and retains disabled state");
        return count;
    }

    private sealed class FakeFirewall : IFirewallBackend
    {
        public FirewallSnapshot? Entry { get; set; }
        public int PutCount { get; private set; }
        public bool Duplicate { get; set; }
        public bool BroadenOnPut { get; set; }
        public IReadOnlyList<FirewallSnapshot> Find(string name) => Entry is null ? [] : Duplicate ? [Entry, Entry] : [Entry];
        public FirewallSnapshot Put(string name, FirewallScope scope, bool enabled)
        {
            PutCount++;
            Entry = new FirewallSnapshot(name, FirewallOwnership.Group, "test", scope.ProtocolNumber, 1, 1, enabled, scope.LocalAddresses,
                scope.ListenPort.ToString(), BroadenOnPut ? "*" : scope.RemoteAddresses, "*", (int)scope.Profiles,
                false, "", "", "All", "", "");
            return Entry;
        }
        public void Remove(string name) => Entry = null;
    }
}

using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class FirewallStatusReader
{
    public static FirewallStatusSnapshot Read()
    {
        try
        {
            var config = FirewallStore.Read();
            // Enumerate Windows rules once per refresh, not once per row.
            var actual = new WindowsFirewallBackend().FindMany(config.Rules.Select(r => FirewallOwnership.Name(r.Id)));
            var status = config.Rules.ToDictionary(r => r.Scope.ListenerKey,
                r => FirewallRowStatus.Evaluate(r, actual.GetValueOrDefault(FirewallOwnership.Name(r.Id)) ?? []));
            return new FirewallStatusSnapshot(status);
        }
        catch (Exception error) { return new FirewallStatusSnapshot(new Dictionary<string, FirewallRowStatus>(), error.Message); }
    }
}

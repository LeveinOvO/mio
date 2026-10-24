using PortForwarder.Core;

namespace PortForwarder.Windows;

public sealed record FirewallStatusSnapshot(IReadOnlyDictionary<string, FirewallRowStatus> Entries, string? Error = null)
{
    public FirewallRowStatus ForListener(string listener) => Error is null
        ? Entries.GetValueOrDefault(listener) ?? FirewallRowStatus.Unconfigured
        : FirewallRowStatus.QueryFailed(Error);
}

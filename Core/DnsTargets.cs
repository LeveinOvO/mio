using System.Net;

namespace PortForwarder.Core;

public interface ITargetResolver
{
    Task<IReadOnlyList<string>> ResolveAsync(string hostname, CancellationToken cancellationToken);
}

public static class DnsTargets
{
    public static bool IsHostname(ForwardRule rule) => !IPAddress.TryParse(rule.ConnectAddress, out _);

    public static string SelectIPv4(IReadOnlyList<string> addresses, string? current)
    {
        var valid = addresses.Select(RuleValidation.NormalizeIPv4).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => Convert.ToHexString(IPAddress.Parse(a).GetAddressBytes()), StringComparer.Ordinal).ToList();
        if (valid.Count == 0) throw new InvalidOperationException("目标域名没有可用的 IPv4 地址。");
        return current is not null && valid.Contains(current) ? current : valid[0];
    }

    public static async Task<ForwardRule> ResolveRuleAsync(ForwardRule desired, string? current,
        ITargetResolver resolver, CancellationToken cancellationToken)
    {
        RuleValidation.Validate(desired);
        if (!IsHostname(desired)) return desired;
        var addresses = await resolver.ResolveAsync(desired.ConnectAddress, cancellationToken);
        return desired with { ConnectAddress = SelectIPv4(addresses, current) };
    }
}

using System.Globalization;
using System.Net;

namespace PortForwarder.Core;

[Flags]
public enum FirewallProfiles { Domain = 1, Private = 2, Public = 4 }
public enum FirewallSourceMode { Cidr, Any }

public sealed record FirewallScope(string ListenAddress, int ListenPort, FirewallSourceMode SourceMode,
    string[] SourceCidrs, FirewallProfiles Profiles, ForwardProtocol Protocol = ForwardProtocol.Tcp)
{
    public string LocalAddresses => ListenAddress == "0.0.0.0" ? "*" : ListenAddress;
    public string RemoteAddresses => SourceMode == FirewallSourceMode.Any ? "*" : string.Join(",", SourceCidrs);
    public string Listener => $"{ListenAddress}:{ListenPort}";
    public string ListenerKey => Protocol + ":" + Listener;
    public string ProtocolText => Protocol == ForwardProtocol.Tcp ? "TCP" : "UDP";
    public int ProtocolNumber => Protocol == ForwardProtocol.Tcp ? 6 : 17;
    public string ProfileNames => string.Join("、", new[]
    {
        Profiles.HasFlag(FirewallProfiles.Private) ? "专用" : null,
        Profiles.HasFlag(FirewallProfiles.Domain) ? "域" : null,
        Profiles.HasFlag(FirewallProfiles.Public) ? "公用" : null
    }.Where(name => name is not null));
}

public static class FirewallValidation
{
    public static FirewallScope Create(ForwardRule rule, FirewallSourceMode mode, string input, FirewallProfiles profiles)
    {
        RuleValidation.Validate(rule);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("不支持的来源模式。");
        if (profiles == 0 || ((int)profiles & ~7) != 0) throw new ArgumentException("至少选择一个网络配置文件。");
        var cidrs = mode == FirewallSourceMode.Any ? [] : ParseCidrs(input);
        return new FirewallScope(RuleValidation.NormalizeIPv4(rule.ListenAddress), rule.ListenPort, mode, cidrs, profiles, rule.Protocol);
    }

    public static string[] ParseCidrs(string? input)
    {
        var tokens = (input ?? "").Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new ArgumentException("指定来源 CIDR 不能为空；请至少输入一个 IPv4 CIDR。");
        if (tokens.Length > 100) throw new ArgumentException("最多填写 100 个 CIDR。");
        return tokens.Select(NormalizeCidr).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    public static string NormalizeCidr(string input)
    {
        var parts = input.Split('/');
        if (parts.Length != 2 || parts[1].Any(c => c is < '0' or > '9') ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 1 or > 32)
            throw new ArgumentException($"无效 CIDR：{input}。请使用 IPv4/1–32；完全放行请选择“允许任意来源”。");
        var address = RuleValidation.NormalizeIPv4(parts[0]);
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var network = value & (uint.MaxValue << (32 - prefix));
        return $"{network >> 24}.{(network >> 16) & 255}.{(network >> 8) & 255}.{network & 255}/{prefix}";
    }

    public static void Validate(FirewallScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.SourceCidrs is null) throw new ArgumentException("缺少来源 CIDR。");
        Create(new ForwardRule(scope.ListenAddress, scope.ListenPort, "127.0.0.1", 1, scope.Protocol), scope.SourceMode,
            string.Join(",", scope.SourceCidrs), scope.Profiles);
        if (scope.SourceMode == FirewallSourceMode.Any && scope.SourceCidrs.Length != 0)
            throw new ArgumentException("任意来源模式不能包含 CIDR 列表。");
    }

    public static string NormalizeWindowsAddresses(string input)
    {
        if (input == "*") return input;
        return string.Join(",", input.Split(',').Select(token =>
        {
            var parts = token.Trim().Split('/');
            if (parts.Length == 1) return NormalizeCidr(parts[0] + "/32");
            if (parts.Length == 2 && parts[1].Contains('.'))
            {
                var bytes = IPAddress.Parse(RuleValidation.NormalizeIPv4(parts[1])).GetAddressBytes();
                var bits = string.Concat(bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
                var prefix = bits.TakeWhile(c => c == '1').Count();
                if (bits[prefix..].Contains('1')) throw new ArgumentException("防火墙返回了非连续子网掩码。");
                return NormalizeCidr(parts[0] + "/" + prefix);
            }
            return NormalizeCidr(token.Trim());
        }).Distinct().OrderBy(s => s, StringComparer.Ordinal));
    }
}

// Snapshot of all security-relevant fields. A changed foreign rule is never silently adopted.
public sealed record FirewallSnapshot(string Name, string Group, string Description, int Protocol, int Direction,
    int Action, bool Enabled, string LocalAddresses, string LocalPorts, string RemoteAddresses,
    string RemotePorts, int Profiles, bool EdgeTraversal, string ApplicationName, string ServiceName,
    string InterfaceTypes, string Interfaces, string IcmpTypesAndCodes);

public sealed record OwnedFirewallRule(Guid Id, FirewallScope Scope, FirewallSnapshot? Expected, string Message = "");
public sealed record FirewallConfiguration(int Version, List<OwnedFirewallRule> Rules)
{
    public static FirewallConfiguration Empty() => new(1, []);
}

public interface IFirewallBackend
{
    IReadOnlyList<FirewallSnapshot> Find(string name);
    FirewallSnapshot Put(string name, FirewallScope scope, bool enabled);
    void Remove(string name);
}

public static class FirewallOwnership
{
    public const string Group = "Mio.Managed.TCP.v1";
    public static string Name(Guid id) => "Mio.TCP." + id.ToString("N");
    public static bool Matches(OwnedFirewallRule? a, OwnedFirewallRule? b) => a is null ? b is null : b is not null &&
        a.Id == b.Id && a.Expected == b.Expected && a.Scope.ListenAddress == b.Scope.ListenAddress &&
        a.Scope.ListenPort == b.Scope.ListenPort && a.Scope.Protocol == b.Scope.Protocol && a.Scope.SourceMode == b.Scope.SourceMode &&
        a.Scope.Profiles == b.Scope.Profiles && a.Scope.SourceCidrs.SequenceEqual(b.Scope.SourceCidrs);

    public static void EnsureUnchanged(OwnedFirewallRule owned, IFirewallBackend backend)
    {
        var matches = backend.Find(Name(owned.Id));
        if (matches.Count == 0) return;
        if (owned.Expected is null || matches.Count != 1 || matches[0] != owned.Expected || matches[0].Group != Group)
            throw new InvalidOperationException("防火墙规则被其他程序修改或存在同名规则，请检查 Windows 防火墙后重试。");
    }

    public static OwnedFirewallRule Apply(OwnedFirewallRule owned, bool enabled, IFirewallBackend backend)
    {
        FirewallValidation.Validate(owned.Scope);
        EnsureUnchanged(owned, backend);
        var snapshot = backend.Put(Name(owned.Id), owned.Scope, enabled);
        if (snapshot.Name != Name(owned.Id) || snapshot.Group != Group || snapshot.Protocol != owned.Scope.ProtocolNumber ||
            snapshot.Direction != 1 || snapshot.Action != 1 || snapshot.Enabled != enabled ||
            snapshot.Profiles != (int)owned.Scope.Profiles || snapshot.EdgeTraversal ||
            snapshot.LocalPorts != owned.Scope.ListenPort.ToString(CultureInfo.InvariantCulture) ||
            FirewallValidation.NormalizeWindowsAddresses(snapshot.LocalAddresses) != FirewallValidation.NormalizeWindowsAddresses(owned.Scope.LocalAddresses) ||
            FirewallValidation.NormalizeWindowsAddresses(snapshot.RemoteAddresses) != FirewallValidation.NormalizeWindowsAddresses(owned.Scope.RemoteAddresses) ||
            snapshot.RemotePorts != "*" || snapshot.ApplicationName != "" || snapshot.ServiceName != "" ||
            !snapshot.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase) || snapshot.Interfaces != "")
            throw new InvalidOperationException("防火墙规则保存结果与设置不一致，请打开“防火墙”检查。");
        return owned with { Expected = snapshot, Message = enabled ? "防火墙规则已启用" : "防火墙规则已禁用" };
    }
}

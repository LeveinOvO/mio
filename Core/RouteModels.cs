namespace PortForwarder.Core;

public sealed record RouteInterface(uint Index, string Name, uint Metric, string State, string[]? Addresses = null)
{
    public string Label => $"{Name} · #{Index} · 接口 Metric {Metric} · {State}";
}

public sealed record RouteEntry(string DestinationPrefix, string NextHop, uint InterfaceIndex,
    string InterfaceAlias, uint RouteMetric, uint InterfaceMetric, string Protocol,
    string Store, string Snapshot, bool Editable)
{
    public string InterfaceText => $"{InterfaceAlias} · #{InterfaceIndex}";
    public string StoreText => Store == "Persistent" ? "持久" : "临时 / 活动";
    public string TypeText => Editable ? "静态" : $"系统 · {Protocol}";
    public ulong EffectiveMetric => (ulong)RouteMetric + InterfaceMetric;
}

public sealed record RouteState(List<RouteEntry> Routes, List<RouteInterface> Interfaces, string WriteBlockedReason = "");
public sealed record RouteDraft(string DestinationPrefix, string NextHop, uint InterfaceIndex,
    uint RouteMetric, bool Persistent, bool AutoInterface = false);

public static class RouteInterfaceSelection
{
    public static List<RouteInterface> Candidates(string nextHop, IEnumerable<RouteInterface> interfaces)
    {
        var hop = RuleValidation.NormalizeIPv4(nextHop);
        uint Number(string ip) => ip.Split('.').Aggregate(0u, (v, p) => (v << 8) | byte.Parse(p));
        uint value = Number(hop);
        if (value == 0 || value >> 24 == 127 || value >> 24 >= 224) return [];
        return interfaces.Where(i => i.State == "Connected" && (i.Addresses ?? []).Any(cidr =>
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits < 1 || bits > 32) return false;
            uint local = Number(parts[0]), mask = uint.MaxValue << (32 - bits);
            return local != value && (local & mask) == (value & mask) &&
                (bits >= 31 || (value != (local & mask) && value != ((local & mask) | ~mask)));
        })).GroupBy(i => i.Index).Select(g => g.First()).ToList();
    }
}

public static class RouteValidation
{
    public static RouteDraft Normalize(RouteDraft draft)
    {
        var parts = draft.DestinationPrefix.Trim().Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            throw new ArgumentException("目标网段必须为 IPv4 CIDR，例如 192.168.10.0/24。");
        var address = RuleValidation.NormalizeIPv4(parts[0]);
        var bytes = address.Split('.').Select(byte.Parse).ToArray();
        uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        if ((value & mask) != value) throw new ArgumentException("目标网段包含主机位，请填写网络地址。");
        var hop = RuleValidation.NormalizeIPv4(draft.NextHop);
        var first = byte.Parse(hop.Split('.')[0]);
        if (hop != "0.0.0.0" && (first == 0 || first == 127 || first >= 224))
            throw new ArgumentException("下一跳须为单播 IPv4；直连路由填写 0.0.0.0。");
        if (draft.InterfaceIndex == 0) throw new ArgumentException("请选择网络接口。");
        if (draft.RouteMetric > 65535) throw new ArgumentException("路由 Metric 范围为 0–65535。");
        return draft with { DestinationPrefix = $"{address}/{prefix}", NextHop = hop };
    }
}

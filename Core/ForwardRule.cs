using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PortForwarder.Core;

public enum ForwardProtocol { Tcp, Udp }

public sealed record ForwardRule(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort,
    ForwardProtocol Protocol = ForwardProtocol.Tcp)
{
    public string ProtocolText => Protocol == ForwardProtocol.Tcp ? "TCP" : "UDP";
    public string ListenerKey => Protocol + ":" + ListenEndpoint;
    public bool UsesTcp => Protocol != ForwardProtocol.Udp;
    public bool UsesUdp => Protocol != ForwardProtocol.Tcp;
    public string ListenEndpoint => $"{ListenAddress}:{ListenPort}";
    public string ConnectEndpoint => $"{ConnectAddress}:{ConnectPort}";

    public bool HasSameListener(ForwardRule other) =>
        Protocol == other.Protocol && ListenPort == other.ListenPort && ListenAddress.Equals(other.ListenAddress, StringComparison.OrdinalIgnoreCase);

    public bool ConflictsWith(ForwardRule other) => ListenPort == other.ListenPort &&
        (ListenAddress == "0.0.0.0" || other.ListenAddress == "0.0.0.0" || ListenAddress == other.ListenAddress) &&
        ((UsesTcp && other.UsesTcp) || (UsesUdp && other.UsesUdp));

    public bool Matches(ForwardRule other) => HasSameListener(other) && ConnectPort == other.ConnectPort &&
        ConnectAddress.Equals(other.ConnectAddress, StringComparison.OrdinalIgnoreCase);
}

public static class RuleValidation
{
    public static ForwardRule Create(string listenAddress, double listenPort, string connectAddress, double connectPort,
        ForwardProtocol protocol = ForwardProtocol.Tcp)
    {
        var listen = NormalizeIPv4(listenAddress);
        var connect = NormalizeTarget(connectAddress);
        if (!Enum.IsDefined(protocol)) throw new ArgumentException("请选择 TCP 或 UDP。");
        return new ForwardRule(listen, ValidatePort(listenPort), connect, ValidatePort(connectPort), protocol);
    }

    public static void Validate(ForwardRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Create(rule.ListenAddress, rule.ListenPort, rule.ConnectAddress, rule.ConnectPort, rule.Protocol);
    }

    public static int ValidatePort(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 1 || value > 65535)
            throw new ArgumentException("端口必须是 1–65535 之间的整数。");
        return (int)value;
    }

    public static string NormalizeIPv4(string? input)
    {
        var text = input?.Trim() ?? "";
        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is < 1 or > 3 ||
                p.Any(c => c is < '0' or > '9') || !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ||
            !IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("监听地址必须是完整 IPv4 地址，例如 192.168.2.157。");
        return string.Join('.', parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
    }

    public static string NormalizeTarget(string? input)
    {
        var text = input?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("请输入目标 IPv4 地址或域名。");
        if (IPAddress.TryParse(text, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("仅支持 IPv4 转发，请输入 IPv4 地址或域名。");
            return NormalizeIPv4(text);
        }
        if (text.All(c => c is >= '0' and <= '9' or '.'))
            throw new ArgumentException("目标 IPv4 地址格式不正确。");
        try { text = new IdnMapping().GetAscii(text); }
        catch (ArgumentException) { throw new ArgumentException("目标域名格式不正确。"); }
        var hostname = text.TrimEnd('.');
        if (hostname.Length is < 1 or > 253 || text.Length > 254 || text.EndsWith("..", StringComparison.Ordinal) ||
            hostname.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("请输入 IPv4 地址或域名，不要包含 http://、端口、路径或空格。");
        return text;
    }
}

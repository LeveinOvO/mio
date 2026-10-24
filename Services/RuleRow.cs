using System.ComponentModel;
using PortForwarder.Core;

namespace PortForwarder.Services;

public sealed class RuleRow(RealmRuntimeState state) : INotifyPropertyChanged
{
    public RealmRuntimeState State { get; private set; } = state;
    public RealmRule Rule => State.Rule;
    public ForwardRule Actual => Rule.Applied;
    public ForwardRule Editable => Rule.Desired;
    public bool IsEnabled => Rule.Enabled;
    public string ProtocolText => Actual.ProtocolText;
    public string ListenAddress => Actual.ListenAddress;
    public int ListenPort => Actual.ListenPort;
    public string ConnectAddress => Rule.Desired.ConnectAddress;
    public int ConnectPort => Actual.ConnectPort;
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { if (_isChecked != value) { _isChecked = value; Notify(nameof(IsChecked)); } } }
    private FirewallRowStatus _firewall = FirewallRowStatus.Unconfigured;
    public string FirewallStatusText => _firewall.Text;
    public string FirewallStatusTooltip => _firewall.Tooltip;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Details => !Rule.Enabled ? "已停用 · 配置保留" : !State.Running ?
        "未运行：" + (State.Error == "" ? "正在启动" : State.Error) : DnsTargets.IsHostname(Rule.Desired) ?
        $"已应用 {Actual.ConnectAddress} · " + (Rule.Message.StartsWith("解析失败", StringComparison.Ordinal) ? "DNS 失败，保留旧 IP" : "托盘监控中") : "转发运行中";
    public string Tooltip => $"{ProtocolText} · {Editable.ConnectEndpoint}\n已应用：{Actual.ConnectEndpoint}\n{Rule.Message}\n" +
        $"上次检查：{Rule.LastChecked?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未检查"}";

    public void Update(RealmRuntimeState updated)
    {
        var values = new Dictionary<string, object?>
        {
            [nameof(IsEnabled)] = IsEnabled, [nameof(ConnectAddress)] = ConnectAddress,
            [nameof(ProtocolText)] = ProtocolText, [nameof(ListenAddress)] = ListenAddress, [nameof(ListenPort)] = ListenPort,
            [nameof(ConnectPort)] = ConnectPort, [nameof(Details)] = Details, [nameof(Tooltip)] = Tooltip
        };
        State = updated;
        var next = new Dictionary<string, object?>
        {
            [nameof(IsEnabled)] = IsEnabled, [nameof(ConnectAddress)] = ConnectAddress,
            [nameof(ProtocolText)] = ProtocolText, [nameof(ListenAddress)] = ListenAddress, [nameof(ListenPort)] = ListenPort,
            [nameof(ConnectPort)] = ConnectPort, [nameof(Details)] = Details, [nameof(Tooltip)] = Tooltip
        };
        foreach (var item in values) if (!Equals(item.Value, next[item.Key])) Notify(item.Key);
    }

    public void UpdateFirewallStatus(FirewallRowStatus status)
    {
        var previous = _firewall; _firewall = status;
        if (previous.Text != status.Text) Notify(nameof(FirewallStatusText));
        if (previous.Tooltip != status.Tooltip) Notify(nameof(FirewallStatusTooltip));
    }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

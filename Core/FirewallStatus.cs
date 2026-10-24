namespace PortForwarder.Core;

public sealed record FirewallRowStatus(string Text, string Tooltip)
{
    public static FirewallRowStatus Unconfigured { get; } = new("未配置", "点击“防火墙”配置允许的来源。");
    public static FirewallRowStatus QueryFailed(string error) => new("查询失败", "无法读取防火墙状态，请刷新重试：" + error);

    public static FirewallRowStatus Evaluate(OwnedFirewallRule? owned, IReadOnlyList<FirewallSnapshot> actual)
    {
        if (owned is null) return Unconfigured;
        var scope = owned.Scope;
        var summary = $"{scope.ProtocolText} 入站 · {scope.Listener}\n来源：" +
            (scope.SourceMode == FirewallSourceMode.Any ? "任意来源 IP" : string.Join(", ", scope.SourceCidrs)) +
            $"\n网络配置文件：{scope.ProfileNames}\n";
        if (actual.Count == 0)
            return new(owned.Expected is null ? "保存未完成" : "规则缺失", summary + "请点击“防火墙”重新保存。");
        if (actual.Count > 1)
            return new("同名冲突", summary + "存在多条同名规则，请检查 Windows 防火墙。");
        var rule = actual[0];
        if (owned.Expected is null)
            return new("待核验", summary + $"规则已{(rule.Enabled ? "启用" : "禁用")}，上次保存未完成，请打开“防火墙”检查。");
        if (rule != owned.Expected || rule.Group != FirewallOwnership.Group)
            return new("外部已修改", summary + $"规则已{(rule.Enabled ? "启用" : "禁用")}，设置被其他程序修改，请检查 Windows 防火墙。");
        return new(rule.Enabled ? "已启用" : "已禁用", summary.TrimEnd());
    }
}

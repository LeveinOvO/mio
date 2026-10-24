using PortForwarder.Core;
using PortForwarder.Services;
using PortForwarder.Windows;

internal static class FirewallStatusTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string text)
        { if (!condition) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        var rule = new ForwardRule("192.168.1.10", 8080, "192.0.2.10", 80);
        var scope = FirewallValidation.Create(rule, FirewallSourceMode.Cidr, "192.168.1.0/24", FirewallProfiles.Private);
        var id = Guid.NewGuid();
        var actual = new FirewallSnapshot(FirewallOwnership.Name(id), FirewallOwnership.Group, "test", 6, 1, 1, true,
            scope.LocalAddresses, "8080", scope.RemoteAddresses, "*", 2, false, "", "", "All", "", "");
        var owned = new OwnedFirewallRule(id, scope, actual);
        Check(FirewallRowStatus.Evaluate(null, []).Text == "未配置", "No tool metadata is unconfigured, not falsely blocked");
        Check(FirewallRowStatus.Evaluate(owned, [actual]).Text == "已启用", "Read matching Windows enabled rule");
        var disabled = actual with { Enabled = false };
        Check(FirewallRowStatus.Evaluate(owned with { Expected = disabled }, [disabled]).Text == "已禁用", "Read matching Windows disabled rule");
        Check(FirewallRowStatus.Evaluate(owned, []).Text == "规则缺失", "Detect externally removed Windows rule");
        Check(FirewallRowStatus.Evaluate(owned with { Expected = null }, []).Text == "保存未完成", "No snapshot and no rule is unfinished save");
        Check(FirewallRowStatus.Evaluate(owned with { Expected = null }, [actual]).Text == "待核验", "Actual rule without snapshot requires verification");
        Check(FirewallRowStatus.Evaluate(owned, [actual, actual]).Text == "同名冲突", "Report duplicate names");
        Check(FirewallRowStatus.Evaluate(owned, [actual with { Enabled = false }]).Text == "外部已修改", "Report external enabled-state change");
        Check(FirewallRowStatus.Evaluate(owned, [actual with { RemoteAddresses = "*" }]).Text == "外部已修改", "Report external source-scope change");
        var status = FirewallRowStatus.Evaluate(owned, [actual]);
        Check(status.Tooltip.Contains("192.168.1.0/24") && status.Tooltip.Contains("专用") && !status.Tooltip.Contains("不代表"), "Tooltip contains source and profiles without connectivity disclaimers");
        Check(scope.ProfileNames == "专用" && (scope with { Profiles = FirewallProfiles.Private | FirewallProfiles.Domain | FirewallProfiles.Public }).ProfileNames == "专用、域、公用",
            "Network profiles use consistent Chinese labels");
        var row = new RuleRow(new RealmRuntimeState(new RealmRule(Guid.NewGuid(), rule, rule, true), true));
        var notifications = new List<string>();
        row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);
        row.UpdateFirewallStatus(status);
        notifications.Clear();
        row.UpdateFirewallStatus(status);
        Check(notifications.Count == 0, "Unchanged firewall status refresh produces no UI notifications");
        row.UpdateFirewallStatus(FirewallRowStatus.Evaluate(owned, []));
        Check(notifications.Contains(nameof(RuleRow.FirewallStatusText)), "Changed firewall status updates existing row in place");
        var snapshot = FirewallStatusReader.Read();
        Check(snapshot.Error is null, "Read-only batched Windows firewall status query succeeds");
        Check(snapshot.ForListener("192.0.2.254:65535").Text == "未配置", "Unknown listener has unconfigured status");
        var failed = new FirewallStatusSnapshot(new Dictionary<string, FirewallRowStatus>(), "test error");
        Check(failed.ForListener(rule.ListenEndpoint).Text == "查询失败", "Query errors shown without losing forwarding list");
        return count;
    }
}

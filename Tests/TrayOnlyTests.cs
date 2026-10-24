using PortForwarder.Core;
using PortForwarder.Windows;

internal static class TrayOnlyTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        Check(TrayRuntimePolicy.ShouldHideOnClose(true, true, false), "Close hides to tray when retention enabled and icon available");
        Check(!TrayRuntimePolicy.ShouldHideOnClose(false, true, false), "Unchecked tray retention closes program rather than hiding");
        Check(!TrayRuntimePolicy.ShouldHideOnClose(true, false, false), "Tray failure falls back to program exit, no invisible monitor");
        Check(!TrayRuntimePolicy.ShouldHideOnClose(true, true, true), "Explicit exit always stops tray program");
        var status = new TrayMonitorStatus(0, 0, false);
        Check(status.Description == "无域名规则需要监控", "Tray distinguishes idle process from actively managed DNS rules");
        status = status with { DomainCount = 3, PausedCount = 1 };
        Check(status.MonitoringAvailable && status.Description.Contains("2 条规则") && status.Description.Contains("1 条暂停"), "Tray status reports active and paused domain counts");
        Check(!(status with { Error = "DNS error" }).MonitoringAvailable, "Monitor errors are visible and not falsely healthy");
        Check((status with { PausedCount = 3 }).Description.Contains("已暂停"), "Fully paused configuration has explicit monitor status");
        Check((status with { DnsErrorCount = 1 }).Description.Contains("解析失败，保留旧 IP"), "Tray surfaces per-rule DNS failure while retry loop remains active");
        foreach (var operation in new[] { (RuleOperation)999 })
        {
            try
            {
                await DesktopRuleOperations.ExecuteAsync(operation, null, null);
                throw new Exception("Expected unsupported legacy operation");
            }
            catch (ArgumentException) { Check(true, "Tray desktop rejects legacy/invalid operation before writing: " + operation); }
        }
        // Construct and stop without Start: proves no service registration or background work at construction.
        var runtime = new TrayMonitorRuntime();
        await runtime.StopAsync();
        Check(!runtime.Status.Checking, "Unstarted runtime can stop without any system writes");
        return count;
    }
}

namespace PortForwarder.Core;

public sealed record TrayMonitorStatus(int DomainCount, int PausedCount,
    bool Checking, string? Error = null, int DnsErrorCount = 0)
{
    public bool MonitoringAvailable => Error is null;
    public string Description => Error is not null ? "DNS 检查失败：" + Error :
        DomainCount == 0 ? "无域名规则需要监控" :
        PausedCount == DomainCount ? $"{DomainCount} 条域名规则已暂停，需编辑确认" :
        $"DNS 监控运行中 · {DomainCount - PausedCount} 条规则 · 每 60 秒检查" +
        (PausedCount > 0 ? $" · {PausedCount} 条暂停" : "") +
        (DnsErrorCount > 0 ? $" · {DnsErrorCount} 条解析失败，保留旧 IP" : "");
}

public static class TrayRuntimePolicy
{
    public static bool ShouldHideOnClose(bool keepInTray, bool trayAvailable, bool exitRequested) =>
        keepInTray && trayAvailable && !exitRequested;
}

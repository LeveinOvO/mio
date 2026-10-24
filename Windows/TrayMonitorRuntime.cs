using PortForwarder.Core;

namespace PortForwarder.Windows;

public sealed class TrayMonitorRuntime
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _worker;
    private TrayMonitorStatus _status = new(0, 0, false);
    public TrayMonitorStatus Status => Volatile.Read(ref _status);
    public void Start() { if (_worker is not null) throw new InvalidOperationException("监控已启动。"); _worker = Task.Run(RunAsync); }
    public void RequestCheck() { try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { } }
    public async Task StopAsync()
    {
        _stop.Cancel(); RequestCheck();
        if (_worker is not null) { await _worker; await RealmRuntime.StopAsync(); }
    }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await RealmRuntime.ReconcileAsync(_stop.Token);
                await RealmRuntime.CheckDnsAsync(_stop.Token);
                var config = RealmStore.Read();
                var domains = config.Rules.Where(r => r.Enabled && DnsTargets.IsHostname(r.Desired)).ToList();
                Volatile.Write(ref _status, new(domains.Count, 0, false, DnsErrorCount: domains.Count(r => r.Message.StartsWith("解析失败"))));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) { Volatile.Write(ref _status, Status with { Error = error.Message }); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(60), _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
}

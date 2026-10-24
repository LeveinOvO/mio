using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using PortForwarder.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PortForwarder.Core;
using PortForwarder.Windows;

namespace PortForwarder;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<RuleRow> _rules = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private IReadOnlyList<ListenAddressOption> _addresses = [];
    private bool _busy;
    private bool _shuttingDown;
    private bool _refreshing;
    private bool _rulesReadable = true;
    private bool _syncingRows;
    private int _interactionVersion;
    private bool _dialogOpen;
    private string? _hostError;
    public bool HasPendingInteraction => _busy || _dialogOpen;
    public bool KeepInTray => KeepInTrayBox.IsChecked == true;
    public void NotifyShuttingDown()
    {
        _shuttingDown = true;
        _statusTimer.Stop();
        SetBusy(true);
        KeepInTrayBox.IsEnabled = false;
        ShowMessage(InfoBarSeverity.Informational, "正在停止 Realm 转发…");
    }
    public void NotifyExitBlocked() => ShowMessage(InfoBarSeverity.Warning, "请先完成当前操作或关闭对话框，再退出程序。");
    public void ReportHostError(string message)
    {
        _hostError = message;
        ShowMessage(InfoBarSeverity.Error, "原生宿主：" + message);
    }

    public MainPage()
    {
        InitializeComponent();
        KeepInTrayBox.IsChecked = TrayPreferences.Load().KeepInTray;
        RuleList.ItemsSource = _rules;
        Loaded += async (_, _) => { await RefreshAsync(); _statusTimer.Start(); };
        Unloaded += (_, _) => _statusTimer.Stop();
        _statusTimer.Tick += async (_, _) =>
        {
            if (!_busy && !_dialogOpen && !_refreshing) await RefreshAsync(keepMessage: true, silent: true);
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Add_Click(object sender, RoutedEventArgs e) => await EditRuleAsync(null);
    private async void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RuleRow rule }) await EditRuleAsync(rule);
    }

    private async void RowFirewall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen || !_rulesReadable || sender is not Button { Tag: RuleRow row }) return;
        _interactionVersion++;
        _dialogOpen = true;
        try
        {
            var host = await NativeHostClient.StateAsync();
            var existing = host.FirewallConfig.Rules.FirstOrDefault(r => r.Scope.ListenerKey == row.Actual.ListenerKey);
            var editor = new FirewallEditor(row.Actual, row.IsEnabled, existing) { XamlRoot = XamlRoot };
            var answer = await editor.ShowAsync();
            if (!editor.RemoveRequested && (answer != ContentDialogResult.Primary || editor.Scope is null)) return;
            var scope = editor.Scope;
            var preview = editor.RemoveRequested
                ? $"删除 {row.Actual.ListenEndpoint} 对应的防火墙规则？\n端口转发保持不变。"
                : $"{row.Actual.ProtocolText} 入站：{row.Actual.ListenEndpoint}\n来源：{(scope!.SourceMode == FirewallSourceMode.Any ? "任意来源 IP" : string.Join(", ", scope.SourceCidrs))}\n网络配置文件：{scope.ProfileNames}\n状态：{(row.IsEnabled ? "启用" : "禁用，开启转发时一起启用")}" +
                  (scope.SourceMode == FirewallSourceMode.Any ? "\n\n将允许任意来源 IP 访问此端口。" : "") +
                  (row.Actual.ListenAddress == "0.0.0.0" ? "\n此规则应用于所有 IPv4 网卡地址。" : "");
            var confirm = new ContentDialog { XamlRoot = XamlRoot,
                Title = editor.RemoveRequested ? "删除防火墙规则？" : "确认防火墙规则范围",
                Content = preview, PrimaryButtonText = editor.RemoveRequested ? "确认删除" : "确认保存",
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            SetBusy(true);
            var result = editor.RemoveRequested && existing is not null
                ? await NativeHostClient.RemoveFirewallAsync(existing)
                : await NativeHostClient.ConfigureFirewallAsync(row.Rule, scope!, existing);
            if (!result.Success) throw new IOException(result.Message);
            ShowMessage(InfoBarSeverity.Success, editor.RemoveRequested ? "防火墙规则已删除，端口转发保留。" : "防火墙规则已保存。");
        }
        catch (Exception error) { RuntimeLogger.Error("firewall.operation.failed", error); ShowMessage(InfoBarSeverity.Error, error.Message); }
        finally
        {
            SetBusy(false);
            _dialogOpen = false;
            UpdateButtons();
        }
        await RefreshAsync(keepMessage: true, silent: true);
    }
    private async void RuleList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement source)
        {
            for (DependencyObject? node = source; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (node is CheckBox or ToggleSwitch or Button) return;
        }
        if (RuleList.SelectedItem is RuleRow rule) await EditRuleAsync(rule);
    }
    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async Task RefreshAsync(bool keepMessage = false, bool silent = false)
    {
        if (_busy || _refreshing || _shuttingDown) return;
        _refreshing = true;
        var interactionVersion = _interactionVersion;
        if (!silent) SetBusy(true);
        if (!keepMessage) StatusMessage.IsOpen = false;
        try
        {
            var host = await NativeHostClient.StateAsync();
            var data = (Addresses: await Task.Run(NetworkAddresses.Read), Realm: host.Rules, Firewall: NativeHostClient.FirewallStatus(host));
            if (_shuttingDown) return;
            // Discard background snapshots if the user started an operation while querying.
            if (silent && (_busy || _dialogOpen || interactionVersion != _interactionVersion)) return;
            _rulesReadable = true;
            _hostError = null;
            var monitoring = host.Monitor;
            var serviceText = monitoring.Description;
            if (ServiceStatusText.Text != serviceText) ServiceStatusText.Text = serviceText;
            _syncingRows = true;
            try
            {
                RuleListUpdater.Apply(_rules, data.Realm);
                foreach (var row in _rules)
                {
                    row.UpdateFirewallStatus(data.Firewall.ForListener(row.Actual.ListenerKey));
                }
            }
            finally { _syncingRows = false; }
            _addresses = data.Addresses;
            EmptyState.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = "尚无端口转发规则";
            var countText = $"{_rules.Count} 条规则 · {_rules.Count(r => r.IsEnabled)} 条启用 · Realm · TCP / UDP";
            if (RuleCountText.Text != countText) RuleCountText.Text = countText;
        }
        catch (Exception error)
        {
            RuntimeLogger.Error("state.refresh.failed", error, silent ? "background=true" : "background=false");
            if (_shuttingDown) return;
            if (silent && (_busy || _dialogOpen || interactionVersion != _interactionVersion)) return;
            // Keep the last snapshot visible, but block stale edits until a query succeeds.
            _rulesReadable = false;
            if (_rules.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                EmptyStateText.Text = "无法读取系统规则";
            }
            ShowMessage(InfoBarSeverity.Error, "刷新失败，暂时无法编辑。请重新刷新：" + error.Message);
        }
        finally
        {
            _refreshing = false;
            if (!silent && !_shuttingDown) SetBusy(false);
            else UpdateButtons();
            if (_hostError is not null && !keepMessage) ReportHostError(_hostError);
        }
    }

    private async Task EditRuleAsync(RuleRow? row)
    {
        if (_busy || _dialogOpen || !_rulesReadable) return;
        _interactionVersion++;
        _dialogOpen = true;
        try
        {
            var original = row?.Actual;
            var editor = new RuleEditor(_addresses, row?.Editable) { XamlRoot = XamlRoot };
            if (await editor.ShowAsync() != ContentDialogResult.Primary || editor.Rule is null) return;
            var updated = editor.Rule;
            if (row is not null && row.Editable.Matches(updated)) return;
            await ExecuteAsync(original is null ? RuleOperation.Add : RuleOperation.Edit, original, updated, row?.Rule);
        }
        catch (Exception error) { RuntimeLogger.Error("rule.edit.failed", error); ShowMessage(InfoBarSeverity.Error, error.Message); }
        finally { _dialogOpen = false; UpdateButtons(); }
    }

    private async Task ExecuteAsync(RuleOperation operation, ForwardRule? original, ForwardRule? updated,
        RealmRule? expected = null, List<RuleBatchItem>? items = null)
    {
        SetBusy(true);
        try
        {
            var result = await NativeHostClient.OperationAsync(operation, original, updated, expected, items);
            if (!result.Success) RuntimeLogger.Warning("rule.operation.rejected", result.Message);
            ShowMessage(result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning, result.Message);
        }
        catch (Exception error) { RuntimeLogger.Error("rule.operation.failed", error); ShowMessage(InfoBarSeverity.Error, error.Message); }
        finally { SetBusy(false); }
        await RefreshAsync(keepMessage: true);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        RefreshButton.IsEnabled = !_busy && !_dialogOpen;
        AddButton.IsEnabled = !_busy && !_dialogOpen && _rulesReadable;
        RuleList.IsEnabled = !_busy;
        var checkedRows = _rules.Where(r => r.IsChecked).ToList();
        CheckedCountText.Text = $"已勾选 {checkedRows.Count} 项";
        SelectAllBox.IsChecked = checkedRows.Count == 0 ? false : checkedRows.Count == _rules.Count ? true : null;
        SelectAllBox.IsEnabled = !_busy && !_dialogOpen && _rules.Count > 0;
        BatchEnableButton.IsEnabled = !_busy && !_dialogOpen && _rulesReadable && checkedRows.Any(r => !r.IsEnabled);
        BatchDisableButton.IsEnabled = !_busy && !_dialogOpen && _rulesReadable && checkedRows.Any();
        BatchDeleteButton.IsEnabled = !_busy && !_dialogOpen && _rulesReadable && checkedRows.Any();
    }

    private void ShowMessage(InfoBarSeverity severity, string message)
    {
        StatusMessage.Severity = severity;
        StatusMessage.Message = message;
        StatusMessage.IsOpen = true;
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateButtons();
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var check = !_rules.All(r => r.IsChecked);
        foreach (var row in _rules) row.IsChecked = check;
        UpdateButtons();
    }

    private async void RuleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not RuleRow row || _syncingRows || toggle.IsOn == row.IsEnabled) return;
        var enable = toggle.IsOn;
        // Do not display an optimistic state when UAC can be cancelled or Windows can reject a write.
        toggle.IsOn = row.IsEnabled;
        if (_busy || _dialogOpen || !_rulesReadable) return;
        _interactionVersion++;
        _dialogOpen = true;
        try
        {
            await ExecuteAsync(enable ? RuleOperation.Enable : RuleOperation.Disable, row.Actual, null, row.Rule);
        }
        finally { _dialogOpen = false; UpdateButtons(); }
    }

    private async void BatchEnable_Click(object sender, RoutedEventArgs e) => await BatchAsync(RuleOperation.Enable);
    private async void BatchDisable_Click(object sender, RoutedEventArgs e) => await BatchAsync(RuleOperation.Disable);
    private async void BatchDelete_Click(object sender, RoutedEventArgs e) => await BatchAsync(RuleOperation.Delete);

    private async Task BatchAsync(RuleOperation operation)
    {
        if (_busy || _dialogOpen || !_rulesReadable) return;
        var rows = _rules.Where(r => r.IsChecked && (operation == RuleOperation.Delete ||
            (operation == RuleOperation.Enable ? !r.IsEnabled : r.IsEnabled))).ToList();
        if (rows.Count == 0) return;
        if (rows.Count > 100) { ShowMessage(InfoBarSeverity.Warning, "每批最多 100 条，请减少勾选数量。"); return; }
        _interactionVersion++;
        _dialogOpen = true;
        try
        {
            var verb = operation == RuleOperation.Enable ? "启用" : operation == RuleOperation.Disable ? "停用" : "删除";
            var preview = string.Join("\n", rows.Take(8).Select(r => $"{r.Actual.ListenEndpoint} → {r.Editable.ConnectEndpoint}"));
            if (rows.Count > 8) preview += $"\n…另有 {rows.Count - 8} 条";
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"批量{verb} {rows.Count} 条规则？",
                Content = preview + "\n\n" + (operation == RuleOperation.Delete
                    ? "删除所选端口转发及其配置，同时删除对应的防火墙规则。"
                    : operation == RuleOperation.Disable ? "停止所选端口转发，保留配置，同时禁用对应的防火墙规则。"
                    : "恢复所选端口转发，同时启用对应的防火墙规则。域名将重新解析；0.0.0.0 表示所有 IPv4 网卡地址。"),
                PrimaryButtonText = $"确认{verb}", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            var items = rows.Select(r => new RuleBatchItem(operation, r.Actual, null, r.Rule)).ToList();
            await ExecuteAsync(RuleOperation.Batch, null, null, items: items);
        }
        catch (Exception error) { ShowMessage(InfoBarSeverity.Error, error.Message); }
        finally { _dialogOpen = false; UpdateButtons(); }
    }

    private void KeepInTray_Click(object sender, RoutedEventArgs e)
    {
        try { new TrayPreferences(KeepInTray).Save(); }
        catch (Exception error) { ShowMessage(InfoBarSeverity.Warning, "托盘设置保存失败：" + error.Message); }
    }

}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortForwarder.Core;
using PortForwarder.Services;
using PortForwarder.Windows;

namespace PortForwarder;

public sealed partial class RoutesPage : Page
{
    private RouteState _state = new([], []);
    private bool _busy;
    private bool _dialog;
    private bool _readable;
    private RouteDraft? _pendingSelection;
    public bool HasPendingInteraction => _busy || _dialog;
    public RoutesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }
    private void Message(InfoBarSeverity severity, string text) { Notice.Severity = severity; Notice.Message = text; Notice.IsOpen = true; }
    private void Buttons()
    {
        var writable = _readable && string.IsNullOrEmpty(_state.WriteBlockedReason);
        AddButton.IsEnabled = writable && !HasPendingInteraction;
        EditButton.IsEnabled = DeleteButton.IsEnabled = writable && !HasPendingInteraction && RouteList.SelectedItem is RouteEntry { Editable: true };
        RefreshButton.IsEnabled = !HasPendingInteraction;
        BusyRing.IsActive = _busy;
    }
    private void ApplyFilter()
    {
        if (RouteList is null || FilterBox is null || CategoryBox is null || CountText is null) return;
        var selected = RouteList.SelectedItem as RouteEntry;
        var filter = FilterBox.Text.Trim();
        var rows = _state.Routes
            .Where(r => CategoryBox.SelectedIndex != 1 || r.Editable)
            .Where(r => CategoryBox.SelectedIndex != 2 || !r.Editable)
            .Where(r => $"{r.DestinationPrefix} {r.NextHop} {r.InterfaceText}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Editable)
            .ThenByDescending(r => r.Store == "Persistent")
            .ThenBy(r => AddressOrder(r.DestinationPrefix.Split('/')[0]))
            .ThenBy(r => int.Parse(r.DestinationPrefix.Split('/')[1]))
            .ThenBy(r => r.InterfaceIndex)
            .ThenBy(r => AddressOrder(r.NextHop)).ToList();
        RouteList.ItemsSource = rows;
        if (selected is not null)
            RouteList.SelectedItem = rows.FirstOrDefault(r => r.DestinationPrefix == selected.DestinationPrefix && r.NextHop == selected.NextHop && r.InterfaceIndex == selected.InterfaceIndex);
        CountText.Text = $"显示 {rows.Count} / {_state.Routes.Count} 条 · 静态 {_state.Routes.Count(r => r.Editable)} 条";
        Buttons();
    }
    private static uint AddressOrder(string address) => address.Split('.').Aggregate(0u, (value, part) => (value << 8) | byte.Parse(part));
    private async Task RefreshAsync()
    {
        if (HasPendingInteraction) return;
        _busy = true; Buttons();
        try
        {
            _state = await NativeHostClient.RoutesAsync(); _readable = true;
            if (!string.IsNullOrEmpty(_state.WriteBlockedReason)) Message(InfoBarSeverity.Warning, _state.WriteBlockedReason);
            if (_pendingSelection is not null)
            {
                // A successful operation must not be hidden by an old text/category filter.
                FilterBox.Text = "";
                if (CategoryBox.SelectedIndex == 2) CategoryBox.SelectedIndex = 0;
            }
            ApplyFilter();
            if (_pendingSelection is { } target)
            {
                var row = _state.Routes.FirstOrDefault(r => r.DestinationPrefix == target.DestinationPrefix && r.NextHop == target.NextHop && r.InterfaceIndex == target.InterfaceIndex);
                if (row is not null) { RouteList.SelectedItem = row; RouteList.ScrollIntoView(row); }
                _pendingSelection = null;
            }
        }
        catch (Exception error) { _readable = false; RuntimeLogger.Error("routes.refresh.failed", error); Message(InfoBarSeverity.Error, error.Message); }
        finally { _busy = false; Buttons(); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { Notice.IsOpen = false; await RefreshAsync(); }
    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Category_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => Buttons();
    private async void Add_Click(object sender, RoutedEventArgs e) => await EditAsync(null);
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (RouteList.SelectedItem is RouteEntry { Editable: true } row) await EditAsync(row); }
    private async Task EditAsync(RouteEntry? original)
    {
        if (HasPendingInteraction || !_readable) return;
        _dialog = true; Buttons();
        var prefix = new TextBox { Header = "目标网段（CIDR）", Text = original?.DestinationPrefix ?? "", PlaceholderText = "192.168.10.0/24" };
        var hop = new TextBox { Header = "下一跳（直连填写 0.0.0.0）", Text = original?.NextHop ?? "" };
        var interfaces = new ComboBox { Header = "出口网卡", ItemsSource = _state.Interfaces, DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
        interfaces.SelectedItem = _state.Interfaces.FirstOrDefault(i => i.Index == original?.InterfaceIndex);
        var manual = new CheckBox { Content = "手动指定出口网卡", IsChecked = original is not null };
        var matchText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        RouteInterface? automatic = null;
        void UpdateMatch()
        {
            interfaces.Visibility = manual.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            automatic = null;
            if (manual.IsChecked == true) { matchText.Text = "使用手动指定的网卡（编辑时保留原接口）。"; return; }
            try
            {
                var candidates = RouteInterfaceSelection.Candidates(hop.Text.Trim(), _state.Interfaces);
                automatic = candidates.Count == 1 ? candidates[0] : null;
                matchText.Text = automatic is not null ? $"自动出口：{automatic.Label}" :
                    hop.Text.Trim() == "0.0.0.0" ? "直连路由请勾选手动指定出口网卡。" :
                    candidates.Count > 1 ? "多个网卡可直达下一跳，请手动选择：" + string.Join("、", candidates.Select(i => i.Name)) :
                    "未找到直连下一跳的有效网卡；请检查地址或手动指定特殊接口。";
            }
            catch (ArgumentException) { matchText.Text = "输入下一跳 IPv4 后自动匹配出口网卡。"; }
        }
        hop.TextChanged += (_, _) => UpdateMatch();
        manual.Checked += (_, _) => UpdateMatch();
        manual.Unchecked += (_, _) => UpdateMatch();
        UpdateMatch();
        var metric = new NumberBox { Header = "路由 Metric（0–65535）", Minimum = 0, Maximum = 65535, Value = original?.RouteMetric ?? 256, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var persistent = new CheckBox { Content = "持久路由（Windows 重启后保留）", IsChecked = original?.Store == "Persistent" };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(prefix, "RoutePrefix");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(hop, "RouteHop");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(interfaces, "RouteInterface");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(metric, "RouteMetric");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(persistent, "RoutePersistent");
        var panel = new StackPanel { Spacing = 12, Width = 500 };
        foreach (var element in new UIElement[] { prefix, hop, matchText, manual, interfaces, metric, persistent, errorText }) panel.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = original is null ? "添加 IPv4 路由" : "编辑 IPv4 路由", Content = panel,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        RouteDraft? draft = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                UpdateMatch();
                var iface = manual.IsChecked == true ? interfaces.SelectedItem as RouteInterface : automatic;
                if (iface is null) throw new ArgumentException("无法唯一确定出口网卡，请检查下一跳或手动选择。");
                if (!double.IsFinite(metric.Value) || metric.Value != Math.Truncate(metric.Value)) throw new ArgumentException("Metric 必须为整数。");
                draft = RouteValidation.Normalize(new(prefix.Text, hop.Text, iface.Index, checked((uint)metric.Value), persistent.IsChecked == true, manual.IsChecked != true));
            }
            catch (Exception error) { args.Cancel = true; errorText.Text = error.Message; }
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && draft is not null)
                await ExecuteAsync(original is null ? "Add" : "Edit", original, draft);
        }
        catch (Exception error) { RuntimeLogger.Error("routes.edit.failed", error); Message(InfoBarSeverity.Error, error.Message); }
        finally { _dialog = false; Buttons(); }
        await RefreshAsync();
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (HasPendingInteraction || RouteList.SelectedItem is not RouteEntry { Editable: true } row) return;
        _dialog = true; Buttons();
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除路由？", Content = $"{row.DestinationPrefix} → {row.NextHop}\n{row.InterfaceText} · {row.StoreText}",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ExecuteAsync("Delete", row, null);
        }
        catch (Exception error) { RuntimeLogger.Error("routes.delete.failed", error); Message(InfoBarSeverity.Error, error.Message); }
        finally { _dialog = false; Buttons(); }
        await RefreshAsync();
    }
    private async Task ExecuteAsync(string operation, RouteEntry? expected, RouteDraft? draft)
    {
        _busy = true; Buttons();
        try
        {
            var result = await NativeHostClient.RouteOperationAsync(operation, expected, draft);
            if (!result.Success) RuntimeLogger.Warning("routes.operation.rejected", result.Message);
            if (result.Success && draft is not null) _pendingSelection = draft;
            Message(result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Success ? "路由已更新。" : result.Message);
        }
        finally { _busy = false; Buttons(); }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortForwarder.Core;

namespace PortForwarder;

public sealed partial class FirewallEditor : ContentDialog
{
    private readonly ForwardRule _rule;
    public FirewallScope? Scope { get; private set; }
    public bool RemoveRequested { get; private set; }

    public FirewallEditor(ForwardRule rule, bool enabled, OwnedFirewallRule? owned)
    {
        _rule = rule;
        InitializeComponent();
        ListenerText.Text = $"{rule.ProtocolText} 入站 · {rule.ListenEndpoint}";
        LifecycleText.Text = enabled ? "防火墙规则随端口转发一起启停和删除。" :
            "当前转发已停用。保存的防火墙规则将保持禁用，开启转发时一起启用。";
        if (owned is not null)
        {
            CidrBox.Text = string.Join("\n", owned.Scope.SourceCidrs);
            CidrMode.IsChecked = owned.Scope.SourceMode == FirewallSourceMode.Cidr;
            AnyMode.IsChecked = owned.Scope.SourceMode == FirewallSourceMode.Any;
            PrivateProfile.IsChecked = owned.Scope.Profiles.HasFlag(FirewallProfiles.Private);
            DomainProfile.IsChecked = owned.Scope.Profiles.HasFlag(FirewallProfiles.Domain);
            PublicProfile.IsChecked = owned.Scope.Profiles.HasFlag(FirewallProfiles.Public);
            ExistingText.Text = owned.Expected is null ? "上次保存未完成，请重新保存或删除这条配置。" :
                $"防火墙规则已创建 · {(owned.Expected.Enabled ? "已启用" : "已禁用")}";
            RemoveButton.Visibility = Visibility.Visible;
        }
        UpdateMode();
    }

    private void SourceMode_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void UpdateMode() { if (CidrBox is not null) CidrBox.IsEnabled = AnyMode?.IsChecked != true; }
    private void Remove_Click(object sender, RoutedEventArgs e) { RemoveRequested = true; Hide(); }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var profiles = (PrivateProfile.IsChecked == true ? FirewallProfiles.Private : 0) |
                (DomainProfile.IsChecked == true ? FirewallProfiles.Domain : 0) |
                (PublicProfile.IsChecked == true ? FirewallProfiles.Public : 0);
            Scope = FirewallValidation.Create(_rule, AnyMode.IsChecked == true ? FirewallSourceMode.Any : FirewallSourceMode.Cidr,
                CidrBox.Text, profiles);
        }
        catch (ArgumentException error)
        {
            args.Cancel = true;
            ValidationMessage.Message = error.Message;
            ValidationMessage.IsOpen = true;
        }
    }
}

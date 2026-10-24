using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using PortForwarder.Core;
using PortForwarder.Services;

namespace PortForwarder;

public sealed partial class RuleEditor : ContentDialog
{
    private readonly string _originalListenAddress;
    private readonly IReadOnlyList<ListenAddressOption> _listenOptions;
    private bool _listenAddressInitialized;
    public ForwardRule? Rule { get; private set; }

    public RuleEditor(IReadOnlyList<ListenAddressOption> addresses, ForwardRule? original)
    {
        _originalListenAddress = original?.ListenAddress ?? "";
        _listenOptions = NetworkAddresses.PrepareForEdit(addresses, original?.ListenAddress);
        InitializeComponent();
        Title = original is null ? "添加转发规则" : "编辑转发规则";
        ListenAddressBox.ItemsSource = _listenOptions;
        ListenAddressBox.Loaded += (_, _) => InitializeListenAddress();
        Opened += (_, _) => InitializeListenAddress();
        if (original is not null)
        {
            ListenPortBox.Text = original.ListenPort.ToString(CultureInfo.InvariantCulture);
            ConnectAddressBox.Text = original.ConnectAddress;
            ConnectPortBox.Text = original.ConnectPort.ToString(CultureInfo.InvariantCulture);
            ProtocolBox.SelectedIndex = original.Protocol == ForwardProtocol.Tcp ? 0 : 1;
        }
    }

    private void InitializeListenAddress()
    {
        // Editable ComboBox applies its text template after construction. Select the original
        // item once the control is loaded. Selection and editable text use the same label.
        if (_listenAddressInitialized) return;
        _listenAddressInitialized = true;
        if (_originalListenAddress.Length == 0) return;
        var option = _listenOptions.First(o => o.Address == _originalListenAddress);
        ListenAddressBox.SelectedItem = option;
        ListenAddressBox.Text = option.DisplayName;
    }

    private void ListenAddress_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ListenAddressBox.SelectedItem is ListenAddressOption option) ListenAddressBox.Text = option.DisplayName;
    }

    private void ListenAddress_Submitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        // Manually entered addresses need not be one of the discovered adapters.
        args.Handled = true;
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            // TextBox exposes current input immediately; no NumberBox commit/spinner required.
            if (!int.TryParse(ListenPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var listenPort) ||
                !int.TryParse(ConnectPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var connectPort))
                throw new ArgumentException("端口必须是 1–65535 之间的整数。");
            var listenAddress = NetworkAddresses.ResolveEditorAddress(ListenAddressBox.Text, _listenOptions);
            Rule = RuleValidation.Create(listenAddress, listenPort,
                ConnectAddressBox.Text, connectPort, ProtocolBox.SelectedIndex == 0 ? ForwardProtocol.Tcp : ForwardProtocol.Udp);
            ValidationMessage.IsOpen = false;
        }
        catch (ArgumentException error)
        {
            args.Cancel = true;
            ValidationMessage.Message = error.Message;
            ValidationMessage.IsOpen = true;
        }
    }
}

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortForwarder.Services;
using PortForwarder.Windows;

namespace PortForwarder;

public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _closeReady;
    private readonly WindowSubclass _subclass;
    private readonly uint _restoreMessage = RegisterWindowMessage("Mio.RestoreGui");
    private readonly uint _hostStoppedMessage = RegisterWindowMessage("Mio.HostStopped");
    private readonly IntPtr _handle;
    private MainPage? _forwardPage;
    private RoutesPage? _routesPage;

    public MainWindow()
    {
        InitializeComponent();
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(_handle) / 96.0;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(1340 * scale), (int)(700 * scale)));
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _forwardPage = new MainPage();
        RootFrame.Content = _forwardPage;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        _subclass = HandleMessage;
        if (!SetWindowSubclass(_handle, _subclass, 1, 0)) throw new IOException("无法初始化配置窗口。");
        AppWindow.Closing += async (_, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (_closing) return;
            if (_forwardPage.HasPendingInteraction || _routesPage?.HasPendingInteraction == true) { _forwardPage.NotifyExitBlocked(); return; }
            _closing = true;
            try
            {
                // Window retention now means retain only the native host, never retain WinUI.
                if (!_forwardPage.KeepInTray)
                {
                    _forwardPage.NotifyShuttingDown();
                    var result = await NativeHostClient.ShutdownAsync();
                    if (!result.Success) throw new IOException(result.Message);
                }
                _closeReady = true; Close();
            }
            catch (Exception error) { RuntimeLogger.Error("window.close.failed", error); _closing = false; ReportHostError(error.Message); }
        };
        Closed += (_, _) => { RemoveWindowSubclass(_handle, _subclass, 1); };
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_forwardPage is null || RootFrame is null) return;
        bool routes = (args.SelectedItem as NavigationViewItem)?.Tag as string == "routes";
        if (_forwardPage.HasPendingInteraction || _routesPage?.HasPendingInteraction == true)
        {
            var selected = Navigation.MenuItems[RootFrame.Content is RoutesPage ? 1 : 0];
            if (Navigation.SelectedItem != selected) Navigation.SelectedItem = selected;
            return;
        }
        RootFrame.Content = routes ? (_routesPage ??= new RoutesPage()) : _forwardPage;
    }
    public void ReportHostError(string message) => _forwardPage?.ReportHostError(message);
    public static void RestoreExisting() => PostMessage(new IntPtr(0xffff), RegisterWindowMessage("Mio.RestoreGui"), UIntPtr.Zero, IntPtr.Zero);
    private IntPtr HandleMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == _restoreMessage) { AppWindow.Show(); if (IsIconic(window)) ShowWindow(window, 9); Activate(); SetForegroundWindow(window); return IntPtr.Zero; }
        if (message == _hostStoppedMessage) { _closeReady = true; Close(); return IntPtr.Zero; }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate IntPtr WindowSubclass(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, WindowSubclass callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, WindowSubclass callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}

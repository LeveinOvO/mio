using Microsoft.UI.Xaml;
using PortForwarder.Windows;
using PortForwarder.Services;

namespace PortForwarder;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _instance;
    public App()
    {
        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception error) RuntimeLogger.Error("ui.unhandled", error);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            RuntimeLogger.Error("ui.unobserved-task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = new Mutex(false, @"Local\Mio.ConfigWindow");
        bool acquired;
        try { acquired = _instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { MainWindow.RestoreExisting(); _instance.Dispose(); _instance = null; Exit(); return; }
        _window = new MainWindow();
        _window.Closed += (_, _) => { _instance!.ReleaseMutex(); _instance.Dispose(); _instance = null; };
        _window.Activate();
        try { await NativeHostClient.EnsureRunningAsync(); }
        catch (Exception error) { RuntimeLogger.Error("host.start.failed", error); if (_window is MainWindow window) window.ReportHostError(error.Message); }
    }
}

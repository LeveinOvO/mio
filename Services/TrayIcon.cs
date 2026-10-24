using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PortForwarder.Services;

// A native notification-area icon. All calls and callbacks stay on the WinUI thread.
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private const string OwnerWindowTitle = "PortForwarder.TrayMonitor.Owner";
    private const uint RestoreMessage = 0x8002;
    private readonly WindowProc _windowProc;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly string _className = "PortForwarder.Tray." + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance;
    private IntPtr _window;
    private IntPtr _icon;
    private readonly uint _taskbarCreated;
    private bool _added;
    private string _tooltip = "Windows 端口转发";

    public bool Available => _added;
    public string? LastError { get; private set; }

    public TrayIcon(string iconPath, Action open, Action exit)
    {
        _open = open;
        _exit = exit;
        _windowProc = HandleMessage;
        _instance = GetModuleHandle(null);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _instance,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProc), ClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // Hidden top-level window receives TaskbarCreated broadcasts after Explorer restarts.
            _window = CreateWindowEx(0, _className, OwnerWindowTitle, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _icon = LoadIconFile(iconPath);
            AddIcon();
        }
        catch { Dispose(); throw; }
    }

    public void SetStatus(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        if (!_added) { AddIcon(); return; }
        var data = Data();
        data.Flags = 0x4; // NIF_TIP
        if (!ShellNotifyIcon(1, ref data))
        {
            _added = false;
            LastError = "更新托盘图标失败，将在下次检查重试。";
        }
    }

    public void ShowHiddenNotice()
    {
        if (!_added) return;
        var data = Data();
        data.Flags = 0x10; // NIF_INFO
        data.InfoTitle = "已隐藏到系统托盘";
        data.Info = "单击图标恢复窗口。留在托盘时继续转发；退出程序停止转发，配置保留。";
        data.InfoFlags = 1;
        ShellNotifyIcon(1, ref data);
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = IconId,
        Flags = 0x1 | 0x2 | 0x4 | 0x80, CallbackMessage = CallbackMessage, Icon = _icon,
        Tip = _tooltip, Info = "", InfoTitle = ""
    };

    private void AddIcon()
    {
        if (_window == IntPtr.Zero || _icon == IntPtr.Zero) return;
        var data = Data();
        _added = ShellNotifyIcon(0, ref data);
        if (!_added)
        {
            LastError = "托盘图标显示失败，请重新打开程序。";
            return;
        }
        LastError = null;
        data.Version = 4;
        ShellNotifyIcon(4, ref data);
    }

    private static IntPtr LoadIconFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("托盘图标文件缺失，请保留完整发布文件夹。", path);
        var icon = LoadImage(IntPtr.Zero, path, 1, 0, 0, 0x10 | 0x40);
        if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载托盘图标：" + path);
        return icon;
    }

    // Verify published native assets without creating any GUI or notification icon.
    internal static void VerifyIconFile(string path)
    {
        var icon = LoadIconFile(path);
        DestroyIcon(icon);
    }

    private IntPtr HandleMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        // Never allow a managed exception to escape a native callback.
        try
        {
            if (message == RestoreMessage) { _open(); return IntPtr.Zero; }
            if (message == _taskbarCreated) { _added = false; AddIcon(); return IntPtr.Zero; }
            if (message == CallbackMessage)
            {
                var code = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
                if (code is 0x400 or 0x401 or 0x405) _open(); // NIN_SELECT, KEYSELECT, BALLOONUSERCLICK
                else if (code == 0x7B) ShowMenu(); // WM_CONTEXTMENU
                return IntPtr.Zero;
            }
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine("Tray callback: " + error.Message); }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, new UIntPtr(1), "打开窗口");
            AppendMenu(menu, 0x800, UIntPtr.Zero, null);
            AppendMenu(menu, 0, new UIntPtr(3), "退出程序（停止转发）");
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var choice = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, IntPtr.Zero);
            PostMessage(_window, 0, UIntPtr.Zero, IntPtr.Zero);
            if (choice == 1) _open();
            if (choice == 3) _exit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            ShellNotifyIcon(2, ref data);
            _added = false;
        }
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        UnregisterClass(_className, _instance);
        GC.KeepAlive(_windowProc);
    }

    internal static void RestoreExistingWindow()
    {
        var window = FindWindow(null, OwnerWindowTitle);
        if (window != IntPtr.Zero) PostMessage(window, RestoreMessage, UIntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr WindowProcedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
}

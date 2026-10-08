using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CloCloWidget;

// The notification-area ("tray") icon, straight on Windows' Shell_NotifyIcon
// instead of Windows Forms' NotifyIcon, so CloClo doesn't carry the whole of
// Windows Forms for one icon. A hidden window receives the icon's mouse
// messages; the icon is added back if Explorer restarts, and removed on
// Dispose.
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001;           // WM_APP + 1: our callback message
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const int NIM_ADD = 0, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly string _tooltip;
    private readonly int _taskbarCreated;
    private bool _added;

    public event Action? DoubleClick;
    public event Action? RightClick;

    // The window that gets the icon's messages - also the one to bring to the
    // foreground before showing the icon's menu, so the menu closes when you
    // click elsewhere.
    public IntPtr Handle => _window.Handle;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        // Never shown: no WS_VISIBLE, zero size, a tool window (no taskbar button).
        _window = new HwndSource(new HwndSourceParameters("CloCloTray")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x80,                                    // WS_EX_TOOLWINDOW
        });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // The exe's own icon (ApplicationIcon in the csproj), small size;
        // Windows' generic application icon if that can't be read.
        var small = new IntPtr[1];
        if (Environment.ProcessPath != null && ExtractIconEx(Environment.ProcessPath, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
        {
            _icon = small[0]; _ownsIcon = true;
        }
        else
        {
            _icon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION, shared - never destroyed
        }
        Add();
    }

    private void Add()
    {
        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Ignore callbacks for other tray IDs; this hidden window may receive
        // unrelated WM_APP messages, and only our own icon should dispatch UI.
        if (msg == WM_APP_TRAY && wParam.ToInt32() == 1)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
                case WM_RBUTTONUP: RightClick?.Invoke(); break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted and the notification area is empty again.
            Add();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_ownsIcon) DestroyIcon(_icon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
}

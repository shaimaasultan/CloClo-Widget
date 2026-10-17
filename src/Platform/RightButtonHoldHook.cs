using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CloCloWidget;

// Hold to talk anywhere on screen: a low-level mouse hook (no DLL is loaded
// into other programs; Windows calls back into this process) watches the
// right button. A press is held back: released before HoldTime, the click is
// replayed to the window under the cursor, so ordinary right-click menus keep
// working; moved away first, it is replayed as the start of a right-drag;
// held for HoldTime, it becomes hold to talk and the release is consumed.
// Right-clicks on windows of a higher privilege level (admin apps) are left
// alone, because Windows doesn't let this process replay clicks into them.
// Only the right button's state is used; positions are never stored.
internal sealed class RightButtonHoldHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int DragSlop = 8;
    // Marks the clicks replayed here, so the hook lets them through.
    private static readonly IntPtr ReplayMarker = new(0x436C6F43); // "CloC"

    private enum State { Idle, Pending, Held }

    private readonly HookProc _proc; // kept referenced while the hook is set
    private readonly DispatcherTimer _holdTimer;
    private IntPtr _hook;
    private State _state;
    private POINT _origin;

    public event Action? HoldStarted;
    public event Action? HoldEnded;

    public RightButtonHoldHook(TimeSpan holdTime)
    {
        _proc = HookCallback;
        _holdTimer = new DispatcherTimer { Interval = holdTime };
        _holdTimer.Tick += HoldTimer_Tick;
        // The callback runs on this (UI) thread, which pumps messages.
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        _holdTimer.Stop();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        // A press held back but not yet replayed is replayed now.
        if (_state == State.Pending) SendRightButton(down: true, up: true);
        _state = State.Idle;
    }

    private void HoldTimer_Tick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (_state != State.Pending) return;
        _state = State.Held;
        HoldStarted?.Invoke();
    }

    // Kept short: Windows drops a hook that is slow to answer.
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == ReplayMarker) return CallNextHookEx(_hook, nCode, wParam, lParam);

        switch ((int)wParam)
        {
            case WM_RBUTTONDOWN:
                if (_state == State.Held)
                {
                    // The release was never seen (a secure desktop, say).
                    _state = State.Idle;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => HoldEnded?.Invoke()));
                }
                if (_state != State.Idle || !CanReplayTo(data.pt)) break;
                _state = State.Pending;
                _origin = data.pt;
                _holdTimer.Start();
                return 1;

            case WM_MOUSEMOVE:
                if (_state != State.Pending ||
                    (Math.Abs(data.pt.X - _origin.X) <= DragSlop && Math.Abs(data.pt.Y - _origin.Y) <= DragSlop)) break;
                // A right-drag: press at the original place, then carry on.
                _holdTimer.Stop();
                _state = State.Idle;
                var origin = _origin;
                var current = data.pt;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    SetCursorPos(origin.X, origin.Y);
                    SendRightButton(down: true, up: false);
                    SetCursorPos(current.X, current.Y);
                }));
                break;

            case WM_RBUTTONUP:
                if (_state == State.Pending)
                {
                    _holdTimer.Stop();
                    _state = State.Idle;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => SendRightButton(down: true, up: true)));
                    return 1;
                }
                if (_state == State.Held)
                {
                    _state = State.Idle;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => HoldEnded?.Invoke()));
                    return 1;
                }
                break;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SendRightButton(bool down, bool up)
    {
        var inputs = new INPUT[(down ? 1 : 0) + (up ? 1 : 0)];
        var i = 0;
        if (down) inputs[i++] = Mouse(MOUSEEVENTF_RIGHTDOWN);
        if (up) inputs[i] = Mouse(MOUSEEVENTF_RIGHTUP);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Mouse(uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = ReplayMarker } },
    };

    // Windows blocks replayed input into a process running at a higher
    // integrity level, so a press held back there would be lost. Those
    // windows (and any that can't be checked) keep their right-click as is.
    private static bool CanReplayTo(POINT pt)
    {
        var window = WindowFromPoint(pt);
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return false;
        if (processId == Environment.ProcessId) return true;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return false;
            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) &&
                       elevated == 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // As large as its biggest member, or SendInput rejects every input.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

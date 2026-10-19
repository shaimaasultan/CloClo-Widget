using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CloCloWidget;

// The apps with a window on the taskbar, so a voice shutdown can name them
// and close them first instead of being held by them. Names come from each
// program's file description ("Microsoft Word"), never from window titles,
// which can name private documents. Closing asks each window to close, as
// its X button does, so an app with unsaved work can still ask to save.
internal static class OpenApps
{
    // ProcessId is the app's own process; for a Store app that's the one
    // behind its frame, not ApplicationFrameHost. IsShell marks Explorer,
    // whose folder windows are closed but whose process is never ended.
    internal sealed record App(string Name, uint ProcessId, IntPtr[] Windows, bool IsShell);

    internal static List<App> List()
    {
        var own = (uint)Environment.ProcessId;
        var windows = new Dictionary<uint, List<IntPtr>>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsTaskbarWindow(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;
            if (ImageName(pid) == "applicationframehost.exe" && CoreWindowProcess(hwnd) is { } app) pid = app;
            if (pid == own) return true;
            if (!windows.TryGetValue(pid, out var list)) windows[pid] = list = [];
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        var apps = new List<App>();
        foreach (var (pid, list) in windows)
        {
            var path = ImagePath(pid);
            var image = path == null ? "" : Path.GetFileName(path).ToLowerInvariant();
            apps.Add(new App(DisplayName(path), pid, list.ToArray(), image == "explorer.exe"));
        }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Asks every window of the app to close. False when Windows refused
    // (an app running as administrator, which CloClo can't message).
    internal static bool Close(App app)
    {
        var sent = false;
        foreach (var hwnd in app.Windows)
            if (IsWindow(hwnd) && PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) sent = true;
        return sent;
    }

    // Ends the app's process without letting it save. Only after the user
    // said "force close" for apps that stayed open; never Explorer.
    internal static bool ForceClose(App app)
    {
        if (app.IsShell) return false;
        try
        {
            using var process = Process.GetProcessById((int)app.ProcessId);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false; // already gone, or running as administrator
        }
    }

    internal static bool IsStillOpen(App app) =>
        app.Windows.Any(hwnd => IsWindow(hwnd) && IsWindowVisible(hwnd));

    // The same test the taskbar uses, roughly: a visible, unowned, titled
    // window that isn't a tool window or hidden on another virtual desktop.
    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    // A Store app's frame belongs to ApplicationFrameHost; the app itself
    // owns the CoreWindow inside it.
    private static uint? CoreWindowProcess(IntPtr frame)
    {
        GetWindowThreadProcessId(frame, out var framePid);
        uint? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var pid);
            if (pid == framePid) return true;
            found = pid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string DisplayName(string? path)
    {
        if (path == null) return "An app";
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) &&
                !description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return description;
        }
        catch (FileNotFoundException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? ImageName(uint pid) => ImagePath(pid) is { } path ? Path.GetFileName(path).ToLowerInvariant() : null;

    private static string? ImagePath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint WM_CLOSE = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

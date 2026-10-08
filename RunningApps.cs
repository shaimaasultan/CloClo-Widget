using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CloCloWidget;

// AppUserModelId is null for the (common) case where the app never
// explicitly registered one via the property store — most plain Win32
// apps (Notepad, Explorer, ...) don't; only Store/UWP-style apps and a
// handful of others (browsers, Spotify) reliably do. WindowHandle is
// always present, so there's still a way to bring the app to the
// foreground even without an AUMID to watch media through.
public readonly record struct RunningApp(string Title, string? AppUserModelId, IntPtr WindowHandle);

// Enumerates visible top-level windows (roughly the same set Alt-Tab
// shows) and, where available, resolves each one's AppUserModelId — the
// same property the taskbar itself groups windows by — via
// SHGetPropertyStoreForWindow. This is what lets the search picker offer
// literally anything running, not just apps that already happen to hold
// a media session.
public static class RunningApps
{
    public static List<RunningApp> List()
    {
        var results = new List<RunningApp>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true; // dialogs/owned popups, not top-level apps
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            var len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            var aumid = TryGetAppUserModelId(hwnd);
            // Without an AUMID, dedupe by process instead, so multiple
            // windows from the same app (e.g. several Explorer windows)
            // collapse to one entry rather than one per window.
            GetWindowThreadProcessId(hwnd, out var pid);
            var dedupeKey = !string.IsNullOrWhiteSpace(aumid) ? aumid! : $"pid:{pid}";
            if (seenKeys.Add(dedupeKey))
            {
                results.Add(new RunningApp(title, string.IsNullOrWhiteSpace(aumid) ? null : aumid, hwnd));
            }
            return true;
        }, IntPtr.Zero);

        return results;
    }

    // Brings a window without a usable AUMID to the foreground directly,
    // as a fallback to AppLauncher.TryActivate.
    public static void Activate(IntPtr hwnd)
    {
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch { /* best-effort */ }
    }

    private static string? TryGetAppUserModelId(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreForWindow(hwnd, ref iid, out var propertyStore);
            store = propertyStore;
            var key = PKEY_AppUserModel_ID;
            store.GetValue(ref key, out var value);
            try
            {
                return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointerValue) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store != null && Marshal.IsComObject(store)) Marshal.FinalReleaseComObject(store);
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const ushort VT_LPWSTR = 31;
    private const int SW_RESTORE = 9;

    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PropVariant pv);
        int SetValue(ref PROPERTYKEY key, ref PropVariant pv);
        int Commit();
    }
}

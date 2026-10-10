using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CloCloWidget;

// Keeps a window out of screenshots, screen recordings and screen sharing
// (Teams, Zoom, OBS, Snipping Tool…) while it stays fully visible on the
// local display. The widget shows notification text, highlighted
// selections and clipboard history — exactly what shouldn't end up in a
// shared screen or a recording by accident.
public static class ScreenCapture
{
    private const uint WDA_NONE = 0x00;
    // Windows 10 2004+; on anything older the call just fails and the
    // window stays capturable, which is the same as before.
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public static void SetExcluded(Window window, bool excluded)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            return;
        }
        // Not shown yet — apply as soon as it has a handle.
        window.SourceInitialized += (_, _) =>
            SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }
}

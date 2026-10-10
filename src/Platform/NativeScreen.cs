using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using MediaColor = System.Windows.Media.Color;

namespace CloCloWidget;

// The few screen facts CloClo needs, straight from Windows (in physical
// pixels, like Windows Forms' Cursor and Screen gave them): where the
// cursor is, each monitor's working area, and the colour of a pixel.
public static class NativeScreen
{
    public static (int X, int Y) CursorPosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    // Each monitor's working area (without the taskbar).
    public static List<Int32Rect> WorkingAreas()
    {
        var areas = new List<Int32Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                areas.Add(new Int32Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top));
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    // The pixel at a screen point, copied the way Windows Forms'
    // CopyFromScreen did it (a plain BitBlt, which leaves out layered windows
    // such as the colour picker's own near-invisible overlay).
    public static MediaColor ColorAt(int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 1, 1);
        var old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, 1, 1, screen, x, y, 0x00CC0020); // SRCCOPY
            var c = GetPixel(memory, 0, 0);                         // 0x00BBGGRR
            return MediaColor.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}

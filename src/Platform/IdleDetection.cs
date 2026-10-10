using System;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// How long since any keyboard or mouse input anywhere on the system —
// Windows' own idle-time counter (the same one screen savers and lock
// timeouts use), not anything this app tracks itself. A plain, cheap,
// synchronous local query — no listener, no background thread.
public static class IdleDetection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan TimeSinceLastInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // Both are millisecond tick counts since boot — GetTickCount wraps
        // around every ~49.7 days, same as dwTime does, so the subtraction
        // still comes out right even across a wrap.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}

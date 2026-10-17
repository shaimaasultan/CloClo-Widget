using System;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// Battery percentage/charging state/remaining time, straight from Windows'
// own power status (the same info Explorer's own battery flyout shows) —
// entirely local and synchronous, no network call and nothing to poll
// externally.
public static class BatteryInfo
{
    // PluggedIn is the charger itself: Windows stops reporting Charging once
    // the battery is full (or held below full to protect it), so a laptop
    // on its charger at 100% is plugged in but not charging.
    public record Status(double Percent, bool Charging, string? TimeRemaining, bool PluggedIn);

    public static Status? Read()
    {
        if (!GetSystemPowerStatus(out var power)) return null;
        // 128: no system battery; 255: unknown status.
        if ((power.BatteryFlag & 128) != 0 || power.BatteryFlag == 255 || power.BatteryLifePercent > 100) return null;

        var charging = (power.BatteryFlag & 8) != 0;
        var pluggedIn = charging || power.ACLineStatus == 1;
        // Windows only exposes an estimate for time-until-empty, not
        // time-until-full, so charging simply shows no time at all rather
        // than a made-up number.
        var timeRemaining = !pluggedIn ? FormatTimeRemaining(power.BatteryLifeTime) : null;

        return new Status(power.BatteryLifePercent, charging, timeRemaining, pluggedIn);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static string? FormatTimeRemaining(int seconds)
    {
        if (seconds < 0) return null; // -1 means "Windows doesn't know yet"
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }
}

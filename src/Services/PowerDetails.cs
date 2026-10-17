using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Devices.Power;
using Windows.System.Power;

namespace CloCloWidget;

// The battery hover card: what Windows itself reports about power right
// now - the battery report (charge rate and capacities), PowerManager
// (charger, battery saver, time left) and the Power mode slider. Read
// when the card opens; every line Windows can't report is left out.
public static class PowerDetails
{
    public record Card(string Heading, List<string> Lines, bool NoBattery = false);

    public static Card Read()
    {
        BatteryReport? report = null;
        try { report = Battery.AggregateBattery.GetReport(); } catch { }
        var basic = SafeRead(BatteryInfo.Read);
        var mode = SafeRead(WindowsStatusNative.PowerMode);
        var supply = SafeRead(() => (PowerSupplyStatus?)PowerManager.PowerSupplyStatus);
        var saver = SafeRead(() => (EnergySaverStatus?)PowerManager.EnergySaverStatus);

        var lines = new List<string>();
        if (report == null || report.Status == BatteryStatus.NotPresent || basic == null)
        {
            // A desktop (or a battery Windows can't read): mains power only.
            if (mode != null) lines.Add($"Power mode: {mode}");
            if (saver == EnergySaverStatus.On) lines.Add("Energy saver on");
            if (report?.Status == BatteryStatus.NotPresent) lines.Add("No battery in this PC");
            return new Card("AC power", lines, NoBattery: report?.Status == BatteryStatus.NotPresent);
        }

        var percent = Math.Round(basic.Percent);
        var charging = report.Status == BatteryStatus.Charging || basic.Charging;
        var pluggedIn = charging || basic.PluggedIn;
        var state = charging ? "Charging" : pluggedIn ? "Plugged in, not charging" : "On battery";
        var heading = $"Battery {percent}% · {state}";

        var full = report.FullChargeCapacityInMilliwattHours;
        var remaining = report.RemainingCapacityInMilliwattHours;
        var rate = report.ChargeRateInMilliwatts;

        if (supply == PowerSupplyStatus.Inadequate) lines.Add("The charger is too weak to charge fully");
        if (charging)
        {
            // Windows gives no time-to-full, so it's worked out from the
            // capacity still to fill and the rate it's filling at.
            if (full is int f && remaining is int r && rate is int c && c > 0 && f > r)
                lines.Add($"About {WindowsStatusWindow.Duration(TimeSpan.FromHours((double)(f - r) / c))} to full");
        }
        else if (!pluggedIn)
        {
            var left = SafeRead(() => (TimeSpan?)PowerManager.RemainingDischargeTime);
            // Unknown while Windows is still estimating (zero or the maximum).
            if (left is { } t && t > TimeSpan.Zero && t < TimeSpan.FromDays(7))
                lines.Add($"About {WindowsStatusWindow.Duration(t)} left");
            else if (basic.TimeRemaining is { } text)
                lines.Add($"About {text} left");
        }
        if (rate is int mw && mw != 0)
            lines.Add(mw > 0 ? $"Charging at {Watts(mw)}" : $"Using {Watts(-mw)}");

        if (mode != null) lines.Add($"Power mode: {mode}");
        // "Disabled" is Windows' word for off while plugged in or turned off for good.
        if (saver != null) lines.Add(saver == EnergySaverStatus.On ? "Battery saver on" : "Battery saver off");

        var design = report.DesignCapacityInMilliwattHours;
        if (design is int d && d > 0 && full is int fc && fc > 0)
            lines.Add($"Battery health {Math.Round(100.0 * fc / d)}% ({WattHours(fc)} of {WattHours(d)} design)");

        return new Card(heading, lines);
    }

    // The card as one sentence-per-line string, for screen readers.
    public static string Spoken(Card card) => string.Join(". ", [card.Heading.Replace(" · ", ", "), .. card.Lines]) + ".";

    private static string Watts(int milliwatts) => (milliwatts / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " W";

    private static string WattHours(int milliwattHours) => (milliwattHours / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Wh";

    private static T? SafeRead<T>(Func<T?> read)
    {
        try { return read(); } catch { return default; }
    }
}

using System;
using System.Collections.Generic;

namespace CloCloWidget;

public readonly record struct PrayerOccurrence(string Name, DateTimeOffset StartsAtUtc);

// Local solar calculation. The selected convention changes only the Fajr and
// Isha twilight angles; Asr uses the selected shadow factor. No web request is
// made for the calculation.
public static class PrayerTimesCalculator
{
    public static IReadOnlyList<PrayerOccurrence> ForDate(
        DateTime date, double latitude, double longitude, string method, string asrMethod)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            return Array.Empty<PrayerOccurrence>();

        var utcDay = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var (fajrAngle, ishaAngle) = method switch
        {
            "mwl" => (18.0, 17.0),
            "egyptian" => (19.5, 17.5),
            _ => (15.0, 15.0), // ISNA
        };
        var noon = SolarNoon(utcDay, longitude);
        var sunrise = SolarEvent(utcDay, latitude, longitude, -0.833, morning: true);
        var sunset = SolarEvent(utcDay, latitude, longitude, -0.833, morning: false);
        if (sunrise is null || sunset is null) return Array.Empty<PrayerOccurrence>();

        var fajr = SolarEvent(utcDay, latitude, longitude, -fajrAngle, morning: true);
        var isha = SolarEvent(utcDay, latitude, longitude, -ishaAngle, morning: false);

        // A quarter of the night bounds twilight at high latitudes, where the
        // selected sun angle may never occur in summer. Polar days without a
        // sunrise/sunset remain unavailable rather than showing false times.
        var previousSunset = SolarEvent(utcDay.AddDays(-1), latitude, longitude, -0.833, morning: false);
        var nextSunrise = SolarEvent(utcDay.AddDays(1), latitude, longitude, -0.833, morning: true);
        if (previousSunset is { } previous)
        {
            var latestFajr = sunrise.Value - TimeSpan.FromTicks((sunrise.Value - previous).Ticks / 4);
            if (fajr is null || fajr < latestFajr) fajr = latestFajr;
        }
        if (nextSunrise is { } next)
        {
            var latestIsha = sunset.Value + TimeSpan.FromTicks((next - sunset.Value).Ticks / 4);
            if (isha is null || isha > latestIsha) isha = latestIsha;
        }

        var declination = SolarPosition(noon).Declination;
        var shadowFactor = asrMethod == "hanafi" ? 2.0 : 1.0;
        var asrAltitude = RadiansToDegrees(Math.Atan(1.0 /
            (shadowFactor + Math.Tan(DegreesToRadians(Math.Abs(latitude - declination))))));
        var asr = SolarEvent(utcDay, latitude, longitude, asrAltitude, morning: false);

        var result = new List<PrayerOccurrence>(5);
        Add(result, "Fajr", fajr);
        Add(result, "Dhuhr", noon);
        Add(result, "Asr", asr);
        Add(result, "Maghrib", sunset);
        Add(result, "Isha", isha);
        return result;
    }

    private static void Add(List<PrayerOccurrence> prayers, string name, DateTime? utc)
    {
        if (utc is { } time)
            prayers.Add(new PrayerOccurrence(name, new DateTimeOffset(time, TimeSpan.Zero)));
    }

    private static DateTime SolarNoon(DateTime utcDay, double longitude)
    {
        var minute = 720.0 - 4.0 * longitude;
        for (var i = 0; i < 3; i++)
            minute = 720.0 - 4.0 * longitude - SolarPosition(utcDay.AddMinutes(minute)).EquationOfTime;
        return utcDay.AddMinutes(minute);
    }

    private static DateTime? SolarEvent(DateTime utcDay, double latitude, double longitude,
        double altitude, bool morning)
    {
        var noon = SolarNoon(utcDay, longitude);
        var minute = (noon - utcDay).TotalMinutes;
        for (var i = 0; i < 3; i++)
        {
            var declination = SolarPosition(utcDay.AddMinutes(minute)).Declination;
            var cosine = (Math.Sin(DegreesToRadians(altitude)) -
                Math.Sin(DegreesToRadians(latitude)) * Math.Sin(DegreesToRadians(declination))) /
                (Math.Cos(DegreesToRadians(latitude)) * Math.Cos(DegreesToRadians(declination)));
            if (!double.IsFinite(cosine) || cosine < -1 || cosine > 1) return null;
            var offset = 4.0 * RadiansToDegrees(Math.Acos(cosine));
            minute = (noon - utcDay).TotalMinutes + (morning ? -offset : offset);
        }
        return utcDay.AddMinutes(minute);
    }

    private static (double Declination, double EquationOfTime) SolarPosition(DateTime utc)
    {
        var julianDay = utc.ToOADate() + 2415018.5;
        var centuries = (julianDay - 2451545.0) / 36525.0;
        var meanLongitude = Normalize(280.46646 + centuries * (36000.76983 + 0.0003032 * centuries));
        var meanAnomaly = Normalize(357.52911 + centuries * (35999.05029 - 0.0001537 * centuries));
        var eccentricity = 0.016708634 - centuries * (0.000042037 + 0.0000001267 * centuries);
        var anomaly = DegreesToRadians(meanAnomaly);
        var equationOfCenter = Math.Sin(anomaly) *
            (1.914602 - centuries * (0.004817 + 0.000014 * centuries)) +
            Math.Sin(2 * anomaly) * (0.019993 - 0.000101 * centuries) +
            Math.Sin(3 * anomaly) * 0.000289;
        var omega = 125.04 - 1934.136 * centuries;
        var longitude = meanLongitude + equationOfCenter - 0.00569 -
            0.00478 * Math.Sin(DegreesToRadians(omega));
        var meanObliquity = 23.0 + (26.0 +
            (21.448 - centuries * (46.815 + centuries * (0.00059 - 0.001813 * centuries))) / 60.0) / 60.0;
        var obliquity = meanObliquity + 0.00256 * Math.Cos(DegreesToRadians(omega));
        var declination = RadiansToDegrees(Math.Asin(
            Math.Sin(DegreesToRadians(obliquity)) * Math.Sin(DegreesToRadians(longitude))));
        var y = Math.Pow(Math.Tan(DegreesToRadians(obliquity) / 2.0), 2);
        var meanLongitudeRadians = DegreesToRadians(meanLongitude);
        var equationOfTime = 4.0 * RadiansToDegrees(
            y * Math.Sin(2 * meanLongitudeRadians) - 2 * eccentricity * Math.Sin(anomaly) +
            4 * eccentricity * y * Math.Sin(anomaly) * Math.Cos(2 * meanLongitudeRadians) -
            0.5 * y * y * Math.Sin(4 * meanLongitudeRadians) -
            1.25 * eccentricity * eccentricity * Math.Sin(2 * anomaly));
        return (declination, equationOfTime);
    }

    private static double Normalize(double degrees) => (degrees % 360 + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
}

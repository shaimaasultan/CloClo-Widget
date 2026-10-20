using System;

namespace CloCloWidget;

// Where the moon is in the sky, from the main terms of its orbit (good to
// a few tenths of a degree, ample for facing it), plus how lit it is.
public static class MoonPosition
{
    public readonly record struct Reading(double Azimuth, double Altitude, double Illumination, bool Waxing)
    {
        public string PhaseName => Illumination switch
        {
            < 0.03 => "new moon",
            < 0.40 => Waxing ? "waxing crescent" : "waning crescent",
            < 0.60 => Waxing ? "first-quarter half moon" : "last-quarter half moon",
            < 0.97 => Waxing ? "waxing gibbous" : "waning gibbous",
            _ => "full moon",
        };
    }

    public static Reading At(DateTime utc, double latitude, double longitude)
    {
        var d = utc.ToOADate() + 2415018.5 - 2451545.0;
        var meanLongitude = 218.316 + 13.176396 * d;
        var anomaly = Rad(134.963 + 13.064993 * d);
        var latitudeArgument = Rad(93.272 + 13.229350 * d);
        var elongation = Rad(297.850 + 12.190749 * d);
        var sunAnomaly = Rad(357.529 + 0.98560028 * d);

        var lambda = Rad(meanLongitude + 6.289 * Math.Sin(anomaly)
            + 1.274 * Math.Sin(2 * elongation - anomaly)
            + 0.658 * Math.Sin(2 * elongation)
            + 0.214 * Math.Sin(2 * anomaly)
            - 0.186 * Math.Sin(sunAnomaly)
            - 0.114 * Math.Sin(2 * latitudeArgument));
        var beta = Rad(5.128 * Math.Sin(latitudeArgument)
            + 0.281 * Math.Sin(anomaly + latitudeArgument)
            + 0.278 * Math.Sin(anomaly - latitudeArgument)
            + 0.173 * Math.Sin(2 * elongation - latitudeArgument));
        var obliquity = Rad(23.439 - 0.0000004 * d);

        var rightAscension = Math.Atan2(
            Math.Sin(lambda) * Math.Cos(obliquity) - Math.Tan(beta) * Math.Sin(obliquity), Math.Cos(lambda));
        var declination = Math.Asin(Math.Sin(beta) * Math.Cos(obliquity) +
            Math.Cos(beta) * Math.Sin(obliquity) * Math.Sin(lambda));
        var siderealTime = Rad(280.46061837 + 360.98564736629 * d + longitude);
        var hourAngle = siderealTime - rightAscension;

        var lat = Rad(latitude);
        var altitude = Deg(Math.Asin(Math.Clamp(Math.Sin(lat) * Math.Sin(declination) +
            Math.Cos(lat) * Math.Cos(declination) * Math.Cos(hourAngle), -1, 1)));
        // Seen from the surface, not the Earth's centre, the moon sits up
        // to about a degree lower.
        altitude -= 0.95 * Math.Cos(Rad(altitude));
        var azimuth = QiblaDirection.Normalize(Deg(Math.Atan2(Math.Sin(hourAngle),
            Math.Cos(hourAngle) * Math.Sin(lat) - Math.Tan(declination) * Math.Cos(lat))) + 180.0);

        // The angle between moon and sun as seen from Earth sets the phase.
        var phaseAngle = QiblaDirection.Normalize(Deg(lambda) - SunLongitude(d));
        return new Reading(azimuth, altitude, (1 - Math.Cos(Rad(phaseAngle))) / 2, phaseAngle < 180);
    }

    private static double SunLongitude(double d)
    {
        var anomaly = Rad(357.529 + 0.98560028 * d);
        return 280.459 + 0.98564736 * d + 1.915 * Math.Sin(anomaly) + 0.020 * Math.Sin(2 * anomaly);
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;
    private static double Deg(double radians) => radians * 180.0 / Math.PI;
}

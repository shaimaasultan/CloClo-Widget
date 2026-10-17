using System;

namespace CloCloWidget;

// The Qibla: the initial great-circle bearing from a place to the Kaaba,
// in degrees clockwise from true north. Works offline from the saved
// location the prayer times already use.
public static class QiblaDirection
{
    public const double KaabaLatitude = 21.4225;
    public const double KaabaLongitude = 39.8262;

    private static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private static readonly string[] CardinalWords =
        { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
    private static readonly string[] ArabicCardinals =
        { "الشمال", "الشمال الشرقي", "الشرق", "الجنوب الشرقي", "الجنوب", "الجنوب الغربي", "الغرب", "الشمال الغربي" };

    // Null for an invalid location, or at the Kaaba itself where every
    // direction is the Qibla.
    public static double? BearingFrom(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) return null;
        var lat = ToRadians(latitude);
        var kaabaLat = ToRadians(KaabaLatitude);
        var deltaLon = ToRadians(KaabaLongitude - longitude);
        var y = Math.Sin(deltaLon) * Math.Cos(kaabaLat);
        var x = Math.Cos(lat) * Math.Sin(kaabaLat) - Math.Sin(lat) * Math.Cos(kaabaLat) * Math.Cos(deltaLon);
        if (Math.Abs(x) < 1e-12 && Math.Abs(y) < 1e-12) return null;
        return Normalize(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    // The rhumb line: the one compass heading that, held all the way,
    // reaches the Kaaba. It is the straight line on a flat (Mercator) map,
    // and longer than the great circle. Null where it is undefined: an
    // invalid location, the poles, or the Kaaba itself.
    public static double? FlatMapBearingFrom(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            Math.Abs(latitude) >= 89.99 || Math.Abs(longitude) > 180) return null;
        var deltaLon = KaabaLongitude - longitude;
        if (deltaLon > 180) deltaLon -= 360;
        else if (deltaLon < -180) deltaLon += 360;
        var deltaPsi = Math.Log(Math.Tan(Math.PI / 4 + ToRadians(KaabaLatitude) / 2) /
            Math.Tan(Math.PI / 4 + ToRadians(latitude) / 2));
        if (Math.Abs(deltaPsi) < 1e-12 && Math.Abs(deltaLon) < 1e-9) return null;
        return Normalize(Math.Atan2(ToRadians(deltaLon), deltaPsi) * 180.0 / Math.PI);
    }

    public static double Normalize(double degrees)
    {
        var d = degrees % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    private static int CardinalIndex(double bearing) => (int)Math.Round(Normalize(bearing) / 45.0) % 8;

    // "Qibla 136° SE"
    public static string ShortText(double bearing) =>
        $"Qibla {Math.Round(Normalize(bearing)) % 360:0}° {Cardinals[CardinalIndex(bearing)]}";

    // Read by screen readers and spoken aloud: no symbols that a voice skips.
    public static string SpokenText(double bearing) =>
        $"The Qibla is {Math.Round(Normalize(bearing)) % 360:0} degrees from north, toward the {CardinalWords[CardinalIndex(bearing)]}.";

    public static string SpokenArabicText(double bearing) =>
        $"القبلة على بعد {Math.Round(Normalize(bearing)) % 360:0} درجة من الشمال، ناحية {ArabicCardinals[CardinalIndex(bearing)]}.";

    public static string ShortFlatMapText(double bearing) =>
        $"{Math.Round(Normalize(bearing)) % 360:0}° {Cardinals[CardinalIndex(bearing)]}";

    public static string SpokenFlatMapText(double bearing) =>
        $"On a flat map, the straight line to the Kaaba is {Math.Round(Normalize(bearing)) % 360:0} degrees from north, toward the {CardinalWords[CardinalIndex(bearing)]}.";

    public static string SpokenFlatMapArabicText(double bearing) =>
        $"على الخريطة المسطحة، الخط المستقيم للكعبة {Math.Round(Normalize(bearing)) % 360:0} درجة من الشمال، ناحية {ArabicCardinals[CardinalIndex(bearing)]}.";

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}

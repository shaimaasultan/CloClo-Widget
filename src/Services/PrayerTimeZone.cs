using System;
using System.Linq;

namespace CloCloWidget;

public static class PrayerTimeZone
{
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 100 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+');

    public static bool TryResolve(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Local;
        if (!IsValidId(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id!);
            return true;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id!, out var windowsId))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                return true;
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return false;
    }

    public static TimeZoneInfo Resolve(string? id) =>
        TryResolve(id, out var zone) ? zone : TimeZoneInfo.Local;

    public static DateTime Today(string? id) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Resolve(id)).Date;
}

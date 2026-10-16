using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CloCloWidget;

public readonly record struct WeatherReading(int Temperature, string Sky, bool IsDay, string? TimeZoneId);

// The current conditions from Open-Meteo (no API key, no account). Fetched
// here on the host rather than from the rendering layer, so nothing that
// draws Keeper ever needs network access of its own — the only outbound
// call the widget makes for weather is this one, with a fixed host and
// numbers it formats itself.
public static class WeatherService
{
    // Wind only gets its own visual when it's the most notable thing
    // happening — under a plain or overcast sky. Rain/snow/storm/fog already
    // read as "bad weather" on their own.
    private const double WindyThresholdKmh = 28;

    public static async Task<WeatherReading?> GetCurrentAsync(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
            || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
        {
            return null;
        }

        // Two decimals is ~1 km — plenty for a weather lookup, and it means
        // a GPS fix from "Use my location" never leaves the machine at full
        // precision.
        var lat = WidgetSettings.RoundCoordinate(latitude).ToString(CultureInfo.InvariantCulture);
        var lon = WidgetSettings.RoundCoordinate(longitude).ToString(CultureInfo.InvariantCulture);
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,weather_code,wind_speed_10m,cloud_cover,is_day&timezone=auto";

        try
        {
            using var doc = JsonDocument.Parse(await NetworkClient.Shared.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            var temp = (int)Math.Round(current.GetProperty("temperature_2m").GetDouble());
            var code = current.GetProperty("weather_code").GetInt32();
            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            var cloudCover = current.GetProperty("cloud_cover").GetDouble();
            var isDay = current.GetProperty("is_day").GetInt32() != 0;

            var sky = CodeToSky(code, cloudCover);
            if (sky is "clear" or "cloudy" or "mostlycloudy" or "overcast" && wind >= WindyThresholdKmh) sky = "windy";
            var timeZoneId = doc.RootElement.TryGetProperty("timezone", out var zone)
                ? zone.GetString() : null;
            return new WeatherReading(temp, sky, isDay, timeZoneId);
        }
        catch
        {
            return null;
        }
    }

    // Cloud cover is a continuous 0-100% reading, banded into four visual
    // steps — the weather code's own cloud buckets (0/1/2/3) are too coarse
    // to tell "partly cloudy" from "overcast but not raining" apart. The
    // weather code still picks precipitation/fog, since cloud cover alone
    // can't tell "cloudy" from "cloudy and also raining".
    private static string CloudCoverToSky(double cloudCover) => cloudCover switch
    {
        < 20 => "clear",
        < 45 => "cloudy", // partly cloudy
        < 75 => "mostlycloudy",
        _ => "overcast",
    };

    private static string CodeToSky(int code, double cloudCover)
    {
        if (new[] { 51, 53, 55, 56, 57 }.Contains(code)) return "drizzle";
        if (new[] { 61, 63, 65, 66, 67, 80, 81, 82 }.Contains(code)) return "rain";
        if (new[] { 71, 73, 75, 77, 85, 86 }.Contains(code)) return "snow";
        if (new[] { 95, 96, 99 }.Contains(code)) return "storm";
        if (new[] { 45, 48 }.Contains(code)) return "fog";
        return CloudCoverToSky(cloudCover);
    }
}

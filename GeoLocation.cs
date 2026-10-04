using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace CloCloWidget;

// Location without typing a city name — Windows' own Geolocation API (the
// same one Maps/Weather use), gated by the system's own permission under
// Settings > Privacy > Location > "Let desktop apps access your location".
// Returns null on denial/failure so the caller can show a plain message
// instead of crashing.
public static class GeoLocation
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    static GeoLocation()
    {
        // Nominatim's usage policy requires a real User-Agent or requests
        // get silently rejected — same "free, no API key" spirit as the
        // open-meteo forward search LocationDialog already uses.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CloCloWidget/1.0 (desktop weather widget)");
    }

    public static async Task<(double Lat, double Lon)?> GetCurrentAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return null;

            var geolocator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var pos = await geolocator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
            return (pos.Coordinate.Point.Position.Latitude, pos.Coordinate.Point.Position.Longitude);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> ReverseGeocodeAsync(double lat, double lon)
    {
        try
        {
            var url = "https://nominatim.openstreetmap.org/reverse?format=json" +
                // City-level lookup (zoom=10), so a ~1 km point is all it
                // needs — the precise GPS fix never leaves the machine.
                $"&lat={WidgetSettings.RoundCoordinate(lat).ToString(CultureInfo.InvariantCulture)}" +
                $"&lon={WidgetSettings.RoundCoordinate(lon).ToString(CultureInfo.InvariantCulture)}&zoom=10";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("address", out var address)) return null;

            foreach (var key in new[] { "city", "town", "village", "hamlet", "municipality", "county" })
            {
                if (address.TryGetProperty(key, out var v))
                {
                    var name = v.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

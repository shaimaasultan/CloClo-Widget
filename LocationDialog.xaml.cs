using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CloCloWidget;

public partial class LocationDialog : Window
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    public string ResolvedCity { get; private set; } = "";
    public double ResolvedLat { get; private set; }
    public double ResolvedLon { get; private set; }

    public LocationDialog(string currentCity)
    {
        InitializeComponent();
        CityBox.Text = currentCity;
        Loaded += (_, _) =>
        {
            CityBox.Focus();
            CityBox.SelectAll();
        };
    }

    private void CityBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = ResolveAndCloseAsync();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => _ = ResolveAndCloseAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void UseLocation_Click(object sender, RoutedEventArgs e)
    {
        UseLocationButton.IsEnabled = false;
        OkButton.IsEnabled = false;
        StatusText.Text = "Finding your location…";
        try
        {
            var pos = await GeoLocation.GetCurrentAsync();
            if (pos == null)
            {
                StatusText.Text = "Couldn't get your location — check Windows' location permission " +
                    "(Settings > Privacy & security > Location > Let desktop apps access your location).";
                return;
            }

            var (lat, lon) = pos.Value;
            StatusText.Text = "Found you — looking up the place name…";
            var city = await GeoLocation.ReverseGeocodeAsync(lat, lon)
                ?? $"{lat.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, {lon.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";

            ResolvedLat = WidgetSettings.RoundCoordinate(lat);
            ResolvedLon = WidgetSettings.RoundCoordinate(lon);
            ResolvedCity = city;
            DialogResult = true;
            Close();
        }
        finally
        {
            UseLocationButton.IsEnabled = true;
            OkButton.IsEnabled = true;
        }
    }

    private async Task ResolveAndCloseAsync()
    {
        var name = CityBox.Text.Trim();
        if (name.Length == 0) return;

        OkButton.IsEnabled = false;
        StatusText.Text = "Looking that up…";
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(name)}";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                ResolvedLat = WidgetSettings.RoundCoordinate(first.GetProperty("latitude").GetDouble());
                ResolvedLon = WidgetSettings.RoundCoordinate(first.GetProperty("longitude").GetDouble());
                ResolvedCity = first.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "Couldn't find that place — try a different spelling.";
                OkButton.IsEnabled = true;
            }
        }
        catch
        {
            StatusText.Text = "Couldn't reach the geocoding service — check your connection.";
            OkButton.IsEnabled = true;
        }
    }
}

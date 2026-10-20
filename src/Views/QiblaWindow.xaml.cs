using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Devices.Sensors;

namespace CloCloWidget;

public partial class QiblaWindow : Window
{
    private const double RoseSize = 200;
    private const string DifferenceNote =
        "The two directions differ because a flat map stretches the Earth; ask your local mosque which one it follows.";
    private readonly WidgetSettings _settings;
    private readonly double? _bearing;
    private readonly double? _flatBearing;
    private readonly bool _arabic;
    private readonly List<RotateTransform> _roseRotations = new();
    // Sky guides drawn on both compasses: (turn to its azimuth, the marker).
    private readonly List<(RotateTransform Rotation, UIElement Marker)> _sunMarkers = new();
    private readonly List<(RotateTransform Rotation, UIElement Marker)> _moonMarkers = new();
    private readonly List<(RotateTransform Rotation, UIElement Marker)> _starMarkers = new();
    private readonly DispatcherTimer _sunTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _loading = true;
    private Compass? _compass;
    private DateTime _lastLiveAnnouncement;

    public QiblaWindow(WidgetSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _bearing = QiblaDirection.BearingFrom(settings.Latitude, settings.Longitude);
        _flatBearing = QiblaDirection.FlatMapBearingFrom(settings.Latitude, settings.Longitude);
        _arabic = settings.VoiceCommandsInArabic;
        LocationText.Text = $"{settings.City}  ({settings.Latitude:F2}, {settings.Longitude:F2})";

        GlobeCompassHost.Content = BuildCompass(_bearing,
            _bearing is { } b ? "Globe compass. " + QiblaDirection.SpokenText(b) : "Globe compass: Qibla unavailable");
        FlatCompassHost.Content = BuildCompass(_flatBearing,
            _flatBearing is { } f ? "Flat map compass. " + QiblaDirection.SpokenFlatMapText(f) : "Flat map compass: direction unavailable");
        FlatBearingText.Text = _flatBearing is { } flat ? QiblaDirection.ShortFlatMapText(flat) : "Unavailable";
        if (_flatBearing is { } flatSpoken) AutomationProperties.SetName(FlatBearingText, QiblaDirection.SpokenFlatMapText(flatSpoken));

        if (_bearing is not { } bearing)
        {
            BearingText.Text = "Qibla unavailable";
            HintText.Text = "The saved location is at the Kaaba or is not valid. Set your location from the widget menu.";
            SensorText.Text = "";
            ReadAloudButton.IsEnabled = false;
            MatchScreenBox.IsEnabled = SunGuideBox.IsEnabled = false;
            ScreenHeadingPanel.IsEnabled = false;
            return;
        }

        BearingText.Text = QiblaDirection.ShortText(bearing);
        AutomationProperties.SetName(BearingText, QiblaDirection.SpokenText(bearing));
        if (_arabic)
        {
            ArabicText.Text = QiblaDirection.SpokenArabicText(bearing) +
                (_flatBearing is { } flatArabic ? Environment.NewLine + QiblaDirection.SpokenFlatMapArabicText(flatArabic) : "");
            ArabicText.Visibility = Visibility.Visible;
        }
        SensorText.Text = "No compass sensor on this PC, so the compasses don't turn with it by themselves.";
        MatchScreenBox.IsChecked = settings.QiblaMatchScreen;
        ScreenHeadingSlider.Value = settings.QiblaScreenHeading;
        SunGuideBox.IsChecked = settings.QiblaSunGuide;
        ShowScreenHeading();
        _loading = false;
        _sunTimer.Tick += (_, _) => UpdateSky();
        StartCompass();
        ApplyOptions();
        Closed += (_, _) =>
        {
            StopCompass();
            _sunTimer.Stop();
            SaveOptions();
        };
    }

    private bool MatchScreen => MatchScreenBox.IsChecked == true;
    private bool SunGuide => SunGuideBox.IsChecked == true;
    private double ScreenHeading => Math.Round(ScreenHeadingSlider.Value) % 360;

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) ApplyOptions();
    }

    private void ScreenHeading_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        ShowScreenHeading();
        ApplyOptions();
    }

    private void ShowScreenHeading()
    {
        var text = $"{ScreenHeading:0}° {QiblaDirection.CardinalOf(ScreenHeading)}";
        ScreenHeadingValue.Text = text;
        AutomationProperties.SetHelpText(ScreenHeadingSlider, text);
    }

    private void SaveOptions()
    {
        if (_bearing == null) return;
        if (_settings.QiblaMatchScreen == MatchScreen && _settings.QiblaScreenHeading == ScreenHeading &&
            _settings.QiblaSunGuide == SunGuide) return;
        _settings.QiblaMatchScreen = MatchScreen;
        _settings.QiblaScreenHeading = ScreenHeading;
        _settings.QiblaSunGuide = SunGuide;
        _settings.Save();
    }

    // Without a sensor the roses are turned by the saved screen direction
    // (or not at all), so straight up on screen is straight ahead in the room.
    private void ApplyOptions()
    {
        if (_bearing is not { } bearing) return;
        ScreenHeadingPanel.IsEnabled = MatchScreen && _compass == null;
        if (_compass == null)
        {
            var heading = MatchScreen ? ScreenHeading : 0;
            foreach (var rotation in _roseRotations) rotation.Angle = -heading;
            HintText.Text = MatchScreen
                ? "The compasses are turned to match your screen: the top of each compass is the way you face, " +
                  $"{heading:0}° {QiblaDirection.CardinalOf(heading)}. {TurnText(bearing - heading)} {DifferenceNote}"
                : "North is at the top. Find north with a phone compass or a known landmark, " +
                  $"then turn {Math.Round(bearing) % 360:0} degrees clockwise from it and face that way to pray. {DifferenceNote}";
        }
        UpdateSky();
    }

    // A turn in degrees, clockwise positive, as an instruction.
    private static string TurnText(double turn)
    {
        turn = QiblaDirection.Normalize(turn);
        if (turn > 180) turn -= 360;
        var rounded = Math.Round(turn);
        return Math.Abs(rounded) <= 5 ? "You are facing the Qibla."
            : rounded > 0 ? $"Turn {rounded:0} degrees to your right to face the Qibla."
            : $"Turn {-rounded:0} degrees to your left to face the Qibla.";
    }

    // Day: the sun. Night: the moon when it is up, and the North Star, which
    // stays within a degree of true north all night (northern hemisphere).
    private void UpdateSky()
    {
        if (!SunGuide || _bearing is not { } bearing)
        {
            _sunTimer.Stop();
            ShowMarkers(_sunMarkers, null);
            ShowMarkers(_moonMarkers, null);
            ShowMarkers(_starMarkers, null);
            SunText.Text = "";
            SunText.Visibility = Visibility.Collapsed;
            return;
        }
        _sunTimer.Start();
        SunText.Visibility = Visibility.Visible;
        var now = DateTime.UtcNow;
        var latitude = _settings.Latitude;
        var (sunAzimuth, sunAltitude) = PrayerTimesCalculator.SunPosition(now, latitude, _settings.Longitude);
        var moon = MoonPosition.At(now, latitude, _settings.Longitude);
        var lines = new List<string>();

        if (sunAltitude > 0)
        {
            ShowMarkers(_sunMarkers, sunAzimuth);
            ShowMarkers(_moonMarkers, null);
            ShowMarkers(_starMarkers, null);
            var where = $"The sun is at {Place(sunAzimuth, sunAltitude)} (the small sun on the compasses).";
            lines.Add(sunAltitude > 75
                ? where + " It is almost overhead, so it is not a good guide right now."
                : where + " " + FaceThen("the sun", bearing - sunAzimuth));
        }
        else
        {
            ShowMarkers(_sunMarkers, null);
            var moonUp = moon.Altitude > 2;
            ShowMarkers(_moonMarkers, moonUp ? moon.Azimuth : null);
            if (moonUp)
                lines.Add($"The moon ({moon.PhaseName}) is at {Place(moon.Azimuth, moon.Altitude)}, the grey circle. " +
                    FaceThen("the moon", bearing - moon.Azimuth));
            // Stars show once the sky is properly dark (sun 6° or more below).
            var dark = sunAltitude < -6;
            var starUp = dark && latitude >= 5;
            ShowMarkers(_starMarkers, starUp ? 0 : null);
            if (starUp)
                lines.Add($"The North Star is due north, {Math.Round(latitude):0}° above the horizon, the star on the compasses. " +
                    "Find the Big Dipper and follow its two end stars past the cup. " + FaceThen("the North Star", bearing));
            else if (dark)
                lines.Add("From here the North Star is too low to see. In the southern sky, the long bar of the " +
                    "Southern Cross points toward due south. " + FaceThen("due south", bearing - 180));
            else if (!moonUp)
                lines.Add("The sun has just set. The North Star shows once the sky is darker; this updates every minute.");
            if (!moonUp && (dark || latitude < 5))
                lines.Add(moon.Altitude > -2 ? "The moon is on the horizon." : "The moon is below the horizon now.");
        }
        SunText.Text = string.Join(Environment.NewLine, lines);
        AutomationProperties.SetName(SunText, string.Join(" ", lines).Replace("°", " degrees"));
    }

    private static string Place(double azimuth, double altitude) =>
        $"{Math.Round(azimuth) % 360:0}° {QiblaDirection.CardinalOf(azimuth)}, {Math.Round(altitude):0}° above the horizon";

    // "Face the moon, then turn 66 degrees to your left to face the Qibla."
    private static string FaceThen(string guide, double turn) =>
        $"Face {guide}, then t" + TurnText(turn)[1..];

    private static void ShowMarkers(List<(RotateTransform Rotation, UIElement Marker)> markers, double? azimuth)
    {
        foreach (var (rotation, marker) in markers)
        {
            marker.Visibility = azimuth is null ? Visibility.Collapsed : Visibility.Visible;
            if (azimuth is { } angle) rotation.Angle = angle;
        }
    }

    // Most PCs have no magnetometer: Compass.GetDefault() is then null. A
    // PC that has one reports a fused heading here; Windows' absolute
    // OrientationSensor needs the same magnetometer, so it adds nothing.
    private void StartCompass()
    {
        try { _compass = Compass.GetDefault(); }
        catch { _compass = null; }
        if (_compass == null) return;
        _compass.ReportInterval = Math.Max(_compass.MinimumReportInterval, 100u);
        _compass.ReadingChanged += Compass_ReadingChanged;
        SensorText.Text = "Compass sensor found. Hold the PC flat; the compasses turn with it.";
        MatchScreenBox.IsEnabled = false;
        HintText.Text = DifferenceNote;
        if (_compass.GetCurrentReading() is { } reading) ShowHeading(reading);
    }

    private void StopCompass()
    {
        if (_compass == null) return;
        _compass.ReadingChanged -= Compass_ReadingChanged;
        _compass = null;
    }

    private void Compass_ReadingChanged(Compass sender, CompassReadingChangedEventArgs args) =>
        Dispatcher.BeginInvoke(new Action(() => { if (_compass != null) ShowHeading(args.Reading); }));

    private void ShowHeading(CompassReading reading)
    {
        if (_bearing is not { } bearing) return;
        var heading = reading.HeadingTrueNorth ?? reading.HeadingMagneticNorth;
        if (!double.IsFinite(heading)) return;
        // Each rose turns so its N points at real north, carrying its needle.
        foreach (var rotation in _roseRotations) rotation.Angle = -heading;
        var guidance = TurnText(bearing - heading);
        SensorText.Text = $"Facing {Math.Round(heading) % 360:0}°. {guidance} (globe direction)";
        // Screen readers hear the live guidance at most once a second.
        if (DateTime.UtcNow - _lastLiveAnnouncement < TimeSpan.FromSeconds(1)) return;
        _lastLiveAnnouncement = DateTime.UtcNow;
        AutomationProperties.SetName(SensorText, $"Facing {Math.Round(heading) % 360:0} degrees. {guidance}");
    }

    private QiblaCompassArea BuildCompass(double? bearing, string accessibleName)
    {
        var center = RoseSize / 2;
        var text = (Brush)FindResource(SystemColors.WindowTextBrushKey);
        var area = new QiblaCompassArea { Width = RoseSize, Height = RoseSize, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(area, accessibleName);

        var roseRotation = new RotateTransform();
        _roseRotations.Add(roseRotation);
        var rose = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = roseRotation };
        area.Children.Add(rose);
        rose.Children.Add(new Ellipse { Stroke = text, StrokeThickness = 2 });

        var ticks = new Canvas { Width = RoseSize, Height = RoseSize };
        rose.Children.Add(ticks);
        for (var degrees = 0; degrees < 360; degrees += 15)
        {
            var major = degrees % 90 == 0;
            var radians = degrees * Math.PI / 180;
            var outer = center - 3;
            var inner = outer - (major ? 13 : degrees % 45 == 0 ? 9 : 5);
            ticks.Children.Add(new Line
            {
                X1 = center + outer * Math.Sin(radians), Y1 = center - outer * Math.Cos(radians),
                X2 = center + inner * Math.Sin(radians), Y2 = center - inner * Math.Cos(radians),
                Stroke = text, StrokeThickness = major ? 2 : 1,
            });
        }
        string[] letters = { "N", "E", "S", "W" };
        for (var i = 0; i < 4; i++)
        {
            var radians = i * Math.PI / 2;
            var label = new TextBlock
            {
                Text = letters[i], FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 14, Width = 20, TextAlignment = TextAlignment.Center, Foreground = text,
            };
            var radius = center - 27;
            Canvas.SetLeft(label, center + radius * Math.Sin(radians) - 10);
            Canvas.SetTop(label, center - radius * Math.Cos(radians) - 10);
            ticks.Children.Add(label);
        }

        // Sky guides, inside the rim at their azimuths. The North Star sits
        // further in so it stays clear of the N.
        var sunDisc = new Ellipse { Width = 16, Height = 16, Fill = Brushes.Gold, Stroke = text, StrokeThickness = 1.5 };
        _sunMarkers.Add(AddSkyMarker(rose, sunDisc, center, 34));
        var moonDisc = new Ellipse { Width = 15, Height = 15, Fill = Brushes.Gainsboro, Stroke = text, StrokeThickness = 1.5 };
        _moonMarkers.Add(AddSkyMarker(rose, moonDisc, center, 34));
        var star = new Polygon { Fill = Brushes.Gold, Stroke = text, StrokeThickness = 1, Width = 18, Height = 18 };
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? 9.0 : 3.8;
            var pointAngle = i * Math.PI / 5;
            star.Points.Add(new Point(9 + radius * Math.Sin(pointAngle), 9 - radius * Math.Cos(pointAngle)));
        }
        _starMarkers.Add(AddSkyMarker(rose, star, center, 46));

        if (bearing is { } angle)
        {
            // Drawn pointing up, then turned to the bearing; the square at the
            // tip stands for the Kaaba.
            var needle = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle) };
            needle.Children.Add(new Polygon
            {
                Points = new PointCollection { new(center, 28), new(center - 8, center), new(center + 8, center) },
                Fill = (Brush)FindResource(SystemColors.HighlightBrushKey),
            });
            needle.Children.Add(new Polygon
            {
                Points = new PointCollection { new(center - 8, center), new(center + 8, center), new(center, center + 30) },
                Fill = (Brush)FindResource(SystemColors.GrayTextBrushKey), Opacity = 0.6,
            });
            needle.Children.Add(new Rectangle
            {
                Width = 13, Height = 13, Fill = text,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(center - 6.5, 11, 0, 0),
            });
            rose.Children.Add(needle);
        }
        rose.Children.Add(new Ellipse { Width = 11, Height = 11, Fill = text });
        return area;
    }

    private static (RotateTransform, UIElement) AddSkyMarker(Grid rose, FrameworkElement shape, double center, double top)
    {
        shape.HorizontalAlignment = HorizontalAlignment.Left;
        shape.VerticalAlignment = VerticalAlignment.Top;
        shape.Margin = new Thickness(center - shape.Width / 2, top, 0, 0);
        var rotation = new RotateTransform();
        var holder = new Grid
        {
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotation,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        holder.Children.Add(shape);
        rose.Children.Add(holder);
        return (rotation, holder);
    }

    private void ReadAloud_Click(object sender, RoutedEventArgs e)
    {
        if (_bearing is not { } bearing) return;
        var arabic = _arabic && SpeechReader.IsVoiceAvailable(ReadAloudVoice.ArabicHoda);
        var message = arabic ? QiblaDirection.SpokenArabicText(bearing) : QiblaDirection.SpokenText(bearing);
        if (_flatBearing is { } flat)
            message += " " + (arabic ? QiblaDirection.SpokenFlatMapArabicText(flat) : QiblaDirection.SpokenFlatMapText(flat));
        SpeechReader.Speak(message, selectedVoice: arabic ? ReadAloudVoice.ArabicHoda : null);
    }
}

// Screen readers meet each compass as one named picture, not as its loose
// N/E/S/W letters.
public sealed class QiblaCompassArea : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(QiblaCompassArea owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(QiblaCompassArea);
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

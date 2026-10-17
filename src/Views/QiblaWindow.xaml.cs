using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Windows.Devices.Sensors;

namespace CloCloWidget;

public partial class QiblaWindow : Window
{
    private const double RoseSize = 200;
    private readonly double? _bearing;
    private readonly double? _flatBearing;
    private readonly bool _arabic;
    private readonly List<RotateTransform> _roseRotations = new();
    private Compass? _compass;
    private DateTime _lastLiveAnnouncement;

    public QiblaWindow(WidgetSettings settings)
    {
        InitializeComponent();
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
        HintText.Text = "North is at the top. Find north with a phone compass or a known landmark, " +
            $"then turn {Math.Round(bearing) % 360:0} degrees clockwise from it and face that way to pray. " +
            "The two directions differ because a flat map stretches the Earth; ask your local mosque which one it follows.";
        SensorText.Text = "No compass sensor on this PC, so the compasses don't turn with it.";
        StartCompass();
        Closed += (_, _) => StopCompass();
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
        var turn = QiblaDirection.Normalize(bearing - heading);
        if (turn > 180) turn -= 360;
        var rounded = Math.Round(turn);
        var guidance = Math.Abs(rounded) <= 5 ? "You are facing the Qibla."
            : rounded > 0 ? $"Turn {rounded:0} degrees right." : $"Turn {-rounded:0} degrees left.";
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

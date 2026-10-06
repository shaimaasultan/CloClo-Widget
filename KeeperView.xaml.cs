using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace CloCloWidget;

// Draws Keeper natively — the WPF port of what used to be Assets/widget.html
// running in a WebView2. The host calls the Set*/Perk methods below where it
// used to post JSON messages to the page; every CSS keyframe animation is a
// WPF animation here, with the same durations, delays and easing.
public partial class KeeperView : System.Windows.Controls.UserControl
{
    // CSS's own ease-in-out / ease-out curves, so motion feels the same.
    private static readonly KeySpline EaseInOut = new(0.42, 0, 0.58, 1);
    private static readonly KeySpline EaseOut = new(0, 0, 0.58, 1);
    private const double LineWidth = 97;

    private static readonly Dictionary<string, string> SkyLabels = new()
    {
        ["clear"] = "Clear", ["cloudy"] = "Partly Cloudy", ["mostlycloudy"] = "Mostly Cloudy", ["overcast"] = "Cloudy",
        ["windy"] = "Windy", ["fog"] = "Fog", ["drizzle"] = "Drizzle", ["rain"] = "Rain", ["snow"] = "Snow", ["storm"] = "Storm",
    };

    private readonly Random _random = new();
    private readonly Dictionary<string, FrameworkElement> _iconsByKey;
    // Looping animations per sky decoration, started only while that
    // decoration is actually showing.
    private readonly Dictionary<string, List<(IAnimatable Target, DependencyProperty Property, AnimationTimeline Animation)>> _loops = new();
    private readonly List<Rectangle> _drops = new();

    private string _skyOverride = "auto";
    private string? _liveSky;
    private string _sky = "clear";
    private bool _isNight;
    private string _activityState = "awake";
    private string _avatar = "keeper";
    // Set while a one-shot (perk, or a fidget that moves the whole body)
    // owns KeeperWrap's transform, so two never stomp each other.
    private bool _wrapBusy;
    private readonly DispatcherTimer _flashTimer = new();
    // The sky in words (null when there's none to show) and the clock share
    // the top line; the clock ticks on its own.
    private string? _skyText;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public KeeperView()
    {
        InitializeComponent();
        _iconsByKey = new()
        {
            ["youtube"] = IconYoutube, ["spotify"] = IconSpotify, ["bell"] = IconBell, ["search"] = IconSearch,
            ["gpu"] = IconGpu, ["read"] = IconRead, ["mic"] = IconMic, ["convert"] = IconConvert,
            ["color"] = IconColor, ["qr"] = IconQr, ["volume"] = IconVolume, ["clipboard"] = IconClipboard,
            ["stopwatch"] = IconStopwatch,
        };

        BuildSunRays();
        BuildPrecipitation();
        BuildLoops();

        _flashTimer.Tick += (_, _) => { FlashLightning(); ScheduleFlash(); };
        _clockTimer.Tick += (_, _) => UpdateTopLine();
        _clockTimer.Start();

        ApplySky();
        RestartAmbient();
        ScheduleBlink();
        ScheduleFidget();
    }

    // ---------------------------------------------------------------- host API

    public void SetPlace(string city) => Place.Text = city;

    // null hides the stopwatch line; otherwise shows that elapsed time.
    public void SetStopwatch(TimeSpan? elapsed)
    {
        StopwatchLabel.Visibility = Show(elapsed != null);
        if (elapsed is TimeSpan t) StopwatchTime.Text = " " + FormatElapsed(t);
        PlaceStopwatch();
    }

    // Top-left, in the battery's column: under its time-left line, under the
    // percentage when there's no time left to show, or in its place when
    // there's no battery - clear of the sky decorations on the right.
    private void PlaceStopwatch()
    {
        var top = Battery.Visibility != Visibility.Visible ? 15
            : BatteryTime.Visibility == Visibility.Visible ? 41
            : 30;
        Canvas.SetTop(StopwatchLabel, top);
    }

    // m:ss, or h:mm:ss from an hour on.
    public static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // "keeper" (the original) or "lady": the same figure, poses and weather
    // outfits, with long hair, lashes, rose lips and a skirt for "lady".
    public void SetAvatar(string avatar)
    {
        _avatar = avatar == "lady" ? "lady" : "keeper";
        var lady = _avatar == "lady";
        HairDefault.Visibility = Show(!lady);
        LadyHairBack.Visibility = LadyHairFront.Visibility = LadyLashes.Visibility = Show(lady);
        var mouth = (Brush)FindResource(lady ? "Lips" : "Hair");
        MouthAwake.Stroke = MouthSleep.Stroke = MouthWorkout.Fill = mouth;
        ApplyOutfit();
    }

    public void SetEnabledIcons(IEnumerable<string> keys)
    {
        var enabled = new HashSet<string>(keys);
        foreach (var (key, el) in _iconsByKey) el.Visibility = enabled.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
    }

    // "auto" follows the live forecast; anything else is a manual pick from
    // the right-click menu that wins over it. Until the sky is actually
    // known the label stays hidden — it must never claim "Clear" just
    // because nothing has arrived yet.
    public void SetSkyOverride(string sky)
    {
        _skyOverride = sky;
        var shown = sky != "auto" ? sky : _liveSky;
        _sky = shown ?? "clear";
        ShowSkyLabel(shown);
        ApplySky();
    }

    public void SetWeather(WeatherReading? reading)
    {
        if (reading is not { } w)
        {
            Temp.Text = "—°";
            return;
        }
        Temp.Text = $"{w.Temperature}°";
        _liveSky = w.Sky;
        // Day/night applies even under a manual sky, so a picked "Clear"
        // still shows the moon at night.
        _isNight = !w.IsDay;
        if (_skyOverride == "auto")
        {
            _sky = w.Sky;
            ShowSkyLabel(w.Sky);
        }
        ApplySky();
    }

    public void SetBattery(BatteryInfo.Status? status)
    {
        if (status is not { } s)
        {
            Battery.Visibility = Visibility.Collapsed;
            PlaceStopwatch();
            return;
        }
        BatteryPercent.Text = $"{Math.Round(s.Percent)}%";
        BatteryFill.Width = Math.Max(1, Math.Round(s.Percent / 100 * 11));
        BatteryFill.Fill = s.Charging ? (Brush)FindResource("Highlight")
            : s.Percent <= 15 ? new SolidColorBrush(Color.FromRgb(0xe2, 0x66, 0x5c))
            : (Brush)FindResource("Face");
        BatteryBolt.Visibility = s.Charging ? Visibility.Visible : Visibility.Collapsed;
        BatteryTime.Text = s.TimeRemaining is { Length: > 0 } t ? $"{t} left" : "";
        BatteryTime.Visibility = BatteryTime.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Battery.Visibility = Visibility.Visible;
        PlaceStopwatch();
    }

    // Null title hides the label. The icon is a notification's own app
    // logo (media never has one), shown in place of the note glyph.
    public void SetNowPlaying(string? title, string? subtitle, ImageSource? icon, bool isNotification)
    {
        if (string.IsNullOrEmpty(title))
        {
            NowPlaying.Visibility = Visibility.Collapsed;
            return;
        }

        NowPlayingTitle.Text = title;
        NowPlayingSubtitle.Text = subtitle ?? "";
        SubtitleLine.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        NowPlayingIcon.Source = icon;
        NowPlayingIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingNote.Visibility = icon != null ? Visibility.Collapsed : Visibility.Visible;
        // The note glyph is 5px wide, the icon 15px; the text column starts
        // 4px after whichever one is showing.
        var left = icon != null ? 28 : 18;
        var width = LineWidth - (left - 18);
        foreach (var line in new[] { TitleLine, SubtitleLine })
        {
            Canvas.SetLeft(line, left);
            line.Width = width;
        }

        ListButton.Visibility = isNotification ? Visibility.Visible : Visibility.Collapsed;
        NowPlaying.Visibility = Visibility.Visible;

        // Only scroll a line if it actually overflows — a short one sits still.
        FitLine(NowPlayingTitle, width);
        FitLine(NowPlayingSubtitle, width);
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // Decodes a notification's app logo. Never trusted for anything beyond
    // pixels: PNG only (what Windows app logos are) — WIC would otherwise
    // hand any other format to whichever image codec claims it, including
    // third-party ones — decoded at a fixed small size, and frozen.
    public static ImageSource? DecodeIcon(byte[]? bytes)
    {
        if (bytes is not { Length: > 8 } || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // "awake", "asleep" or "workout" — measured host-side (see
    // UpdateActivityState in MainWindow.xaml.cs). Purely cosmetic.
    public void SetActivity(string state, int secondsLeft)
    {
        if (state != _activityState)
        {
            _activityState = state;
            var sleeping = state == "asleep";
            var working = state == "workout";

            MouthAwake.Visibility = Show(!sleeping && !working);
            MouthSleep.Visibility = Show(sleeping);
            MouthWorkout.Visibility = Show(working);
            Zzz.Visibility = Show(sleeping);
            EyesOpen.Visibility = Show(!sleeping);
            EyesClosed.Visibility = Show(sleeping);
            HangingArms.Visibility = Show(!working);
            WorkoutArms.Visibility = Show(working);
            WorkoutCountdown.Visibility = Show(working);
            ApplyOutfit();

            SetLoops("zzz", sleeping);
            // Dims a touch once asleep — a resting cue that reads even out
            // of the corner of your eye.
            Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(sleeping ? 0.85 : 1, TimeSpan.FromSeconds(0.6))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
            RestartAmbient();
        }

        if (state == "workout")
        {
            WorkoutCountdown.Text = $"{secondsLeft / 60}:{secondsLeft % 60:00}";
        }
    }

    // A one-shot "noticed that" bounce the instant the host starts watching
    // something — ties Keeper's posture to the click.
    public void Perk()
    {
        if (_activityState != "awake" || _wrapBusy) return;
        RunWrapOneShot(0.5, EaseOut,
            (WrapTranslate, TranslateTransform.YProperty, new[] { (0.0, 0.0), (0.3, -6), (0.6, -2), (1, 0) }),
            (WrapScale, ScaleTransform.ScaleXProperty, new[] { (0.0, 1.0), (0.3, 1.04), (0.6, 1.01), (1, 1) }),
            (WrapScale, ScaleTransform.ScaleYProperty, new[] { (0.0, 1.0), (0.3, 1.04), (0.6, 1.01), (1, 1) }));
    }

    // ---------------------------------------------------------------- outfit

    // What Keeper wears: the weather's outfit while awake, gym clothes during
    // a workout break and night clothes while asleep - for either avatar.
    // The sky itself keeps showing the weather either way.
    private void ApplyOutfit()
    {
        var lady = _avatar == "lady";
        var gym = _activityState == "workout";
        var sleep = _activityState == "asleep";
        var weather = !gym && !sleep;

        foreach (var el in new UIElement[]
        {
            TorsoRain, TorsoSnowExtra, TorsoStorm, RaincoatHoodBack, RaincoatHood, SnowHat, Sunglasses, UmbrellaHeld, UmbrellaLeaning,
            GymTop, GymShorts, Headband, PajamaTop, Nightgown, Nightcap,
        })
        {
            el.Visibility = Visibility.Collapsed;
        }
        TorsoDefault.Visibility = Show(weather);
        LadySkirt.Visibility = Show(lady && weather);

        string arms, legs, shoes;
        if (gym)
        {
            GymTop.Visibility = Headband.Visibility = Visibility.Visible;
            GymTopBody.Fill = (Brush)FindResource(lady ? "GymTopLady" : "GymTop");
            GymShorts.Visibility = Show(!lady);
            (arms, legs, shoes) = ("Skin", lady ? "Leggings" : "Skin", "Sneakers");
        }
        else if (sleep)
        {
            Nightcap.Visibility = Visibility.Visible;
            NightcapBody.Fill = (Brush)FindResource(lady ? "Nightgown" : "Pajama");
            (lady ? Nightgown : PajamaTop).Visibility = Visibility.Visible;
            (arms, legs, shoes) = (lady ? "Nightgown" : "Pajama", lady ? "Skin" : "Pajama", "Slippers");
        }
        else
        {
            switch (_sky)
            {
                case "rain":
                    TorsoRain.Visibility = RaincoatHoodBack.Visibility = RaincoatHood.Visibility = UmbrellaHeld.Visibility = Visibility.Visible;
                    break;
                case "drizzle":
                    // Light enough that the hood alone is enough — no full
                    // raincoat, no umbrella, unlike proper rain.
                    RaincoatHoodBack.Visibility = RaincoatHood.Visibility = Visibility.Visible;
                    break;
                case "snow":
                    TorsoSnowExtra.Visibility = SnowHat.Visibility = Visibility.Visible;
                    break;
                case "storm":
                    TorsoStorm.Visibility = Visibility.Visible;
                    break;
                case "clear":
                    Sunglasses.Visibility = Visibility.Visible;
                    break;
                // cloudy/mostlycloudy/overcast/fog/windy: default torso, no sunglasses
            }
            arms = _sky switch { "rain" => "Raincoat", "storm" => "Body1", _ => "Sweater" };
            (legs, shoes) = ("Trousers", "Shoes");
        }
        // In the rain the umbrella waits, leaning beside them, during a break or a nap.
        UmbrellaLeaning.Visibility = Show(!weather && _sky == "rain");

        var armBrush = (Brush)FindResource(arms);
        ArmL.Stroke = ArmR.Stroke = WorkoutArmL.Stroke = WorkoutArmR.Stroke = armBrush;
        LegL.Fill = LegR.Fill = (Brush)FindResource(legs);
        ShoeL.Fill = ShoeR.Fill = (Brush)FindResource(shoes);
    }

    // ---------------------------------------------------------------- sky

    private void ShowSkyLabel(string? sky)
    {
        _skyText = sky != null && SkyLabels.TryGetValue(sky, out var l) ? l : null;
        UpdateTopLine();
    }

    // "Clear · 9:41 PM" - or just the time while there's no sky to name. The
    // time follows Windows' own short-time format (12- or 24-hour).
    private void UpdateTopLine()
    {
        var time = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        var text = _skyText != null ? $"{_skyText} · {time}" : time;
        if (SkyLabel.Text != text) SkyLabel.Text = text;
    }

    private void ApplySky()
    {
        var kind = _sky;
        ApplyOutfit();

        Sun.Visibility = Show(kind == "clear" && !_isNight);
        Moon.Visibility = Show(kind == "clear" && _isNight);
        Cloud.Visibility = Show(kind == "cloudy");
        CloudMostly.Visibility = Show(kind == "mostlycloudy");
        CloudOvercast.Visibility = Show(kind == "overcast");
        // The peeking sun turns a pale moon-white at night.
        var peek = _isNight ? new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf8)) : (Brush)FindResource("Highlight");
        PeekSun.Fill = peek;
        PeekSunGlow.Fill = peek;

        SetLoops("moon", kind == "clear" && _isNight);
        SetLoops("cloud", kind == "cloudy");
        SetLoops("cloudMostly", kind == "mostlycloudy");
        SetLoops("cloudOvercast", kind == "overcast");
        SetLoops("flakes", kind == "snow");
        SetLoops("leaves", kind == "windy");
        SetLoops("fog", kind == "fog");
        Fog.BeginAnimation(OpacityProperty, new DoubleAnimation(kind == "fog" ? 1 : 0, TimeSpan.FromSeconds(0.4)));
        ConfigureDrops(kind);

        if (kind == "storm") { if (!_flashTimer.IsEnabled) ScheduleFlash(); }
        else _flashTimer.Stop();

        RestartAmbient();
    }

    private void BuildSunRays()
    {
        var highlight = (Brush)FindResource("Highlight");
        for (var i = 0; i < 8; i++)
        {
            var ray = new Rectangle
            {
                Width = 2, Height = 6, RadiusX = 1, RadiusY = 1, Fill = highlight, Opacity = 0.85,
                RenderTransform = new RotateTransform(360.0 / 8 * i, 1, 13),
            };
            Canvas.SetLeft(ray, 12);
            Canvas.SetTop(ray, 12);
            SunRays.Children.Add(ray);
        }
    }

    // Same positions and timings as the old page's inline styles.
    private void BuildPrecipitation()
    {
        var dropBrush = new SolidColorBrush(Color.FromRgb(0xcf, 0xe3, 0xf7));
        foreach (var (left, duration, delay) in new[] { (8.0, 0.9, 0.0), (26, 1.1, 0.3), (112, 0.8, 0.15), (132, 1.0, 0.5) })
        {
            var drop = new Rectangle { Width = 2, Height = 12, RadiusX = 1, RadiusY = 1, Fill = dropBrush, Opacity = 0, RenderTransform = new TranslateTransform(), Tag = (duration, delay) };
            Canvas.SetLeft(drop, left);
            Canvas.SetTop(drop, -14);
            Precipitation.Children.Add(drop);
            _drops.Add(drop);
        }

        foreach (var (left, duration, delay) in new[] { (14.0, 5.0, 0.0), (40, 6.5, 1.2), (100, 5.8, 0.6), (126, 7.0, 2.0) })
        {
            var shift = new TranslateTransform();
            var flake = new Ellipse { Width = 4, Height = 4, Fill = Brushes.White, Opacity = 0.85, RenderTransform = shift, Visibility = Visibility.Collapsed };
            Canvas.SetLeft(flake, left);
            Canvas.SetTop(flake, -8);
            Precipitation.Children.Add(flake);
            AddLoop("flakes", flake, VisibilityProperty, null);
            AddLoop("flakes", shift, TranslateTransform.XProperty, Loop(Keys(duration, null, (0, 0), (0.5, 6), (1, -4)), delay));
            AddLoop("flakes", shift, TranslateTransform.YProperty, Loop(Keys(duration, null, (0, -8), (0.5, 100), (1, 214)), delay));
        }

        // A leaf: rounded on every corner but the bottom-left.
        var leafShape = Geometry.Parse("M0,2 A3,2 0 0 1 3,0 A3,2 0 0 1 6,2 A3,2 0 0 1 3,4 L0,4 Z");
        var leafBrush = new SolidColorBrush(Color.FromRgb(0xc9, 0x8a, 0x3b));
        foreach (var (top, duration, delay) in new[] { (60.0, 3.2, 0.0), (100, 2.6, 1.1), (140, 3.6, 0.4) })
        {
            var spin = new RotateTransform(0, 3, 2);
            var move = new TranslateTransform();
            var leaf = new Path { Data = leafShape, Fill = leafBrush, Opacity = 0, RenderTransform = new TransformGroup { Children = { spin, move } } };
            Canvas.SetLeft(leaf, -10);
            Canvas.SetTop(leaf, top);
            Precipitation.Children.Add(leaf);
            AddLoop("leaves", move, TranslateTransform.XProperty, Loop(Keys(duration, null, (0, 0), (1, 172)), delay));
            AddLoop("leaves", move, TranslateTransform.YProperty, Loop(Keys(duration, null, (0, 0), (1, 14)), delay));
            AddLoop("leaves", spin, RotateTransform.AngleProperty, Loop(Keys(duration, null, (0, 0), (1, 340)), delay));
            AddLoop("leaves", leaf, OpacityProperty, Loop(Keys(duration, null, (0, 0), (0.08, 0.9), (0.92, 0.9), (1, 0)), delay));
        }
    }

    private void BuildLoops()
    {
        AddLoop("moon", Star1, OpacityProperty, Loop(Keys(2.4, EaseInOut, (0, 0.25), (0.5, 1), (1, 0.25)), 0));
        AddLoop("moon", Star2, OpacityProperty, Loop(Keys(2.4, EaseInOut, (0, 0.25), (0.5, 1), (1, 0.25)), 1.1));
        AddLoop("cloud", (TranslateTransform)Cloud.RenderTransform, TranslateTransform.XProperty, Loop(Keys(6, EaseInOut, (0, 0), (0.5, -4), (1, 0)), 0));
        AddLoop("cloudMostly", (TranslateTransform)CloudMostly.RenderTransform, TranslateTransform.XProperty, Loop(Keys(7, EaseInOut, (0, 0), (0.5, -4), (1, 0)), 0));
        AddLoop("cloudOvercast", (TranslateTransform)CloudOvercast.RenderTransform, TranslateTransform.XProperty, Loop(Keys(8, EaseInOut, (0, 0), (0.5, -4), (1, 0)), 0));
        AddLoop("fog", (TranslateTransform)Wisp1.RenderTransform, TranslateTransform.XProperty, Loop(Keys(10, EaseInOut, (0, 0), (0.5, 16), (1, 0)), 0));
        AddLoop("fog", (TranslateTransform)Wisp2.RenderTransform, TranslateTransform.XProperty, Loop(Keys(8, EaseInOut, (0, 0), (0.5, 16), (1, 0)), 1.5));
        foreach (var (z, delay) in new[] { (Z1, 0.0), (Z2, 0.4) })
        {
            AddLoop("zzz", (TranslateTransform)z.RenderTransform, TranslateTransform.YProperty, Loop(Keys(2.6, EaseInOut, (0, 0), (0.5, -3), (1, 0)), delay));
            AddLoop("zzz", z, OpacityProperty, Loop(Keys(2.6, EaseInOut, (0, 0.55), (0.5, 0.9), (1, 0.55)), delay));
        }
    }

    // Rain and storm share the drops; drizzle is the same drops dialled
    // down — thinner, shorter, fainter and slower.
    private void ConfigureDrops(string kind)
    {
        var raining = kind is "rain" or "storm";
        var drizzle = kind == "drizzle";
        foreach (var drop in _drops)
        {
            var shift = (TranslateTransform)drop.RenderTransform;
            if (!raining && !drizzle)
            {
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                drop.Opacity = 0;
                continue;
            }
            var (duration, delay) = ((double, double))drop.Tag;
            drop.Width = drizzle ? 1.5 : 2;
            drop.Height = drizzle ? 7 : 12;
            drop.Opacity = drizzle ? 0.4 : 0.75;
            shift.BeginAnimation(TranslateTransform.YProperty, Loop(Keys(drizzle ? 1.9 : duration, null, (0, -14), (1, 214)), delay));
        }
    }

    private void ScheduleFlash()
    {
        _flashTimer.Interval = TimeSpan.FromMilliseconds(1800 + _random.NextDouble() * 3200);
        _flashTimer.Start();
    }

    private void FlashLightning()
    {
        if (_sky != "storm") return;
        Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(0.5))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ---------------------------------------------------------------- keeper motion

    // The always-on motion: a gentle breath (slower asleep, faster mid-
    // workout), or a sway in the wind, which overrides everything else on
    // the body — including perks and fidgets, same as the old CSS did.
    private void RestartAmbient()
    {
        if (_wrapBusy) return;
        ClearWrap();
        if (_sky == "windy")
        {
            WrapTranslate.BeginAnimation(TranslateTransform.YProperty, Loop(Keys(2.2, EaseInOut, (0, 0), (0.3, -2), (0.65, -3), (1, 0)), 0));
            WrapRotate.BeginAnimation(RotateTransform.AngleProperty, Loop(Keys(2.2, EaseInOut, (0, 0), (0.3, -4), (0.65, 2.5), (1, 0)), 0));
            return;
        }
        var period = _activityState switch { "asleep" => 5.5, "workout" => 1.6, _ => 3.2 };
        WrapTranslate.BeginAnimation(TranslateTransform.YProperty, Loop(Keys(period, EaseInOut, (0, 0), (0.5, -3), (1, 0)), 0));
    }

    private void ClearWrap()
    {
        WrapTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        WrapTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        WrapRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        WrapScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WrapScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void RunWrapOneShot(double seconds, KeySpline spline, params (IAnimatable Target, DependencyProperty Property, (double, double)[] Frames)[] parts)
    {
        if (_sky == "windy") return; // the sway wins, as it did in CSS
        _wrapBusy = true;
        ClearWrap();
        var anims = parts.Select(p => OneShot(Keys(seconds, spline, p.Frames))).ToList();
        // Must be attached before BeginAnimation — the clock copies the
        // timeline's handlers when it's created.
        anims[^1].Completed += (_, _) => { _wrapBusy = false; RestartAmbient(); };
        for (var i = 0; i < parts.Length; i++) parts[i].Target.BeginAnimation(parts[i].Property, anims[i]);
    }

    private void ScheduleBlink()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400 + _random.NextDouble() * 2800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_activityState != "asleep")
            {
                EyesOpen.Visibility = Visibility.Collapsed;
                EyesClosed.Visibility = Visibility.Visible;
            }
            var reopen = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            reopen.Tick += (_, _) =>
            {
                reopen.Stop();
                if (_activityState != "asleep")
                {
                    EyesOpen.Visibility = Visibility.Visible;
                    EyesClosed.Visibility = Visibility.Collapsed;
                }
                ScheduleBlink();
            };
            reopen.Start();
        };
        timer.Start();
    }

    // Idle fidgets — rare, random micro-animations while just standing
    // there (awake, not asleep or mid-stretch). Charm, not signal.
    private void ScheduleFidget()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15000 + _random.NextDouble() * 25000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_activityState == "awake" && !_wrapBusy) PlayFidget(_random.Next(5));
            ScheduleFidget();
        };
        timer.Start();
    }

    private void PlayFidget(int which)
    {
        switch (which)
        {
            case 0: // lean
                RunWrapOneShot(1.1, EaseInOut, (WrapRotate, RotateTransform.AngleProperty, new[] { (0.0, 0.0), (0.5, -2), (1, 0) }));
                break;
            case 1: // squish
                RunWrapOneShot(0.9, EaseInOut, (WrapScale, ScaleTransform.ScaleYProperty, new[] { (0.0, 1.0), (0.5, 0.97), (1, 1) }));
                break;
            case 2: // head tilt — doesn't lock the body
                HeadRotate.BeginAnimation(RotateTransform.AngleProperty, OneShot(Keys(1.3, EaseInOut, (0, 0), (0.5, 4), (1, 0))));
                break;
            case 3: // weight shift
                RunWrapOneShot(1.2, EaseInOut, (WrapTranslate, TranslateTransform.XProperty, new[] { (0.0, 0.0), (0.5, 2.5), (1, 0) }));
                break;
            default: // quick tug at the sleeve — arm and hand move as one
                RightArmShift.BeginAnimation(TranslateTransform.XProperty, OneShot(Keys(0.8, EaseInOut, (0, 0), (0.4, 1), (0.75, -0.5), (1, 0))));
                RightArmShift.BeginAnimation(TranslateTransform.YProperty, OneShot(Keys(0.8, EaseInOut, (0, 0), (0.4, -1.5), (0.75, 0), (1, 0))));
                break;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void FitLine(TextBlock text, double lineWidth)
    {
        var shift = (TranslateTransform)text.RenderTransform;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = text.DesiredSize.Width;
        if (width <= lineWidth) return;
        // translateX(100%) -> translateX(-100%) of the text's own width.
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(width, -width, TimeSpan.FromSeconds(7))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void AddLoop(string group, IAnimatable target, DependencyProperty property, AnimationTimeline? animation)
    {
        if (!_loops.TryGetValue(group, out var list)) _loops[group] = list = new();
        list.Add((target, property, animation!));
    }

    // Starts or stops one decoration's animations. A null animation on
    // VisibilityProperty is a plain show/hide toggle for that element.
    private void SetLoops(string group, bool on)
    {
        if (!_loops.TryGetValue(group, out var list)) return;
        foreach (var (target, property, animation) in list)
        {
            if (property == VisibilityProperty)
            {
                ((UIElement)target).Visibility = Show(on);
                continue;
            }
            target.BeginAnimation(property, on ? animation : null);
        }
    }

    private static DoubleAnimationUsingKeyFrames Keys(double seconds, KeySpline? spline, params (double Time, double Value)[] frames)
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds) };
        foreach (var (time, value) in frames)
        {
            anim.KeyFrames.Add(spline == null
                ? new LinearDoubleKeyFrame(value, KeyTime.FromPercent(time))
                : new SplineDoubleKeyFrame(value, KeyTime.FromPercent(time), spline));
        }
        return anim;
    }

    private static AnimationTimeline Loop(DoubleAnimationUsingKeyFrames anim, double delaySeconds)
    {
        anim.RepeatBehavior = RepeatBehavior.Forever;
        anim.BeginTime = TimeSpan.FromSeconds(delaySeconds);
        anim.Freeze();
        return anim;
    }

    private static AnimationTimeline OneShot(DoubleAnimationUsingKeyFrames anim)
    {
        anim.FillBehavior = FillBehavior.Stop;
        return anim;
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}

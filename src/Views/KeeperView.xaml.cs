using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    private sealed class TextContrastState
    {
        public bool UsesDarkText { get; set; }
        public bool? PendingDarkText { get; set; }
        public int PendingSamples { get; set; }
    }

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
    private bool _customReminderActive;
    private string _avatar = "keeper";
    private string _outfit = "classic";
    private string _motionMode = "full";
    private readonly string? _shirtName;

    // Colour sets for the everyday outfit: the sweater, Keeper's trousers and
    // Lady Keeper's skirt. Weather, gym and night clothes keep their own.
    public static readonly (string Key, string Label, Color Top, Color Trousers, Color Skirt)[] Outfits =
    {
        ("classic", "Classic red", Rgb(0xb0483d), Rgb(0x46506a), Rgb(0x4b3f72)),
        ("ocean", "Ocean blue", Rgb(0x3d7cc0), Rgb(0x34405a), Rgb(0x27496d)),
        ("forest", "Forest green", Rgb(0x3f8a5a), Rgb(0x4a4436), Rgb(0x2f5a3e)),
        ("plum", "Plum", Rgb(0x8a4f9e), Rgb(0x3d3550), Rgb(0x3d3550)),
        ("mustard", "Mustard", Rgb(0xd09a2c), Rgb(0x4a3f35), Rgb(0x6b4a2f)),
        ("charcoal", "Charcoal", Rgb(0x4a4f58), Rgb(0x2a2d33), Rgb(0x7a2f3a)),
    };

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    // Set while a one-shot (perk, or a fidget that moves the whole body)
    // owns KeeperWrap's transform, so two never stomp each other.
    private bool _wrapBusy;
    private readonly DispatcherTimer _flashTimer = new();
    // The time and date on the top line, ticking on their own.
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DispatcherTimer? _blinkTimer;
    private DispatcherTimer? _blinkReopenTimer;
    private DispatcherTimer? _fidgetTimer;
    private DispatcherTimer? _notificationCueTimer;
    private DispatcherTimer? _startupSaluteTimer;
    private DispatcherTimer? _voiceResultTimer;
    private string _voiceCaptureState = "off";
    private string? _voiceResultState;
    private string? _activeNotificationCue;
    private int _contrastTicks;
    private readonly Dictionary<TextBlock, TextContrastState> _textContrast = new();

    public KeeperView()
    {
        InitializeComponent();
        _shirtName = UserAccountName.GetUserName();
        WorkoutCountdown.Text = _shirtName ?? string.Empty;
        WorkoutCountdown.Visibility = Show(!string.IsNullOrEmpty(_shirtName));
        _iconsByKey = new()
        {
            ["youtube"] = IconYoutube, ["spotify"] = IconSpotify, ["bell"] = IconBell, ["search"] = IconSearch,
            ["gpu"] = IconGpu, ["read"] = IconRead, ["details"] = IconDetails, ["mic"] = IconMic, ["convert"] = IconConvert,
            ["color"] = IconColor, ["qr"] = IconQr, ["volume"] = IconVolume, ["clipboard"] = IconClipboard,
            ["stopwatch"] = IconStopwatch, ["daily"] = IconDaily, ["sudoku"] = IconSudoku,
        };

        BuildSunRays();
        BuildPrecipitation();
        BuildLoops();

        _flashTimer.Tick += (_, _) => { FlashLightning(); ScheduleFlash(); };
        Loaded += (_, _) =>
        {
            _contrastTicks = 0;
            UpdateTextContrast();
        };
        Unloaded += (_, _) =>
        {
            ClearStartupSalute();
            SetVoiceStatus("off");
        };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            if (++_contrastTicks >= 2)
            {
                _contrastTicks = 0;
                UpdateTextContrast();
            }
        };
        UpdateClock();
        _clockTimer.Start();

        ApplySky();
        RestartAmbient();
        ScheduleBlink();
        ScheduleFidget();
    }

    // ---------------------------------------------------------------- host API

    public void SetPlace(string city) => Place.Text = city;

    // Persistent local capture state with a two-second result overlay. The
    // overlay returns to the actual capture state, so a slow model restart
    // cannot make the pin claim it is listening before the microphone starts.
    public void SetVoiceStatus(string state)
    {
        if (state is "complete" or "failed")
        {
            _voiceResultState = state;
            StopVoiceResultTimer();
            _voiceResultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _voiceResultTimer.Tick += VoiceResultTimer_Tick;
            _voiceResultTimer.Start();
        }
        else
        {
            _voiceCaptureState = state is "wake" or "armed" or "processing" ? state : "off";
            if (_voiceCaptureState == "off")
            {
                StopVoiceResultTimer();
                _voiceResultState = null;
            }
        }
        RenderVoiceStatus();
    }

    private void VoiceResultTimer_Tick(object? sender, EventArgs e)
    {
        StopVoiceResultTimer();
        _voiceResultState = null;
        RenderVoiceStatus();
    }

    private void StopVoiceResultTimer()
    {
        if (_voiceResultTimer == null) return;
        _voiceResultTimer.Stop();
        _voiceResultTimer.Tick -= VoiceResultTimer_Tick;
        _voiceResultTimer = null;
    }

    private void RenderVoiceStatus()
    {
        var state = _voiceResultState ?? _voiceCaptureState;
        VoiceStatusPin.Visibility = Show(state != "off");
        VoiceMicGlyph.Visibility = Show(state is "wake" or "armed");
        VoiceWorkingGlyph.Visibility = Show(state == "processing");
        VoiceDoneGlyph.Visibility = Show(state == "complete");
        VoiceFailedGlyph.Visibility = Show(state == "failed");
        VoicePinBody.Fill = state switch
        {
            "processing" or "failed" => Brushes.DarkOrange,
            "complete" => Brushes.ForestGreen,
            _ => Brushes.DodgerBlue,
        };

        VoicePinBody.BeginAnimation(UIElement.OpacityProperty, null);
        VoicePinBody.Opacity = 1;
        VoiceHeadRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        VoiceHeadRotate.Angle = 0;
        if (_motionMode == "still") return;
        if (state == "armed")
        {
            VoicePinBody.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1,
                _motionMode == "gentle" ? 0.8 : 0.55,
                TimeSpan.FromMilliseconds(_motionMode == "gentle" ? 1100 : 700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
            VoiceHeadRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, _motionMode == "gentle" ? 2 : 4, TimeSpan.FromMilliseconds(220)));
        }
        else if (state == "complete")
        {
            VoiceHeadRotate.BeginAnimation(RotateTransform.AngleProperty,
                OneShot(Keys(0.8, EaseInOut, (0, 0), (0.3, 5), (0.65, -2), (1, 0))));
        }
    }

    public void SetMotionMode(string mode)
    {
        var normalized = mode is "full" or "gentle" or "still" ? mode : "full";
        if (_motionMode == normalized) return;
        _motionMode = normalized;
        ClearStartupSalute();
        RenderVoiceStatus();
        if (_motionMode == "still") ClearNotificationCue();
        StopIdleMotionTimers();
        _wrapBusy = false;
        ClearWrap();
        HeadRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        RightArmShift.BeginAnimation(TranslateTransform.XProperty, null);
        RightArmShift.BeginAnimation(TranslateTransform.YProperty, null);
        EyesOpen.Visibility = Show(!EyesResting);
        EyesClosed.Visibility = Show(EyesResting);
        _flashTimer.Stop();
        foreach (var group in _loops.Keys.ToArray()) SetLoops(group, false);
        ApplySky();
        if (_motionMode != "still")
        {
            ScheduleBlink();
            ScheduleFidget();
        }
    }

    // One of Outfits' keys; anything else falls back to "classic".
    public void SetOutfit(string key)
    {
        _outfit = Array.Exists(Outfits, o => o.Key == key) ? key : "classic";
        ApplyOutfit();
    }

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
        LadyLashes.Visibility = Show(lady);
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
            TempValue.Text = "—°";
            return;
        }
        TempValue.Text = $"{w.Temperature}°";
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
            NowPlayingTitle.Text = "";
            NowPlayingSubtitle.Text = "";
            FitLine(NowPlayingTitle, LineWidth);
            FitLine(NowPlayingSubtitle, LineWidth);
            NowPlayingIcon.Source = null;
            NowPlayingIcon.Visibility = Visibility.Collapsed;
            NowPlayingNote.Visibility = Visibility.Visible;
            SubtitleLine.Visibility = Visibility.Collapsed;
            ListButton.Visibility = Visibility.Collapsed;
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

    public void SetCustomReminderActive(bool active)
    {
        if (_customReminderActive == active) return;
        _customReminderActive = active;
        WorkoutCountdown.Visibility = Show(!active && (_activityState == "workout" ||
            (_activityState == "awake" && !string.IsNullOrEmpty(_shirtName))));
        ApplyOutfit();
    }

    // Notification text is never executed. A small allowlist selects one of
    // three decorative poses for a few seconds, then restores the idle arms.
    public void SetNotificationCue(string cue)
    {
        if (_motionMode == "still" || _activityState != "awake" || cue is not ("photo" or "news" or "system" or "chatgpt" or "claude"))
        {
            ClearNotificationCue();
            return;
        }

        ClearNotificationCue();
        ClearStartupSalute();
        _activeNotificationCue = cue;
        HangingArms.Visibility = Visibility.Collapsed;
        UmbrellaHeld.Visibility = Visibility.Collapsed;
        NotificationPhotoPose.Visibility = Show(cue == "photo");
        NotificationNewsPose.Visibility = Show(cue == "news");
        NotificationSystemPose.Visibility = Show(cue == "system");
        NotificationChatGptPose.Visibility = Show(cue == "chatgpt");
        NotificationClaudePose.Visibility = Show(cue == "claude");
        MorningMug.Visibility = EveningBook.Visibility = Visibility.Collapsed;

        var timer = _notificationCueTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_activeNotificationCue == cue) ClearNotificationCue();
        };
        timer.Start();
    }

    private void ClearNotificationCue()
    {
        var hadCue = _activeNotificationCue != null;
        _notificationCueTimer?.Stop();
        _notificationCueTimer = null;
        _activeNotificationCue = null;
        NotificationPhotoPose.Visibility = Visibility.Collapsed;
        NotificationNewsPose.Visibility = Visibility.Collapsed;
        NotificationSystemPose.Visibility = Visibility.Collapsed;
        NotificationChatGptPose.Visibility = Visibility.Collapsed;
        NotificationClaudePose.Visibility = Visibility.Collapsed;
        HangingArms.Visibility = Show(ArmsHang);
        if (hadCue) ApplyOutfit();
        UpdateTimeDetails(DateTime.Now);
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
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // "awake", "asleep", "workout" or "prayer" — measured host-side (see
    // UpdateActivityState in MainWindow.xaml.cs). Purely cosmetic.
    public void SetActivity(string state, int secondsLeft)
    {
        if (state != _activityState)
        {
            _activityState = state;
            ClearStartupSalute();
            ClearNotificationCue();
            var sleeping = state == "asleep";
            var working = state == "workout";
            // Prayer: eyes softly closed and a calm smile, standing still on
            // the mat with folded hands - no z's and no dimming, unlike sleep.
            var praying = state == "prayer";

            MouthAwake.Visibility = Show(!sleeping && !working && !praying);
            MouthSleep.Visibility = Show(sleeping || praying);
            MouthWorkout.Visibility = Show(working);
            Zzz.Visibility = Show(sleeping);
            EyesOpen.Visibility = Show(!EyesResting);
            EyesClosed.Visibility = Show(EyesResting);
            HangingArms.Visibility = Show(ArmsHang);
            WorkoutArms.Visibility = Show(working);
            PrayerArms.Visibility = PrayerMat.Visibility = Show(praying);
            WorkoutCountdown.Visibility = Show(working || (state == "awake" && !string.IsNullOrEmpty(_shirtName)));
            if (!working && state == "awake") WorkoutCountdown.Text = _shirtName ?? string.Empty;
            ApplyOutfit();

            SetLoops("zzz", sleeping);
            // Dims a touch once asleep — a resting cue that reads even out
            // of the corner of your eye.
            if (_motionMode == "full")
                Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(sleeping ? 0.85 : 1, TimeSpan.FromSeconds(0.6))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            else
            {
                Stage.BeginAnimation(OpacityProperty, null);
                Stage.Opacity = sleeping ? 0.85 : 1;
            }
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
    // a workout break, night clothes while asleep and prayer clothes at
    // prayer time - for either avatar. The sky keeps showing the weather.
    private void ApplyOutfit()
    {
        var lady = _avatar == "lady";
        var gym = _activityState == "workout";
        var sleep = _activityState == "asleep";
        var prayer = _activityState == "prayer";
        var reminder = _customReminderActive && !gym && !sleep && !prayer;
        var weather = !gym && !sleep && !prayer && !reminder;

        foreach (var el in new UIElement[]
        {
            TorsoRain, TorsoSnowExtra, TorsoStorm, RaincoatHoodBack, RaincoatHood, SnowHat, Sunglasses, UmbrellaHeld, UmbrellaLeaning,
            GymTop, ReminderTop, GymShorts, Headband, PajamaTop, Nightgown, Nightcap, PrayerRobe, PrayerDress, Kufi, Hijab,
        })
        {
            el.Visibility = Visibility.Collapsed;
        }
        TorsoDefault.Visibility = Show(weather);
        LadySkirt.Visibility = Show(lady && weather);
        // The hijab covers Lady Keeper's hair at prayer time.
        HairDefault.Visibility = Show(!lady);
        LadyHairBack.Visibility = LadyHairFront.Visibility = Show(lady && !prayer);

        string arms, legs, shoes;
        if (prayer)
        {
            // Bare feet on the mat; sleeves edged so folded arms read on the robe.
            (lady ? PrayerDress : PrayerRobe).Visibility = Visibility.Visible;
            (lady ? (UIElement)Hijab : Kufi).Visibility = Visibility.Visible;
            var clothes = lady ? "PrayerDress" : "PrayerRobe";
            (arms, legs, shoes) = (clothes, clothes, "Skin");
            PrayerArmLEdge.Stroke = PrayerArmREdge.Stroke = (Brush)FindResource(clothes + "Edge");
        }
        else if (gym)
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
        else if (reminder)
        {
            ReminderTop.Visibility = Visibility.Visible;
            arms = "ReminderTop";
            (legs, shoes) = ("Trousers", "Shoes");
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
        UmbrellaLeaning.Visibility = Show(!weather && !reminder && _sky == "rain");

        // The everyday outfit's colours come from the chosen set.
        var set = Array.Find(Outfits, o => o.Key == _outfit);
        Brush Paint(string key) => key switch
        {
            "Sweater" => new SolidColorBrush(set.Top),
            "ReminderTop" => (Brush)FindResource("ReminderTop"),
            "Trousers" => new SolidColorBrush(set.Trousers),
            _ => (Brush)FindResource(key),
        };
        SweaterBody.Fill = Paint("Sweater");
        LadySkirt.Fill = new SolidColorBrush(set.Skirt);

        var armBrush = Paint(arms);
        ArmL.Stroke = ArmR.Stroke = WorkoutArmL.Stroke = WorkoutArmR.Stroke = PrayerArmL.Stroke = PrayerArmR.Stroke = armBrush;
        LegL.Fill = LegR.Fill = Paint(legs);
        ShoeL.Fill = ShoeR.Fill = (Brush)FindResource(shoes);
    }

    // ---------------------------------------------------------------- sky

    // Next to the temperature: "12° Clear".
    private void ShowSkyLabel(string? sky)
    {
        SkyWord.Text = sky != null && SkyLabels.TryGetValue(sky, out var l) ? "  " + l : "";
    }

    // "9:41 PM · Tue, Oct 6", in Windows' own formats: its short time
    // (12- or 24-hour) and its day/month order, with short names.
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
        var time = now.ToString("t", culture);
        var date = now.ToString("ddd, " + monthDay, culture);
        if (ClockTime.Text != time) ClockTime.Text = time;
        if (ClockDate.Text != date) ClockDate.Text = date;
        UpdateTimeDetails(now);
    }

    // Briefly raise a hand when startup greeting speech actually begins.
    // Still mode holds a static salute and returns automatically, preserving
    // the cue without introducing motion for users who disabled animation.
    public void Salute()
    {
        if (_activityState != "awake") return;
        ClearStartupSalute();
        HangingArms.Visibility = Visibility.Collapsed;
        SalutePose.Visibility = Visibility.Visible;
        _startupSaluteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_motionMode == "still" ? 2.5 : 2.1) };
        _startupSaluteTimer.Tick += StartupSaluteTimer_Tick;
        _startupSaluteTimer.Start();

        if (_motionMode == "still") return;
        var animation = Keys(2.1, null, (0.0, 27), (0.2, 0), (0.78, 0), (1.0, 27));
        animation.Completed += (_, _) => ClearStartupSalute();
        SaluteArmShift.BeginAnimation(TranslateTransform.YProperty, OneShot(animation));
        SaluteArmWave.BeginAnimation(RotateTransform.AngleProperty,
            OneShot(Keys(2.1, null, (0.0, 0), (0.2, 0), (0.31, -7), (0.42, 7), (0.53, -7), (0.64, 7), (0.76, 0), (1.0, 0))));
    }

    private void StartupSaluteTimer_Tick(object? sender, EventArgs e) => ClearStartupSalute();

    private void ClearStartupSalute()
    {
        if (_startupSaluteTimer != null)
        {
            _startupSaluteTimer.Stop();
            _startupSaluteTimer.Tick -= StartupSaluteTimer_Tick;
            _startupSaluteTimer = null;
        }
        SaluteArmShift.BeginAnimation(TranslateTransform.YProperty, null);
        SaluteArmWave.BeginAnimation(RotateTransform.AngleProperty, null);
        SalutePose.Visibility = Visibility.Collapsed;
        if (ArmsHang && _activeNotificationCue == null)
            HangingArms.Visibility = Visibility.Visible;
    }

    // The stretch and the prayer pose each replace the hanging arms.
    private bool ArmsHang => _activityState is not ("workout" or "prayer");

    // Asleep and at prayer the eyes stay closed, so blinks leave them be.
    private bool EyesResting => _activityState is "asleep" or "prayer";

    private void UpdateTimeDetails(DateTime now)
    {
        var awake = _activityState == "awake";
        MorningMug.Visibility = Show(awake && now.Hour >= 7 && now.Hour < 11);
        EveningBook.Visibility = Show(awake && now.Hour >= 19 && now.Hour < 22);
    }

    // Sample one pixel behind each label group, not the widget itself: the
    // layered-window BitBlt used by NativeScreen.ColorAt leaves transparent
    // WPF windows out. No screenshot or sampled color is retained.
    private void UpdateTextContrast()
    {
        if (!IsLoaded) return;

        ApplyTextContrast(DateTimeLine, DateTimeLine, new Point(2, DateTimeLine.ActualHeight / 2));
        ApplyTextContrast(StopwatchLabel, StopwatchLabel, new Point(StopwatchLabel.ActualWidth + 3, StopwatchLabel.ActualHeight / 2));
        ApplyTextContrast(Temp, Temp, new Point(2, Temp.ActualHeight / 2));
        ApplyTextContrast(Place, Place, new Point(2, Place.ActualHeight / 2));
        ApplyTextContrast(BatteryPercent, BatteryPercent, new Point(BatteryPercent.ActualWidth + 3, BatteryPercent.ActualHeight / 2));
        ApplyTextContrast(BatteryBolt, BatteryPercent, new Point(BatteryPercent.ActualWidth + 3, BatteryPercent.ActualHeight / 2));
        ApplyTextContrast(BatteryTime, BatteryTime, new Point(BatteryTime.ActualWidth + 3, BatteryTime.ActualHeight / 2));

        var iconStyle = (Style)FindResource("IconGlyph");
        var iconBackground = TrySampleBackground(MediaBar, new Point(2, MediaBar.ActualHeight / 2));
        if (iconBackground is null) return;
        foreach (var icon in MediaBar.Children.OfType<Grid>())
        {
            foreach (var text in icon.Children.OfType<TextBlock>())
            {
                if (ReferenceEquals(text.Style, iconStyle)) ApplyTextContrast(text, iconBackground.Value);
            }
        }
    }

    private void ApplyTextContrast(TextBlock target, FrameworkElement sampleFrom, Point localSamplePoint)
    {
        if (!sampleFrom.IsVisible || sampleFrom.ActualWidth <= 0 || sampleFrom.ActualHeight <= 0) return;
        try
        {
            var screenPoint = sampleFrom.PointToScreen(localSamplePoint);
            var background = NativeScreen.ColorAt((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
            ApplyTextContrast(target, background);
        }
        catch
        {
            // Keep the last usable contrast if the element has no screen point.
        }
    }

    private Color? TrySampleBackground(FrameworkElement sampleFrom, Point localSamplePoint)
    {
        if (!sampleFrom.IsVisible || sampleFrom.ActualWidth <= 0 || sampleFrom.ActualHeight <= 0) return null;
        try
        {
            var screenPoint = sampleFrom.PointToScreen(localSamplePoint);
            return NativeScreen.ColorAt((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
        }
        catch { return null; }
    }

    private void ApplyTextContrast(TextBlock target, Color background)
    {
        if (!_textContrast.TryGetValue(target, out var state))
        {
            state = new TextContrastState { UsesDarkText = IsWhiteBackground(background) };
            _textContrast.Add(target, state);
            SetTextContrastStyle(target, state.UsesDarkText);
            return;
        }

        // Require two consecutive readings before switching, which filters
        // transient changes from animation or another window moving.
        var desiredStyle = IsWhiteBackground(background);

        if (desiredStyle == state.UsesDarkText)
        {
            state.PendingDarkText = null;
            state.PendingSamples = 0;
            return;
        }

        if (state.PendingDarkText == desiredStyle) state.PendingSamples++;
        else
        {
            state.PendingDarkText = desiredStyle;
            state.PendingSamples = 1;
        }
        if (state.PendingSamples < 2) return;

        state.UsesDarkText = desiredStyle;
        state.PendingDarkText = null;
        state.PendingSamples = 0;
        SetTextContrastStyle(target, state.UsesDarkText);
    }

    private void SetTextContrastStyle(TextBlock target, bool useDarkText)
    {
        target.Foreground = (Brush)FindResource(useDarkText ? "BlackText" : "WhiteText");
        target.Effect = (Effect)FindResource(useDarkText ? "TextHalo" : "TextShadow");
    }

    private static bool IsWhiteBackground(Color color) =>
        color.R >= 228 && color.G >= 228 && color.B >= 228 &&
        Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) <= 18;

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
        if (_motionMode == "full")
            Fog.BeginAnimation(OpacityProperty, new DoubleAnimation(kind == "fog" ? 1 : 0, TimeSpan.FromSeconds(0.4)));
        else
        {
            Fog.BeginAnimation(OpacityProperty, null);
            Fog.Opacity = kind == "fog" ? 1 : 0;
        }
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
            if ((!raining && !drizzle) || _motionMode != "full")
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
        if (_motionMode != "full" || _sky != "storm") { _flashTimer.Stop(); return; }
        _flashTimer.Interval = TimeSpan.FromMilliseconds(1800 + _random.NextDouble() * 3200);
        _flashTimer.Start();
    }

    private void FlashLightning()
    {
        if (_motionMode != "full" || _sky != "storm") return;
        Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(0.5))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ---------------------------------------------------------------- keeper motion

    // The always-on motion: a gentle breath (slower asleep, faster mid-
    // workout, slow and shallow at prayer), or a sway in the wind, which
    // overrides everything else on the body — including perks and fidgets,
    // same as the old CSS did — except prayer, which stays calm and upright.
    private void RestartAmbient()
    {
        if (_wrapBusy) return;
        ClearWrap();
        if (_motionMode == "still") return;
        var praying = _activityState == "prayer";
        if (_sky == "windy" && !praying)
        {
            var scale = _motionMode == "gentle" ? 0.35 : 1;
            var windyPeriod = _motionMode == "gentle" ? 6.6 : 2.2;
            WrapTranslate.BeginAnimation(TranslateTransform.YProperty, Loop(Keys(windyPeriod, EaseInOut, (0, 0), (0.3, -2 * scale), (0.65, -3 * scale), (1, 0)), 0));
            WrapRotate.BeginAnimation(RotateTransform.AngleProperty, Loop(Keys(windyPeriod, EaseInOut, (0, 0), (0.3, -4 * scale), (0.65, 2.5 * scale), (1, 0)), 0));
            return;
        }
        var period = _activityState switch { "asleep" => 5.5, "workout" => 1.6, "prayer" => 6, _ => 3.2 };
        if (_motionMode == "gentle") period = Math.Max(period, 6.5);
        var lift = _motionMode == "gentle" ? -0.8 : praying ? -1.2 : -3;
        WrapTranslate.BeginAnimation(TranslateTransform.YProperty, Loop(Keys(period, EaseInOut, (0, 0), (0.5, lift), (1, 0)), 0));
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
        if (_motionMode == "still") return;
        if (_motionMode == "gentle") seconds *= 1.5;
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
        if (_motionMode == "still") return;
        _blinkTimer?.Stop();
        var gentle = _motionMode == "gentle";
        var timer = _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(gentle ? 9000 + _random.NextDouble() * 6000 : 2400 + _random.NextDouble() * 2800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_motionMode == "still") return;
            if (!EyesResting)
            {
                EyesOpen.Visibility = Visibility.Collapsed;
                EyesClosed.Visibility = Visibility.Visible;
            }
            var reopen = _blinkReopenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            reopen.Tick += (_, _) =>
            {
                reopen.Stop();
                if (_motionMode == "still") return;
                if (!EyesResting)
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
        if (_motionMode == "still") return;
        var gentle = _motionMode == "gentle";
        var timer = _fidgetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(gentle ? 55000 + _random.NextDouble() * 35000 : 15000 + _random.NextDouble() * 25000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_motionMode != "still" && _activityState == "awake" && !_wrapBusy)
                PlayFidget(gentle ? _random.Next(2, 5) : _random.Next(5));
            ScheduleFidget();
        };
        timer.Start();
    }

    private void StopIdleMotionTimers()
    {
        _blinkTimer?.Stop();
        _blinkTimer = null;
        _blinkReopenTimer?.Stop();
        _blinkReopenTimer = null;
        _fidgetTimer?.Stop();
        _fidgetTimer = null;
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
            target.BeginAnimation(property, on && _motionMode == "full" ? animation : null);
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

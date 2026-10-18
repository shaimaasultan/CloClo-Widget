using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace CloCloWidget;

public partial class MainWindow : Window
{
    private WidgetSettings _settings = WidgetSettings.Load();
    private TrayIcon? _trayIcon;
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _batteryTimer;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _weatherTimer;
    private DispatcherTimer? _greetingTimer;
    private DispatcherTimer? _customReminderTimer;
    private DispatcherTimer? _customReminderDisplayTimer;
    private DispatcherTimer? _prayerTimer;
    private LocalAudioPlayer? _azanPlayer;
    private LocalAudioPlayer? _reminderAudioPlayer;
    private string? _reminderSpeechAfterAudio;
    private DateTimeOffset _lastPrayerCheckUtc;
    private readonly HashSet<long> _playedPrayerInstants = new();
    private bool _resumeWatchAfterCustomReminder;
    private readonly Dictionary<CustomReminderSettings, DateTime> _customReminderLastChecked = new();
    // "awake", "asleep", "workout" or "prayer" — see UpdateActivityState.
    private string _activityState = "awake";
    // When the current unbroken stretch of activity began — reset every
    // time a real break (BreakThreshold) is seen before the workout pose
    // ever triggers, so WorkoutAfterMinutes measures time since the last
    // actual pause, not time since launch.
    private DateTime _activeStreakStart = DateTime.Now;
    // Set the moment the workout pose starts, to BreakThreshold from then —
    // the visible countdown on Keeper's shirt runs down to this and the
    // pose ends on its own once time's up, whether or not you actually
    // stopped, rather than waiting to detect a real pause.
    private DateTime? _workoutCountdownEnd;
    private static readonly TimeSpan BreakThreshold = TimeSpan.FromMinutes(2);
    // Set when a prayer time arrives with the prayer look on: Keeper wears
    // prayer clothes on a prayer mat, ahead of every other state, until you
    // mark the prayer done (MarkPrayed).
    private bool _prayerLookActive;
    private Window? _avatarWindow;
    private CommandSearchBar? _commandSearchBar;
    private ScientificCalculatorWindow? _calculatorWindow;
    private SoundPatternTestWindow? _soundPatternWindow;
    private VoiceSignalsWindow? _voiceSignalsWindow;
    private DavidMatchWindow? _davidMatchWindow;
    private VoiceConverterWindow? _voiceConverterWindow;
    private LetterSignalsWindow? _letterSignalsWindow;
    private ContextMenu? _activeContextMenu;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    // The widget renders at 1.3x (see #stage's zoom in widget.html) — every
    // rect below is the original 150x270-canvas value times that same
    // factor, since WPF (unlike CSS) has no idea the page is zoomed and
    // needs the actual on-screen pixel positions to hit-test correctly.
    private const double Scale = 1.3;

    // Roughly where the avatar + the temperature/location text sit, as an
    // offset/size within the window — this is the rect the separate,
    // genuinely-interactive _avatarWindow covers (see CreateAvatarHitWindow).
    // Widened from a tighter 18/114 so the pause button at the right edge
    // of the now-playing label — see below — actually falls inside it; a
    // rect outside _avatarWindow's own bounds never receives a click at
    // all, it just falls through to the desktop (learned this the hard way
    // with the now-removed switch icon).
    private const double AvatarOffsetX = 10 * Scale;
    private const double AvatarOffsetY = 21 * Scale;
    private const double AvatarWidth = 140 * Scale;
    private const double AvatarHeight = 280 * Scale;
    private const double CommandSearchTop = 324;
    // How much of the widget (in physical pixels, each axis) has to land on a
    // monitor's working area before its position counts as usable.
    private const int MinVisiblePixels = 80;

    // The full set of possible media-bar icons, in display order — which
    // ones are actually shown (see WidgetSettings.EnabledIcons) is user
    // configurable via the "Icons" menu, since the bar only fits about 7 at
    // once. Positions are no longer fixed constants because of that: see
    // ComputeIconRects, which mirrors #mediaBar's flexbox layout in
    // widget.html (same icon width/gap/anchor) for whichever subset is
    // currently enabled.
    private static readonly string[] IconOrder =
    {
        "youtube", "spotify", "bell", "search", "gpu", "read", "details", "mic", "convert", "color", "qr", "volume", "clipboard", "stopwatch", "daily", "sudoku",
    };
    private const double IconWidth = 18, IconGap = 2, IconTop = 187, IconHeight = 20;
    // Matches #mediaBar's own left:10px anchor (stage x=10, i.e. relative
    // x=0) through to the stage's right edge (150) — the full width
    // available for the icon row, in avatarWindow-relative stage units.
    private const double IconBarWidth = 140;
    private const int MaxVisibleIcons = 7;
    // The pause button inside the label itself — same coordinate space.
    // Only acts while actually watching something (_watchedFetcher is
    // set); otherwise the label (and this button) isn't even shown. Spans
    // the label's full (now two-line) height rather than trying to track
    // exactly where the button glyph sits within it.
    private static readonly Rect PauseIconRect = new(116 * Scale, 209 * Scale, 22 * Scale, 36 * Scale);
    // The list button, notifications only — opens Windows' own flyout
    // (the full list) instead of launching an app. Sits just left of the
    // pause button; only meaningful while _watchingNotifications, but
    // harmless to check unconditionally since the label isn't shown at
    // all when nothing's being watched.
    private static readonly Rect ListIconRect = new(96 * Scale, 209 * Scale, 20 * Scale, 36 * Scale);
    // The rest of the label (icon/note + both text lines) is display-only.
    // Checked after PauseIconRect/ListIconRect, which it overlaps, so those
    // controls' clicks are claimed first.
    private static readonly Rect LabelBodyRect = new(0, 209 * Scale, 137 * Scale, 36 * Scale);
    // At prayer time, the check badge beside the head and the head itself
    // both mean "I've prayed". KeeperView's art is drawn in 100x140 units,
    // placed at stage (21, 24) by a uniform Viewbox of 108x145 - padded a
    // little so the small badge is easy to hit.
    private static readonly Rect PrayedBadgeRect = ArtRect(77, 15, 19, 19);
    private static readonly Rect PrayedHeadRect = ArtRect(23, 18, 54, 54);

    private static Rect ArtRect(double x, double y, double width, double height)
    {
        const double artScale = 145.0 / 140;
        const double artLeft = 21 + (108 - 100 * artScale) / 2;
        return new Rect((artLeft + x * artScale - AvatarOffsetX / Scale) * Scale,
            (24 + y * artScale - AvatarOffsetY / Scale) * Scale,
            width * artScale * Scale, height * artScale * Scale);
    }

    private static readonly string[] YoutubeAumids = { "edge", "chrome" };
    private static readonly string[] SpotifyAumids = { "spotify" };

    // Whatever the label is currently following — set by clicking an icon,
    // kept live by _nowPlayingTimer until it actually stops (rather than a
    // timed popup that hides itself regardless of whether it's still
    // relevant), or until the pause button stops it explicitly. A plain
    // fetcher delegate so the same watch/poll/pause machinery works for
    // both "what's playing in this app" and "the latest notification".
    // Icon is the sending app's logo bytes (notifications only — media has none) or null.
    private Func<Task<(string Title, string Subtitle, byte[]? Icon)?>>? _watchedFetcher;
    // Notifications aren't really "one app" the way media is — the label
    // shows only the latest of possibly several, so clicking it opens
    // Windows' own notification flyout (the full list) rather than
    // launching whatever app happened to send the latest one.
    private bool _watchingNotifications;
    private DispatcherTimer? _nowPlayingTimer;

    // The last foreground window that belonged to some OTHER process —
    // tracked continuously rather than read at click time, because by the
    // time a click handler runs, clicking our own avatar window has almost
    // certainly already made IT the foreground window. Polled instead of a
    // proper WinEventHook since a few hundred ms of staleness is harmless
    // for a user-initiated "read my selection" action.
    private IntPtr _lastExternalForegroundWindow;
    private DispatcherTimer? _foregroundTrackTimer;

    // A couple of seconds after something closes - once the window's
    // rendering resources and event handlers have actually let go - one
    // aggressive full collection: it compacts the heap and returns the freed
    // memory to Windows straight away. Several closes in a row collapse
    // into one.
    private readonly DispatcherTimer _memoryRelease = new() { Interval = TimeSpan.FromSeconds(2) };

    // The stopwatch icon: one click starts it, the next stops it, the next
    // resets it. Its time shows under the sky/time line at the top.
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    // The 💡 icon's speech bubble (today's quote, word, joke and fact), or
    // null while it's closed.
    private DailyBubbleWindow? _dailyBubble;
    // The Layer Sudoku window, while it's open (one at a time).
    private SudokuWindow? _sudoku;
    private DictationCaptureWindow? _dictationWindow;
    private bool _voiceActivationClosing;
    // Hold to talk doesn't start while a speech editor or voice test uses
    // the microphone, or while the azan plays.
    private bool _voicePausedForEditor;
    private bool _voicePausedForAudio;
    private SelectionDetailsWindow? _selectionDetailsWindow;
    private int _selectionDetailsRequest;
    private WindowsStatusWindow? _windowsStatusWindow;
    private FileInsightsWindow? _fileInsightsWindow;
    // The avatar's head, in this window's units: where the bubble's tail points.
    private const double HeadY = 52 * Scale;
    private DispatcherTimer? _stopwatchTimer;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    // Ordered by actual cloud density (see cloudCoverToSky in widget.html),
    // not alphabetically — "Mostly Cloudy" (a sun peeking through) is
    // lighter than "Cloudy" (solid overcast, no sun at all), matching the
    // same convention Windows' own weather icons use despite how the names
    // read at first glance.
    private static readonly (string Value, string Label)[] SkyOptions =
    {
        ("auto", "Auto (live)"),
        ("clear", "Clear"),
        ("cloudy", "Partly Cloudy"),
        ("mostlycloudy", "Mostly Cloudy"),
        ("overcast", "Cloudy"),
        ("windy", "Windy"),
        ("fog", "Fog"),
        ("drizzle", "Drizzle"),
        ("rain", "Rain"),
        ("snow", "Snow"),
        ("storm", "Storm"),
    };

    public MainWindow()
    {
        InitializeComponent();
        // Coalesce close events so several tools closing together schedule
        // one background collection after their references have been cleared.
        _memoryRelease.Tick += (_, _) => { _memoryRelease.Stop(); _ = Task.Run(ReleaseMemory); };
        SpeechReader.Finished += () => Dispatcher.BeginInvoke(new Action(ScheduleMemoryRelease));
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            _voiceActivationClosing = true;
            UpdatePushToTalkHook(); // closing: removes the mouse watcher
            Keeper.SetVoiceStatus("off");
            // SystemEvents holds its handlers in a static list, so leaving this
            // one attached would keep the whole window alive past close.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            // A static WinRT event, the same as SystemEvents above.
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            if (_devices != null)
            {
                _devices.Changed -= OnDevicesChanged;
                _devices.Dispose();
                _devices = null;
            }
            if (_greetingTimer != null)
            {
                _greetingTimer.Stop();
                _greetingTimer.Tick -= GreetingTimer_Tick;
                _greetingTimer = null;
            }
            SaveWindowPosition();
            _customReminderTimer?.Stop();
            if (_customReminderTimer != null) _customReminderTimer.Tick -= CustomReminderTimer_Tick;
            _customReminderTimer = null;
            StopCustomReminderDisplayTimer();
            if (_prayerTimer != null)
            {
                _prayerTimer.Stop();
                _prayerTimer.Tick -= PrayerTimer_Tick;
                _prayerTimer = null;
            }
            _azanPlayer?.Dispose();
            _azanPlayer = null;
            if (_reminderAudioPlayer != null)
            {
                _reminderAudioPlayer.Finished -= ReminderAudioFinished;
                _reminderAudioPlayer.Dispose();
                _reminderAudioPlayer = null;
            }
            _reminderSpeechAfterAudio = null;
            _commandSearchBar?.CloseSuggestions();
            _avatarWindow?.Close();
            _commandSearchBar = null;
        };
    }

    // This window is purely a visual layer now — every click passes
    // straight through it (WS_EX_TRANSPARENT) to whatever's behind it on
    // the desktop. The only thing that's ever actually clickable is the
    // separate _avatarWindow created in CreateAvatarHitWindow, so there's
    // no WM_NCHITTEST guesswork about pixel colours or fractional boxes:
    // a plain, ordinary, fully hit-testable window either is or isn't
    // under the cursor.
    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        ScreenCapture.SetExcluded(this, _settings.HideFromScreenCapture);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartupRegistration.RepairIfStale();
        // Starting CloClo again while it runs just brings this copy back.
        Program.Instance?.ListenForShowRequests(() => Dispatcher.BeginInvoke(new Action(ShowBecauseStartedAgain)));
        PlaceWindow();
        SetupTrayIcon();
        CreateAvatarHitWindow();
        UpdatePushToTalkHook();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Topmost = _settings.AlwaysOnTop;
        SetupTopmostTimer();
        SetupForegroundTracking();
        SetupBatteryTimer();
        SetupNetworkWatch();
        SetupDeviceWatch();
        SetupIdleTimer();
        SetupCustomReminderTimer();
        SetupPrayerTimer();
        QueueGreeting();

        ApplyDisplaySettings();
        RefreshBattery();
        PushActivityState();
        SetupWeatherTimer();
    }

    private void SetupWeatherTimer()
    {
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
        _weatherTimer.Start();
        _ = RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        var latitude = _settings.Latitude;
        var longitude = _settings.Longitude;
        var reading = await WeatherService.GetCurrentAsync(latitude, longitude);
        // A location change can finish while the previous weather request is
        // still in flight. Never apply its weather or time zone to the new city.
        if (latitude != _settings.Latitude || longitude != _settings.Longitude) return;
        Keeper.SetWeather(reading);
        if (reading is { TimeZoneId: { } zone } && PrayerTimeZone.IsValidId(zone) &&
            _settings.LocationTimeZoneId != zone)
        {
            _settings.LocationTimeZoneId = zone;
            _settings.Save();
        }
    }

    private void QueueGreeting()
    {
        if (!_settings.SpeakGreeting) return;
        _greetingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _greetingTimer.Tick += GreetingTimer_Tick;
        _greetingTimer.Start();
    }

    private void GreetingTimer_Tick(object? sender, EventArgs e)
    {
        _greetingTimer?.Stop();
        if (_greetingTimer != null) _greetingTimer.Tick -= GreetingTimer_Tick;
        _greetingTimer = null;
        if (_settings.SpeakGreeting && IsLoaded)
            SpeechReader.Speak(BuildGreeting(), () => Dispatcher.BeginInvoke(new Action(Keeper.Salute)));
    }

    private static string BuildGreeting()
    {
        var displayName = UserAccountName.GetDisplayName();
        var userName = UserAccountName.GetUserName();
        return $"Hi, {displayName ?? userName ?? "there"}.";
    }

    // "Current session" (tried first) turned out to be Windows' own guess
    // at what's relevant — whichever app last had media-key focus — which
    // could stick to a browser tab that wasn't even playing anymore instead
    // of actually-playing Spotify. Clicking an icon instead asks about one
    // specific thing, and keeps watching it — the label stays up for as
    // long as it's still relevant, and disappears once it actually isn't,
    // rather than hiding itself after a fixed delay regardless.
    private void WatchApp(string[] aumidMatches)
    {
        _watchingNotifications = false;
        Watch(async () =>
        {
            var info = await NowPlaying.GetForAppAsync(aumidMatches);
            return info is { } np ? (np.Title, np.Artist, (byte[]?)null) : ((string, string, byte[]?)?)null;
        });
    }

    private void WatchNotifications()
    {
        _watchingNotifications = true;
        var cueShown = false;
        Watch(async () =>
        {
            var n = await NotificationWatcher.GetLatestAsync();
            if (n is not { } latest) return null;
            if (!cueShown)
            {
                cueShown = true;
                var cue = ClassifyNotification(latest.AppName, latest.Title);
                if (cue != "none") Keeper.SetNotificationCue(cue);
            }
            return _settings.ShowNotificationText
                ? ($"{latest.AppName}: {latest.Title}", latest.Body, latest.IconPng)
                : (latest.AppName, "New notification", latest.IconPng);
        });
    }

    // Notification details are read only after the user clicks the bell.
    // Classification uses just the sender and title to choose a decorative
    // pose; notification text is never executed or used to activate an app.
    private static string ClassifyNotification(string appName, string title)
    {
        static bool HasAny(string value, params string[] terms) =>
            terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

        // App-specific reactions use the trusted sender display name only;
        // notification text from these apps is never interpreted as a command.
        if (HasAny(appName, "chatgpt", "openai")) return "chatgpt";
        if (HasAny(appName, "claude", "anthropic")) return "claude";

        if (HasAny(appName, "snipping tool", "screen capture", "screenshot", "camera") ||
            HasAny(title, "screenshot", "screen shot", "screen snip", "snip saved", "photo captured", "capture saved"))
            return "photo";

        if (HasAny(appName, "news", "newspaper") ||
            HasAny(title, "news", "headline", "breaking", "new article"))
            return "news";

        if (HasAny(appName, "windows update", "windows security", "microsoft defender", "device security") ||
            HasAny(title, "system update", "update available", "restart required", "maintenance", "security alert", "backup completed", "driver update", "firmware update"))
            return "system";

        return "none";
    }

    // Static, not really something to "watch" the way media or
    // notifications change — but reusing the same label/poll/pause
    // mechanism is simpler than building a separate one-shot display just
    // for this, and re-querying WMI every few seconds is cheap enough.
    // GPU names are display-only, like all other watched metadata.
    private void WatchGpus()
    {
        _watchingNotifications = false;
        Watch(async () =>
        {
            var names = await Task.Run(GpuInfo.GetNames);
            if (names.Count == 0) return ((string, string, byte[]?)?)null;
            var title = names[0];
            var subtitle = names.Count > 1 ? string.Join(", ", names.Skip(1)) : "";
            return (title, subtitle, (byte[]?)null);
        });
    }

    // A one-shot action, not a watch — grabs whatever's currently
    // highlighted in whichever app had focus before this click, shows it in
    // the label, and has Keeper read it aloud. Selection comes from UI
    // Automation or a guarded Ctrl+C clipboard fallback. Deliberately
    // doesn't go through Watch()/the polling timer the way GPU info does.
    private void ReadSelection()
    {
        _watchingNotifications = false;
        _ = ReadSelectionAsync();
    }

    private async Task ReadSelectionAsync()
    {
        var target = _lastExternalForegroundWindow;
        var text = target == IntPtr.Zero ? null : await TextSelectionReader.ReadSelectedTextAsync(target);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowOneShotLabel("Selection unavailable", "Highlight copyable text in another app, then try again.");
            OpenSpeechWindow();
            return;
        }

        text = text.Trim();
        const int titleLimit = 60;
        var title = text.Length > titleLimit ? text[..titleLimit] + "…" : text;
        var subtitle = text.Length > titleLimit ? text : "";
        ShowOneShotLabel(title, subtitle);
        var editor = OpenSpeechWindow();
        editor.AddSelectedText(text);
        editor.StartReading();
    }

    private void ShowSelectionDetails()
    {
        _watchingNotifications = false;
        _selectionDetailsWindow?.Close();
        _ = ShowSelectionDetailsAsync(++_selectionDetailsRequest);
    }

    private async Task ShowSelectionDetailsAsync(int requestId)
    {
        var target = _lastExternalForegroundWindow;
        var text = target == IntPtr.Zero ? null : await TextSelectionReader.ReadSelectedTextAsync(target);
        if (requestId != _selectionDetailsRequest) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowOneShotLabel("Selection unavailable", "Highlight copyable text in another app, then try again.");
            return;
        }

        var window = new SelectionDetailsWindow(text)
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _selectionDetailsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_selectionDetailsWindow, window)) _selectionDetailsWindow = null;
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    private void ShowWindowsStatus()
    {
        if (_windowsStatusWindow != null)
        {
            _windowsStatusWindow.Activate();
            return;
        }

        var window = new WindowsStatusWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _windowsStatusWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_windowsStatusWindow, window)) _windowsStatusWindow = null;
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    private void ShowFileInsights()
    {
        if (_fileInsightsWindow != null)
        {
            if (_fileInsightsWindow.WindowState == WindowState.Minimized) _fileInsightsWindow.WindowState = WindowState.Normal;
            _fileInsightsWindow.Activate();
            return;
        }

        var window = new FileInsightsWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _fileInsightsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_fileInsightsWindow, window)) _fileInsightsWindow = null;
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    private void ShowScientificCalculator()
    {
        if (_calculatorWindow != null)
        {
            if (_calculatorWindow.WindowState == WindowState.Minimized) _calculatorWindow.WindowState = WindowState.Normal;
            _calculatorWindow.Activate();
            return;
        }

        var window = new ScientificCalculatorWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _calculatorWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_calculatorWindow, window)) _calculatorWindow = null;
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    // Hold to talk is paused while the test listens on the microphone.
    private void ShowSoundPatternTest()
    {
        if (_soundPatternWindow != null)
        {
            if (_soundPatternWindow.WindowState == WindowState.Minimized) _soundPatternWindow.WindowState = WindowState.Normal;
            _soundPatternWindow.Activate();
            return;
        }

        _voicePausedForEditor = true;
        AbortPushToTalk();
        var window = new SoundPatternTestWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _soundPatternWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_soundPatternWindow, window)) _soundPatternWindow = null;
            if (_dictationWindow == null && !VoiceTestWindowOpen) ResumeVoiceAfterEditor();
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    // Like the sound pattern test, hold to talk pauses while this window
    // can record.
    private void ShowVoiceSignals()
    {
        if (_voiceSignalsWindow != null)
        {
            if (_voiceSignalsWindow.WindowState == WindowState.Minimized) _voiceSignalsWindow.WindowState = WindowState.Normal;
            _voiceSignalsWindow.Activate();
            return;
        }

        _voicePausedForEditor = true;
        AbortPushToTalk();
        var window = new VoiceSignalsWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _voiceSignalsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_voiceSignalsWindow, window)) _voiceSignalsWindow = null;
            if (_dictationWindow == null && !VoiceTestWindowOpen) ResumeVoiceAfterEditor();
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    // The voice test windows record from the microphone; hold to talk stays
    // paused while any of them is open. (The dictation editor resumes it
    // itself when its capture stops, so it isn't counted here.)
    private bool VoiceTestWindowOpen =>
        _soundPatternWindow != null || _voiceSignalsWindow != null ||
        _davidMatchWindow != null || _voiceConverterWindow != null;

    // Builds words from letters - no microphone, so hold to talk stays on.
    private void ShowLetterSignals()
    {
        if (_letterSignalsWindow != null)
        {
            if (_letterSignalsWindow.WindowState == WindowState.Minimized) _letterSignalsWindow.WindowState = WindowState.Normal;
            _letterSignalsWindow.Activate();
            return;
        }
        var window = new LetterSignalsWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _letterSignalsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_letterSignalsWindow, window)) _letterSignalsWindow = null;
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    // Records like Voice signals, so hold to talk pauses while it's open.
    private void ShowVoiceConverter()
    {
        if (_voiceConverterWindow != null)
        {
            if (_voiceConverterWindow.WindowState == WindowState.Minimized) _voiceConverterWindow.WindowState = WindowState.Normal;
            _voiceConverterWindow.Activate();
            return;
        }

        _voicePausedForEditor = true;
        AbortPushToTalk();
        var window = new VoiceConverterWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _voiceConverterWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_voiceConverterWindow, window)) _voiceConverterWindow = null;
            if (_dictationWindow == null && !VoiceTestWindowOpen) ResumeVoiceAfterEditor();
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    // Records like Voice signals, so hold to talk pauses while it's open.
    private void ShowDavidMatch()
    {
        if (_davidMatchWindow != null)
        {
            if (_davidMatchWindow.WindowState == WindowState.Minimized) _davidMatchWindow.WindowState = WindowState.Normal;
            _davidMatchWindow.Activate();
            return;
        }

        _voicePausedForEditor = true;
        AbortPushToTalk();
        var window = new DavidMatchWindow
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _davidMatchWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_davidMatchWindow, window)) _davidMatchWindow = null;
            if (_dictationWindow == null && !VoiceTestWindowOpen) ResumeVoiceAfterEditor();
        };
        PositionBesideWidget(window);
        ShowSensitive(window);
    }

    // Search runs only these fixed menu actions. Typed text filters labels and
    // aliases; it is never passed to a process, file path or command parser.
    private IReadOnlyList<WidgetSearchAction> BuildQuickActions()
    {
        var actions = new List<WidgetSearchAction>
        {
            new("Windows status", "storage connection drive disk network wifi cpu processor memory ram process battery power display monitor gpu graphics uptime security antivirus firewall update dashboard", WidgetSearchActionKind.OpenWindow, ShowWindowsStatus),
            new("Show PC uptime", "uptime boot started restart running time since", WidgetSearchActionKind.Toggle, ToggleShowUptime, () => _settings.ShowUptime),
            new("Show internet connection", "network wifi wi-fi ethernet wired cellular mobile 5g 4g lte offline metered public private signal bars", WidgetSearchActionKind.Toggle, ToggleShowNetwork, () => _settings.ShowNetwork),
            new("Show Bluetooth and headphones", "bluetooth headphones headset earbuds earphones audio devices connected", WidgetSearchActionKind.Toggle, ToggleShowDevices, () => _settings.ShowDevices),
            new("File summary and search", "files folder documents dashboard find text word phrase grep size count docx", WidgetSearchActionKind.OpenWindow, ShowFileInsights),
            new("Scientific calculator", "math arithmetic trig sin cos log square root", WidgetSearchActionKind.OpenWindow, ShowScientificCalculator),
            new("Sound pattern test", "voice signal microphone cloclo accessibility", WidgetSearchActionKind.OpenWindow, ShowSoundPatternTest),
            new("Voice signals", "waveform david hoda microphone record compare accessibility", WidgetSearchActionKind.OpenWindow, ShowVoiceSignals),
            new("Find it in David's words", "david match word microphone record recognise accessibility", WidgetSearchActionKind.OpenWindow, ShowDavidMatch),
            new("Voice converter", "david convert my voice microphone record compare accessibility", WidgetSearchActionKind.OpenWindow, ShowVoiceConverter),
            new("Letter signals", "letters formant table build word david compare signal", WidgetSearchActionKind.OpenWindow, ShowLetterSignals),
            new("Custom reminders", "reminder remainder schedule message", WidgetSearchActionKind.OpenWindow, OpenCustomReminders),
            new("Prayer times and azan", "salah fajr dhuhr asr maghrib isha adhan", WidgetSearchActionKind.OpenWindow, OpenPrayerTimes),
            new("Open dictation folder", "dictation files recordings", WidgetSearchActionKind.OpenFolder, OpenDictationFolder),
            new("Set location", "city place weather gps", WidgetSearchActionKind.OpenWindow, OpenLocationDialog),
            new("Refresh weather", "forecast update", WidgetSearchActionKind.Run, () => _ = RefreshWeatherAsync()),
            new("Start new dictation file", "dictate speech recording", WidgetSearchActionKind.CreateFile, StartNewDictationFile),
            new("Hide widget (restore from tray)", "visibility tray hide", WidgetSearchActionKind.Hide, ToggleVisible),
            new("Always on top", "toggle floating", WidgetSearchActionKind.Toggle, ToggleAlwaysOnTop, () => _settings.AlwaysOnTop),
            new("Start with Windows", "startup login toggle", WidgetSearchActionKind.Toggle, ToggleStartWithWindows, () => StartupRegistration.IsEnabled()),
            new("Privacy: Hide from screen sharing", "capture screenshots toggle", WidgetSearchActionKind.Toggle, ToggleHideFromScreenCapture, () => _settings.HideFromScreenCapture),
            new("Privacy: Show notification text", "notifications privacy toggle", WidgetSearchActionKind.Toggle, ToggleNotificationText, () => _settings.ShowNotificationText),
            new("Privacy: Speak greeting", "account name startup voice toggle", WidgetSearchActionKind.Toggle, ToggleSpeakGreeting, () => _settings.SpeakGreeting),
            new("Privacy: Hold right-click to talk", "push to talk hold mouse microphone voice command open app accessibility toggle", WidgetSearchActionKind.Toggle, TogglePushToTalk, () => _settings.PushToTalkEnabled),
            new("Privacy: Voice commands in Arabic", "cloclo arabic عربي voice command language toggle", WidgetSearchActionKind.Toggle, ToggleVoiceCommandsInArabic, () => _settings.VoiceCommandsInArabic),
        };

        // All icon actions remain available here even when their icon is hidden.
        // These are the same handlers the icon hit targets call, never text commands.
        actions.AddRange(new WidgetSearchAction[]
        {
            new("Watch YouTube", "youtube media playing", WidgetSearchActionKind.Watch, () => WatchApp(YoutubeAumids)),
            new("Watch Spotify", "spotify media playing", WidgetSearchActionKind.Watch, () => WatchApp(SpotifyAumids)),
            new("Watch notifications", "bell latest notification", WidgetSearchActionKind.Watch, WatchNotifications),
            new("Choose media session", "search other apps media picker", WidgetSearchActionKind.OpenMenu, ShowAppPickerFromSearch),
            new("GPU info", "graphics card hardware", WidgetSearchActionKind.Watch, WatchGpus),
            new("Read selection aloud", "speech editor selected text", WidgetSearchActionKind.OpenWindow, ReadSelection),
            new("Selection details", "selected text words characters lines", WidgetSearchActionKind.OpenWindow, ShowSelectionDetails),
            new("Speech and dictation", "mic whisper offline read aloud load save text", WidgetSearchActionKind.OpenWindow, OpenDictationCapture),
            new("Convert units", "selected text measurement temperature", WidgetSearchActionKind.OpenWindow, ConvertSelection),
            new("Color picker", "eyedropper screen color", WidgetSearchActionKind.Run, StartColorPicking),
            new("QR code from selection", "selected text link", WidgetSearchActionKind.OpenWindow, ShowQrCode),
            new("Mute or unmute volume", "sound audio speaker", WidgetSearchActionKind.Toggle,
                ToggleVolume, () => VolumeControl.GetState()?.Muted, OnLabel: "Muted", OffLabel: "Sound on"),
            new("Clipboard history", "copied text", WidgetSearchActionKind.OpenWindow, ShowClipboardHistory),
            new("Stopwatch: start, pause or stop", "timer elapsed toggle", WidgetSearchActionKind.Run,
                ToggleStopwatch, CurrentBadge: StopwatchSearchBadge),
            new("Daily quote, word, joke and fact", "bubble today", WidgetSearchActionKind.Toggle, ToggleDailyBubble, () => _dailyBubble != null),
            new("Layer Sudoku", "daily game puzzle", WidgetSearchActionKind.OpenWindow, OpenSudoku),
        });

        foreach (var opt in AvatarOptions)
            actions.Add(new WidgetSearchAction($"Avatar: {opt.Label}", "character theme", WidgetSearchActionKind.Set, () => SetAvatar(opt.Value)));
        foreach (var outfit in KeeperView.Outfits)
            actions.Add(new WidgetSearchAction($"Outfit colour: {outfit.Label}", "avatar clothing theme", WidgetSearchActionKind.Set, () => SetOutfit(outfit.Key)));
        foreach (var opt in SkyOptions)
            actions.Add(new WidgetSearchAction($"Weather: {opt.Label}", "sky theme forecast", WidgetSearchActionKind.Set, () => SetSky(opt.Value)));
        foreach (var opt in MotionOptions)
            actions.Add(new WidgetSearchAction($"Motion: {opt.Label}", "animation reduced motion", WidgetSearchActionKind.Set, () => SetMotionMode(opt.Value)));
        foreach (var opt in SleepOptions)
            actions.Add(new WidgetSearchAction($"Sleep after: {opt.Label}", "idle sleep", WidgetSearchActionKind.Set, () => SetSleepAfter(opt.Minutes)));
        foreach (var opt in WorkoutOptions)
            actions.Add(new WidgetSearchAction($"Workout reminder: {opt.Label}", "exercise break", WidgetSearchActionKind.Set, () => SetWorkoutAfter(opt.Minutes)));
        foreach (var opt in IconOptions)
            actions.Add(new WidgetSearchAction($"Show/hide icon: {opt.Label}", "icons icon bar visibility toggle",
                WidgetSearchActionKind.Toggle, () => ToggleIcon(opt.Key),
                () => _settings.EnabledIcons.Contains(opt.Key), IconSetting: true));

        actions.Add(new WidgetSearchAction("Exit CloClo", "quit close app", WidgetSearchActionKind.Exit, () => System.Windows.Application.Current.Shutdown()));
        return actions;
    }

    // Same one-shot pattern as ReadSelection — grabs whatever's currently
    // highlighted and opens a QR code for it, rather than polling.
    private void ShowQrCode()
    {
        _watchingNotifications = false;
        _ = ShowQrCodeAsync();
    }

    private async Task ShowQrCodeAsync()
    {
        var target = _lastExternalForegroundWindow;
        if (target == IntPtr.Zero) return;

        var text = await TextSelectionReader.ReadSelectedTextAsync(target);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowOneShotLabel("Selection unavailable", "Highlight copyable text in another app, then try again.");
            return;
        }

        try
        {
            ShowSensitive(new QrCodeWindow(text.Trim()) { Owner = this });
        }
        catch
        {
            // QR codes have a real capacity limit — a whole paragraph
            // accidentally selected instead of a link will blow past it.
            ShowOneShotLabel("Too much text for a QR code", "Try selecting just a link or a short line.");
        }
    }

    // A quick toggle, not a watch or a window — mute state can (and does)
    // change from outside the widget too (hardware keys, other apps), so
    // this always reads+flips the real current state rather than tracking
    // its own, and just flashes the result in the label rather than
    // keeping something continuously in sync with it.
    private void ToggleVolume()
    {
        _watchingNotifications = false;
        var state = VolumeControl.ToggleMute();
        if (state == null)
        {
            ShowOneShotLabel("Couldn't reach volume control", "");
            return;
        }
        var title = state.Muted ? "Muted" : $"{Math.Round(state.Level * 100)}% volume";
        ShowOneShotLabel(title, "");
    }

    // Pulled fresh from Windows' own Clipboard History (the same one Win+V
    // shows) only at the moment this is clicked — no background listener,
    // no timer, nothing running between clicks. That's a real Windows
    // feature the user has to have turned on themselves (Settings > System
    // > Clipboard > Clipboard history); if they haven't, this says so
    // rather than silently showing nothing.
    private void ShowClipboardHistory()
    {
        _watchingNotifications = false;
        _ = ShowClipboardHistoryAsync();
    }

    private async Task ShowClipboardHistoryAsync()
    {
        var result = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
        if (result.Status == Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled)
        {
            ShowOneShotLabel("Clipboard history is off", "Turn it on in Settings > System > Clipboard.");
            return;
        }
        if (result.Status != Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success || result.Items.Count == 0)
        {
            ShowOneShotLabel("Clipboard history is empty", "Copy something, then click again.");
            return;
        }

        var texts = new List<string>();
        foreach (var item in result.Items.OrderByDescending(i => i.Timestamp))
        {
            if (!item.Content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) continue;
            var text = await item.Content.GetTextAsync();
            if (!string.IsNullOrEmpty(text)) texts.Add(text);
        }

        if (texts.Count == 0)
        {
            ShowOneShotLabel("Nothing text-based in your clipboard history", "");
            return;
        }
        ShowSensitive(new ClipboardHistoryWindow(texts) { Owner = this });
    }

    // Windows that put the user's own text on screen follow the same
    // screen-capture setting as the widget itself.
    private void ShowSensitive(Window window)
    {
        ScreenCapture.SetExcluded(window, _settings.HideFromScreenCapture);
        ShowToolWindow(window);
    }

    // Shared editor for dictation and read-aloud. The microphone and playback
    // toggles feed local Whisper text into the editor; saving remains explicit.
    private void OpenDictationCapture() => OpenSpeechWindow();

    private DictationCaptureWindow OpenSpeechWindow()
    {
        _watchingNotifications = false;
        if (_dictationWindow?.IsLoaded == true)
        {
            _dictationWindow.Activate();
            return _dictationWindow;
        }

        _voicePausedForEditor = true;
        AbortPushToTalk();
        var dialog = new DictationCaptureWindow()
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _dictationWindow = dialog;
        dialog.SaveRequested += SaveDictationText;
        dialog.OpenFolderRequested += OpenDictationFolder;
        dialog.CaptureStopped += ResumeVoiceAfterEditor;
        dialog.Closed += (_, _) =>
        {
            dialog.SaveRequested -= SaveDictationText;
            dialog.OpenFolderRequested -= OpenDictationFolder;
            if (ReferenceEquals(_dictationWindow, dialog)) _dictationWindow = null;
        };
        PositionBesideWidget(dialog);
        ScreenCapture.SetExcluded(dialog, _settings.HideFromScreenCapture);
        ShowToolWindow(dialog);
        return dialog;
    }

    private string? SaveDictationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var path = _settings.DictationFilePath;
        try
        {
            if (!SpeechToText.IsInDictationFolder(path) || !SpeechToText.TryAppend(path, text))
            {
                path = SpeechToText.CreateNewFileWithText(text);
                _settings.DictationFilePath = path;
                _settings.Save();
            }
        }
        catch
        {
            ShowOneShotLabel("Dictation wasn't saved", "Check local app-data storage and try again.");
            return null;
        }

        const int titleLimit = 60;
        var title = text.Length > titleLimit ? text[..titleLimit] + "…" : text;
        ShowOneShotLabel(title, $"Saved to {Path.GetFileName(path)}");
        return path;
    }

    // Always opens the converter window — a text selection like "5 km" is
    // used as a convenience pre-fill (same TextSelectionReader ReadSelection
    // uses) when there's a valid one, but isn't required; the window itself
    // is a plain From/To/Number converter you can drive entirely by hand.
    private void ConvertSelection()
    {
        _watchingNotifications = false;
        _ = ConvertSelectionAsync();
    }

    private async Task ConvertSelectionAsync()
    {
        UnitConverter.ParsedSelection? parsed = null;

        var target = _lastExternalForegroundWindow;
        if (target != IntPtr.Zero)
        {
            var text = await TextSelectionReader.ReadSelectedTextAsync(target);
            if (!string.IsNullOrWhiteSpace(text))
            {
                (parsed, _) = UnitConverter.ParseSelection(text);
            }
        }

        ShowToolWindow(new UnitConverterWindow(parsed) { Owner = this });
    }

    // Eyedropper: live hex preview while hovering anywhere on screen (not
    // just this widget), click to copy, Escape to cancel — delegated to
    // ColorPickerOverlay, a real fullscreen window that genuinely owns the
    // cursor and the click while it's up. A first attempt polled
    // GetAsyncKeyState from a background timer instead of using a real
    // window; that couldn't change the system cursor (whatever real window
    // was actually under the pointer controlled that) and couldn't stop a
    // click from also reaching whatever was underneath — both confirmed
    // broken by user report before switching to this approach.
    private ColorPickerOverlay? _colorPickerOverlay;

    private void StartColorPicking()
    {
        _watchingNotifications = false;
        StopColorPicking(); // in case a previous pick session is still running

        _colorPickerOverlay = new ColorPickerOverlay();
        TrackToolWindow(_colorPickerOverlay);
        _colorPickerOverlay.Previewed += color =>
            ShowOneShotLabel(ColorPicker.ToHex(color), "Click to copy, Esc to cancel");
        _colorPickerOverlay.Picked += color =>
        {
            var hex = ColorPicker.ToHex(color);
            ShowOneShotLabel(hex, SafeClipboard.TrySetText(hex) ? "Copied to clipboard" : "Clipboard busy - not copied");
            ShowToolWindow(new ColorViewerWindow(color) { Owner = this });
            _colorPickerOverlay = null;
        };
        _colorPickerOverlay.Cancelled += () =>
        {
            ShowOneShotLabel("Cancelled", "");
            _colorPickerOverlay = null;
        };
        _colorPickerOverlay.Show();
    }

    private void StopColorPicking()
    {
        _colorPickerOverlay?.Close();
        _colorPickerOverlay = null;
    }

    private void TrackToolWindow(Window window) => window.Closed += (_, _) => ScheduleMemoryRelease();

    private void ShowToolWindow(Window window)
    {
        TrackToolWindow(window);
        window.Show();
    }

    private bool? ShowToolDialog(Window window)
    {
        TrackToolWindow(window);
        return window.ShowDialog();
    }

    // "Start new dictation file" selects where a later explicit Save appends.
    private void StartNewDictationFile()
    {
        var path = SpeechToText.CreateNewFile();
        _settings.DictationFilePath = path;
        _settings.Save();
        ShowOneShotLabel("New dictation file", Path.GetFileName(path));
    }

    // Pushes a single label update without starting _nowPlayingTimer, for
    // one-shot results like ReadSelection() above where re-fetching on a
    // timer would be wrong rather than just wasteful.
    private void ShowOneShotLabel(string title, string subtitle)
    {
        _watchedFetcher = () => Task.FromResult<(string, string, byte[]?)?>((title, subtitle, null));
        _nowPlayingTimer?.Stop();
        _ = RefreshWatched();
    }

    private void Watch(Func<Task<(string Title, string Subtitle, byte[]? Icon)?>> fetcher)
    {
        _watchedFetcher = fetcher;
        _nowPlayingTimer ??= CreateNowPlayingTimer();
        _nowPlayingTimer.Start(); // idempotent — resumes it if the pause button stopped it earlier
        PushPerk();
        _ = RefreshWatched();
    }

    // A one-shot cue the instant watching starts — ties Keeper's posture to
    // the same click that's about to start updating the label, purely
    // cosmetic like the rest of the activity-state work.
    private void PushPerk() => Keeper.Perk();

    // The pause button inside the label itself, not a fourth icon — stops
    // polling immediately and hides the label, rather than waiting for the
    // next 4s tick to notice "nothing" on its own.
    private void StopWatching()
    {
        StopCustomReminderDisplayTimer();
        _watchedFetcher = null;
        _nowPlayingTimer?.Stop();
        SpeechReader.Stop();
        StopColorPicking();
        Keeper.SetNowPlaying(null, null, null, false);
        ScheduleMemoryRelease();
    }

    // The label body is display-only; its separate pause and notifications
    // list controls remain available below.
    // The list button, notifications only — opens the full list instead
    // of launching just the one app that sent the latest notification.
    // Win+N is the standard shortcut for Windows' notification flyout on
    // both Windows 10 and 11 — simulating it is simpler and more robust
    // than trying to activate the flyout's own host process by AUMID.
    private void OpenNotificationFlyout()
    {
        KeyboardInput.SendChord(KeyboardInput.VK_LWIN, KeyboardInput.VK_N);
    }

    // Lists media sessions for viewing only. Selecting an entry starts
    // polling its title and artist; it never activates the other app.
    private void ShowAppPicker() => _ = ShowAppPickerAsync(placeAtSearch: false);

    private void ShowAppPickerFromSearch() => _ = ShowAppPickerAsync(placeAtSearch: true);

    private async Task ShowAppPickerAsync(bool placeAtSearch)
    {
        var menu = new ContextMenu();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in manager.GetSessions())
            {
                var aumid = session.SourceAppUserModelId;
                if (string.IsNullOrWhiteSpace(aumid) || !seen.Add(aumid)) continue;

                var label = aumid;
                try
                {
                    var appInfo = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
                    if (!string.IsNullOrWhiteSpace(appInfo?.DisplayInfo?.DisplayName)) label = appInfo.DisplayInfo.DisplayName;
                }
                catch { /* fall back to the raw AUMID */ }

                var playing = session.GetPlaybackInfo()?.PlaybackStatus
                    == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var itemText = playing ? $"▶ {label}" : label;
                menu.Items.Add(Item(itemText, () => WatchApp(new[] { aumid })));
            }
        }
        catch { /* leave the media-session list empty */ }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(Item("No media sessions found", null, enabled: false));
        }
        OpenMenu(menu, placeAtSearch);
    }

    private DispatcherTimer CreateNowPlayingTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += async (_, _) => await RefreshWatched();
        timer.Start();
        return timer;
    }

    private async Task RefreshWatched()
    {
        if (_watchedFetcher == null) return;
        var result = await _watchedFetcher();
        if (result is { } r) Keeper.SetNowPlaying(r.Title, r.Subtitle, KeeperView.DecodeIcon(r.Icon), _watchingNotifications);
        else Keeper.SetNowPlaying(null, null, null, false);
    }

    // A second, genuinely interactive window, invisible and sized to
    // roughly the character + its temperature/location text, sitting on
    // top of the (click-through) render window. It owns dragging and the
    // right-click/double-click menu directly via normal WPF mouse events,
    // since the render window never sees mouse input at all once it's
    // WS_EX_TRANSPARENT.
    private void CreateAvatarHitWindow()
    {
        _avatarWindow = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            // Alpha must be non-zero, or this layered window becomes fully
            // click-through at the OS/DWM level regardless of any hit-test
            // logic — WPF's Brushes.Transparent is alpha 0 and was silently
            // swallowing every click (confirmed via WindowFromPoint missing
            // this window entirely, even dead-centre). 1/255 is visually
            // invisible but non-zero, so the whole window stays hit-testable.
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0)),
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Topmost = _settings.AlwaysOnTop,
            Width = AvatarWidth,
            Height = AvatarHeight,
            Left = Left + AvatarOffsetX,
            Top = Top + AvatarOffsetY,
        };
        var hitSurface = new Canvas();
        _commandSearchBar = new CommandSearchBar(BuildQuickActions());
        _commandSearchBar.CommandRequested += action => action.Execute();
        _commandSearchBar.SuggestionsOpened += ReassertTopmost;
        Canvas.SetLeft(_commandSearchBar, 4);
        Canvas.SetTop(_commandSearchBar, CommandSearchTop);
        hitSurface.Children.Add(_commandSearchBar);
        _avatarWindow.Content = hitSurface;
        CreateUptimeHover(hitSurface);
        CreateConnectionHovers(hitSurface);
        _avatarWindow.PreviewMouseLeftButtonDown += AvatarWindow_MouseLeftButtonDown;
        _avatarWindow.MouseRightButtonUp += (_, e) =>
        {
            if (e.GetPosition(_avatarWindow).Y < CommandSearchTop) ShowContextMenuAtCursor();
        };
        _avatarWindow.LocationChanged += (_, _) =>
        {
            Left = _avatarWindow.Left - AvatarOffsetX;
            Top = _avatarWindow.Top - AvatarOffsetY;
            _dailyBubble?.PlaceBeside(this, HeadY);
        };
        ScreenCapture.SetExcluded(_avatarWindow, _settings.HideFromScreenCapture);
        _avatarWindow.Show();
    }

    // ---- Uptime sleep details ----

    // A transparent hover target in the avatar window, kept over the moon
    // Keeper draws beside the uptime (the render window takes no mouse
    // input). Hovering it shows how much of the uptime was sleep, on a
    // card in the daily bubble's colours (DailyBubbleWindow.xaml).
    private Border? _uptimeHover;
    private Popup? _uptimeTip;
    private int _uptimeTipVersion;

    private static readonly System.Windows.Media.Brush TipCard = Frozen(0x2a, 0x20, 0x19);
    private static readonly System.Windows.Media.Brush TipEdge = Frozen(0x8a, 0x6a, 0x2f);
    private static readonly System.Windows.Media.Brush TipText = Frozen(0xf3, 0xec, 0xdd);
    private static readonly System.Windows.Media.Brush TipMuted = Frozen(0xcb, 0xbf, 0xa8);
    private static readonly System.Windows.Media.Brush TipAccent = Frozen(0xe6, 0xc4, 0x76);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private Border? _networkHover, _bluetoothHover, _headphonesHover, _batteryHover, _acPowerHover;
    private Popup? _networkTip, _bluetoothTip, _headphonesTip, _batteryTip, _acPowerTip;

    private void CreateUptimeHover(Canvas hitSurface)
    {
        (_uptimeHover, _uptimeTip) = CreateStatusHover(hitSurface, FillUptimeTip);
        Keeper.UptimeIconMoved += PlaceUptimeHover;
        PlaceUptimeHover();
    }

    // The same kind of hover card for the battery and each connection icon
    // beside it.
    private void CreateConnectionHovers(Canvas hitSurface)
    {
        (_batteryHover, _batteryTip) = CreateStatusHover(hitSurface, FillBatteryTip);
        (_acPowerHover, _acPowerTip) = CreateStatusHover(hitSurface, FillBatteryTip);
        (_networkHover, _networkTip) = CreateStatusHover(hitSurface, FillNetworkTip);
        (_bluetoothHover, _bluetoothTip) = CreateStatusHover(hitSurface, FillBluetoothTip);
        (_headphonesHover, _headphonesTip) = CreateStatusHover(hitSurface, FillHeadphonesTip);
        Keeper.ConnectionIconsMoved += PlaceConnectionHovers;
        PlaceConnectionHovers();
    }

    private void PlaceConnectionHovers()
    {
        PlaceStatusHover(_networkHover, Keeper.NetworkIconBounds(this));
        PlaceStatusHover(_bluetoothHover, Keeper.BluetoothIconBounds(this));
        PlaceStatusHover(_headphonesHover, Keeper.HeadphonesIconBounds(this));
        PlaceStatusHover(_batteryHover, Keeper.BatteryIconBounds(this));
        PlaceStatusHover(_acPowerHover, Keeper.AcPowerIconBounds(this));
    }

    private (Border Hover, Popup Tip) CreateStatusHover(Canvas hitSurface, Action<Popup> fill)
    {
        var hover = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Visibility = Visibility.Collapsed,
        };
        // Opened by hand: the avatar window is never activated, and WPF's
        // own tooltip timing doesn't open tooltips for it. It opens beside
        // the widget, level with the icon, so Keeper's motion never sits
        // on top of it (WPF flips it right if there's no room on the left).
        var tip = new Popup
        {
            AllowsTransparency = true,
            PlacementTarget = hitSurface,
            Placement = PlacementMode.Left,
            PopupAnimation = PopupAnimation.Fade,
        };
        tip.Opened += (_, _) => RaiseAboveWidget(tip.Child);
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            fill(tip);
            tip.VerticalOffset = Canvas.GetTop(hover) - 10;
            tip.IsOpen = true;
        };
        hover.MouseEnter += (_, _) => delay.Start();
        hover.MouseLeave += (_, _) =>
        {
            delay.Stop();
            tip.IsOpen = false;
        };
        hitSurface.Children.Add(hover);
        return (hover, tip);
    }

    private void PlaceUptimeHover() => PlaceStatusHover(_uptimeHover, Keeper.UptimeSleepIconBounds(this));

    private static void PlaceStatusHover(Border? hover, Rect? bounds)
    {
        if (hover == null) return;
        if (bounds is not { } icon)
        {
            hover.Visibility = Visibility.Collapsed;
            return;
        }
        // A few pixels of slack around the small glyph; clipped to the top
        // of the avatar window, which starts a little below the widget's.
        const double pad = 3;
        var left = icon.Left - AvatarOffsetX - pad;
        var top = Math.Max(0, icon.Top - AvatarOffsetY - pad);
        var bottom = icon.Bottom - AvatarOffsetY + pad;
        if (bottom <= top)
        {
            hover.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(hover, left);
        Canvas.SetTop(hover, top);
        hover.Width = icon.Width + 2 * pad;
        hover.Height = bottom - top;
        hover.Visibility = Visibility.Visible;
    }

    // The totals at once, then the sleep periods and sign-in from Windows'
    // event log, read off the UI thread.
    private void FillUptimeTip(Popup tip)
    {
        var version = ++_uptimeTipVersion;
        var now = DateTime.Now;
        var up = WindowsStatus.Uptime();
        var asleep = WindowsStatus.Asleep(up) ?? TimeSpan.Zero;
        var heading = $"Includes {WindowsStatusWindow.Duration(asleep)} of sleep";
        var lines = new List<string>
        {
            $"Awake {WindowsStatusWindow.Duration(up - asleep)} of {WindowsStatusWindow.Duration(up)}",
            $"Windows started {WindowsStatusWindow.When(now - up)}",
        };
        const string hint = "Only a restart or a full shutdown resets uptime. Windows status shows each day.";
        tip.Child = UptimeCard(heading, lines, hint);

        _ = Task.Run(() => (WindowsStatus.ReadSleepPeriods(), WindowsStatus.ReadSignIns(), WindowsStatus.ReadLastStart())).ContinueWith(read =>
        {
            if (version != _uptimeTipVersion || read.IsFaulted) return;
            var (sleeps, signIns, start) = read.Result;
            var details = new List<string>(lines);
            var summary = heading;
            // "Includes 12 h 41 min hibernated and 1 h 1 min shut down", so a
            // Fast Startup shutdown isn't counted as sleep.
            if (sleeps != null)
            {
                var off = WindowsStatusWindow.OffKinds
                    .Select(k => (k.Word, Time: WindowsStatusWindow.AsleepWithin(sleeps.Where(p => k.Kinds.Contains(p.Kind)), now - up, now)))
                    .Where(k => k.Time > TimeSpan.Zero)
                    .Select(k => $"{WindowsStatusWindow.Duration(k.Time)} {k.Word}").ToList();
                if (off.Count > 0) summary = $"Includes {JoinAnd(off)}";
            }
            if (start is { } how) details[1] += $" · {WindowsStatusWindow.StartLabel(how)}";
            if (sleeps is { Count: > 0 })
            {
                var last = sleeps[^1];
                var what = last.Kind switch
                {
                    WindowsStatus.SleepKind.FastStartupShutdown => "Fast Startup shutdown",
                    WindowsStatus.SleepKind.FastStartupRestart => "Fast Startup restart → shutdown",
                    WindowsStatus.SleepKind.Hibernate => last.Cause is { } cause ? $"hibernated ({cause})" : "hibernated",
                    _ => "asleep",
                };
                details.Insert(0, $"{(sleeps.Count > 1 ? "Last " + what : char.ToUpper(what[0]) + what[1..])} " +
                    $"{WindowsStatusWindow.When(last.Start)} → {WindowsStatusWindow.When(last.End)} " +
                    $"({WindowsStatusWindow.Duration(last.End - last.Start)})");
                // "1 hibernation and 1 shutdown since Windows started": Fast
                // Startup shutdowns don't reset uptime, so they're counted too.
                if (sleeps.Count > 1)
                {
                    var counts = new (string Noun, WindowsStatus.SleepKind[] Kinds)[]
                        {
                            ("sleep", [WindowsStatus.SleepKind.Sleep]),
                            ("hibernation", [WindowsStatus.SleepKind.Hibernate]),
                            ("shutdown", [WindowsStatus.SleepKind.FastStartupShutdown, WindowsStatus.SleepKind.FastStartupRestart]),
                        }
                        .Select(k => (k.Noun, Count: sleeps.Count(p => k.Kinds.Contains(p.Kind))))
                        .Where(k => k.Count > 0)
                        .Select(k => $"{k.Count} {k.Noun}{(k.Count == 1 ? "" : "s")}").ToList();
                    details.Insert(1, $"{JoinAnd(counts)} since Windows started");
                }
            }
            if (signIns?.LastOrDefault(s => s.SignedOut == null) is { } session)
                details.Add($"Signed in {WindowsStatusWindow.WithDate(session.SignedIn)}");
            tip.Child = UptimeCard(summary, details, hint);
            if (tip.IsOpen) RaiseAboveWidget(tip.Child);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // "a", "a and b", "a, b and c".
    private static string JoinAnd(List<string> parts) =>
        parts.Count < 2 ? string.Concat(parts) : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";

    // The daily bubble's card: dark, gold-edged and rounded, a gold heading,
    // cream lines and a muted note. The margin leaves room for the shadow.
    private static Border UptimeCard(string heading, IEnumerable<string> lines, string hint)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = heading, Foreground = TipAccent, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var line in lines)
            body.Children.Add(new TextBlock { Text = line, Foreground = TipText, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
        body.Children.Add(new TextBlock
        {
            Text = hint, Foreground = TipMuted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        return new Border
        {
            Background = TipCard, BorderBrush = TipEdge, BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(10, 6, 12, 14), MaxWidth = 320,
            Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black, Direction = 270, ShadowDepth = 2, BlurRadius = 10, Opacity = 0.45,
            },
        };
    }

    // Mirrors #mediaBar's own flexbox layout in widget.html for whichever
    // icons are currently enabled — width/gap/anchor must stay in sync with
    // the CSS there. Computed fresh per click rather than cached, since
    // it's cheap and only needs to be correct at the moment of a click, not
    // continuously.
    private Dictionary<string, Rect> ComputeIconRects()
    {
        var visible = IconOrder.Where(k => _settings.EnabledIcons.Contains(k)).ToList();
        var contentWidth = visible.Count * IconWidth + Math.Max(0, visible.Count - 1) * IconGap;
        var margin = (IconBarWidth - contentWidth) / 2;

        var result = new Dictionary<string, Rect>();
        var x = margin;
        foreach (var key in visible)
        {
            result[key] = new Rect(x * Scale, IconTop * Scale, IconWidth * Scale, IconHeight * Scale);
            x += IconWidth + IconGap;
        }
        return result;
    }

    private void AvatarWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_avatarWindow);
        if (p.Y >= CommandSearchTop) return;
        if (_activityState == "prayer" && (PrayedBadgeRect.Contains(p) || PrayedHeadRect.Contains(p)))
        {
            MarkPrayed();
            return;
        }
        var icons = ComputeIconRects();

        if (icons.TryGetValue("youtube", out var youtubeRect) && youtubeRect.Contains(p))
        {
            WatchApp(YoutubeAumids);
            return;
        }
        if (icons.TryGetValue("spotify", out var spotifyRect) && spotifyRect.Contains(p))
        {
            WatchApp(SpotifyAumids);
            return;
        }
        if (icons.TryGetValue("bell", out var bellRect) && bellRect.Contains(p))
        {
            WatchNotifications();
            return;
        }
        if (icons.TryGetValue("search", out var searchRect) && searchRect.Contains(p))
        {
            ShowAppPicker();
            return;
        }
        // Still keyed "gpu" so saved icon choices carry over; the Graphics
        // tab there lists the GPUs this icon used to show.
        if (icons.TryGetValue("gpu", out var gpuRect) && gpuRect.Contains(p))
        {
            ShowWindowsStatus();
            return;
        }
        if (icons.TryGetValue("read", out var readRect) && readRect.Contains(p))
        {
            ReadSelection();
            return;
        }
        if (icons.TryGetValue("details", out var detailsRect) && detailsRect.Contains(p))
        {
            ShowSelectionDetails();
            return;
        }
        if (icons.TryGetValue("mic", out var micRect) && micRect.Contains(p))
        {
            OpenDictationCapture();
            return;
        }
        if (icons.TryGetValue("convert", out var convertRect) && convertRect.Contains(p))
        {
            ConvertSelection();
            return;
        }
        if (icons.TryGetValue("color", out var colorRect) && colorRect.Contains(p))
        {
            StartColorPicking();
            return;
        }
        if (icons.TryGetValue("qr", out var qrRect) && qrRect.Contains(p))
        {
            ShowQrCode();
            return;
        }
        if (icons.TryGetValue("volume", out var volumeRect) && volumeRect.Contains(p))
        {
            ToggleVolume();
            return;
        }
        if (icons.TryGetValue("clipboard", out var clipboardRect) && clipboardRect.Contains(p))
        {
            ShowClipboardHistory();
            return;
        }
        if (icons.TryGetValue("sudoku", out var sudokuRect) && sudokuRect.Contains(p))
        {
            OpenSudoku();
            return;
        }
        if (icons.TryGetValue("daily", out var dailyRect) && dailyRect.Contains(p))
        {
            ToggleDailyBubble();
            return;
        }
        if (icons.TryGetValue("stopwatch", out var stopwatchRect) && stopwatchRect.Contains(p))
        {
            ToggleStopwatch();
            return;
        }
        if (PauseIconRect.Contains(p) && _watchedFetcher != null)
        {
            StopWatching();
            return;
        }
        if (ListIconRect.Contains(p) && _watchedFetcher != null && _watchingNotifications)
        {
            OpenNotificationFlyout();
            return;
        }
        if (LabelBodyRect.Contains(p) && _watchedFetcher != null)
        {
            // This area displays metadata only; it does not activate its source app.
            return;
        }

        if (e.ClickCount >= 2)
        {
            ShowContextMenuAtCursor();
            return;
        }
        try { _avatarWindow!.DragMove(); } catch { /* button already released */ }
        SaveWindowPosition();
    }

    private WidgetSearchBadge StopwatchSearchBadge() => _stopwatch.IsRunning
        ? new WidgetSearchBadge("Pause", "#FF94620F")
        : _stopwatch.Elapsed > TimeSpan.Zero
            ? new WidgetSearchBadge("Stop", "#FF9A3737")
            : new WidgetSearchBadge("Start", "#FF237A48");

    private void ToggleStopwatch()
    {
        if (_stopwatch.IsRunning)
        {
            _stopwatch.Stop();
            _stopwatchTimer?.Stop();
            Keeper.SetStopwatch(_stopwatch.Elapsed);
            ShowOneShotLabel("Stopwatch paused at " + KeeperView.FormatElapsed(_stopwatch.Elapsed), "Click the stopwatch again to stop and reset it.");
        }
        else if (_stopwatch.Elapsed > TimeSpan.Zero)
        {
            _stopwatch.Reset();
            Keeper.SetStopwatch(null);
            ShowOneShotLabel("Stopwatch stopped", "Click the stopwatch to start it.");
        }
        else
        {
            if (_stopwatchTimer == null)
            {
                _stopwatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _stopwatchTimer.Tick += (_, _) => Keeper.SetStopwatch(_stopwatch.Elapsed);
            }
            _stopwatch.Start();
            _stopwatchTimer.Start();
            Keeper.SetStopwatch(TimeSpan.Zero);
            ShowOneShotLabel("Stopwatch started", "Click the stopwatch again to pause it.");
        }
    }

    private void ToggleDailyBubble()
    {
        if (_dailyBubble != null)
        {
            _dailyBubble.Close();
            return;
        }
        var bubble = new DailyBubbleWindow { Owner = this, Topmost = _settings.AlwaysOnTop };
        bubble.SizeChanged += (_, _) => bubble.PlaceBeside(this, HeadY);
        bubble.Closed += (_, _) => _dailyBubble = null;
        _dailyBubble = bubble;
        bubble.PlaceBeside(this, HeadY);
        ShowSensitive(bubble);
    }

    // Opens the game where it was left - or today's daily puzzle the first
    // time - or brings an open game to the front.
    private void OpenSudoku()
    {
        if (_sudoku != null)
        {
            if (_sudoku.WindowState == WindowState.Minimized) _sudoku.WindowState = WindowState.Normal;
            _sudoku.Activate();
            return;
        }
        _sudoku = new SudokuWindow();
        _sudoku.BackgroundWorkCompletedAfterClose += ScheduleMemoryRelease;
        _sudoku.Closed += (_, _) => { _sudoku = null; ScheduleMemoryRelease(); };
        _sudoku.Show();
        _sudoku.Activate();
    }

    private void ScheduleMemoryRelease()
    {
        _memoryRelease.Stop();
        _memoryRelease.Start();
    }

    private static void ReleaseMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private void ShowContextMenuAtCursor() => OpenMenu(BuildMenu(includeShowHide: false));

    // A window being marked "topmost" does not always mean it stays visually
    // and interactively above every other app — when another app's window
    // becomes the active/foreground window (which happens constantly if
    // that's whatever you're actually using), Windows can end up rendering
    // and hit-testing that foreground window above a topmost-but-inactive
    // one anyway, even over areas the foreground window doesn't visually
    // occupy. Re-asserting HWND_TOPMOST every couple of seconds — a
    // well-worn trick among "always on top" utility apps — wins the widget
    // back its spot instead of it silently going dead to clicks.
    private void SetupTopmostTimer()
    {
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _topmostTimer.Start();
        ReassertTopmost();
    }

    // Battery is local, synchronous info (see BatteryInfo.cs) — polled on a
    // slow timer purely because it can change (plugged in, unplugged) while
    // the widget sits there, not because reading it is expensive. Pushed to
    // the page the same way now-playing already is: PostWebMessageAsJson,
    // not a page reload.
    private void SetupBatteryTimer()
    {
        _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _batteryTimer.Tick += (_, _) =>
        {
            RefreshBattery();
            // Windows raises no event when only the signal strength changes,
            // so Wi-Fi and cellular bars are re-read with the battery.
            if (_network?.Bars != null) RefreshNetwork();
        };
        _batteryTimer.Start();
    }

    private void RefreshBattery()
    {
        var battery = BatteryInfo.Read();
        Keeper.SetBattery(battery);
        // The hover card's details stay readable to screen readers too.
        var card = PowerDetails.Read();
        if (battery != null) Keeper.SetBatteryDetails(PowerDetails.Spoken(card));
        // A desktop gets a plug in the battery's place, with the same card.
        Keeper.SetAcPower(battery == null && card.NoBattery ? PowerDetails.Spoken(card) : null);
    }

    // Read fresh each time the card opens: charge rate, time left, power
    // mode and battery health (PowerDetails.cs).
    private void FillBatteryTip(Popup tip)
    {
        var card = PowerDetails.Read();
        if (card.NoBattery) Keeper.SetAcPower(PowerDetails.Spoken(card));
        else Keeper.SetBatteryDetails(PowerDetails.Spoken(card));
        tip.Child = UptimeCard(card.Heading, card.Lines, "Windows status shows the power plan.");
        if (tip.IsOpen) RaiseAboveWidget(tip.Child);
    }

    // ---- Internet connection ----

    // Read at startup and again whenever Windows reports a change of
    // connection, cost or connectivity (NetworkStatusChanged).
    private NetworkStatus.State? _network;
    private int _networkReadVersion;

    private void SetupNetworkWatch()
    {
        try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged; }
        catch { /* Without the event the icon still shows the state at startup. */ }
        RefreshNetwork();
    }

    // Raised on a background thread.
    private void OnNetworkStatusChanged(object sender) =>
        Dispatcher.BeginInvoke(new Action(RefreshNetwork));

    private void RefreshNetwork()
    {
        var version = ++_networkReadVersion;
        _ = Task.Run(NetworkStatus.Read).ContinueWith(read =>
        {
            if (version != _networkReadVersion || read.IsFaulted || read.Result == _network) return;
            _network = read.Result;
            Keeper.SetNetwork(read.Result);
            if (_networkTip is { IsOpen: true } tip) FillNetworkTip(tip);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FillNetworkTip(Popup tip)
    {
        if (_network is not { } s) return;
        var heading = NetworkStatus.Title(s) + (s.Bars is int bars ? $" · {bars} of 4 bars" : "");
        var lines = new List<string>();
        if (s.Kind == NetworkStatus.Kind.Offline) lines.Add("No internet connection");
        else
        {
            lines.Add(s.LimitedReason switch
            {
                "limited internet" => "Limited internet (a sign-in page or restricted)",
                "no internet" => "Local network only, no internet",
                _ => "Internet access",
            });
            lines.Add(s.Category switch
            {
                NetworkStatus.Category.Public => "Public network: this PC is hidden from other devices",
                NetworkStatus.Category.Private => "Private network: other devices can find this PC",
                NetworkStatus.Category.Domain => "Work domain network",
                _ => "Network type unknown",
            });
            var metered = NetworkStatus.MeteredWords(s);
            lines.Add(char.ToUpper(metered[0]) + metered[1..] + (s.Metered ? " (Windows limits background data)" : ""));
        }
        tip.Child = UptimeCard(heading, lines, "Windows status shows each network adapter.");
        if (tip.IsOpen) RaiseAboveWidget(tip.Child);
    }

    // ---- Bluetooth and headphones ----

    // Windows' own radio, Bluetooth device and audio endpoint events drive
    // these (see DeviceStatus.cs); nothing is polled.
    private DeviceStatus? _devices;
    private DeviceStatus.BluetoothState? _bluetooth;
    private DeviceStatus.HeadphonesState? _headphones;
    private int _devicesReadVersion;
    private bool _devicesRefreshQueued;

    private void SetupDeviceWatch()
    {
        _devices = new DeviceStatus();
        _devices.Changed += OnDevicesChanged;
        _ = _devices.StartAsync();
    }

    // Raised on background threads, often several at once while Windows
    // lists the connected devices; one refresh covers a burst.
    private void OnDevicesChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_devicesRefreshQueued) return;
            _devicesRefreshQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _devicesRefreshQueued = false;
                RefreshDevices();
            }));
        }));
    }

    private void RefreshDevices()
    {
        if (_devices is not { } devices) return;
        var version = ++_devicesReadVersion;
        _ = Task.Run(() => (devices.ReadBluetooth(), devices.ReadHeadphones())).ContinueWith(read =>
        {
            if (version != _devicesReadVersion || read.IsFaulted || _devices == null) return;
            var (bluetooth, headphones) = read.Result;
            if (bluetooth != _bluetooth)
            {
                _bluetooth = bluetooth;
                Keeper.SetBluetooth(bluetooth);
                if (_bluetoothTip is { IsOpen: true } tip) FillBluetoothTip(tip);
            }
            if (headphones != _headphones)
            {
                _headphones = headphones;
                Keeper.SetHeadphones(headphones);
                if (_headphonesTip is { IsOpen: true } tip) FillHeadphonesTip(tip);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FillBluetoothTip(Popup tip)
    {
        if (_bluetooth is not { } s) return;
        var lines = new List<string>
        {
            s.Connected switch { 0 => "No devices connected", 1 => "1 device connected", var n => $"{n} devices connected" },
        };
        tip.Child = UptimeCard("Bluetooth on", lines, "Windows' Bluetooth settings show the device names.");
        if (tip.IsOpen) RaiseAboveWidget(tip.Child);
    }

    private void FillHeadphonesTip(Popup tip)
    {
        if (_headphones is not { } s) return;
        var lines = new List<string>
        {
            s.Bluetooth ? "Connected over Bluetooth" : "Wired or USB",
            "Sound plays through them (the default playback device)",
        };
        if (s.Kind == DeviceStatus.AudioKind.Headset) lines.Insert(1, "With a microphone");
        tip.Child = UptimeCard(s.Kind == DeviceStatus.AudioKind.Headset ? "Headset connected" : "Headphones connected",
            lines, "Windows' sound settings show the device name.");
        if (tip.IsOpen) RaiseAboveWidget(tip.Child);
    }

    // Purely cosmetic and purely local — checks Windows' own system-wide
    // idle counter (see IdleDetection.cs) on a fast-ish timer, same shape
    // as the battery timer above just quicker (matches the existing
    // foreground-tracking timer's 400ms — a local GetLastInputInfo call is
    // just as cheap), so the visible workout countdown ticks smoothly.
    // Nothing is "listening" for input; this just asks, often, how long
    // it's been. Every icon still works exactly the same in any state.
    private void SetupIdleTimer()
    {
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) => UpdateActivityState();
        _idleTimer.Start();
    }

    private void SetupCustomReminderTimer()
    {
        var now = DateTime.Now;
        _customReminderLastChecked.Clear();
        foreach (var reminder in _settings.CustomReminders)
            _customReminderLastChecked[reminder] = now;

        _customReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _customReminderTimer.Tick += CustomReminderTimer_Tick;
        _customReminderTimer.Start();
    }

    private void SetupPrayerTimer()
    {
        _lastPrayerCheckUtc = DateTimeOffset.UtcNow;
        _prayerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _prayerTimer.Tick += PrayerTimer_Tick;
        _prayerTimer.Start();
    }

    private void PrayerTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _lastPrayerCheckUtc;
        _lastPrayerCheckUtc = now;
        // The azan and the prayer look are separate opt-ins at the same moment.
        var azan = _settings.PrayerAzanEnabled && LocalAudioPlayer.IsPlayable(_settings.PrayerAzanAudioPath);
        var look = _settings.PrayerLookEnabled;
        if ((!azan && !look) ||
            now - previous > TimeSpan.FromMinutes(2) || previous > now) return;

        // Consider adjacent dates because a location can be in another time
        // zone. UTC instants avoid duplicate playback during DST changes.
        var locationDate = PrayerTimeZone.Today(_settings.LocationTimeZoneId);
        for (var day = -2; day <= 2; day++)
        {
            foreach (var prayer in PrayerTimesCalculator.ForDate(locationDate.AddDays(day),
                         _settings.Latitude, _settings.Longitude,
                         _settings.PrayerCalculationMethod, _settings.PrayerAsrMethod))
            {
                if (prayer.StartsAtUtc <= previous || prayer.StartsAtUtc > now ||
                    !_playedPrayerInstants.Add(prayer.StartsAtUtc.UtcTicks)) continue;
                if (look) StartPrayerLook();
                var playing = false;
                if (azan)
                {
                    _reminderAudioPlayer?.Stop();
                    _reminderSpeechAfterAudio = null;
                    SpeechReader.Stop();
                    if (_azanPlayer == null)
                    {
                        _azanPlayer = new LocalAudioPlayer();
                        _azanPlayer.Finished += ResumeVoiceAfterAudio;
                    }
                    playing = _azanPlayer.TryPlay(_settings.PrayerAzanAudioPath);
                    if (playing) PauseVoiceForAudio();
                }
                if (playing || look) ShowOneShotLabel($"{prayer.Name} prayer time", _settings.City);
                break;
            }
        }
        _playedPrayerInstants.RemoveWhere(ticks => ticks < now.AddDays(-2).UtcTicks);
    }

    // A custom reminder's cardigan and label give way to the prayer look,
    // and a stretch break in progress ends: the prayer counts as the break.
    private void StartPrayerLook()
    {
        StopCustomReminderDisplayTimer();
        _workoutCountdownEnd = null;
        _prayerLookActive = true;
        UpdateActivityState();
    }

    // From the check badge, a click on the head or "I've prayed" in the menu:
    // back to the everyday look, with a small nod of acknowledgement.
    private void MarkPrayed()
    {
        if (!_prayerLookActive) return;
        _prayerLookActive = false;
        _activeStreakStart = DateTime.Now; // the next work stretch starts fresh
        UpdateActivityState();
        Keeper.Perk();
    }

    // Nothing listens while the azan plays: a hold in progress is dropped
    // and a new one waits until it ends.
    private bool CloCloSoundPlaying => _azanPlayer?.IsPlaying == true;

    private void PauseVoiceForAudio()
    {
        if (_voicePausedForAudio) return;
        _voicePausedForAudio = true;
        AbortPushToTalk();
    }

    private void ResumeVoiceAfterAudio()
    {
        if (!_voicePausedForAudio || CloCloSoundPlaying) return;
        _voicePausedForAudio = false;
    }

    private void CustomReminderTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var reminder in _settings.CustomReminders)
        {
            if (!reminder.Enabled) continue;
            if (_activityState != "awake")
            {
                _customReminderLastChecked[reminder] = now;
                continue;
            }
            if (!IsWithinReminderHours(reminder, now))
            {
                _customReminderLastChecked[reminder] = now;
                continue;
            }

            if (!_customReminderLastChecked.TryGetValue(reminder, out var lastChecked))
                lastChecked = _customReminderLastChecked[reminder] = now;
            if (now - lastChecked < TimeSpan.FromMinutes(reminder.IntervalMinutes)) continue;

            // Deliver at most one per minute so simultaneous schedules are
            // spaced out rather than replacing one another's visible label.
            _customReminderLastChecked[reminder] = now;
            ShowCustomReminder(reminder.Message);
            PlayCustomReminderAudio(reminder);
            break;
        }
    }

    private void PlayCustomReminderAudio(CustomReminderSettings reminder)
    {
        _reminderAudioPlayer?.Stop();
        _reminderSpeechAfterAudio = null;
        // Prayer audio takes priority if the two schedules happen to coincide.
        if (_azanPlayer?.IsPlaying == true) return;
        if (reminder.PlayAudio)
        {
            if (_reminderAudioPlayer == null)
            {
                _reminderAudioPlayer = new LocalAudioPlayer();
                _reminderAudioPlayer.Finished += ReminderAudioFinished;
            }
            _reminderSpeechAfterAudio = reminder.Speak ? reminder.Message : null;
            if (_reminderAudioPlayer.TryPlay(reminder.AudioPath)) return;
            _reminderSpeechAfterAudio = null;
        }
        if (reminder.Speak) SpeechReader.Speak(reminder.Message);
    }

    // Reminders play in CloClo's one audio session and keep the microphone
    // open: voice listening carries on, and echo cancellation keeps CloClo
    // from hearing its own sound.
    private void ReminderAudioFinished()
    {
        var message = _reminderSpeechAfterAudio;
        _reminderSpeechAfterAudio = null;
        if (message != null && !_voiceActivationClosing && _azanPlayer?.IsPlaying != true)
            SpeechReader.Speak(message);
    }

    private void ShowCustomReminder(string message)
    {
        _resumeWatchAfterCustomReminder = _nowPlayingTimer?.IsEnabled == true;
        _nowPlayingTimer?.Stop();
        Keeper.SetCustomReminderActive(true);
        Keeper.SetNowPlaying(message, "Custom reminder", null, false);
        _customReminderDisplayTimer ??= new DispatcherTimer();
        _customReminderDisplayTimer.Stop();
        _customReminderDisplayTimer.Interval = TimeSpan.FromSeconds(10);
        _customReminderDisplayTimer.Tick -= CustomReminderDisplayTimer_Tick;
        _customReminderDisplayTimer.Tick += CustomReminderDisplayTimer_Tick;
        _customReminderDisplayTimer.Start();
    }

    private async void CustomReminderDisplayTimer_Tick(object? sender, EventArgs e)
    {
        var resumeWatch = _resumeWatchAfterCustomReminder;
        StopCustomReminderDisplayTimer();
        await RefreshWatched();
        if (resumeWatch) _nowPlayingTimer?.Start();
    }

    private void StopCustomReminderDisplayTimer()
    {
        Keeper.SetCustomReminderActive(false);
        if (_customReminderDisplayTimer == null) return;
        _customReminderDisplayTimer.Stop();
        _customReminderDisplayTimer.Tick -= CustomReminderDisplayTimer_Tick;
        _customReminderDisplayTimer = null;
        _resumeWatchAfterCustomReminder = false;
    }

    private static bool IsWithinReminderHours(CustomReminderSettings reminder, DateTime now) =>
        reminder.StartHour < reminder.EndHour
            ? now.Hour >= reminder.StartHour && now.Hour < reminder.EndHour
            : now.Hour >= reminder.StartHour || now.Hour < reminder.EndHour;

    // Four states. Prayer comes first, then sleep takes priority over the
    // workout nudge (if you've actually stepped away, you don't also need a
    // "take a break" pose):
    // - prayer: from a prayer time, with the prayer look on, until you mark
    //   it prayed, whether or not you're at the PC. The next prayer time
    //   simply continues it. Custom reminders wait meanwhile (they only
    //   fire while awake).
    // - asleep: idle for at least SleepAfterMinutes.
    // - workout: it's been at least WorkoutAfterMinutes since the last real
    //   break (an idle stretch of BreakThreshold+, before the pose ever
    //   triggered). Once showing, it runs its own BreakThreshold-length
    //   countdown to completion regardless of further activity — a visible,
    //   predictable timer beats silently waiting to detect a real pause.
    // - awake: neither.
    private void UpdateActivityState()
    {
        if (_prayerLookActive)
        {
            if (_settings.PrayerLookEnabled)
            {
                SetActivityState("prayer");
                return;
            }
            _prayerLookActive = false;
            _activeStreakStart = DateTime.Now; // the next work stretch starts fresh
        }

        var idle = IdleDetection.TimeSinceLastInput();
        var sleepThreshold = _settings.SleepAfterMinutes;
        var isAsleep = sleepThreshold > 0 && idle >= TimeSpan.FromMinutes(sleepThreshold);

        if (isAsleep)
        {
            _workoutCountdownEnd = null;
            SetActivityState("asleep");
            return;
        }

        if (_activityState == "workout")
        {
            if (DateTime.Now >= _workoutCountdownEnd)
            {
                _activeStreakStart = DateTime.Now; // next work stretch starts fresh
                _workoutCountdownEnd = null;
                SetActivityState("awake");
            }
            else
            {
                PushActivityState(); // same state — just ticks the countdown
            }
            return;
        }

        if (idle >= BreakThreshold) _activeStreakStart = DateTime.Now;

        var workoutThreshold = _settings.WorkoutAfterMinutes;
        if (workoutThreshold > 0 && DateTime.Now - _activeStreakStart >= TimeSpan.FromMinutes(workoutThreshold))
        {
            _workoutCountdownEnd = DateTime.Now + BreakThreshold;
            SetActivityState("workout");
            SpeakWorkoutReminder();
            return;
        }

        SetActivityState("awake");
    }

    // Said once as the stretch-break pose starts, in CloClo's audio session
    // with the microphone left open. The azan takes priority.
    private void SpeakWorkoutReminder()
    {
        if (_voiceActivationClosing || _azanPlayer?.IsPlaying == true) return;
        var arabic = _settings.VoiceCommandsInArabic && SpeechReader.IsVoiceAvailable(ReadAloudVoice.ArabicHoda);
        SpeechReader.Speak(arabic ? "وقت استراحة قصيرة. قوم اتمطع شوية." : "Time for a short stretch break.",
            selectedVoice: arabic ? ReadAloudVoice.ArabicHoda : null);
    }

    private void SetActivityState(string state)
    {
        if (state == _activityState) return;
        _activityState = state;
        PushActivityState();
    }

    private void PushActivityState()
    {
        var secondsLeft = _workoutCountdownEnd is { } end
            ? Math.Max(0, (int)Math.Ceiling((end - DateTime.Now).TotalSeconds))
            : 0;
        Keeper.SetActivity(_activityState, secondsLeft);
    }

    private void SetupForegroundTracking()
    {
        var ownProcessId = Environment.ProcessId;
        _foregroundTrackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _foregroundTrackTimer.Tick += (_, _) =>
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != ownProcessId) _lastExternalForegroundWindow = hwnd;
        };
        _foregroundTrackTimer.Start();
    }

    private void ReassertTopmost()
    {
        if (!_settings.AlwaysOnTop) return;
        // Context menus (including their submenus) have separate popup HWNDs.
        // Reasserting the widget while one is open would cover the menu.
        if (_activeContextMenu?.IsOpen == true) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        // Reasserted second so it lands in front of the (also topmost) render
        // window — it's the one that actually needs to catch the click.
        if (_avatarWindow != null)
        {
            var avatarHwnd = new WindowInteropHelper(_avatarWindow).Handle;
            if (avatarHwnd != IntPtr.Zero)
            {
                SetWindowPos(avatarHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
        }
        // Popup uses its own HWND. Put it last so the interactive avatar
        // window does not cover suggestions on the next topmost refresh.
        var suggestionsHwnd = _commandSearchBar?.SuggestionsWindowHandle ?? IntPtr.Zero;
        if (suggestionsHwnd != IntPtr.Zero)
            SetWindowPos(suggestionsHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        if (_uptimeTip is { IsOpen: true, Child: { } tipCard }) RaiseAboveWidget(tipCard);
        foreach (var connectionTip in new[] { _networkTip, _bluetoothTip, _headphonesTip })
            if (connectionTip is { IsOpen: true, Child: { } connectionCard }) RaiseAboveWidget(connectionCard);
    }

    // A popup has its own HWND; put it in front of the widget's windows.
    private void RaiseAboveWidget(System.Windows.Media.Visual popupContent)
    {
        if (PresentationSource.FromVisual(popupContent) is HwndSource { Handle: var popupHwnd } && popupHwnd != IntPtr.Zero)
            SetWindowPos(popupHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    private void ApplyDisplaySettings()
    {
        Keeper.SetPlace(_settings.City);
        Keeper.SetAvatar(_settings.Avatar);
        Keeper.SetOutfit(CurrentOutfit);
        Keeper.SetMotionMode(_settings.MotionMode);
        Keeper.SetSkyOverride(_settings.Sky);
        Keeper.SetEnabledIcons(IconOrder.Where(k => _settings.EnabledIcons.Contains(k)));
        Keeper.SetShowUptime(_settings.ShowUptime);
        Keeper.SetShowNetwork(_settings.ShowNetwork);
        Keeper.SetShowDevices(_settings.ShowDevices);
    }

    private void PlaceWindow()
    {
        // Only trusts the saved spot if it's still actually on a connected
        // monitor — a saved position from a second screen that's since been
        // unplugged (or any other monitor-configuration change) would
        // otherwise place the widget in space nothing can ever show, with
        // no way to get it back short of editing settings.json by hand.
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top && IsOnAnyScreen(left, top))
        {
            MoveTo(left, top);
            return;
        }
        MoveToDefaultCorner();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised on a system thread, and the new monitor metrics can trail the
        // event by a moment, so hop onto the UI thread and let the pending
        // layout work settle before measuring anything.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(EnsureOnScreen));
    }

    // Unplugging a monitor (or undocking) leaves this window where it was:
    // Windows rescues ordinary app windows off a vanished display, but not a
    // layered, click-through tool window like this one. PlaceWindow only ran
    // at startup, so nothing noticed — and Closing then saved the stranded
    // spot, carrying it into the next launch too. Re-check on every desktop
    // layout change instead.
    private void EnsureOnScreen()
    {
        if (IsOnAnyScreen(Left, Top)) return;
        MoveToDefaultCorner();
        SaveWindowPosition();
    }

    private void MoveToDefaultCorner()
    {
        // Default spot: bottom-right corner, just above the taskbar — as close
        // to "on top of the clock" as Windows actually allows an app to sit.
        var area = SystemParameters.WorkArea;
        MoveTo(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    // The avatar window is the interactive twin, and normally it drags this
    // one around (see its LocationChanged). Moving this window on its own
    // would leave the pair separated, so carry the avatar along.
    private void MoveTo(double left, double top)
    {
        Left = left;
        Top = top;
        if (_avatarWindow != null)
        {
            _avatarWindow.Left = left + AvatarOffsetX;
            _avatarWindow.Top = top + AvatarOffsetY;
        }
    }

    private bool IsOnAnyScreen(double left, double top)
    {
        // Left/Top are WPF device-independent units; Screen bounds are physical
        // pixels. At anything other than 100% scaling the two disagree, and the
        // gap is exactly where a stranded widget hides: at 125% on a 1920-wide
        // screen, logical x runs out at 1536, yet comparing it raw against the
        // physical bounds accepted anything below 1920.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var rect = new Int32Rect(
            (int)Math.Round(left * dpi.DpiScaleX),
            (int)Math.Round(top * dpi.DpiScaleY),
            (int)Math.Round(Width * dpi.DpiScaleX),
            (int)Math.Round(Height * dpi.DpiScaleY));
        // One corner grazing a screen doesn't make the widget usable, so ask
        // for a real patch of it to be visible, and against the working area
        // so a spot buried under the taskbar doesn't count either.
        return NativeScreen.WorkingAreas().Any(area =>
        {
            var width = Math.Min(area.X + area.Width, rect.X + rect.Width) - Math.Max(area.X, rect.X);
            var height = Math.Min(area.Y + area.Height, rect.Y + rect.Height) - Math.Max(area.Y, rect.Y);
            return width >= MinVisiblePixels && height >= MinVisiblePixels;
        });
    }

    private void SaveWindowPosition()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    private void SetupTrayIcon()
    {
        // The exe's own embedded icon (ApplicationIcon in the csproj) - always
        // there, in exactly the build that's running.
        _trayIcon = new TrayIcon("CloClo weather");
        _trayIcon.DoubleClick += ToggleVisible;
        _trayIcon.RightClick += () =>
        {
            // Our window has to be in front for the menu to close when you
            // click anywhere else (Windows lets the icon's owner do this).
            SetForegroundWindow(_trayIcon.Handle);
            OpenMenu(BuildMenu(includeShowHide: true));
        };
        System.Windows.Application.Current.Exit += (_, _) => _trayIcon?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    // The right-click menu, for both the widget and the tray icon - rebuilt
    // each time it opens, so every tick shows the current setting. It opens
    // at the mouse pointer.
    private ContextMenu BuildMenu(bool includeShowHide)
    {
        var menu = new ContextMenu();
        if (includeShowHide)
            menu.Items.Add(Item(Visibility == Visibility.Visible ? "Hide widget" : "Show widget", ToggleVisible));
        if (_activityState == "prayer")
        {
            menu.Items.Add(Item("I've prayed", MarkPrayed));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Set location…", OpenLocationDialog));
        menu.Items.Add(BuildAvatarMenu());
        menu.Items.Add(BuildSkyMenu());
        menu.Items.Add(BuildIconsMenu());
        menu.Items.Add(BuildMotionMenu());
        menu.Items.Add(BuildSleepMenu());
        menu.Items.Add(BuildWorkoutMenu());
        menu.Items.Add(Item("Custom reminders…", OpenCustomReminders));
        menu.Items.Add(Item("Prayer times and azan…", OpenPrayerTimes));
        menu.Items.Add(Item("Windows status…", ShowWindowsStatus));
        menu.Items.Add(Item("Show PC uptime", ToggleShowUptime, isChecked: _settings.ShowUptime));
        menu.Items.Add(Item("Show internet connection", ToggleShowNetwork, isChecked: _settings.ShowNetwork));
        menu.Items.Add(Item("Show Bluetooth and headphones", ToggleShowDevices, isChecked: _settings.ShowDevices));
        menu.Items.Add(Item("File summary and search…", ShowFileInsights));
        menu.Items.Add(Item("Scientific calculator…", ShowScientificCalculator));
        menu.Items.Add(Item("Sound pattern test…", ShowSoundPatternTest));
        menu.Items.Add(Item("Voice signals…", ShowVoiceSignals));
        menu.Items.Add(Item("Find it in David's words…", ShowDavidMatch));
        menu.Items.Add(Item("Voice converter…", ShowVoiceConverter));
        menu.Items.Add(Item("Letter signals…", ShowLetterSignals));
        menu.Items.Add(Item("Refresh weather", () => _ = RefreshWeatherAsync()));
        menu.Items.Add(Item("Start new dictation file", StartNewDictationFile));
        menu.Items.Add(Item("Open dictation folder", OpenDictationFolder));
        menu.Items.Add(BuildPrivacyMenu());
        menu.Items.Add(Item("Always on top", ToggleAlwaysOnTop, isChecked: _settings.AlwaysOnTop));
        menu.Items.Add(Item("Start with Windows", ToggleStartWithWindows, isChecked: StartupRegistration.IsEnabled()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", () => System.Windows.Application.Current.Shutdown()));
        return menu;
    }

    private void OpenMenu(ContextMenu menu, bool placeAtSearch = false)
    {
        if (placeAtSearch && _commandSearchBar != null)
        {
            menu.PlacementTarget = _commandSearchBar;
            menu.Placement = PlacementMode.Right;
        }
        else menu.Placement = PlacementMode.MousePoint;
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu)) _activeContextMenu = null;
        };
        menu.IsOpen = true;
    }

    // A menu entry. Underscores are doubled so WPF shows them instead of
    // taking them as keyboard-shortcut markers (app titles can have them).
    private static MenuItem Item(string header, Action? onClick, bool isChecked = false, bool enabled = true, UIElement? icon = null)
    {
        var item = new MenuItem { Header = header.Replace("_", "__"), IsChecked = isChecked, IsEnabled = enabled, Icon = icon };
        if (onClick != null) item.Click += (_, _) => onClick();
        return item;
    }

    private static MenuItem SubMenu(string header, IEnumerable<object> items)
    {
        var menu = new MenuItem { Header = header };
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    private MenuItem BuildSkyMenu() =>
        SubMenu("Weather", SkyOptions.Select(opt => Item(opt.Label, () => SetSky(opt.Value), isChecked: _settings.Sky == opt.Value)));

    private static readonly (string Value, string Label)[] MotionOptions =
    {
        ("full", "Full motion"),
        ("gentle", "Gentle motion"),
        ("still", "Reduced motion / still"),
    };

    private MenuItem BuildMotionMenu() =>
        SubMenu("Motion", MotionOptions.Select(opt => Item(opt.Label, () => SetMotionMode(opt.Value), isChecked: _settings.MotionMode == opt.Value)));

    private void SetMotionMode(string mode)
    {
        _settings.MotionMode = mode;
        _settings.Save();
        Keeper.SetMotionMode(mode);
    }

    private static readonly (string Value, string Label)[] AvatarOptions =
    {
        ("keeper", "Keeper"),
        ("lady", "Lady Keeper"),
    };

    private MenuItem BuildAvatarMenu()
    {
        var items = new List<object>();
        foreach (var opt in AvatarOptions)
        {
            items.Add(Item(opt.Label, () => SetAvatar(opt.Value), isChecked: _settings.Avatar == opt.Value));
        }

        // The colours of the avatar shown now; each avatar keeps its own.
        var name = AvatarOptions.First(o => o.Value == (_settings.Avatar == "lady" ? "lady" : "keeper")).Label;
        items.Add(new Separator());
        items.Add(SubMenu($"Outfit colour ({name})", KeeperView.Outfits.Select(outfit => Item(outfit.Label, () => SetOutfit(outfit.Key),
            isChecked: CurrentOutfit == outfit.Key, icon: Swatch(outfit.Top)))));
        return SubMenu("Avatar", items);
    }

    private void SetAvatar(string avatar)
    {
        _settings.Avatar = avatar;
        _settings.Save();
        Keeper.SetAvatar(avatar);
        Keeper.SetOutfit(CurrentOutfit);
    }

    private void SetOutfit(string outfit)
    {
        CurrentOutfit = outfit;
        _settings.Save();
        Keeper.SetOutfit(outfit);
    }

    private string CurrentOutfit
    {
        get => _settings.Avatar == "lady" ? _settings.LadyOutfit : _settings.KeeperOutfit;
        set
        {
            if (_settings.Avatar == "lady") _settings.LadyOutfit = value;
            else _settings.KeeperOutfit = value;
        }
    }

    // A small colour square for a menu item.
    private static UIElement Swatch(System.Windows.Media.Color c) => new System.Windows.Shapes.Rectangle
    {
        Width = 12, Height = 12,
        Fill = new System.Windows.Media.SolidColorBrush(c),
        Stroke = System.Windows.Media.Brushes.DimGray, StrokeThickness = 1,
    };

    private static readonly (int Minutes, string Label)[] SleepOptions =
    {
        (2, "2 minutes"),
        (5, "5 minutes"),
        (15, "15 minutes"),
        (0, "Never"),
    };

    private MenuItem BuildSleepMenu() =>
        SubMenu("Sleep after", SleepOptions.Select(opt => Item(opt.Label, () => SetSleepAfter(opt.Minutes),
            isChecked: _settings.SleepAfterMinutes == opt.Minutes)));

    private void SetSleepAfter(int minutes)
    {
        _settings.SleepAfterMinutes = minutes;
        _settings.Save();
        UpdateActivityState(); // e.g. picking "Never" wakes it right away
    }

    private static readonly (int Minutes, string Label)[] WorkoutOptions =
    {
        (0, "Off"),
        (15, "15 minutes"),
        (30, "30 minutes"),
        (45, "45 minutes"),
        (60, "1 hour"),
    };

    private MenuItem BuildWorkoutMenu() =>
        SubMenu("Workout reminder", WorkoutOptions.Select(opt => Item(opt.Label, () => SetWorkoutAfter(opt.Minutes),
            isChecked: _settings.WorkoutAfterMinutes == opt.Minutes)));

    private void SetWorkoutAfter(int minutes)
    {
        _settings.WorkoutAfterMinutes = minutes;
        _settings.Save();
        _activeStreakStart = DateTime.Now; // start the new threshold's countdown fresh, not mid-streak
        UpdateActivityState();
    }

    private void OpenCustomReminders()
    {
        var dialog = new CustomRemindersWindow(_settings.CustomReminders)
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        PositionBesideWidget(dialog);

        // Keep the widget's foreground recovery timer from repeatedly
        // reordering it while its owned dialog (or the editor it opens) is
        // active. The avatar hit window covers only the widget itself; the
        // reminder window is placed beside that area.
        var resumeTopmostTimer = _topmostTimer?.IsEnabled == true;
        _topmostTimer?.Stop();
        bool? saved = null;
        try
        {
            saved = dialog.ShowDialog();
        }
        finally
        {
            if (resumeTopmostTimer)
            {
                _topmostTimer?.Start();
                ReassertTopmost();
            }
        }

        if (saved != true) return;
        _reminderAudioPlayer?.Stop();
        _reminderSpeechAfterAudio = null;
        _settings.CustomReminders = dialog.Reminders.ToList();
        _settings.Save();
        var now = DateTime.Now;
        _customReminderLastChecked.Clear();
        foreach (var reminder in _settings.CustomReminders)
            _customReminderLastChecked[reminder] = now;
    }

    private void OpenPrayerTimes()
    {
        var dialog = new PrayerTimesWindow(_settings)
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        PositionBesideWidget(dialog);
        var resumeTopmostTimer = _topmostTimer?.IsEnabled == true;
        _topmostTimer?.Stop();
        bool? saved;
        try { saved = ShowToolDialog(dialog); }
        finally
        {
            if (resumeTopmostTimer)
            {
                _topmostTimer?.Start();
                ReassertTopmost();
            }
        }
        if (saved != true) return;
        _settings.PrayerCalculationMethod = dialog.CalculationMethod;
        _settings.PrayerAsrMethod = dialog.AsrMethod;
        _settings.PrayerAzanAudioPath = dialog.AudioPath;
        _settings.PrayerAzanEnabled = dialog.AzanEnabled;
        _settings.PrayerLookEnabled = dialog.PrayerLookEnabled;
        _settings.Save();
        UpdateActivityState(); // turning the prayer look off ends one in progress
        _lastPrayerCheckUtc = DateTimeOffset.UtcNow;
        _playedPrayerInstants.Clear();
        _azanPlayer?.Stop();
        ResumeVoiceAfterAudio();
        if (!_settings.PrayerAzanEnabled)
        {
            _azanPlayer?.Dispose();
            _azanPlayer = null;
        }
    }

    private void PositionBesideWidget(Window dialog)
    {
        var workAreas = NativeScreen.WorkingAreas();
        if (workAreas.Count == 0) return;

        var screenOrigin = PointToScreen(new Point(0, 0));
        var screenBottomRight = PointToScreen(new Point(Width, Height));
        var widgetLeft = screenOrigin.X;
        var widgetTop = screenOrigin.Y;
        var widgetRight = screenBottomRight.X;
        var widgetBottom = screenBottomRight.Y;
        var widgetCenterY = (widgetTop + widgetBottom) / 2;
        var area = workAreas
            .OrderByDescending(candidate =>
            {
                var overlapWidth = Math.Max(0, Math.Min(widgetRight, candidate.X + candidate.Width) - Math.Max(widgetLeft, candidate.X));
                var overlapHeight = Math.Max(0, Math.Min(widgetBottom, candidate.Y + candidate.Height) - Math.Max(widgetTop, candidate.Y));
                return overlapWidth * overlapHeight;
            })
            .First();

        var dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var dialogWidth = dialog.Width * dpiScale.DpiScaleX;
        var dialogHeight = dialog.Height * dpiScale.DpiScaleY;
        const double gap = 12;
        var rightSpace = area.X + area.Width - widgetRight;
        var leftSpace = widgetLeft - area.X;

        var placeRight = rightSpace >= dialogWidth + gap || rightSpace >= leftSpace;
        var targetLeft = placeRight ? widgetRight + gap : widgetLeft - dialogWidth - gap;
        var targetTop = widgetCenterY - dialogHeight / 2;
        targetLeft = Math.Clamp(targetLeft, area.X, Math.Max(area.X, area.X + area.Width - dialogWidth));
        targetTop = Math.Clamp(targetTop, area.Y, Math.Max(area.Y, area.Y + area.Height - dialogHeight));

        // Convert the physical screen offset back to WPF's device-independent
        // coordinates using the widget's per-monitor scale.
        dialog.Left = Left + (targetLeft - widgetLeft) / dpiScale.DpiScaleX;
        dialog.Top = Top + (targetTop - widgetTop) / dpiScale.DpiScaleY;
    }

    // Display names for the same keys ComputeIconRects/IconOrder use —
    // separate from that array since menu labels need to be human-readable
    // and IconOrder needs to stay a plain ordered key list.
    private static readonly (string Key, string Label)[] IconOptions =
    {
        ("youtube", "YouTube"),
        ("spotify", "Spotify"),
        ("bell", "Notifications"),
        ("search", "Search other apps"),
        ("gpu", "Windows status"),
        ("read", "Read selection aloud"),
        ("details", "Selection details"),
        ("mic", "Dictate"),
        ("convert", "Convert units"),
        ("color", "Color picker"),
        ("qr", "QR code from selection"),
        ("volume", "Volume/mute toggle"),
        ("clipboard", "Clipboard history"),
        ("stopwatch", "Stopwatch"),
        ("daily", "Daily quote, word, joke & fact"),
        ("sudoku", "Layer Sudoku (daily game)"),
    };

    private MenuItem BuildIconsMenu() =>
        SubMenu("Icons", IconOptions.Select(opt => Item(opt.Label, () => ToggleIcon(opt.Key), isChecked: _settings.EnabledIcons.Contains(opt.Key))));

    // The media bar only fits about MaxVisibleIcons at once (see
    // ComputeIconRects) — enabling one more than that would silently
    // overflow past the window's edge, the same clipping bug class hit
    // (and fixed) repeatedly earlier for the fixed 6/7-icon layouts, so
    // this refuses instead of letting the count grow unbounded.
    private void ToggleIcon(string key)
    {
        if (_settings.EnabledIcons.Contains(key))
        {
            _settings.EnabledIcons.Remove(key);
        }
        else
        {
            if (_settings.EnabledIcons.Count >= MaxVisibleIcons)
            {
                ShowOneShotLabel("Icon bar is full", "Turn off another icon first to add this one.");
                return;
            }
            _settings.EnabledIcons.Add(key);
        }
        _settings.Save();
        ApplyDisplaySettings();
    }

    private MenuItem BuildPrivacyMenu() =>
        SubMenu("Privacy", new object[]
        {
            Item("Hide from screen sharing", ToggleHideFromScreenCapture, isChecked: _settings.HideFromScreenCapture),
            Item("Show notification text", ToggleNotificationText, isChecked: _settings.ShowNotificationText),
            Item("Speak greeting and account name on startup", ToggleSpeakGreeting, isChecked: _settings.SpeakGreeting),
            Item("Hold right-click to talk", TogglePushToTalk, isChecked: _settings.PushToTalkEnabled),
            Item("Voice commands in Arabic", ToggleVoiceCommandsInArabic, isChecked: _settings.VoiceCommandsInArabic),
        });

    private void ToggleHideFromScreenCapture()
    {
        _settings.HideFromScreenCapture = !_settings.HideFromScreenCapture;
        _settings.Save();
        ScreenCapture.SetExcluded(this, _settings.HideFromScreenCapture);
        if (_avatarWindow != null) ScreenCapture.SetExcluded(_avatarWindow, _settings.HideFromScreenCapture);
        foreach (Window owned in OwnedWindows)
        {
            if (owned is QrCodeWindow or ClipboardHistoryWindow or DailyBubbleWindow or SelectionDetailsWindow or WindowsStatusWindow)
                ScreenCapture.SetExcluded(owned, _settings.HideFromScreenCapture);
        }
    }

    private void ToggleShowUptime()
    {
        _settings.ShowUptime = !_settings.ShowUptime;
        _settings.Save();
        Keeper.SetShowUptime(_settings.ShowUptime);
    }

    private void ToggleShowNetwork()
    {
        _settings.ShowNetwork = !_settings.ShowNetwork;
        _settings.Save();
        Keeper.SetShowNetwork(_settings.ShowNetwork);
    }

    private void ToggleShowDevices()
    {
        _settings.ShowDevices = !_settings.ShowDevices;
        _settings.Save();
        Keeper.SetShowDevices(_settings.ShowDevices);
    }

    private void ToggleNotificationText()
    {
        _settings.ShowNotificationText = !_settings.ShowNotificationText;
        _settings.Save();
        if (_watchingNotifications) _ = RefreshWatched();
    }

    private void ToggleSpeakGreeting()
    {
        _settings.SpeakGreeting = !_settings.SpeakGreeting;
        _settings.Save();
        if (!_settings.SpeakGreeting && _greetingTimer != null)
        {
            _greetingTimer.Stop();
            _greetingTimer.Tick -= GreetingTimer_Tick;
            _greetingTimer = null;
        }
    }

    private void ToggleVoiceCommandsInArabic()
    {
        _settings.VoiceCommandsInArabic = !_settings.VoiceCommandsInArabic;
        _settings.Save();
        ShowOneShotLabel(_settings.VoiceCommandsInArabic ? "Voice commands: Arabic" : "Voice commands: English",
            "Applies from the next hold of the right mouse button.");
    }

    // Speech editors and voice tests use the microphone themselves; hold to
    // talk waits until the last of them closes.
    private void ResumeVoiceAfterEditor()
    {
        if (VoiceTestWindowOpen) return; // still listening; it resumes on close
        _voicePausedForEditor = false;
    }

    private static async Task SpeakFixedVoiceResponseAsync(string message,
        ReadAloudVoice voice = ReadAloudVoice.EnglishDavid)
    {
        // Callers provide only fixed phrases and fixed application names,
        // never transcript text.
        if (!SpeechReader.IsVoiceAvailable(voice)) return;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished() => finished.TrySetResult();
        SpeechReader.Stop();
        SpeechReader.Finished += OnFinished;
        try
        {
            SpeechReader.Speak(message,
                onFailed: OnFinished, selectedVoice: voice);
            try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException)
            {
                // If playback stalls, end it before the microphone resumes.
                SpeechReader.Stop();
            }
        }
        finally
        {
            SpeechReader.Finished -= OnFinished;
        }
    }

    // Opened by Explorer's full path with the folder as an argument, not by
    // shell-executing the folder path: if something other than a folder ever
    // sat at that path, shell-execute would open or run it.
    private void OpenDictationFolder()
    {
        try
        {
            Directory.CreateDirectory(SpeechToText.DictationFolder);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(SpeechToText.DictationFolder);
            using var explorerProcess = System.Diagnostics.Process.Start(start);
        }
        catch
        {
            ShowOneShotLabel("Couldn't open the dictation folder", "");
        }
    }

    private void SetSky(string sky)
    {
        _settings.Sky = sky;
        _settings.Save();
        ApplyDisplaySettings();
    }

    private void OpenLocationDialog()
    {
        var dialog = new LocationDialog(_settings.City) { Owner = this };
        if (ShowToolDialog(dialog) == true)
        {
            _settings.City = dialog.ResolvedCity;
            _settings.Latitude = dialog.ResolvedLat;
            _settings.Longitude = dialog.ResolvedLon;
            _settings.LocationTimeZoneId = PrayerTimeZone.IsValidId(dialog.ResolvedTimeZoneId)
                ? dialog.ResolvedTimeZoneId : null;
            _settings.Save();
            ApplyDisplaySettings();
            _ = RefreshWeatherAsync();
            _lastPrayerCheckUtc = DateTimeOffset.UtcNow;
            _playedPrayerInstants.Clear();
        }
    }

    private void ToggleAlwaysOnTop()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        if (_avatarWindow != null) _avatarWindow.Topmost = _settings.AlwaysOnTop;
        if (_dailyBubble != null) _dailyBubble.Topmost = _settings.AlwaysOnTop;
        _settings.Save();
    }

    private void ToggleStartWithWindows()
    {
        StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
    }

    // A second copy was started (see SingleInstance.cs): show this one -
    // it may have been hidden from the tray - and say so.
    private void ShowBecauseStartedAgain()
    {
        Visibility = Visibility.Visible;
        if (_avatarWindow != null) _avatarWindow.Visibility = Visibility.Visible;
        ShowOneShotLabel("CloClo is already running", "This is it.");
    }

    private void ToggleVisible()
    {
        if (Visibility == Visibility.Visible && _trayIcon?.IsAvailable != true)
        {
            ShowOneShotLabel("Can't hide the widget", "The tray icon is unavailable. Try again after Windows Explorer recovers.");
            return;
        }
        Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != Visibility.Visible) _commandSearchBar?.CloseSuggestions();
        if (_avatarWindow != null) _avatarWindow.Visibility = Visibility;
        if (Visibility != Visibility.Visible) _dailyBubble?.Close();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private bool _resumeWatchAfterCustomReminder;
    private readonly Dictionary<CustomReminderSettings, DateTime> _customReminderLastChecked = new();
    // "awake", "asleep", or "workout" — see UpdateActivityState.
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
    private Window? _avatarWindow;
    private CommandSearchBar? _commandSearchBar;
    private ScientificCalculatorWindow? _calculatorWindow;
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
    private LocalAudioTranscriber? _voiceActivation;
    private Task _voiceActivationStop = Task.CompletedTask;
    private bool _voiceActivationStarting;
    private bool _voiceActivationClosing;
    private bool _voicePausedForEditor;
    private bool _voiceActionPending;
    private bool _voiceArmed;
    private DateTime _voiceArmedUntil;
    private string _voiceCommandText = "";
    private DispatcherTimer? _voiceArmTimer;
    private DispatcherTimer? _voiceRetryTimer;
    private int _voiceRetryDelaySeconds = 2;
    private DateTime _voiceListeningSinceUtc;
    private SelectionDetailsWindow? _selectionDetailsWindow;
    private int _selectionDetailsRequest;
    private WindowsStatusWindow? _windowsStatusWindow;
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
            StopVoiceRetryTimer();
            Keeper.SetVoiceStatus("off");
            _ = StopVoiceActivationAsync();
            // SystemEvents holds its handlers in a static list, so leaving this
            // one attached would keep the whole window alive past close.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Topmost = _settings.AlwaysOnTop;
        SetupTopmostTimer();
        SetupForegroundTracking();
        SetupBatteryTimer();
        SetupIdleTimer();
        SetupCustomReminderTimer();
        QueueGreeting();
        if (_settings.VoiceActivationEnabled) _ = StartVoiceActivationAsync();

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
        Keeper.SetWeather(await WeatherService.GetCurrentAsync(_settings.Latitude, _settings.Longitude));
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

    // Search runs only these fixed menu actions. Typed text filters labels and
    // aliases; it is never passed to a process, file path or command parser.
    private IReadOnlyList<WidgetSearchAction> BuildQuickActions()
    {
        var actions = new List<WidgetSearchAction>
        {
            new("Windows status", "storage connection drive disk network wifi", WidgetSearchActionKind.OpenWindow, ShowWindowsStatus),
            new("Scientific calculator", "math arithmetic trig sin cos log square root", WidgetSearchActionKind.OpenWindow, ShowScientificCalculator),
            new("Custom reminders", "reminder remainder schedule message", WidgetSearchActionKind.OpenWindow, OpenCustomReminders),
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
            new("Privacy: Voice activation", "cloclo wake word microphone voice command toggle", WidgetSearchActionKind.Toggle, ToggleVoiceActivation, () => _settings.VoiceActivationEnabled),
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
        StopVoiceRetryTimer();
        _ = StopVoiceActivationAsync();
        var dialog = new DictationCaptureWindow()
        {
            Owner = this,
            Topmost = _settings.AlwaysOnTop,
        };
        _dictationWindow = dialog;
        dialog.SaveRequested += SaveDictationText;
        dialog.OpenFolderRequested += OpenDictationFolder;
        dialog.CaptureStopped += ResumeVoiceActivationAfterEditor;
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
        if (icons.TryGetValue("gpu", out var gpuRect) && gpuRect.Contains(p))
        {
            WatchGpus();
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
        _batteryTimer.Tick += (_, _) => RefreshBattery();
        _batteryTimer.Start();
    }

    private void RefreshBattery()
    {
        Keeper.SetBattery(BatteryInfo.Read());
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
            if (reminder.Speak) SpeechReader.Speak(reminder.Message);
            break;
        }
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

    // Three states, sleep takes priority over the workout nudge (if you've
    // actually stepped away, you don't also need a "take a break" pose):
    // - asleep: idle for at least SleepAfterMinutes.
    // - workout: it's been at least WorkoutAfterMinutes since the last real
    //   break (an idle stretch of BreakThreshold+, before the pose ever
    //   triggered). Once showing, it runs its own BreakThreshold-length
    //   countdown to completion regardless of further activity — a visible,
    //   predictable timer beats silently waiting to detect a real pause.
    // - awake: neither.
    private void UpdateActivityState()
    {
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
            return;
        }

        SetActivityState("awake");
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
    }

    private void ApplyDisplaySettings()
    {
        Keeper.SetPlace(_settings.City);
        Keeper.SetAvatar(_settings.Avatar);
        Keeper.SetOutfit(CurrentOutfit);
        Keeper.SetMotionMode(_settings.MotionMode);
        Keeper.SetSkyOverride(_settings.Sky);
        Keeper.SetEnabledIcons(IconOrder.Where(k => _settings.EnabledIcons.Contains(k)));
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
        menu.Items.Add(Item("Set location…", OpenLocationDialog));
        menu.Items.Add(BuildAvatarMenu());
        menu.Items.Add(BuildSkyMenu());
        menu.Items.Add(BuildIconsMenu());
        menu.Items.Add(BuildMotionMenu());
        menu.Items.Add(BuildSleepMenu());
        menu.Items.Add(BuildWorkoutMenu());
        menu.Items.Add(Item("Custom reminders…", OpenCustomReminders));
        menu.Items.Add(Item("Windows status…", ShowWindowsStatus));
        menu.Items.Add(Item("Scientific calculator…", ShowScientificCalculator));
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
        _settings.CustomReminders = dialog.Reminders.ToList();
        _settings.Save();
        var now = DateTime.Now;
        _customReminderLastChecked.Clear();
        foreach (var reminder in _settings.CustomReminders)
            _customReminderLastChecked[reminder] = now;
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
        ("gpu", "GPU info"),
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
            Item("Allow local CloClo voice activation", ToggleVoiceActivation, isChecked: _settings.VoiceActivationEnabled),
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

    private void ToggleVoiceActivation()
    {
        _settings.VoiceActivationEnabled = !_settings.VoiceActivationEnabled;
        _settings.Save();
        StopVoiceRetryTimer();
        if (_settings.VoiceActivationEnabled)
        {
            _voiceRetryDelaySeconds = 2;
            ShowOneShotLabel("Local voice activation on", "Say CloClo, then an approved command. Say CloClo stop voice now to turn it off.");
            _ = StartVoiceActivationAsync();
        }
        else
        {
            _ = StopVoiceActivationAsync(abortPendingAudio: true);
            Keeper.SetVoiceStatus("off");
            ShowOneShotLabel("Voice activation off", "Microphone command capture stopped.");
        }
    }

    private void ResumeVoiceActivationAfterEditor()
    {
        _voicePausedForEditor = false;
        StopVoiceRetryTimer();
        if (_settings.VoiceActivationEnabled) _ = StartVoiceActivationAsync();
    }

    private async Task StartVoiceActivationAsync()
    {
        if (_voiceActivationStarting || _voiceActivation != null || _voiceActionPending ||
            !_settings.VoiceActivationEnabled || _voicePausedForEditor || _voiceActivationClosing) return;
        StopVoiceRetryTimer();
        _voiceActivationStarting = true;
        Keeper.SetVoiceStatus("processing");
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                !System.Runtime.Intrinsics.X86.Avx2.IsSupported ||
                !System.Runtime.Intrinsics.X86.Fma.IsSupported ||
                (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & (1 << 29)) == 0)
                throw new NotSupportedException("Local voice activation needs Windows 11 and AVX2, FMA, and F16C.");

            await _voiceActivationStop;
            if (!_settings.VoiceActivationEnabled || _voicePausedForEditor || _voiceActivationClosing) return;
            var session = new LocalAudioTranscriber(AudioCaptureSource.Microphone,
                recognitionPrompt: ApprovedVoiceAppLauncher.RecognitionPrompt);
            _voiceActivation = session;
            session.TextRecognized += text =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_voiceActivation == session && !_voiceActionPending) ProcessVoiceActivationText(session, text);
                }));
            };
            session.Failed += message =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_voiceActivation == session) _ = RecoverVoiceActivationAsync(message);
                }));
            };
            await session.StartAsync("en-US");
            if (_voiceActivation == session)
            {
                _voiceListeningSinceUtc = DateTime.UtcNow;
                Keeper.SetVoiceStatus("wake");
            }
        }
        catch (Exception ex)
        {
            if (_settings.VoiceActivationEnabled && !_voicePausedForEditor && !_voiceActivationClosing)
                await RecoverVoiceActivationAsync($"Local voice activation stopped ({ex.GetType().Name}).");
        }
        finally
        {
            _voiceActivationStarting = false;
            if (_settings.VoiceActivationEnabled && _voiceActivation == null &&
                !_voicePausedForEditor && !_voiceActivationClosing && !_voiceActionPending &&
                _voiceRetryTimer == null)
                ScheduleVoiceActivationRetry("Voice capture was interrupted.");
        }
    }

    private void ProcessVoiceActivationText(LocalAudioTranscriber session, string text)
    {
        if (!_settings.VoiceActivationEnabled || _voicePausedForEditor || _voiceActivationClosing) return;
        if (ApprovedVoiceAppLauncher.IsWakePhrase(text))
        {
            ArmVoiceCommand("CloClo");
            ShowOneShotLabel("CloClo is listening", "Say CloClo open an approved app now, or CloClo stop voice now.");
            return;
        }

        if ((!_voiceArmed || DateTime.UtcNow > _voiceArmedUntil) &&
            ApprovedVoiceAppLauncher.StartsWithWakeWord(text) &&
            !ApprovedVoiceAppLauncher.HasFinishWord(text))
        {
            ArmVoiceCommand(text);
            return;
        }

        var phrase = text;
        if (_voiceArmed && DateTime.UtcNow <= _voiceArmedUntil)
        {
            // Whisper can split a spoken phrase into adjacent text segments.
            // A repeated wake word begins a new command instead of appending
            // to the previous wake phrase.
            if (!ApprovedVoiceAppLauncher.StartsWithWakeWord(text))
                phrase = _voiceCommandText + " " + text;
            _voiceCommandText = phrase.Length <= 160 ? phrase : "";
        }
        else
        {
            DisarmVoiceCommand();
        }

        if (ApprovedVoiceAppLauncher.IsStopVoiceCommand(phrase))
        {
            DisarmVoiceCommand();
            _voiceActionPending = true;
            Keeper.SetVoiceStatus("processing");
            _ = DisableVoiceActivationByVoiceAsync();
        }
        else if (ApprovedVoiceAppLauncher.TryMatchFinishedCommand(phrase, out var app))
        {
            DisarmVoiceCommand();
            _voiceActionPending = true;
            Keeper.SetVoiceStatus("processing");
            _ = ConfirmVoiceActivationAsync(session, app);
        }
        else if (ApprovedVoiceAppLauncher.HasFinishWord(phrase))
        {
            DisarmVoiceCommand();
            Keeper.SetVoiceStatus("wake");
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Voice command not recognized", "Say CloClo open an approved app now, or CloClo stop voice now.");
        }
    }

    private void ArmVoiceCommand(string initialText)
    {
        DisarmVoiceCommand();
        _voiceArmed = true;
        _voiceArmedUntil = DateTime.UtcNow.AddSeconds(30);
        _voiceCommandText = initialText;
        _voiceArmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _voiceArmTimer.Tick += VoiceArmTimer_Tick;
        _voiceArmTimer.Start();
        Keeper.SetVoiceStatus("armed");
    }

    private void VoiceArmTimer_Tick(object? sender, EventArgs e)
    {
        DisarmVoiceCommand();
        if (_settings.VoiceActivationEnabled && _voiceActivation != null)
            Keeper.SetVoiceStatus("wake");
    }

    private void DisarmVoiceCommand()
    {
        _voiceArmed = false;
        _voiceCommandText = "";
        if (_voiceArmTimer == null) return;
        _voiceArmTimer.Stop();
        _voiceArmTimer.Tick -= VoiceArmTimer_Tick;
        _voiceArmTimer = null;
    }

    private async Task DisableVoiceActivationByVoiceAsync()
    {
        _settings.VoiceActivationEnabled = false;
        _settings.Save();
        StopVoiceRetryTimer();
        try
        {
            // Abort capture immediately; native cleanup can finish while the
            // fixed response plays. No more microphone data is accepted.
            var captureStop = StopVoiceActivationAsync(abortPendingAudio: true);
            Keeper.SetVoiceStatus("off");
            if (!_voiceActivationClosing)
            {
                ShowOneShotLabel("Voice activation off", "Microphone command capture stopped.");
                try { await SpeakFixedVoiceResponseAsync("Okay, stopping voice now."); }
                catch { /* Spoken feedback is optional; capture is already off. */ }
            }
            await captureStop;
        }
        finally
        {
            _voiceActionPending = false;
            if (_settings.VoiceActivationEnabled && !_voiceActivationClosing && !_voicePausedForEditor)
                _ = StartVoiceActivationAsync();
        }
    }

    private async Task ConfirmVoiceActivationAsync(LocalAudioTranscriber session, ApprovedVoiceApp app)
    {
        try
        {
            if (_voiceActivation != session) return;
            // End this capture before displaying a modal question. No new
            // microphone session starts until the question is dismissed.
            var captureStop = StopVoiceActivationAsync(abortPendingAudio: true);
            var name = ApprovedVoiceAppLauncher.DisplayName(app);
            // Capture has been aborted synchronously. Start the fixed response
            // while Whisper finishes cleanup, then wait for cleanup before the
            // confirmation dialog. No app starts until Yes is chosen.
            if (_settings.VoiceActivationEnabled && !_voiceActivationClosing)
            {
                try { await SpeakVoiceCommandAcknowledgementAsync(name); }
                catch { /* Spoken feedback is optional; confirmation still follows. */ }
            }
            await captureStop;
            if (!_settings.VoiceActivationEnabled || _voiceActivationClosing) return;
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
                if (_avatarWindow != null) _avatarWindow.Visibility = Visibility.Visible;
            }
            Activate();
            var answer = MessageBox.Show(this, $"Open {name}?", "Confirm voice command",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes || !_settings.VoiceActivationEnabled)
            {
                Keeper.SetVoiceStatus("failed");
                ShowOneShotLabel($"Did not open {name}", "");
                return;
            }
            var error = ApprovedVoiceAppLauncher.Launch(app);
            ShowOneShotLabel(error ?? $"Opened {name}", "");
            if (error != null)
            {
                Keeper.SetVoiceStatus("failed");
                return;
            }
            if (_settings.VoiceActivationEnabled && !_voiceActivationClosing)
                Keeper.SetVoiceStatus("complete");
        }
        finally
        {
            _voiceActionPending = false;
            if (_settings.VoiceActivationEnabled) _ = StartVoiceActivationAsync();
        }
    }

    private static Task SpeakVoiceCommandAcknowledgementAsync(string appName) =>
        SpeakFixedVoiceResponseAsync($"Okay, opening {appName} now.");

    private static async Task SpeakFixedVoiceResponseAsync(string message)
    {
        // Callers provide only fixed application names or a fixed stop message,
        // never transcript text. Microphone capture is already stopped.
        if (!SpeechReader.IsVoiceAvailable(ReadAloudVoice.EnglishDavid)) return;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished() => finished.TrySetResult();
        SpeechReader.Stop();
        SpeechReader.Finished += OnFinished;
        try
        {
            SpeechReader.Speak(message,
                onFailed: OnFinished, selectedVoice: ReadAloudVoice.EnglishDavid);
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

    private async Task RecoverVoiceActivationAsync(string message)
    {
        if (_voiceListeningSinceUtc != default &&
            DateTime.UtcNow - _voiceListeningSinceUtc > TimeSpan.FromMinutes(1))
            _voiceRetryDelaySeconds = 2;
        _voiceListeningSinceUtc = default;
        await StopVoiceActivationAsync(abortPendingAudio: true);
        ScheduleVoiceActivationRetry(message);
    }

    private void ScheduleVoiceActivationRetry(string message)
    {
        if (!_settings.VoiceActivationEnabled || _voicePausedForEditor ||
            _voiceActivationClosing || _voiceRetryTimer != null) return;
        var delay = _voiceRetryDelaySeconds;
        _voiceRetryDelaySeconds = Math.Min(delay * 2, 60);
        Keeper.SetVoiceStatus("processing");
        Keeper.SetVoiceStatus("failed");
        ShowOneShotLabel(message, $"Retrying local voice capture in {delay} seconds. Privacy remains on.");
        _voiceRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        _voiceRetryTimer.Tick += VoiceRetryTimer_Tick;
        _voiceRetryTimer.Start();
    }

    private void VoiceRetryTimer_Tick(object? sender, EventArgs e)
    {
        StopVoiceRetryTimer();
        if (_settings.VoiceActivationEnabled && !_voicePausedForEditor && !_voiceActivationClosing)
            _ = StartVoiceActivationAsync();
    }

    private void StopVoiceRetryTimer()
    {
        if (_voiceRetryTimer == null) return;
        _voiceRetryTimer.Stop();
        _voiceRetryTimer.Tick -= VoiceRetryTimer_Tick;
        _voiceRetryTimer = null;
    }

    private async Task StopVoiceActivationAsync(bool abortPendingAudio = false)
    {
        var session = _voiceActivation;
        _voiceActivation = null;
        DisarmVoiceCommand();
        if (!_voiceActionPending) Keeper.SetVoiceStatus("off");
        if (session != null) _voiceActivationStop = (abortPendingAudio
            ? session.AbortAsync() : session.DisposeAsync()).AsTask();
        await _voiceActivationStop;
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
            _settings.Save();
            ApplyDisplaySettings();
            _ = RefreshWeatherAsync();
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

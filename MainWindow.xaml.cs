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
using DrawingForms = System.Windows.Forms;

namespace CloCloWidget;

public partial class MainWindow : Window
{
    private WidgetSettings _settings = WidgetSettings.Load();
    private DrawingForms.NotifyIcon? _trayIcon;
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _batteryTimer;
    private DispatcherTimer? _idleTimer;
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
    private const double AvatarHeight = 245 * Scale;
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
        "youtube", "spotify", "bell", "search", "gpu", "read", "mic", "convert", "color", "qr", "volume", "clipboard",
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
    // The rest of the label (icon/note + both text lines) — clicking there
    // opens the app. Checked after PauseIconRect/ListIconRect, which it
    // overlaps, so those rects' clicks are claimed first.
    private static readonly Rect LabelBodyRect = new(0, 209 * Scale, 137 * Scale, 36 * Scale);

    private static readonly string[] YoutubeAumids = { "edge", "chrome" };
    private static readonly string[] SpotifyAumids = { "spotify" };

    // Whatever the label is currently following — set by clicking an icon,
    // kept live by _nowPlayingTimer until it actually stops (rather than a
    // timed popup that hides itself regardless of whether it's still
    // relevant), or until the pause button stops it explicitly. A plain
    // fetcher delegate so the same watch/poll/pause machinery works for
    // both "what's playing in this app" and "the latest notification".
    // Icon is a data: URI (notifications only — media has none) or null.
    private Func<Task<(string Title, string Subtitle, string? Icon, string? AppUserModelId)?>>? _watchedFetcher;
    private string? _watchedAppUserModelId;
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

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    private const byte VK_LWIN = 0x5B;
    private const byte VK_N = 0x4E;
    private const uint KEYEVENTF_KEYUP = 0x0002;

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
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            // SystemEvents holds its handlers in a static list, so leaving this
            // one attached would keep the whole window alive past close.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SaveWindowPosition();
            _avatarWindow?.Close();
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
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();
        SetupTrayIcon();
        CreateAvatarHitWindow();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Topmost = _settings.AlwaysOnTop;
        SetupTopmostTimer();
        SetupForegroundTracking();
        SetupBatteryTimer();
        SetupIdleTimer();

        await Web.EnsureCoreWebView2Async();
        Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        // Every NavigateToWidget() call (Refresh weather, a location or sky
        // change) reloads the page from scratch, dropping whatever label
        // was showing. Push a fresh one once the new page is actually ready,
        // rather than relying on "did it change" — from the host's side
        // nothing changed, but the page lost it regardless.
        Web.CoreWebView2.NavigationCompleted += (_, _) => { _ = RefreshWatched(); RefreshBattery(); PushActivityState(); };

        NavigateToWidget();
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
            return info is { } np ? (np.Title, np.Artist, (string?)null, (string?)np.AppUserModelId) : ((string, string, string?, string?)?)null;
        });
    }

    private void WatchNotifications()
    {
        _watchingNotifications = true;
        Watch(async () =>
        {
            var n = await NotificationWatcher.GetLatestAsync();
            return n is { } latest ? ($"{latest.AppName}: {latest.Title}", latest.Body, latest.IconDataUri, latest.AppUserModelId) : ((string, string, string?, string?)?)null;
        });
    }

    // Static, not really something to "watch" the way media or
    // notifications change — but reusing the same label/poll/pause
    // mechanism is simpler than building a separate one-shot display just
    // for this, and re-querying WMI every few seconds is cheap enough.
    // There's no AUMID for a GPU, so the label body's "open app" click
    // just does nothing here, which is the correct behaviour.
    private void WatchGpus()
    {
        _watchingNotifications = false;
        Watch(async () =>
        {
            var names = await Task.Run(GpuInfo.GetNames);
            if (names.Count == 0) return ((string, string, string?, string?)?)null;
            var title = names[0];
            var subtitle = names.Count > 1 ? string.Join(", ", names.Skip(1)) : "";
            return (title, subtitle, (string?)null, (string?)null);
        });
    }

    // A one-shot action, not a watch — grabs whatever's currently
    // highlighted in whichever app had focus before this click, shows it in
    // the label, and has Keeper read it aloud. Deliberately doesn't go
    // through Watch()/the polling timer the way GPU info does: re-running
    // this every 4 seconds would mean repeatedly stealing focus and firing
    // Ctrl+C into whatever the user is now doing, which GPU's harmless WMI
    // re-query is not.
    private void ReadSelection()
    {
        _watchingNotifications = false;
        _ = ReadSelectionAsync();
    }

    private async Task ReadSelectionAsync()
    {
        var target = _lastExternalForegroundWindow;
        if (target == IntPtr.Zero) return;

        var text = await TextSelectionReader.ReadSelectedTextAsync(target);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowOneShotLabel("Nothing selected", "Highlight some text, then click again.");
            return;
        }

        text = text.Trim();
        const int titleLimit = 60;
        var title = text.Length > titleLimit ? text[..titleLimit] + "…" : text;
        var subtitle = text.Length > titleLimit ? text : "";
        ShowOneShotLabel(title, subtitle);
        SpeechReader.Speak(text);
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
            ShowOneShotLabel("Nothing selected", "Highlight some text, then click again.");
            return;
        }

        try
        {
            new QrCodeWindow(text.Trim()) { Owner = this }.Show();
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
        new ClipboardHistoryWindow(texts) { Owner = this }.Show();
    }

    // Voice dictation, appended to a running text file rather than just
    // shown in the label — there's nothing to copy/paste, it's just there
    // the next time you open the file. Delegates the actual listening to
    // Windows' own Voice Typing (see DictationCaptureWindow) rather than
    // calling WinRT's SpeechRecognizer directly (still available in
    // SpeechToText.RecognizeAsync) — Voice Typing reliably captured real
    // audio on this machine when SpeechRecognizer did not, traced to which
    // input device held Windows' "Default Device" role.
    private void OpenDictationCapture()
    {
        _watchingNotifications = false;
        var dialog = new DictationCaptureWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.CapturedText)) return;

        var text = dialog.CapturedText;
        var path = _settings.DictationFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = SpeechToText.CreateNewFile();
            _settings.DictationFilePath = path;
            _settings.Save();
        }
        SpeechToText.Append(path, text);

        const int titleLimit = 60;
        var title = text.Length > titleLimit ? text[..titleLimit] + "…" : text;
        ShowOneShotLabel(title, $"Saved to {Path.GetFileName(path)}");
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

        new UnitConverterWindow(parsed) { Owner = this }.Show();
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
        _colorPickerOverlay.Previewed += color =>
            ShowOneShotLabel(ColorPicker.ToHex(color), "Click to copy, Esc to cancel");
        _colorPickerOverlay.Picked += color =>
        {
            var hex = ColorPicker.ToHex(color);
            System.Windows.Clipboard.SetText(hex);
            ShowOneShotLabel(hex, "Copied to clipboard");
            new ColorViewerWindow(color) { Owner = this }.Show();
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

    // "Start new dictation file" menu action — the counterpart to
    // DictateAsync's default of appending to whatever file is already
    // current. Deliberately not on the icon bar itself (no room left, and
    // it's a rare, deliberate action compared to dictating itself).
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
        _watchedFetcher = () => Task.FromResult<(string, string, string?, string?)?>((title, subtitle, null, null));
        _watchedAppUserModelId = null;
        _nowPlayingTimer?.Stop();
        _ = RefreshWatched();
    }

    private void Watch(Func<Task<(string Title, string Subtitle, string? Icon, string? AppUserModelId)?>> fetcher)
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
    private void PushPerk()
    {
        try { Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"perk\"}"); } catch { /* page not ready yet */ }
    }

    // The pause button inside the label itself, not a fourth icon — stops
    // polling immediately and hides the label, rather than waiting for the
    // next 4s tick to notice "nothing" on its own.
    private void StopWatching()
    {
        _watchedFetcher = null;
        _watchedAppUserModelId = null;
        _nowPlayingTimer?.Stop();
        SpeechReader.Stop();
        StopColorPicking();
        try { Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"nowPlaying\",\"title\":null,\"artist\":null,\"icon\":null}"); } catch { }
    }

    // Clicking the label body (anywhere but the pause/list buttons)
    // launches whichever app it's showing — media or notification alike —
    // using the AUMID captured from the last successful poll rather than
    // re-querying on click.
    private void OpenWatchedApp() => AppLauncher.TryActivate(_watchedAppUserModelId);

    // The list button, notifications only — opens the full list instead
    // of launching just the one app that sent the latest notification.
    // Win+N is the standard shortcut for Windows' notification flyout on
    // both Windows 10 and 11 — simulating it is simpler and more robust
    // than trying to activate the flyout's own host process by AUMID.
    private void OpenNotificationFlyout()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_N, 0, 0, UIntPtr.Zero);
        keybd_event(VK_N, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // Two sections: apps that currently hold a media session (▶, same as
    // before — YouTube/Spotify are just two hardcoded guesses at this same
    // list), and below a separator, every other currently-running app
    // (from RunningApps.List(), the same visible-window set Alt-Tab shows)
    // in case what the user's after isn't playing anything (yet), or isn't
    // media at all — picking one still watches it, and also brings it to
    // the foreground right away so clicking it always does *something*
    // visible even if it never reports a track.
    private async void ShowAppPicker(double screenX, double screenY)
    {
        var menu = new DrawingForms.ContextMenuStrip();
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
                var match = aumid; // capture for the closure below
                menu.Items.Add(itemText, null, (_, _) => WatchApp(new[] { match }));
            }
        }
        catch { /* leave the "playing" section empty/fallback below */ }

        try
        {
            var running = RunningApps.List();
            if (running.Count > 0 && menu.Items.Count > 0)
            {
                menu.Items.Add(new DrawingForms.ToolStripSeparator());
            }
            foreach (var app in running)
            {
                // Most plain Win32 apps never register an AUMID, so this
                // is often null — dedupe against the "playing" section by
                // AUMID when there is one, otherwise nothing to collide
                // with, so just show it.
                if (app.AppUserModelId != null && !seen.Add(app.AppUserModelId)) continue;
                var aumid = app.AppUserModelId;
                var hwnd = app.WindowHandle;
                menu.Items.Add(app.Title, null, (_, _) =>
                {
                    if (aumid != null)
                    {
                        WatchApp(new[] { aumid });
                        AppLauncher.TryActivate(aumid);
                    }
                    else
                    {
                        // No AUMID to watch media through — just bring it
                        // to the foreground directly via its window handle.
                        RunningApps.Activate(hwnd);
                    }
                });
            }
        }
        catch { /* leave the "running" section empty */ }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add("Nothing found to watch", null, (_, _) => { }).Enabled = false;
        }
        menu.Show(new System.Drawing.Point((int)Math.Round(screenX), (int)Math.Round(screenY)));
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
        _watchedAppUserModelId = result?.AppUserModelId;
        var json = result is { } r
            ? $"{{\"type\":\"nowPlaying\",\"title\":{JsonSerializer.Serialize(r.Title)},\"artist\":{JsonSerializer.Serialize(r.Subtitle)},\"icon\":{JsonSerializer.Serialize(r.Icon)},\"isNotification\":{(_watchingNotifications ? "true" : "false")}}}"
            : "{\"type\":\"nowPlaying\",\"title\":null,\"artist\":null,\"icon\":null,\"isNotification\":false}";
        try { Web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* page not ready yet */ }
    }

    // A second, genuinely interactive window, invisible and sized to
    // roughly the character + its temperature/location text, sitting on
    // top of the (click-through) render window. It owns dragging and the
    // right-click/double-click menu directly via normal WPF mouse events —
    // no WebView2 message relay needed, since WebView2 never sees mouse
    // input at all once the parent is WS_EX_TRANSPARENT.
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
        _avatarWindow.PreviewMouseLeftButtonDown += AvatarWindow_MouseLeftButtonDown;
        _avatarWindow.MouseRightButtonUp += (_, _) => ShowContextMenuAtCursor();
        _avatarWindow.LocationChanged += (_, _) =>
        {
            Left = _avatarWindow.Left - AvatarOffsetX;
            Top = _avatarWindow.Top - AvatarOffsetY;
        };
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
            // Plain arithmetic rather than _avatarWindow.PointToScreen(p) or
            // a separate DrawingForms.Cursor.Position read — both of those
            // returned distorted/stale values during testing (confirmed via
            // logging: Left/Top and p were individually correct, but
            // PointToScreen's result was nowhere near their sum), while
            // Left/Top and GetPosition share the same DIU coordinate space
            // for this plain, untransformed window, so adding them directly
            // is both simpler and the one that's actually been verified.
            ShowAppPicker(_avatarWindow!.Left + p.X, _avatarWindow.Top + p.Y);
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
        if (LabelBodyRect.Contains(p) && _watchedFetcher != null && _watchedAppUserModelId != null)
        {
            OpenWatchedApp();
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

    private void ShowContextMenuAtCursor()
    {
        var p = DrawingForms.Cursor.Position;
        ShowContextMenu(p.X, p.Y);
    }

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
        var status = BatteryInfo.Read();
        var json = status is { } s
            ? $"{{\"type\":\"battery\",\"percent\":{s.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"charging\":{(s.Charging ? "true" : "false")},\"timeRemaining\":{JsonSerializer.Serialize(s.TimeRemaining)}}}"
            : "{\"type\":\"battery\",\"percent\":null,\"charging\":false,\"timeRemaining\":null}";
        try { Web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* page not ready yet */ }
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
        var json = $"{{\"type\":\"activity\",\"state\":{JsonSerializer.Serialize(_activityState)},\"secondsLeft\":{secondsLeft}}}";
        try { Web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* page not ready yet */ }
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
    }

    private void NavigateToWidget()
    {
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        var htmlPath = Path.Combine(assetsDir, "widget.html");
        var query =
            $"?lat={_settings.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&lon={_settings.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&city={Uri.EscapeDataString(_settings.City)}" +
            $"&sky={Uri.EscapeDataString(_settings.Sky)}" +
            $"&icons={Uri.EscapeDataString(string.Join(",", IconOrder.Where(k => _settings.EnabledIcons.Contains(k))))}";
        Web.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + query);
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
        var rect = new System.Drawing.Rectangle(
            (int)Math.Round(left * dpi.DpiScaleX),
            (int)Math.Round(top * dpi.DpiScaleY),
            (int)Math.Round(Width * dpi.DpiScaleX),
            (int)Math.Round(Height * dpi.DpiScaleY));
        // One corner grazing a screen doesn't make the widget usable, so ask
        // for a real patch of it to be visible, and against the working area
        // so a spot buried under the taskbar doesn't count either.
        return DrawingForms.Screen.AllScreens.Any(s =>
        {
            var overlap = System.Drawing.Rectangle.Intersect(s.WorkingArea, rect);
            return overlap.Width >= MinVisiblePixels && overlap.Height >= MinVisiblePixels;
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
        // Extracted from the exe's own embedded icon (set via ApplicationIcon
        // in the csproj) rather than loaded from a loose Assets/icon.ico —
        // that file mysteriously vanished from `dotnet publish`'s output
        // (present in plain `dotnet build` output, absent after publish)
        // once ApplicationIcon also pointed at it, so File.Exists() was
        // silently failing and falling back to the generic system icon.
        // The exe's own resource doesn't have that problem: it's always
        // there, in exactly the build that's actually running.
        System.Drawing.Icon trayIcon;
        try
        {
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            trayIcon = System.Drawing.SystemIcons.Application;
        }
        _trayIcon = new DrawingForms.NotifyIcon
        {
            Icon = trayIcon,
            Visible = true,
            Text = "CloClo weather",
        };
        _trayIcon.DoubleClick += (_, _) => ToggleVisible();

        // Unlike ShowContextMenu below, the tray's menu isn't rebuilt per
        // click — Windows owns the right-click-to-show wiring for
        // NotifyIcon.ContextMenuStrip internally, so there's no call site
        // here to rebuild from. Refresh the checkbox states via Opening
        // instead, which fires right before every display.
        var trayMenu = new DrawingForms.ContextMenuStrip();
        trayMenu.Opening += (_, _) =>
        {
            trayMenu.Items.Clear();
            PopulateMenu(trayMenu, includeShowHide: true);
        };
        _trayIcon.ContextMenuStrip = trayMenu;
    }

    // Shown two ways: as the tray icon's menu (Windows positions it near the
    // tray automatically), and via .Show(Point) at the cursor for a
    // right-click on the widget itself — a WPF ContextMenu popup turned out
    // to be unreliable on this window (AllowsTransparency + a WebView2's own
    // native child window both interfere with WPF's popup placement), so
    // both paths share this one WinForms menu instead.
    private DrawingForms.ContextMenuStrip BuildMenu(bool includeShowHide)
    {
        var menu = new DrawingForms.ContextMenuStrip();
        PopulateMenu(menu, includeShowHide);
        return menu;
    }

    private void PopulateMenu(DrawingForms.ContextMenuStrip menu, bool includeShowHide)
    {
        if (includeShowHide)
        {
            menu.Items.Add("Show / hide", null, (_, _) => ToggleVisible());
        }
        menu.Items.Add("Set location…", null, (_, _) => OpenLocationDialog());
        menu.Items.Add(BuildSkyMenu());
        menu.Items.Add(BuildIconsMenu());
        menu.Items.Add(BuildSleepMenu());
        menu.Items.Add(BuildWorkoutMenu());
        menu.Items.Add("Refresh weather", null, (_, _) => NavigateToWidget());
        menu.Items.Add("Start new dictation file", null, (_, _) => StartNewDictationFile());
        var topMost = new DrawingForms.ToolStripMenuItem("Always on top", null, (_, _) => ToggleAlwaysOnTop()) { Checked = _settings.AlwaysOnTop };
        menu.Items.Add(topMost);
        var startup = new DrawingForms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows()) { Checked = StartupRegistration.IsEnabled() };
        menu.Items.Add(startup);
        menu.Items.Add(new DrawingForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());
    }

    private DrawingForms.ToolStripMenuItem BuildSkyMenu()
    {
        var weather = new DrawingForms.ToolStripMenuItem("Weather");
        var items = SkyOptions
            .Select(opt => new DrawingForms.ToolStripMenuItem(opt.Label) { Checked = _settings.Sky == opt.Value })
            .ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var value = SkyOptions[i].Value;
            items[i].Click += (_, _) =>
            {
                SetSky(value);
                foreach (var it in items) it.Checked = false;
                items[Array.FindIndex(SkyOptions, o => o.Value == value)].Checked = true;
            };
            weather.DropDownItems.Add(items[i]);
        }
        return weather;
    }

    private static readonly (int Minutes, string Label)[] SleepOptions =
    {
        (2, "2 minutes"),
        (5, "5 minutes"),
        (15, "15 minutes"),
        (0, "Never"),
    };

    private DrawingForms.ToolStripMenuItem BuildSleepMenu()
    {
        var sleepMenu = new DrawingForms.ToolStripMenuItem("Sleep after");
        var items = SleepOptions
            .Select(opt => new DrawingForms.ToolStripMenuItem(opt.Label) { Checked = _settings.SleepAfterMinutes == opt.Minutes })
            .ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var minutes = SleepOptions[i].Minutes;
            items[i].Click += (_, _) =>
            {
                _settings.SleepAfterMinutes = minutes;
                _settings.Save();
                foreach (var it in items) it.Checked = false;
                items[Array.FindIndex(SleepOptions, o => o.Minutes == minutes)].Checked = true;
                UpdateActivityState(); // e.g. picking "Never" wakes it right away
            };
            sleepMenu.DropDownItems.Add(items[i]);
        }
        return sleepMenu;
    }

    private static readonly (int Minutes, string Label)[] WorkoutOptions =
    {
        (0, "Off"),
        (15, "15 minutes"),
        (30, "30 minutes"),
        (45, "45 minutes"),
        (60, "1 hour"),
    };

    private DrawingForms.ToolStripMenuItem BuildWorkoutMenu()
    {
        var workoutMenu = new DrawingForms.ToolStripMenuItem("Workout reminder");
        var items = WorkoutOptions
            .Select(opt => new DrawingForms.ToolStripMenuItem(opt.Label) { Checked = _settings.WorkoutAfterMinutes == opt.Minutes })
            .ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var minutes = WorkoutOptions[i].Minutes;
            items[i].Click += (_, _) =>
            {
                _settings.WorkoutAfterMinutes = minutes;
                _settings.Save();
                foreach (var it in items) it.Checked = false;
                items[Array.FindIndex(WorkoutOptions, o => o.Minutes == minutes)].Checked = true;
                _activeStreakStart = DateTime.Now; // start the new threshold's countdown fresh, not mid-streak
                UpdateActivityState();
            };
            workoutMenu.DropDownItems.Add(items[i]);
        }
        return workoutMenu;
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
        ("mic", "Dictate"),
        ("convert", "Convert units"),
        ("color", "Color picker"),
        ("qr", "QR code from selection"),
        ("volume", "Volume/mute toggle"),
        ("clipboard", "Clipboard history"),
    };

    private DrawingForms.ToolStripMenuItem BuildIconsMenu()
    {
        var iconsMenu = new DrawingForms.ToolStripMenuItem("Icons");
        var items = IconOptions
            .Select(opt => new DrawingForms.ToolStripMenuItem(opt.Label) { Checked = _settings.EnabledIcons.Contains(opt.Key) })
            .ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var key = IconOptions[i].Key;
            var item = items[i];
            // Capturing `item` (fresh per iteration) rather than indexing
            // items[i] inside the closure — i is shared across every
            // iteration, so by the time any of these actually fire (well
            // after the loop finishes), it already equals items.Length,
            // one past the end. Confirmed via a live IndexOutOfRangeException
            // report before this fix.
            item.Click += (_, _) => ToggleIcon(key, item);
            iconsMenu.DropDownItems.Add(item);
        }
        return iconsMenu;
    }

    // The media bar only fits about MaxVisibleIcons at once (see
    // ComputeIconRects) — enabling one more than that would silently
    // overflow past the window's edge, the same clipping bug class hit
    // (and fixed) repeatedly earlier for the fixed 6/7-icon layouts, so
    // this refuses instead of letting the count grow unbounded.
    private void ToggleIcon(string key, DrawingForms.ToolStripMenuItem item)
    {
        if (_settings.EnabledIcons.Contains(key))
        {
            _settings.EnabledIcons.Remove(key);
            item.Checked = false;
        }
        else
        {
            if (_settings.EnabledIcons.Count >= MaxVisibleIcons)
            {
                ShowOneShotLabel("Icon bar is full", "Turn off another icon first to add this one.");
                return;
            }
            _settings.EnabledIcons.Add(key);
            item.Checked = true;
        }
        _settings.Save();
        NavigateToWidget();
    }

    private void SetSky(string sky)
    {
        _settings.Sky = sky;
        _settings.Save();
        NavigateToWidget();
    }

    private void ShowContextMenu(double screenX, double screenY)
    {
        var menu = BuildMenu(includeShowHide: false);
        menu.Show(new System.Drawing.Point((int)Math.Round(screenX), (int)Math.Round(screenY)));
    }

    private void OpenLocationDialog()
    {
        var dialog = new LocationDialog(_settings.City) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings.City = dialog.ResolvedCity;
            _settings.Latitude = dialog.ResolvedLat;
            _settings.Longitude = dialog.ResolvedLon;
            _settings.Save();
            NavigateToWidget();
        }
    }

    private void ToggleAlwaysOnTop()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        if (_avatarWindow != null) _avatarWindow.Topmost = _settings.AlwaysOnTop;
        _settings.Save();
    }

    private void ToggleStartWithWindows()
    {
        StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
    }

    private void ToggleVisible()
    {
        Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        if (_avatarWindow != null) _avatarWindow.Visibility = Visibility;
    }
}

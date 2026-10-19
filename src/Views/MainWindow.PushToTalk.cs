using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloCloWidget;

// Hold to talk: holding the right mouse button anywhere on screen for
// PushToTalkHoldSeconds opens the microphone, and releasing it runs the
// approved app that was named, with no confirmation, or "shut down" starts a
// full shutdown (see StartShutdownAsync). Each step
// is also spoken, so it works without seeing the screen. Shorter
// right-clicks reach the app under the cursor as usual (RightButtonHoldHook).
// The microphone is open only while the button is held. Off by default
// (Privacy menu).
public partial class MainWindow
{
    private const double PushToTalkHoldSeconds = 1;
    private RightButtonHoldHook? _pushToTalkHook;
    private LocalAudioTranscriber? _pushToTalk;
    // Threshold reached and the button not yet released.
    private bool _pushToTalkHeld;
    private readonly List<string> _pushToTalkText = [];
    // Time to say "cancel" after the shutdown announcement ends.
    private const int ShutdownCountdownSeconds = 10;
    private CancellationTokenSource? _shutdownCountdown;

    // Called at startup and when the setting changes.
    private void UpdatePushToTalkHook()
    {
        if (_settings.PushToTalkEnabled && !_voiceActivationClosing)
        {
            if (_pushToTalkHook != null) return;
            try
            {
                _pushToTalkHook = new RightButtonHoldHook(TimeSpan.FromSeconds(PushToTalkHoldSeconds));
                _pushToTalkHook.HoldStarted += OnPushToTalkHoldStarted;
                _pushToTalkHook.HoldEnded += OnPushToTalkHoldEnded;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                ShowOneShotLabel("Hold to talk could not start", $"Windows refused the mouse watcher ({ex.NativeErrorCode}).");
            }
            return;
        }
        AbortPushToTalk();
        _pushToTalkHook?.Dispose();
        _pushToTalkHook = null;
    }

    private void OnPushToTalkHoldEnded()
    {
        if (!_pushToTalkHeld) return;
        _pushToTalkHeld = false;
        if (_pushToTalk is { } session) _ = FinishPushToTalkAsync(session);
    }

    private async void OnPushToTalkHoldStarted()
    {
        if (_pushToTalk != null) return; // the previous command is still finishing
        _pushToTalkHeld = true;
        if (_voiceActivationClosing || _voicePausedForEditor || _voicePausedForAudio)
        {
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Microphone is busy", "Try again when the current voice or audio task ends.");
            await SpeakPushToTalkStepAsync("The microphone is busy.", "الميكروفون مشغول.");
            return;
        }

        var arabic = _settings.VoiceCommandsInArabic;
        var session = new LocalAudioTranscriber(AudioCaptureSource.Microphone,
            recognitionPrompt: arabic
                ? ApprovedVoiceAppLauncher.ArabicRecognitionPrompt
                : ApprovedVoiceAppLauncher.RecognitionPrompt);
        _pushToTalk = session;
        lock (_pushToTalkText) _pushToTalkText.Clear();
        session.TextRecognized += text =>
        {
            lock (_pushToTalkText) _pushToTalkText.Add(text);
        };
        session.Failed += message =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
            {
                // After release, FinishPushToTalkAsync owns the session.
                if (_pushToTalk != session || !_pushToTalkHeld) return;
                AbortPushToTalk();
                Keeper.SetVoiceStatus("failed");
                ShowOneShotLabel("Hold to talk stopped", message);
            }));
        };
        Keeper.SetVoiceStatus("processing");
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                !System.Runtime.Intrinsics.X86.Avx2.IsSupported ||
                !System.Runtime.Intrinsics.X86.Fma.IsSupported)
                throw new NotSupportedException("Local voice commands need Windows 11 with AVX2 and FMA.");
            await session.StartAsync(arabic ? "ar-EG" : "en-US");
            if (_pushToTalk == session && _pushToTalkHeld)
            {
                ShowOneShotLabel("Listening", "Say an app name, like Notepad, then release.");
                // The microphone hears silence while CloClo says it, so the
                // cue can't become part of the command.
                session.PauseVoiceRecognition();
                await SpeakPushToTalkStepAsync("Listening.", "أنا سامعك.");
                if (_pushToTalk == session && _pushToTalkHeld)
                {
                    session.ResumeVoiceRecognition();
                    Keeper.SetVoiceStatus("armed");
                }
            }
        }
        catch (Exception ex)
        {
            if (_pushToTalk != session || !_pushToTalkHeld) return; // released or aborted while starting
            AbortPushToTalk();
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Hold to talk could not start", ex is NotSupportedException ? ex.Message : ex.GetType().Name);
            await SpeakPushToTalkStepAsync("Voice commands could not start.", "الأوامر الصوتية مش شغالة.");
        }
    }

    private async Task FinishPushToTalkAsync(LocalAudioTranscriber session)
    {
        Keeper.SetVoiceStatus("processing");
        try
        {
            // The last syllable is often still in the audio buffer at release.
            await Task.Delay(250);
            // A graceful stop decodes the phrase still queued.
            await session.DisposeAsync();
        }
        catch { /* No text: reported below. */ }
        if (_pushToTalk != session) return; // aborted meanwhile
        _pushToTalk = null;
        string heard;
        lock (_pushToTalkText)
        {
            heard = string.Join(" ", _pushToTalkText);
            _pushToTalkText.Clear();
        }
        Keeper.SetVoiceStatus("off");
        if (_voiceActivationClosing) return;

        if (heard.Length == 0)
        {
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Didn't hear a command", "Hold right-click, speak, then release.");
            await SpeakPushToTalkStepAsync("I didn't hear a command.", "ما سمعتش أمر.");
            return;
        }
        if (ApprovedVoiceAppLauncher.IsCancelCommand(heard))
        {
            if (await CancelShutdownByUserAsync()) return;
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Nothing to cancel", "");
            await SpeakPushToTalkStepAsync("There is nothing to cancel.", "مفيش حاجة ألغيها.");
            return;
        }
        if (_shutdownAwaitingClose && _offerForceClose && ApprovedVoiceAppLauncher.IsForceCloseCommand(heard))
        {
            await CloseAppsThenShutDownAsync(force: true);
            return;
        }
        if (_shutdownAwaitingClose && (ApprovedVoiceAppLauncher.IsCloseAppsCommand(heard) ||
                                       ApprovedVoiceAppLauncher.IsForceCloseCommand(heard)))
        {
            await CloseAppsThenShutDownAsync(force: false);
            return;
        }
        if (ApprovedVoiceAppLauncher.IsShutdownCommand(heard))
        {
            await StartShutdownAsync();
            return;
        }
        if (!ApprovedVoiceAppLauncher.TryMatchHeldCommand(heard, out var app))
        {
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Not an approved command", $"Heard: {heard}");
            await SpeakPushToTalkStepAsync("That is not an approved command.", "ده مش أمر مسموح.");
            return;
        }
        var name = ApprovedVoiceAppLauncher.DisplayName(app);
        var arabicName = ApprovedVoiceAppLauncher.ArabicDisplayName(app);
        Keeper.SetVoiceStatus("processing");
        ShowOneShotLabel($"Opening {name}", "");
        await SpeakPushToTalkStepAsync($"Opening {name} now.", $"بفتح {arabicName} دلوقتي.");
        if (_voiceActivationClosing) return;
        var error = ApprovedVoiceAppLauncher.Launch(app);
        Keeper.SetVoiceStatus(error == null ? "complete" : "failed");
        ShowOneShotLabel(error ?? $"Opened {name}", "");
        if (error != null)
            await SpeakPushToTalkStepAsync($"Could not open {name}.", $"تعذر فتح {arabicName}.");
    }

    // "Shut down". With apps open, CloClo names them and waits for "close"
    // (or "cancel"), asks each to close as its X button does, and shuts down
    // once none is left. An app still open after that, usually asking to
    // save, is named again: "close" tries again after saving, and "force
    // close" ends it without saving. With no apps open, a spoken countdown
    // leaves time to say "cancel". Silence for ShutdownAnswerSeconds,
    // turning hold to talk off or closing CloClo cancels it. The shutdown
    // itself is a full one (FullShutdown). ShutdownConfirmWindow shows each
    // step too, and its buttons answer the same way.
    private const int ShutdownAnswerSeconds = 60;
    private const int AppsCloseWaitSeconds = 10;
    private List<OpenApps.App> _appsToClose = [];
    private bool _shutdownAwaitingClose;
    private bool _offerForceClose;
    private int _shutdownQuestion;
    private ShutdownConfirmWindow? _shutdownWindow;
    // "Shut down now" clicked during the no-apps countdown.
    private bool _shutdownSkipWait;

    private void ShowShutdownStep(string heading, string detail, IEnumerable<string> apps,
        string? confirmText, bool offerForceClose = false)
    {
        if (_shutdownWindow != null)
        {
            _shutdownWindow.ShowStep(heading, detail, apps, confirmText, offerForceClose);
            return;
        }
        var window = new ShutdownConfirmWindow();
        window.ShowStep(heading, detail, apps, confirmText, offerForceClose);
        window.ConfirmRequested += () =>
        {
            if (_shutdownAwaitingClose) _ = CloseAppsThenShutDownAsync(force: false);
            else if (_shutdownCountdown != null) _shutdownSkipWait = true;
        };
        window.ForceCloseRequested += () =>
        {
            if (_shutdownAwaitingClose && _offerForceClose) _ = CloseAppsThenShutDownAsync(force: true);
        };
        window.CancelRequested += () =>
        {
            _shutdownWindow = null;
            _ = CancelShutdownByUserAsync();
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shutdownWindow, window)) _shutdownWindow = null;
        };
        _shutdownWindow = window;
        ShowSensitive(window); // app names can be private, like the other tool windows
        window.Activate();
    }

    private void CloseShutdownWindow()
    {
        var window = _shutdownWindow;
        _shutdownWindow = null;
        window?.CloseFromCode();
    }

    // "Cancel" said, clicked, or the window closed. False when no shutdown
    // was in progress.
    private async Task<bool> CancelShutdownByUserAsync()
    {
        if (!CancelShutdownCountdown()) return false;
        Keeper.SetVoiceStatus("complete");
        ShowOneShotLabel("Shutdown cancelled", "");
        await SpeakPushToTalkStepAsync("Shutdown cancelled.", "تم إلغاء إيقاف التشغيل.");
        return true;
    }

    private async Task StartShutdownAsync()
    {
        if (_shutdownCountdown != null)
        {
            ShowOneShotLabel("Already shutting down", "Hold right-click and say cancel to stop.");
            await SpeakPushToTalkStepAsync("Already shutting down. Say cancel to stop.", "الجهاز بيقفل. قول إلغاء عشان توقفه.");
            return;
        }
        var apps = OpenApps.List();
        if (apps.Count == 0)
        {
            await CountDownToShutdownAsync();
            return;
        }
        _shutdownCountdown = new CancellationTokenSource();
        await AskToCloseAppsAsync(apps, stillOpen: false);
    }

    private async Task AskToCloseAppsAsync(List<OpenApps.App> apps, bool stillOpen)
    {
        if (_shutdownCountdown is not { } shutdown) return;
        _appsToClose = apps;
        _shutdownAwaitingClose = true;
        _offerForceClose = stillOpen;
        var question = ++_shutdownQuestion;
        _ = CancelShutdownIfUnansweredAsync(question, shutdown.Token);

        var appNames = apps.Select(a => a.Name).Distinct().ToList();
        var names = string.Join(", ", appNames);
        Keeper.SetVoiceStatus("processing");
        if (!stillOpen)
        {
            var count = apps.Count == 1 ? "1 app is open" : $"{apps.Count} apps are open";
            ShowShutdownStep($"Before shutting down: {count}",
                "Close these apps and shut down? Apps with unsaved work will ask you to save. " +
                "You can also hold right-click and say close, or cancel.",
                appNames, "Close apps and shut down");
            ShowOneShotLabel($"Before shutting down: {count}", $"{names}. Hold right-click and say close, or cancel.");
            await SpeakPushToTalkStepAsync(
                $"{count}: {names}. To close them and shut down, hold right-click and say close. Or say cancel.",
                $"فيه {apps.Count} برامج مفتوحة: {names}. عشان أقفلهم وأقفل الجهاز، دوس كليك يمين مطول وقول اقفلهم. أو قول إلغاء.");
        }
        else
        {
            ShowShutdownStep("Still open",
                "It may be asking you to save. Save, then choose Try again. Force close closes it without saving.",
                appNames, "Try again", offerForceClose: true);
            ShowOneShotLabel("Still open", $"{names}. Save, then say close; or say force close, or cancel.");
            await SpeakPushToTalkStepAsync(
                $"Still open: {names}. It may be asking you to save. Say close to try again, force close to close without saving, or cancel.",
                $"لسه مفتوح: {names}. ممكن يكون عايز يحفظ. قول اقفلهم تاني، أو اقفلهم غصب من غير حفظ، أو إلغاء.");
        }
    }

    private async Task CancelShutdownIfUnansweredAsync(int question, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(ShutdownAnswerSeconds), token);
            while (_pushToTalk != null || _pushToTalkHeld) await Task.Delay(200, token);
        }
        catch (OperationCanceledException) { return; }
        if (question != _shutdownQuestion || !_shutdownAwaitingClose) return;
        CancelShutdownCountdown();
        Keeper.SetVoiceStatus("failed");
        ShowOneShotLabel("Shutdown cancelled", "No answer.");
        await SpeakPushToTalkStepAsync("No answer. Shutdown cancelled.", "مفيش رد. تم إلغاء إيقاف التشغيل.");
    }

    private async Task CloseAppsThenShutDownAsync(bool force)
    {
        if (_shutdownCountdown is not { } shutdown) return;
        _shutdownAwaitingClose = false;
        _shutdownQuestion++;
        var apps = _appsToClose;
        Keeper.SetVoiceStatus("processing");
        ShowShutdownStep(force ? "Closing without saving" : "Closing apps",
            "CloClo shuts down when they have closed.", apps.Select(a => a.Name).Distinct(), null);
        ShowOneShotLabel(force ? "Closing without saving" : "Closing apps", string.Join(", ", apps.Select(a => a.Name).Distinct()));
        await SpeakPushToTalkStepAsync(force ? "Closing them without saving." : "Closing them.",
            force ? "بقفلهم من غير حفظ." : "بقفلهم.");
        foreach (var app in apps)
        {
            if (force) OpenApps.ForceClose(app);
            else OpenApps.Close(app);
        }
        try
        {
            var until = DateTime.UtcNow.AddSeconds(AppsCloseWaitSeconds);
            while (DateTime.UtcNow < until && apps.Any(OpenApps.IsStillOpen)) await Task.Delay(500, shutdown.Token);
        }
        catch (OperationCanceledException) { return; }
        if (_voiceActivationClosing || shutdown.IsCancellationRequested) return;

        // Listed again, so an app opened meanwhile is asked about too. After
        // "force close", what's left (an app running as administrator) gets
        // Windows' own shutdown notice instead.
        var remaining = OpenApps.List();
        if (remaining.Count > 0 && !force)
        {
            await AskToCloseAppsAsync(remaining, stillOpen: true);
            return;
        }
        ShowShutdownStep("Shutting down", "All apps are closed.", [], null);
        await SpeakPushToTalkStepAsync("All apps are closed. Shutting down now.", "قفلت كل البرامج. بقفل الجهاز دلوقتي.");
        if (shutdown.IsCancellationRequested || _voiceActivationClosing) return;
        var error = FullShutdown.Start();
        CancelShutdownCountdown();
        if (error == null) return;
        Keeper.SetVoiceStatus("failed");
        ShowOneShotLabel("Could not shut down", error);
        await SpeakPushToTalkStepAsync("Could not shut down.", "معرفتش أقفل الجهاز.");
    }

    // No apps open: announce, wait ShutdownCountdownSeconds (longer while a
    // hold is still being handled, so a "cancel" being said isn't cut off),
    // then shut down.
    private async Task CountDownToShutdownAsync()
    {
        using var countdown = new CancellationTokenSource();
        _shutdownCountdown = countdown;
        try
        {
            Keeper.SetVoiceStatus("processing");
            _shutdownSkipWait = false;
            ShowShutdownStep($"Shutting down in {ShutdownCountdownSeconds} seconds",
                "No apps are open. You can also hold right-click and say cancel.", [], "Shut down now");
            ShowOneShotLabel($"Shutting down in {ShutdownCountdownSeconds} seconds",
                "Hold right-click and say cancel to stop.");
            await SpeakPushToTalkStepAsync(
                $"No apps are open. Shutting down in {ShutdownCountdownSeconds} seconds. To cancel, hold right-click and say cancel.",
                $"مفيش برامج مفتوحة. الجهاز هيقفل خلال {ShutdownCountdownSeconds} ثواني. عشان تلغي، دوس كليك يمين مطول وقول إلغاء.");
            var until = DateTime.UtcNow.AddSeconds(ShutdownCountdownSeconds);
            while (!_shutdownSkipWait && (DateTime.UtcNow < until || _pushToTalk != null || _pushToTalkHeld))
                await Task.Delay(200, countdown.Token);
            if (_voiceActivationClosing) return;
            ShowShutdownStep("Shutting down", "", [], null);
            await SpeakPushToTalkStepAsync("Shutting down now.", "بقفل الجهاز دلوقتي.");
            countdown.Token.ThrowIfCancellationRequested();
            var error = FullShutdown.Start();
            CloseShutdownWindow();
            if (error == null) return;
            Keeper.SetVoiceStatus("failed");
            ShowOneShotLabel("Could not shut down", error);
            await SpeakPushToTalkStepAsync("Could not shut down.", "معرفتش أقفل الجهاز.");
        }
        catch (OperationCanceledException) { /* "cancel" was said */ }
        finally
        {
            if (ReferenceEquals(_shutdownCountdown, countdown)) _shutdownCountdown = null;
        }
    }

    // Ends whatever stage a voice shutdown is in. False when none was.
    private bool CancelShutdownCountdown()
    {
        _shutdownAwaitingClose = false;
        _appsToClose = [];
        _shutdownQuestion++;
        CloseShutdownWindow();
        if (_shutdownCountdown == null) return false;
        _shutdownCountdown.Cancel();
        _shutdownCountdown = null;
        return true;
    }

    // Each step is also spoken, for users who can't see the label. Only
    // fixed phrases and fixed app names are said, never the transcript.
    private async Task SpeakPushToTalkStepAsync(string english, string arabic)
    {
        if (_voiceActivationClosing || CloCloSoundPlaying) return; // don't talk over the azan or a reminder
        var useArabic = _settings.VoiceCommandsInArabic && SpeechReader.IsVoiceAvailable(ReadAloudVoice.ArabicHoda);
        try
        {
            await SpeakFixedVoiceResponseAsync(useArabic ? arabic : english,
                useArabic ? ReadAloudVoice.ArabicHoda : ReadAloudVoice.EnglishDavid);
        }
        catch { /* Spoken steps are optional; the label shows the same. */ }
    }

    private void AbortPushToTalk()
    {
        var held = _pushToTalkHeld;
        _pushToTalkHeld = false;
        var session = _pushToTalk;
        _pushToTalk = null;
        if (session == null) return;
        // A released session is already stopping in FinishPushToTalkAsync,
        // which drops its result once it sees it was aborted.
        if (held) _ = session.AbortAsync().AsTask();
        Keeper.SetVoiceStatus("off");
    }

    private void TogglePushToTalk()
    {
        _settings.PushToTalkEnabled = !_settings.PushToTalkEnabled;
        _settings.Save();
        if (!_settings.PushToTalkEnabled) CancelShutdownCountdown();
        UpdatePushToTalkHook();
        ShowOneShotLabel(_settings.PushToTalkEnabled ? "Hold to talk on" : "Hold to talk off",
            _settings.PushToTalkEnabled
                ? "Hold right-click anywhere, say an app name or shut down, then release."
                : "Right-click opens the menu only.");
    }
}

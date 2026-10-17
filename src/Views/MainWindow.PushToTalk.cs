using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloCloWidget;

// Hold to talk: holding the right mouse button anywhere on screen for
// PushToTalkHoldSeconds opens the microphone, and releasing it runs the
// approved app that was named, with no confirmation. Each step
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
        UpdatePushToTalkHook();
        ShowOneShotLabel(_settings.PushToTalkEnabled ? "Hold to talk on" : "Hold to talk off",
            _settings.PushToTalkEnabled
                ? "Hold right-click anywhere, say an app name, then release."
                : "Right-click opens the menu only.");
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Media = System.Windows.Media;

namespace CloCloWidget;

// One editor for microphone dictation, text-to-speech, and user-chosen text
// files. Windows Voice Typing (Win+H) types only into the focused text box;
// it is stopped whenever this window loses focus.
public partial class DictationCaptureWindow : Window
{
    private const ushort VK_H = 0x48;
    private const int MaxLoadedBytes = 256 * 1024;
    private const string EnglishLanguageTag = "en-US";
    private const string ArabicLanguageTag = "ar-EG";
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private static readonly Media.Brush ActiveLangBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0xc9, 0xa2, 0x4b));
    private static readonly Media.Brush InactiveLangBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0x3c, 0x2f, 0x1a));

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CultureInfo _originalInputLanguage = InputLanguageManager.Current.CurrentInputLanguage;
    private string _currentLanguageTag = EnglishLanguageTag;
    private int _micVersion;
    private bool _voiceTypingStarted;
    private HashSet<IntPtr> _textInputWindowsBefore = new();
    private bool _readActive;
    private PlaybackAudioTranscriber? _playback;
    private bool _playbackStarting;
    private string? _lastSavedText;

    // The parent retains the existing restricted append-to-dictation-folder flow.
    public event Func<string, string?>? SaveRequested;
    public event Action? OpenFolderRequested;

    public DictationCaptureWindow()
    {
        InitializeComponent();
        UpdateLanguageHighlight();
        SpeechReader.Finished += OnSpeechFinished;
        Loaded += (_, _) => CaptureBox.Focus();
        Deactivated += (_, _) =>
        {
            if (MicToggle.IsChecked != true) return;
            StopMicrophone();
            Instructions.Text = "Dictation paused because this window lost focus. Turn it on again to continue.";
        };
        Closing += (_, _) => StopMicrophone();
        Closed += async (_, _) =>
        {
            _lifetime.Cancel();
            SpeechReader.Finished -= OnSpeechFinished;
            StopMicrophone();
            await StopPlaybackAsync();
            StopReading();
            try { InputLanguageManager.Current.CurrentInputLanguage = _originalInputLanguage; }
            catch { /* the original layout may have been removed */ }
            _lifetime.Dispose();
        };
    }

    public void AddSelectedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var start = CaptureBox.Text.Length;
        if (start > 0 && !char.IsWhiteSpace(CaptureBox.Text[^1]))
        {
            CaptureBox.AppendText(Environment.NewLine);
            start = CaptureBox.Text.Length;
        }
        CaptureBox.AppendText(text);
        CaptureBox.Select(start, text.Length);
        Instructions.Text = "Selected text was added. Read aloud will speak this selection; clear the selection to read the whole box.";
    }

    public void StartReading()
    {
        var text = CaptureBox.SelectionLength > 0 ? CaptureBox.SelectedText : CaptureBox.Text;
        text = text.Trim();
        if (text.Length == 0)
        {
            ReadToggle.IsChecked = false;
            ReadToggle.Content = "Read aloud: Off";
            Instructions.Text = "Type, dictate, or load some text before reading aloud.";
            return;
        }

        _readActive = true;
        ReadToggle.IsChecked = true;
        ReadToggle.Content = "Read aloud: On";
        Instructions.Text = text.Length > SpeechReader.MaxSpokenChars
            ? $"Reading the first {SpeechReader.MaxSpokenChars:N0} characters. Select another part to read it."
            : "Reading from the text box. Turn Read aloud off to stop.";
        SpeechReader.Speak(text,
            onFailed: () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_readActive) return;
                StopReading();
                Instructions.Text = "Read aloud could not start. Check your audio output and speech voices.";
            })));
        if (MicToggle.IsChecked == true) CaptureBox.Focus();
    }

    private void ReadToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ReadToggle.IsChecked == true) StartReading();
        else StopReading();
    }

    private void StopReading()
    {
        if (_readActive) SpeechReader.Stop();
        _readActive = false;
        ReadToggle.IsChecked = false;
        ReadToggle.Content = "Read aloud: Off";
    }

    private void OnSpeechFinished() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_readActive) return;
        _readActive = false;
        ReadToggle.IsChecked = false;
        ReadToggle.Content = "Read aloud: Off";
        Instructions.Text = "Read aloud finished.";
    }));

    private async void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        if (MicToggle.IsChecked == true)
        {
            await StopPlaybackAsync();
            await StartMicrophoneAsync();
        }
        else StopMicrophone();
    }

    private async void PlaybackToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PlaybackToggle.IsChecked == true) await StartPlaybackAsync();
        else await StopPlaybackAsync();
    }

    private async Task StartPlaybackAsync()
    {
        if (_playback != null || _playbackStarting) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            !System.Runtime.Intrinsics.X86.Avx2.IsSupported ||
            !System.Runtime.Intrinsics.X86.Fma.IsSupported ||
            (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & (1 << 29)) == 0)
        {
            PlaybackToggle.IsChecked = false;
            Instructions.Text = "Local playback transcription needs Windows 11 and a CPU with AVX2, FMA, and F16C support.";
            return;
        }
        _playbackStarting = true;
        StopMicrophone();
        PlaybackToggle.IsEnabled = false;
        PlaybackToggle.Content = "Transcribe playing audio: Preparing";
        Instructions.Text = "Checking the local speech model. The first use downloads about 142 MiB; playback audio stays on this PC.";
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (IsLoaded)
                    Instructions.Text = $"Downloading local speech model: {bytes / 1048576} of 142 MiB. Audio capture has not started yet.";
            });
            await PlaybackModelStore.EnsureDownloadedAsync(progress, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            PlaybackToggle.IsChecked = false;
            return;
        }
        catch (Exception ex)
        {
            PlaybackToggle.IsChecked = false;
            PlaybackToggle.Content = "Transcribe playing audio: Off";
            Instructions.Text = $"Could not prepare the local speech model: {ex.Message}";
            return;
        }
        finally
        {
            _playbackStarting = false;
            PlaybackToggle.IsEnabled = true;
        }
        if (!IsLoaded || PlaybackToggle.IsChecked != true) return;
        var session = new PlaybackAudioTranscriber();
        _playback = session;
        PlaybackToggle.Content = "Transcribe playing audio: Starting";
        Instructions.Text = "Starting capture of the default Windows speaker output.";
        session.TextRecognized += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_playback != session || !IsLoaded) return;
            var separator = CaptureBox.Text.Length > 0 && !char.IsWhiteSpace(CaptureBox.Text[^1]) ? " " : "";
            if (CaptureBox.Text.Length + separator.Length + text.Length > CaptureBox.MaxLength)
            {
                _ = StopPlaybackWithMessageAsync("The editor is full. Save or clear text before transcribing more audio.");
                return;
            }
            CaptureBox.AppendText(separator + text);
        }));
        session.ProgressChanged += (recognized, _, bytes) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_playback != session || !IsLoaded) return;
            Instructions.Text = $"Listening to speaker output: {recognized} segments transcribed, {bytes / 32000} seconds of audio received. Only Save writes text to a file.";
        }));
        session.Failed += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_playback == session) _ = StopPlaybackWithMessageAsync(message);
        }));
        try
        {
            await session.StartAsync(_currentLanguageTag);
            if (_playback != session) return;
            PlaybackToggle.Content = "Transcribe playing audio: On";
            Instructions.Text = "Transcribing locally in 10-second chunks from the default output device. Turn this off to stop; only Save writes text to a file.";
        }
        catch (Exception ex)
        {
            if (_playback != session) return;
            var message = ex switch
            {
                InvalidDataException => ex.Message,
                DllNotFoundException or BadImageFormatException =>
                    "Could not load the local speech runtime. Check that CloClo is fully installed and the Visual C++ 2022 x64 runtime is present.",
                _ => $"Could not start local playback transcription ({ex.GetType().Name}). Check the model and output device."
            };
            await StopPlaybackWithMessageAsync(message);
        }
    }

    private async Task StopPlaybackWithMessageAsync(string message)
    {
        await StopPlaybackAsync();
        if (IsLoaded) Instructions.Text = message;
    }

    private async Task StopPlaybackAsync()
    {
        var session = _playback;
        _playback = null;
        PlaybackToggle.IsChecked = false;
        PlaybackToggle.Content = "Transcribe playing audio: Off";
        if (session != null) await session.DisposeAsync();
    }

    private async Task StartMicrophoneAsync()
    {
        var version = ++_micVersion;
        MicToggle.Content = "Dictate: Starting";
        CaptureBox.Focus();
        try
        {
            await Task.Delay(300, _lifetime.Token);
            if (!IsLoaded || !IsActive || MicToggle.IsChecked != true || version != _micVersion) return;
            InputLanguageManager.Current.CurrentInputLanguage = CultureInfo.GetCultureInfo(_currentLanguageTag);
            _textInputWindowsBefore = VisibleTextInputWindows();
            OpenVoiceTyping();
            _voiceTypingStarted = true;
            MicToggle.Content = "Dictate: On";
            Instructions.Text = "Windows Voice Typing is listening. Keep this text box active, or turn Dictate off to stop.";
        }
        catch (OperationCanceledException) { }
    }

    private void StopMicrophone()
    {
        if (!_voiceTypingStarted && MicToggle.IsChecked != true) return;
        var stopVersion = ++_micVersion;
        if (_voiceTypingStarted)
        {
            var windowsBefore = new HashSet<IntPtr>(_textInputWindowsBefore);
            CloseVoiceTyping(windowsBefore, includeExisting: true);
            _ = CloseVoiceTypingWhenShownAsync(windowsBefore, stopVersion);
        }
        _voiceTypingStarted = false;
        _textInputWindowsBefore.Clear();
        MicToggle.IsChecked = false;
        MicToggle.Content = "Dictate: Off";
    }

    private async Task CloseVoiceTypingWhenShownAsync(HashSet<IntPtr> windowsBefore, int stopVersion)
    {
        // Windows may create the Win+H toolbar after our close event. Only
        // retry for text-input windows that were absent before this session.
        await Task.Delay(350);
        if (stopVersion != Volatile.Read(ref _micVersion)) return;
        CloseVoiceTyping(windowsBefore, includeExisting: false);
        await Task.Delay(500);
        if (stopVersion != Volatile.Read(ref _micVersion)) return;
        CloseVoiceTyping(windowsBefore, includeExisting: false);
    }

    private async void English_Click(object sender, RoutedEventArgs e) => await SwitchLanguageAsync(EnglishLanguageTag);
    private async void Arabic_Click(object sender, RoutedEventArgs e) => await SwitchLanguageAsync(ArabicLanguageTag);

    private async Task SwitchLanguageAsync(string cultureTag)
    {
        if (cultureTag == _currentLanguageTag) return;
        var restartPlayback = _playback != null;
        if (restartPlayback) await StopPlaybackAsync();
        _currentLanguageTag = cultureTag;
        UpdateLanguageHighlight();
        if (MicToggle.IsChecked == true)
        {
            StopMicrophone();
            MicToggle.IsChecked = true;
            await StartMicrophoneAsync();
        }
        if (restartPlayback)
        {
            PlaybackToggle.IsChecked = true;
            await StartPlaybackAsync();
        }
    }

    private void UpdateLanguageHighlight()
    {
        EnglishButton.Background = _currentLanguageTag == EnglishLanguageTag ? ActiveLangBrush : InactiveLangBrush;
        ArabicButton.Background = _currentLanguageTag == ArabicLanguageTag ? ActiveLangBrush : InactiveLangBrush;
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        StopMicrophone();
        await StopPlaybackAsync();
        StopReading();
        var picker = new OpenFileDialog
        {
            Title = "Load text to read",
            Filter = "Text files|*.txt;*.md;*.log;*.csv|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (Directory.Exists(SpeechToText.DictationFolder))
            picker.InitialDirectory = SpeechToText.DictationFolder;
        if (picker.ShowDialog(this) != true) return;

        try
        {
            using var stream = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxLoadedBytes) throw new InvalidDataException();
            var bytes = new byte[MaxLoadedBytes + 1];
            var count = 0;
            int read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0)
                count += read;
            if (count > MaxLoadedBytes) throw new InvalidDataException();
            using var memory = new MemoryStream(bytes, 0, count, writable: false);
            using var reader = new StreamReader(memory, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Contains('\0')) throw new InvalidDataException();
            CaptureBox.Text = text;
            _lastSavedText = null;
            CaptureBox.CaretIndex = 0;
            Instructions.Text = $"Loaded {Path.GetFileName(picker.FileName)}. Read aloud speaks up to {SpeechReader.MaxSpokenChars:N0} characters at a time; select another part to read it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
        {
            Instructions.Text = $"Could not load that text file. Choose a readable text file under {MaxLoadedBytes / 1024} KB.";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        StopMicrophone();
        await StopPlaybackAsync();
        var text = CaptureBox.Text.Trim();
        if (text.Length == 0)
        {
            Instructions.Text = "There is no text to save.";
            return;
        }
        if (text == _lastSavedText)
        {
            Instructions.Text = "This text is already saved.";
            return;
        }
        var addedText = _lastSavedText != null && text.StartsWith(_lastSavedText, StringComparison.Ordinal)
            ? text[_lastSavedText.Length..].Trim()
            : text;
        if (addedText.Length == 0)
        {
            _lastSavedText = text;
            Instructions.Text = "This text is already saved.";
            return;
        }
        var path = SaveRequested?.Invoke(addedText);
        if (path == null)
        {
            Instructions.Text = "Dictation was not saved.";
            return;
        }
        _lastSavedText = text;
        Instructions.Text = $"Saved to {Path.GetFileName(path)}.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        StopMicrophone();
        OpenFolderRequested?.Invoke();
    }

    private static void OpenVoiceTyping() => KeyboardInput.SendChord(KeyboardInput.VK_LWIN, VK_H);

    // Win+H toggles the floating toolbar, so sending it again can reopen a
    // session Windows already closed. Prefer the small text-input windows
    // shown by this session, including the error popup if Windows opened it.
    private static void CloseVoiceTyping(HashSet<IntPtr> windowsBefore, bool includeExisting)
    {
        var visibleWindows = VisibleTextInputWindows();
        var targets = new HashSet<IntPtr>();
        foreach (var hWnd in visibleWindows)
        {
            if (windowsBefore.Contains(hWnd) || !GetWindowRect(hWnd, out var rect)) continue;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width is >= 80 and <= 550 && height is >= 25 and <= 180)
                targets.Add(hWnd);
        }

        // With the Windows voice-typing launcher enabled, Win+H can reuse a
        // toolbar that was already visible before this editor turned the mic
        // on. In that case, prefer the same small toolbar shape.
        if (targets.Count == 0 && includeExisting)
        {
            foreach (var hWnd in visibleWindows)
            {
                if (!GetWindowRect(hWnd, out var rect)) continue;
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width is not (>= 80 and <= 550) || height is not (>= 25 and <= 180)) continue;
                targets.Add(hWnd);
                break;
            }
        }

        // Older Windows builds used this class for the toolbar. Keep it as
        // a fallback if its reported size differs from the usual small bar.
        if (targets.Count == 0)
        {
            foreach (var hWnd in visibleWindows)
            {
                if (!includeExisting && windowsBefore.Contains(hWnd)) continue;
                var className = new StringBuilder(256);
                GetClassName(hWnd, className, className.Capacity);
                if (className.ToString() != "Windows.UI.Core.CoreWindow") continue;
                targets.Add(hWnd);
                break;
            }
        }
        foreach (var target in targets)
            PostMessage(target, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static HashSet<IntPtr> VisibleTextInputWindows()
    {
        var windows = new HashSet<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.ProcessName is "TextInputHost" or "InputApp") windows.Add(hWnd);
            }
            catch { /* process exited between enumeration and lookup */ }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}

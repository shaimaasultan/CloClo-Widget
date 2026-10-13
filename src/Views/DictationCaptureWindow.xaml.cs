using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Media = System.Windows.Media;

namespace CloCloWidget;

// One editor for local Whisper microphone/playback transcription, text-to-speech,
// and user-chosen text files. Audio results only append text to this editor.
public partial class DictationCaptureWindow : Window
{
    private const int MaxLoadedBytes = 256 * 1024;
    private const string EnglishLanguageTag = "en-US";
    private const string ArabicLanguageTag = "ar-EG";

    private static readonly Media.Brush ActiveLangBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0xc9, 0xa2, 0x4b));
    private static readonly Media.Brush InactiveLangBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0x3c, 0x2f, 0x1a));

    private string _currentLanguageTag = EnglishLanguageTag;
    private bool _readActive;
    private LocalAudioTranscriber? _microphone;
    private LocalAudioTranscriber? _drainingMicrophone;
    private LocalAudioTranscriber? _playback;
    private Task _microphoneStop = Task.CompletedTask;
    private Task _playbackStop = Task.CompletedTask;
    private string? _lastSavedText;

    // The parent retains the existing restricted append-to-dictation-folder flow.
    public event Func<string, string?>? SaveRequested;
    public event Action? OpenFolderRequested;
    public event Action? CaptureStopped;

    public DictationCaptureWindow()
    {
        InitializeComponent();
        UpdateLanguageHighlight();
        SpeechReader.Finished += OnSpeechFinished;
        Loaded += (_, _) => CaptureBox.Focus();
        Closed += async (_, _) =>
        {
            SpeechReader.Finished -= OnSpeechFinished;
            await StopMicrophoneAsync();
            await StopPlaybackAsync();
            StopReading();
            CaptureStopped?.Invoke();
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

        var voice = _currentLanguageTag == ArabicLanguageTag
            ? ReadAloudVoice.ArabicHoda
            : ReadAloudVoice.EnglishDavid;
        if (!SpeechReader.IsVoiceAvailable(voice))
        {
            ReadToggle.IsChecked = false;
            ReadToggle.Content = "Read aloud: Off";
            Instructions.Text = voice == ReadAloudVoice.ArabicHoda
                ? "Microsoft Hoda is not available. Install the Arabic speech voice in Windows settings."
                : "Microsoft David is not available. Install the English speech voice in Windows settings.";
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
            })),
            selectedVoice: voice);
        if (MicToggle.IsChecked == true) CaptureBox.Focus();
    }

    private void ReadToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ReadToggle.IsChecked == true && IsLoaded) StartReading();
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
            if (MicToggle.IsChecked == true && IsLoaded) await StartMicrophoneAsync();
        }
        else await StopMicrophoneAsync();
    }

    private void ReduceNoiseToggle_Click(object sender, RoutedEventArgs e)
    {
        _microphone?.SetMicrophoneNoiseReduction(ReduceNoiseToggle.IsChecked == true);
        if (_microphone != null)
            Instructions.Text = "Microphone noise filtering will change with the next audio segment. It cannot remove overlapping voices or TV audio.";
    }

    private void HighResolutionMicToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_microphone != null)
            Instructions.Text = "Turn Dictate off and on to switch microphone capture format. The current session keeps its displayed format.";
    }

    private async void PlaybackToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PlaybackToggle.IsChecked == true && IsLoaded) await StartPlaybackAsync();
        else await StopPlaybackAsync();
    }

    private async Task StartPlaybackAsync()
    {
        if (_playback != null) return;
        if (!CanRunLocalTranscription())
        {
            PlaybackToggle.IsChecked = false;
            Instructions.Text = "Local playback transcription needs Windows 11 and a CPU with AVX2, FMA, and F16C support.";
            return;
        }
        await StopMicrophoneAsync();
        await _playbackStop;
        if (_playback != null) return;
        if (PlaybackToggle.IsChecked != true || !IsLoaded) return;
        PlaybackToggle.Content = "Transcribe playing audio: Preparing";
        Instructions.Text = "Verifying the bundled speech model. Playback audio stays on this PC.";
        var session = new LocalAudioTranscriber(AudioCaptureSource.Playback);
        _playback = session;
        AttachRecognizedText(session);
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
            Instructions.Text = "Transcribing locally phrase by phrase from the default output device. Turn this off to stop; only Save writes text to a file.";
        }
        catch (Exception ex)
        {
            if (_playback != session) return;
            var message = ex switch
            {
                FileNotFoundException => "The bundled speech model or runtime is missing. Reinstall CloClo.",
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
        if (session != null) _playbackStop = session.DisposeAsync().AsTask();
        await _playbackStop;
    }

    private static bool CanRunLocalTranscription() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        System.Runtime.Intrinsics.X86.Avx2.IsSupported &&
        System.Runtime.Intrinsics.X86.Fma.IsSupported &&
        (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0;

    private void AttachRecognizedText(LocalAudioTranscriber session) =>
        session.TextRecognized += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if ((_microphone != session && _drainingMicrophone != session && _playback != session) || !IsLoaded) return;
            var separator = CaptureBox.Text.Length > 0 && !char.IsWhiteSpace(CaptureBox.Text[^1]) ? " " : "";
            if (CaptureBox.Text.Length + separator.Length + text.Length > CaptureBox.MaxLength)
            {
                const string message = "The editor is full. Save or clear text before transcribing more audio.";
                if (_microphone == session || _drainingMicrophone == session) _ = StopMicrophoneWithMessageAsync(message);
                else _ = StopPlaybackWithMessageAsync(message);
                return;
            }
            CaptureBox.AppendText(separator + text);
        }));

    private async Task StartMicrophoneAsync()
    {
        if (_microphone != null) return;
        if (!CanRunLocalTranscription())
        {
            MicToggle.IsChecked = false;
            Instructions.Text = "Local microphone dictation needs Windows 11 and a CPU with AVX2, FMA, and F16C support.";
            return;
        }
        await StopPlaybackAsync();
        await _microphoneStop;
        if (_microphone != null) return;
        if (MicToggle.IsChecked != true || !IsLoaded) return;
        MicToggle.Content = "Dictate: Preparing";
        Instructions.Text = "Verifying the bundled speech model. Microphone audio stays on this PC.";
        var session = new LocalAudioTranscriber(AudioCaptureSource.Microphone,
            reduceMicrophoneNoise: ReduceNoiseToggle.IsChecked == true,
            useHighResolutionMicrophone: HighResolutionMicToggle.IsChecked == true);
        _microphone = session;
        AttachRecognizedText(session);
        session.ProgressChanged += (recognized, _, bytes) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_microphone != session || !IsLoaded) return;
            Instructions.Text = $"Listening to {MicrophoneLabel(session)}: {recognized} segments transcribed, {bytes / session.CaptureBytesPerSecond} seconds of audio received. Only Save writes text to a file.";
        }));
        session.Failed += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_microphone == session) _ = StopMicrophoneWithMessageAsync(message);
        }));
        try
        {
            await session.StartAsync(_currentLanguageTag);
            if (_microphone != session) return;
            MicToggle.Content = "Dictate: On";
            Instructions.Text = $"Transcribing locally from {MicrophoneLabel(session)} phrase by phrase. Turn this off to stop; only Save writes text to a file.";
        }
        catch (Exception ex)
        {
            if (_microphone != session) return;
            var message = ex switch
            {
                FileNotFoundException => "The bundled speech model or runtime is missing. Reinstall CloClo.",
                InvalidDataException => ex.Message,
                DllNotFoundException or BadImageFormatException =>
                    "Could not load the local speech runtime. Check that CloClo is fully installed and the Visual C++ 2022 x64 runtime is present.",
                UnauthorizedAccessException => "Microphone access was denied. Check Windows microphone privacy settings.",
                NotSupportedException or InvalidOperationException when HighResolutionMicToggle.IsChecked == true =>
                    "This input device could not start 24-bit, 48 kHz capture. Turn off the comparison checkbox to use the original mode.",
                _ => $"Could not start local microphone dictation ({ex.GetType().Name}). Check the default input device and microphone permission."
            };
            await StopMicrophoneWithMessageAsync(message);
        }
    }

    private static string MicrophoneLabel(LocalAudioTranscriber session) =>
        $"{(string.IsNullOrWhiteSpace(session.DeviceName) ? "the default microphone" : session.DeviceName)} ({session.CaptureFormat ?? "unknown format"})";

    private async Task StopMicrophoneWithMessageAsync(string message)
    {
        await StopMicrophoneAsync();
        if (IsLoaded) Instructions.Text = message;
    }

    private async Task StopMicrophoneAsync()
    {
        var session = _microphone;
        _microphone = null;
        MicToggle.IsChecked = false;
        MicToggle.Content = "Dictate: Off";
        if (session != null)
        {
            _drainingMicrophone = session;
            _microphoneStop = DrainMicrophoneAsync(session);
        }
        await _microphoneStop;
    }

    private async Task DrainMicrophoneAsync(LocalAudioTranscriber session)
    {
        await session.DisposeAsync();
        // Drain text events queued by the final Whisper chunk before Save
        // reads the editor or another capture session can start.
        await Dispatcher.InvokeAsync(() => { });
        if (_drainingMicrophone == session) _drainingMicrophone = null;
    }

    private async void English_Click(object sender, RoutedEventArgs e) => await SwitchLanguageAsync(EnglishLanguageTag);
    private async void Arabic_Click(object sender, RoutedEventArgs e) => await SwitchLanguageAsync(ArabicLanguageTag);

    private async Task SwitchLanguageAsync(string cultureTag)
    {
        if (cultureTag == _currentLanguageTag) return;
        if (_readActive) StopReading();
        var restartMicrophone = _microphone != null;
        var restartPlayback = _playback != null;
        if (restartMicrophone) await StopMicrophoneAsync();
        if (restartPlayback) await StopPlaybackAsync();
        _currentLanguageTag = cultureTag;
        UpdateLanguageHighlight();
        if (restartMicrophone)
        {
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
        // Lay Arabic out right to left without adding direction marks to the
        // text itself, so saved dictation files contain only what was said.
        CaptureBox.FlowDirection = _currentLanguageTag == ArabicLanguageTag
            ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        await StopMicrophoneAsync();
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
        await StopMicrophoneAsync();
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

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        await StopMicrophoneAsync();
        OpenFolderRequested?.Invoke();
    }
}

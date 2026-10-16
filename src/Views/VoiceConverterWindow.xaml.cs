using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloCloWidget;

// Converts a recording of one word into Windows David's voice and shows it
// beside David saying that word and David saying another word: four
// signals, each playable. The conversion takes only what David's voice has
// in general (pitch, mouth size, colour), so the word stays the person's.
// Signals only: no model, no FFT. Nothing is saved.
public partial class VoiceConverterWindow : Window
{
    private const int MaximumRecordingSeconds = 6;

    private static readonly Brush MeBrush = Frozen("#7FB8E0");
    private static readonly Brush ConvertedBrush = Frozen("#7FD18B");
    private static readonly Brush SaidBrush = Frozen("#E8A75A");
    private static readonly Brush OtherBrush = Frozen("#C98BE0");

    private DavidWordMatcher.Word? _said;
    private DavidWordMatcher.Word? _other;
    private float[]? _me;
    private float[]? _converted;
    private MicrophoneRecording? _recorder;
    private int _version;
    private readonly DispatcherTimer _liveDraw = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _wordsChanged = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private System.Media.SoundPlayer? _player;
    private bool _closing;

    public VoiceConverterWindow()
    {
        InitializeComponent();
        _liveDraw.Tick += (_, _) => DrawLiveRecording();
        _wordsChanged.Tick += async (_, _) =>
        {
            _wordsChanged.Stop();
            await PrepareDavidAsync();
        };
        SaidBox.TextChanged += (_, _) => { _wordsChanged.Stop(); _wordsChanged.Start(); };
        OtherBox.TextChanged += (_, _) => { _wordsChanged.Stop(); _wordsChanged.Start(); };
        SizeChanged += (_, _) => DrawAll();
        Loaded += async (_, _) => await PrepareDavidAsync();
        Closed += async (_, _) =>
        {
            _closing = true;
            _liveDraw.Stop();
            _wordsChanged.Stop();
            _player?.Stop();
            _player?.Dispose();
            if (_recorder != null) await _recorder.DisposeAsync();
        };
    }

    // ------------------------------------------------------------ David's two words

    private async Task PrepareDavidAsync()
    {
        var said = SaidBox.Text.Trim();
        var other = OtherBox.Text.Trim();
        SaidTitle.Text = said.Length == 0 ? "3 · David: word I said" : $"3 · David: “{said}”";
        OtherTitle.Text = other.Length == 0 ? "4 · David: other word" : $"4 · David: “{other}”";
        _said = _other = null;
        PlaySaidButton.IsEnabled = PlayOtherButton.IsEnabled = false;
        var texts = new[] { said, other }.Where(t => t.Length > 0).ToList();
        if (texts.Count == 0) { DrawAll(); return; }
        SaidInfo.Text = OtherInfo.Text = "David is saying it…";
        try
        {
            // The same David take as the other voice pages this session.
            var words = await DavidWordMatcher.PrepareAsync(texts);
            if (_closing) return;
            if (words == null) { SaidInfo.Text = OtherInfo.Text = "Windows David isn't installed."; return; }
            // Only use the result if the boxes still say the same.
            if (SaidBox.Text.Trim() != said || OtherBox.Text.Trim() != other) return;
            _said = words.FirstOrDefault(w => w.Text.Equals(said, StringComparison.OrdinalIgnoreCase));
            _other = words.FirstOrDefault(w => w.Text.Equals(other, StringComparison.OrdinalIgnoreCase));
            SaidInfo.Text = _said == null ? (said.Length == 0 ? "" : "David said nothing for this.") : Describe(_said.Samples);
            OtherInfo.Text = _other == null ? (other.Length == 0 ? "" : "David said nothing for this.") : Describe(_other.Samples);
            PlaySaidButton.IsEnabled = _said != null;
            PlayOtherButton.IsEnabled = _other != null;
            await ConvertAsync(); // colour and mouth size come from David's words
        }
        catch (Exception ex)
        {
            SaidInfo.Text = OtherInfo.Text = $"Couldn't make David's words ({ex.GetType().Name}).";
        }
    }

    private void Swap_Click(object sender, RoutedEventArgs e) =>
        (SaidBox.Text, OtherBox.Text) = (OtherBox.Text, SaidBox.Text);

    // ------------------------------------------------------------ recording

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder != null)
        {
            await StopRecordingAsync();
            return;
        }
        try
        {
            var recording = MicrophoneRecording.Start();
            _recorder = recording;
            RecordButton.Content = "■ Stop";
            RecordStatus.Text = "Starting the microphone — wait for “speak now”…";
            recording.Listening += () => Dispatcher.BeginInvoke(() =>
            {
                if (_recorder == recording)
                    RecordStatus.Text = $"Speak now: say “{SaidBox.Text.Trim()}”, then press Stop (stops by itself after {MaximumRecordingSeconds} s).";
            });
            _liveDraw.Start();
        }
        catch (Exception ex)
        {
            _recorder = null;
            RecordStatus.Text = $"Couldn't open the microphone ({ex.GetType().Name}).";
        }
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _recorder = null;
        _liveDraw.Stop();
        var recorded = await recorder.StopAsync();
        if (_closing) return;
        RecordButton.Content = "● Record me";
        if (recorded.Length < VoiceSamples.SampleRate / 10) { RecordStatus.Text = "Nothing was recorded."; return; }
        _me = VoiceSamples.TrimSilence(recorded);
        PlayMeButton.IsEnabled = true;
        RecordStatus.Text = $"Recorded “{SaidBox.Text.Trim()}”.";
        await ConvertAsync();
    }

    private void DrawLiveRecording()
    {
        if (_recorder == null) return;
        var recorded = _recorder.Snapshot();
        VoiceSignalsWindow.DrawSignal(MeCanvas, recorded, MeBrush, MaximumRecordingSeconds * VoiceSamples.SampleRate);
        MeInfo.Text = $"{recorded.Length / (double)VoiceSamples.SampleRate:0.0} s";
        if (recorded.Length >= MaximumRecordingSeconds * VoiceSamples.SampleRate) _ = StopRecordingAsync();
    }

    // ------------------------------------------------------------ converting

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = ConvertAsync();
    }

    private void Pitch_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PitchText == null) return; // still loading
        PitchText.Text = $"{PitchSlider.Value:0} Hz";
        Option_Changed(sender, e);
    }

    private void MouthBox_Changed(object sender, SelectionChangedEventArgs e) => Option_Changed(sender, e);

    // David's voice in general here: both of his words one after the other.
    private float[]? DavidVoice() =>
        _said == null && _other == null ? null
            : (_said?.Samples ?? []).Concat(_other?.Samples ?? []).ToArray();

    // Your word in David's voice: his pitch, and optionally his mouth size
    // and colour - all from his voice in general, so the word stays yours.
    private async Task ConvertAsync()
    {
        if (_me == null || _recorder != null) { DrawAll(); return; }
        var version = ++_version;
        var me = _me;
        var voice = DavidVoice();
        var mouth = (MouthBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1";
        var options = new VoiceConverter.Options(
            PitchHz: (float)PitchSlider.Value,
            FollowIntonation: IntonationCheck.IsChecked == true,
            MouthScale: mouth == "voice" ? 1f : float.Parse(mouth, CultureInfo.InvariantCulture),
            MatchColor: ColourCheck.IsChecked == true && voice != null,
            ColourFrom: ColourCheck.IsChecked == true ? voice : null);
        var said = _said;
        var other = _other;
        ConvertedInfo.Text = "Converting…";

        var (converted, scale, scores) = await Task.Run(() =>
        {
            var o = mouth == "voice" && voice != null ? options with { MouthScale = VoiceConverter.VoiceMouthScale(me, voice) } : options;
            var result = VoiceConverter.ToDavidStyle(me, o);
            // How close the converted word is to each of David's two words,
            // measured as on the "Find it in David's words" page.
            var words = new[] { said, other }.Where(w => w != null).Select(w => w!).ToList();
            var ranked = words.Count == 0 ? [] : DavidWordMatcher.Rank(me, words, o, false);
            return (result.Samples, o.MouthScale, ranked);
        });
        if (_closing || version != _version) return;

        _converted = converted;
        PlayConvertedButton.IsEnabled = converted.Length > 0;
        ConvertedInfo.Text = Describe(converted) + $" · David's pitch {PitchSlider.Value:0} Hz" +
                             (scale != 1 ? $", mouth size {scale:0.00}" : "") +
                             (options.MatchColor ? ", his colour" : "");
        ScoreText.Text = scores.Count == 0 ? ""
            : "Converted voice compared with each David word (same length, moment by moment): " +
              string.Join("  ·  ", scores.Select(m => $"“{m.Word.Text}” {m.Score:P0}")) +
              $"  →  closer to “{scores[0].Word.Text}”";
        DrawAll();
    }

    // ------------------------------------------------------------ drawing and playing

    private void DrawAll()
    {
        if (MeCanvas == null || _recorder != null) return;
        var signals = new[] { _me, _converted, _said?.Samples, _other?.Samples };
        // Same time scale: the longest fills the width. Otherwise each signal
        // is stretched across the full width.
        var same = SameScaleCheck.IsChecked == true;
        var longest = signals.Max(s => s?.Length ?? 0);
        int Across(float[]? s) => same ? longest : s?.Length ?? 0;
        VoiceSignalsWindow.DrawSignal(MeCanvas, _me, MeBrush, Across(_me));
        VoiceSignalsWindow.DrawSignal(ConvertedCanvas, _converted, ConvertedBrush, Across(_converted));
        VoiceSignalsWindow.DrawSignal(SaidCanvas, _said?.Samples, SaidBrush, Across(_said?.Samples));
        VoiceSignalsWindow.DrawSignal(OtherCanvas, _other?.Samples, OtherBrush, Across(_other?.Samples));
        if (_me != null) MeInfo.Text = Describe(_me);
    }

    private static string Describe(float[] samples) =>
        $"{samples.Length * 1000 / VoiceSamples.SampleRate} ms, peak {(samples.Length == 0 ? 0 : samples.Max(Math.Abs)):0.00} (drawn scaled to fit)";

    private void PlayMe_Click(object sender, RoutedEventArgs e) { if (_me != null) Play(VoiceSamples.ToWav(_me)); }
    private void PlayConverted_Click(object sender, RoutedEventArgs e) { if (_converted is { Length: > 0 }) Play(VoiceSamples.ToWav(_converted)); }
    private void PlaySaid_Click(object sender, RoutedEventArgs e) { if (_said != null) Play(_said.Speech.Wav); }
    private void PlayOther_Click(object sender, RoutedEventArgs e) { if (_other != null) Play(_other.Speech.Wav); }

    private void Play(byte[] wav)
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = new System.Media.SoundPlayer(new MemoryStream(wav));
            _player.Play();
        }
        catch
        {
            RecordStatus.Text = "Couldn't play the sound.";
        }
    }

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}

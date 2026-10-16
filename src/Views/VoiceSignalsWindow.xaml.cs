using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CloCloWidget;

// Three raw signals of the same word, one above the other: Windows David
// saying it, an Arabic Windows voice (Hoda or Naayf) saying it, and the
// person through the microphone.
// No analysis - just the samples, drawn as they are. Everything stays in
// memory; nothing is saved.
public partial class VoiceSignalsWindow : Window
{
    private const int MaximumRecordingSeconds = 6;

    private static readonly Brush DavidBrush = Frozen("#E8A75A");
    private static readonly Brush HodaBrush = Frozen("#C98BE0");
    private static readonly Brush MeBrush = Frozen("#7FB8E0");
    private static readonly Brush ConvertedBrush = Frozen("#7FD18B");
    internal static readonly Brush AxisBrush = Frozen("#2A3A48");

    private VoiceSamples.Speech? _david;
    private VoiceSamples.Speech? _hoda;
    private float[]? _me;
    private byte[]? _meWav;
    private string? _meWord;
    private float[]? _converted;
    private string? _davidText;
    private string? _hodaText;

    private MicrophoneRecording? _recorder;
    private readonly DispatcherTimer _liveDraw = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private System.Media.SoundPlayer? _player;
    private bool _closing;

    public VoiceSignalsWindow()
    {
        InitializeComponent();
        _liveDraw.Tick += (_, _) => DrawLiveRecording();
        WordBox.TextChanged += (_, _) => ForgetVoices();
        SizeChanged += (_, _) => Redraw(this, new RoutedEventArgs());
        Closed += async (_, _) =>
        {
            _closing = true;
            _player?.Stop();
            _player?.Dispose();
            await StopRecordingAsync(keep: false);
        };
    }

    private string Word => WordBox.Text.Trim();

    private static bool HasArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ');
    private static bool HasLatin(string text) => text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    // A changed word makes the voices' signals out of date.
    private void ForgetVoices()
    {
        _david = null;
        _hoda = null;
        DavidCanvas.Children.Clear();
        HodaCanvas.Children.Clear();
        DavidInfo.Text = "";
        HodaInfo.Text = "";
        if (_me != null) MeInfo.Text = $"recorded for “{_meWord}” — record again for “{Word}”";
    }

    // ------------------------------------------------------------ David and Hoda

    private async void David_Click(object sender, RoutedEventArgs e)
    {
        if (Word.Length == 0) { StatusText.Text = "Type a word first."; return; }
        var text = HasArabic(Word) && !HasLatin(Word) ? VoiceSamples.ToLatin(Word) : Word;
        var hadDavid = _david != null;
        _david ??= await SpeakDavidAsync(text);
        _davidText = text;
        if (!hadDavid && _david != null && _me != null) Convert();
        Show(_david, DavidInfo);
    }

    // Hoda (female) or Naayf (male), the two Arabic voices Windows offers.
    private string ArabicVoice => (ArabicVoiceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Hoda";

    private void ArabicVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HodaButton == null || HodaCanvas == null) return; // still loading
        HodaButton.Content = $"▶ Hear {ArabicVoice}";
        _hoda = null;
        HodaCanvas.Children.Clear();
        HodaInfo.Text = "";
        Redraw(this, new RoutedEventArgs());
    }

    private async void Hoda_Click(object sender, RoutedEventArgs e)
    {
        if (Word.Length == 0) { StatusText.Text = "Type a word first."; return; }
        var text = HasArabic(Word) ? Word : VoiceSamples.ToArabic(Word);
        _hoda ??= await SpeakAsync(HodaButton, $"Microsoft {ArabicVoice}", "ar", text, HodaInfo);
        _hodaText = text;
        Show(_hoda, HodaInfo);
    }

    // David's take of the word is shared with the "Find it in David's
    // words" page, so the same settings sound the same on both.
    private async Task<VoiceSamples.Speech?> SpeakDavidAsync(string text)
    {
        DavidButton.IsEnabled = false;
        try
        {
            var words = await DavidWordMatcher.PrepareAsync([text]);
            if (words == null) DavidInfo.Text = "Microsoft David isn't installed (Settings → Time & language → Speech).";
            return words is [var word] ? word.Speech with { Samples = word.Samples } : null;
        }
        catch (Exception ex)
        {
            DavidInfo.Text = $"Couldn't make the voice ({ex.GetType().Name}).";
            return null;
        }
        finally
        {
            DavidButton.IsEnabled = true;
        }
    }

    private async Task<VoiceSamples.Speech?> SpeakAsync(Button button, string voice, string language, string text, TextBlock info)
    {
        button.IsEnabled = false;
        try
        {
            var speech = await VoiceSamples.SpeakAsync(voice, language, text);
            if (speech == null) info.Text = $"{voice} isn't installed (Settings → Time & language → Speech).";
            return speech == null ? null : speech with { Samples = VoiceSamples.TrimSilence(speech.Samples) };
        }
        catch (Exception ex)
        {
            info.Text = $"Couldn't make the voice ({ex.GetType().Name}).";
            return null;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Show(VoiceSamples.Speech? speech, TextBlock info)
    {
        if (speech == null || _closing) return;
        if (speech.Samples.Length == 0) { info.Text = "The voice said nothing for this text."; return; }
        Play(speech.Wav);
        Redraw(this, new RoutedEventArgs());
    }

    // ------------------------------------------------------------ you

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder != null)
        {
            await StopRecordingAsync(keep: true);
            return;
        }
        if (Word.Length == 0) { StatusText.Text = "Type a word first."; return; }
        try
        {
            var recording = MicrophoneRecording.Start();
            _recorder = recording;
            RecordButton.Content = "■ Stop";
            MeInfo.Text = "starting the microphone — wait for “speak now”…";
            recording.Listening += () => Dispatcher.BeginInvoke(() =>
            {
                if (_recorder == recording)
                    MeInfo.Text = $"speak now: say “{Word}”, then press Stop (stops by itself after {MaximumRecordingSeconds} s)";
            });
            _liveDraw.Start();
        }
        catch (Exception ex)
        {
            _recorder = null;
            MeInfo.Text = $"Couldn't open the microphone ({ex.GetType().Name}).";
        }
    }

    private async Task StopRecordingAsync(bool keep)
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _recorder = null;
        _liveDraw.Stop();
        var recorded = await recorder.StopAsync();
        if (_closing || !keep) return;

        RecordButton.Content = "● Record me";
        if (recorded.Length == 0) { MeInfo.Text = "Nothing was recorded."; return; }
        _me = VoiceSamples.TrimSilence(recorded);
        _meWav = VoiceSamples.ToWav(_me);
        _meWord = Word;
        HearMeButton.IsEnabled = true;
        Convert();
        Redraw(this, new RoutedEventArgs());
    }

    // While recording: the last few seconds as they come in.
    private void DrawLiveRecording()
    {
        if (_recorder == null) return;
        var recorded = _recorder.Snapshot();
        DrawSignal(MeCanvas, recorded, MeBrush, MaximumRecordingSeconds * VoiceSamples.SampleRate);
        if (recorded.Length > 0) MeInfo.Text = $"speak now: say “{Word}” — {recorded.Length / (double)VoiceSamples.SampleRate:0.0} s";
        if (recorded.Length >= MaximumRecordingSeconds * VoiceSamples.SampleRate) _ = StopRecordingAsync(keep: true);
    }

    // ------------------------------------------------------------ you, David style

    private void Convert()
    {
        _converted = null;
        ConvertedInfo.Text = "";
        HearConvertedButton.IsEnabled = false;
        if (_me == null) return;

        var mouth = (MouthBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1";
        var options = new VoiceConverter.Options(
            PitchHz: (float)PitchSlider.Value,
            FollowIntonation: IntonationCheck.IsChecked == true,
            MouthScale: mouth == "fit" ? 1f : float.Parse(mouth, System.Globalization.CultureInfo.InvariantCulture),
            FitMouth: mouth == "fit",
            MatchColor: ColourCheck.IsChecked == true,
            MatchMelody: MelodyCheck.IsChecked == true,
            MatchTiming: TimingCheck.IsChecked == true,
            MatchLoudness: LoudnessCheck.IsChecked == true,
            MatchMouth: MouthShapeCheck.IsChecked == true);
        // David's recording of the same word, for the options that borrow
        // from him; without it those options are left off.
        var needsDavid = options.FitMouth || options.MatchColor || options.MatchMelody || options.MatchTiming ||
                         options.MatchLoudness || options.MatchMouth;
        var david = _david?.Samples;
        var result = VoiceConverter.ToDavidStyle(_me, options, david);
        _converted = result.Samples;
        HearConvertedButton.IsEnabled = _converted.Length > 0;

        var notes = new List<string>();
        if (needsDavid && david == null) notes.Add("press ▶ Hear David first — the David options need his word");
        if (options.FitMouth && david != null) notes.Add($"mouth size fitted: {result.MouthScale:0.00}");
        if (david != null && options.MatchMouth)
            notes.Add("David's mouth is the word itself: from here on you hear his word, whatever you said" +
                      (options.MatchColor ? " (his mouth already has his colour)" : ""));
        else if (david != null && (options.FitMouth || options.MatchMelody || options.MatchTiming || options.MatchLoudness))
            notes.Add("borrows from David's word, so it moves toward his word whatever you said — fine for seeing, not for telling words apart");
        ConvertedInfo.Text = string.Join(" · ", notes);
    }

    private void MouthBox_Changed(object sender, SelectionChangedEventArgs e) => ConversionOption_Changed(sender, e);

    private void ConversionOption_Changed(object sender, RoutedEventArgs e)
    {
        if (PitchText == null || ConvertedCanvas == null || ConvertedInfo == null) return; // still loading
        PitchText.Text = $"{PitchSlider.Value:0} Hz";
        if (_me == null || _recorder != null) return;
        Convert();
        Redraw(this, new RoutedEventArgs());
    }

    private void HearConverted_Click(object sender, RoutedEventArgs e)
    {
        if (_converted is { Length: > 0 }) Play(VoiceSamples.ToWav(_converted));
    }

    private void HearMe_Click(object sender, RoutedEventArgs e)
    {
        if (_meWav != null) Play(_meWav);
    }

    // ------------------------------------------------------------ drawing

    private void Redraw(object sender, RoutedEventArgs e)
    {
        if (DavidCanvas == null || OutlineCanvas == null || _recorder != null) return;
        var david = _david?.Samples;
        var hoda = _hoda?.Samples;
        var me = _me;

        // The timeline everything is put on: yours, or David's before you record.
        var target = me ?? david ?? hoda;
        float[]? Outline(float[]? x) => x == null ? null : SignalAlignment.Outline(x);
        var (davidLine, hodaLine, meLine) = (Outline(david), Outline(hoda), Outline(me));
        var targetFeatures = target == null ? null : SignalAlignment.Measure(target);

        var timeNote = "";
        if (target != null && StretchedRadio.IsChecked == true)
        {
            (david, davidLine) = Stretched(david, davidLine, target.Length);
            (hoda, hodaLine) = Stretched(hoda, hodaLine, target.Length);
            timeNote = me != null ? " (stretched to your length)" : "";
        }
        else if (target != null && targetFeatures != null && LinedUpRadio.IsChecked == true)
        {
            (david, davidLine) = LinedUp(david, davidLine, targetFeatures, ReferenceEquals(target, _david?.Samples));
            (hoda, hodaLine) = LinedUp(hoda, hodaLine, targetFeatures, ReferenceEquals(target, _hoda?.Samples));
            timeNote = me != null ? " (lined up with yours)" : " (lined up with David's)";
        }

        // Same time scale for all three: the longest fills the width.
        var longest = new[] { david, hoda, me }.Max(x => x?.Length ?? 0);
        DrawSignal(DavidCanvas, david, DavidBrush, longest);
        DrawSignal(HodaCanvas, hoda, HodaBrush, longest);
        DrawSignal(MeCanvas, me, MeBrush, longest);
        DrawSignal(ConvertedCanvas, _converted, ConvertedBrush, longest);
        DrawOutlines(longest / SignalAlignment.BlockSamples, (davidLine, DavidBrush), (hodaLine, HodaBrush), (meLine, MeBrush));

        // Arabic text is isolated so it can't reorder the rest of the line.
        if (_david != null) DavidInfo.Text = $"said “\u2068{_davidText}\u2069” — " + Describe(_david.Samples) + timeNote;
        if (_hoda != null) HodaInfo.Text = $"said “\u2068{_hodaText}\u2069” — " + Describe(_hoda.Samples) + timeNote;
        if (_converted is { Length: > 0 })
            ConvertedCanvas.ToolTip = "Your mouth shapes, played from sharp pulses at a low pitch, as Windows David's voice is made.";
        if (_me != null) MeInfo.Text = Describe(_me) + ClippingNote(_me) + (_meWord == Word ? "" : $" — recorded for “\u2068{_meWord}\u2069”");
        ScoresText.Text = Scores();
    }

    private static (float[]?, float[]?) Stretched(float[]? samples, float[]? outline, int length) =>
        samples == null ? (null, null)
            : (SignalAlignment.Stretch(samples, length), SignalAlignment.Stretch(outline!, length / SignalAlignment.BlockSamples));

    private static (float[]?, float[]?) LinedUp(float[]? samples, float[]? outline, SignalAlignment.Features target, bool isTarget)
    {
        if (samples == null || isTarget) return (samples, outline);
        var alignment = SignalAlignment.Align(target, SignalAlignment.Measure(samples));
        return alignment == null ? (samples, outline)
            : (SignalAlignment.Warp(samples, alignment), SignalAlignment.WarpOutline(outline!, alignment));
    }

    // Two scores for each pair, whatever the view: loudness and
    // zero-crossings once lined up by DTW, and the mouth's shape once lined
    // up (the source - pitch and pulse shape - left out entirely).
    private string Scores()
    {
        var voices = new List<(string Name, float[] Samples)>();
        if (_me != null) voices.Add(("You", _me));
        if (_converted is { Length: > 0 }) voices.Add(("You→David style", _converted));
        if (_david != null) voices.Add(("David", _david.Samples));
        if (_hoda != null) voices.Add((ArabicVoice, _hoda.Samples));
        var features = voices.Select(v => SignalAlignment.Measure(v.Samples)).ToList();
        var mouths = voices.Select(v => VoiceConverter.MouthShapes(v.Samples)).ToList();
        var loudness = new List<string>();
        var mouth = new List<string>();
        for (var i = 0; i < voices.Count; i++)
            for (var j = i + 1; j < voices.Count; j++)
            {
                var pair = $"{voices[i].Name} vs {voices[j].Name}";
                if (SignalAlignment.Align(features[i], features[j]) is { } alignment) loudness.Add($"{pair} {alignment.Similarity:P0}");
                mouth.Add($"{pair} {VoiceConverter.MouthSimilarity(mouths[i], mouths[j]):P0}");
            }
        return loudness.Count == 0 ? ""
            : $"loudness + crossings: {string.Join(", ", loudness)}\nmouth shape: {string.Join(", ", mouth)}";
    }

    private void DrawOutlines(int blocksAcrossWidth, params (float[]? Line, Brush Brush)[] outlines)
    {
        OutlineCanvas.Children.Clear();
        var width = OutlineCanvas.ActualWidth;
        var height = OutlineCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || blocksAcrossWidth <= 1) return;
        foreach (var (line, brush) in outlines)
        {
            if (line == null || line.Length < 2) continue;
            var points = new PointCollection(line.Length);
            for (var i = 0; i < line.Length; i++)
                points.Add(new Point(i * width / (blocksAcrossWidth - 1), height - 4 - line[i] * (height - 8)));
            OutlineCanvas.Children.Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = 2, Opacity = 0.9 });
        }
    }

    // Samples at the very top of the range were cut flat by the microphone,
    // which changes the wave's shape.
    private static string ClippingNote(float[] samples)
    {
        var clipped = samples.Count(x => Math.Abs(x) >= 0.999f);
        var volume = MicrophoneVolume();
        var volumeText = volume == null ? "" : $", mic input volume {volume:P0}";
        return clipped == 0 ? volumeText
            : $" — ⚠ {clipped} samples clipped{volumeText}: lower the input volume in Settings → System → Sound → your microphone";
    }

    // The Windows input volume of the microphone being recorded (read only).
    private static float? MicrophoneVolume()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(float[] samples)
    {
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        return $"{samples.Length * 1000 / VoiceSamples.SampleRate} ms, {samples.Length:N0} samples, peak {peak:0.000} (drawn scaled to fit)";
    }

    // The samples as they are: for each pixel column, a line from the lowest
    // to the highest sample in it. Scaled so the loudest sample nearly
    // fills the lane, since the microphone is much quieter than the voices.
    internal static void DrawSignal(Canvas canvas, float[]? samples, Brush brush, int samplesAcrossWidth)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var middle = height / 2;
        canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = middle, Y2 = middle, Stroke = AxisBrush, StrokeThickness = 1 });
        if (samples == null || samples.Length == 0) return;

        var across = Math.Max(samples.Length, samplesAcrossWidth);
        var peak = Math.Max(1e-4f, samples.Max(Math.Abs));
        var scale = middle * 0.92 / peak;
        var columns = (int)Math.Ceiling(width * samples.Length / across);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var x = 0; x < columns; x++)
            {
                var from = (int)((long)x * across / width);
                var to = Math.Min(samples.Length, (int)((long)(x + 1) * across / width) + 1);
                if (from >= to) continue;
                float low = samples[from], high = samples[from];
                for (var i = from + 1; i < to; i++)
                {
                    if (samples[i] < low) low = samples[i];
                    if (samples[i] > high) high = samples[i];
                }
                context.BeginFigure(new Point(x + 0.5, middle - high * scale), false, false);
                context.LineTo(new Point(x + 0.5, middle - low * scale + 0.5), true, false);
            }
        }
        geometry.Freeze();
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = brush, StrokeThickness = 1 });
    }

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
            StatusText.Text = "Couldn't play the sound.";
        }
    }

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}

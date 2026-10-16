using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloCloWidget;

// Two words built from a table of letter sounds - no recording - beside
// Windows David's real recordings of them, to see how the signals differ.
// The mouth comes from the table or from David's measured letters; the
// melody is plain or David's for that word. Signals only: no model, no FFT.
public partial class LetterSignalsWindow : Window
{
    private static readonly Brush Built1Brush = Frozen("#7FD18B");
    private static readonly Brush Built2Brush = Frozen("#7FB8E0");
    private static readonly Brush Real1Brush = Frozen("#E8A75A");
    private static readonly Brush Real2Brush = Frozen("#C98BE0");

    private readonly ObservableCollection<LetterSynth.Sound> _table = new(LetterSynth.DefaultTable());
    private Dictionary<string, double[]>? _davidMouths;
    private DavidWordMatcher.Word? _real1, _real2;
    private float[]? _built1, _built2;
    private int _version;
    private readonly DispatcherTimer _wordsChanged = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private System.Media.SoundPlayer? _player;
    private bool _closing;

    public LetterSignalsWindow()
    {
        InitializeComponent();
        TableGrid.ItemsSource = _table;
        _wordsChanged.Tick += async (_, _) =>
        {
            _wordsChanged.Stop();
            await RebuildAsync();
        };
        Word1Box.TextChanged += (_, _) => { _wordsChanged.Stop(); _wordsChanged.Start(); };
        Word2Box.TextChanged += (_, _) => { _wordsChanged.Stop(); _wordsChanged.Start(); };
        SizeChanged += (_, _) => DrawAll();
        Loaded += async (_, _) => await RebuildAsync();
        Closed += (_, _) =>
        {
            _closing = true;
            _wordsChanged.Stop();
            _player?.Stop();
            _player?.Dispose();
        };
    }

    private void Choice_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _ = RebuildAsync();
    }

    // The edited number is committed after this event; rebuild just after.
    private void TableGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(() => _ = RebuildAsync(), DispatcherPriority.Background);

    private void ResetTable_Click(object sender, RoutedEventArgs e)
    {
        _table.Clear();
        foreach (var sound in LetterSynth.DefaultTable()) _table.Add(sound);
        _ = RebuildAsync();
    }

    // Builds both words from the table and fetches David's real takes.
    private async Task RebuildAsync()
    {
        var version = ++_version;
        var word1 = Word1Box.Text.Trim();
        var word2 = Word2Box.Text.Trim();
        var useDavidMouth = DavidMouthRadio.IsChecked == true;
        var useDavidMelody = DavidMelodyRadio.IsChecked == true;
        ChoiceStatus.Text = "";
        try
        {
            // David's real words (the same takes as on the other voice pages).
            var texts = new[] { word1, word2 }.Where(t => t.Length > 0).ToList();
            var real = texts.Count == 0 ? [] : await DavidWordMatcher.PrepareAsync(texts) ?? [];
            if (_closing || version != _version) return;
            _real1 = real.FirstOrDefault(w => w.Text.Equals(word1, StringComparison.OrdinalIgnoreCase));
            _real2 = real.FirstOrDefault(w => w.Text.Equals(word2, StringComparison.OrdinalIgnoreCase));

            if (useDavidMouth && _davidMouths == null)
            {
                ChoiceStatus.Text = "Measuring David's mouth for each letter (he says “kah”, “lah”, “oh”…)…";
                _davidMouths = await MeasureDavidMouthsAsync();
                if (_closing || version != _version) return;
                ChoiceStatus.Text = _davidMouths == null ? "Windows David isn't installed." : "";
            }

            var table = _table.Select(s => s.Copy()).ToList();
            var mouths = useDavidMouth ? _davidMouths : null;
            var real1 = _real1;
            var real2 = _real2;
            var (built1, built2, sounds1, sounds2) = await Task.Run(() =>
            {
                var s1 = LetterSynth.Spell(word1, table);
                var s2 = LetterSynth.Spell(word2, table);
                var m1 = useDavidMelody && real1 != null ? MelodyOf(real1.Samples) : LetterSynth.Melody.Plain;
                var m2 = useDavidMelody && real2 != null ? MelodyOf(real2.Samples) : LetterSynth.Melody.Plain;
                return (LetterSynth.Build(s1, m1, mouths), LetterSynth.Build(s2, m2, mouths), s1, s2);
            });
            if (_closing || version != _version) return;
            _built1 = built1;
            _built2 = built2;

            Built1Title.Text = $"1 · “{word1}” built from letters";
            Built2Title.Text = $"2 · “{word2}” built from letters";
            Real1Title.Text = $"3 · David's real “{word1}”";
            Real2Title.Text = $"4 · David's real “{word2}”";
            Built1Info.Text = $"letters: {string.Join(" ", sounds1.Select(s => s.Key))} · {Describe(built1)}";
            Built2Info.Text = $"letters: {string.Join(" ", sounds2.Select(s => s.Key))} · {Describe(built2)}";
            Real1Info.Text = _real1 == null ? "" : Describe(_real1.Samples);
            Real2Info.Text = _real2 == null ? "" : Describe(_real2.Samples);
            PlayBuilt1Button.IsEnabled = built1.Length > 0;
            PlayBuilt2Button.IsEnabled = built2.Length > 0;
            PlayReal1Button.IsEnabled = _real1 != null;
            PlayReal2Button.IsEnabled = _real2 != null;
            ScoreText.Text = await Task.Run(() => Scores(word1, word2, built1, built2, real1?.Samples, real2?.Samples));
            DrawAll();
        }
        catch (Exception ex)
        {
            ChoiceStatus.Text = $"Couldn't build the words ({ex.GetType().Name}).";
        }
    }

    // How alike each built word is to the other and to each real David word:
    // mouth shapes and loudness, after lining the two up in time (DTW).
    private static string Scores(string word1, string word2, float[] built1, float[] built2, float[]? real1, float[]? real2)
    {
        static string Score(float[]? a, float[]? b) =>
            a is not { Length: > 0 } || b is not { Length: > 0 } ? "  –"
                : $"{(VoiceConverter.MouthSimilarity(VoiceConverter.MouthShapes(a), VoiceConverter.MouthShapes(b)) +
                     (SignalAlignment.Align(SignalAlignment.Measure(a), SignalAlignment.Measure(b))?.Similarity ?? 0)) / 2,4:P0}";
        var w1 = word1.Length > 10 ? word1[..10] : word1;
        var w2 = word2.Length > 10 ? word2[..10] : word2;
        return $"How alike (mouth + loudness, lined up):     vs David's {w1,-10}  vs David's {w2,-10}  vs the other built word\n" +
               $"  built “{w1}”{new string(' ', 26 - w1.Length)}{Score(built1, real1),10}{"",12}{Score(built1, real2),10}{"",14}{Score(built1, built2),8}\n" +
               $"  built “{w2}”{new string(' ', 26 - w2.Length)}{Score(built2, real1),10}{"",12}{Score(built2, real2),10}";
    }

    // David's mouth for each letter: the middle of that letter's sound when
    // he says it ("kah" for k, "oh" for o), as a filter.
    private static async Task<Dictionary<string, double[]>?> MeasureDavidMouthsAsync()
    {
        var letters = await VoiceLetterReference.DavidLetters.GetAsync();
        if (letters == null) return null;
        var mouths = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var letter in letters.Values)
            if (letter.Utterance?.Samples is { Length: > 0 } samples && letter.Segment is { } segment)
                mouths[letter.Name] = VoiceConverter.MouthFilterAt(samples,
                    (segment.StartFrame + segment.Frames / 2) * SoundPatternAnalyzer.HopSamples + 200);
        return mouths;
    }

    // David's pitch and loudness through his real word, by position 0-1.
    private static LetterSynth.Melody MelodyOf(float[] samples)
    {
        const int hop = VoiceSamples.SampleRate / 100, window = 640;
        var pitches = new List<double>();
        for (var at = 0; at + window <= samples.Length; at += hop)
        {
            var period = WaveShape.Period(samples.AsSpan(at, window));
            pitches.Add(period == 0 ? 0 : (double)VoiceSamples.SampleRate / period);
        }
        // Where he isn't voiced, keep the nearest voiced pitch.
        var voiced = pitches.Where(p => p > 0).ToList();
        var fallback = voiced.Count == 0 ? 95 : voiced.OrderBy(p => p).ElementAt(voiced.Count / 2);
        for (var i = 0; i < pitches.Count; i++)
            if (pitches[i] <= 0) pitches[i] = i > 0 ? pitches[i - 1] : fallback;
        var loudness = SignalAlignment.Outline(samples);
        double At(IReadOnlyList<double> values, double x) => values.Count == 0 ? 0 : values[Math.Clamp((int)(x * values.Count), 0, values.Count - 1)];
        var loud = loudness.Select(v => (double)v).ToList();
        return new LetterSynth.Melody(x => pitches.Count == 0 ? fallback : At(pitches, x), x => Math.Max(0.15, At(loud, x)));
    }

    // ------------------------------------------------------------ drawing and playing

    private void DrawAll()
    {
        if (Built1Canvas == null) return;
        var longest = new[] { _built1, _built2, _real1?.Samples, _real2?.Samples }.Max(s => s?.Length ?? 0);
        VoiceSignalsWindow.DrawSignal(Built1Canvas, _built1, Built1Brush, longest);
        VoiceSignalsWindow.DrawSignal(Built2Canvas, _built2, Built2Brush, longest);
        VoiceSignalsWindow.DrawSignal(Real1Canvas, _real1?.Samples, Real1Brush, longest);
        VoiceSignalsWindow.DrawSignal(Real2Canvas, _real2?.Samples, Real2Brush, longest);
    }

    private static string Describe(float[] samples) => $"{samples.Length * 1000 / VoiceSamples.SampleRate} ms";

    private void PlayBuilt1_Click(object sender, RoutedEventArgs e) { if (_built1 is { Length: > 0 }) Play(VoiceSamples.ToWav(_built1)); }
    private void PlayBuilt2_Click(object sender, RoutedEventArgs e) { if (_built2 is { Length: > 0 }) Play(VoiceSamples.ToWav(_built2)); }
    private void PlayReal1_Click(object sender, RoutedEventArgs e) { if (_real1 != null) Play(_real1.Speech.Wav); }
    private void PlayReal2_Click(object sender, RoutedEventArgs e) { if (_real2 != null) Play(_real2.Speech.Wav); }

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
            ChoiceStatus.Text = "Couldn't play the sound.";
        }
    }

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}

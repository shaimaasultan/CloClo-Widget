using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;

namespace CloCloWidget;

// Experiment: writes the letters heard in each word spoken into the
// microphone, measured with plain signal math, and learns letters taught to
// it said alone. Audio stays in memory and is dropped as soon as it is
// analysed. Only the numbers describing a sound are saved, and only when a
// letter is taught; nothing is run.
public partial class SoundPatternTestWindow : Window
{
    private const int LiveFrames = 300; // 3 s of 10 ms frames
    // A taught letter is used when its fingerprint is at least this close;
    // otherwise the rule guess stands.
    private const double TaughtMatchMinimum = 0.6;
    // Hoda's references are one voice, so ask for a closer match than for
    // letters the person taught.
    private const double HodaMatchMinimum = 0.75;

    private static readonly Brush SilenceBrush = Frozen("#3A4A58");
    private static readonly Brush ConsonantBrush = Frozen("#4A7FB5");
    private static readonly Brush VowelBrush = Frozen("#3FA37A");
    private static readonly Brush MineBrush = Frozen("#7FB8E0");
    private static readonly Brush HodaBrush = Frozen("#E8A75A");
    private static readonly Brush DavidBrush = Frozen("#E8A75A");
    private static readonly Brush SineBrush = Frozen("#8A99A6");

    private readonly SoundPatternAnalyzer _analyzer = new();
    private readonly ConcurrentQueue<SoundFrame> _pendingFrames = new();
    private readonly SoundFrame[] _live = new SoundFrame[LiveFrames];
    private readonly Rectangle[] _liveBars = new Rectangle[LiveFrames];
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ObservableCollection<HistoryRow> _history = new();
    private readonly ObservableCollection<PieceRow> _pieces = new();
    private readonly SoundPieceLibrary _library = SoundPieceLibrary.Load();
    private IReadOnlyList<SoundPieceLibrary.LetterSummary> _summaries = [];
    private WasapiRecorder? _recorder;
    private SoundUtterance? _last;
    private IReadOnlyList<string>? _reference;
    private string? _referenceLabel;
    private volatile bool _closing;
    private IReadOnlyDictionary<string, VoiceLetterReference.Letter>? _hoda;
    private VoiceLetterReference.Letter? _comparedHoda;
    private IReadOnlyDictionary<string, VoiceLetterReference.Letter>? _david;
    private VoiceLetterReference.Letter? _comparedDavid;
    private static readonly float[] SineCycle = WaveShape.Sine();
    private System.Media.SoundPlayer? _player;

    public SoundPatternTestWindow()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _history;
        PiecesList.ItemsSource = _pieces;
        RefreshLibrary();
        for (var i = 0; i < LiveFrames; i++)
        {
            _live[i] = new SoundFrame(SoundType.Silence, ' ', -90, 0, 0, 0);
            _liveBars[i] = new Rectangle();
            LiveCanvas.Children.Add(_liveBars[i]);
        }

        _analyzer.FrameAnalyzed += frame => _pendingFrames.Enqueue(frame);
        _analyzer.UtteranceFinished += utterance =>
        {
            if (!_closing) Dispatcher.BeginInvoke(() => ShowUtterance(utterance));
        };
        _redraw.Tick += (_, _) => DrawLive();
        SensitivitySlider.ValueChanged += (_, _) => ApplySensitivity();
        ApplySensitivity();
        UpdateTeachStatus();
        Loaded += async (_, _) =>
        {
            await StartMicrophoneAsync();
            await LoadHodaAsync();
            await LoadDavidAsync();
        };
        Closed += async (_, _) =>
        {
            _player?.Stop();
            _player?.Dispose();
            await StopMicrophoneAsync();
        };
    }

    private async Task StartMicrophoneAsync()
    {
        try
        {
            // Windows converts the default microphone to 16 kHz mono here.
            var recorder = new WasapiRecorderBuilder().WithFormat(new WaveFormat(SoundPatternAnalyzer.SampleRate, 16, 1)).Build();
            var samples = new float[4096];
            recorder.DataAvailable += (buffer, _, _, _) =>
            {
                if (_closing) return;
                var count = Math.Min(buffer.Length / 2, samples.Length);
                for (var i = 0; i < count; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(i * 2, 2)) / 32768f;
                _analyzer.Push(samples.AsSpan(0, count));
            };
            recorder.RecordingStopped += (_, _) =>
            {
                if (!_closing) Dispatcher.BeginInvoke(() => StatusText.Text = "The microphone stopped. Close and reopen this window to try again.");
            };
            _recorder = recorder;
            recorder.StartRecording();
            if (_closing) { await StopMicrophoneAsync(); return; }
            StatusText.Text = $"Listening on {recorder.DeviceFriendlyName}. No audio is recorded or saved.";
            _redraw.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open the microphone ({ex.GetType().Name}). Check the default input device and microphone permission.";
        }
    }

    private async Task LoadHodaAsync()
    {
        CompareScore.Text = "Preparing Windows Hoda's letters…";
        try { _hoda = await VoiceLetterReference.Hoda.GetAsync(); }
        catch { _hoda = null; }
        if (_closing) return;
        CompareScore.Text = _hoda == null
            ? "The Windows Hoda voice isn't installed (Settings → Time & language → Speech)."
            : "Say a letter to compare it with Hoda's.";
        RefreshLibrary();
    }

    private async Task LoadDavidAsync()
    {
        WaveScore.Text = "Preparing Windows David's vowels…";
        try { _david = await VoiceLetterReference.David.GetAsync(); }
        catch { _david = null; }
        if (_closing) return;
        WaveScore.Text = _david == null
            ? "The Windows David voice isn't installed (Settings → Time & language → Speech)."
            : "Say a vowel — a, e, i, o or u — to compare its wave with David's and a sine wave.";
    }

    private async Task StopMicrophoneAsync()
    {
        _closing = true;
        _redraw.Stop();
        var recorder = _recorder;
        _recorder = null;
        if (recorder != null)
        {
            try { await recorder.DisposeAsync(); }
            catch { /* already stopped */ }
        }
    }

    // The analyser reads these on the capture thread; single writes are atomic.
    private void ApplySensitivity()
    {
        _analyzer.StartDb = (float)SensitivitySlider.Value;
        UpdateSensitivityText();
    }

    private void UpdateSensitivityText() =>
        SensitivityText.Text = $"words start {_analyzer.StartDb:0} dB above background ({_analyzer.NoiseFloorDb:0} dB)";

    private void DrawLive()
    {
        var added = 0;
        while (_pendingFrames.TryDequeue(out var frame))
        {
            Array.Copy(_live, 1, _live, 0, LiveFrames - 1);
            _live[^1] = frame;
            added++;
        }
        if (added == 0) return;
        UpdateSensitivityText();

        var width = LiveCanvas.ActualWidth;
        var height = LiveCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var barWidth = width / LiveFrames;
        var floor = _analyzer.NoiseFloorDb;
        for (var i = 0; i < LiveFrames; i++)
        {
            var frame = _live[i];
            // Height: 0 dB above the floor = empty, 45 dB = full.
            var level = Math.Clamp((frame.LevelDb - floor) / 45.0, 0.04, 1);
            var bar = _liveBars[i];
            bar.Width = Math.Max(1, barWidth);
            bar.Height = level * height;
            bar.Fill = BrushFor(frame.Type);
            Canvas.SetLeft(bar, i * barWidth);
            Canvas.SetTop(bar, height - bar.Height);
        }
    }

    // ------------------------------------------------------------ reading letters

    private enum LetterSource { Rules, Hoda, Taught }

    private sealed record ReadLetter(SoundSegment Segment, SoundPiece Measured, string Letter, LetterSource Source);

    // One letter per sound: a taught letter when one is close enough, then
    // Hoda's, otherwise the rule guess. Vowels only match vowels and
    // consonants only consonants.
    private List<ReadLetter> ReadLetters(SoundUtterance utterance)
    {
        var letters = new List<ReadLetter>();
        for (var i = 0; i < utterance.Segments.Count; i++)
        {
            var segment = utterance.Segments[i];
            var measured = SoundPiece.FromSegment(segment, utterance, i + 1, "", "", "");
            if (segment.Type == SoundType.Silence)
            {
                letters.Add(new ReadLetter(segment, measured, "_", LetterSource.Rules));
                continue;
            }
            var taught = BestTaught(measured, sameKind: true);
            var hoda = BestHoda(measured);
            letters.Add(taught.Letter != null && taught.Score >= TaughtMatchMinimum
                ? new ReadLetter(segment, measured, taught.Letter, LetterSource.Taught)
                : hoda.Letter != null && hoda.Score >= HodaMatchMinimum
                    ? new ReadLetter(segment, measured, hoda.Letter, LetterSource.Hoda)
                    : new ReadLetter(segment, measured, segment.Letter.Length > 0 ? segment.Letter : "?", LetterSource.Rules));
        }
        return letters;
    }

    private (string? Letter, double Score) BestTaught(SoundPiece piece, bool sameKind)
    {
        var pieceIsVowel = piece.Type == nameof(SoundType.Vowel);
        return _summaries
            .Where(s => !sameKind || IsVowelLetter(s.Letter) == pieceIsVowel)
            .Select(s => ((string?)s.Letter, SoundPiece.BandSimilarity(s.Bands, piece.Bands)))
            .OrderByDescending(x => x.Item2)
            .FirstOrDefault();
    }

    private (string? Letter, double Score) BestHoda(SoundPiece piece)
    {
        if (_hoda == null) return (null, 0);
        var pieceIsVowel = piece.Type == nameof(SoundType.Vowel);
        return _hoda.Values
            .Where(h => h.Piece != null && VoiceLetterReference.IsVowel(h.Name) == pieceIsVowel)
            .Select(h => ((string?)h.Name, SoundPiece.BandSimilarity(h.Piece!.Bands, piece.Bands)))
            .OrderByDescending(x => x.Item2)
            .FirstOrDefault();
    }

    private VoiceLetterReference.Letter? HodaFor(string letter) =>
        _hoda != null && _hoda.TryGetValue(VoiceLetterReference.Canonical(letter), out var hoda) && hoda.Piece != null ? hoda : null;

    private string VersusHoda(string letter, float[] bands) =>
        HodaFor(letter) is { } hoda ? $"{SoundPiece.BandSimilarity(hoda.Piece!.Bands, bands):P0}" : "";

    private static bool IsVowelLetter(string letter) => VoiceLetterReference.IsVowel(letter);

    private void ShowUtterance(SoundUtterance utterance)
    {
        _last = utterance;
        var letters = ReadLetters(utterance);
        var spoken = letters.Where(l => l.Segment.Type != SoundType.Silence).ToList();
        PatternText.Text = string.Join(" ", letters.Select(l => l.Letter));
        DetailText.Text = $"{utterance.Syllables} syllable(s), {utterance.Frames.Count * 10} ms. " +
                          $"Letters: {spoken.Count(l => l.Source == LetterSource.Taught)} taught, " +
                          $"{spoken.Count(l => l.Source == LetterSource.Hoda)} from Hoda, " +
                          $"{spoken.Count(l => l.Source == LetterSource.Rules)} by rules.";
        DrawWord(utterance, letters);
        ShowPieces(letters);

        var word = WordBox.Text.Trim();
        if (TeachCheck.IsChecked == true)
        {
            var (label, taughtSegment) = TeachLetter(utterance, letters);
            word = label;
            var taught = letters.FirstOrDefault(l => l.Segment == taughtSegment);
            if (taught != null)
            {
                ShowComparison(TeachBox.Text.Trim(), taught.Measured);
                ShowWave(TeachBox.Text.Trim(), utterance, taught.Segment);
            }
        }
        else
        {
            // A letter said alone, or the main sound of a word: its longest sound.
            var main = spoken.MaxBy(l => l.Segment.Frames);
            if (main != null)
            {
                ShowComparison(main.Letter, main.Measured);
                // The wave is a vowel's: the longest vowel, if there is one.
                var vowel = spoken.Where(l => l.Segment.Type == SoundType.Vowel).MaxBy(l => l.Segment.Frames) ?? main;
                ShowWave(vowel.Letter, utterance, vowel.Segment);
            }
        }

        var tokens = letters.Select(l => l.Letter).ToList();
        _history.Insert(0, new HistoryRow(DateTime.Now.ToString("HH:mm:ss"), SpeakerBox.Text.Trim(), word,
            tokens, utterance.Syllables, MatchText(tokens)));
        if (_history.Count > 200) _history.RemoveAt(_history.Count - 1);
    }

    private void DrawWord(SoundUtterance utterance, IReadOnlyList<ReadLetter> letters)
    {
        WordCanvas.Children.Clear();
        var width = WordCanvas.ActualWidth;
        var height = WordCanvas.ActualHeight;
        if (width <= 0 || letters.Count == 0) return;
        var first = utterance.Segments[0].StartFrame;
        var last = utterance.Segments[^1].StartFrame + utterance.Segments[^1].Frames;
        var scale = width / Math.Max(1, last - first);
        foreach (var letter in letters)
        {
            var block = new Border
            {
                Width = Math.Max(16, letter.Segment.Frames * scale - 1),
                Height = height,
                Background = BrushFor(letter.Segment.Type),
                Child = new TextBlock
                {
                    Text = letter.Letter,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextDecorations = letter.Source == LetterSource.Taught ? TextDecorations.Underline : null,
                    FontStyle = letter.Source == LetterSource.Hoda ? FontStyles.Italic : FontStyles.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(block, (letter.Segment.StartFrame - first) * scale);
            WordCanvas.Children.Add(block);
        }
    }

    // ------------------------------------------------------------ teaching

    private void TeachCheck_Changed(object sender, RoutedEventArgs e)
    {
        _analyzer.AcceptVoiceless = TeachCheck.IsChecked == true;
        UpdateTeachStatus();
    }

    private void TeachBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTeachStatus();

    private void UpdateTeachStatus()
    {
        var letter = TeachBox.Text.Trim();
        var saved = letter.Length == 0 ? 0 : _library.Pieces.Count(p => string.Equals(p.Letter, letter, StringComparison.OrdinalIgnoreCase));
        TeachStatus.Text = TeachCheck.IsChecked != true ? "Off — words are only read, nothing is saved."
            : letter.Length == 0 ? "Type the letter to teach."
            : $"Say “{letter}” alone, then pause. {saved} saved so far.";
    }

    // Saves the one sound that stands for the letter: the longest vowel for a
    // vowel, otherwise the first consonant ("k" said alone is often "k-uh";
    // the "uh" is not the letter). Hoda's references are cut the same way.
    private (string Label, SoundSegment? Segment) TeachLetter(SoundUtterance utterance, IReadOnlyList<ReadLetter> letters)
    {
        var letter = TeachBox.Text.Trim();
        if (letter.Length == 0)
        {
            TeachStatus.Text = "Type the letter to teach first.";
            return ("", null);
        }
        var segment = VoiceLetterReference.PickLetterSegment(utterance, IsVowelLetter(letter));
        var pick = letters.FirstOrDefault(l => l.Segment == segment);
        if (pick == null) return ("", null);

        _library.Pieces.Add(SoundPiece.FromSegment(pick.Segment, utterance, pick.Measured.Position, letter,
            $"({letter} alone)", SpeakerBox.Text));
        if (!_library.Save())
        {
            _library.Pieces.RemoveAt(_library.Pieces.Count - 1);
            TeachStatus.Text = "Couldn't save the letter.";
            return ("", null);
        }
        RefreshLibrary();
        var saved = _library.Pieces.Count(p => string.Equals(p.Letter, letter, StringComparison.OrdinalIgnoreCase));
        TeachStatus.Text = $"Saved “{letter}” ({saved} so far). Say it again, or change the letter.";
        return ($"({letter} alone)", pick.Segment);
    }

    // ------------------------------------------------------------ compare with Hoda

    // Your sound and Hoda's for the same letter, band by band, side by side.
    private void ShowComparison(string letter, SoundPiece mine)
    {
        letter = letter.Trim();
        CompareCanvas.Children.Clear();
        var hoda = letter is "" or "?" ? null : HodaFor(letter);
        _comparedHoda = hoda;
        HearHodaButton.IsEnabled = hoda != null;
        CompareTitle.Text = letter is "" or "?" ? "?" : $"“{letter}”";
        MineLegend.Text = $"You  {Describe(mine)}";
        if (hoda == null)
        {
            HodaLegend.Text = "Windows Hoda";
            CompareScore.Text = _hoda == null ? CompareScore.Text
                : letter is "" or "?" ? "Not sure which letter this is."
                : $"Hoda has no “{letter}” (Arabic has no p, v, x…).";
            DrawCompareBars(mine.Bands, null);
            return;
        }
        HodaLegend.Text = $"Hoda “{hoda.Spoken}”  {Describe(hoda.Piece!)}";
        var similarity = SoundPiece.BandSimilarity(mine.Bands, hoda.Piece!.Bands);
        CompareScore.Text = $"{similarity:P0} like Hoda's";
        DrawCompareBars(mine.Bands, hoda.Piece.Bands);
    }

    private static string Describe(SoundPiece piece) =>
        piece.F1 > 0 ? $"{piece.DurationMs} ms, F1 {piece.F1:0} / F2 {piece.F2:0}" : $"{piece.DurationMs} ms";

    private void DrawCompareBars(float[] mine, float[]? hoda)
    {
        var width = CompareCanvas.ActualWidth;
        var height = CompareCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var group = width / mine.Length;
        var bar = Math.Max(2, group * 0.38);
        for (var b = 0; b < mine.Length; b++)
        {
            AddBar(b * group + group * 0.1, mine[b], MineBrush);
            if (hoda != null && b < hoda.Length) AddBar(b * group + group * 0.1 + bar + 2, hoda[b], HodaBrush);
        }

        void AddBar(double left, float value, Brush brush)
        {
            var h = Math.Clamp((value + 20) / 40.0, 0.05, 1) * height;
            var rect = new Rectangle { Width = bar, Height = h, Fill = brush };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, height - h);
            CompareCanvas.Children.Add(rect);
        }
    }

    private void HearHoda_Click(object sender, RoutedEventArgs e) => Play(_comparedHoda);

    // ------------------------------------------------------------ wave (signal only)

    private void CompareMode_Changed(object sender, RoutedEventArgs e)
    {
        if (WavePanel == null || BandsPanel == null) return;
        var wave = WaveModeRadio.IsChecked == true;
        WavePanel.Visibility = wave ? Visibility.Visible : Visibility.Collapsed;
        BandsPanel.Visibility = wave ? Visibility.Collapsed : Visibility.Visible;
    }

    // Your vowel, David's and a plain sine wave, compared on the samples alone.
    private void ShowWave(string letter, SoundUtterance utterance, SoundSegment segment)
    {
        letter = letter.Trim();
        CycleCanvas.Children.Clear();
        RingCanvas.Children.Clear();
        SignalCanvas.Children.Clear();
        WaveTitle.Text = letter is "" or "?" ? "?" : $"“{letter}”";

        var mine = WaveShape.Of(utterance, segment);
        var david = letter is "" or "?" || _david == null ? null
            : _david.TryGetValue(VoiceLetterReference.Canonical(letter), out var d) ? d : null;
        _comparedDavid = david;
        HearDavidButton.IsEnabled = david != null;
        var davidCycle = david?.Utterance != null && david.Segment != null ? WaveShape.Of(david.Utterance, david.Segment) : null;

        if (mine == null)
        {
            WaveScore.Text = "No repeating wave in this sound. Only vowels repeat — s is noise and k is a pop, so they have no single wave shape.";
            if (davidCycle != null) DrawCycles(null, davidCycle);
            return;
        }

        var lines = new List<string> { $"Your pitch: {mine.PitchHz:0} Hz", $"You vs sine: {WaveShape.Compare(mine.Shape, SineCycle).Similarity:P0}" };
        if (davidCycle != null)
        {
            lines.Add($"David's pitch: {davidCycle.PitchHz:0} Hz (“{david!.Spoken}”)");
            lines.Add($"David vs sine: {WaveShape.Compare(davidCycle.Shape, SineCycle).Similarity:P0}");
            lines.Add($"You vs David, whole cycle: {WaveShape.Compare(mine.Shape, davidCycle.Shape).Similarity:P0}");
            lines.Add($"You vs David, first 4 ms: {WaveShape.CompareRings(mine.Ring, davidCycle.Ring).Similarity:P0}");
        }
        else if (_david != null)
            lines.Add(letter is "" or "?" ? "Not sure which vowel this is." : $"David has no “{letter}” — only the vowels a e i o u.");
        WaveScore.Text = string.Join("\n", lines);
        DrawCycles(mine, davidCycle);
    }

    private void DrawCycles(WaveShape.Cycle? mine, WaveShape.Cycle? david)
    {
        // Line up David and the sine with your cycle before drawing.
        var reference = mine?.Shape ?? david?.Shape;
        if (reference == null) return;
        DrawLine(CycleCanvas, WaveShape.Shifted(SineCycle, WaveShape.Compare(reference, SineCycle).Shift), SineBrush, 0, 1);
        if (david != null)
            DrawLine(CycleCanvas, mine == null ? david.Shape : WaveShape.Shifted(david.Shape, WaveShape.Compare(mine.Shape, david.Shape).Shift), DavidBrush, 0, 1);
        if (mine != null) DrawLine(CycleCanvas, mine.Shape, MineBrush, 0, 1);

        if (david != null)
        {
            var ring = david.Ring;
            if (mine != null)
            {
                var shift = WaveShape.CompareRings(mine.Ring, david.Ring).Shift;
                ring = Enumerable.Range(0, ring.Length).Select(i => i + shift >= 0 && i + shift < ring.Length ? david.Ring[i + shift] : 0f).ToArray();
            }
            DrawLine(RingCanvas, ring, DavidBrush, 0, 1);
        }
        if (mine != null) DrawLine(RingCanvas, mine.Ring, MineBrush, 0, 1);

        // The signals in two lanes: you on top, David below.
        if (mine != null) DrawLine(SignalCanvas, mine.Signal, MineBrush, 0, 0.5);
        if (david != null) DrawLine(SignalCanvas, david.Signal, DavidBrush, 0.5, 0.5);
    }

    // A wave as a line across the canvas, in the band from top to top+height
    // (fractions of the canvas height). Values are normalized, so about ±2.5.
    private static void DrawLine(Canvas canvas, float[] values, Brush brush, double top, double height)
    {
        var width = canvas.ActualWidth;
        var laneHeight = canvas.ActualHeight * height;
        if (width <= 0 || laneHeight <= 0 || values.Length < 2) return;
        var middle = canvas.ActualHeight * top + laneHeight / 2;
        var points = new PointCollection(values.Length);
        for (var i = 0; i < values.Length; i++)
            points.Add(new Point(i * width / (values.Length - 1), middle - Math.Clamp(values[i] / 2.5, -1, 1) * laneHeight / 2 * 0.9));
        canvas.Children.Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = 2 });
    }

    private void HearDavid_Click(object sender, RoutedEventArgs e) => Play(_comparedDavid);

    private void Play(VoiceLetterReference.Letter? letter)
    {
        if (letter == null) return;
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = new System.Media.SoundPlayer(new MemoryStream(letter.Wav));
            _player.Play();
        }
        catch
        {
            WaveScore.Text = "Couldn't play the voice's sound.";
        }
    }

    // ------------------------------------------------------------ history

    private string MatchText(IReadOnlyList<string> letters) =>
        _reference == null ? "" : $"{SoundUtterance.Similarity(letters, _reference):P0}";

    private void UseAsReference_Click(object sender, RoutedEventArgs e)
    {
        var row = HistoryList.SelectedItem as HistoryRow ?? _history.FirstOrDefault();
        if (row == null) return;
        _reference = row.Letters;
        _referenceLabel = $"{row.Speaker} {row.Word}".Trim();
        ReferenceText.Text = $"Reference: {row.Pattern}" +
                             (string.IsNullOrEmpty(_referenceLabel) ? "" : $"  ({_referenceLabel})") +
                             " — Match shows how close each word's letters are";
        for (var i = 0; i < _history.Count; i++)
            _history[i] = _history[i] with { Match = MatchText(_history[i].Letters) };
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        var text = new StringBuilder();
        if (_reference != null) text.AppendLine($"Reference\t{_referenceLabel}\t{string.Join(" ", _reference)}");
        foreach (var row in _history.Reverse())
            text.AppendLine($"{row.Time}\t{row.Speaker}\t{row.Word}\t{row.Pattern}\t{row.Syllables}\t{row.Match}");
        SafeClipboard.TrySetText(text.ToString());
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _reference = null;
        _referenceLabel = null;
        ReferenceText.Text = "Pick a row and use it as reference to see how close each word's letters are";
    }

    // ------------------------------------------------------------ letters of last word

    private void ShowPieces(IReadOnlyList<ReadLetter> letters)
    {
        _pieces.Clear();
        SaveStatus.Text = "";
        foreach (var letter in letters.Where(l => l.Segment.Type != SoundType.Silence))
        {
            var closest = BestTaught(letter.Measured, sameKind: false);
            _pieces.Add(new PieceRow(letter.Segment, letter.Measured, letter.Letter == "?" ? "" : letter.Letter,
                closest.Letter == null ? "" : $"{closest.Letter} ({closest.Score:P0})",
                VersusHoda(letter.Letter, letter.Measured.Bands)));
        }
    }

    private void SaveSounds_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null || _pieces.Count == 0)
        {
            SaveStatus.Text = "  Say a word first.";
            return;
        }
        var toSave = _pieces.Where(p => !string.IsNullOrWhiteSpace(p.Letter)).ToList();
        if (toSave.Count == 0)
        {
            SaveStatus.Text = "  Type a letter for at least one sound.";
            return;
        }
        foreach (var row in toSave)
            _library.Pieces.Add(SoundPiece.FromSegment(row.Segment, _last, row.Position, row.Letter,
                WordBox.Text, SpeakerBox.Text));
        if (!_library.Save())
        {
            _library.Pieces.RemoveRange(_library.Pieces.Count - toSave.Count, toSave.Count);
            SaveStatus.Text = "  Couldn't save the letters.";
            return;
        }
        _pieces.Clear();
        SaveStatus.Text = $"  Saved {toSave.Count} letter(s): {string.Join(" ", toSave.Select(p => p.Letter.Trim()))}";
        RefreshLibrary();
    }

    // ------------------------------------------------------------ taught letters

    private void RefreshLibrary()
    {
        _summaries = _library.Summaries();
        LibraryList.ItemsSource = _summaries.Select(s => new LibraryRow(s, VersusHoda(s.Letter, s.Bands))).ToList();
        LibraryText.Text = _library.Pieces.Count == 0
            ? "No letters taught yet. Use “Teach one letter” above, or correct and save the letters of a word."
            : $"{_library.Pieces.Count} sound(s) taught for {_summaries.Count} letter(s), averaged per letter. " +
              "Similar fingerprints from different speakers mean the letter can be recognised without a model.";
        UpdateTeachStatus();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SoundPieceLibrary.FolderPath);
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(SoundPieceLibrary.FolderPath);
            using var explorerProcess = System.Diagnostics.Process.Start(start);
        }
        catch
        {
            LibraryText.Text = "Couldn't open the folder.";
        }
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (_library.Pieces.Count == 0) return;
        if (MessageBox.Show(this, $"Delete all {_library.Pieces.Count} taught sounds? This can't be undone.",
                "CloClo — Taught letters", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var kept = _library.Pieces.ToList();
        _library.Pieces.Clear();
        if (!_library.Save())
        {
            _library.Pieces.AddRange(kept);
            LibraryText.Text = "Couldn't delete the taught letters.";
            return;
        }
        RefreshLibrary();
    }

    // ------------------------------------------------------------ helpers

    // Band values are about -20..+20 dB around the sound's own average.
    private static IReadOnlyList<double> BarsFor(float[] bands) =>
        bands.Select(b => Math.Clamp((b + 20) / 40.0, 0.05, 1) * 24).ToList();

    private static Brush BrushFor(SoundType type) => type switch
    {
        SoundType.Vowel => VowelBrush,
        SoundType.Silence => SilenceBrush,
        _ => ConsonantBrush,
    };

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private sealed record HistoryRow(string Time, string Speaker, string Word, IReadOnlyList<string> Letters, int Syllables, string Match)
    {
        public string Pattern => string.Join(" ", Letters);
    }

    private sealed class PieceRow(SoundSegment segment, SoundPiece measured, string letter, string closest, string versusHoda)
    {
        public SoundSegment Segment { get; } = segment;
        public int Position { get; } = measured.Position;
        public Brush Brush { get; } = BrushFor(segment.Type);
        public string Letter { get; set; } = letter;
        public int DurationMs { get; } = measured.DurationMs;
        public string Quieter { get; } = $"{measured.LevelBelowPeakDb:0} dB";
        public string Formants { get; } = measured.F1 > 0 ? $"{measured.F1:0} / {measured.F2:0}" : "";
        public IReadOnlyList<double> Bars { get; } = BarsFor(measured.Bands);
        public string Closest { get; } = closest;
        public string VersusHoda { get; } = versusHoda;
    }

    private sealed class LibraryRow(SoundPieceLibrary.LetterSummary summary, string versusHoda)
    {
        public string Letter { get; } = summary.Letter;
        public int Count { get; } = summary.Count;
        public int Speakers { get; } = summary.Speakers;
        public int DurationMs { get; } = summary.DurationMs;
        public IReadOnlyList<double> Bars { get; } = BarsFor(summary.Bands);
        public string VersusHoda { get; } = versusHoda;
    }
}

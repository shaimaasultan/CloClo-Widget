using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloCloWidget;

// Record a word and find which of Windows David's words it is closest to -
// optionally after saying the recording again in David's style - then hear
// David say it. Signals only: no model, no FFT. Everything stays in memory;
// nothing is saved.
public partial class DavidMatchWindow : Window
{
    private const int MaximumRecordingSeconds = 4;
    private const int MaximumWords = 60;

    private static readonly Brush MeBrush = Frozen("#7FB8E0");
    private static readonly Brush ComparedBrush = Frozen("#7FD18B");
    private static readonly Brush DavidBrush = Frozen("#E8A75A");

    private List<DavidWordMatcher.Word>? _words;
    private MicrophoneRecording? _recorder;
    private float[]? _me;
    private string? _meTaughtAs; // the word this recording was already added to
    private DavidWordMatcher.Match? _selected;
    private int _rankVersion;
    private readonly DispatcherTimer _liveDraw = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private System.Media.SoundPlayer? _player;
    private bool _closing;

    public DavidMatchWindow()
    {
        InitializeComponent();
        _liveDraw.Tick += (_, _) => DrawLiveRecording();
        SizeChanged += (_, _) => DrawLanes();
        Loaded += async (_, _) => await PrepareAsync();
        Closed += async (_, _) =>
        {
            _closing = true;
            _liveDraw.Stop();
            _player?.Stop();
            _player?.Dispose();
            if (_recorder != null) await _recorder.DisposeAsync();
        };
    }

    // ------------------------------------------------------------ David's words

    private async void Prepare_Click(object sender, RoutedEventArgs e) => await PrepareAsync();

    private async Task PrepareAsync()
    {
        var texts = WordsBox.Text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumWords).ToList();
        if (texts.Count == 0) { WordsStatus.Text = "Type some words first."; return; }
        PrepareButton.IsEnabled = false;
        WordsStatus.Text = $"David is saying {texts.Count} words…";
        try
        {
            var more = MoreExamplesCheck.IsChecked == true;
            var words = await DavidWordMatcher.PrepareAsync(texts, more,
                (done, total) => WordsStatus.Text = more ? $"Preparing examples… {done}/{total} words" : $"David is saying the words… {done}/{total}");
            if (_closing) return;
            if (words == null) { WordsStatus.Text = "Windows David isn't installed (Settings → Time & language → Speech)."; return; }
            _words = words;
            _davidVoice = null;
            var skipped = texts.Count - words.Count;
            WordsStatus.Text = $"{words.Count} words ready" + (skipped > 0 ? $" ({skipped} David said nothing for)" : "");
            await RankAsync(play: false);
        }
        catch (Exception ex)
        {
            WordsStatus.Text = $"Couldn't prepare David's words ({ex.GetType().Name}).";
        }
        finally
        {
            PrepareButton.IsEnabled = true;
        }
    }

    private async void MoreExamples_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // Turning examples on needs them made; off just compares again.
        if (MoreExamplesCheck.IsChecked == true) await PrepareAsync();
        else await RankAsync(play: false);
    }

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
                    RecordStatus.Text = $"Speak now: say one of David's words, then press Stop (stops by itself after {MaximumRecordingSeconds} s).";
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
        _meTaughtAs = null;
        HearMeButton.IsEnabled = true;
        RecordStatus.Text = "";
        await RankAsync(play: true);
    }

    private void DrawLiveRecording()
    {
        if (_recorder == null) return;
        var recorded = _recorder.Snapshot();
        VoiceSignalsWindow.DrawSignal(MeCanvas, recorded, MeBrush, MaximumRecordingSeconds * VoiceSamples.SampleRate);
        if (recorded.Length >= MaximumRecordingSeconds * VoiceSamples.SampleRate) _ = StopRecordingAsync();
    }

    // ------------------------------------------------------------ matching

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = RankAsync(play: false);
    }

    private void Pitch_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PitchText == null) return; // still loading
        PitchText.Text = $"{PitchSlider.Value:0} Hz";
        Option_Changed(sender, e);
    }

    private void MouthBox_Changed(object sender, SelectionChangedEventArgs e) => Option_Changed(sender, e);

    // The boxes as converter options. Mouth size "fit to David's voice" is
    // worked out later, on the worker thread (FitVoice).
    private (VoiceConverter.Options? Options, bool FitVoice) CurrentOptions()
    {
        if (ConvertCheck.IsChecked != true) return (null, false);
        var mouth = (MouthBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1";
        var options = new VoiceConverter.Options(
            PitchHz: (float)PitchSlider.Value,
            FollowIntonation: IntonationCheck.IsChecked == true,
            MouthScale: mouth == "voice" ? 1f : float.Parse(mouth, CultureInfo.InvariantCulture),
            MatchColor: ColourCheck.IsChecked == true,
            // His colour from all his words together: his voice in general.
            ColourFrom: ColourCheck.IsChecked == true ? DavidVoice() : null,
            // Only what David's voice has in general: pitch, mouth size,
            // colour. His melody, loudness and mouth belong to one word and
            // would replace the person's word with his.
            MatchMelody: false);
        return (options, mouth == "voice");
    }

    // All of David's prepared words one after another: his voice in general.
    private float[]? _davidVoice;
    private float[] DavidVoice() => _davidVoice ??= (_words ?? []).SelectMany(w => w.Samples).ToArray();

    // Compares the recording with all of David's words on a worker thread;
    // a newer request (an option changed meanwhile) wins.
    private async Task RankAsync(bool play)
    {
        if (_me == null || _words == null || _words.Count == 0 || _recorder != null) return;
        var version = ++_rankVersion;
        var me = _me;
        var words = _words;
        var (options, fitVoice) = CurrentOptions();
        var voice = fitVoice ? DavidVoice() : null;
        BestScoreText.Text = "Comparing…";
        var more = MoreExamplesCheck.IsChecked == true;
        var matches = await Task.Run(() =>
        {
            if (options != null && voice != null)
                options = options with { MouthScale = VoiceConverter.VoiceMouthScale(me, voice) };
            // Every box ticked, towards each of David's words in turn;
            // compared with that word at a fixed length.
            return DavidWordMatcher.Rank(me, words, options, more);
        });
        if (_closing || version != _rankVersion) return;

        RankingList.ItemsSource = matches.Select((m, i) => new RankRow(i + 1, m)).ToList();
        var best = matches[0];
        BestWordText.Text = best.Word.Text;
        var runnerUp = matches.Count > 1 ? matches[1] : null;
        BestScoreText.Text = $"score {best.Score:P0}" +
                             (runnerUp == null ? "" : $" · next: {runnerUp.Word.Text} {runnerUp.Score:P0}") +
                             (runnerUp != null && best.Score - runnerUp.Score < 0.02 ? " — too close to call" : "");
        HearDavidButton.IsEnabled = true;
        // A word picked in the list stays picked when a setting changes; a
        // new recording starts from the best match.
        var keep = play ? -1 : matches.FindIndex(m => m.Word.Text == _selected?.Word.Text);
        _selected = null;
        RankingList.SelectedIndex = Math.Max(0, keep);
        if (play) Play(best.Word.Speech.Wav);
    }

    private void Ranking_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RankingList.SelectedItem is not RankRow row) return;
        var changed = _selected?.Word != row.Match.Word;
        _selected = row.Match;
        ThatsItButton.IsEnabled = _me != null && _meTaughtAs != row.Match.Word.Text;
        ThatsItButton.Content = $"✓ I said “{row.Match.Word.Text}”";
        var own = row.Match.Word.Examples.Count(x => x.Own);
        ExampleText.Text = $"closest example: {row.Match.Example.Source}" + (own > 0 ? $" · {own} of yours for this word" : "");
        HearDavidButton.Content = row.Match.Example.Own
            ? $"▶ Hear David say “{row.Match.Word.Text}”"
            : $"▶ Hear David say “{row.Match.Word.Text}” ({ExampleSpeed(row.Match.Example)})";
        HearConvertedButton.IsEnabled = ConvertCheck.IsChecked == true && row.Match.Compared.Length > 0;
        DrawLanes();
        // Picking another word in the list plays David saying it.
        if (changed && e.RemovedItems.Count > 0 && RankingList.IsKeyboardFocusWithin) Play(row.Match.Word.Speech.Wav);
    }

    private void DrawLanes()
    {
        var selected = _selected;
        // The green lane is exactly what was compared.
        var shown = selected?.Compared;
        // The David lane shows the very example the recording was compared
        // with (e.g. David slow), so the two lanes line up as compared.
        var davidSamples = DavidShown(selected);
        // Fixed timing: each signal is drawn stretched evenly across the
        // full width, as it is compared.
        if (_recorder == null) VoiceSignalsWindow.DrawSignal(MeCanvas, _me, MeBrush, _me?.Length ?? 0);
        VoiceSignalsWindow.DrawSignal(ComparedCanvas, shown, ComparedBrush, shown?.Length ?? 0);
        VoiceSignalsWindow.DrawSignal(DavidCanvas, davidSamples, DavidBrush, davidSamples?.Length ?? 0);
        if (_me != null) MeInfo.Text = $"{_me.Length * 1000 / VoiceSamples.SampleRate} ms";
        if (selected == null) return;
        ComparedInfo.Text = ConvertCheck.IsChecked == true
            ? $"in David's style{(selected.MouthScale != 1 ? $", mouth size {selected.MouthScale:0.00}" : "")}" +
              (selected.Example.Own ? ", compared as recorded with your own example"
                  : ", the same for every word") +
              $" — {shown!.Length * 1000 / VoiceSamples.SampleRate} ms, drawn stretched to the fixed length"
            : "as recorded (David's style is off)";
        DavidTitle.Text = selected.Example.Own
            ? $"Windows David: “{selected.Word.Text}”"
            : $"Windows David: “{selected.Word.Text}” ({ExampleSpeed(selected.Example)})";
        DavidInfo.Text = $"{davidSamples!.Length * 1000 / VoiceSamples.SampleRate} ms, drawn stretched to the fixed length · score {selected.Score:P0} (mouth {selected.Mouth:P0}, loudness {selected.Loudness:P0})" +
                         (selected.Example.Own ? $" · it matched your own example {selected.Example.Source[4..]} best, shown here is David's normal take" : "");
    }

    // David's example the recording was compared with; his normal take when
    // the closest example was the person's own.
    private static float[]? DavidShown(DavidWordMatcher.Match? match) =>
        match == null ? null : match.Example.Own ? match.Word.Samples : match.Example.Samples;

    private static string ExampleSpeed(DavidWordMatcher.Example example) =>
        example.Source.StartsWith("David, ", StringComparison.Ordinal) ? example.Source["David, ".Length..] : "normal";

    // Your recording becomes one more example of the word picked in the list.
    private async void ThatsIt_Click(object sender, RoutedEventArgs e)
    {
        if (_me == null || _selected == null || _meTaughtAs == _selected.Word.Text) return;
        var word = _selected.Word;
        ThatsItButton.IsEnabled = false;
        await DavidWordMatcher.AddOwnExampleAsync(word, _me);
        _meTaughtAs = word.Text;
        RecordStatus.Text = $"Kept as an example of “{word.Text}” for this session ({word.Examples.Count(x => x.Own)} of yours). Record it again to see the difference.";
        await RankAsync(play: false);
    }

    // ------------------------------------------------------------ playing

    private void HearMe_Click(object sender, RoutedEventArgs e)
    {
        if (_me != null) Play(VoiceSamples.ToWav(_me));
    }

    private void HearConverted_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { Compared.Length: > 0 }) Play(VoiceSamples.ToWav(_selected.Compared));
    }

    private void HearDavid_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        // The example that was compared, at its own speed.
        Play(_selected.Example.Own || _selected.Example.Source == "David"
            ? _selected.Word.Speech.Wav
            : VoiceSamples.ToWav(_selected.Example.Samples));
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
            RecordStatus.Text = "Couldn't play the sound.";
        }
    }

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private sealed record RankRow(int Rank, DavidWordMatcher.Match Match)
    {
        public string Text => Match.Word.Text;
        public double BarWidth => Math.Max(2, Match.Score * 140);
        public string Detail => $"{Match.Score:P0}  {Match.Example.Source} (mouth {Match.Mouth:P0}, loudness {Match.Loudness:P0})";
    }
}

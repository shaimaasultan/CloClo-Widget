using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace CloCloWidget;

// Learn how Whisper spells your English from one passage read aloud, predict
// how it will spell a word list, and try the corrections on live dictation.
// Uses CloClo's local Whisper model; audio stays in memory and only the
// learned letter counts and the word list are saved (AccentProfile).
public partial class AccentPatternsWindow : Window
{
    private const string EnglishLanguageTag = "en-US";
    private const int MaximumHeardChars = 8000;

    // Original sentences, grouped by the sounds they exercise: p/b, f/v,
    // both "th" sounds, sh/ch/j/zh, r/l/w, short and long vowels, final
    // consonants, clusters, and CloClo's own command words.
    private const string Passage =
        "Please bring the big blue paper bag to the back porch. " +
        "Very few people have five fast vans in the village. " +
        "I think this thin thread is thicker than that thing there. " +
        "She chose cheap shoes and a jacket for the June journey. " +
        "Larry rarely rolls the red wheel along the low road with Will. " +
        "Is it a ship or a sheep? Fill the cup, then feel the full pool. " +
        "Bed, bad, bud and bird each sound a little different. " +
        "Get the cat, put the hat on the mat, and stop at the gate. " +
        "The strong student sprinted down the street to explain the script. " +
        "Measure the pleasure, and say yes to the zoo visit. " +
        "Hello, how are you? We were going to watch a movie with our neighbours. " +
        "Open Notepad, play some music on Spotify, then check the weather and the calendar. " +
        "Turn the volume up, close the window, and search the web for a good recipe.";

    private enum Mode { None, Passage, Test }

    private readonly AccentProfile _profile = AccentProfile.Load();
    private AccentCorrector? _corrector;
    private LocalAudioTranscriber? _session;
    private Mode _mode;
    private Task _stopping = Task.CompletedTask;
    private bool _heardLearned;
    private bool _closing;

    public AccentPatternsWindow()
    {
        InitializeComponent();
        PassageBox.Text = Passage;
        WordsBox.Text = _profile.Words;
        ShowPatterns();
        Predict();
        PassageStatus.Text = _profile.Takes == 0
            ? "No patterns yet. Record the passage; it is learned when you press Stop."
            : $"{_profile.Takes} take(s) learned.";
        Closing += (_, _) =>
        {
            _closing = true;
            SaveWords();
        };
        Closed += async (_, _) => await StopAsync();
    }

    // ------------------------------------------------------------ recording

    private async void RecordPassage_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Passage) { await StopAsync(); return; }
        HeardBox.Clear();
        await StartAsync(Mode.Passage);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Test) { await StopAsync(); return; }
        RawBox.Clear();
        CorrectedBox.Clear();
        ChangesBox.Clear();
        Predict();
        if (_profile.Rules.Count == 0 && _profile.Swaps.Count == 0)
            ChangesBox.Text = "No patterns learned yet: only exact list words will match. Record the passage first (step 1)." + Environment.NewLine;
        await StartAsync(Mode.Test);
    }

    private async Task StartAsync(Mode mode)
    {
        await StopAsync();
        if (_closing) return;
        if (!DictationCaptureWindow.CanRunLocalTranscription())
        {
            LiveText.Text = "Local Whisper needs Windows 11 and a CPU with AVX2, FMA, and F16C support.";
            return;
        }

        var session = new LocalAudioTranscriber(AudioCaptureSource.Microphone);
        _session = session;
        _mode = mode;
        UpdateButtons();
        LiveText.Text = "Loading the local speech model…";
        session.InterimTextRecognized += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_session == session) LiveText.Text = text.Length == 0 ? "Listening…" : "Hearing: " + text;
        }));
        session.TextRecognized += text => Dispatcher.BeginInvoke(new Action(() => OnText(session, mode, text)));
        session.Failed += message => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_session != session) return;
            await StopAsync();
            LiveText.Text = message;
        }));
        try
        {
            await session.StartAsync(EnglishLanguageTag);
            if (_session != session) return;
            LiveText.Text = mode == Mode.Passage
                ? $"Listening with the {session.Engine}. Read the passage at your normal pace, then press Stop."
                : $"Listening with the {session.Engine}. Say sentences that use your words, then press Stop.";
        }
        catch (Exception ex)
        {
            if (_session != session) return;
            await StopAsync();
            LiveText.Text = ex switch
            {
                FileNotFoundException => "The bundled speech model or runtime is missing. Reinstall CloClo.",
                UnauthorizedAccessException => "Microphone access was denied. Check Windows microphone privacy settings.",
                _ => $"Could not start local Whisper ({ex.GetType().Name}). Check the default microphone.",
            };
        }
    }

    // Whisper's last phrase arrives while the session drains, so text from a
    // session that is stopping still counts.
    private void OnText(LocalAudioTranscriber session, Mode mode, string text)
    {
        if (_closing || (_session != session && _draining != session)) return;
        text = text.Trim();
        if (text.Length == 0) return;
        if (mode == Mode.Passage)
        {
            if (HeardBox.Text.Length + text.Length > MaximumHeardChars) return;
            HeardBox.AppendText((HeardBox.Text.Length > 0 ? " " : "") + text);
        }
        else
        {
            RawBox.AppendText((RawBox.Text.Length > 0 ? " " : "") + text);
            var (corrected, changes) = (_corrector ??= NewCorrector()).Correct(text);
            CorrectedBox.AppendText((CorrectedBox.Text.Length > 0 ? " " : "") + corrected);
            ChangesBox.AppendText(changes.Count == 0
                ? $"no list word heard in: {text}{Environment.NewLine}"
                : string.Join(Environment.NewLine, changes) + Environment.NewLine);
            ChangesBox.ScrollToEnd();
        }
    }

    private LocalAudioTranscriber? _draining;

    private async Task StopAsync()
    {
        var wasPassage = _mode == Mode.Passage;
        var session = _session;
        _session = null;
        _mode = Mode.None;
        if (session != null)
        {
            _draining = session;
            _stopping = DrainAsync(session);
        }
        if (!_closing) UpdateButtons();
        await _stopping;
        // A finished passage is learned straight away; Learn is only needed
        // again after the heard text is edited or pasted.
        if (wasPassage && !_closing)
        {
            if (HeardBox.Text.Trim().Length > 0) Learn();
            else PassageStatus.Text = "Whisper heard nothing. Check the microphone and record the passage again.";
        }
    }

    private async Task DrainAsync(LocalAudioTranscriber session)
    {
        try { await session.DisposeAsync(); }
        catch { /* the session already reported its failure */ }
        await Dispatcher.InvokeAsync(() => { });
        if (_draining == session) _draining = null;
        if (!_closing && _session == null) LiveText.Text = "";
    }

    private void UpdateButtons()
    {
        RecordPassageButton.Content = _mode == Mode.Passage ? "■ Stop" : "● Record passage";
        TestButton.Content = _mode == Mode.Test ? "■ Stop" : "● Dictate test";
        RecordPassageButton.IsEnabled = _mode != Mode.Test;
        TestButton.IsEnabled = _mode != Mode.Passage;
        LearnButton.IsEnabled = CanLearn;
    }

    private bool CanLearn => _mode == Mode.None && !_heardLearned && HeardBox.Text.Trim().Length > 0;

    // ------------------------------------------------------------ learning

    // Text typed or pasted by hand is a new take; text Whisper adds while
    // recording is learned when recording stops.
    private void Heard_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (LearnButton == null) return;
        if (HeardBox.IsKeyboardFocusWithin) _heardLearned = false;
        LearnButton.IsEnabled = CanLearn;
    }

    private void Learn_Click(object sender, RoutedEventArgs e) => Learn();

    private void Learn()
    {
        var take = _profile.Learn(PassageBox.Text, HeardBox.Text);
        _profile.Save();
        _heardLearned = true; // one take is learned once
        LearnButton.IsEnabled = false;
        var accuracy = take.PassageWords == 0 ? 0 : 1 - (double)take.Misheard / take.PassageWords;
        PassageStatus.Text = take.Misheard == 0
            ? $"Take {_profile.Takes}: Whisper wrote all {take.PassageWords} words as written, so this take has nothing to learn. Saved."
            : $"Take {_profile.Takes}: {take.PassageWords} words, {take.Misheard} heard differently " +
              $"({Math.Max(0, accuracy):P0} right). {_profile.Rules.Count} letter patterns so far. Saved.";
        ShowPatterns(take);
        Predict();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget every learned pattern?", "CloClo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _profile.Reset();
        _profile.Save();
        PassageStatus.Text = "Patterns forgotten.";
        ShowPatterns();
        Predict();
    }

    private void ShowPatterns(AccentTake? take = null)
    {
        var text = new StringBuilder();
        if (take is { Differences.Count: > 0 })
        {
            text.AppendLine("This take - said → Whisper wrote:");
            foreach (var (expected, heard) in take.Differences.Take(40)) text.AppendLine($"  {expected,-16} → {heard}");
            text.AppendLine();
        }
        var rules = _profile.Rules.Select(r => (Rule: r, P: _profile.Probability(r)))
            .OrderByDescending(r => r.P).ThenByDescending(r => r.Rule.Count).ToList();
        if (rules.Count == 0)
        {
            text.AppendLine("No letter patterns yet.");
        }
        else
        {
            text.AppendLine("Letter patterns (said → written, how often):");
            foreach (var (rule, p) in rules.Take(60))
            {
                var heard = rule.Heard.Length == 0 ? "(dropped)" : rule.Heard;
                text.AppendLine($"  {rule.Expected,-4} → {heard,-10} {rule.Count,3} of {_profile.Seen.GetValueOrDefault(rule.Expected),-4} {p,5:P0}");
            }
        }
        var swaps = _profile.Swaps.OrderByDescending(s => s.Count).Take(30).ToList();
        if (swaps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Whole words written differently:");
            foreach (var swap in swaps) text.AppendLine($"  {swap.Expected,-16} → {swap.Heard,-16} ×{swap.Count}");
        }
        PatternsBox.Text = text.ToString();
    }

    // ------------------------------------------------------------ word list

    private void Predict_Click(object sender, RoutedEventArgs e)
    {
        SaveWords();
        Predict();
    }

    private void Words_LostFocus(object sender, RoutedEventArgs e) => SaveWords();

    private void SaveWords()
    {
        if (_profile.Words == WordsBox.Text) return;
        _profile.Words = WordsBox.Text;
        _profile.Save();
        _corrector = null;
    }

    private AccentCorrector NewCorrector() => new(_profile, AccentProfile.WordList(WordsBox.Text));

    private void Predict()
    {
        _corrector = NewCorrector();
        var text = new StringBuilder();
        foreach (var word in AccentProfile.WordList(WordsBox.Text))
        {
            var spellings = _profile.Spellings(word);
            text.Append($"{word,-14} ← ");
            text.AppendLine(spellings.Count == 0
                ? "(no learned differences apply)"
                : string.Join(", ", spellings.Select(s => $"{s.Text} {s.Probability:P0}")));
        }
        SpellingsBox.Text = text.ToString();
    }

    private void CorrectTyped_Click(object sender, RoutedEventArgs e)
    {
        Predict();
        var (corrected, changes) = _corrector!.Correct(RawBox.Text.Replace('\r', ' ').Replace('\n', ' '));
        CorrectedBox.Text = corrected;
        ChangesBox.Text = changes.Count > 0 ? string.Join(Environment.NewLine, changes)
            : _profile.Rules.Count == 0 && _profile.Swaps.Count == 0
                ? "No changes: no patterns learned yet. Record the passage first (step 1)."
                : "No changes: no word here is close to a list word under your patterns.";
    }
}

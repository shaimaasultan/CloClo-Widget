using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CloCloWidget;

// Memorize the Quran an ayah - or a short run of ayat - at a time. The ayat
// are shown large and, with a reciter folder chosen, recited one after the
// other with the ayah being recited highlighted; after the last one comes a
// short pause and the run starts again, until it is marked memorized. Then
// the next run of the same length begins. Everything is offline: the text is
// compiled into the exe and the recitations are the person's own local files.
public partial class QuranWindow : Window
{
    private sealed record Choice(int Value, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xf3, 0xec, 0xdd));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0xcb, 0xbf, 0xa8));
    private static readonly Brush HighlightBrush = Frozen(Color.FromRgb(0xe6, 0xc4, 0x76));

    private readonly QuranStore _store;
    private readonly LocalAudioPlayer _player = new();
    private readonly DispatcherTimer _gapTimer = new();
    private readonly List<Run> _ayahRuns = new();
    private readonly List<Run> _englishRuns = new();
    private bool _reciting;
    private bool _ready;
    private int _repeats;
    // Which ayah of the range is being recited (0 = the first).
    private int _playIndex;
    // A one-off message for the status line, cleared when the range changes.
    private string? _notice;

    // Raised when the loop starts or stops, so the main window can keep hold
    // to talk from listening to the recitation, and stop it for the azan.
    public event Action<bool>? RecitingChanged;

    public bool IsReciting => _reciting;

    private int Start => _store.Ayah;
    private int End => _store.AyahEnd;
    private int Length => End - Start + 1;

    public QuranWindow(QuranStore store)
    {
        InitializeComponent();
        _store = store;
        HeaderGlyph.Text = "\uE82D";
        FooterText.Text = QuranText.Attribution + " " + QuranText.EnglishAttribution +
                          " Recitations: per-ayah MP3 files named like everyayah.com's " +
                          "(001001.mp3 is Al-Fatiha 1), in a folder you choose.";

        SurahBox.ItemsSource = QuranText.Surahs
            .Select(s => new Choice(s.Number, $"{s.Number}. {s.ArabicName} — {s.Name}")).ToList();
        GapBox.ItemsSource = QuranStore.GapChoices.Select(g => new Choice(g, $"{g} s")).ToList();
        ReminderBox.ItemsSource = QuranStore.ReminderChoices
            .Select(m => new Choice(m, m == 0 ? "Off" : m < 60 ? $"Every {m} min" : $"Every {m / 60} h")).ToList();
        GapBox.SelectedItem = ((List<Choice>)GapBox.ItemsSource).First(c => c.Value == _store.GapSeconds);
        ReminderBox.SelectedItem = ((List<Choice>)ReminderBox.ItemsSource).First(c => c.Value == _store.ReminderMinutes);

        _player.Finished += OnRecitationFinished;
        _gapTimer.Tick += (_, _) =>
        {
            _gapTimer.Stop();
            if (_reciting) PlayCurrent();
        };
        Closed += (_, _) =>
        {
            SetReciting(false);
            _player.Dispose();
        };

        EnglishBox.IsChecked = _store.ShowEnglish;
        ShowRange(_store.Surah, _store.Ayah, _store.AyahEnd);
        _ready = true;
    }

    // Stops the loop (for the azan, or when another sound needs the speakers).
    public void StopRecitation() => SetReciting(false);

    private void ShowRange(int surah, int start, int end)
    {
        var info = QuranText.GetSurah(surah);
        start = Math.Clamp(start, 1, info.AyahCount);
        end = Math.Clamp(end, start, Math.Min(info.AyahCount, start + QuranStore.MaxRange - 1));

        var wasReciting = _reciting;
        SetReciting(false);
        _store.Surah = surah;
        _store.Ayah = start;
        _store.AyahEnd = end;
        _store.Save();
        _repeats = 0;
        _notice = null;

        var oldReady = _ready;
        _ready = false;
        SurahBox.SelectedIndex = surah - 1;
        AyahBox.ItemsSource = Enumerable.Range(1, info.AyahCount).Select(n => new Choice(n, n.ToString(CultureInfo.CurrentCulture))).ToList();
        AyahBox.SelectedIndex = start - 1;
        AyahEndBox.ItemsSource = Enumerable.Range(start, Math.Min(info.AyahCount, start + QuranStore.MaxRange - 1) - start + 1)
            .Select(n => new Choice(n, n.ToString(CultureInfo.CurrentCulture))).ToList();
        AyahEndBox.SelectedIndex = end - start;
        _ready = oldReady;

        SurahSubtitle.Text = $"سورة {info.ArabicName} · {info.Name} ({info.Meaning}) · {info.AyahCount} ayat";
        BuildText();
        System.Windows.Automation.AutomationProperties.SetHelpText(AyahText,
            Length == 1 ? $"{info.Name}, ayah {start}" : $"{info.Name}, ayat {start} to {end}");

        UpdateState();
        if (wasReciting) SetReciting(true);
    }

    // The range as runs, one per ayah, so the one being recited can be lit up.
    private void BuildText()
    {
        _ayahRuns.Clear();
        AyahText.Inlines.Clear();
        string? basmala = null;
        for (var ayah = Start; ayah <= End; ayah++)
        {
            var (b, text) = QuranText.GetAyah(_store.Surah, ayah);
            basmala ??= b;
            var run = new Run(text + " " + AyahMarker(ayah)) { Foreground = TextBrush };
            _ayahRuns.Add(run);
            if (ayah > Start) AyahText.Inlines.Add(new Run(" "));
            AyahText.Inlines.Add(run);
        }
        BasmalaText.Text = basmala ?? "";
        BasmalaText.Visibility = basmala != null ? Visibility.Visible : Visibility.Collapsed;
        ShowEnglish();
    }

    private void ShowEnglish()
    {
        _englishRuns.Clear();
        EnglishText.Inlines.Clear();
        EnglishText.Visibility = _store.ShowEnglish ? Visibility.Visible : Visibility.Collapsed;
        // Smaller with the English, and smaller again for a run of ayat, so
        // the text and its meaning usually fit without scrolling.
        var size = (_store.ShowEnglish ? 29 : 34) - (Length > 1 ? 3 : 0);
        AyahText.FontSize = size;
        AyahText.LineHeight = size * 1.7;
        if (!_store.ShowEnglish) return;
        for (var ayah = Start; ayah <= End; ayah++)
        {
            if (ayah > Start) EnglishText.Inlines.Add(new LineBreak());
            var prefix = Length > 1 ? $"({ayah}) " : "";
            var run = new Run(prefix + QuranText.GetEnglish(_store.Surah, ayah)) { Foreground = MutedBrush };
            _englishRuns.Add(run);
            EnglishText.Inlines.Add(run);
        }
        Highlight();
    }

    // Lights up the ayah being recited (only while a run of ayat is playing).
    private void Highlight()
    {
        var lit = _reciting && Length > 1 ? _playIndex : -1;
        for (var i = 0; i < _ayahRuns.Count; i++)
            _ayahRuns[i].Foreground = i == lit ? HighlightBrush : TextBrush;
        for (var i = 0; i < _englishRuns.Count; i++)
            _englishRuns[i].Foreground = i == lit ? TextBrush : MutedBrush;
        if (lit >= 0 && lit < _ayahRuns.Count) _ayahRuns[lit].BringIntoView();
    }

    private void EnglishBox_Click(object sender, RoutedEventArgs e)
    {
        _store.ShowEnglish = EnglishBox.IsChecked == true;
        _store.Save();
        ShowEnglish();
    }

    // The ayah number between ornate brackets, in Arabic-Indic digits: ﴿٥﴾.
    private static string AyahMarker(int ayah)
    {
        var digits = new StringBuilder();
        foreach (var c in ayah.ToString(CultureInfo.InvariantCulture)) digits.Append((char)(0x0660 + (c - '0')));
        return $"{(char)0xFD3F}{digits}{(char)0xFD3E}";
    }

    // The first ayah of the range with no recitation file, or null if all have one.
    private int? MissingAudio()
    {
        for (var ayah = Start; ayah <= End; ayah++)
            if (_store.AudioFileFor(_store.Surah, ayah) == null) return ayah;
        return null;
    }

    private void UpdateState()
    {
        var surah = _store.Surah;
        var info = QuranText.GetSurah(surah);
        var missing = MissingAudio();
        var hasAudio = missing == null;
        var range = Length > 1;

        PlayButton.Content = hasAudio ? (_reciting ? "❚❚ Pause" : "▶ Repeat") : "+1 read";
        var count = _repeats switch
        {
            0 => "",
            1 => hasAudio ? "heard once" : "read once",
            _ => hasAudio ? $"heard {_repeats} times" : $"read {_repeats} times",
        };
        RepeatText.Text = _reciting && range
            ? $"Ayah {Start + _playIndex} of {Start}–{End}" + (count.Length > 0 ? $" · {count}" : "")
            : count.Length > 0
                ? char.ToUpper(count[0], CultureInfo.CurrentCulture) + count[1..]
                : hasAudio ? "Press Repeat to hear it on a loop" : "Read it aloud, then press +1 read";

        var memorized = Enumerable.Range(Start, Length).All(a => _store.IsMemorized(surah, a));
        MemorizedBadge.Visibility = memorized ? Visibility.Visible : Visibility.Collapsed;
        MemorizedButton.Content = memorized
            ? (range ? "Next ayat ▶" : "Next ayah ▶")
            : (range ? $"✓ Memorized — next {Length} ayat" : "✓ Memorized — next ayah");

        var done = _store.MemorizedIn(surah);
        SurahProgress.Maximum = info.AyahCount;
        SurahProgress.Value = done;
        ProgressText.Text = $"{done} of {info.AyahCount} ayat memorized in {info.Name} · {_store.Memorized.Count} in all";

        var installed = _store.AudioFolder == null ? QuranStore.InstalledReciterFolder() : null;
        FolderText.Text = _store.AudioFolder
            ?? (installed != null ? $"Installed: {System.IO.Path.GetFileName(installed)}" : "No reciter folder: text only");
        StatusText.Text = _notice ?? (_store.EffectiveAudioFolder != null && missing is { } m
            ? $"The reciter folder has no {surah:000}{m:000}.mp3, so {(range ? "these ayat are" : "this ayah is")} text only."
            : "");
    }

    private void SetReciting(bool on)
    {
        if (on && MissingAudio() != null) on = false;
        if (on == _reciting)
        {
            if (on) return;
            _gapTimer.Stop();
            _player.Stop();
            return;
        }
        _reciting = on;
        _playIndex = 0;
        _gapTimer.Stop();
        if (on) PlayCurrent();
        else _player.Stop();
        Highlight();
        RecitingChanged?.Invoke(on);
        if (IsLoaded) UpdateState();
    }

    private void PlayCurrent()
    {
        var path = _store.AudioFileFor(_store.Surah, Start + _playIndex);
        if (path == null || !_player.TryPlay(path))
        {
            _notice = "Couldn't play the recitation file. Check the reciter folder.";
            SetReciting(false);
            return;
        }
        Highlight();
        UpdateState();
    }

    // The next ayah of the range follows straight on; after the last one
    // comes the pause, then the range starts again.
    private void OnRecitationFinished()
    {
        if (!_reciting) return;
        if (_playIndex < Length - 1)
        {
            _playIndex++;
            PlayCurrent();
            return;
        }
        _repeats++;
        _playIndex = 0;
        Highlight();
        UpdateState();
        _gapTimer.Interval = TimeSpan.FromSeconds(_store.GapSeconds);
        _gapTimer.Start();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (MissingAudio() != null)
        {
            _repeats++;
            UpdateState();
            return;
        }
        SetReciting(!_reciting);
    }

    private void Memorized_Click(object sender, RoutedEventArgs e)
    {
        for (var ayah = Start; ayah <= End; ayah++)
        {
            var index = QuranText.GlobalIndex(_store.Surah, ayah);
            if (!_store.Memorized.Contains(index)) _store.Memorized.Add(index);
        }
        _store.Memorized.Sort();

        var (surah, length) = (_store.Surah, Length);
        var count = QuranText.GetSurah(surah).AyahCount;
        if (End < count)
        {
            ShowRange(surah, End + 1, End + length);
        }
        else if (surah < 114)
        {
            ShowRange(surah + 1, 1, length);
            _notice = $"{QuranText.GetSurah(surah).Name} is done. Starting {QuranText.GetSurah(surah + 1).Name}.";
            UpdateState();
        }
        else
        {
            _store.Save();
            _notice = "That was the last ayah of the Quran. May Allah accept it.";
            UpdateState();
        }
    }

    // Back by one range of the same length, without unmarking anything.
    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        var (surah, length) = (_store.Surah, Length);
        if (Start > 1)
        {
            ShowRange(surah, Math.Max(1, Start - length), Start - 1);
        }
        else if (surah > 1)
        {
            var count = QuranText.GetSurah(surah - 1).AyahCount;
            ShowRange(surah - 1, Math.Max(1, count - length + 1), count);
        }
    }

    private void SurahBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SurahBox.SelectedItem is not Choice choice || choice.Value == _store.Surah) return;
        ShowRange(choice.Value, 1, Length);
    }

    // Moving the start keeps the length of the range where it can.
    private void AyahBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AyahBox.SelectedItem is not Choice choice || choice.Value == Start) return;
        ShowRange(_store.Surah, choice.Value, choice.Value + Length - 1);
    }

    private void AyahEndBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AyahEndBox.SelectedItem is not Choice choice || choice.Value == End) return;
        ShowRange(_store.Surah, Start, choice.Value);
    }

    private void GapBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GapBox.SelectedItem is not Choice choice) return;
        _store.GapSeconds = choice.Value;
        _store.Save();
    }

    private void ReminderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReminderBox.SelectedItem is not Choice choice) return;
        _store.ReminderMinutes = choice.Value;
        _store.Save();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder of per-ayah recitations (001001.mp3 …)", Multiselect = false };
        // Start at the recitations a Program Files install put beside CloClo.
        if (System.IO.Directory.Exists(QuranStore.InstalledRecitationsRoot))
            picker.InitialDirectory = QuranStore.InstalledRecitationsRoot;
        if (picker.ShowDialog(this) != true) return;
        if (!QuranStore.IsAllowedFolder(picker.FolderName))
        {
            _notice = "Choose a folder on this PC (not a network share).";
            UpdateState();
            return;
        }
        SetReciting(false);
        _store.AudioFolder = picker.FolderName;
        _store.Save();
        _notice = _store.AudioFileFor(1, 1) != null || _store.AudioFileFor(_store.Surah, Start) != null
            ? null
            : "No files named like 001001.mp3 were found in that folder.";
        UpdateState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CloCloWidget;

// Memorize the Quran one ayah at a time: the ayah is shown large and, with a
// reciter folder chosen, played on a loop with a short pause between
// repetitions, until it is marked memorized; then the next ayah begins.
// Everything is offline: the text is compiled into the exe and the
// recitations are the person's own local files.
public partial class QuranWindow : Window
{
    private sealed record Choice(int Value, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly QuranStore _store;
    private readonly LocalAudioPlayer _player = new();
    private readonly DispatcherTimer _gapTimer = new();
    private bool _reciting;
    private bool _ready;
    private int _repeats;
    // A one-off message for the status line, cleared when the ayah changes.
    private string? _notice;

    // Raised when the loop starts or stops, so the main window can keep hold
    // to talk from listening to the recitation, and stop it for the azan.
    public event Action<bool>? RecitingChanged;

    public bool IsReciting => _reciting;

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
        GapBox.SelectedItem = ((System.Collections.Generic.List<Choice>)GapBox.ItemsSource).First(c => c.Value == _store.GapSeconds);
        ReminderBox.SelectedItem = ((System.Collections.Generic.List<Choice>)ReminderBox.ItemsSource).First(c => c.Value == _store.ReminderMinutes);

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
        ShowAyah(_store.Surah, _store.Ayah);
        _ready = true;
    }

    // Stops the loop (for the azan, or when another sound needs the speakers).
    public void StopRecitation() => SetReciting(false);

    private void ShowAyah(int surah, int ayah)
    {
        var wasReciting = _reciting;
        SetReciting(false);
        _store.Surah = surah;
        _store.Ayah = ayah;
        _store.Save();
        _repeats = 0;
        _notice = null;

        var info = QuranText.GetSurah(surah);
        var oldReady = _ready;
        _ready = false;
        SurahBox.SelectedIndex = surah - 1;
        AyahBox.ItemsSource = Enumerable.Range(1, info.AyahCount).Select(n => new Choice(n, n.ToString(CultureInfo.CurrentCulture))).ToList();
        AyahBox.SelectedIndex = ayah - 1;
        _ready = oldReady;

        SurahSubtitle.Text = $"سورة {info.ArabicName} · {info.Name} ({info.Meaning}) · {info.AyahCount} ayat";
        var (basmala, text) = QuranText.GetAyah(surah, ayah);
        BasmalaText.Text = basmala ?? "";
        BasmalaText.Visibility = basmala != null ? Visibility.Visible : Visibility.Collapsed;
        AyahText.Text = text + " " + AyahMarker(ayah);
        ShowEnglish();
        System.Windows.Automation.AutomationProperties.SetHelpText(AyahText, $"{info.Name}, ayah {ayah}");

        UpdateState();
        if (wasReciting) SetReciting(true);
    }

    private void ShowEnglish()
    {
        EnglishText.Visibility = _store.ShowEnglish ? Visibility.Visible : Visibility.Collapsed;
        // A little smaller with the English, so a long ayah and its meaning
        // usually fit without scrolling.
        AyahText.FontSize = _store.ShowEnglish ? 29 : 34;
        AyahText.LineHeight = _store.ShowEnglish ? 50 : 58;
        EnglishText.Text = _store.ShowEnglish ? QuranText.GetEnglish(_store.Surah, _store.Ayah) : "";
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

    private void UpdateState()
    {
        var surah = _store.Surah;
        var ayah = _store.Ayah;
        var info = QuranText.GetSurah(surah);
        var hasAudio = _store.AudioFileFor(surah, ayah) != null;

        PlayButton.Content = hasAudio ? (_reciting ? "❚❚ Pause" : "▶ Repeat") : "+1 read";
        RepeatText.Text = _repeats switch
        {
            0 => hasAudio ? "Press Repeat to hear it on a loop" : "Read it aloud, then press +1 read",
            1 => hasAudio ? "Heard once" : "Read once",
            _ => hasAudio ? $"Heard {_repeats} times" : $"Read {_repeats} times",
        };
        var memorized = _store.IsMemorized(surah, ayah);
        MemorizedBadge.Visibility = memorized ? Visibility.Visible : Visibility.Collapsed;
        MemorizedButton.Content = memorized ? "Next ayah ▶" : "✓ Memorized — next ayah";

        var done = _store.MemorizedIn(surah);
        SurahProgress.Maximum = info.AyahCount;
        SurahProgress.Value = done;
        ProgressText.Text = $"{done} of {info.AyahCount} ayat memorized in {info.Name} · {_store.Memorized.Count} in all";

        FolderText.Text = _store.AudioFolder ?? "No reciter folder: text only";
        StatusText.Text = _notice ?? (_store.AudioFolder != null && !hasAudio
            ? $"The reciter folder has no {surah:000}{ayah:000}.mp3, so this ayah is text only."
            : "");
    }

    private void SetReciting(bool on)
    {
        if (on && _store.AudioFileFor(_store.Surah, _store.Ayah) == null) on = false;
        if (on == _reciting)
        {
            if (on) return;
            _gapTimer.Stop();
            _player.Stop();
            return;
        }
        _reciting = on;
        _gapTimer.Stop();
        if (on) PlayCurrent();
        else _player.Stop();
        RecitingChanged?.Invoke(on);
        if (IsLoaded) UpdateState();
    }

    private void PlayCurrent()
    {
        var path = _store.AudioFileFor(_store.Surah, _store.Ayah);
        if (path == null || !_player.TryPlay(path))
        {
            _notice = "Couldn't play the recitation file. Check the reciter folder.";
            SetReciting(false);
        }
    }

    private void OnRecitationFinished()
    {
        _repeats++;
        UpdateState();
        if (!_reciting) return;
        _gapTimer.Interval = TimeSpan.FromSeconds(_store.GapSeconds);
        _gapTimer.Start();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_store.AudioFileFor(_store.Surah, _store.Ayah) == null)
        {
            _repeats++;
            UpdateState();
            return;
        }
        SetReciting(!_reciting);
    }

    private void Memorized_Click(object sender, RoutedEventArgs e)
    {
        var index = QuranText.GlobalIndex(_store.Surah, _store.Ayah);
        if (!_store.Memorized.Contains(index))
        {
            _store.Memorized.Add(index);
            _store.Memorized.Sort();
        }
        var (surah, ayah) = (_store.Surah, _store.Ayah);
        if (ayah < QuranText.GetSurah(surah).AyahCount)
        {
            ShowAyah(surah, ayah + 1);
        }
        else if (surah < 114)
        {
            ShowAyah(surah + 1, 1);
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

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        var (surah, ayah) = (_store.Surah, _store.Ayah);
        if (ayah > 1) ShowAyah(surah, ayah - 1);
        else if (surah > 1) ShowAyah(surah - 1, QuranText.GetSurah(surah - 1).AyahCount);
    }

    private void SurahBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SurahBox.SelectedItem is not Choice choice || choice.Value == _store.Surah) return;
        ShowAyah(choice.Value, 1);
    }

    private void AyahBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AyahBox.SelectedItem is not Choice choice || choice.Value == _store.Ayah) return;
        ShowAyah(_store.Surah, choice.Value);
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
        _notice = _store.AudioFileFor(1, 1) != null || _store.AudioFileFor(_store.Surah, _store.Ayah) != null
            ? null
            : "No files named like 001001.mp3 were found in that folder.";
        UpdateState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

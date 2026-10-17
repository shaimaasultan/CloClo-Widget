using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CloCloWidget;

public partial class PrayerTimesWindow : Window
{
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly TimeZoneInfo _timeZone;
    private readonly string? _timeZoneId;
    private readonly LocalAudioPlayer _preview = new();
    private readonly WidgetSettings _settings;
    private string? _audioPath;

    public bool AzanEnabled => EnableAzanBox.IsChecked == true;
    public bool PrayerLookEnabled => EnableLookBox.IsChecked == true;
    public string CalculationMethod => SelectedTag(MethodBox);
    public string AsrMethod => SelectedTag(AsrBox);
    public string? AudioPath => _audioPath;

    public PrayerTimesWindow(WidgetSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _latitude = settings.Latitude;
        _longitude = settings.Longitude;
        _timeZoneId = settings.LocationTimeZoneId;
        var hasLocationTimeZone = PrayerTimeZone.TryResolve(_timeZoneId, out var zone);
        _timeZone = zone;
        LocationText.Text = $"{settings.City}  ({_latitude:F2}, {_longitude:F2})";
        TimeZoneText.Text = !hasLocationTimeZone
            ? "Times use this PC's time zone until the location time zone is available. Compare with your local mosque."
            : $"Times use {_timeZoneId}. Calculated offline from the saved location; compare with your local mosque.";
        SelectTag(MethodBox, settings.PrayerCalculationMethod);
        SelectTag(AsrBox, settings.PrayerAsrMethod);
        _audioPath = settings.PrayerAzanAudioPath;
        EnableAzanBox.IsChecked = settings.PrayerAzanEnabled;
        EnableLookBox.IsChecked = settings.PrayerLookEnabled;
        UpdateAudioLabel();
        UpdateTimes();
        Closed += (_, _) => _preview.Dispose();
    }

    private static string SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "isna";

    private static void SelectTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            ?? box.Items[0];
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TimesText != null && MethodBox.SelectedItem != null && AsrBox.SelectedItem != null)
            UpdateTimes();
    }

    private void UpdateTimes()
    {
        var locationDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).Date;
        ScheduleHeading.Text = $"Five prayer times — {locationDate:ddd, MMM d}";
        var times = PrayerTimesCalculator.ForDate(locationDate,
            _latitude, _longitude, CalculationMethod, AsrMethod);
        TimesText.Text = times.Count == 0
            ? "Prayer times are unavailable for this location and date."
            : string.Join(Environment.NewLine, times.Select(prayer =>
                $"{prayer.Name,-10} {TimeZoneInfo.ConvertTime(prayer.StartsAtUtc, _timeZone):h:mm tt}"));
    }

    private void UpdateAudioLabel() => AudioFileText.Text =
        _audioPath is null ? "No audio file selected" : Path.GetFileName(_audioPath);

    private void ChooseAudio_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a local azan recording",
            Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true) return;
        if (!LocalAudioPlayer.IsAllowedLocalAudioFile(picker.FileName))
        {
            StatusText.Text = "Choose an MP3 or WAV file on this PC.";
            return;
        }
        _audioPath = picker.FileName;
        StatusText.Text = "";
        UpdateAudioLabel();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _preview.TryPlay(_audioPath)
            ? "Playing preview. Close this window to stop it."
            : "The audio file is missing, unreadable, or over 30 MB.";
    }

    private void Qibla_Click(object sender, RoutedEventArgs e)
    {
        var qibla = new QiblaWindow(_settings) { Owner = this, Topmost = Topmost };
        qibla.ShowDialog();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AzanEnabled && !LocalAudioPlayer.IsPlayable(_audioPath))
        {
            StatusText.Text = "Choose a readable local MP3 or WAV file (up to 30 MB) before enabling azan.";
            return;
        }
        DialogResult = true;
    }
}

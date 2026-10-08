using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloCloWidget;

public class WidgetSettings
{
    private const int MaxSettingsFileBytes = 64 * 1024;
    private const int MaxVisibleIcons = 7;
    private static readonly JsonSerializerOptions LoadOptions = new() { MaxDepth = 16 };
    private static readonly HashSet<string> AllowedSkies = new(StringComparer.Ordinal)
    {
        "auto", "clear", "cloudy", "mostlycloudy", "overcast", "windy", "fog", "drizzle", "rain", "snow", "storm",
    };
    private static readonly HashSet<string> AllowedIcons = new(StringComparer.Ordinal)
    {
        "youtube", "spotify", "bell", "search", "gpu", "read", "mic", "convert", "color", "qr", "volume", "clipboard", "stopwatch", "daily", "sudoku",
    };
    private static readonly HashSet<int> AllowedSleepIntervals = new() { 0, 2, 5, 15 };
    private static readonly HashSet<int> AllowedWorkoutIntervals = new() { 0, 15, 30, 45, 60 };
    public string City { get; set; } = "Cairo";
    public double Latitude { get; set; } = 30.0444;
    public double Longitude { get; set; } = 31.2357;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    // "auto" follows the real forecast; otherwise one of clear/rain/snow/storm,
    // picked by hand from the right-click menu, overriding the live sky.
    public string Sky { get; set; } = "auto";
    // Where dictated text (see SpeechToText.cs) currently gets appended —
    // null until the first dictation ever happens, then persists across
    // restarts so "append" keeps landing in the same file until the user
    // explicitly starts a new one.
    public string? DictationFilePath { get; set; }
    // Which media-bar icons are currently shown — the bar only has room
    // for about 7 at once (see MainWindow.xaml.cs's icon layout math), so
    // this is how the "Icons" menu lets newer/less-essential ones (convert,
    // color) be swapped in without silently overflowing the bar. Defaults
    // to exactly the icons that existed before this became configurable.
    public HashSet<string> EnabledIcons { get; set; } = new()
    {
        "youtube", "spotify", "bell", "search", "gpu", "read", "mic",
    };
    // How long with no keyboard/mouse input anywhere on the system (see
    // IdleDetection.cs) before Keeper visually falls asleep — 0 means
    // never. Picked from the right-click menu's "Sleep after" submenu.
    public int SleepAfterMinutes { get; set; } = 5;
    // How long a stretch of continuous activity (no break of at least a
    // couple of minutes) before Keeper nudges a stretch break — 0 means
    // never. Picked from the right-click menu's "Workout reminder" submenu.
    public int WorkoutAfterMinutes { get; set; } = 15;
    // Keeps the widget (and the clipboard/QR windows it opens) out of
    // screenshots, recordings and screen sharing — see ScreenCapture.cs.
    // On by default: it changes nothing on the local display.
    public bool HideFromScreenCapture { get; set; } = true;
    // Off = the notification label shows only which app sent it, never the
    // message text itself (2FA codes, message previews…).
    public bool ShowNotificationText { get; set; } = true;
    // Speaks a short welcome using the local account name when it is a
    // simple, safe-to-say name. The user can disable it in Privacy.
    public bool SpeakGreeting { get; set; } = true;
    // Which figure the widget shows: "keeper" or "lady" - picked from the
    // right-click menu's "Avatar" submenu.
    public string Avatar { get; set; } = "keeper";
    // Each avatar's everyday outfit colours - a key of KeeperView.Outfits,
    // picked from "Avatar" -> "Outfit colour".
    public string KeeperOutfit { get; set; } = "classic";
    public string LadyOutfit { get; set; } = "classic";

    // Two decimals is ~1 km: plenty for weather and a city name, without
    // keeping or sending a precise position.
    public static double RoundCoordinate(double value) => Math.Round(value, 2);

    private static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloCloWidget");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var bytes = ReadSettingsBytes();
                var loaded = JsonSerializer.Deserialize<WidgetSettings>(bytes, LoadOptions);
                if (loaded != null)
                {
                    return loaded.ValidateAndNormalize();
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults below.
        }
        return new WidgetSettings();
    }

    private static byte[] ReadSettingsBytes()
    {
        // Read at most the limit plus one byte; checking FileInfo.Length alone
        // would allow a file that grows after the check to use unbounded memory.
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaxSettingsFileBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxSettingsFileBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("Settings file exceeds the size limit.");
        Array.Resize(ref buffer, count);
        return buffer;
    }

    private WidgetSettings ValidateAndNormalize()
    {
        City = IsSafeText(City, 100) ? City.Trim() : "Cairo";
        Latitude = IsValidCoordinate(Latitude, -90, 90) ? RoundCoordinate(Latitude) : 30.04;
        Longitude = IsValidCoordinate(Longitude, -180, 180) ? RoundCoordinate(Longitude) : 31.24;
        WindowLeft = IsValidWindowCoordinate(WindowLeft) ? WindowLeft : null;
        WindowTop = IsValidWindowCoordinate(WindowTop) ? WindowTop : null;
        Sky = Sky is not null && AllowedSkies.Contains(Sky) ? Sky : "auto";
        Avatar = Avatar is "keeper" or "lady" ? Avatar : "keeper";
        KeeperOutfit = IsAllowedOutfit(KeeperOutfit) ? KeeperOutfit : "classic";
        LadyOutfit = IsAllowedOutfit(LadyOutfit) ? LadyOutfit : "classic";
        SleepAfterMinutes = AllowedSleepIntervals.Contains(SleepAfterMinutes) ? SleepAfterMinutes : 5;
        WorkoutAfterMinutes = AllowedWorkoutIntervals.Contains(WorkoutAfterMinutes) ? WorkoutAfterMinutes : 15;
        if (DictationFilePath is not null &&
            (DictationFilePath.Length > 2048 || !SpeechToText.IsInDictationFolder(DictationFilePath)))
            DictationFilePath = null;

        var enabled = EnabledIcons ?? new WidgetSettings().EnabledIcons;
        EnabledIcons = new HashSet<string>(
            enabled.Where(key => key is not null && AllowedIcons.Contains(key))
                   .Take(MaxVisibleIcons),
            StringComparer.Ordinal);
        return this;
    }

    private static bool IsSafeText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsValidCoordinate(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;

    private static bool IsValidWindowCoordinate(double? value) =>
        value is null || (double.IsFinite(value.Value) && Math.Abs(value.Value) <= 1_000_000);

    private static bool IsAllowedOutfit(string? value) =>
        value is not null && KeeperView.Outfits.Any(outfit => outfit.Key == value);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            // Written beside and then swapped in, so a crash mid-write can
            // never leave a half-written file — which Load would discard,
            // silently resetting privacy choices like ShowNotificationText.
            // The temporary file gets a random name and must not exist yet
            // (CreateNew): a fixed name could be pre-created by something
            // else - as a link, say - to redirect this write.
            var temp = Path.Combine(FolderPath, "settings." + Path.GetRandomFileName() + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                }
                File.Move(temp, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch
        {
            // Best-effort persistence; losing a save isn't fatal.
        }
    }
}

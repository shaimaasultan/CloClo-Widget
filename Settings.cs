using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CloCloWidget;

public class WidgetSettings
{
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
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<WidgetSettings>(json);
                if (loaded != null)
                {
                    // Older builds stored full GPS precision; trimmed here so
                    // the next save no longer keeps it on disk.
                    loaded.Latitude = RoundCoordinate(loaded.Latitude);
                    loaded.Longitude = RoundCoordinate(loaded.Longitude);
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults below.
        }
        return new WidgetSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            // Written beside and then swapped in, so a crash mid-write can
            // never leave a half-written file — which Load would discard,
            // silently resetting privacy choices like ShowNotificationText.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence; losing a save isn't fatal.
        }
    }
}

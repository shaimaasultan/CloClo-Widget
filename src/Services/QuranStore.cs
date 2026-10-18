using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloCloWidget;

// Quran memorization progress: the ayah or run of ayat being learnt, the ayat marked as
// memorized, the reciter folder and the repeat and reminder choices. Kept in
// its own file beside settings.json and written the same way (a fresh
// temporary file, then swapped in). Everything read back is checked, since
// any program running as this user can edit the file.
public sealed class QuranStore
{
    private const int MaxStoreFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions LoadOptions = new() { MaxDepth = 8 };
    public static readonly int[] GapChoices = { 1, 2, 3, 5 };
    public static readonly int[] ReminderChoices = { 0, 30, 60, 120 };
    // The longest run of ayat learnt together.
    public const int MaxRange = 20;

    public int Surah { get; set; } = 1;
    // The ayat being learnt: from Ayah to AyahEnd (the same for one ayah).
    public int Ayah { get; set; } = 1;
    public int AyahEnd { get; set; } = 1;
    // A folder of per-ayah recitations named like everyayah.com's
    // (001001.mp3 = Al-Fatiha 1; .wav works too). Chosen by the person;
    // never downloaded.
    public string? AudioFolder { get; set; }
    // Seconds of quiet between repetitions.
    public int GapSeconds { get; set; } = 2;
    // Whether the English meaning is shown under the Arabic.
    public bool ShowEnglish { get; set; }
    // How often the avatar shows the ayah being learnt; 0 = never.
    public int ReminderMinutes { get; set; }
    // Memorized ayat, as positions in the whole Quran (0 to 6235).
    public List<int> Memorized { get; set; } = new();

    private static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloCloWidget");

    private static string FilePath => Path.Combine(FolderPath, "quran.json");

    public bool IsMemorized(int surah, int ayah) => Memorized.Contains(QuranText.GlobalIndex(surah, ayah));

    public int MemorizedIn(int surah)
    {
        var first = QuranText.GlobalIndex(surah, 1);
        var last = first + QuranText.GetSurah(surah).AyahCount;
        return Memorized.Count(i => i >= first && i < last);
    }

    // Where a Program Files install puts the recitations it found in the
    // project's Assets\Recitations (one folder per reciter).
    public static string InstalledRecitationsRoot => Path.Combine(AppContext.BaseDirectory, "Recitations");

    // The first installed reciter folder with any recitations, or null.
    public static string? InstalledReciterFolder()
    {
        try
        {
            if (!Directory.Exists(InstalledRecitationsRoot)) return null;
            return Directory.GetDirectories(InstalledRecitationsRoot)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(d => Directory.EnumerateFiles(d, "??????.mp3").Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The folder chosen in the window, or else the installed recitations.
    public string? EffectiveAudioFolder => AudioFolder ?? InstalledReciterFolder();

    // The recitation file for an ayah, if the folder has a playable one.
    public string? AudioFileFor(int surah, int ayah)
    {
        var folder = EffectiveAudioFolder;
        if (!IsAllowedFolder(folder)) return null;
        foreach (var extension in new[] { ".mp3", ".wav" })
        {
            var path = Path.Combine(folder!, $"{surah:000}{ayah:000}{extension}");
            if (LocalAudioPlayer.IsPlayable(path)) return path;
        }
        return null;
    }

    // A fully qualified folder on a local drive (no network share).
    public static bool IsAllowedFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                   new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                   PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static QuranStore Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new QuranStore();
            var bytes = ReadStoreBytes();
            var loaded = JsonSerializer.Deserialize<QuranStore>(bytes, LoadOptions);
            if (loaded != null) return loaded.Checked();
        }
        catch
        {
            // Unreadable or corrupt: start fresh.
        }
        return new QuranStore();
    }

    private static byte[] ReadStoreBytes()
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaxStoreFileBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxStoreFileBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("Quran progress exceeds the size limit.");
        Array.Resize(ref buffer, count);
        return buffer;
    }

    private QuranStore Checked()
    {
        if (!QuranText.IsValid(Surah, Ayah)) { Surah = 1; Ayah = 1; }
        if (!QuranText.IsValid(Surah, AyahEnd) || AyahEnd < Ayah || AyahEnd - Ayah >= MaxRange) AyahEnd = Ayah;
        if (!IsAllowedFolder(AudioFolder)) AudioFolder = null;
        if (!GapChoices.Contains(GapSeconds)) GapSeconds = 2;
        if (!ReminderChoices.Contains(ReminderMinutes)) ReminderMinutes = 0;
        Memorized = (Memorized ?? new()).Where(i => i is >= 0 and < QuranText.AyahTotal).Distinct().OrderBy(i => i).ToList();
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(this);
            var temp = Path.Combine(FolderPath, "quran." + Path.GetRandomFileName() + ".tmp");
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
            // Best effort: losing a save isn't fatal.
        }
    }
}

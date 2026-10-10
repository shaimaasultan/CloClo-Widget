using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloCloWidget;

// The Sudoku game's saved state: the game in progress, the dailies solved
// (for the streak) and the score history. Kept in its own file beside
// settings.json and written the same way (a fresh temporary file, then
// swapped in). Everything read back is checked - sizes, ranges, lengths -
// since any program running as this user can edit the file; anything that
// doesn't fit is dropped rather than trusted.
public sealed class SudokuGame
{
    public int N { get; set; } = 9;
    public string Level { get; set; } = "easy";
    public string? Daily { get; set; }
    public int[] Puzzle { get; set; } = Array.Empty<int>();
    public int[] Solution { get; set; } = Array.Empty<int>();
    public int[] Grid { get; set; } = Array.Empty<int>();
    public int[] Notes { get; set; } = Array.Empty<int>();
    public int Time { get; set; }
    public int Hints { get; set; }
    public int Mistakes { get; set; }
    public bool Solved { get; set; }
    public bool Recorded { get; set; }
    public int Score { get; set; }
}

public sealed class SudokuRecord
{
    public DateTime Date { get; set; }
    public string? Daily { get; set; }
    public int N { get; set; }
    public string Level { get; set; } = "easy";
    public int Time { get; set; }
    public int Hints { get; set; }
    public int Mistakes { get; set; }
    public int Score { get; set; }
}

public sealed class SudokuStore
{
    private const int MaxStoreFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions LoadOptions = new() { MaxDepth = 16 };

    public SudokuGame? Game { get; set; }
    public List<string> DailySolved { get; set; } = new();
    public List<SudokuRecord> History { get; set; } = new();
    public bool HighlightMistakes { get; set; } = true;
    public int LastSize { get; set; } = 9;
    public string LastLevel { get; set; } = "easy";

    private static readonly int[] Sizes = { 6, 9, 12, 16 };

    private static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloCloWidget");

    private static string FilePath => Path.Combine(FolderPath, "sudoku.json");

    public static SudokuStore Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new SudokuStore();
            var bytes = ReadStoreBytes();
            var loaded = JsonSerializer.Deserialize<SudokuStore>(bytes, LoadOptions);
            if (loaded != null) return loaded.Checked();
        }
        catch
        {
            // Unreadable or corrupt: start fresh.
        }
        return new SudokuStore();
    }

    private static byte[] ReadStoreBytes()
    {
        // Bound the actual read, not only FileInfo.Length: the file could
        // otherwise grow between a size check and ReadAllText.
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
            throw new InvalidDataException("Sudoku save exceeds the size limit.");
        Array.Resize(ref buffer, count);
        return buffer;
    }

    // Keeps only what makes sense.
    private SudokuStore Checked()
    {
        if (Game != null && !IsValid(Game)) Game = null;
        DailySolved = (DailySolved ?? new()).Where(IsDateKey).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        History = (History ?? new())
            .Where(r => r != null && Sizes.Contains(r.N) && SudokuEngine.Levels.Contains(r.Level) && r.Time >= 0 && r.Hints >= 0 && r.Score >= 0)
            .TakeLast(500).ToList();
        if (!Sizes.Contains(LastSize)) LastSize = 9;
        if (!SudokuEngine.Levels.Contains(LastLevel)) LastLevel = "easy";
        return this;
    }

    private static bool IsValid(SudokuGame g)
    {
        if (!Sizes.Contains(g.N) || !SudokuEngine.Levels.Contains(g.Level)) return false;
        if (g.Daily != null && !IsDateKey(g.Daily)) return false;
        var nn = g.N * g.N;
        if (g.Puzzle?.Length != nn || g.Solution?.Length != nn || g.Grid?.Length != nn || g.Notes?.Length != nn) return false;
        var full = (1 << (g.N + 1)) - 2;
        for (var i = 0; i < nn; i++)
        {
            if (g.Solution[i] < 1 || g.Solution[i] > g.N) return false;
            if (g.Puzzle[i] != 0 && g.Puzzle[i] != g.Solution[i]) return false;
            if (g.Grid[i] < 0 || g.Grid[i] > g.N) return false;
            if (g.Puzzle[i] != 0 && g.Grid[i] != g.Puzzle[i]) return false;
            if ((g.Notes[i] & ~full) != 0) return false;
        }
        // The solution itself must be a valid filled grid.
        var geo = new SudokuGeo(g.N);
        if (SudokuEngine.HasConflict(geo, g.Solution)) return false;
        return g.Time >= 0 && g.Hints >= 0 && g.Mistakes >= 0;
    }

    private static bool IsDateKey(string? s) =>
        s != null && DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(this);
            // A fresh, randomly named temporary file (CreateNew: nothing can
            // have been placed there first), then swapped in.
            var temp = Path.Combine(FolderPath, "sudoku." + Path.GetRandomFileName() + ".tmp");
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

    // ------------------------------------------------------------ streak

    public (int Current, int Best, int Total, bool DoneToday) Streak(DateTime today)
    {
        var days = new HashSet<string>(DailySolved);
        var key = SudokuEngine.DateKey(today);
        // Alive until the end of today even if today's isn't done yet.
        var k = days.Contains(key) ? today : today.AddDays(-1);
        var current = 0;
        while (days.Contains(SudokuEngine.DateKey(k))) { current++; k = k.AddDays(-1); }
        int best = 0, run = 0; DateTime? prev = null;
        foreach (var d in DailySolved)
        {
            var date = DateTime.ParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            run = prev != null && prev.Value.AddDays(1) == date ? run + 1 : 1;
            best = Math.Max(best, run); prev = date;
        }
        return (current, best, days.Count, days.Contains(key));
    }
}

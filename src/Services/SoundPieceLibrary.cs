using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloCloWidget;

// One sound of a word (a "k", an "o", an "l"), described by numbers only -
// no audio is kept. Letter is what the person says it is; the rest is
// measured. LevelBelowPeakDb is how much quieter it is than the loudest part
// of its word, so it doesn't depend on how loud the person spoke.
public sealed record SoundPiece(
    string Letter,
    string Type,
    string Token,
    int DurationMs,
    float LevelBelowPeakDb,
    float Pitch,
    float F1,
    float F2,
    float HighShare,
    float LowShare,
    float[] Bands,
    string Word,
    string Speaker,
    int Position,
    DateTime Saved)
{
    internal static SoundPiece FromSegment(SoundSegment segment, SoundUtterance utterance, int position,
        string letter, string word, string speaker)
    {
        var frames = utterance.Frames.Skip(segment.StartFrame).Take(segment.Frames).ToList();
        var peak = utterance.Frames.Count == 0 ? 0 : utterance.Frames.Max(f => f.LevelDb);
        var voiced = frames.Where(f => f.Pitch > 0).ToList();
        var shaped = frames.Where(f => f.F1 > 0 && f.F2 > 0).ToList();
        var banded = frames.Where(f => f.Bands != null).Select(f => f.Bands!).ToList();
        var bands = new float[SoundPatternAnalyzer.BandEdgesHz.Length - 1];
        if (banded.Count > 0)
            for (var b = 0; b < bands.Length; b++)
                bands[b] = MathF.Round(banded.Average(x => x[b]), 1);
        return new SoundPiece(
            letter.Trim(),
            segment.Type.ToString(),
            segment.Token,
            segment.Frames * 10,
            MathF.Round(peak - (frames.Count == 0 ? peak : frames.Average(f => f.LevelDb)), 1),
            voiced.Count == 0 ? 0 : MathF.Round(voiced.Average(f => f.Pitch)),
            shaped.Count == 0 ? 0 : MathF.Round(shaped.Average(f => f.F1)),
            shaped.Count == 0 ? 0 : MathF.Round(shaped.Average(f => f.F2)),
            frames.Count == 0 ? 0 : MathF.Round(frames.Average(f => f.HighShare), 3),
            frames.Count == 0 ? 0 : MathF.Round(frames.Average(f => f.LowShare), 3),
            bands,
            word.Trim(),
            speaker.Trim(),
            position,
            DateTime.Now);
    }

    // A first guess at the letter (by rules); the person corrects it.
    internal static string GuessLetter(SoundSegment segment) => segment.Letter;

    // How alike two fingerprints are: 1 = the same shape, 0 = far apart
    // (an average band difference of 10 dB or more).
    public static double BandSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return Math.Max(0, 1 - sum / a.Length / 10);
    }
}

// Saved sound pieces, in %AppData%\CloCloWidget\sound-pieces.json. These
// include measured values and the user's letter, word, and speaker labels;
// raw audio is never written.
public sealed class SoundPieceLibrary
{
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxPieces = 20000;
    private static readonly JsonSerializerOptions LoadOptions = new() { MaxDepth = 16 };

    public List<SoundPiece> Pieces { get; set; } = new();

    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloCloWidget");

    private static string FilePath => Path.Combine(FolderPath, "sound-pieces.json");

    public static SoundPieceLibrary Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > MaxFileBytes) return new SoundPieceLibrary();
            var loaded = JsonSerializer.Deserialize<SoundPieceLibrary>(File.ReadAllBytes(FilePath), LoadOptions);
            if (loaded?.Pieces != null)
            {
                loaded.Pieces = loaded.Pieces.Where(p => p?.Bands != null && p.Letter != null).Take(MaxPieces).ToList();
                return loaded;
            }
        }
        catch
        {
            // Unreadable or corrupt: start fresh.
        }
        return new SoundPieceLibrary();
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            if (Pieces.Count > MaxPieces) Pieces.RemoveRange(0, Pieces.Count - MaxPieces);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes) return false;
            // A fresh, randomly named temporary file (CreateNew: nothing can
            // have been placed there first), then swapped in.
            var temp = Path.Combine(FolderPath, "sound-pieces." + Path.GetRandomFileName() + ".tmp");
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
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Each letter's saved pieces folded into one average, for comparing.
    public IReadOnlyList<LetterSummary> Summaries() => Pieces
        .Where(p => p.Letter.Length > 0)
        .GroupBy(p => p.Letter, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var bands = new float[g.First().Bands.Length];
            for (var b = 0; b < bands.Length; b++) bands[b] = g.Average(p => b < p.Bands.Length ? p.Bands[b] : 0);
            return new LetterSummary(g.Key, g.Count(), g.Select(p => p.Speaker).Where(s => s.Length > 0).Distinct().Count(),
                g.GroupBy(p => p.Token).OrderByDescending(t => t.Count()).First().Key,
                (int)g.Average(p => p.DurationMs), (float)g.Average(p => p.LevelBelowPeakDb), bands);
        })
        .OrderBy(s => s.Letter, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public sealed record LetterSummary(string Letter, int Count, int Speakers, string UsualToken, int DurationMs,
        float LevelBelowPeakDb, float[] Bands);
}

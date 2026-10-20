using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CloCloWidget;

// How Whisper spells this speaker's English. Learned by reading a known
// passage aloud: the words Whisper returns are lined up with the passage,
// and inside each misheard word every place the letters differ ("p" heard as
// "b") is counted against how often that spelling appeared in the passage.
// Those counts predict how Whisper will misspell words that were never
// recorded, so a word list can be corrected in dictation without saying each
// word. Only the counts and the word list are saved - never audio, and never
// the passage or what Whisper heard.
public sealed class AccentRule
{
    public string Expected { get; set; } = "";
    public string Heard { get; set; } = "";
    public int Count { get; set; }
}

// A whole word Whisper wrote as another word ("their" as "there").
public sealed class AccentSwap
{
    public string Expected { get; set; } = "";
    public string Heard { get; set; } = "";
    public int Count { get; set; }
}

public sealed record AccentSpelling(string Text, double Probability);

public sealed record AccentTake(int PassageWords, int HeardWords, int Misheard,
    IReadOnlyList<(string Expected, string Heard)> Differences);

public sealed class AccentProfile
{
    // Letter differences are taken only from short spans; anything longer is
    // a different word, not a pronunciation.
    public const int MaxSpan = 3;
    private const int MaxRules = 2000;
    private const int MaxSwaps = 500;
    private const int MaxWordsText = 4000;
    private const int MaxPassageChars = 4000;
    private const int MaxStoreFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions LoadOptions = new() { MaxDepth = 16 };

    public const string DefaultWords =
        "cloclo, notepad, spotify, youtube, calculator, weather, calendar, volume up, volume down, open, close, search, music, settings, screenshot";

    public List<AccentRule> Rules { get; set; } = new();
    // How many times each Expected spelling appeared in the passages read.
    public Dictionary<string, int> Seen { get; set; } = new();
    public List<AccentSwap> Swaps { get; set; } = new();
    public string Words { get; set; } = DefaultWords;
    public int Takes { get; set; }

    public double Probability(AccentRule rule) =>
        Seen.TryGetValue(rule.Expected, out var seen) && seen > 0 ? Math.Min(1, (double)rule.Count / seen) : 0;

    public void Reset()
    {
        Rules.Clear();
        Seen.Clear();
        Swaps.Clear();
        Takes = 0;
    }

    // ------------------------------------------------------------ learning

    public AccentTake Learn(string passage, string heard)
    {
        var expected = Tokens(Truncate(passage));
        var got = Tokens(Truncate(heard));
        var differences = new List<(string, string)>();
        var newRules = new List<(string Expected, string Heard)>();
        var misheard = 0;

        foreach (var (e, h) in AlignWords(expected, got))
        {
            if (e == null || h == null) { misheard++; continue; }
            if (e == h) continue;
            misheard++;
            differences.Add((e, h));
            AddSwap(e, h);
            // Only similar words say something about pronunciation; a
            // completely different word is a mishearing of the whole word.
            if (Distance(e, h) > Math.Max(e.Length, h.Length) * 0.6) continue;
            if (e.Any(char.IsDigit) || h.Any(char.IsDigit)) continue;
            newRules.AddRange(LetterDifferences(e, h));
        }

        foreach (var (e, h) in newRules)
        {
            var rule = Rules.FirstOrDefault(r => r.Expected == e && r.Heard == h);
            if (rule == null)
            {
                if (Rules.Count >= MaxRules) continue;
                Rules.Add(rule = new AccentRule { Expected = e, Heard = h });
            }
            rule.Count++;
        }

        // Every rule's spelling is counted in this passage, including rules
        // from earlier takes, so each rule's rate stays "heard wrong / said".
        var text = " " + string.Join(" ", expected) + " ";
        foreach (var spelling in Rules.Select(r => r.Expected).Distinct())
        {
            var count = Occurrences(text, spelling);
            if (count > 0) Seen[spelling] = Seen.GetValueOrDefault(spelling) + count;
        }
        Takes++;
        return new AccentTake(expected.Count, got.Count, misheard, differences);
    }

    private void AddSwap(string expected, string heard)
    {
        var swap = Swaps.FirstOrDefault(s => s.Expected == expected && s.Heard == heard);
        if (swap == null)
        {
            if (Swaps.Count >= MaxSwaps) return;
            Swaps.Add(swap = new AccentSwap { Expected = expected, Heard = heard });
        }
        swap.Count++;
    }

    // The spans where two similar words differ, each with enough of the word
    // around it to place it: an inserted letter carries its neighbour.
    private static IEnumerable<(string Expected, string Heard)> LetterDifferences(string expected, string heard)
    {
        var ops = AlignLetters(expected, heard);
        var i = 0;
        var refIndex = 0;
        while (i < ops.Count)
        {
            if (ops[i].E != null && ops[i].E == ops[i].H) { refIndex++; i++; continue; }
            var start = refIndex;
            var e = new StringBuilder();
            var h = new StringBuilder();
            while (i < ops.Count && !(ops[i].E != null && ops[i].E == ops[i].H))
            {
                if (ops[i].E is char ec) { e.Append(ec); refIndex++; }
                if (ops[i].H is char hc) h.Append(hc);
                i++;
            }
            if (e.Length > MaxSpan || h.Length > MaxSpan) continue;
            if (e.Length == 0)
            {
                // Inserted letters: anchor on the letter before, else after.
                if (start > 0) yield return (expected[start - 1].ToString(), expected[start - 1] + h.ToString());
                else if (start < expected.Length) yield return (expected[start].ToString(), h + expected[start].ToString());
                continue;
            }
            yield return (e.ToString(), h.ToString());
        }
    }

    // ------------------------------------------------------------ predicting

    // The ways Whisper is likely to write a word or phrase for this speaker,
    // most likely first, by applying up to two learned differences.
    public IReadOnlyList<AccentSpelling> Spellings(string phrase, int maximum = 8, double minimumRate = 0.1)
    {
        var key = Key(phrase);
        if (key.Length == 0) return Array.Empty<AccentSpelling>();
        var rules = Rules.Select(r => (Rule: r, P: Probability(r)))
            .Where(r => r.P >= minimumRate).OrderByDescending(r => r.P).Take(200).ToList();
        var best = new Dictionary<string, double> { [key] = 1 };
        var frontier = new List<(string Text, double P)> { (key, 1) };
        for (var depth = 0; depth < 2 && frontier.Count > 0; depth++)
        {
            var next = new Dictionary<string, double>();
            foreach (var (text, p) in frontier)
            {
                foreach (var (rule, rate) in rules)
                {
                    for (var at = text.IndexOf(rule.Expected, StringComparison.Ordinal); at >= 0;
                         at = text.IndexOf(rule.Expected, at + 1, StringComparison.Ordinal))
                    {
                        var changed = text[..at] + rule.Heard + text[(at + rule.Expected.Length)..];
                        if (changed.Length == 0) continue;
                        var q = p * rate;
                        if (q > best.GetValueOrDefault(changed)) { best[changed] = q; next[changed] = q; }
                    }
                }
            }
            frontier = next.OrderByDescending(kv => kv.Value).Take(24).Select(kv => (kv.Key, kv.Value)).ToList();
        }
        return best.Where(kv => kv.Key != key).OrderByDescending(kv => kv.Value).Take(maximum)
            .Select(kv => new AccentSpelling(kv.Key, kv.Value)).ToList();
    }

    // Cost of reading heard as expected: a learned single-letter difference
    // costs less the more often it happens; any other edit costs 1.
    internal double WeightedDistance(string heard, string expected)
    {
        var rates = Rules.Where(r => r.Expected.Length == 1 && r.Heard.Length == 1)
            .ToDictionary(r => (r.Expected[0], r.Heard[0]), Probability);
        var previous = new double[expected.Length + 1];
        var current = new double[expected.Length + 1];
        for (var j = 0; j <= expected.Length; j++) previous[j] = j;
        for (var i = 1; i <= heard.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= expected.Length; j++)
            {
                var substitute = heard[i - 1] == expected[j - 1] ? 0
                    : rates.TryGetValue((expected[j - 1], heard[i - 1]), out var p) ? Math.Max(0.25, 1 - p) : 1;
                current[j] = Math.Min(previous[j - 1] + substitute, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[expected.Length];
    }

    // ------------------------------------------------------------ text helpers

    // Lowercase words of letters, digits and apostrophes.
    public static List<string> Tokens(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c) || c == '\'') word.Append(c);
            else if (word.Length > 0) { words.Add(word.ToString().Trim('\'')); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString().Trim('\''));
        words.RemoveAll(w => w.Length == 0);
        return words;
    }

    // A word or phrase as one spaceless key, so "note pad" meets "notepad".
    public static string Key(string phrase) => string.Concat(Tokens(phrase));

    public static List<string> WordList(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => Key(w).Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();

    private static string Truncate(string text) => text.Length > MaxPassageChars ? text[..MaxPassageChars] : text;

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    internal static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1), Math.Min(previous[j] + 1, current[j - 1] + 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    // Lines up two word sequences. Swapping a word costs how different it
    // is (0 to 1), so a misheard word pairs with the word that was said
    // rather than counting as one word dropped and another added.
    private static List<(string? Expected, string? Heard)> AlignWords(List<string> expected, List<string> heard)
    {
        int n = expected.Count, m = heard.Count;
        var cost = new double[n + 1, m + 1];
        for (var i = 0; i <= n; i++) cost[i, 0] = i;
        for (var j = 0; j <= m; j++) cost[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var e = expected[i - 1];
                var h = heard[j - 1];
                var swap = e == h ? 0 : (double)Distance(e, h) / Math.Max(e.Length, h.Length);
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + swap, Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1));
            }
        var pairs = new List<(string?, string?)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0)
            {
                var e = expected[i - 1];
                var h = heard[j - 1];
                var swap = e == h ? 0 : (double)Distance(e, h) / Math.Max(e.Length, h.Length);
                if (Math.Abs(cost[i, j] - (cost[i - 1, j - 1] + swap)) < 1e-9) { pairs.Add((e, h)); i--; j--; continue; }
            }
            if (i > 0 && Math.Abs(cost[i, j] - (cost[i - 1, j] + 1)) < 1e-9) { pairs.Add((expected[i - 1], null)); i--; }
            else { pairs.Add((null, heard[j - 1])); j--; }
        }
        pairs.Reverse();
        return pairs;
    }

    private static List<(char? E, char? H)> AlignLetters(string expected, string heard)
    {
        int n = expected.Length, m = heard.Length;
        var cost = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) cost[i, 0] = i;
        for (var j = 0; j <= m; j++) cost[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + (expected[i - 1] == heard[j - 1] ? 0 : 1),
                    Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1));
        var ops = new List<(char?, char?)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && cost[i, j] == cost[i - 1, j - 1] + (expected[i - 1] == heard[j - 1] ? 0 : 1))
            { ops.Add((expected[i - 1], heard[j - 1])); i--; j--; }
            else if (i > 0 && cost[i, j] == cost[i - 1, j] + 1) { ops.Add((expected[i - 1], null)); i--; }
            else { ops.Add((null, heard[j - 1])); j--; }
        }
        ops.Reverse();
        return ops;
    }

    // ------------------------------------------------------------ storage

    private static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloCloWidget");

    private static string FilePath => Path.Combine(FolderPath, "accent-patterns.json");

    // Any program running as this user can edit the file, so everything read
    // back is bounded and checked; anything that doesn't fit is dropped.
    public static AccentProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
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
                if (count <= MaxStoreFileBytes)
                {
                    var loaded = JsonSerializer.Deserialize<AccentProfile>(buffer.AsSpan(0, count), LoadOptions);
                    if (loaded != null) return loaded.Validated();
                }
            }
        }
        catch
        {
            // Corrupt or unreadable: start with an empty profile.
        }
        return new AccentProfile();
    }

    private static bool IsSpelling(string? text, bool allowEmpty) =>
        text != null && text.Length <= MaxSpan + 1 && (allowEmpty || text.Length > 0) &&
        text.All(c => char.IsLetterOrDigit(c) || c == '\'');

    private static bool IsWord(string? text) =>
        text != null && text.Length is > 0 and <= 40 && text.All(c => char.IsLetterOrDigit(c) || c == '\'');

    private AccentProfile Validated()
    {
        Rules = (Rules ?? new()).Where(r => r != null && IsSpelling(r.Expected, false) && IsSpelling(r.Heard, true) && r.Count is > 0 and < 1_000_000)
            .Take(MaxRules).ToList();
        Seen = (Seen ?? new()).Where(kv => IsSpelling(kv.Key, false) && kv.Value is > 0 and < 10_000_000)
            .Take(MaxRules).ToDictionary(kv => kv.Key, kv => kv.Value);
        Swaps = (Swaps ?? new()).Where(s => s != null && IsWord(s.Expected) && IsWord(s.Heard) && s.Count is > 0 and < 1_000_000)
            .Take(MaxSwaps).ToList();
        Words = Words is { Length: <= MaxWordsText } ? Words : DefaultWords;
        Takes = Math.Clamp(Takes, 0, 100_000);
        return this;
    }

    public void Save()
    {
        try
        {
            if (Words.Length > MaxWordsText) Words = Words[..MaxWordsText];
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            // Written beside and then swapped in, under a fresh random name
            // that must not exist yet, as settings.json is.
            var temp = Path.Combine(FolderPath, "accent-patterns." + Path.GetRandomFileName() + ".tmp");
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

// Corrects dictated text toward a word list, using the profile's predicted
// spellings first and its weighted letter distance second. Phrases of up to
// three dictated words are joined, so "note bad" can become "Notepad".
public sealed class AccentCorrector
{
    private const int MaxJoin = 3;
    private readonly AccentProfile _profile;
    private readonly List<(string Key, string Display)> _entries;
    private readonly Dictionary<string, (string Display, double P)> _spellings = new();

    public AccentCorrector(AccentProfile profile, IEnumerable<string> words)
    {
        _profile = profile;
        _entries = words.Select(w => (AccentProfile.Key(w), w)).Where(e => e.Item1.Length > 0).ToList();
        foreach (var (key, display) in _entries) _spellings[key] = (display, 1);
        // A list word already heard wrong in a passage maps back directly.
        foreach (var swap in profile.Swaps)
        {
            var entry = _entries.FirstOrDefault(e => e.Key == swap.Expected);
            if (entry.Display != null) _spellings.TryAdd(swap.Heard, (entry.Display, 0.99));
        }
        foreach (var (key, display) in _entries)
            foreach (var spelling in profile.Spellings(display))
                if (!_spellings.TryGetValue(spelling.Text, out var known) || known.P < spelling.Probability)
                    _spellings[spelling.Text] = (display, spelling.Probability);
    }

    public (string Text, IReadOnlyList<string> Changes) Correct(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keys = parts.Select(AccentProfile.Key).ToArray();
        var output = new List<string>();
        var changes = new List<string>();
        for (var i = 0; i < parts.Length;)
        {
            var matched = false;
            for (var n = Math.Min(MaxJoin, parts.Length - i); n >= 1 && !matched; n--)
            {
                var span = parts.Skip(i).Take(n).ToArray();
                var key = string.Concat(keys.Skip(i).Take(n));
                if (key.Length == 0) continue;
                var display = Find(key, n, out var how);
                if (display == null) continue;
                var spoken = string.Join(" ", span);
                var trailing = new string(span[^1].Reverse().TakeWhile(c => !char.IsLetterOrDigit(c)).Reverse().ToArray());
                var leading = new string(span[0].TakeWhile(c => !char.IsLetterOrDigit(c)).ToArray());
                var replacement = leading + display + trailing;
                if (!string.Equals(AccentProfile.Key(spoken), AccentProfile.Key(display), StringComparison.Ordinal))
                    changes.Add($"{spoken} → {display}  ({how})");
                output.Add(replacement);
                i += n;
                matched = true;
            }
            if (!matched) { output.Add(parts[i]); i++; }
        }
        return (string.Join(" ", output), changes);
    }

    private string? Find(string key, int joined, out string how)
    {
        how = "";
        if (_spellings.TryGetValue(key, out var exact))
        {
            how = exact.P >= 1 ? "already right" : $"predicted spelling, {exact.P:P0}";
            return exact.Display;
        }
        // Near matches only for longer words, and only within what learned
        // differences explain: one plain edit is never enough for a short
        // word ("chose" must not become "close").
        string? best = null;
        var bestCost = double.MaxValue;
        foreach (var (entryKey, display) in _entries)
        {
            if (entryKey.Length < 5 || Math.Abs(entryKey.Length - key.Length) > 2) continue;
            if (joined > 1 && entryKey.Length < key.Length - 1) continue;
            var allowed = entryKey.Length >= 8 ? 1.6 : 0.8;
            var cost = _profile.WeightedDistance(key, entryKey);
            if (cost <= allowed && cost < bestCost) { bestCost = cost; best = display; }
        }
        if (best != null) how = $"close to it, cost {bestCost:0.##}";
        return best;
    }
}

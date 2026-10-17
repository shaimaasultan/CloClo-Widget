using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace CloCloWidget;

// The file summary and search behind FileInsightsWindow. Everything is read
// locally and in memory: files are only opened for reading, nothing found in
// them is run, and nothing is saved or sent. Only local fixed or removable
// drives are accepted (no network shares), hidden/system files and folder
// links (junctions, symlinks) are skipped, and every limit below is fixed so a
// huge folder or a hostile file can't exhaust memory.
public static class FileInsights
{
    public const int MaxFiles = 200_000;
    public const int MaxMatches = 5_000;
    // Larger text files are listed in the summary but not read.
    public const long MaxTextBytes = 32L * 1024 * 1024;
    public const long MaxDocxBytes = 64L * 1024 * 1024;
    // The uncompressed word/document.xml a .docx may expand to.
    private const long MaxDocxXmlBytes = 128L * 1024 * 1024;
    private const int ContextChars = 70;
    // A line longer than this (minified code, one-line JSON) is read in
    // pieces of this size, so one line can't fill memory. Consecutive pieces
    // share PieceOverlap characters, so a phrase that spans the cut is still
    // found, once.
    private const int MaxPieceChars = 64 * 1024;
    private const int PieceOverlap = 1024;

    // Read as plain text. Plain-text formats only: no binary or archive types,
    // and only extensions here are opened at all.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config", ".properties", ".env",
        ".html", ".htm", ".css", ".scss", ".less", ".svg", ".srt", ".vtt", ".tex", ".bib",
        ".cs", ".xaml", ".csproj", ".sln", ".props", ".targets", ".vb", ".fs", ".py", ".java", ".kt",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".c", ".h", ".cpp", ".hpp", ".cc",
        ".go", ".rs", ".rb", ".php", ".swift", ".dart", ".lua", ".r", ".sql", ".sh", ".ps1", ".psm1",
        ".psd1", ".bat", ".cmd", ".gitignore", ".gitattributes", ".editorconfig",
    };

    public static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path));
    public static bool IsDocx(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);
    public static bool IsReadable(string path) => IsTextFile(path) || IsDocx(path);

    public sealed record FileEntry(string Path, string Extension, long Size, DateTime Modified);

    public sealed record Scope(IReadOnlyList<FileEntry> Files, bool Truncated, int SkippedEntries);

    public sealed record TypeRow(string Type, int Count, long Size);

    public sealed record TextCounts(int FilesRead, int FilesSkipped, long Lines, long Words);

    public sealed record Match(string Path, int Line, string Before, string Hit, string After, bool IsDocx);

    public sealed record SearchProgress(int FilesDone, int FilesTotal, int Matches);

    // The same local-only rule LocalAudioPlayer uses: fully qualified,
    // canonical, not UNC and not on a network drive.
    public static bool IsAllowedLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                   new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Network or DriveType.NoRootDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                   PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Scope ListFolder(string folder, bool includeSubfolders, CancellationToken token)
    {
        var files = new List<FileEntry>();
        var skipped = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            token.ThrowIfCancellationRequested();
            if (files.Count >= MaxFiles) return new Scope(files, true, skipped);
            var entry = TryEntry(info);
            if (entry is null) skipped++;
            else files.Add(entry);
        }
        return new Scope(files, false, skipped);
    }

    public static Scope ListFiles(IEnumerable<string> paths)
    {
        var files = new List<FileEntry>();
        var skipped = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (files.Count >= MaxFiles) return new Scope(files, true, skipped);
            var entry = IsAllowedLocalPath(path) ? TryEntry(new FileInfo(path)) : null;
            if (entry is null) skipped++;
            else files.Add(entry);
        }
        return new Scope(files, false, skipped);
    }

    private static FileEntry? TryEntry(FileInfo info)
    {
        try
        {
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return new FileEntry(info.FullName, info.Extension.ToLowerInvariant(), info.Length, info.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<TypeRow> ByType(IReadOnlyList<FileEntry> files) =>
        files.GroupBy(f => f.Extension.Length == 0 ? "(no extension)" : f.Extension)
             .Select(g => new TypeRow(g.Key, g.Count(), g.Sum(f => f.Size)))
             .OrderByDescending(r => r.Size).ThenByDescending(r => r.Count)
             .ToList();

    // Line and word counts for the plain-text and .docx files in the scope.
    public static TextCounts CountText(IReadOnlyList<FileEntry> files, IProgress<int>? progress, CancellationToken token)
    {
        long lines = 0, words = 0;
        int read = 0, skipped = 0, done = 0;
        var reports = new ReportThrottle();
        var readable = files.Where(f => IsReadable(f.Path)).ToList();
        Parallel.ForEach(readable, Parallelism(token), file =>
        {
            var counts = TryCount(file);
            if (counts is { } c)
            {
                Interlocked.Add(ref lines, c.Lines);
                Interlocked.Add(ref words, c.Words);
                Interlocked.Increment(ref read);
            }
            else Interlocked.Increment(ref skipped);
            var now = Interlocked.Increment(ref done);
            if (progress != null && reports.Due()) progress.Report(now);
        });
        progress?.Report(readable.Count);
        return new TextCounts(read, skipped, lines, words);
    }

    private static (long Lines, long Words)? TryCount(FileEntry file)
    {
        try
        {
            long lines = 0, words = 0;
            var inWord = false;
            foreach (var piece in ReadLines(file))
            {
                // A new line starts outside a word; a later piece of the same
                // line carries on from where the previous one ended.
                if (piece.Line != lines) inWord = false;
                lines = piece.Line;
                for (var i = piece.Overlap; i < piece.Text.Length; i++)
                {
                    var letter = !char.IsWhiteSpace(piece.Text[i]);
                    if (letter && !inWord) words++;
                    inWord = letter;
                }
            }
            return (lines, words);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    // Builds a case-insensitive pattern for the whole phrase: the words in
    // order, any run of spaces between them matching any run of whitespace.
    // With wholeWords, the phrase must not start or end inside a longer word.
    public static Regex? BuildPhrasePattern(string phrase, bool wholeWords)
    {
        var parts = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var body = string.Join(@"\s+", parts.Select(Regex.Escape));
        if (wholeWords) body = $@"(?<![\p{{L}}\p{{N}}_]){body}(?![\p{{L}}\p{{N}}_])";
        return new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    // Searches the readable files, a few at a time. Matches are handed to
    // onMatch as they are found (from worker threads); the search stops after
    // MaxMatches. Returns whether it stopped because of that limit.
    public static bool Search(IReadOnlyList<FileEntry> files, Regex pattern, Action<Match> onMatch,
                              IProgress<SearchProgress>? progress, CancellationToken token)
    {
        var readable = files.Where(f => IsReadable(f.Path)).ToList();
        int done = 0, matches = 0;
        var reports = new ReportThrottle();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            Parallel.ForEach(readable, Parallelism(limit.Token), (file, state) =>
            {
                try
                {
                    foreach (var piece in ReadLines(file))
                    {
                        if (limit.IsCancellationRequested) { state.Stop(); return; }
                        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(piece.Text))
                        {
                            // Already reported from the previous piece.
                            if (m.Index + m.Length <= piece.Overlap) continue;
                            if (Interlocked.Increment(ref matches) > MaxMatches)
                            {
                                limit.Cancel();
                                return;
                            }
                            onMatch(MakeMatch(file, piece.Line, piece.Text, m));
                        }
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex) || ex is RegexMatchTimeoutException) { }
                var now = Interlocked.Increment(ref done);
                if (progress != null && reports.Due()) progress.Report(new SearchProgress(now, readable.Count, Math.Min(matches, MaxMatches)));
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        progress?.Report(new SearchProgress(readable.Count, readable.Count, Math.Min(matches, MaxMatches)));
        return matches > MaxMatches;
    }

    private static Match MakeMatch(FileEntry file, int line, string text, System.Text.RegularExpressions.Match m)
    {
        var beforeStart = Math.Max(0, m.Index - ContextChars);
        var afterEnd = Math.Min(text.Length, m.Index + m.Length + ContextChars);
        var before = Flatten(text[beforeStart..m.Index]).TrimStart();
        var after = Flatten(text[(m.Index + m.Length)..afterEnd]).TrimEnd();
        if (beforeStart > 0) before = "…" + before;
        if (afterEnd < text.Length) after += "…";
        var hit = m.Length > 200 ? Flatten(m.Value[..200]) + "…" : Flatten(m.Value);
        return new Match(file.Path, line, before, hit, after, IsDocx(file.Path));
    }

    private static string Flatten(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // At most about ten progress reports a second, whichever thread asks:
    // each one is a message to the window, and a folder of tiny files would
    // otherwise send thousands.
    private sealed class ReportThrottle
    {
        private long _next;

        public bool Due()
        {
            var now = Environment.TickCount64;
            var next = Interlocked.Read(ref _next);
            return now >= next && Interlocked.CompareExchange(ref _next, now + 100, next) == next;
        }
    }

    private static ParallelOptions Parallelism(CancellationToken token) =>
        new() { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or
              NotSupportedException or ArgumentException or DecoderFallbackException;

    // Text with its 1-based line (or .docx paragraph) number. Its first
    // Overlap characters repeat the end of the previous piece of a long line.
    private readonly record struct Piece(int Line, string Text, int Overlap);

    // Lines of a plain-text file, or paragraphs of a .docx, in pieces of at
    // most MaxPieceChars. A text file that turns out to be binary (NUL
    // characters near the start) throws.
    private static IEnumerable<Piece> ReadLines(FileEntry file)
    {
        if (IsDocx(file.Path))
        {
            if (file.Size > MaxDocxBytes) throw new InvalidDataException("Too large");
            return SplitParagraphs(ReadDocxParagraphs(file.Path));
        }
        if (file.Size > MaxTextBytes) throw new InvalidDataException("Too large");
        return ReadTextLines(file.Path);
    }

    private static IEnumerable<Piece> SplitParagraphs(IEnumerable<string> paragraphs)
    {
        var number = 0;
        foreach (var paragraph in paragraphs)
        {
            number++;
            for (var start = 0; ; start += MaxPieceChars - PieceOverlap)
            {
                var end = Math.Min(paragraph.Length, start + MaxPieceChars);
                yield return new Piece(number, paragraph[start..end], start == 0 ? 0 : PieceOverlap);
                if (end == paragraph.Length) break;
            }
        }
    }

    // Like StreamReader.ReadLine (LF, CR or CRLF end a line), but a long line
    // comes out in overlapping pieces instead of as one huge string.
    private static IEnumerable<Piece> ReadTextLines(string path)
    {
        const char Lf = (char)10, Cr = (char)13;
        if (LooksBinary(path)) throw new InvalidDataException("Binary");
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[16 * 1024];
        var line = new StringBuilder();
        var number = 1;
        var overlap = 0;
        var afterCr = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (afterCr)
                {
                    afterCr = false;
                    if (ch == Lf) continue;
                }
                if (ch is Lf or Cr)
                {
                    yield return new Piece(number++, line.ToString(), overlap);
                    line.Clear();
                    overlap = 0;
                    afterCr = ch == Cr;
                    continue;
                }
                line.Append(ch);
                if (line.Length >= MaxPieceChars)
                {
                    yield return new Piece(number, line.ToString(), overlap);
                    line.Remove(0, line.Length - PieceOverlap);
                    overlap = PieceOverlap;
                }
            }
        }
        // As with ReadLine, text after the last line break is a line, but a
        // final line break doesn't start an empty one.
        if (line.Length > overlap) yield return new Piece(number, line.ToString(), overlap);
    }

    private static bool LooksBinary(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var head = new char[4096];
        var length = reader.ReadBlock(head, 0, head.Length);
        return Array.IndexOf(head, (char)0, 0, length) >= 0;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

    // A .docx is a zip; its body text is word/document.xml. Each paragraph
    // (w:p) becomes one "line"; tabs and line breaks inside it become spaces.
    // DTDs are refused and the expanded XML size is checked first.
    private static IEnumerable<string> ReadDocxParagraphs(string path)
    {
        using var stream = OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document");
        if (entry.Length > MaxDocxXmlBytes) throw new InvalidDataException("Too large");
        using var body = entry.Open();
        using var xml = XmlReader.Create(body, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraph = new StringBuilder();
        var inParagraph = false;
        xml.Read();
        while (!xml.EOF)
        {
            if (xml.NamespaceURI == W && xml.NodeType == XmlNodeType.Element)
            {
                if (xml.LocalName == "t" && inParagraph && !xml.IsEmptyElement)
                {
                    // Moves on to the node after </w:t> by itself.
                    paragraph.Append(xml.ReadElementContentAsString());
                    continue;
                }
                if (xml.LocalName == "p")
                {
                    // A paragraph inside another (a text box) ends the outer one here.
                    if (inParagraph) yield return paragraph.ToString();
                    paragraph.Clear();
                    inParagraph = !xml.IsEmptyElement;
                    if (!inParagraph) yield return string.Empty;
                }
                else if (inParagraph && xml.LocalName is "tab" or "br" or "cr") paragraph.Append(' ');
            }
            else if (xml.NamespaceURI == W && xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p" && inParagraph)
            {
                inParagraph = false;
                yield return paragraph.ToString();
                paragraph.Clear();
            }
            xml.Read();
        }
    }
}

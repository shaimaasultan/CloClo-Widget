using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CloCloWidget;

// Summarises a chosen folder or set of files, and searches them for a word or
// sentence (FileInsights does the reading). The choice isn't remembered:
// closing the window cancels any work and forgets the files and results.
public partial class FileInsightsWindow : Window
{
    private const int TopFiles = 10;

    private ObservableCollection<ResultRow> _results = new();
    private readonly ConcurrentQueue<FileInsights.Match> _pendingMatches = new();
    private readonly DispatcherTimer _resultTimer;
    private string? _folder;
    private IReadOnlyList<string>? _chosenFiles;
    private FileInsights.Scope? _scope;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchCts;
    private string _scanStatus = "";
    private string _searchStatus = "";
    // The search's own result line, before any "showing N" note.
    private string _searchSummary = "";
    private string? _typeFilter;
    private bool _closed;

    public FileInsightsWindow()
    {
        InitializeComponent();
        BindResults();
        RefreshTypeChoices();
        // Matches arrive from worker threads; they are added to the list in
        // batches so a search with thousands of them keeps the window smooth.
        _resultTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _resultTimer.Tick += (_, _) => DrainMatches();
        UpdateButtons();
        Closed += (_, _) =>
        {
            _closed = true;
            _scanCts?.Cancel();
            _searchCts?.Cancel();
            _resultTimer.Stop();
            _pendingMatches.Clear();
            _results.Clear();
            _scope = null;
            _chosenFiles = null;
            TypeList.ItemsSource = LargestList.ItemsSource = NewestList.ItemsSource = null;
        };
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder to summarise and search", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (!FileInsights.IsAllowedLocalPath(picker.FolderName) || !Directory.Exists(picker.FolderName))
        {
            SetScanStatus("Choose a folder on this PC; network folders aren't read.");
            return;
        }
        _folder = picker.FolderName;
        _chosenFiles = null;
        _ = LoadScopeAsync();
    }

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose files to summarise and search",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (picker.ShowDialog(this) != true || picker.FileNames.Length == 0) return;
        _folder = null;
        _chosenFiles = picker.FileNames;
        _ = LoadScopeAsync();
    }

    private void Subfolders_Click(object sender, RoutedEventArgs e)
    {
        if (_folder != null) _ = LoadScopeAsync();
    }

    // Lists the files, shows the summary straight away, then reads the text
    // files for line and word counts.
    private async Task LoadScopeAsync()
    {
        CancelSearch();
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var token = cts.Token;
        ClearResults("");
        ClearSummary();
        var folder = _folder;
        var chosen = _chosenFiles;
        var subfolders = SubfoldersCheck.IsChecked == true;
        ScopeText.Text = folder != null
            ? $"Folder: {folder}{(subfolders ? " (with subfolders)" : " (this folder only)")}"
            : $"{chosen!.Count:N0} chosen file{(chosen.Count == 1 ? "" : "s")}";
        ScopeText.ToolTip = folder ?? string.Join(Environment.NewLine, chosen!.Take(30));
        SetScanStatus("Listing files…");
        UpdateButtons();
        try
        {
            var scope = await Task.Run(() => folder != null
                ? FileInsights.ListFolder(folder, subfolders, token)
                : FileInsights.ListFiles(chosen!), token);
            if (_closed || token.IsCancellationRequested) return;
            _scope = scope;
            ShowSummary(scope);
            UpdateButtons();

            var readable = scope.Files.Count(f => FileInsights.IsReadable(f.Path));
            if (readable == 0)
            {
                SetScanStatus("");
                LinesText.Text = WordsText.Text = "0";
                TextFilesText.Text = "0";
                return;
            }
            SetScanStatus($"Counting lines and words in {readable:N0} text file{(readable == 1 ? "" : "s")}…");
            var progress = new Progress<int>(done =>
            {
                if (!_closed && ReferenceEquals(_scanCts, cts)) ShowProgress(done, readable);
            });
            var counts = await Task.Run(() => FileInsights.CountText(scope.Files, progress, token), token);
            if (_closed || token.IsCancellationRequested) return;
            TextFilesText.Text = counts.FilesRead.ToString("N0");
            LinesText.Text = counts.Lines.ToString("N0");
            WordsText.Text = counts.Words.ToString("N0");
            if (counts.FilesSkipped > 0)
                SummaryNote.Text += $" {counts.FilesSkipped:N0} text file{(counts.FilesSkipped == 1 ? " was" : "s were")} skipped (binary, too large, locked or unreadable).";
            SetScanStatus("");
        }
        catch (OperationCanceledException)
        {
            if (!_closed && ReferenceEquals(_scanCts, cts))
            {
                // Once listed, the tiles themselves say counting stopped.
                if (LinesText.Text == "…") LinesText.Text = WordsText.Text = TextFilesText.Text = "stopped";
                SetScanStatus(_scope == null ? "Listing cancelled." : "");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_closed) SetScanStatus("That folder couldn't be read.");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
            if (!_closed) UpdateButtons();
        }
    }

    private void ClearSummary()
    {
        _scope = null;
        FileCountText.Text = TotalSizeText.Text = "–";
        TextFilesText.Text = LinesText.Text = WordsText.Text = "–";
        SummaryNote.Text = "";
        TypeList.ItemsSource = LargestList.ItemsSource = NewestList.ItemsSource = null;
    }

    private void ShowSummary(FileInsights.Scope scope)
    {
        var files = scope.Files;
        var total = files.Sum(f => f.Size);
        FileCountText.Text = files.Count.ToString("N0");
        TotalSizeText.Text = FormatBytes(total);
        TextFilesText.Text = LinesText.Text = WordsText.Text = "…";
        TypeList.ItemsSource = FileInsights.ByType(files)
            .Select(r => new TypeRowView(r.Type, r.Count, FormatBytes(r.Size),
                total == 0 ? $"{100.0 * r.Count / Math.Max(1, files.Count):0.#}%" : $"{100.0 * r.Size / total:0.#}%"))
            .ToList();
        LargestList.ItemsSource = files.OrderByDescending(f => f.Size).Take(TopFiles).Select(FileRowView.From).ToList();
        NewestList.ItemsSource = files.OrderByDescending(f => f.Modified).Take(TopFiles).Select(FileRowView.From).ToList();

        var notes = new List<string>();
        if (scope.Truncated) notes.Add($"Stopped listing at {FileInsights.MaxFiles:N0} files.");
        if (scope.SkippedEntries > 0) notes.Add($"{scope.SkippedEntries:N0} file{(scope.SkippedEntries == 1 ? "" : "s")} couldn't be read and {(scope.SkippedEntries == 1 ? "is" : "are")} left out.");
        if (_folder != null) notes.Add("Hidden and system files and folder links are skipped.");
        SummaryNote.Text = string.Join(" ", notes);
    }

    private void PhraseBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateButtons();

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var scope = _scope;
        if (scope == null) return;
        var pattern = FileInsights.BuildPhrasePattern(PhraseBox.Text, WholeWordsCheck.IsChecked == true);
        if (pattern == null) return;
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        var token = cts.Token;
        ClearResults("");
        var readable = scope.Files.Count(f => FileInsights.IsReadable(f.Path));
        if (readable == 0)
        {
            _searchCts = null;
            cts.Dispose();
            SearchStatus.Text = "None of these files are text or Word documents, so there is nothing to search.";
            return;
        }
        SetSearchStatus($"Searching {readable:N0} file{(readable == 1 ? "" : "s")}…");
        UpdateButtons();
        _resultTimer.Start();
        var progress = new Progress<FileInsights.SearchProgress>(p =>
        {
            if (_closed || !ReferenceEquals(_searchCts, cts)) return;
            ShowProgress(p.FilesDone, p.FilesTotal);
            SetSearchStatus($"Searching… {p.FilesDone:N0} of {p.FilesTotal:N0} files, {p.Matches:N0} match{(p.Matches == 1 ? "" : "es")}");
        });
        try
        {
            var limited = await Task.Run(() => FileInsights.Search(scope.Files, pattern, match =>
            {
                if (!token.IsCancellationRequested) _pendingMatches.Enqueue(match);
            }, progress, token), token);
            if (_closed || !ReferenceEquals(_searchCts, cts)) return;
            DrainMatches();
            // Matches arrive in whatever order the files finished; once the
            // search is done they are listed by file, then line.
            _results = new ObservableCollection<ResultRow>(_results
                .OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Line));
            BindResults();
            var count = _results.Count;
            var files = _results.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            ShowSearchSummary(count == 0
                ? $"No matches in {readable:N0} file{(readable == 1 ? "" : "s")}."
                : $"{count:N0} match{(count == 1 ? "" : "es")} in {files:N0} file{(files == 1 ? "" : "s")}" +
                  (limited ? $" — stopped at the first {FileInsights.MaxMatches:N0}; try a longer phrase." : "."));
            SetSearchStatus("");
            if (ResultList.Items.Count > 0 && ResultList.SelectedIndex < 0) ResultList.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            if (!_closed && ReferenceEquals(_searchCts, cts))
            {
                DrainMatches();
                ShowSearchSummary($"Search cancelled — {_results.Count:N0} match{(_results.Count == 1 ? "" : "es")} so far.");
                SetSearchStatus("");
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                _resultTimer.Stop();
                if (!_closed) RefreshTypeChoices();
            }
            cts.Dispose();
            if (!_closed) UpdateButtons();
        }
    }

    private void DrainMatches()
    {
        var added = 0;
        while (added < 2_000 && _pendingMatches.TryDequeue(out var match))
        {
            _results.Add(ResultRow.From(match));
            added++;
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _resultTimer.Stop();
    }

    private void ClearResults(string status)
    {
        _pendingMatches.Clear();
        _results.Clear();
        _searchSummary = status;
        SearchStatus.Text = status;
    }

    // Result filters narrow the matches already found, without searching
    // again: by file type, and by text in the file name, folder or line.
    private void BindResults()
    {
        ResultList.ItemsSource = _results;
        CollectionViewSource.GetDefaultView(_results).Filter = item => item is ResultRow row && ShowsRow(row);
    }

    private bool ShowsRow(ResultRow row)
    {
        if (_typeFilter != null && !string.Equals(TypeOf(row.Path), _typeFilter, StringComparison.OrdinalIgnoreCase)) return false;
        var text = ResultFilterBox.Text.Trim();
        return text.Length == 0 ||
               row.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
               row.Folder.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
               (row.Before + row.Hit + row.After).Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string TypeOf(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension.Length == 0 ? "(no extension)" : extension;
    }

    // The type list offers the types among the results, with their match
    // counts. A chosen type stays chosen for the next search, even with none.
    private void RefreshTypeChoices()
    {
        var choices = new List<TypeChoice> { new(null, "All types") };
        choices.AddRange(_results.GroupBy(r => TypeOf(r.Path))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TypeChoice(g.Key, $"{g.Key} ({g.Count():N0})")));
        if (_typeFilter != null && choices.All(c => c.Type != _typeFilter))
            choices.Add(new TypeChoice(_typeFilter, $"{_typeFilter} (0)"));
        var keep = _typeFilter;
        TypeFilterBox.ItemsSource = choices;
        TypeFilterBox.SelectedItem = choices.First(c => c.Type == keep);
    }

    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeFilterBox.SelectedItem is not TypeChoice choice || choice.Type == _typeFilter) return;
        _typeFilter = choice.Type;
        ApplyResultFilters();
    }

    private void ResultFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyResultFilters();

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _typeFilter = null;
        RefreshTypeChoices();
        ResultFilterBox.Text = "";
        ApplyResultFilters();
    }

    private void ApplyResultFilters()
    {
        if (!IsInitialized) return;
        CollectionViewSource.GetDefaultView(_results).Refresh();
        ClearFiltersButton.IsEnabled = IsFiltered;
        ShowSearchSummary(_searchSummary);
    }

    private bool IsFiltered => _typeFilter != null || ResultFilterBox.Text.Trim().Length > 0;

    private void ShowSearchSummary(string summary)
    {
        _searchSummary = summary;
        SearchStatus.Text = IsFiltered && _results.Count > 0
            ? $"{summary} Showing {ResultList.Items.Count:N0} with the filters."
            : summary;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _searchCts?.Cancel();
    }

    private void SetScanStatus(string text)
    {
        _scanStatus = text;
        RefreshStatus();
    }

    private void SetSearchStatus(string text)
    {
        _searchStatus = text;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        StatusText.Text = string.Join("   ", new[] { _searchStatus, _scanStatus }.Where(s => s.Length > 0));
        var busy = _scanCts != null || _searchCts != null;
        if (!busy || StatusText.Text.Length == 0) Progress.Visibility = Visibility.Hidden;
        else if (Progress.Visibility != Visibility.Visible)
        {
            Progress.IsIndeterminate = true;
            Progress.Visibility = Visibility.Visible;
        }
    }

    private void ShowProgress(int done, int total)
    {
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, total);
        Progress.Value = done;
        Progress.Visibility = Visibility.Visible;
    }

    private void UpdateButtons()
    {
        var busy = _scanCts != null || _searchCts != null;
        CancelButton.IsEnabled = busy;
        SearchButton.IsEnabled = _scope != null && !string.IsNullOrWhiteSpace(PhraseBox.Text);
        RefreshStatus();
    }

    private void ResultList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelected();
    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is ResultRow row) Reveal(row.Path);
    }

    private void FileList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: FileRowView row }) Reveal(row.Path);
    }

    // Text files open in Notepad and Word documents in the app Windows uses
    // for .docx. Only file types this tool reads can be opened, so a match
    // can never launch a program or script: scripts are text, and open as text.
    private void OpenSelected()
    {
        if (ResultList.SelectedItem is not ResultRow row) return;
        if (!FileInsights.IsAllowedLocalPath(row.Path) || !File.Exists(row.Path) || !FileInsights.IsReadable(row.Path))
        {
            SearchStatus.Text = "That file is no longer there.";
            return;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var program = FileInsights.IsDocx(row.Path)
            ? Path.Combine(windows, "explorer.exe")
            : Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        start.ArgumentList.Add(row.Path);
        StartQuietly(start, "The file couldn't be opened.");
        if (!FileInsights.IsDocx(row.Path))
            SearchStatus.Text = $"Opened in Notepad — the match is on line {row.Line:N0} (Ctrl+G goes to a line).";
    }

    // Opens File Explorer with the file selected; the file itself isn't opened.
    private void Reveal(string path)
    {
        if (!FileInsights.IsAllowedLocalPath(path) || !File.Exists(path)) return;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        // Explorer reads /select,"path" as one argument; a Windows path can't contain a quote.
        var start = new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false };
        StartQuietly(start, "File Explorer couldn't be opened.");
    }

    private void StartQuietly(ProcessStartInfo start, string failure)
    {
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            SearchStatus.Text = failure;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {units[0]}" : $"{value:0.#} {units[unit]}";
    }

    // Each row's ToString is what screen readers announce for it.
    private sealed record TypeChoice(string? Type, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record TypeRowView(string Type, int Count, string SizeText, string Share)
    {
        public override string ToString() => $"{Type}: {Count:N0} file{(Count == 1 ? "" : "s")}, {SizeText}, {Share}";
    }

    private sealed record FileRowView(string Path, string Name, string SizeText, string ModifiedText, string Folder)
    {
        public override string ToString() => $"{Name}, {SizeText}, modified {ModifiedText}, in {Folder}";

        public static FileRowView From(FileInsights.FileEntry file) =>
            new(file.Path, System.IO.Path.GetFileName(file.Path), FormatBytes(file.Size),
                file.Modified.ToString("g"), System.IO.Path.GetDirectoryName(file.Path) ?? "");
    }

    private sealed record ResultRow(string Path, string Name, string Folder, int Line, string LineText,
                                    string Before, string Hit, string After)
    {
        public override string ToString() => $"{Name}, {(LineText.StartsWith('¶') ? "paragraph" : "line")} {Line:N0}: {Before}{Hit}{After}";

        public static ResultRow From(FileInsights.Match match) =>
            new(match.Path, System.IO.Path.GetFileName(match.Path), System.IO.Path.GetDirectoryName(match.Path) ?? "", match.Line,
                match.IsDocx ? $"¶ {match.Line:N0}" : match.Line.ToString("N0"),
                match.Before, match.Hit, match.After);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace CloCloWidget;

public partial class SudokuWindow : Window
{
    // The HTML game's dark theme.
    private static readonly Brush Surface = Rgb(0x1c1f25), Ink = Rgb(0xe7e8eb), Muted = Rgb(0x9aa0aa), Line = Rgb(0x383d46),
        LineStrong = Rgb(0xc9cdd4), UserInk = Rgb(0x86abff), Wrong = Rgb(0xff6f6f), WrongBg = Rgb(0x4a2328),
        Selected = Rgb(0x2e4172), Peer = Rgb(0x242830), Same = Rgb(0x2a3654), Blocked = Rgb(0x4a4d55), BlockedInk = Rgb(0xb9bcc3),
        Forced = Rgb(0xe0c21e), ForcedInk = Rgb(0x111111), LayerDigit = Rgb(0xc9782a), Accent = Rgb(0x6f98ff),
        CellInk = Rgb(0xf3f4f6), UserOn = Rgb(0xc3d5ff), Btn = Rgb(0x2a2e36);

    private sealed class Cell
    {
        public Border Outer = null!, Fill = null!, Ring = null!;
        public TextBlock Digit = null!;
        public UniformGrid Notes = null!;
    }

    // An overlay on the board: the layer of digit D (D = -1: none), its
    // forced cells, the cell being explained and a row/column to mark.
    private sealed record Overlay(int D, bool[] Layer, HashSet<int> Forced, int Focus = -1, HashSet<int>? Unit = null);

    private readonly SudokuStore _store;
    private SudokuGame _game = null!;
    private SudokuGeo _geo = null!;
    private readonly List<Cell> _cells = new();
    private readonly List<(int[] Grid, int[] Notes)> _undo = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _selected = -1;
    private bool _notesMode, _paused, _busy, _layerOpen;
    private int _layerDigit;
    private Overlay? _overlay;
    private SudokuEngine.Hint? _hint;

    public SudokuWindow()
    {
        InitializeComponent();
        _store = SudokuStore.Load();
        MistakesBox.IsChecked = _store.HighlightMistakes;
        Select(SizeBox, _store.LastSize.ToString());
        Select(LevelBox, _store.LastLevel);
        _timer.Tick += (_, _) => Tick();
        KeyDown += OnKeyDown;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && _game != null && !_game.Solved) SetPaused(true); };
        Closing += (_, _) => { _timer.Stop(); _store.Save(); };

        if (_store.Game != null)
        {
            _game = _store.Game;
            StartGame(paused: !_game.Solved);
        }
        else
        {
            StartDaily();
        }
        RenderDaily();
    }

    // ------------------------------------------------------------ games

    private async void NewGame(int n, string level, string? daily)
    {
        if (_busy) return;
        _busy = true;
        BusyCover.Visibility = Visibility.Visible;
        WinCover.Visibility = Visibility.Collapsed;
        _timer.Stop();
        var seed = daily != null ? SudokuEngine.DailySeed(daily) : null;
        var (puzzle, solution) = await Task.Run(() => SudokuEngine.Generate(n, level, seed, "varied"));
        _game = new SudokuGame
        {
            N = n, Level = level, Daily = daily, Puzzle = puzzle, Solution = solution,
            Grid = (int[])puzzle.Clone(), Notes = new int[n * n],
        };
        _store.Game = _game;
        _undo.Clear();
        BusyCover.Visibility = Visibility.Collapsed;
        _busy = false;
        StartGame(paused: false);
        _store.Save();
    }

    private void StartGame(bool paused)
    {
        _geo = new SudokuGeo(_game.N);
        _selected = -1; _overlay = null; _hint = null; _layerDigit = 0;
        HintPanel.Visibility = Visibility.Collapsed;
        if (_game.Daily == null) { Select(SizeBox, _game.N.ToString()); Select(LevelBox, _game.Level); }
        BuildBoard();
        BuildPad();
        if (_layerOpen) { _layerDigit = SudokuEngine.DigitsByFrequency(_geo, _game.Grid).Order[0]; _overlay = LayerOverlay(_layerDigit); }
        SetPaused(paused && !_game.Solved);
        Render();
    }

    private bool InProgress => _game != null && !_game.Solved && _game.Grid.Where((v, i) => v != 0 && _game.Puzzle[i] == 0).Any();

    private void StartDaily()
    {
        var today = SudokuEngine.DateKey(DateTime.Today);
        if (_game != null && _game.Daily == today && !_game.Solved) { SetPaused(false); return; }
        NewGame(9, SudokuEngine.DailyLevel(DateTime.Today), today);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (InProgress && !Confirm("Start a new puzzle? Current progress will be lost.")) return;
        var n = int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag);
        var level = (string)((ComboBoxItem)LevelBox.SelectedItem).Tag;
        _store.LastSize = n; _store.LastLevel = level;
        NewGame(n, level, null);
    }

    private void Daily_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var today = SudokuEngine.DateKey(DateTime.Today);
        if (_game.Daily != today && InProgress && !Confirm("Start today's daily puzzle? Your current game will be lost.")) return;
        StartDaily();
    }

    private bool Confirm(string message) =>
        System.Windows.MessageBox.Show(this, message, "Layer Sudoku", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    // ------------------------------------------------------------ board

    private void BuildBoard()
    {
        int n = _geo.N;
        Board.Children.Clear(); _cells.Clear();
        Board.Rows = n; Board.Columns = n;
        var size = (BoardHost.Width - 4) / n;
        var digitSize = Math.Max(10, size * (n > 9 ? 0.52 : 0.6));
        var noteSize = Math.Max(6, size / _geo.BC * 0.8);
        for (var i = 0; i < n * n; i++)
        {
            int r = _geo.R[i], c = _geo.C[i];
            double right = c == n - 1 ? 0 : 1, bottom = r == n - 1 ? 0 : 1;
            var cell = new Cell();
            cell.Digit = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = digitSize };
            cell.Notes = new UniformGrid { Rows = _geo.BR, Columns = _geo.BC, Margin = new Thickness(1) };
            for (var d = 1; d <= n; d++)
                cell.Notes.Children.Add(new TextBlock { FontSize = noteSize, FontWeight = FontWeights.Medium, Foreground = NoteBrush(d),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            cell.Ring = new Border { BorderBrush = Accent, BorderThickness = new Thickness(0) };
            cell.Fill = new Border { Child = new Grid { Children = { cell.Notes, cell.Digit, cell.Ring } } };
            cell.Outer = new Border
            {
                BorderBrush = Line, BorderThickness = new Thickness(0, 0, right, bottom), Child = cell.Fill, Cursor = Cursors.Hand,
            };
            var index = i;
            cell.Outer.MouseLeftButtonDown += (_, _) => { _selected = index; Render(); };
            AutomationIdFor(cell.Outer, i);
            Board.Children.Add(cell.Outer);
            _cells.Add(cell);
        }

        // One frame per box; neighbours' 1 px edges add up to a 2 px line.
        int boxRows = n / _geo.BR, boxCols = n / _geo.BC;
        BoxLines.Children.Clear();
        BoxLines.Rows = boxRows; BoxLines.Columns = boxCols;
        for (var b = 0; b < boxRows * boxCols; b++)
        {
            int br = b / boxCols, bc = b % boxCols;
            BoxLines.Children.Add(new Border
            {
                BorderBrush = LineStrong,
                BorderThickness = new Thickness(bc == 0 ? 0 : 1, br == 0 ? 0 : 1, bc == boxCols - 1 ? 0 : 1, br == boxRows - 1 ? 0 : 1),
            });
        }
    }

    private void AutomationIdFor(UIElement e, int i) =>
        System.Windows.Automation.AutomationProperties.SetName(e, $"Row {_geo.R[i] + 1}, column {_geo.C[i] + 1}");

    private void BuildPad()
    {
        int n = _geo.N, perRow = n <= 9 ? n : (n + 1) / 2;
        Pad.Children.Clear();
        Pad.Columns = perRow; Pad.Rows = (n + perRow - 1) / perRow;
        for (var d = 1; d <= n; d++)
        {
            var digit = d;
            var label = new Grid();
            label.Children.Add(new TextBlock { Text = SudokuEngine.Sym(d), FontSize = n > 9 ? 18 : 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { FontSize = 10, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -4) });
            var button = new Button
            {
                Content = label, Height = n > 9 ? 40 : 46, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2, 8, 2),
                Background = HueBrush(d), Foreground = CellInk, Tag = d,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "Number " + SudokuEngine.Sym(d));
            button.Click += (_, _) => Enter(digit);
            Pad.Children.Add(button);
        }
    }

    private bool IsWrong(int i) => _game.Grid[i] != 0 && _game.Puzzle[i] == 0 && _game.Grid[i] != _game.Solution[i];

    private void Render()
    {
        if (_game == null || _geo == null) return;
        int n = _geo.N;
        var grid = _game.Grid;
        var showWrong = MistakesBox.IsChecked == true;
        var selValue = _selected >= 0 ? grid[_selected] : 0;
        var peers = _selected >= 0 ? new HashSet<int>(_geo.Peers[_selected]) : null;

        for (var i = 0; i < n * n; i++)
        {
            var cell = _cells[i];
            bool given = _game.Puzzle[i] != 0, filled = grid[i] != 0, wrong = showWrong && IsWrong(i);
            bool ldig = false, forced = false, blk = false, unit = false, focus = false, sel = false, same = false, peer = false;
            if (_overlay != null)
            {
                if (grid[i] == _overlay.D) ldig = true;
                else if (_overlay.Forced.Contains(i)) forced = true;
                else if (!filled && !_overlay.Layer[i]) blk = true;
                unit = _overlay.Unit?.Contains(i) == true;
                focus = _overlay.Focus == i;
                sel = i == _selected;
            }
            else if (_selected >= 0)
            {
                if (i == _selected) sel = true;
                else if (selValue != 0 && grid[i] == selValue) same = true;
                else if (peers!.Contains(i)) peer = true;
            }

            Brush bg = Surface, fg = given ? Ink : UserInk;
            if (peer) bg = Peer;
            if (same) bg = Same;
            if (sel) bg = Selected;
            if (wrong) { bg = WrongBg; fg = Wrong; }
            if (blk) { bg = Blocked; fg = BlockedInk; }
            if (ldig) { bg = LayerDigit; fg = ForcedInk; }
            if (forced) { bg = Forced; fg = ForcedInk; }
            var hued = filled && !ldig && !forced && !wrong;
            if (hued) { bg = HueBrush(grid[i]); fg = given ? CellInk : UserOn; }

            Brush? ring = null; double ringWidth = 0;
            if (hued && sel) { ring = Accent; ringWidth = 3; }
            else if (hued && same) { ring = CellInk; ringWidth = 2; }
            if (unit) { ring = Accent; ringWidth = 2; }
            if (focus) { ring = Accent; ringWidth = 3; }

            cell.Fill.Background = bg;
            cell.Ring.BorderBrush = ring;
            cell.Ring.BorderThickness = new Thickness(ringWidth);
            cell.Digit.Text = filled ? SudokuEngine.Sym(grid[i]) : "";
            cell.Digit.Foreground = fg;
            cell.Digit.FontWeight = given ? FontWeights.ExtraBold : FontWeights.Normal;
            var notes = filled ? 0 : _game.Notes[i];
            for (var d = 1; d <= n; d++)
                ((TextBlock)cell.Notes.Children[d - 1]).Text = (notes & (1 << d)) != 0 ? SudokuEngine.Sym(d) : "";
        }

        var counts = SudokuEngine.CountDigits(_geo, grid);
        foreach (Button b in Pad.Children)
        {
            var d = (int)b.Tag; var left = n - counts[d];
            ((TextBlock)((Grid)b.Content).Children[1]).Text = left > 0 ? left.ToString() : "";
            b.Opacity = left <= 0 ? 0.25 : 1;
        }

        NotesButton.Background = _notesMode ? Accent : Btn;
        NotesButton.Foreground = _notesMode ? Rgb(0x0d1117) : Ink;
        LayerButton.Background = _layerOpen ? Accent : Btn;
        LayerButton.Foreground = _layerOpen ? Rgb(0x0d1117) : Ink;
        UndoButton.IsEnabled = _undo.Count > 0;
        PauseButton.IsEnabled = !_game.Solved;
        StatusLevel.Text = _game.Daily != null
            ? $"📅 Daily · {SudokuEngine.LevelLabel(_game.Level)}"
            : $"{n}×{n} · {SudokuEngine.LevelLabel(_game.Level)}";
        var hintsLeft = Math.Max(0, HintsLeft);
        StatusHints.Text = $"Hints {_game.Hints}/{HintLimit}";
        HintLabel.Text = $"Hint · {hintsLeft}";
        HintButton.Opacity = hintsLeft <= 0 ? 0.45 : 1;
        RenderTime();
        if (_layerOpen) RenderLayerPanel();
    }

    // ------------------------------------------------------------ moves

    private void PushUndo()
    {
        _undo.Add(((int[])_game.Grid.Clone(), (int[])_game.Notes.Clone()));
        if (_undo.Count > 300) _undo.RemoveAt(0);
    }

    private void SetValue(int i, int d)
    {
        _game.Grid[i] = d; _game.Notes[i] = 0;
        if (d != 0) foreach (var p in _geo.Peers[i]) _game.Notes[p] &= ~(1 << d);
    }

    private void AfterMove()
    {
        _hint = null; HintPanel.Visibility = Visibility.Collapsed;
        _overlay = _layerOpen && _layerDigit != 0 ? LayerOverlay(_layerDigit) : null;
        Render(); CheckWin(); _store.Save();
    }

    private bool CanEdit => !_busy && !_game.Solved && !_paused && _selected >= 0 && _game.Puzzle[_selected] == 0;

    private void Enter(int d)
    {
        if (!CanEdit) return;
        PushUndo();
        if (_notesMode && _game.Grid[_selected] == 0) _game.Notes[_selected] ^= 1 << d;
        else
        {
            var v = _game.Grid[_selected] == d ? 0 : d;
            SetValue(_selected, v);
            if (v != 0 && v != _game.Solution[_selected]) _game.Mistakes++;
        }
        AfterMove();
    }

    private void Erase()
    {
        if (!CanEdit) return;
        if (_game.Grid[_selected] == 0 && _game.Notes[_selected] == 0) return;
        PushUndo(); _game.Grid[_selected] = 0; _game.Notes[_selected] = 0; AfterMove();
    }

    private void Undo()
    {
        if (_paused || _busy || _undo.Count == 0) return;
        var (grid, notes) = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _game.Grid = grid; _game.Notes = notes; _game.Solved = false; AfterMove();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Erase_Click(object sender, RoutedEventArgs e) { if (!_busy) Erase(); }
    private void Notes_Click(object sender, RoutedEventArgs e) { _notesMode = !_notesMode; Render(); }

    private void CheckWin()
    {
        if (_game.Solved || !_game.Grid.SequenceEqual(_game.Solution)) return;
        _game.Solved = true; _timer.Stop();
        string? best = null;
        if (!_game.Recorded)
        {
            _game.Recorded = true;
            _game.Score = SudokuEngine.Score(_game.Puzzle, _game.Level, _game.Time, _game.Hints);
            var same = _store.History.Where(h => h.N == _game.N && h.Level == _game.Level).ToList();
            if (same.Count > 0)
            {
                if (_game.Score > same.Max(h => h.Score)) best = "New best score!";
                else if (_game.Time < same.Min(h => h.Time)) best = "New fastest time!";
            }
            _store.History.Add(new SudokuRecord
            {
                Date = DateTime.Now, Daily = _game.Daily, N = _game.N, Level = _game.Level,
                Time = _game.Time, Hints = _game.Hints, Mistakes = _game.Mistakes, Score = _game.Score,
            });
            if (_store.History.Count > 500) _store.History.RemoveRange(0, _store.History.Count - 500);
        }
        if (_game.Daily != null && !_store.DailySolved.Contains(_game.Daily))
        {
            _store.DailySolved.Add(_game.Daily);
            _store.DailySolved.Sort(StringComparer.Ordinal);
        }
        _store.Save();
        RenderDaily();

        var streak = _store.Streak(DateTime.Today);
        WinScore.Text = $"{_game.Score:N0} pts";
        WinBest.Text = best ?? "";
        WinBest.Visibility = best != null ? Visibility.Visible : Visibility.Collapsed;
        WinStreak.Text = _game.Daily != null ? $"🔥 {streak.Current}-day streak{(streak.Current >= streak.Best && streak.Current > 1 ? " — your best!" : "")}" : "";
        WinStreak.Visibility = _game.Daily != null ? Visibility.Visible : Visibility.Collapsed;
        WinMessage.Text = $"{(_game.Daily != null ? "Daily puzzle · " : "")}{_game.N}×{_game.N} {SudokuEngine.LevelLabel(_game.Level)} in {Format(_game.Time)} " +
                          $"with {_game.Hints} hint{(_game.Hints == 1 ? "" : "s")}.";
        WinCover.Visibility = Visibility.Visible;
    }

    private void WinNew_Click(object sender, RoutedEventArgs e)
    {
        WinCover.Visibility = Visibility.Collapsed;
        New_Click(sender, e);
    }

    private void WinClose_Click(object sender, RoutedEventArgs e) => WinCover.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------ hints

    private int HintLimit => SudokuEngine.HintLimit(_game.Level, _game.N);
    private int HintsLeft => HintLimit - _game.Hints;

    private void Hint_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _game.Solved || _paused) return;
        var h = SudokuEngine.FindHint(_geo, _game.Grid, _game.Puzzle, _game.Solution);
        if (h != null && h.Kind != "wrong")
        {
            if (HintsLeft <= 0) { ShowNoHints(); return; }
            _game.Hints++;
        }
        ShowHint(h);
        _store.Save();
    }

    private string CellName(int i) => $"row {_geo.R[i] + 1}, column {_geo.C[i] + 1}";

    private void ShowHint(SudokuEngine.Hint? h)
    {
        _hint = h;
        HintPlace.Visibility = Visibility.Visible;
        _overlay = null;
        var nn = _geo.N * _geo.N;
        if (h == null)
        {
            HintTitle.Text = "Hint"; HintMessage.Text = "Nothing left to deduce."; HintPlace.Visibility = Visibility.Collapsed;
        }
        else if (h.Kind == "wrong")
        {
            HintTitle.Text = "Check your entries";
            HintMessage.Text = "Some numbers (in red) don't fit the solution. The layer method needs a correct board — fix them first.";
            HintPlace.Content = "Remove wrong numbers";
            MistakesBox.IsChecked = true;
        }
        else if (h.Kind == "layer")
        {
            var d = SudokuEngine.Sym(h.Digit);
            HintTitle.Text = $"Layer of {d}";
            var why = h.Rank == 0 ? $"{d} is the most repeated number ({h.Count} times)." : $"{d} appears {h.Count} times (the more repeated layers give nothing new).";
            HintMessage.Text = $"{why} Gray out every cell in a box, row or column that already has {d}. In box {h.Box + 1} only one cell is left (yellow) — {CellName(h.Cell)} must be {d}.";
            _overlay = new Overlay(h.Digit, h.Layer!, new HashSet<int> { h.Cell }, h.Cell);
            HintPlace.Content = $"Place {d}";
        }
        else if (h.Kind == "single")
        {
            HintTitle.Text = $"Single missing in a {h.UnitKind}";
            HintMessage.Text = $"No layer gives a new cell. But {h.UnitKind} {h.Unit + 1} has only one empty cell, and the only number missing there is {SudokuEngine.Sym(h.Value)}.";
            var unit = h.UnitKind == "row" ? _geo.Rows[h.Unit] : _geo.Cols[h.Unit];
            _overlay = new Overlay(-1, Enumerable.Repeat(true, nn).ToArray(), new HashSet<int>(), h.Cell, new HashSet<int>(unit));
            HintPlace.Content = $"Place {SudokuEngine.Sym(h.Value)}";
        }
        else
        {
            HintTitle.Text = "Branching (MRV)";
            HintMessage.Text = $"The layers are stuck. The cell with the fewest candidates is {CellName(h.Cell)}: {{{string.Join(", ", h.Candidates!.Select(SudokuEngine.Sym))}}}. " +
                               $"Trying them one by one, only {SudokuEngine.Sym(h.Value)} leads to a solution.";
            _overlay = new Overlay(-1, Enumerable.Repeat(true, nn).ToArray(), new HashSet<int>(), h.Cell);
            HintPlace.Content = $"Place {SudokuEngine.Sym(h.Value)}";
        }
        if (h != null && h.Cell >= 0) _selected = h.Cell;
        HintPanel.Visibility = Visibility.Visible;
        Render();
    }

    private void ShowNoHints()
    {
        _hint = null; _overlay = null;
        HintTitle.Text = "No hints left";
        HintMessage.Text = $"You've used all {HintLimit} hints for this puzzle. Try the Layer view: start with the most repeated number and look for a box with only one open cell.";
        HintPlace.Visibility = Visibility.Collapsed;
        HintPanel.Visibility = Visibility.Visible;
        Render();
    }

    private void HintPlace_Click(object sender, RoutedEventArgs e)
    {
        if (_hint == null || _game.Solved) return;
        PushUndo();
        if (_hint.Kind == "wrong") { for (var i = 0; i < _game.Grid.Length; i++) if (IsWrong(i)) _game.Grid[i] = 0; }
        else SetValue(_hint.Cell, _hint.Value);
        AfterMove();
    }

    private void HintClose_Click(object sender, RoutedEventArgs e)
    {
        _hint = null;
        _overlay = _layerOpen && _layerDigit != 0 ? LayerOverlay(_layerDigit) : null;
        HintPanel.Visibility = Visibility.Collapsed;
        Render();
    }

    // ------------------------------------------------------------ layers

    private Overlay LayerOverlay(int d)
    {
        var layer = SudokuEngine.BuildLayer(_geo, _game.Grid, d);
        return new Overlay(d, layer, SudokuEngine.ForcedInLayer(_geo, layer).Select(f => f.Cell).ToHashSet());
    }

    private void Layer_Click(object sender, RoutedEventArgs e) { if (!_busy) ToggleLayer(!_layerOpen); }
    private void LayerClose_Click(object sender, RoutedEventArgs e) => ToggleLayer(false);

    private void ToggleLayer(bool on)
    {
        _layerOpen = on;
        LayerPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _hint = null; HintPanel.Visibility = Visibility.Collapsed;
        if (on) { _layerDigit = SudokuEngine.DigitsByFrequency(_geo, _game.Grid).Order[0]; _overlay = LayerOverlay(_layerDigit); }
        else { _layerDigit = 0; _overlay = null; }
        Render();
    }

    private void RenderLayerPanel()
    {
        var (order, counts) = SudokuEngine.DigitsByFrequency(_geo, _game.Grid);
        Chips.Children.Clear();
        foreach (var d in order)
        {
            if (counts[d] >= _geo.N) continue;
            var digit = d;
            var chip = new Button
            {
                Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(10, 4, 10, 4),
                Background = d == _layerDigit ? Accent : HueBrush(d), Foreground = d == _layerDigit ? Rgb(0x0d1117) : CellInk,
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new System.Windows.Documents.Run(SudokuEngine.Sym(d)) { FontSize = 16, FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run($" ×{counts[d]}") { FontSize = 11 },
                    },
                },
            };
            chip.Click += (_, _) =>
            {
                _layerDigit = digit; _overlay = LayerOverlay(digit); _hint = null;
                HintPanel.Visibility = Visibility.Collapsed; Render();
            };
            Chips.Children.Add(chip);
        }
        if (_layerDigit == 0 || order.Length == 0)
        {
            LayerMessage.Text = order.Length > 0 ? $"Start with {SudokuEngine.Sym(order[0])} — it appears most often ({counts[order[0]]} times)." : "";
            FillYellowButton.IsEnabled = false;
            return;
        }
        var count = _overlay?.Forced.Count ?? 0;
        var digitText = SudokuEngine.Sym(_layerDigit);
        LayerMessage.Text = count > 0
            ? $"Layer {digitText}: {count} box{(count > 1 ? "es have" : " has")} only one open cell — those cells must be {digitText}."
            : $"Layer {digitText}: no box has a single open cell yet. Try the next most repeated digit.";
        FillYellowButton.IsEnabled = count > 0;
    }

    // Filling the yellow cells counts as one hint.
    private void FillYellow_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay == null || _overlay.Forced.Count == 0 || _game.Solved || _paused) return;
        if (HintsLeft <= 0) { ShowNoHints(); return; }
        if (_game.Grid.Where((_, i) => IsWrong(i)).Any()) { ShowHint(new SudokuEngine.Hint("wrong")); return; }
        PushUndo();
        foreach (var i in _overlay.Forced) SetValue(i, _overlay.D);
        _game.Hints++;
        AfterMove();
    }

    // ------------------------------------------------------------ timer, pause

    private void Tick()
    {
        if (_paused || _game.Solved || _busy) return;
        _game.Time++;
        RenderTime();
        if (_game.Time % 10 == 0) _store.Save();
    }

    private static string Format(int s)
    {
        int h = s / 3600, m = s / 60 % 60, x = s % 60;
        return (h > 0 ? $"{h}:{m:00}" : m.ToString()) + $":{x:00}";
    }

    private void RenderTime() => PauseText.Text = $"{(_paused ? "▶" : "⏸")} {Format(_game.Time)}";

    private void SetPaused(bool paused)
    {
        if (paused && _game.Solved) paused = false;
        _paused = paused;
        PauseCover.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        Pad.Opacity = paused ? 0.35 : 1;
        Pad.IsEnabled = !paused;
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, paused ? "Resume" : "Pause");
        if (paused || _game.Solved) _timer.Stop(); else _timer.Start();
        RenderTime();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) { if (!_busy && !_game.Solved) SetPaused(!_paused); }
    private void PauseCover_Click(object sender, MouseButtonEventArgs e) { if (!_busy) SetPaused(false); }

    // ------------------------------------------------------------ daily, options

    private void RenderDaily()
    {
        var s = _store.Streak(DateTime.Today);
        DailyStreak.Text = $"{(s.DoneToday ? "✓ " : "")}🔥 {s.Current}";
        DailyButton.Background = s.DoneToday ? Forced : Btn;
        DailyButton.Foreground = s.DoneToday ? ForcedInk : Ink;
        StreakText.Text = $"Streak {s.Current} · best {s.Best} · {s.Total} dailies solved";
    }

    private void Mistakes_Click(object sender, RoutedEventArgs e)
    {
        _store.HighlightMistakes = MistakesBox.IsChecked == true;
        _store.Save();
        Render();
    }

    // ------------------------------------------------------------ keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is ComboBox or ComboBoxItem) return;
        int n = _geo.N;
        if (e.Key is Key.P or Key.Space) { if (!_game.Solved) SetPaused(!_paused); e.Handled = true; return; }
        if (_paused) return;
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Left or Key.Right:
                if (_selected < 0) _selected = 0;
                else
                {
                    int r = _geo.R[_selected], c = _geo.C[_selected];
                    if (e.Key == Key.Up) r = (r + n - 1) % n;
                    if (e.Key == Key.Down) r = (r + 1) % n;
                    if (e.Key == Key.Left) c = (c + n - 1) % n;
                    if (e.Key == Key.Right) c = (c + 1) % n;
                    _selected = r * n + c;
                }
                Render(); e.Handled = true; return;
            case Key.Back or Key.Delete or Key.D0 or Key.NumPad0: Erase(); e.Handled = true; return;
            case Key.N: _notesMode = !_notesMode; Render(); e.Handled = true; return;
            case Key.Z: Undo(); e.Handled = true; return;
        }
        var v = KeyValue(e.Key);
        if (v >= 1 && v <= n) { Enter(v); e.Handled = true; }
    }

    private static int KeyValue(Key key) =>
        key is >= Key.D1 and <= Key.D9 ? key - Key.D0 :
        key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 :
        key is >= Key.A and <= Key.G ? 10 + (key - Key.A) : 0;

    // ------------------------------------------------------------ colours

    private static SolidColorBrush Rgb(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    // One colour per digit, a hue sweep (as the HTML's dark theme: cells
    // hsl(h 42% 33%), note digits hsl(h 80% 70%)).
    private double Hue(int d) => Math.Round((d - 1) * 360.0 / (_geo?.N ?? 9));
    private Brush HueBrush(int d) => Hsl(Hue(d), 0.42, 0.33);
    private Brush NoteBrush(int d) => Hsl(Hue(d), 0.80, 0.70);

    private static Brush Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        var brush = new SolidColorBrush(Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255)));
        brush.Freeze();
        return brush;
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items) if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }
}

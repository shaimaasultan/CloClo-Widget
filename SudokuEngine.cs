using System;
using System.Collections.Generic;
using System.Linq;

namespace CloCloWidget;

// The Layer Sudoku engine - a line-by-line port of the core of
// LayerSudoku.html (itself a port of suduku15.py): the layer-propagation
// method, the generator and the hints. The random source and every loop
// order follow the JavaScript exactly, so a daily puzzle here is the same
// puzzle the HTML game gives for that date.
public sealed class SudokuGeo
{
    public readonly int N, BR, BC;
    public readonly int[] R, C, B;
    public readonly int[][] Rows, Cols, Boxes, Peers;

    public SudokuGeo(int n)
    {
        N = n;
        (BR, BC) = ChooseBoxShape(n);
        R = new int[n * n]; C = new int[n * n]; B = new int[n * n];
        var rows = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        var cols = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        var boxes = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
            {
                int i = r * n + c, b = r / BR * (n / BC) + c / BC;
                R[i] = r; C[i] = c; B[i] = b;
                rows[r].Add(i); cols[c].Add(i); boxes[b].Add(i);
            }
        Rows = rows.Select(l => l.ToArray()).ToArray();
        Cols = cols.Select(l => l.ToArray()).ToArray();
        Boxes = boxes.Select(l => l.ToArray()).ToArray();
        Peers = new int[n * n][];
        for (var i = 0; i < n * n; i++)
        {
            var seen = new List<int>();
            foreach (var j in Rows[R[i]].Concat(Cols[C[i]]).Concat(Boxes[B[i]]))
                if (j != i && !seen.Contains(j)) seen.Add(j);
            Peers[i] = seen.ToArray();
        }
    }

    // Box shape (a, b) with a*b = N and a closest to sqrt(N).
    private static (int, int) ChooseBoxShape(int n)
    {
        double best = double.MaxValue; int br = 1, bc = n;
        var root = Math.Sqrt(n);
        for (var r = 1; r <= n; r++)
            if (n % r == 0)
            {
                var c = n / r;
                if (r <= c) { var d = Math.Abs(r - root); if (d < best) { best = d; br = r; bc = c; } }
            }
        return (br, bc);
    }
}

public static class SudokuEngine
{
    public const string Symbols = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public static string Sym(int v) => v <= 0 ? "" : v < Symbols.Length ? Symbols[v].ToString() : v.ToString();

    public static readonly string[] Levels = { "easy", "medium", "hard", "expert" };
    public static string LevelLabel(string level) => level switch { "easy" => "Easy", "medium" => "Medium", "hard" => "Hard", _ => "Expert" };
    private static double Ratio(string level) => level switch { "easy" => 0.52, "medium" => 0.42, "hard" => 0.30, _ => 0.22 };

    // ------------------------------------------------------------ layers

    public static int[] CountDigits(SudokuGeo g, int[] grid)
    {
        var c = new int[g.N + 1];
        foreach (var v in grid) if (v != 0) c[v]++;
        return c;
    }

    // Digits ordered by how often they already appear (most repeated first).
    public static (int[] Order, int[] Counts) DigitsByFrequency(SudokuGeo g, int[] grid)
    {
        var c = CountDigits(g, grid);
        var ds = Enumerable.Range(1, g.N).ToList();
        ds.Sort((a, b) => c[b] != c[a] ? c[b] - c[a] : a - b);
        return (ds.ToArray(), c);
    }

    // 1 = an empty cell that can still be d (no d in its box, row or column).
    public static bool[] BuildLayer(SudokuGeo g, int[] grid, int d)
    {
        int n = g.N;
        bool[] rowHas = new bool[n], colHas = new bool[n], boxHas = new bool[n];
        for (var i = 0; i < n * n; i++) if (grid[i] == d) { rowHas[g.R[i]] = colHas[g.C[i]] = boxHas[g.B[i]] = true; }
        var layer = new bool[n * n];
        for (var i = 0; i < n * n; i++) layer[i] = grid[i] == 0 && !rowHas[g.R[i]] && !colHas[g.C[i]] && !boxHas[g.B[i]];
        return layer;
    }

    // Boxes whose layer has exactly one open cell: forced ("yellow") cells.
    public static List<(int Box, int Cell)> ForcedInLayer(SudokuGeo g, bool[] layer)
    {
        var output = new List<(int, int)>();
        for (var b = 0; b < g.N; b++)
        {
            int count = 0, last = -1;
            foreach (var i in g.Boxes[b]) if (layer[i]) { count++; last = i; if (count > 1) break; }
            if (count == 1) output.Add((b, last));
        }
        return output;
    }

    // The first row (then column) with exactly one empty cell.
    public static (string Kind, int Unit, int Cell, int Value)? SingleMissing(SudokuGeo g, int[] grid)
    {
        return Scan(g.Rows, "row") ?? Scan(g.Cols, "column");

        (string, int, int, int)? Scan(int[][] units, string kind)
        {
            for (var u = 0; u < g.N; u++)
            {
                int empty = -1, count = 0, seen = 0;
                foreach (var i in units[u]) { if (grid[i] == 0) { count++; empty = i; } else seen |= 1 << grid[i]; }
                if (count == 1)
                {
                    var v = 0;
                    for (var d = 1; d <= g.N; d++) if ((seen & (1 << d)) == 0) { v = d; break; }
                    return (kind, u, empty, v);
                }
            }
            return null;
        }
    }

    public static int[] CandMasks(SudokuGeo g, int[] grid)
    {
        int n = g.N, full = (1 << (n + 1)) - 2;
        int[] rm = new int[n], cm = new int[n], bm = new int[n];
        for (var i = 0; i < n * n; i++) if (grid[i] != 0) { var b = 1 << grid[i]; rm[g.R[i]] |= b; cm[g.C[i]] |= b; bm[g.B[i]] |= b; }
        var output = new int[n * n];
        for (var i = 0; i < n * n; i++) output[i] = grid[i] != 0 ? 0 : full & ~(rm[g.R[i]] | cm[g.C[i]] | bm[g.B[i]]);
        return output;
    }

    public static int PopCount(int x) { var n = 0; while (x != 0) { x &= x - 1; n++; } return n; }

    public static List<int> BitsOf(int mask, int n)
    {
        var a = new List<int>();
        for (var d = 1; d <= n; d++) if ((mask & (1 << d)) != 0) a.Add(d);
        return a;
    }

    // A duplicate in a unit, or an empty cell with no candidates.
    public static bool HasConflict(SudokuGeo g, int[] grid)
    {
        foreach (var units in new[] { g.Rows, g.Cols, g.Boxes })
            foreach (var u in units)
            {
                var seen = 0;
                foreach (var i in u) if (grid[i] != 0) { var b = 1 << grid[i]; if ((seen & b) != 0) return true; seen |= b; }
            }
        var cm = CandMasks(g, grid);
        for (var i = 0; i < grid.Length; i++) if (grid[i] == 0 && cm[i] == 0) return true;
        return false;
    }

    // Layers (most repeated first) and single-missing rows/columns, until
    // nothing changes. Fills grid in place; false on a contradiction.
    public static bool Propagate(SudokuGeo g, int[] grid)
    {
        for (; ; )
        {
            var changed = false;
            foreach (var d in DigitsByFrequency(g, grid).Order)
            {
                var layer = BuildLayer(g, grid, d);
                foreach (var f in ForcedInLayer(g, layer)) if (grid[f.Cell] == 0) { grid[f.Cell] = d; changed = true; }
            }
            for (var sm = SingleMissing(g, grid); sm != null; sm = SingleMissing(g, grid))
            {
                if (sm.Value.Value == 0) return false;
                grid[sm.Value.Cell] = sm.Value.Value; changed = true;
            }
            if (HasConflict(g, grid)) return false;
            if (!changed) return true;
        }
    }

    // Layer propagation + MRV branching; counts solutions up to limit.
    public static (int Count, bool Aborted) CountSolutions(SudokuGeo g, int[] start, int limit, int budget)
    {
        int count = 0, nodes = 0; var aborted = false;
        Rec((int[])start.Clone());
        return (count, aborted);

        void Rec(int[] grid)
        {
            if (count >= limit || aborted) return;
            if (++nodes > budget) { aborted = true; return; }
            if (!Propagate(g, grid)) return;
            var cm = CandMasks(g, grid); int best = -1, bc = 99;
            for (var i = 0; i < grid.Length; i++) if (grid[i] == 0) { var k = PopCount(cm[i]); if (k < bc) { bc = k; best = i; } }
            if (best < 0) { count++; return; }
            foreach (var d in BitsOf(cm[best], g.N))
            {
                var h = (int[])grid.Clone(); h[best] = d; Rec(h);
                if (count >= limit || aborted) return;
            }
        }
    }

    // ------------------------------------------------------------ generator

    // The JavaScript's seeded generator (for the daily puzzle), bit for bit.
    public static Func<double> SeededRandom(string text)
    {
        unchecked
        {
            var h = 1779033703 ^ text.Length;
            foreach (var ch in text)
            {
                h = (h ^ ch) * (int)3432918353u;
                h = (h << 13) | (int)((uint)h >> 19);
            }
            var a = h;
            return () =>
            {
                a = a + 0x6D2B79F5;
                var t = (a ^ (int)((uint)a >> 15)) * (1 | a);
                t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
                return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
            };
        }
    }

    private static void Shuffle<T>(IList<T> a, Func<double> rand)
    {
        for (var i = a.Count - 1; i > 0; i--)
        {
            var j = (int)Math.Floor(rand() * (i + 1));
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    // A random full grid (any family): random fill, fewest-options cell first.
    private static int[] GenerateVaried(SudokuGeo g, Func<double> rand)
    {
        int n = g.N, nn = n * n, full = (1 << (n + 1)) - 2;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var grid = new int[nn];
            int[] rm = new int[n], cm = new int[n], bm = new int[n];
            int nodes = 0, limitNodes = n <= 9 ? 20000 : 80000;
            if (Rec()) return grid;

            bool Rec()
            {
                if (++nodes > limitNodes) return false;
                int best = -1, bc = 99, bmask = 0;
                for (var i = 0; i < nn; i++)
                    if (grid[i] == 0)
                    {
                        int m = full & ~(rm[g.R[i]] | cm[g.C[i]] | bm[g.B[i]]), k = PopCount(m);
                        if (k < bc) { bc = k; best = i; bmask = m; if (k <= 1) break; }
                    }
                if (best < 0) return true;
                if (bc == 0) return false;
                var digits = BitsOf(bmask, n);
                Shuffle(digits, rand);
                foreach (var d in digits)
                {
                    var b = 1 << d; grid[best] = d; rm[g.R[best]] |= b; cm[g.C[best]] |= b; bm[g.B[best]] |= b;
                    if (Rec()) return true;
                    grid[best] = 0; rm[g.R[best]] &= ~b; cm[g.C[best]] &= ~b; bm[g.B[best]] &= ~b;
                }
                return false;
            }
        }
        return GenerateClassic(g, rand);
    }

    // The home grid, scrambled: digits, rows within bands, bands, columns
    // within stacks, stacks.
    private static int[] GenerateClassic(SudokuGeo g, Func<double> rand)
    {
        int n = g.N, br = g.BR, bc = g.BC;
        var rows = new List<int[]>();
        for (var r = 0; r < n; r++) { var row = new int[n]; for (var c = 0; c < n; c++) row[c] = (r * bc + r / br + c) % n + 1; rows.Add(row); }
        var perm = Enumerable.Range(1, n).ToList(); Shuffle(perm, rand);
        rows = rows.Select(row => row.Select(v => perm[v - 1]).ToArray()).ToList();
        var bands = Enumerable.Range(0, n / br).ToList(); Shuffle(bands, rand);
        var rowOrder = new List<int>();
        foreach (var b in bands) { var k = Enumerable.Range(0, br).Select(x => b * br + x).ToList(); Shuffle(k, rand); rowOrder.AddRange(k); }
        rows = rowOrder.Select(r => rows[r]).ToList();
        var stacks = Enumerable.Range(0, n / bc).ToList(); Shuffle(stacks, rand);
        var colOrder = new List<int>();
        foreach (var s in stacks) { var k = Enumerable.Range(0, bc).Select(x => s * bc + x).ToList(); Shuffle(k, rand); colOrder.AddRange(k); }
        return rows.SelectMany(row => colOrder.Select(c => row[c])).ToArray();
    }

    // Symmetric (180°) clue removal, keeping a removal only while the puzzle
    // stays solvable by the layer method (Easy-Hard) or uniquely solvable with
    // MRV branching (Expert). seed: the daily puzzle's seed, or null.
    public static (int[] Puzzle, int[] Solution) Generate(int n, string level, string? seed, string family)
    {
        var rand = seed != null ? SeededRandom(seed) : Random.Shared.NextDouble;
        var g = new SudokuGeo(n);
        var solution = family == "classic" ? GenerateClassic(g, rand) : GenerateVaried(g, rand);
        var puzzle = (int[])solution.Clone();
        var total = n * n;
        var target = (int)Math.Floor(total * Ratio(level) + 0.5);   // JavaScript's Math.round
        var budget = n <= 9 ? 4000 : 600;
        var layerMethod = level != "expert";

        bool Accept(int[] p)
        {
            if (layerMethod) { var t = (int[])p.Clone(); return Propagate(g, t) && t.All(v => v != 0); }
            var r = CountSolutions(g, p, 2, budget); return !r.Aborted && r.Count == 1;
        }

        var pairs = new List<(int, int)>();
        for (var i = 0; i <= (total - 1) / 2.0; i++) pairs.Add((i, total - 1 - i));
        Shuffle(pairs, rand);
        var clues = total;
        foreach (var (a, b) in pairs)
        {
            if (clues <= target) break;
            int va = puzzle[a], vb = puzzle[b];
            puzzle[a] = 0; puzzle[b] = 0;
            if (Accept(puzzle)) clues -= a == b ? 1 : 2; else { puzzle[a] = va; puzzle[b] = vb; }
        }
        // Expert: after the symmetric pass, also try removing single clues.
        if (!layerMethod && n <= 12)
        {
            var order = Enumerable.Range(0, total).ToList(); Shuffle(order, rand);
            foreach (var i in order)
            {
                if (clues <= target) break;
                if (puzzle[i] == 0) continue;
                var v = puzzle[i]; puzzle[i] = 0;
                if (Accept(puzzle)) clues--; else puzzle[i] = v;
            }
        }
        return (puzzle, solution);
    }

    // ------------------------------------------------------------ daily

    // 9×9; difficulty follows the week: Mon-Tue Easy, Wed-Thu Medium,
    // Fri-Sat Hard, Sun Expert.
    public static string DailyLevel(DateTime day) =>
        new[] { "expert", "easy", "easy", "medium", "medium", "hard", "hard" }[(int)day.DayOfWeek];

    public static string DateKey(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string DailySeed(string dateKey) => "layer-sudoku-daily-" + dateKey;

    // ------------------------------------------------------------ hints

    public sealed record Hint(string Kind, int Cell = -1, int Value = 0, int Digit = 0, int Count = 0, int Rank = 0,
        int Box = 0, bool[]? Layer = null, string? UnitKind = null, int Unit = 0, List<int>? Candidates = null, List<int>? WrongCells = null);

    // The method step by step: fix wrong entries, then layers (most repeated
    // first), then a row or column with one empty cell, then the cell with
    // the fewest candidates.
    public static Hint? FindHint(SudokuGeo g, int[] grid, int[] puzzle, int[] solution)
    {
        var wrong = new List<int>();
        for (var i = 0; i < grid.Length; i++) if (grid[i] != 0 && puzzle[i] == 0 && grid[i] != solution[i]) wrong.Add(i);
        if (wrong.Count > 0) return new Hint("wrong", WrongCells: wrong);

        var (order, counts) = DigitsByFrequency(g, grid);
        for (var k = 0; k < order.Length; k++)
        {
            var d = order[k];
            if (counts[d] >= g.N) continue;
            var layer = BuildLayer(g, grid, d);
            var forced = ForcedInLayer(g, layer);
            if (forced.Count > 0)
                return new Hint("layer", forced[0].Cell, d, d, counts[d], k, forced[0].Box, layer);
        }

        var sm = SingleMissing(g, grid);
        if (sm != null) return new Hint("single", sm.Value.Cell, sm.Value.Value, UnitKind: sm.Value.Kind, Unit: sm.Value.Unit);

        var cm = CandMasks(g, grid); int best = -1, bc = 99;
        for (var i = 0; i < grid.Length; i++) if (grid[i] == 0) { var k = PopCount(cm[i]); if (k < bc) { bc = k; best = i; } }
        if (best < 0) return null;
        return new Hint("mrv", best, solution[best], Candidates: BitsOf(cm[best], g.N));
    }

    // ------------------------------------------------------------ score

    public static double LevelMultiplier(string level) => level switch { "easy" => 1, "medium" => 1.5, "hard" => 2, _ => 3 };

    // Score = size × level × speed ÷ hints: harder and bigger score more;
    // faster than par (8 s per empty cell) adds up to +50%; each hint -10%.
    public static int Score(int[] puzzle, string level, int seconds, int hints)
    {
        var empty = puzzle.Count(v => v == 0);
        var par = empty * 8;
        var speed = Math.Min(1.5, Math.Max(0.3, par / (double)Math.Max(1, seconds)));
        return (int)Math.Floor(empty * 10 * LevelMultiplier(level) * speed * Math.Pow(0.9, hints) + 0.5);
    }

    // Hints per puzzle: more for easier levels and bigger grids.
    public static int HintLimit(string level, int n)
    {
        var baseHints = level switch { "easy" => 5, "medium" => 4, "hard" => 3, _ => 2 };
        return Math.Max(1, (int)Math.Floor(baseHints * n / 9.0 + 0.5));
    }
}

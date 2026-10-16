using System;
using System.Collections.Generic;
using System.Linq;

namespace CloCloWidget;

// Rough sound types that stay similar from one speaker to another. They are
// measured with plain signal math (energy, pitch, frequency balance and the
// two lowest resonances), not with a trained model.
internal enum SoundType { Silence, Burst, Hiss, Hum, Vowel }

// Bands: the frame's spectral shape in 8 bands (dB, average removed so
// loudness and microphone gain drop out); null for silence. PeakHz and
// CentroidHz (the balance point of the spectrum) tell k/t/p and s/sh/f apart.
internal readonly record struct SoundFrame(SoundType Type, char Vowel, float LevelDb, float Pitch, float F1, float F2,
    float HighShare = 0, float LowShare = 0, float[]? Bands = null, float PeakHz = 0, float CentroidHz = 0);

// Letter: a finer guess within the type, by rules (see GuessLetter) - less
// reliable than the type itself, especially across speakers.
internal sealed record SoundSegment(SoundType Type, char Vowel, int StartFrame, int Frames, string Letter = "")
{
    // One symbol per segment: K burst (k, t, p), S hiss (s, sh, f, h),
    // M hum (m, n, l), a/e/i/o vowel colour, _ a real pause.
    public string Token => Type switch
    {
        SoundType.Burst => "K",
        SoundType.Hiss => "S",
        SoundType.Hum => "M",
        SoundType.Vowel => Vowel.ToString(),
        _ => "_",
    };
}

// Samples: the utterance's audio (16 kHz), kept in memory only so its wave
// can be shown and compared; frame k covers Samples[k*160 .. k*160+400).
internal sealed record SoundUtterance(IReadOnlyList<SoundSegment> Segments, IReadOnlyList<SoundFrame> Frames,
    float[]? Samples = null)
{
    public IReadOnlyList<string> Tokens { get; } = Segments.Select(s => s.Token).ToList();
    public string Pattern => string.Join(" ", Tokens);
    public string Letters => string.Join(" ", Segments.Select(s => s.Type == SoundType.Silence ? "_" : s.Letter.Length > 0 ? s.Letter : "?"));
    public int Syllables => Segments.Count(s => s.Type == SoundType.Vowel);

    // 1 = the same pattern, 0 = nothing in common. Edit distance over the
    // tokens, so one missing or extra sound costs only one step.
    public static double Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1;
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++) previous[j] = j;
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Count] / Math.Max(a.Count, b.Count);
    }
}

// Takes 16 kHz mono audio, describes every 10 ms with a SoundFrame and
// reports each utterance (sound between pauses) as a pattern of segments.
// Not thread-safe: feed it from one thread.
internal sealed class SoundPatternAnalyzer
{
    public const int SampleRate = 16000;
    public const int HopSamples = 160;   // 10 ms per frame
    private const int WindowSamples = 400; // 25 ms analysis window
    private const int FftSize = 512;
    private const int LpcOrder = 16;
    private const int PreRollFrames = 12;
    private const int EndSilenceFrames = 35;  // a 350 ms pause ends an utterance
    private const int MaximumFrames = 300;    // 3 s
    private const int FloorFrames = 300;      // background is measured over 3 s
    private const int MinimumVoicedFrames = 6; // a word has at least 60 ms of voice
    private const float HumBelowPeakDb = 8;
    // Edges of the 8 fingerprint bands, roughly evenly spaced by ear.
    internal static readonly int[] BandEdgesHz = { 100, 250, 450, 700, 1000, 1500, 2300, 3500, 7000 };

    private readonly float[] _window = new float[WindowSamples];
    // The last ~4 s of audio, so a finished utterance can take its samples.
    private readonly float[] _recent = new float[65536];
    private long _sampleCount;
    private readonly float[] _hamming = new float[WindowSamples];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly double[] _autocorrelation = new double[LpcOrder + 1];
    private readonly double[] _lpc = new double[LpcOrder + 1];
    private readonly float[] _envelope = new float[FftSize / 2];
    private int _filled;
    private int _sinceHop;
    private float _noiseFloorDb = -60;
    private readonly Queue<float> _floorLevels = new();
    private readonly float[] _floorSorted = new float[FloorFrames];

    private readonly Queue<SoundFrame> _preRoll = new();
    private readonly List<SoundFrame> _utterance = new();
    private int _loudRun;
    private int _quietRun;
    private bool _speaking;

    public event Action<SoundFrame>? FrameAnalyzed;
    public event Action<SoundUtterance>? UtteranceFinished;

    public float NoiseFloorDb => _noiseFloorDb;

    // How far above the background a sound must rise to count as a word
    // starting. Higher ignores more of the room (fan, keyboard, TV).
    public float StartDb { get; set; } = 15;
    private float SilenceDb => Math.Max(6, StartDb - 6);

    // Also report sounds without any voice ("sss", "k" said alone). Off for
    // words, where a voiceless sound is usually a click or the keyboard.
    public bool AcceptVoiceless { get; set; }

    public SoundPatternAnalyzer()
    {
        for (var i = 0; i < WindowSamples; i++)
            _hamming[i] = 0.54f - 0.46f * MathF.Cos(2 * MathF.PI * i / (WindowSamples - 1));
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _recent[_sampleCount++ & (_recent.Length - 1)] = sample;
            if (_filled < WindowSamples) _window[_filled++] = sample;
            else
            {
                Array.Copy(_window, 1, _window, 0, WindowSamples - 1);
                _window[WindowSamples - 1] = sample;
            }
            if (_filled == WindowSamples && ++_sinceHop >= HopSamples)
            {
                _sinceHop = 0;
                OnFrame(AnalyzeFrame());
            }
        }
    }

    private SoundFrame AnalyzeFrame()
    {
        double energy = 0;
        var crossings = 0;
        for (var i = 0; i < WindowSamples; i++)
        {
            energy += _window[i] * _window[i];
            if (i > 0 && (_window[i] >= 0) != (_window[i - 1] >= 0)) crossings++;
        }
        var levelDb = (float)(10 * Math.Log10(energy / WindowSamples + 1e-10));
        var crossingRate = (float)crossings / WindowSamples;

        UpdateNoiseFloor(levelDb);
        var relativeDb = levelDb - _noiseFloorDb;
        if (relativeDb < SilenceDb) return new SoundFrame(SoundType.Silence, ' ', levelDb, 0, 0, 0);

        var pitch = EstimatePitch();

        // Power spectrum for the frequency balance.
        for (var i = 0; i < FftSize; i++)
        {
            _re[i] = i < WindowSamples ? _window[i] * _hamming[i] : 0;
            _im[i] = 0;
        }
        Fft(_re, _im);
        double low = 0, mid = 0, high = 0;
        const double binHz = (double)SampleRate / FftSize;
        var bands = new float[BandEdgesHz.Length - 1];
        double peakPower = 0, peakHz = 0, weighted = 0, weight = 0;
        for (var k = 1; k < FftSize / 2; k++)
        {
            var power = _re[k] * _re[k] + _im[k] * _im[k];
            var hz = k * binHz;
            if (hz >= 300 && hz <= 7500)
            {
                weighted += power * hz;
                weight += power;
                if (hz >= 500 && power > peakPower) { peakPower = power; peakHz = hz; }
            }
            for (var b = 0; b < bands.Length; b++)
                if (hz >= BandEdgesHz[b] && hz < BandEdgesHz[b + 1]) { bands[b] += power; break; }
            if (hz < 80) continue;
            if (hz < 450) low += power;
            else if (hz < 3500) mid += power;
            else high += power;
        }
        var total = low + mid + high + 1e-12;
        var highShare = (float)(high / total);
        var lowShare = (float)(low / total);
        for (var b = 0; b < bands.Length; b++) bands[b] = 10 * MathF.Log10(bands[b] + 1e-10f);
        var bandMean = bands.Average();
        for (var b = 0; b < bands.Length; b++) bands[b] -= bandMean;
        var centroidHz = (float)(weight > 0 ? weighted / weight : 0);
        var peak = (float)peakHz;

        if (pitch <= 0)
        {
            // Unvoiced: s/sh/f put most energy up high; bursts are told
            // apart by their shortness later, once the utterance is known.
            var hiss = highShare > 0.3 || crossingRate > 0.25;
            return new SoundFrame(hiss ? SoundType.Hiss : SoundType.Burst, ' ', levelDb, 0, 0, 0, highShare, lowShare, bands, peak, centroidHz);
        }

        var (f1, f2) = EstimateFormants();
        // m, n, l: voiced, but almost all energy below 450 Hz.
        if (lowShare > 0.82) return new SoundFrame(SoundType.Hum, ' ', levelDb, pitch, f1, f2, highShare, lowShare, bands, peak, centroidHz);
        return new SoundFrame(SoundType.Vowel, VowelColour(f1, f2, pitch), levelDb, pitch, f1, f2, highShare, lowShare, bands, peak, centroidHz);
    }

    // The background is the typical quiet level of the last 3 seconds (the
    // 20th percentile), not the single quietest moment: microphones that gate
    // their own noise drop to near zero now and then, and a floor learned from
    // those made a laptop fan look loud. Speech is left out so it is never
    // learned as background, and digital silence is ignored.
    private void UpdateNoiseFloor(float levelDb)
    {
        if (_speaking || levelDb < -85) return;
        _floorLevels.Enqueue(levelDb);
        if (_floorLevels.Count > FloorFrames) _floorLevels.Dequeue();
        if (_floorLevels.Count < 20) return;
        _floorLevels.CopyTo(_floorSorted, 0);
        Array.Sort(_floorSorted, 0, _floorLevels.Count);
        _noiseFloorDb = Math.Clamp(_floorSorted[_floorLevels.Count / 5], -85, -20);
    }

    // Normalized autocorrelation over 70-400 Hz. Returns 0 when unvoiced.
    private float EstimatePitch()
    {
        const int minLag = SampleRate / 400, maxLag = SampleRate / 70;
        double best = 0;
        var bestLag = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0, a = 0, b = 0;
            for (var i = 0; i + lag < WindowSamples; i++)
            {
                sum += _window[i] * _window[i + lag];
                a += _window[i] * _window[i];
                b += _window[i + lag] * _window[i + lag];
            }
            var r = sum / Math.Sqrt(a * b + 1e-12);
            if (r > best) { best = r; bestLag = lag; }
        }
        return best > 0.5 ? (float)SampleRate / bestLag : 0;
    }

    // The two lowest resonances from an LPC envelope of the pre-emphasized frame.
    private (float F1, float F2) EstimateFormants()
    {
        Span<double> frame = stackalloc double[WindowSamples];
        for (var i = 0; i < WindowSamples; i++)
            frame[i] = (_window[i] - (i > 0 ? 0.97 * _window[i - 1] : 0)) * _hamming[i];
        for (var lag = 0; lag <= LpcOrder; lag++)
        {
            double sum = 0;
            for (var i = lag; i < WindowSamples; i++) sum += frame[i] * frame[i - lag];
            _autocorrelation[lag] = sum;
        }
        if (_autocorrelation[0] <= 0) return (0, 0);
        _autocorrelation[0] *= 1.0001;

        // Levinson-Durbin.
        Array.Clear(_lpc);
        _lpc[0] = 1;
        Span<double> previous = stackalloc double[LpcOrder + 1];
        var error = _autocorrelation[0];
        for (var i = 1; i <= LpcOrder; i++)
        {
            double acc = _autocorrelation[i];
            for (var j = 1; j < i; j++) acc += _lpc[j] * _autocorrelation[i - j];
            var k = -acc / error;
            for (var j = 0; j <= LpcOrder; j++) previous[j] = _lpc[j];
            for (var j = 1; j < i; j++) _lpc[j] = previous[j] + k * previous[i - j];
            _lpc[i] = k;
            error *= 1 - k * k;
            if (error <= 0) return (0, 0);
        }

        for (var bin = 0; bin < _envelope.Length; bin++)
        {
            var w = Math.PI * bin / _envelope.Length;
            double re = 0, im = 0;
            for (var j = 0; j <= LpcOrder; j++)
            {
                re += _lpc[j] * Math.Cos(w * j);
                im -= _lpc[j] * Math.Sin(w * j);
            }
            _envelope[bin] = (float)(1 / (re * re + im * im + 1e-12));
        }

        const float binHz = (float)SampleRate / FftSize; // FftSize/2 envelope bins over 0-8 kHz
        var peaks = new List<float>();
        for (var bin = 1; bin < _envelope.Length - 1 && peaks.Count < 3; bin++)
        {
            var hz = bin * binHz;
            if (hz < 200) continue;
            if (hz > 3200) break;
            if (_envelope[bin] > _envelope[bin - 1] && _envelope[bin] >= _envelope[bin + 1])
                peaks.Add(hz);
        }
        var f1 = peaks.FirstOrDefault(p => p <= 1100);
        if (f1 == 0) return (0, 0);
        var f2 = peaks.FirstOrDefault(p => p > f1 + 200);
        return (f1, f2);
    }

    // Vowel colour from the ratio of the two resonances, which shrinks the
    // difference between a child, a woman and a man saying the same vowel.
    // i: front/closed, e: front/mid, a: open, o: back/rounded (also u).
    private static char VowelColour(float f1, float f2, float pitch)
    {
        if (f1 <= 0 || f2 <= 0) return 'v';
        var ratio = f2 / f1;
        if (ratio >= 3.5f) return 'i';
        if (ratio >= 2.3f) return 'e';
        // Higher voices have higher resonances; scale F1 back before
        // deciding between open "a" and rounded "o".
        var scale = Math.Clamp(MathF.Pow(pitch / 130f, 0.33f), 1f, 1.3f);
        return f1 / scale >= 600 ? 'a' : 'o';
    }

    private void OnFrame(SoundFrame frame)
    {
        FrameAnalyzed?.Invoke(frame);
        var relativeDb = frame.LevelDb - _noiseFloorDb;

        if (!_speaking)
        {
            _preRoll.Enqueue(frame);
            if (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
            // Nothing counts until the background has been measured.
            _loudRun = relativeDb >= StartDb && _floorLevels.Count >= 50 ? _loudRun + 1 : 0;
            if (_loudRun < 3) return;
            _speaking = true;
            _quietRun = 0;
            _utterance.Clear();
            _utterance.AddRange(_preRoll);
            _preRoll.Clear();
            return;
        }

        _utterance.Add(frame);
        _quietRun = frame.Type == SoundType.Silence ? _quietRun + 1 : 0;
        if (_quietRun < EndSilenceFrames && _utterance.Count < MaximumFrames) return;

        _speaking = false;
        _loudRun = 0;
        var frames = _utterance.ToList();
        var ranIntoLimit = _utterance.Count >= MaximumFrames;
        _utterance.Clear();
        // Sound that never pauses for 3 s (a fan, TV, music) is not a word.
        if (ranIntoLimit) return;
        if (AcceptVoiceless)
        {
            // Teaching a letter said alone: "sss" or "k" has no voice at all.
            if (frames.Count(f => f.Type != SoundType.Silence) < 4) return;
        }
        // A click, a knock or a keyboard has (almost) no voice in it.
        else if (frames.Count(f => f.Type is SoundType.Vowel or SoundType.Hum) < MinimumVoicedFrames) return;
        var segments = BuildSegments(frames);
        if (segments.Count > 0 && (AcceptVoiceless || segments.Any(s => s.Type is SoundType.Vowel or SoundType.Hum)))
            UtteranceFinished?.Invoke(new SoundUtterance(segments, frames, RecentSamples(frames.Count)));
    }

    private sealed class Run(SoundType type, int start, int count)
    {
        public SoundType Type = type;
        public int Start = start;
        public int Count = count;
        public bool Keep; // a real burst: short on purpose, never folded away
    }

    // Frames are consecutive 10 ms hops, and the newest one's window ends at
    // the newest sample, so the utterance starts (count - 1) hops plus one
    // window earlier.
    private float[] RecentSamples(int frameCount)
    {
        var length = (frameCount - 1) * HopSamples + WindowSamples;
        if (length > _recent.Length || length > _sampleCount) return [];
        var samples = new float[length];
        var start = _sampleCount - length;
        for (var i = 0; i < length; i++) samples[i] = _recent[(start + i) & (_recent.Length - 1)];
        return samples;
    }

    internal static List<SoundSegment> BuildSegments(IReadOnlyList<SoundFrame> frames)
    {
        // A rounded vowel (o, u) keeps its energy as low as m/n/l do, but a
        // vowel is the loudest part of a word and a hum is clearly quieter.
        var peakDb = frames.Count == 0 ? 0 : frames.Max(f => f.LevelDb);
        var types = frames.Select(f => f.Type == SoundType.Hum && f.LevelDb >= peakDb - HumBelowPeakDb
            ? SoundType.Vowel : f.Type).ToArray();

        // Runs of the same type (vowels of any colour count as one run).
        var runs = new List<Run>();
        for (var i = 0; i < types.Length; i++)
        {
            if (runs.Count > 0 && runs[^1].Type == types[i]) runs[^1].Count++;
            else runs.Add(new Run(types[i], i, 1));
        }

        // k/t/p = a short closure (silence) then a short unvoiced pop, hissy
        // or not. Unvoiced sound anywhere else is breath or hiss, and a brief
        // unvoiced gap inside a vowel is only a pitch dropout.
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.Type is not (SoundType.Burst or SoundType.Hiss)) continue;
            var afterSilence = i == 0 || runs[i - 1].Type == SoundType.Silence;
            if (afterSilence && run.Count <= (run.Type == SoundType.Burst ? 6 : 4))
            {
                run.Type = SoundType.Burst;
                run.Keep = true;
            }
            else if (run.Type == SoundType.Burst) run.Type = SoundType.Hiss;
        }
        MergeNeighbours(runs);

        // Fold blips shorter than 30 ms into the longer neighbour.
        while (runs.Count > 1)
        {
            var index = runs.FindIndex(r => r.Count < 3 && !r.Keep);
            if (index < 0) break;
            var left = index > 0 ? runs[index - 1].Count : -1;
            var right = index < runs.Count - 1 ? runs[index + 1].Count : -1;
            var into = runs[left >= right ? index - 1 : index + 1];
            into.Start = Math.Min(into.Start, runs[index].Start);
            into.Count += runs[index].Count;
            runs.RemoveAt(index);
            MergeNeighbours(runs);
        }

        var segments = new List<SoundSegment>();
        foreach (var run in runs)
        {
            // Short silences are the closure before k/t/p, not a pause.
            if (run.Type == SoundType.Silence && run.Count < 15) continue;
            var vowel = ' ';
            if (run.Type == SoundType.Vowel)
                vowel = frames.Skip(run.Start).Take(run.Count).Select(f => f.Type == SoundType.Hum ? 'o' : f.Vowel)
                    .Where(v => v != 'v').GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault('v');
            var letter = GuessLetter(run.Type, frames.Skip(run.Start).Take(run.Count).ToList(), peakDb);
            segments.Add(new SoundSegment(run.Type, vowel, run.Start, run.Count, letter));
        }
        while (segments.Count > 0 && segments[0].Type == SoundType.Silence) segments.RemoveAt(0);
        while (segments.Count > 0 && segments[^1].Type == SoundType.Silence) segments.RemoveAt(segments.Count - 1);
        return segments;
    }

    // A finer letter within each sound type, from where the energy sits:
    //   burst: p low and spread out, k in the middle, t high
    //   hiss:  s very high and strong, sh lower, f weak and flat, h weak and low
    //   hum:   n (also m) almost all below 450 Hz, l (also r, w) has more above
    //   vowel: a o u by the first resonance, i e by a high second one
    internal static string GuessLetter(SoundType type, IReadOnlyList<SoundFrame> frames, float peakDb)
    {
        if (frames.Count == 0) return "";
        switch (type)
        {
            case SoundType.Burst:
            {
                // The first frames of the pop carry the place; the rest is breath.
                var pop = frames.Where(f => f.CentroidHz > 0).Take(3).ToList();
                if (pop.Count == 0) return "k";
                var centroid = pop.Average(f => f.CentroidHz);
                var peak = pop.Average(f => f.PeakHz);
                if (centroid < 1800) return "p";
                if (peak >= 3500 || centroid >= 3800) return "t";
                return "k";
            }
            case SoundType.Hiss:
            {
                var heard = frames.Where(f => f.CentroidHz > 0).ToList();
                if (heard.Count == 0) return "h";
                var weakBy = peakDb - heard.Average(f => f.LevelDb);
                var centroid = heard.Average(f => f.CentroidHz);
                var peak = heard.Average(f => f.PeakHz);
                if (weakBy > 25) return centroid < 2200 ? "h" : "f";
                if (peak >= 3800) return "s";
                if (peak >= 1800 && weakBy < 18) return "sh";
                return "h";
            }
            case SoundType.Hum:
                return frames.Average(f => f.LowShare) >= 0.9 ? "n" : "l";
            case SoundType.Vowel:
            {
                var shaped = frames.Where(f => f.F1 > 0 && f.F2 > 0 && f.Pitch > 0).ToList();
                if (shaped.Count == 0) return "";
                var f1 = shaped.Average(f => f.F1);
                var f2 = shaped.Average(f => f.F2);
                var pitch = shaped.Average(f => f.Pitch);
                // F1 is measured well and orders u < o < a. For o and u the two
                // resonances sit close and are often read as one, which pushes
                // F2 up, so F2 only decides clearly front vowels (i, e).
                var scale = Math.Clamp(Math.Pow(pitch / 130, 0.33), 1, 1.3);
                var scaledF1 = f1 / scale;
                if (f2 / scale >= 1900) return scaledF1 < 380 ? "i" : "e";
                return scaledF1 >= 490 ? "a" : scaledF1 >= 390 ? "o" : "u";
            }
            default:
                return "";
        }
    }

    private static void MergeNeighbours(List<Run> runs)
    {
        for (var i = runs.Count - 1; i > 0; i--)
        {
            if (runs[i].Type != runs[i - 1].Type || runs[i].Keep || runs[i - 1].Keep) continue;
            runs[i - 1].Count += runs[i].Count;
            runs.RemoveAt(i);
        }
    }

    private static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wRe = (float)Math.Cos(angle);
            var wIm = (float)Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                float curRe = 1, curIm = 0;
                for (var j = 0; j < length / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = re[i + j + length / 2] * curRe - im[i + j + length / 2] * curIm;
                    var vIm = re[i + j + length / 2] * curIm + im[i + j + length / 2] * curRe;
                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + length / 2] = uRe - vRe;
                    im[i + j + length / 2] = uIm - vIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}

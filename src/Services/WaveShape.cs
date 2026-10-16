using System;
using System.Collections.Generic;
using System.Linq;

namespace CloCloWidget;

// A vowel is a wave that repeats: every pitch period has nearly the same
// shape, and that shape is what makes an "a" sound like "a". This finds one
// typical period straight from the samples - no FFT, no frequency bands -
// and compares shapes the same way, so a voice can be set beside a plain
// sine wave or another voice. Consonants don't repeat (s is noise, k is a
// pop), so they have no single wave shape.
internal static class WaveShape
{
    public const int Points = 64; // every cycle is stretched to this many points

    // Shape: one whole cycle stretched to Points. Ring: the first 4 ms after
    // each pulse, NOT stretched - the mouth rings like fading sine waves at
    // fixed frequencies (about 700 and 1200 Hz for "a"), so this part keeps
    // its timing whatever the pitch of the voice.
    public sealed record Cycle(float[] Shape, float PitchHz, float[] Signal, float[] Ring);

    public const int RingSamples = 64; // 4 ms at 16 kHz

    // The middle of the segment, where the vowel is steadiest.
    public static Cycle? Of(SoundUtterance utterance, SoundSegment segment)
    {
        var samples = utterance.Samples;
        if (samples == null || samples.Length == 0) return null;
        var start = segment.StartFrame * SoundPatternAnalyzer.HopSamples;
        var end = Math.Min(samples.Length, (segment.StartFrame + segment.Frames) * SoundPatternAnalyzer.HopSamples + 400);
        var trim = (end - start) / 5;
        start += trim;
        end -= trim;
        if (end - start < 400) return null;
        var region = samples.AsSpan(start, end - start);

        // Pitch drifts during a vowel, so the period is measured over a short
        // stretch in the middle only.
        const int maxLag = SoundPatternAnalyzer.SampleRate / 70;
        var probeLength = Math.Min(region.Length, 3 * maxLag);
        var probeStart = (region.Length - probeLength) / 2;
        var period = Period(region.Slice(probeStart, probeLength));
        if (period == 0) return null;

        // Cycles run peak to peak: the highest sample in one period near the
        // middle, then the highest about one cycle later, each cycle allowed
        // to be up to a fifth longer or shorter than the one before.
        var first = Math.Max(0, region.Length / 2 - 4 * period);
        var peaks = new List<int> { ArgMax(region, first, first + period) };
        var cycle = period;
        while (peaks.Count < 9)
        {
            var from = peaks[^1] + cycle - cycle / 5;
            var to = peaks[^1] + cycle + cycle / 5;
            if (to >= region.Length) break;
            peaks.Add(ArgMax(region, from, to));
            cycle = peaks[^1] - peaks[^2];
        }
        if (peaks.Count < 3) return null;

        var shape = new float[Points];
        for (var c = 0; c + 1 < peaks.Count; c++)
        {
            var a = peaks[c];
            var length = peaks[c + 1] - a;
            for (var i = 0; i < Points; i++)
            {
                var position = a + (double)i * length / Points;
                var index = (int)position;
                var fraction = (float)(position - index);
                shape[i] += region[index] * (1 - fraction) + region[Math.Min(index + 1, region.Length - 1)] * fraction;
            }
        }
        Normalize(shape);

        var ring = new float[RingSamples];
        var rings = 0;
        foreach (var peak in peaks)
        {
            // Start a little before the peak so the pulse itself is included.
            var at = peak - 4;
            if (at < 0 || at + RingSamples > region.Length) continue;
            for (var i = 0; i < RingSamples; i++) ring[i] += region[at + i];
            rings++;
        }
        if (rings == 0) return null;
        Normalize(ring);

        // Three cycles of the raw signal, for showing.
        var signalEnd = Math.Min(region.Length, peaks[Math.Min(3, peaks.Count - 1)]);
        var signal = region[peaks[0]..signalEnd].ToArray();
        Normalize(signal);
        var averageCycle = (double)(peaks[^1] - peaks[0]) / (peaks.Count - 1);
        return new Cycle(shape, (float)(SoundPatternAnalyzer.SampleRate / averageCycle), signal, ring);
    }

    // A plain sine wave: the simplest wave that repeats.
    public static float[] Sine()
    {
        var sine = new float[Points];
        for (var i = 0; i < Points; i++) sine[i] = MathF.Sin(2 * MathF.PI * i / Points);
        Normalize(sine);
        return sine;
    }

    // How alike two cycle shapes are, 0-1. Where a cycle "starts" is
    // arbitrary, so every shift of one against the other is tried and the
    // best kept. Shapes are normalized, so loudness doesn't matter.
    public static (double Similarity, int Shift) Compare(float[] a, float[] b)
    {
        double best = double.MinValue;
        var bestShift = 0;
        for (var shift = 0; shift < Points; shift++)
        {
            double sum = 0;
            for (var i = 0; i < Points; i++) sum += a[i] * b[(i + shift) % Points];
            if (sum > best) { best = sum; bestShift = shift; }
        }
        return (Math.Max(0, best / Points), bestShift);
    }

    // Rings start at a pulse, so only small shifts (up to 0.5 ms) are tried.
    public static (double Similarity, int Shift) CompareRings(float[] a, float[] b)
    {
        double best = double.MinValue;
        var bestShift = 0;
        for (var shift = -8; shift <= 8; shift++)
        {
            double sum = 0;
            var count = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var j = i + shift;
                if (j < 0 || j >= b.Length) continue;
                sum += a[i] * b[j];
                count++;
            }
            if (count > 0 && sum / count > best) { best = sum / count; bestShift = shift; }
        }
        return (Math.Max(0, best), bestShift);
    }

    public static float[] Shifted(float[] shape, int shift)
    {
        var result = new float[shape.Length];
        for (var i = 0; i < shape.Length; i++) result[i] = shape[(i + shift) % shape.Length];
        return result;
    }

    // The pitch period by matching the signal against itself delayed: the
    // delay where it lines up best (70-400 Hz voices). Of the delays that
    // line up nearly as well as the best, the shortest is the period - two or
    // three periods line up just as well and mustn't be mistaken for one.
    internal static int Period(ReadOnlySpan<float> x)
    {
        const int minLag = SoundPatternAnalyzer.SampleRate / 400, maxLag = SoundPatternAnalyzer.SampleRate / 70;
        var last = Math.Min(maxLag, x.Length / 2);
        if (last <= minLag + 2) return 0;
        var r = new double[last + 2];
        for (var lag = minLag; lag <= last + 1 && lag < x.Length; lag++)
        {
            double sum = 0, a = 0, b = 0;
            for (var i = 0; i + lag < x.Length; i++)
            {
                sum += x[i] * x[i + lag];
                a += x[i] * x[i];
                b += x[i + lag] * x[i + lag];
            }
            r[lag] = sum / Math.Sqrt(a * b + 1e-12);
        }
        var best = 0.0;
        for (var lag = minLag; lag <= last; lag++) best = Math.Max(best, r[lag]);
        if (best < 0.4) return 0;
        for (var lag = minLag + 1; lag <= last; lag++)
            if (r[lag] >= 0.85 * best && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1]) return lag;
        return 0;
    }

    private static int ArgMax(ReadOnlySpan<float> x, int from, int to)
    {
        var best = from;
        for (var i = from; i < to && i < x.Length; i++) if (x[i] > x[best]) best = i;
        return best;
    }

    // Zero average, and an average power of 1.
    private static void Normalize(float[] x)
    {
        if (x.Length == 0) return;
        var mean = x.Average();
        double power = 0;
        for (var i = 0; i < x.Length; i++) { x[i] -= mean; power += x[i] * x[i]; }
        var scale = (float)(1 / Math.Sqrt(power / x.Length + 1e-12));
        for (var i = 0; i < x.Length; i++) x[i] *= scale;
    }
}

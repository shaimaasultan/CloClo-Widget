using System;
using System.Collections.Generic;
using System.Linq;

namespace CloCloWidget;

// Lines two recordings of the same word up in time, from the samples alone
// (no FFT). Every 10 ms is reduced to two numbers - how loud it is, and how
// often the wave crosses zero (often for s, sh, t; rarely for vowels) - and
// dynamic time warping (DTW) finds the best way to stretch and squeeze one
// recording against the other, so syllables and pauses meet even when one
// speaker is faster, or slower in one part of the word only.
internal static class SignalAlignment
{
    public const int BlockSamples = VoiceSamples.SampleRate / 100; // 10 ms
    private const float FloorDb = -40;

    // Loudness of every 10 ms, 0 (40 dB or more below the loudest part) to 1
    // (the loudest part). Relative to the signal's own loudest part, so a
    // quiet microphone and a loud voice give the same outline.
    public static float[] Outline(float[] samples)
    {
        var blocks = samples.Length / BlockSamples;
        if (blocks == 0) return [];
        var levels = new float[blocks];
        for (var b = 0; b < blocks; b++)
        {
            double power = 0;
            for (var i = b * BlockSamples; i < (b + 1) * BlockSamples; i++) power += samples[i] * samples[i];
            levels[b] = (float)(10 * Math.Log10(power / BlockSamples + 1e-12));
        }
        var peak = levels.Max();
        return levels.Select(db => Math.Clamp((db - peak - FloorDb) / -FloorDb, 0f, 1f)).ToArray();
    }

    public sealed record Features(float[] Loudness, float[] Crossings);

    // Zero-crossings only count where there is sound: in near-silence they
    // measure random noise, which made the same word line up differently
    // from one recording to the next. They fade in between 20 and 12 dB
    // below the loudest part.
    public static Features Measure(float[] samples)
    {
        var loudness = Outline(samples);
        var crossings = Crossings(samples);
        for (var b = 0; b < crossings.Length && b < loudness.Length; b++)
            crossings[b] *= Math.Clamp((loudness[b] - 0.5f) / 0.2f, 0f, 1f);
        return new Features(loudness, crossings);
    }

    // How often the wave crosses zero in every 10 ms, 0 (never) to 1 (every
    // other sample or more).
    public static float[] Crossings(float[] samples)
    {
        var blocks = samples.Length / BlockSamples;
        var result = new float[blocks];
        for (var b = 0; b < blocks; b++)
        {
            var count = 0;
            for (var i = b * BlockSamples + 1; i < (b + 1) * BlockSamples; i++)
                if ((samples[i] >= 0) != (samples[i - 1] >= 0)) count++;
            result[b] = Math.Min(1f, count / (BlockSamples / 2f));
        }
        return result;
    }

    public sealed record Alignment(double Similarity, int[] ReferenceForTarget);

    // For every 10 ms block of the target, the block of the reference that
    // matches it. Similarity: 1 = identical once lined up, 0 = an average
    // difference (loudness plus crossings) of half the scale or more.
    public static Alignment? Align(Features target, Features reference)
    {
        int n = target.Loudness.Length, m = reference.Loudness.Length;
        if (n == 0 || m == 0) return null;
        // The path stays in a band around the straight diagonal, which keeps
        // the warping from folding a whole word into a few blocks.
        var band = Math.Max(Math.Abs(n - m) + 10, Math.Max(n, m) / 2);
        var cost = new double[n + 1, m + 1];
        var steps = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) for (var j = 0; j <= m; j++) cost[i, j] = double.PositiveInfinity;
        cost[0, 0] = 0;
        for (var i = 1; i <= n; i++)
        {
            var center = (int)((long)i * m / n);
            for (var j = Math.Max(1, center - band); j <= Math.Min(m, center + band); j++)
            {
                var d = Math.Abs(target.Loudness[i - 1] - reference.Loudness[j - 1])
                        + Math.Abs(target.Crossings[i - 1] - reference.Crossings[j - 1]);
                double diagonal = cost[i - 1, j - 1], up = cost[i - 1, j], left = cost[i, j - 1];
                if (diagonal <= up && diagonal <= left) { cost[i, j] = d + diagonal; steps[i, j] = steps[i - 1, j - 1] + 1; }
                else if (up <= left) { cost[i, j] = d + up; steps[i, j] = steps[i - 1, j] + 1; }
                else { cost[i, j] = d + left; steps[i, j] = steps[i, j - 1] + 1; }
            }
        }
        if (double.IsInfinity(cost[n, m])) return null;

        // Walk the cheapest path back from the end.
        var match = new int[n];
        int a = n, c = m;
        while (a > 0 && c > 0)
        {
            match[a - 1] = c - 1;
            double diagonal = cost[a - 1, c - 1], up = cost[a - 1, c], left = cost[a, c - 1];
            if (diagonal <= up && diagonal <= left) { a--; c--; }
            else if (up <= left) a--;
            else c--;
        }
        var averageDifference = cost[n, m] / Math.Max(1, steps[n, m]);
        return new Alignment(Math.Clamp(1 - averageDifference / 0.5, 0, 1), match);
    }

    // The reference's samples placed on the target's timeline, 10 ms at a
    // time, so the two can be drawn one above the other with breaks lined up.
    public static float[] Warp(float[] reference, Alignment alignment)
    {
        var result = new float[alignment.ReferenceForTarget.Length * BlockSamples];
        for (var i = 0; i < alignment.ReferenceForTarget.Length; i++)
        {
            var from = alignment.ReferenceForTarget[i] * BlockSamples;
            for (var k = 0; k < BlockSamples && from + k < reference.Length; k++)
                result[i * BlockSamples + k] = reference[from + k];
        }
        return result;
    }

    public static float[] WarpOutline(float[] reference, Alignment alignment) =>
        alignment.ReferenceForTarget.Select(j => reference[j]).ToArray();

    // Evenly stretched or squeezed to a length (for the "same length" view).
    public static float[] Stretch(float[] values, int length)
    {
        if (values.Length == 0 || length <= 0) return [];
        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = length == 1 ? 0 : (double)i * (values.Length - 1) / (length - 1);
            var index = (int)position;
            var fraction = (float)(position - index);
            result[i] = values[index] * (1 - fraction) + values[Math.Min(index + 1, values.Length - 1)] * fraction;
        }
        return result;
    }
}

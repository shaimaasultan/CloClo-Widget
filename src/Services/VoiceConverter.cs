using System;
using System.Collections.Generic;
using System.Linq;

namespace CloCloWidget;

// Says a recording again in a David-like voice, without a model and without
// an FFT (an "LPC vocoder"). A voice is a source - the vocal cords' puffs of
// air, which set the pitch - played through a filter - the mouth, which
// shapes the word. Every 10 ms the mouth is measured with linear prediction
// (each sample predicted from the 16 before it), the speaker's own puffs are
// thrown away, and the mouth is played again from sharp single-sample pulses
// at a low pitch, as Windows David's voice is made, or from noise where the
// speaker was hissing (s, sh, f).
internal static class VoiceConverter
{
    private const int Rate = VoiceSamples.SampleRate;
    private const int Hop = Rate / 100;   // 10 ms
    private const int Window = Rate / 50; // 20 ms
    private const int Order = 16;
    private const float PreEmphasis = 0.97f;

    public sealed record Options(
        float PitchHz = 84,
        // Rise and fall with the speaker's intonation, moved down to PitchHz;
        // otherwise one flat pitch.
        bool FollowIntonation = true,
        // Below 1 lowers every resonance by this factor, as a larger (male)
        // mouth would; 1 leaves the speaker's mouth as it is.
        float MouthScale = 1f,
        // These need David's own recording of the word (the reference):
        // the mouth size whose shapes come closest to his, instead of MouthScale;
        bool FitMouth = false,
        // his average tone in place of the speaker's (mouth, microphone and
        // room together);
        bool MatchColor = false,
        // his pitch, moment by moment, lined up by DTW;
        bool MatchMelody = false,
        // the word re-timed onto his timeline, each part as long as his;
        bool MatchTiming = false,
        // his loudness, moment by moment;
        bool MatchLoudness = false,
        // his mouth shape, moment by moment - the word itself - and with it
        // whether each moment is voiced or hissed. With every option on,
        // the result is his recording rebuilt, whatever the speaker said.
        bool MatchMouth = false,
        // With MatchColor: take his tone from these samples - e.g. all his
        // words together, his voice in general - instead of the reference
        // word. Needed when the word isn't known yet, as when searching.
        float[]? ColourFrom = null);

    public sealed record Result(float[] Samples, float MouthScale);

    public static Result ToDavidStyle(float[] samples, Options options, float[]? reference = null)
    {
        if (samples.Length < Window * 2) return new Result([], 1);
        if (reference is { Length: < Window * 2 }) reference = null;
        var hops = samples.Length / Hop;

        // A larger mouth = every resonance lower. Stretching the recording in
        // time lowers all its frequencies by the same factor; the mouth is
        // then measured on the stretched copy (the pitch it also lowers is
        // thrown away anyway).
        var scale = options.FitMouth && reference != null ? BestMouthScale(samples, reference)
            : Math.Clamp(options.MouthScale, 0.6f, 1.2f);
        var analysed = scale == 1f ? samples : SignalAlignment.Stretch(samples, (int)(samples.Length / scale));

        // The speaker's mouth, source loudness and pitch every 10 ms.
        var filters = new double[hops][];
        var gains = new double[hops];
        var pitches = new float[hops];
        for (var h = 0; h < hops; h++)
        {
            filters[h] = new double[Order + 1];
            gains[h] = Mouth(analysed, (int)((h * Hop + Hop / 2) / scale) - Window / 2, filters[h]);
            pitches[h] = Pitch(samples, h * Hop + Hop / 2 - Window);
        }
        pitches = Smooth(pitches);
        var voicedPitches = pitches.Where(p => p > 0).OrderBy(p => p).ToArray();
        var typicalPitch = voicedPitches.Length == 0 ? options.PitchHz : voicedPitches[voicedPitches.Length / 2];
        float OwnPitch(int h) => pitches[h] <= 0 ? 0
            : options.FollowIntonation ? Math.Clamp(pitches[h] * options.PitchHz / typicalPitch, 50, 250)
            : options.PitchHz;

        // What to play, 10 ms at a time: whose mouth, at what pitch (0 =
        // hiss), how loud a source.
        var plan = new List<(double[] Filter, float Pitch, double Gain)>();
        var alignment = reference != null &&
                        (options.MatchMelody || options.MatchTiming || options.MatchLoudness || options.MatchMouth)
            ? SignalAlignment.Align(SignalAlignment.Measure(samples), SignalAlignment.Measure(reference))
            : null;
        if (alignment == null)
        {
            for (var h = 0; h < hops; h++) plan.Add((filters[h], OwnPitch(h), gains[h]));
        }
        else
        {
            // David's mouth, source loudness and pitch every 10 ms, measured
            // the same way as the speaker's.
            var referenceHops = reference!.Length / Hop;
            var referenceFilters = new double[referenceHops][];
            var referenceGains = new double[referenceHops];
            var referencePitches = new float[referenceHops];
            for (var j = 0; j < referenceHops; j++)
            {
                referenceFilters[j] = new double[Order + 1];
                referenceGains[j] = Mouth(reference, j * Hop + Hop / 2 - Window / 2, referenceFilters[j]);
                referencePitches[j] = Pitch(reference, j * Hop + Hop / 2 - Window);
            }
            referencePitches = Smooth(referencePitches);
            // His loudness on the speaker's scale, so the two can be mixed.
            var gainScale = (gains.Average() + 1e-9) / (referenceGains.Average() + 1e-9);

            (double[], float, double) Step(int own, int theirs)
            {
                var filter = options.MatchMouth ? referenceFilters[theirs] : filters[own];
                // Voiced or hissed belongs to the mouth shape: s is hissed
                // whoever says it.
                var voiced = options.MatchMouth ? referencePitches[theirs] > 0 : pitches[own] > 0;
                var pitch = !voiced ? 0
                    : options.MatchMelody && referencePitches[theirs] > 0 ? referencePitches[theirs]
                    : pitches[own] > 0 ? OwnPitch(own)
                    : options.PitchHz;
                var gain = options.MatchLoudness ? referenceGains[theirs] * gainScale : gains[own];
                return (filter, pitch, gain);
            }

            var match = alignment.ReferenceForTarget;
            if (options.MatchTiming)
            {
                // For each of David's 10 ms, the first of the speaker's that
                // lined up with it (or the one before, where none did).
                var ownFor = Enumerable.Repeat(-1, referenceHops).ToArray();
                for (var h = 0; h < match.Length; h++)
                    if (match[h] < referenceHops && ownFor[match[h]] < 0) ownFor[match[h]] = h;
                for (var j = 0; j < referenceHops; j++)
                    if (ownFor[j] < 0) ownFor[j] = j == 0 ? 0 : ownFor[j - 1];
                for (var j = 0; j < referenceHops; j++) plan.Add(Step(Math.Min(ownFor[j], hops - 1), j));
            }
            else
            {
                for (var h = 0; h < hops; h++) plan.Add(Step(h, Math.Min(match[h], referenceHops - 1)));
            }
        }

        // His average tone in place of the speaker's: two fixed filters, one
        // taking out the speaker's average mouth, one putting in his.
        // (His mouth already carries his tone.)
        double[]? ownColour = null, theirColour = null;
        var colourSource = options.ColourFrom ?? reference;
        if (options.MatchColor && !options.MatchMouth && colourSource != null)
        {
            ownColour = AverageMouth(analysed);
            theirColour = AverageMouth(colourSource);
        }

        var output = new float[plan.Count * Hop];
        var history = new double[Order];       // the mouth filter's last outputs, newest first
        var colourIn = new double[Order];      // the tone filters' last inputs and outputs
        var colourOut = new double[Order];
        double deEmphasis = 0;
        double nextPulse = 0;
        var random = new Random(7);

        for (var p = 0; p < plan.Count; p++)
        {
            var (coefficients, pitch, gain) = plan[p];
            for (var i = 0; i < Hop; i++)
            {
                var n = p * Hop + i;
                double excitation;
                if (pitch > 0)
                {
                    // One sharp pulse per period, carrying the period's energy.
                    var period = Rate / pitch;
                    excitation = 0;
                    if (n >= nextPulse)
                    {
                        excitation = gain * Math.Sqrt(period);
                        nextPulse = n + period;
                    }
                }
                else
                {
                    excitation = gain * (random.NextDouble() * 2 - 1) * Math.Sqrt(3);
                    nextPulse = n;
                }

                // The mouth: y[n] = e[n] - sum a[k] y[n-k].
                var y = excitation;
                for (var k = 1; k <= Order; k++) y -= coefficients[k] * history[k - 1];
                Array.Copy(history, 0, history, 1, Order - 1);
                history[0] = y;

                if (ownColour != null && theirColour != null)
                {
                    var flat = y;
                    for (var k = 1; k <= Order; k++) flat += ownColour[k] * colourIn[k - 1];
                    Array.Copy(colourIn, 0, colourIn, 1, Order - 1);
                    colourIn[0] = y;
                    var coloured = flat;
                    for (var k = 1; k <= Order; k++) coloured -= theirColour[k] * colourOut[k - 1];
                    Array.Copy(colourOut, 0, colourOut, 1, Order - 1);
                    colourOut[0] = coloured;
                    y = coloured;
                }

                // Undo the treble lift used for measuring.
                deEmphasis = y + PreEmphasis * deEmphasis;
                output[n] = (float)deEmphasis;
            }
        }

        var peak = output.Length == 0 ? 0 : output.Max(Math.Abs);
        if (peak > 0) for (var i = 0; i < output.Length; i++) output[i] *= 0.8f / peak;
        return new Result(output, scale);
    }

    // Tries mouth sizes from 30% smaller to 10% larger and keeps the one
    // whose mouth shapes come closest to the reference's - but only if it's
    // clearly closer (by a point or more) than leaving the mouth as it is.
    // The scores of neighbouring sizes are nearly equal, so without that a
    // tiny difference in a recording would swing the choice from one size
    // to another and change the sound completely.
    public static float BestMouthScale(float[] samples, float[] reference)
    {
        var target = MouthShapes(reference);
        var asIs = MouthSimilarity(MouthShapes(samples), target);
        var best = 1f;
        var bestScore = asIs;
        for (var scale = 0.70f; scale <= 1.101f; scale += 0.05f)
        {
            if (Math.Abs(scale - 1) < 1e-3) continue;
            var score = MouthSimilarity(MouthShapes(SignalAlignment.Stretch(samples, (int)(samples.Length / scale))), target);
            if (score > bestScore) { bestScore = score; best = (float)Math.Round(scale, 2); }
        }
        return bestScore - asIs >= 0.01 ? best : 1f;
    }

    // A pitch track without single-moment slips: each 10 ms is voiced if most
    // of the 50 ms around it is, at the middle pitch of those voiced moments.
    private static float[] Smooth(float[] pitches)
    {
        var result = new float[pitches.Length];
        for (var i = 0; i < pitches.Length; i++)
        {
            var around = Enumerable.Range(i - 2, 5).Where(j => j >= 0 && j < pitches.Length).Select(j => pitches[j]).ToList();
            var voiced = around.Where(p => p > 0).OrderBy(p => p).ToList();
            result[i] = voiced.Count * 2 > around.Count ? voiced[voiced.Count / 2] : 0;
        }
        return result;
    }

    // Loudness averaged over 30 ms, so one loud or quiet moment doesn't
    // jerk the melody.
    private static double[] SmoothLevels(double[] levels) =>
        levels.Select((_, i) => Enumerable.Range(i - 1, 3).Where(j => j >= 0 && j < levels.Length).Average(j => levels[j])).ToArray();

    // How loud every 10 ms is (root mean square).
    private static double[] Levels(float[] x, int hops)
    {
        var levels = new double[hops];
        for (var h = 0; h < hops; h++)
        {
            double power = 0;
            for (var i = h * Hop; i < (h + 1) * Hop && i < x.Length; i++) power += x[i] * x[i];
            levels[h] = Math.Sqrt(power / Hop);
        }
        return levels;
    }

    // The mouth size that brings the speaker's voice closest to another
    // voice in general, from both voices' average mouth shape (not any one
    // word's), so it can be chosen before knowing what was said. Sizes
    // from 30% smaller to 10% larger; the speaker's own unless another is
    // clearly closer.
    public static float VoiceMouthScale(float[] samples, float[] voice)
    {
        var target = Cepstrum(AverageMouth(voice));
        double Distance(float scale)
        {
            var stretched = Math.Abs(scale - 1) < 1e-3 ? samples : SignalAlignment.Stretch(samples, (int)(samples.Length / scale));
            var c = Cepstrum(AverageMouth(stretched));
            return Math.Sqrt(c.Zip(target, (x, y) => (x - y) * (x - y)).Sum());
        }
        var asIs = Distance(1f);
        var best = 1f;
        var bestDistance = asIs;
        for (var scale = 0.70f; scale <= 1.101f; scale += 0.05f)
        {
            var d = Distance(scale);
            if (d < bestDistance) { bestDistance = d; best = (float)Math.Round(scale, 2); }
        }
        return asIs - bestDistance >= 0.02 ? best : 1f;
    }

    // A mouth filter as 12 numbers (its cepstrum), by the usual recursion.
    private static double[] Cepstrum(double[] a)
    {
        var c = new double[ShapeSize + 1];
        for (var n = 1; n <= ShapeSize; n++)
        {
            var sum = -a[n];
            for (var k = 1; k < n; k++) sum -= (double)k / n * c[k] * a[n - k];
            c[n] = sum;
        }
        return c.Skip(1).ToArray();
    }

    // One mouth filter for the whole recording: the average over its loud
    // moments - what the speaker, microphone and room add to everything.
    private static double[] AverageMouth(float[] x)
    {
        var total = new double[Order + 1];
        var frames = new List<double[]>();
        for (var start = 0; start + Window <= x.Length; start += Hop)
        {
            var r = new double[Order + 1];
            Autocorrelate(x, start, r);
            frames.Add(r);
        }
        var loudest = frames.Count == 0 ? 0 : frames.Max(r => r[0]);
        foreach (var r in frames.Where(r => r[0] > loudest * 0.01))
            for (var k = 0; k <= Order; k++) total[k] += r[k];
        var a = new double[Order + 1];
        Levinson(total, a);
        return a;
    }

    public const int ShapeSize = 12;

    // The mouth's shape every 10 ms, with the source left out entirely: the
    // linear-prediction filter turned into 12 numbers (its "cepstrum",
    // computed from the filter by a short recursion, no FFT). The average
    // over the recording is taken away, which removes what the microphone
    // and room add to every moment alike. Quiet moments are left out - or,
    // with everyMoment, kept as a neutral shape so every 10 ms keeps its place
    // in time (for comparing at a fixed length).
    public static float[][] MouthShapes(float[] samples, bool everyMoment = false)
    {
        var hops = samples.Length / Hop;
        var a = new double[Order + 1];
        var shapes = new List<float[]>();
        var levels = new List<double>();
        for (var h = 0; h < hops; h++)
        {
            var gain = Mouth(samples, h * Hop + Hop / 2 - Window / 2, a);
            var c = new double[ShapeSize + 1];
            for (var n = 1; n <= ShapeSize; n++)
            {
                var sum = -a[n];
                for (var k = 1; k < n; k++) sum -= (double)k / n * c[k] * a[n - k];
                c[n] = sum;
            }
            shapes.Add(c.Skip(1).Select(v => (float)v).ToArray());
            levels.Add(gain);
        }
        if (shapes.Count == 0) return [];
        var loudest = levels.Max();
        var kept = shapes.Where((_, i) => levels[i] > loudest * 0.03).ToList();
        for (var d = 0; d < ShapeSize; d++)
        {
            var mean = kept.Count == 0 ? 0 : kept.Average(s => s[d]);
            foreach (var s in kept) s[d] -= mean;
        }
        if (!everyMoment) return kept.ToArray();
        return shapes.Select((shape, i) => levels[i] > loudest * 0.03 ? shape : new float[ShapeSize]).ToArray();
    }

    // How alike two recordings' mouth shapes are once lined up in time
    // (DTW): 1 = the same, 0 = an average distance of 2 or more. The same
    // word from two different voices typically scores about 50%, different
    // words about 40%, and a recording against its own David-style
    // conversion over 90%, since the conversion keeps the mouth.
    public static double MouthSimilarity(float[][] x, float[][] y)
    {
        int n = x.Length, m = y.Length;
        if (n == 0 || m == 0) return 0;
        var cost = new double[n + 1, m + 1];
        var steps = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) for (var j = 0; j <= m; j++) cost[i, j] = double.PositiveInfinity;
        cost[0, 0] = 0;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                double d = 0;
                for (var k = 0; k < ShapeSize; k++) { var t = x[i - 1][k] - y[j - 1][k]; d += t * t; }
                d = Math.Sqrt(d);
                double diagonal = cost[i - 1, j - 1], up = cost[i - 1, j], left = cost[i, j - 1];
                if (diagonal <= up && diagonal <= left) { cost[i, j] = d + diagonal; steps[i, j] = steps[i - 1, j - 1] + 1; }
                else if (up <= left) { cost[i, j] = d + up; steps[i, j] = steps[i - 1, j] + 1; }
                else { cost[i, j] = d + left; steps[i, j] = steps[i, j - 1] + 1; }
            }
        return Math.Clamp(1 - cost[n, m] / steps[n, m] / 2, 0, 1);
    }

    // The mouth as a filter (a[0] = 1, then Order numbers) for the 20 ms
    // around one sample, for building speech from it.
    public static double[] MouthFilterAt(float[] samples, int centerSample)
    {
        var a = new double[Order + 1];
        Mouth(samples, centerSample - Window / 2, a);
        return a;
    }

    public const int FilterOrder = Order;

    // The mouth as a filter for one 20 ms window, by linear prediction, and
    // the loudness of what's left once the mouth is taken out (the source).
    private static double Mouth(float[] x, int start, double[] a)
    {
        var r = new double[Order + 1];
        Autocorrelate(x, start, r);
        var error = Levinson(r, a);
        // The Hamming window keeps about 40% of the power; undo that.
        return Math.Sqrt(error / Window / 0.397);
    }

    // How the pre-emphasized, windowed 20 ms lines up with itself delayed
    // by 0 to Order samples.
    private static void Autocorrelate(float[] x, int start, double[] r)
    {
        Span<double> frame = stackalloc double[Window];
        for (var i = 0; i < Window; i++)
        {
            var index = Math.Clamp(start + i, 0, x.Length - 1);
            var previous = Math.Clamp(start + i - 1, 0, x.Length - 1);
            var hamming = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Window - 1));
            frame[i] = (x[index] - PreEmphasis * x[previous]) * hamming;
        }
        for (var lag = 0; lag <= Order; lag++)
        {
            double sum = 0;
            for (var i = lag; i < Window; i++) sum += frame[i] * frame[i - lag];
            r[lag] = sum;
        }
    }

    // Levinson-Durbin: the filter a (a[0] = 1) from the autocorrelation r,
    // returning the energy left unpredicted. A flat filter on silence.
    private static double Levinson(double[] r, double[] a)
    {
        Array.Clear(a);
        a[0] = 1;
        if (r[0] <= 1e-10) return 0;
        var r0 = r[0] * 1.0001; // keeps the filter stable
        Span<double> previousA = stackalloc double[Order + 1];
        var error = r0;
        for (var i = 1; i <= Order; i++)
        {
            var acc = r[i];
            for (var j = 1; j < i; j++) acc += a[j] * r[i - j];
            var k = -acc / error;
            a.CopyTo(previousA);
            for (var j = 1; j < i; j++) a[j] = previousA[j] + k * previousA[i - j];
            a[i] = k;
            error *= 1 - k * k;
            if (error <= 0) { Array.Clear(a); a[0] = 1; return 0; }
        }
        return error;
    }

    // The speaker's pitch around this point, or 0 where there is none
    // (silence, s, sh, f): the delay where the signal best matches itself.
    private static float Pitch(float[] x, int start)
    {
        if (start < 0 || start + Window * 2 > x.Length) return 0;
        var span = x.AsSpan(start, Window * 2);
        double energy = 0;
        foreach (var v in span) energy += v * v;
        if (energy / span.Length < 1e-6) return 0;
        var period = WaveShape.Period(span);
        return period == 0 ? 0 : (float)Rate / period;
    }
}

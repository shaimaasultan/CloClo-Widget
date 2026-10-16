using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloCloWidget;

// Finds which of Windows David's words a recording is closest to, from the
// signals alone: no model, no FFT. David is the only reference voice: every
// word has David saying it (optionally also slower and faster), plus any
// recordings the person marked as that word. The recording is changed with
// the David options ticked - towards each of his words in turn - and the
// result is compared with that word's signal. Timing is fixed: both are
// stretched evenly to the same length and compared moment by moment.
internal static class DavidWordMatcher
{
    // Every signal is stretched evenly to this many 10 ms moments.
    public const int FixedMoments = 60;

    public sealed record Example(string Source, float[] Samples, float[][] Mouth, SignalAlignment.Features Features, bool Own);

    public sealed record Word(string Text, VoiceSamples.Speech Speech, float[] Samples, List<Example> Examples);

    public sealed record Match(Word Word, double Score, double Mouth, double Loudness, float[] Compared, float MouthScale, Example Example);

    // David is the only reference voice; extra examples are him at other speeds.
    private static readonly (double Rate, string Name)[] Speeds = [(0.75, "slow"), (1.3, "fast")];

    // Every word prepared this session. Windows David doesn't say a word
    // exactly the same way twice, and the David options follow his
    // recording closely, so all pages use his one take of each word -
    // otherwise the same settings would sound different from page to page.
    private static readonly Dictionary<string, Word> Shared = new(StringComparer.OrdinalIgnoreCase);

    // Each word with David's normal take, plus - with moreExamples - David
    // slow and fast. Words he says nothing for are left out. null when David
    // isn't installed. progress gets (done, total). Call from the UI thread
    // only (the shared list isn't locked).
    public static async Task<List<Word>?> PrepareAsync(IEnumerable<string> texts, bool moreExamples = false,
        Action<int, int>? progress = null)
    {
        var list = texts.ToList();
        var words = new List<Word>();
        for (var index = 0; index < list.Count; index++)
        {
            var text = list[index];
            progress?.Invoke(index, list.Count);
            if (!Shared.TryGetValue(text, out var word))
            {
                var speech = await VoiceSamples.SpeakAsync("Microsoft David", "en", text);
                if (speech == null) return null;
                var samples = VoiceSamples.TrimSilence(speech.Samples);
                if (samples.Length < VoiceSamples.SampleRate / 20) continue;
                word = new Word(text, speech, samples, [await MeasureAsync("David", samples, own: false)]);
                Shared[text] = word;
            }
            if (moreExamples)
            {
                foreach (var (rate, speedName) in Speeds)
                {
                    var source = $"David, {speedName}";
                    if (word.Examples.Any(e => e.Source == source)) continue;
                    var speech = await VoiceSamples.SpeakAsync("Microsoft David", "en", text, rate);
                    if (speech == null) continue;
                    var samples = VoiceSamples.TrimSilence(speech.Samples);
                    if (samples.Length >= VoiceSamples.SampleRate / 20)
                        word.Examples.Add(await MeasureAsync(source, samples, own: false));
                }
            }
            words.Add(word);
        }
        progress?.Invoke(list.Count, list.Count);
        return words;
    }

    // The person's own recording, marked as this word: kept (as numbers
    // and samples, in memory) for the rest of the session.
    public static async Task AddOwnExampleAsync(Word word, float[] recording)
    {
        var count = word.Examples.Count(e => e.Own);
        word.Examples.Add(await MeasureAsync($"you #{count + 1}", recording, own: true));
    }

    private static Task<Example> MeasureAsync(string source, float[] samples, bool own) =>
        Task.Run(() => new Example(source, samples, VoiceConverter.MouthShapes(samples, everyMoment: true),
            SignalAlignment.Measure(samples), own));

    // Every word, best first. For each of David's examples the recording is
    // changed with the options (towards that example, for the options that
    // take from one word: melody, loudness, mouth, fitted mouth size, colour)
    // and compared with the example's signal at the fixed length. A word
    // scores as its closest example. moreExamples off: only David's normal
    // take and the person's own examples. The person's own examples are
    // compared with the recording as it is: same voice, nothing to change.
    public static List<Match> Rank(float[] recording, IReadOnlyList<Word> words, VoiceConverter.Options? convert, bool moreExamples)
    {
        // Timing is never borrowed: it is fixed for every word instead.
        if (convert != null) convert = convert with { MatchTiming = false };
        var perWord = convert is { } o &&
                      (o.FitMouth || (o.MatchColor && o.ColourFrom == null) || o.MatchMelody || o.MatchLoudness || o.MatchMouth);
        var once = convert != null && !perWord ? VoiceConverter.ToDavidStyle(recording, convert) : null;

        var matches = new List<Match>();
        foreach (var word in words)
        {
            Match? best = null;
            foreach (var example in word.Examples)
            {
                if (!moreExamples && !example.Own && example.Source != "David") continue;
                float[] compared;
                var scale = 1f;
                if (example.Own || convert == null) compared = recording;
                else if (once != null) (compared, scale) = (once.Samples, once.MouthScale);
                else
                {
                    var converted = VoiceConverter.ToDavidStyle(recording, convert, example.Samples);
                    (compared, scale) = (converted.Samples, converted.MouthScale);
                }
                var (mouth, loudness) = AtFixedLength(compared, example);
                var score = (mouth + loudness) / 2;
                if (best == null || score > best.Score)
                    best = new Match(word, score, mouth, loudness, compared, scale, example);
            }
            if (best != null) matches.Add(best);
        }
        return matches.OrderByDescending(m => m.Score).ToList();
    }

    // Both signals stretched evenly to FixedMoments and compared moment by
    // moment: mouth shapes (1 = the same, 0 = an average distance of 2 or
    // more) and loudness with zero-crossings (1 = the same, 0 = an average
    // difference of half the scale or more).
    private static (double Mouth, double Loudness) AtFixedLength(float[] signal, Example example)
    {
        var mouth = Stretch(VoiceConverter.MouthShapes(signal, everyMoment: true));
        var theirMouth = Stretch(example.Mouth);
        var features = SignalAlignment.Measure(signal);
        var loudness = Stretch(features.Loudness);
        var crossings = Stretch(features.Crossings);
        var theirLoudness = Stretch(example.Features.Loudness);
        var theirCrossings = Stretch(example.Features.Crossings);
        if (mouth.Length == 0 || theirMouth.Length == 0 || loudness.Length == 0 || theirLoudness.Length == 0) return (0, 0);

        double mouthDistance = 0, loudnessDifference = 0;
        for (var i = 0; i < FixedMoments; i++)
        {
            double d = 0;
            for (var k = 0; k < mouth[i].Length; k++) { var t = mouth[i][k] - theirMouth[i][k]; d += t * t; }
            mouthDistance += Math.Sqrt(d);
            loudnessDifference += Math.Abs(loudness[i] - theirLoudness[i]) + Math.Abs(crossings[i] - theirCrossings[i]);
        }
        return (Math.Clamp(1 - mouthDistance / FixedMoments / 2, 0, 1),
                Math.Clamp(1 - loudnessDifference / FixedMoments / 0.5, 0, 1));
    }

    // Evenly to FixedMoments: each moment takes the nearest original one.
    private static T[] Stretch<T>(T[] values) =>
        values.Length == 0 ? [] : Enumerable.Range(0, FixedMoments).Select(i => values[i * values.Length / FixedMoments]).ToArray();
}

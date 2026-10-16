using System;
using System.Collections.Generic;
using System.Linq;

namespace CloCloWidget;

// Builds a word's signal from a table of letter sounds - no recording, no
// model, no FFT (a "formant synthesizer"). Each letter is a mouth (its three
// lowest ringing frequencies, F1-F3, or a band of noise for s, sh, f) held
// for a while at a loudness; a word is its letters one after another, each
// sliding into the next over 20 ms as a real mouth moves. The voice is
// David-style sharp pulses; hiss and pops are noise.
internal static class LetterSynth
{
    public const int Rate = VoiceSamples.SampleRate;

    public enum Kind { Vowel, Glide, Liquid, Nasal, Fricative, VoicedFricative, Stop, VoicedStop, Breath }

    // One letter sound. Formants for voiced sounds; NoiseHz is the centre of
    // the hiss (fricatives) or of the pop (stops).
    public sealed class Sound
    {
        public string Key { get; set; } = "";
        public Kind Kind { get; set; }
        public double F1 { get; set; }
        public double F2 { get; set; }
        public double F3 { get; set; }
        public double NoiseHz { get; set; }
        public double Ms { get; set; }
        public double Loudness { get; set; }

        public bool Voiced => Kind is Kind.Vowel or Kind.Glide or Kind.Liquid or Kind.Nasal or Kind.VoicedFricative or Kind.VoicedStop;
        public Sound Copy() => (Sound)MemberwiseClone();
    }

    // Textbook values for an adult male voice (Peterson & Barney style).
    public static List<Sound> DefaultTable() =>
    [
        new() { Key = "a", Kind = Kind.Vowel, F1 = 730, F2 = 1090, F3 = 2440, Ms = 140, Loudness = 1.0 },
        new() { Key = "e", Kind = Kind.Vowel, F1 = 530, F2 = 1840, F3 = 2480, Ms = 120, Loudness = 0.95 },
        new() { Key = "i", Kind = Kind.Vowel, F1 = 270, F2 = 2290, F3 = 3010, Ms = 120, Loudness = 0.85 },
        new() { Key = "o", Kind = Kind.Vowel, F1 = 570, F2 = 840, F3 = 2410, Ms = 140, Loudness = 1.0 },
        new() { Key = "u", Kind = Kind.Vowel, F1 = 300, F2 = 870, F3 = 2240, Ms = 130, Loudness = 0.85 },
        new() { Key = "y", Kind = Kind.Glide, F1 = 280, F2 = 2250, F3 = 2900, Ms = 60, Loudness = 0.6 },
        new() { Key = "w", Kind = Kind.Glide, F1 = 300, F2 = 610, F3 = 2200, Ms = 60, Loudness = 0.6 },
        new() { Key = "l", Kind = Kind.Liquid, F1 = 360, F2 = 1300, F3 = 2700, Ms = 70, Loudness = 0.55 },
        new() { Key = "r", Kind = Kind.Liquid, F1 = 420, F2 = 1300, F3 = 1600, Ms = 70, Loudness = 0.55 },
        new() { Key = "m", Kind = Kind.Nasal, F1 = 280, F2 = 1000, F3 = 2200, Ms = 80, Loudness = 0.45 },
        new() { Key = "n", Kind = Kind.Nasal, F1 = 280, F2 = 1700, F3 = 2600, Ms = 70, Loudness = 0.45 },
        new() { Key = "s", Kind = Kind.Fricative, NoiseHz = 5500, Ms = 110, Loudness = 0.35 },
        new() { Key = "sh", Kind = Kind.Fricative, NoiseHz = 3000, Ms = 110, Loudness = 0.4 },
        new() { Key = "f", Kind = Kind.Fricative, NoiseHz = 4000, Ms = 100, Loudness = 0.15 },
        new() { Key = "th", Kind = Kind.Fricative, NoiseHz = 4500, Ms = 90, Loudness = 0.12 },
        new() { Key = "h", Kind = Kind.Breath, F1 = 500, F2 = 1500, F3 = 2500, Ms = 60, Loudness = 0.2 },
        new() { Key = "z", Kind = Kind.VoicedFricative, F1 = 250, F2 = 1700, F3 = 2600, NoiseHz = 5500, Ms = 90, Loudness = 0.35 },
        new() { Key = "v", Kind = Kind.VoicedFricative, F1 = 250, F2 = 1100, F3 = 2300, NoiseHz = 4000, Ms = 80, Loudness = 0.25 },
        new() { Key = "p", Kind = Kind.Stop, F1 = 400, F2 = 1000, F3 = 2400, NoiseHz = 900, Ms = 90, Loudness = 0.4 },
        new() { Key = "t", Kind = Kind.Stop, F1 = 400, F2 = 1700, F3 = 2600, NoiseHz = 4000, Ms = 90, Loudness = 0.5 },
        new() { Key = "k", Kind = Kind.Stop, F1 = 400, F2 = 1800, F3 = 2500, NoiseHz = 2200, Ms = 100, Loudness = 0.5 },
        new() { Key = "b", Kind = Kind.VoicedStop, F1 = 300, F2 = 900, F3 = 2300, NoiseHz = 900, Ms = 70, Loudness = 0.4 },
        new() { Key = "d", Kind = Kind.VoicedStop, F1 = 300, F2 = 1700, F3 = 2600, NoiseHz = 3500, Ms = 70, Loudness = 0.4 },
        new() { Key = "g", Kind = Kind.VoicedStop, F1 = 300, F2 = 1900, F3 = 2500, NoiseHz = 2000, Ms = 70, Loudness = 0.4 },
    ];

    // Spelling to sounds, roughly as English reads it: two-letter sounds
    // first (sh, th, ou, oo, ee, ch), c and q as k, x as k+s, j as d+y,
    // and a final e after a consonant is silent (as in "youtube").
    public static List<Sound> Spell(string word, IReadOnlyList<Sound> table)
    {
        var byKey = table.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var text = new string(word.ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (text.Length > 2 && text[^1] == 'e' && !"aeiou".Contains(text[^2])) text = text[..^1];
        var aliases = new Dictionary<string, string[]>
        {
            ["ou"] = ["u"], ["oo"] = ["u"], ["ee"] = ["i"], ["ea"] = ["i"], ["ch"] = ["t", "sh"], ["ck"] = ["k"],
            ["c"] = ["k"], ["q"] = ["k"], ["x"] = ["k", "s"], ["j"] = ["d", "y"],
        };
        var sounds = new List<Sound>();
        for (var i = 0; i < text.Length;)
        {
            string[]? keys = null;
            var used = 1;
            if (i + 1 < text.Length)
            {
                var pair = text.Substring(i, 2);
                if (aliases.TryGetValue(pair, out var a)) (keys, used) = (a, 2);
                else if (byKey.ContainsKey(pair)) (keys, used) = ([pair], 2);
            }
            if (keys == null)
            {
                var one = text[i].ToString();
                keys = aliases.TryGetValue(one, out var a) ? a : [one];
            }
            foreach (var key in keys)
                if (byKey.TryGetValue(key, out var sound)) sounds.Add(sound);
            i += used;
        }
        return sounds;
    }

    // Pitch (Hz) and loudness (0-1) through the word, by position 0-1.
    public sealed record Melody(Func<double, double> PitchHz, Func<double, double> Loudness)
    {
        // A plain statement: 110 Hz falling to 85 Hz, level loudness.
        public static Melody Plain { get; } = new(x => 110 - 25 * x, _ => 1);
    }

    // The word's signal. With mouths (David's letters), each letter is played
    // through David's measured mouth for it instead of the table's formants.
    public static float[] Build(IReadOnlyList<Sound> sounds, Melody melody, IReadOnlyDictionary<string, double[]>? mouths = null)
    {
        if (sounds.Count == 0) return [];
        var lengths = sounds.Select(s => (int)(s.Ms * Rate / 1000)).ToArray();
        var total = lengths.Sum();
        var output = new float[total];
        var random = new Random(11);

        // Three formant resonators for the voice, one for the noise.
        var formants = new Resonator[] { new(), new(), new() };
        var hiss = new Resonator();
        // David's mouth as an all-pole filter: the last outputs, newest first.
        var history = new double[VoiceConverter.FilterOrder];
        double nextPulse = 0;
        Sound? previous = null;
        var n = 0;

        for (var index = 0; index < sounds.Count; index++)
        {
            var sound = sounds[index];
            var length = lengths[index];
            var from = previous ?? sound;
            double[]? mouth = null;
            mouths?.TryGetValue(sound.Key, out mouth);
            for (var i = 0; i < length; i++, n++)
            {
                var position = (double)n / total;
                // Slide from the previous letter's mouth over the first 20 ms.
                var glide = Math.Min(1, i / (0.020 * Rate));
                double Mix(double a, double b) => a <= 0 ? b : b <= 0 ? a : a + (b - a) * glide;
                var f1 = Mix(from.F1, sound.F1);
                var f2 = Mix(from.F2, sound.F2);
                var f3 = Mix(from.F3, sound.F3);

                // Fade in and out over 5 ms so letters don't click.
                var edge = Math.Min(1, Math.Min(i, length - i) / (0.005 * Rate));
                var level = sound.Loudness * melody.Loudness(position) * edge;

                // The source: pulses where voiced, noise where hissing or popping.
                double pulse = 0;
                var pitch = Math.Clamp(melody.PitchHz(position), 50, 300);
                var voicing = sound.Kind switch
                {
                    Kind.VoicedStop => i < length * 0.6 ? 0.15 : 1, // a faint "voice bar" while closed
                    _ => sound.Voiced ? 1 : 0,
                };
                if (voicing > 0 && n >= nextPulse)
                {
                    pulse = Math.Sqrt(Rate / pitch);
                    nextPulse = n + Rate / pitch;
                }
                else if (voicing == 0) nextPulse = n;
                var noise = random.NextDouble() * 2 - 1;
                double noiseLevel = sound.Kind switch
                {
                    Kind.Fricative => 1,
                    Kind.VoicedFricative => 0.5,
                    Kind.Breath => 1,
                    // Closed (silence), then a 15 ms pop, then a little breath.
                    Kind.Stop or Kind.VoicedStop => i < length * 0.6 ? 0 : i < length * 0.6 + 0.015 * Rate ? 1.5 : 0.3,
                    _ => 0,
                };

                double sample;
                if (mouth != null)
                {
                    // David's mouth shapes both pulses and noise.
                    var excitation = pulse * voicing + noise * noiseLevel * 0.6;
                    var y = excitation;
                    for (var k = 1; k <= VoiceConverter.FilterOrder; k++) y -= mouth[k] * history[k - 1];
                    Array.Copy(history, 0, history, 1, VoiceConverter.FilterOrder - 1);
                    history[0] = Math.Clamp(y, -1e4, 1e4);
                    sample = history[0];
                }
                else
                {
                    // Pulses through the three formants; noise through a band
                    // at the hiss or pop's centre (breath through the formants).
                    var voice = pulse * voicing;
                    if (sound.Kind == Kind.Breath) voice += noise * noiseLevel * 0.3;
                    formants[0].Tune(f1 > 0 ? f1 : 500, 70);
                    formants[1].Tune(f2 > 0 ? f2 : 1500, 100);
                    formants[2].Tune(f3 > 0 ? f3 : 2500, 140);
                    var shaped = formants[2].Step(formants[1].Step(formants[0].Step(voice)));
                    double hissed = 0;
                    if (sound.NoiseHz > 0 && noiseLevel > 0 && sound.Kind != Kind.Breath)
                    {
                        hiss.Tune(sound.NoiseHz, sound.NoiseHz * 0.6);
                        hissed = hiss.Step(noise * noiseLevel) * 8;
                    }
                    sample = shaped + hissed;
                }
                output[n] = (float)(sample * level);
            }
            previous = sound;
        }

        var peak = output.Max(Math.Abs);
        if (peak > 0) for (var i = 0; i < output.Length; i++) output[i] *= 0.8f / peak;
        return output;
    }

    // A two-pole resonator: rings at one frequency with a given bandwidth,
    // computed sample by sample (y = A x + B y1 + C y2).
    private sealed class Resonator
    {
        private double _a, _b, _c, _y1, _y2;

        public void Tune(double frequency, double bandwidth)
        {
            frequency = Math.Min(frequency, Rate / 2.0 - 200);
            _c = -Math.Exp(-2 * Math.PI * bandwidth / Rate);
            _b = 2 * Math.Exp(-Math.PI * bandwidth / Rate) * Math.Cos(2 * Math.PI * frequency / Rate);
            _a = 1 - _b - _c;
        }

        public double Step(double x)
        {
            var y = _a * x + _b * _y1 + _c * _y2;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}

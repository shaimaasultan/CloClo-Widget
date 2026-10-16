using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.SpeechSynthesis;

namespace CloCloWidget;

// A fixed reference for letters: a Windows voice saying each one, measured
// the same way as the microphone, and only the letter's own sound kept, as
// when teaching. Synthesized in memory once per run; nothing is written to
// disk. Utterance keeps the voice's samples for comparing wave shapes.
internal sealed class VoiceLetterReference(string voiceName, string language, string label,
    (string Name, string Spoken)[] letters)
{
    public sealed record Letter(string Name, string Spoken, SoundPiece? Piece, byte[] Wav,
        SoundUtterance? Utterance, SoundSegment? Segment);

    // Hoda (Arabic) can't read Latin letters, so each is a short Arabic
    // syllable; consonants are followed by a long "aa" so that they're
    // pronounced as sounds, not as letter names.
    public static readonly VoiceLetterReference Hoda = new("Microsoft Hoda", "ar", "Windows Hoda", HodaLetters);

    // David (English) for the vowels, said long so the wave settles.
    public static readonly VoiceLetterReference David = new("Microsoft David", "en", "Windows David",
        [("a", "ah"), ("e", "eh"), ("i", "ee"), ("o", "oh"), ("u", "ooh")]);

    // David saying each letter sound: vowels long, consonants before a long
    // "ah" so they're said as sounds, not as letter names.
    public static readonly VoiceLetterReference DavidLetters = new("Microsoft David", "en", "Windows David",
        [("a", "ah"), ("e", "eh"), ("i", "ee"), ("o", "oh"), ("u", "ooh"),
         ("b", "bah"), ("d", "dah"), ("g", "gah"), ("k", "kah"), ("p", "pah"), ("t", "tah"),
         ("f", "fah"), ("h", "hah"), ("s", "sah"), ("sh", "shah"), ("v", "vah"), ("z", "zah"), ("th", "thah"),
         ("l", "lah"), ("r", "rah"), ("m", "mah"), ("n", "nah"), ("w", "wah"), ("y", "yah")]);

    private static (string Name, string Spoken)[] HodaLetters => new[]
    {
        ("a", "آ"), ("e", "إيه"), ("i", "إي"), ("o", "أوه"), ("u", "أو"),
        ("b", "با"), ("t", "تا"), ("d", "دا"), ("k", "كا"), ("g", "جا"), ("q", "قا"),
        ("f", "فا"), ("s", "سا"), ("z", "زا"), ("sh", "شا"), ("h", "ها"), ("kh", "خا"),
        ("gh", "غا"), ("7", "حا"), ("3", "عا"),
        ("m", "ما"), ("n", "نا"), ("l", "لا"), ("r", "را"), ("w", "وا"), ("y", "يا"),
    };

    public string Label { get; } = label;

    // Arabic letters typed directly map to the same references.
    private static readonly Dictionary<string, string> ArabicAliases = new()
    {
        ["ا"] = "a", ["ب"] = "b", ["ت"] = "t", ["د"] = "d", ["ك"] = "k", ["ج"] = "g", ["ق"] = "q",
        ["ف"] = "f", ["س"] = "s", ["ز"] = "z", ["ش"] = "sh", ["ه"] = "h", ["خ"] = "kh",
        ["غ"] = "gh", ["ح"] = "7", ["ع"] = "3", ["م"] = "m", ["ن"] = "n", ["ل"] = "l",
        ["ر"] = "r", ["و"] = "w", ["ي"] = "y",
    };

    private Task<IReadOnlyDictionary<string, Letter>?>? _building;

    public Task<IReadOnlyDictionary<string, Letter>?> GetAsync() => _building ??= BuildAsync();

    public static string Canonical(string letter)
    {
        var trimmed = letter.Trim().ToLowerInvariant();
        return ArabicAliases.TryGetValue(trimmed, out var latin) ? latin : trimmed;
    }

    public static bool IsVowel(string letter) => Canonical(letter) is "a" or "e" or "i" or "o" or "u";

    private async Task<IReadOnlyDictionary<string, Letter>?> BuildAsync()
    {
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                        v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase) &&
                        v.DisplayName.StartsWith(voiceName, StringComparison.OrdinalIgnoreCase));
        if (voice == null) return null;

        using var synth = new SpeechSynthesizer { Voice = voice };
        var result = new Dictionary<string, Letter>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, spoken) in letters)
        {
            byte[] wav;
            using (var stream = await synth.SynthesizeTextToStreamAsync(spoken))
            {
                wav = new byte[stream.Size];
                await stream.ReadAsync(wav.AsBuffer(), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
            }
            // Measuring is plain math, but keep it off the caller's thread.
            var (piece, utterance, segment) = await Task.Run(() => Measure(name, wav));
            result[name] = new Letter(name, spoken, piece, wav, utterance, segment);
        }
        return result;
    }

    private (SoundPiece?, SoundUtterance?, SoundSegment?) Measure(string name, byte[] wav)
    {
        float[] samples;
        try
        {
            using var reader = new WaveFileReader(new MemoryStream(wav));
            ISampleProvider provider = reader.ToSampleProvider();
            if (provider.WaveFormat.Channels == 2) provider = provider.ToMono();
            if (provider.WaveFormat.SampleRate != SoundPatternAnalyzer.SampleRate)
                provider = new WdlResamplingSampleProvider(provider, SoundPatternAnalyzer.SampleRate);
            var list = new List<float>();
            var buffer = new float[4096];
            int read;
            while ((read = provider.Read(buffer.AsSpan())) > 0) list.AddRange(buffer.AsSpan(0, read).ToArray());
            samples = list.ToArray();
        }
        catch
        {
            return (null, null, null);
        }

        // A second of faint noise either side, as a quiet room would give, so
        // the analyser can measure the background before the letter.
        var random = new Random(1);
        var padded = new float[samples.Length + 2 * SoundPatternAnalyzer.SampleRate];
        for (var i = 0; i < padded.Length; i++) padded[i] = (float)(random.NextDouble() - 0.5) * 0.001f;
        for (var i = 0; i < samples.Length; i++) padded[SoundPatternAnalyzer.SampleRate + i] += samples[i];

        var analyzer = new SoundPatternAnalyzer { AcceptVoiceless = true };
        SoundUtterance? longest = null;
        analyzer.UtteranceFinished += u => { if (longest == null || u.Frames.Count > longest.Frames.Count) longest = u; };
        analyzer.Push(padded);
        if (longest == null) return (null, null, null);

        var segment = PickLetterSegment(longest, IsVowel(name));
        if (segment == null) return (null, longest, null);
        var position = longest.Segments.ToList().IndexOf(segment) + 1;
        return (SoundPiece.FromSegment(segment, longest, position, name, voiceName, Label), longest, segment);
    }

    // A vowel is the longest vowel. A consonant comes first in its syllable
    // ("k" in "كا"), so it's the first sound that isn't the vowel after it.
    internal static SoundSegment? PickLetterSegment(SoundUtterance utterance, bool vowel)
    {
        var spoken = utterance.Segments.Where(s => s.Type != SoundType.Silence).ToList();
        if (vowel) return spoken.Where(s => s.Type == SoundType.Vowel).MaxBy(s => s.Frames) ?? spoken.MaxBy(s => s.Frames);
        return spoken.FirstOrDefault(s => s.Type != SoundType.Vowel) ?? spoken.FirstOrDefault();
    }
}

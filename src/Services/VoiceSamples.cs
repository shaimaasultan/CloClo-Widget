using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.SpeechSynthesis;

namespace CloCloWidget;

// Raw audio for the voice signals window: a Windows voice saying some text,
// or microphone samples made playable - all as 16 kHz mono samples, in
// memory only.
internal static class VoiceSamples
{
    public const int SampleRate = 16000;

    public sealed record Speech(byte[] Wav, float[] Samples);

    // null when the voice isn't installed.
    // rate: 1 = the voice's normal speed, 0.7 slower, 1.3 faster.
    public static async Task<Speech?> SpeakAsync(string voiceName, string language, string text, double rate = 1)
    {
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
            v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase) &&
            v.DisplayName.StartsWith(voiceName, StringComparison.OrdinalIgnoreCase));
        if (voice == null) return null;
        using var synth = new SpeechSynthesizer { Voice = voice };
        synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 6);
        byte[] wav;
        using (var stream = await synth.SynthesizeTextToStreamAsync(text))
        {
            wav = new byte[stream.Size];
            await stream.ReadAsync(wav.AsBuffer(), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
        }
        var samples = await Task.Run(() => ToSamples(wav));
        return new Speech(wav, samples);
    }

    public static float[] ToSamples(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2) provider = provider.ToMono();
        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        var samples = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer.AsSpan())) > 0) samples.AddRange(buffer.AsSpan(0, read).ToArray());
        return samples.ToArray();
    }

    // 16-bit PCM WAV, so the microphone recording can be played back.
    public static byte[] ToWav(float[] samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);              // PCM
            writer.Write((short)1);              // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);        // bytes per second
            writer.Write((short)2);              // bytes per sample
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (var sample in samples)
                writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        }
        return stream.ToArray();
    }

    // Rough spelling of a word in the other script, so both voices can say
    // the same word from one text box: David reads only Latin letters and
    // Hoda only Arabic ones. Letter by letter, longest match first.
    private static readonly (string Latin, string Arabic)[] LatinToArabic =
    {
        ("sh", "ش"), ("kh", "خ"), ("gh", "غ"), ("th", "ث"), ("ch", "تش"), ("ph", "ف"),
        ("oo", "و"), ("ou", "و"), ("ee", "ي"), ("ie", "ي"), ("ai", "اي"), ("ay", "اي"),
        ("a", "ا"), ("b", "ب"), ("c", "ك"), ("d", "د"), ("e", "ي"), ("f", "ف"), ("g", "ج"),
        ("h", "ه"), ("i", "ي"), ("j", "ج"), ("k", "ك"), ("l", "ل"), ("m", "م"), ("n", "ن"),
        ("o", "و"), ("p", "ب"), ("q", "ق"), ("r", "ر"), ("s", "س"), ("t", "ت"), ("u", "و"),
        ("v", "ف"), ("w", "و"), ("x", "كس"), ("y", "ي"), ("z", "ز"),
    };

    private static readonly Dictionary<char, string> ArabicToLatin = new()
    {
        ['ا'] = "a", ['أ'] = "a", ['إ'] = "i", ['آ'] = "aa", ['ب'] = "b", ['ت'] = "t", ['ث'] = "th",
        ['ج'] = "g", ['ح'] = "h", ['خ'] = "kh", ['د'] = "d", ['ذ'] = "z", ['ر'] = "r", ['ز'] = "z",
        ['س'] = "s", ['ش'] = "sh", ['ص'] = "s", ['ض'] = "d", ['ط'] = "t", ['ظ'] = "z", ['ع'] = "a",
        ['غ'] = "gh", ['ف'] = "f", ['ق'] = "k", ['ك'] = "k", ['ل'] = "l", ['م'] = "m", ['ن'] = "n",
        ['ه'] = "h", ['ة'] = "a", ['و'] = "o", ['ي'] = "y", ['ى'] = "a", ['ء'] = "", ['ئ'] = "e", ['ؤ'] = "o",
    };

    public static string ToArabic(string text)
    {
        var lower = text.ToLowerInvariant();
        var result = new StringBuilder();
        for (var i = 0; i < lower.Length;)
        {
            var match = LatinToArabic.FirstOrDefault(p => string.CompareOrdinal(lower, i, p.Latin, 0, p.Latin.Length) == 0);
            if (match.Latin != null)
            {
                // A word starting with a vowel needs the hamza seat.
                result.Append(match.Arabic == "ا" && (i == 0 || lower[i - 1] == ' ') ? "أ" : match.Arabic);
                i += match.Latin.Length;
            }
            else result.Append(lower[i++]);
        }
        return result.ToString();
    }

    public static string ToLatin(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
            // Short-vowel marks (fatha, kasra, sukun…) are dropped.
            result.Append(ArabicToLatin.TryGetValue(c, out var latin) ? latin : c is >= 'ً' and <= 'ْ' ? "" : c.ToString());
        return result.ToString();
    }

    // Drops the quiet lead-in and tail so the signals start together. Speech
    // is what rises clearly above the background: 8 dB over the noise
    // (the quietest tenth of the recording) and no more than 30 dB below the
    // loudest part, for at least 30 ms - plus soft sounds just around it (see
    // below). A microphone's steady hiss would pass a fixed threshold; the
    // voices' digital silence has no noise at all.
    public static float[] TrimSilence(float[] samples)
    {
        const int block = SampleRate / 100;
        var blocks = samples.Length / block;
        if (blocks < 3) return samples;
        var levels = new float[blocks];
        for (var b = 0; b < blocks; b++)
        {
            double power = 0;
            for (var i = b * block; i < (b + 1) * block; i++) power += samples[i] * samples[i];
            levels[b] = (float)Math.Sqrt(power / block);
        }
        var sorted = levels.OrderBy(l => l).ToArray();
        var noise = sorted[blocks / 10];
        var threshold = Math.Max(noise * 2.5f, sorted[^1] * 0.0316f);
        // Three loud blocks in a row starting at b.
        bool Speech(int b) => b >= 0 && b + 2 < blocks && levels[b] > threshold && levels[b + 1] > threshold && levels[b + 2] > threshold;
        var first = Enumerable.Range(0, blocks).FirstOrDefault(Speech, -1);
        if (first < 0) return samples;
        var last = Enumerable.Range(first, blocks - first).Last(b => Speech(b)) + 2;
        // Soft, short sounds at the edges of a word - the pop of a "k" or
        // "t", a quiet "s", "f" or "h" - are too brief or quiet to count as
        // speech above, but belong to the word: keep any block within 200 ms
        // before or after that rises over the background at all (5 dB).
        var soft = noise * 1.8f;
        for (var b = first - 1; b >= Math.Max(0, first - 20); b--)
            if (levels[b] > soft) first = b;
        for (var b = last + 1; b <= Math.Min(blocks - 1, last + 20); b++)
            if (levels[b] > soft) last = b;
        var start = Math.Max(0, (first - 3) * block);
        var end = Math.Min(samples.Length, (last + 4) * block);
        return samples[start..end];
    }
}

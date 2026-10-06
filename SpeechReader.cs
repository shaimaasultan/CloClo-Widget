using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace CloCloWidget;

// Has Keeper read text aloud via Windows' own speech synthesis. Uses the
// WinRT SpeechSynthesizer (Windows.Media.SpeechSynthesis) rather than the
// older System.Speech/SAPI5 API tried first — System.Speech only sees
// voices registered the classic way, which on this machine (and most
// Windows installs with extra language packs) excludes newer "OneCore"
// voices like Arabic; WinRT's SpeechSynthesizer.AllVoices sees both.
public static class SpeechReader
{
    private static readonly SpeechSynthesizer Synth = new();
    private static readonly MediaPlayer Player = new();
    // Plenty for a highlighted paragraph; an accidental select-all of a huge
    // document would otherwise be synthesized in full, all in memory.
    private const int MaxSpokenChars = 5000;

    // Raised (on a background thread) when reading stops, after the
    // synthesized audio has been let go - a moment to free memory.
    public static event Action? Finished;

    static SpeechReader()
    {
        // The player would otherwise keep the last synthesized audio (MBs
        // for a long selection) as its source until the next reading.
        Player.MediaEnded += (_, _) => Release();
        Player.MediaFailed += (_, _) => Release();
    }

    private static void Release()
    {
        Player.Source = null;
        Finished?.Invoke();
    }

    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxSpokenChars) text = text[..MaxSpokenChars];
        _ = SpeakAsync(text);
    }

    public static void Stop()
    {
        if (Player.Source == null) return;
        Player.Pause();
        Release();
    }

    private static async Task SpeakAsync(string text)
    {
        try
        {
            var list = new MediaPlaybackList();
            foreach (var (runText, voice) in SplitByScript(text))
            {
                Synth.Voice = voice;
                var stream = await Synth.SynthesizeTextToStreamAsync(runText);
                list.Items.Add(new MediaPlaybackItem(MediaSource.CreateFromStream(stream, stream.ContentType)));
            }
            if (list.Items.Count == 0) return;

            Player.Source = list;
            Player.Play();
        }
        catch
        {
            // No matching voice, no audio device, etc. — reading aloud is a
            // bonus on top of the label already showing the text, not worth
            // surfacing a failure for.
        }
    }

    // Splits into runs of consecutive words sharing a script, each to be
    // synthesized (and queued for playback, via MediaPlaybackList's own
    // auto-advance) with the matching voice — so e.g. "مرحبا Ahmad, كيف
    // حالك؟" has "مرحبا" and "كيف حالك؟" read in Arabic and "Ahmad," read in
    // English, rather than the whole line read by whichever voice matched
    // the first Arabic character found anywhere in it.
    private static List<(string Text, VoiceInformation Voice)> SplitByScript(string text)
    {
        var arabicVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(
            v => v.Language.StartsWith("ar", StringComparison.OrdinalIgnoreCase));
        var defaultVoice = SpeechSynthesizer.DefaultVoice;

        var runs = new List<(string Text, VoiceInformation Voice)>();
        var currentWords = new List<string>();
        VoiceInformation? currentVoice = null;

        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var isArabic = word.Any(c => c is >= '؀' and <= 'ۿ');
            var voice = isArabic && arabicVoice != null ? arabicVoice : defaultVoice;

            if (currentVoice != null && !ReferenceEquals(voice, currentVoice))
            {
                runs.Add((string.Join(' ', currentWords), currentVoice));
                currentWords.Clear();
            }
            currentWords.Add(word);
            currentVoice = voice;
        }
        if (currentWords.Count > 0 && currentVoice != null)
            runs.Add((string.Join(' ', currentWords), currentVoice));

        return runs;
    }
}

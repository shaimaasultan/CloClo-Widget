using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private static readonly SemaphoreSlim SynthesisGate = new(1, 1);
    private static readonly MediaPlayer Player = new();
    private static readonly object PlaybackLock = new();
    private static PlaybackResources? _playbackResources;
    private static long _requestVersion;
    // Plenty for a highlighted paragraph; an accidental select-all of a huge
    // document would otherwise be synthesized in full, all in memory.
    public const int MaxSpokenChars = 5000;

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
        PlaybackResources? resources;
        lock (PlaybackLock)
        {
            Player.Source = null;
            resources = _playbackResources;
            _playbackResources = null;
        }
        resources?.Dispose();
        Finished?.Invoke();
    }

    public static void Speak(string text, Action? onStarted = null, Action? onFailed = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxSpokenChars) text = text[..MaxSpokenChars];
        var version = Interlocked.Increment(ref _requestVersion);
        _ = SpeakAsync(text, onStarted, onFailed, version);
    }

    public static void Stop()
    {
        Interlocked.Increment(ref _requestVersion);
        if (Player.Source == null) return;
        Player.Pause();
        Release();
    }

    private static async Task SpeakAsync(string text, Action? onStarted, Action? onFailed, long version)
    {
        var resources = new PlaybackResources();
        try
        {
            var list = new MediaPlaybackList();
            await SynthesisGate.WaitAsync();
            try
            {
                if (version != Volatile.Read(ref _requestVersion))
                {
                    resources.Dispose();
                    return;
                }
                foreach (var (runText, voice) in SplitByScript(text))
                {
                    Synth.Voice = voice;
                    var stream = await Synth.SynthesizeTextToStreamAsync(runText);
                    resources.Own(stream);
                    if (version != Volatile.Read(ref _requestVersion))
                    {
                        resources.Dispose();
                        return;
                    }
                    var source = MediaSource.CreateFromStream(stream, stream.ContentType);
                    resources.Own(source);
                    var item = new MediaPlaybackItem(source);
                    list.Items.Add(item);
                }
            }
            finally { SynthesisGate.Release(); }
            if (list.Items.Count == 0)
            {
                resources.Dispose();
                if (version == Volatile.Read(ref _requestVersion))
                {
                    try { onFailed?.Invoke(); }
                    catch { /* A visual callback must not affect speech playback. */ }
                }
                return;
            }

            lock (PlaybackLock)
            {
                if (version != Volatile.Read(ref _requestVersion))
                {
                    resources.Dispose();
                    return;
                }
                Player.Source = null;
                _playbackResources?.Dispose();
                _playbackResources = resources;
                try
                {
                    Player.Source = list;
                    Player.Play();
                }
                catch
                {
                    Player.Source = null;
                    _playbackResources = null;
                    throw;
                }
            }
            try { onStarted?.Invoke(); }
            catch { /* A visual callback must not affect speech playback. */ }
        }
        catch
        {
            resources.Dispose();
            if (version == Volatile.Read(ref _requestVersion))
            {
                try { onFailed?.Invoke(); }
                catch { /* A visual callback must not affect speech playback. */ }
            }
            // No matching voice, no audio device, etc. — reading aloud is a
            // bonus on top of the label already showing the text, not worth
            // surfacing a failure for.
        }
    }

    private sealed class PlaybackResources : IDisposable
    {
        private readonly List<IDisposable> _owned = new();
        private bool _disposed;

        public void Own(IDisposable resource) => _owned.Add(resource);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
            _owned.Clear();
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

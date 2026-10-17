using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.SpeechSynthesis;

namespace CloCloWidget;

public enum ReadAloudVoice
{
    EnglishDavid,
    ArabicHoda,
}

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
    private static readonly object PlaybackLock = new();
    // Speech plays through WASAPI in CloClo's default audio session, the one
    // the azan and reminder sounds use, so Windows lists CloClo once. The
    // WinRT MediaPlayer used before opened a second session of its own.
    private static WasapiPlayer? _player;
    private static PlaybackResources? _playbackResources;
    private static long _requestVersion;
    // Plenty for a highlighted paragraph; an accidental select-all of a huge
    // document would otherwise be synthesized in full, all in memory.
    public const int MaxSpokenChars = 5000;

    // Raised (possibly on a background thread) when reading stops, after the
    // synthesized audio has been let go - a moment to free memory. Every
    // reading ends with it: finished, stopped (even before playback began)
    // or failed. A request replaced by a newer one ends with the newer one.
    public static event Action? Finished;

    // Ends the current reading: the player and the synthesized audio (MBs
    // for a long selection) are let go. With "which", only if that player is
    // still the current one; a replaced reading ends silently.
    private static void Release(WasapiPlayer? which = null)
    {
        WasapiPlayer? player;
        PlaybackResources? resources;
        lock (PlaybackLock)
        {
            if (which != null && !ReferenceEquals(which, _player)) return;
            player = _player;
            resources = _playbackResources;
            _player = null;
            _playbackResources = null;
        }
        DisposePlayer(player);
        resources?.Dispose();
        Finished?.Invoke();
    }

    private static void OnPlaybackStopped(object? sender, StoppedEventArgs e) =>
        Release(sender as WasapiPlayer);

    // Never blocks: PlaybackStopped can run on the playback thread itself.
    private static void DisposePlayer(WasapiPlayer? player)
    {
        if (player == null) return;
        player.PlaybackStopped -= OnPlaybackStopped;
        try { _ = player.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default); }
        catch { /* the output device went away */ }
    }

    public static bool IsVoiceAvailable(ReadAloudVoice voice) => FindVoice(voice) != null;

    public static void Speak(string text, Action? onStarted = null, Action? onFailed = null,
        ReadAloudVoice? selectedVoice = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxSpokenChars) text = text[..MaxSpokenChars];
        var version = Interlocked.Increment(ref _requestVersion);
        _ = SpeakAsync(text, onStarted, onFailed, selectedVoice, version);
    }

    public static void Stop()
    {
        Interlocked.Increment(ref _requestVersion);
        bool playing;
        lock (PlaybackLock) playing = _player != null;
        if (!playing)
        {
            // Nothing playing yet (or still synthesizing): reading is over
            // all the same, and listeners such as the microphone pause for
            // reminders must hear about it.
            Finished?.Invoke();
            return;
        }
        Release();
    }

    private static async Task SpeakAsync(string text, Action? onStarted, Action? onFailed,
        ReadAloudVoice? selectedVoice, long version)
    {
        var resources = new PlaybackResources();
        try
        {
            var parts = new List<ISampleProvider>();
            await SynthesisGate.WaitAsync();
            try
            {
                if (version != Volatile.Read(ref _requestVersion))
                {
                    resources.Dispose();
                    return;
                }
                var runs = selectedVoice is { } choice
                    ? new List<(string Text, VoiceInformation Voice)>
                    {
                        (text, FindVoice(choice) ?? throw new InvalidOperationException("The selected speech voice is unavailable."))
                    }
                    : SplitByScript(text);
                foreach (var (runText, voice) in runs)
                {
                    Synth.Voice = voice;
                    var stream = await Synth.SynthesizeTextToStreamAsync(runText);
                    resources.Own(stream);
                    if (version != Volatile.Read(ref _requestVersion))
                    {
                        resources.Dispose();
                        return;
                    }
                    // The synthesized stream is a WAV file; decode it in memory.
                    var wav = new MemoryStream();
                    using (var read = stream.AsStreamForRead()) await read.CopyToAsync(wav);
                    wav.Position = 0;
                    var reader = new WaveFileReader(wav);
                    resources.Own(reader);
                    parts.Add(reader.ToSampleProvider());
                }
            }
            finally { SynthesisGate.Release(); }
            if (parts.Count == 0)
            {
                resources.Dispose();
                if (version == Volatile.Read(ref _requestVersion))
                {
                    try { onFailed?.Invoke(); }
                    catch { /* A visual callback must not affect speech playback. */ }
                    Finished?.Invoke();
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
                // A newer reading replaces this one without "Finished".
                DisposePlayer(_player);
                _player = null;
                _playbackResources?.Dispose();
                _playbackResources = resources;
                try
                {
                    var player = new WasapiPlayerBuilder().Build();
                    player.PlaybackStopped += OnPlaybackStopped;
                    _player = player;
                    player.Init(new ConcatenatingSampleProvider(ToCommonFormat(parts)).ToWaveProvider());
                    player.Play();
                }
                catch
                {
                    DisposePlayer(_player);
                    _player = null;
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
                Finished?.Invoke();
            }
            // No matching voice, no audio device, etc. — reading aloud is a
            // bonus on top of the label already showing the text, not worth
            // surfacing a failure for.
        }
    }

    // Runs read by different voices can differ in sample rate or channels;
    // they are played back to back in the first run's format.
    private static IEnumerable<ISampleProvider> ToCommonFormat(List<ISampleProvider> parts)
    {
        var rate = parts[0].WaveFormat.SampleRate;
        foreach (var part in parts)
        {
            var mono = part.WaveFormat.Channels == 2 ? part.ToMono() : part;
            yield return mono.WaveFormat.SampleRate == rate ? mono : new WdlResamplingSampleProvider(mono, rate);
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

    private static VoiceInformation? FindVoice(ReadAloudVoice voice)
    {
        var (name, language) = voice switch
        {
            ReadAloudVoice.EnglishDavid => ("Microsoft David", "en"),
            ReadAloudVoice.ArabicHoda => ("Microsoft Hoda", "ar"),
            _ => throw new ArgumentOutOfRangeException(nameof(voice)),
        };
        var matchingLanguage = SpeechSynthesizer.AllVoices.Where(
            v => v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        return matchingLanguage.FirstOrDefault(v =>
                   string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase))
               ?? matchingLanguage.FirstOrDefault(v =>
                   v.DisplayName.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsArabicVoice(VoiceInformation voice) =>
        voice.Language.StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    // Splits into runs of consecutive words sharing a script, each to be
    // synthesized (and played back to back) with the matching voice — so e.g. "مرحبا Ahmad, كيف
    // حالك؟" has "مرحبا" and "كيف حالك؟" read in Arabic and "Ahmad," read in
    // English, rather than the whole line read by whichever voice matched
    // the first Arabic character found anywhere in it.
    private static List<(string Text, VoiceInformation Voice)> SplitByScript(string text)
    {
        // Arabic voices skip Latin-script words entirely, so when Windows'
        // default voice is Arabic (as on a PC set up for Arabic), "Hi, Ahmad."
        // would come out as just "Hi". Latin words use the default voice only
        // when it isn't Arabic, otherwise David or another English voice.
        var windowsDefault = SpeechSynthesizer.DefaultVoice;
        var defaultIsArabic = IsArabicVoice(windowsDefault);
        var arabicVoice = (defaultIsArabic ? windowsDefault : null)
            ?? FindVoice(ReadAloudVoice.ArabicHoda)
            ?? SpeechSynthesizer.AllVoices.FirstOrDefault(IsArabicVoice);
        var defaultVoice = (defaultIsArabic ? null : windowsDefault)
            ?? FindVoice(ReadAloudVoice.EnglishDavid)
            ?? SpeechSynthesizer.AllVoices.FirstOrDefault(
                v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? windowsDefault;

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

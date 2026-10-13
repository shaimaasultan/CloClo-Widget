using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CloCloWidget;

internal enum AudioCaptureSource { Microphone, Playback }

// Captured PCM stays in a bounded memory queue. The local model is loaded only
// for a session and disposed when its toggle is turned off or the window closes.
// Recognition text is emitted as data; this service cannot execute actions.
internal sealed class LocalAudioTranscriber(AudioCaptureSource source, bool reduceMicrophoneNoise = false,
    bool useHighResolutionMicrophone = false, string? recognitionPrompt = null,
    uint? playbackProcessId = null) : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int HighResolutionSampleRate = 48000;
    private const int DictationMaximumSeconds = 15;
    private const int VoiceCommandMaximumSeconds = 8;
    // Whisper returns nothing for less than one second of audio.
    private const int MinimumWhisperSamples = SampleRate * 11 / 10;
    // Decoding hints for dictation. They show Whisper the expected script,
    // spelling, and punctuation; they are never treated as commands.
    private const string EgyptianArabicPrompt = "أهلاً، إزيك؟ النهارده هنتكلم شوية بالعربي.";
    private const string ArabicPrompt = "مرحباً، كيف حالك؟ سنتحدث اليوم باللغة العربية.";
    // Playback is taken as Windows mixes it, 48 kHz float stereo.
    private const int PlaybackSampleRate = 48000;
    // Voice activation keeps at most this much unprocessed audio, dropping
    // the oldest beyond it, and skips phrases that are this far behind.
    private const int VoiceCommandQueueSeconds = 20;
    private const int VoiceCommandStaleSeconds = 2;
    private static readonly object RuntimeLock = new();
    // Whisper.net's manual-load mode binds native imports for the life of the
    // process. Keep these handles alive too; the model and recorder remain
    // scoped to each transcription session.
    private static IntPtr[]? runtimeHandles;
    // Set when the Vulkan runtime set was loaded for a dedicated GPU.
    private static string? gpuName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private volatile PcmQueueStream? _input;
    private volatile WasapiRecorder? _recorder;
    private long _capturedBytes;
    private long _lastProgressBytes;
    private int _recognizedPhrases;
    private int _finishing;
    private volatile bool _reduceMicrophoneNoise = reduceMicrophoneNoise;
    private string? _deviceName;
    private string? _captureFormat;
    private int _captureBytesPerSecond = SampleRate * sizeof(short);
    private string? _engine;

    public event Action<string>? TextRecognized;
    public event Action<int, int, long>? ProgressChanged;
    public event Action<string>? Failed;
    public string? DeviceName => Volatile.Read(ref _deviceName);
    public string? CaptureFormat => Volatile.Read(ref _captureFormat);
    public int CaptureBytesPerSecond => Volatile.Read(ref _captureBytesPerSecond);
    // The model and processor in use, for the editor's status line.
    public string? Engine => Volatile.Read(ref _engine);

    public void SetMicrophoneNoiseReduction(bool enabled)
    {
        if (source == AudioCaptureSource.Microphone)
            _reduceMicrophoneNoise = enabled;
    }

    public Task StartAsync(string languageTag)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = Task.Run(() => RunAsync(languageTag, started));
        return started.Task;
    }

    private async Task RunAsync(string languageTag, TaskCompletionSource started)
    {
        var highResolution = source == AudioCaptureSource.Microphone && useHighResolutionMicrophone;
        var playback = source == AudioCaptureSource.Playback;
        // Both are converted to Whisper's 16 kHz mono here, not by Windows.
        var convertLocally = highResolution || playback;
        // The queue is bounded by bytes, about a minute of audio for dictation
        // and playback. Packet counts would be a poor bound: microphones
        // deliver 5 to 10 ms packets, so 512 of them held under 3 seconds.
        // Voice activation drops its oldest audio instead of failing when
        // decoding falls behind; audio that old can't be a live command.
        var voiceCommands = recognitionPrompt != null;
        using var input = new PcmQueueStream(_stop.Token,
            voiceCommands ? VoiceCommandQueueSeconds * SampleRate * sizeof(short)
            : playback ? 16 * 1024 * 1024 : highResolution ? 8 * 1024 * 1024 : 2 * 1024 * 1024,
            dropOldestWhenFull: voiceCommands);
        _input = input;
        WasapiRecorder? recorder = null;
        try
        {
            EnsureNativeRuntimeLoaded();
            var arabic = languageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
            // Arabic dictation and playback use the large model on a dedicated
            // GPU, where it is about as fast as base on the CPU and makes far
            // fewer mistakes. Everything else, and every PC without such a
            // GPU, keeps the base model on the CPU.
            var useGpu = gpuName != null && arabic && recognitionPrompt == null;
            WhisperFactory factory;
            try
            {
                factory = await CreateFactoryAsync(useGpu ? SpeechModel.LargeV3Turbo : SpeechModel.Base, useGpu);
            }
            catch (Exception ex) when (useGpu && ex is not OperationCanceledException)
            {
                // Out of video memory or a driver failure: stay on the CPU.
                useGpu = false;
                factory = await CreateFactoryAsync(SpeechModel.Base, useGpu: false);
            }
            using var ownedFactory = factory;
            Volatile.Write(ref _engine, useGpu ? $"large-v3-turbo model on {gpuName}" : "base model on the CPU");
            var builder = factory.CreateBuilder().WithLanguage(arabic ? "ar" : "en");
            if (recognitionPrompt != null)
            {
                // Commands are at most 8 seconds, but Whisper encodes a full
                // 30-second window by default. A smaller window (50 frames per
                // second) made English commands about 3x faster with the same
                // matches; Arabic needed 1024 to keep "وقف الصوت" intact. One
                // short segment bounds the work when noise is decoded.
                builder.WithPrompt(recognitionPrompt)
                    .WithAudioContextSize(arabic ? 1024 : 512)
                    .WithSingleSegment()
                    .WithMaxTokensPerSegment(32);
            }
            else
            {
                // Beam search costs more CPU per phrase but clearly improves
                // dictation, Arabic especially. Voice commands stay greedy
                // so wake phrases are answered promptly.
                builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(5));
                if (arabic)
                    builder.WithPrompt(languageTag.Equals("ar-EG", StringComparison.OrdinalIgnoreCase)
                        ? EgyptianArabicPrompt : ArabicPrompt);
            }
            using var processor = builder.Build();
            if (useGpu)
            {
                // The first GPU run prepares its shaders, which can take many
                // seconds (longest after a driver update). Do it while the
                // editor still says Preparing, not on the first spoken phrase.
                await foreach (var _ in processor.ProcessAsync(new float[MinimumWhisperSamples], CancellationToken.None)) { }
            }

            _stop.Token.ThrowIfCancellationRequested();
            void AttachRecorderEvents(WasapiRecorder capture)
            {
                capture.DataAvailable += (buffer, _, _, _) =>
                {
                    if (Volatile.Read(ref _finishing) != 0) return;
                    if (buffer.Length == 0) return;
                    if (buffer.Length > 262144 || !input.TryWrite(buffer))
                    {
                        Failed?.Invoke(recognitionPrompt == null
                            ? "Local transcription cannot keep up with the audio. Turn it off and try again."
                            : "Voice capture could not keep up with the microphone audio.");
                        _stop.Cancel();
                        return;
                    }
                    var captured = Interlocked.Add(ref _capturedBytes, buffer.Length);
                    var last = Interlocked.Read(ref _lastProgressBytes);
                    if (captured - last >= capture.WaveFormat.AverageBytesPerSecond * 5L &&
                        Interlocked.CompareExchange(ref _lastProgressBytes, captured, last) == last)
                        ReportProgress();
                };
                capture.RecordingStopped += (_, _) =>
                {
                    if (_stop.IsCancellationRequested || Volatile.Read(ref _finishing) != 0) return;
                    Failed?.Invoke(source == AudioCaptureSource.Microphone
                        ? "Microphone capture stopped. Check the default input device and microphone permission."
                        : "Playback audio capture stopped. Check the default output device.");
                    _stop.Cancel();
                };
            }

            async Task<WasapiRecorder> BuildRecorderAsync(bool communicationsMode)
            {
                if (playback)
                {
                    // Windows mixes at 48 kHz float. Take that unchanged rather
                    // than Windows' quick conversion to 16 kHz; it is converted
                    // below with the 24-bit microphone mode's resampler.
                    var playbackBuilder = new WasapiRecorderBuilder()
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(PlaybackSampleRate, 2));
                    // With a process id, only that app and its child processes
                    // are captured (per-app loopback, Windows 10 2004 or later),
                    // so notification sounds and other apps are left out.
                    return playbackProcessId is { } processId
                        ? await playbackBuilder.WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync()
                        : playbackBuilder.WithLoopbackCapture().Build();
                }
                var format = highResolution
                    ? new WaveFormat(HighResolutionSampleRate, 24, 1)
                    : new WaveFormat(SampleRate, 16, 1);
                var builder = new WasapiRecorderBuilder().WithFormat(format);
                if (communicationsMode) builder.WithCommunicationsMode();
                // Without loopback the recorder uses the default capture device.
                return builder.Build();
            }

            _stop.Token.ThrowIfCancellationRequested();
            try
            {
                recorder = await BuildRecorderAsync(source == AudioCaptureSource.Microphone);
                AttachRecorderEvents(recorder);
                recorder.StartRecording();
            }
            catch (Exception ex) when (source == AudioCaptureSource.Microphone &&
                                       ex is NotSupportedException or InvalidOperationException &&
                                       !_stop.IsCancellationRequested)
            {
                // Some input devices reject communications processing. Retain
                // the original local capture mode rather than losing dictation.
                Interlocked.Exchange(ref _finishing, 1);
                if (recorder != null) await recorder.DisposeAsync();
                recorder = null;
                Interlocked.Exchange(ref _finishing, 0);
                _stop.Token.ThrowIfCancellationRequested();
                recorder = await BuildRecorderAsync(false);
                AttachRecorderEvents(recorder);
                recorder.StartRecording();
            }
            Volatile.Write(ref _deviceName, recorder.DeviceFriendlyName);
            var captureFormat = recorder.WaveFormat.AsStandardWaveFormat();
            if (highResolution && (captureFormat.SampleRate != HighResolutionSampleRate ||
                                   captureFormat.BitsPerSample != 24 || captureFormat.Channels != 1 ||
                                   captureFormat.Encoding != WaveFormatEncoding.Pcm))
                throw new InvalidDataException("The microphone did not provide 24-bit, 48 kHz mono PCM capture.");
            Volatile.Write(ref _captureBytesPerSecond, captureFormat.AverageBytesPerSecond);
            Volatile.Write(ref _captureFormat,
                $"{captureFormat.BitsPerSample}-bit / {captureFormat.SampleRate / 1000} kHz");
            _recorder = recorder;
            _stop.Token.ThrowIfCancellationRequested();

            // Keep the full-quality stream intact until this managed converter
            // reads it. Channels are averaged to mono, then WDL low-pass
            // resampling produces the 16 kHz mono floats that Whisper requires;
            // no audio or intermediate file leaves memory.
            using var rawStream = convertLocally ? new RawSourceWaveStream(input, captureFormat) : null;
            ISampleProvider? resampler = convertLocally ? ToWhisperInput(rawStream!) : null;
            started.TrySetResult();

            // Each phrase ends at a natural pause, so Whisper never receives
            // half a word. Silence before speech is dropped, which also keeps
            // Whisper from inventing subtitle text out of quiet audio. Voice
            // commands use a shorter pause and cap so they are answered quickly.
            var pcmFrame = new byte[SpeechPhraseReader.FrameSamples * sizeof(short)];
            Func<float[], int> readFrame = resampler != null
                ? frame =>
                {
                    var total = 0;
                    while (total < frame.Length)
                    {
                        var read = resampler!.Read(frame.AsSpan(total));
                        if (read == 0) break;
                        total += read;
                    }
                    return total;
                }
                : frame =>
                {
                    var available = input.Read(pcmFrame, 0, pcmFrame.Length) / sizeof(short);
                    for (var i = 0; i < available; i++)
                        frame[i] = BinaryPrimitives.ReadInt16LittleEndian(pcmFrame.AsSpan(i * 2, 2)) / 32768f;
                    return available;
                };
            var phrases = recognitionPrompt != null
                ? new SpeechPhraseReader(readFrame, SampleRate * VoiceCommandMaximumSeconds,
                    pauseSamples: SampleRate * 2 / 5, minimumPhraseSamples: 0, longPauseSamples: SampleRate * 2 / 5,
                    startThreshold: 0.004f)
                : new SpeechPhraseReader(readFrame, SampleRate * DictationMaximumSeconds,
                    pauseSamples: SampleRate / 2, minimumPhraseSamples: SampleRate * 3, longPauseSamples: SampleRate * 6 / 5,
                    startThreshold: source == AudioCaptureSource.Playback ? 0.002f : 0.004f);
            // Only microphone audio can use this optional in-memory filter.
            // Keep its state across Whisper chunks to avoid clicks at boundaries.
            MicrophoneNoiseFilter? noiseFilter = null;
            while (!_stop.IsCancellationRequested)
            {
                var (samples, voicedSamples, endOfStream, hitLimit) = phrases.ReadPhrase();
                if (samples.Length == 0) break;
                // A cough or click alone is skipped; Whisper tends to turn very
                // short sounds into invented words. A confirmed voice command
                // may be a single short word.
                var worthDecoding = voiceCommands
                    // A wake phrase or command is short and ends at a pause.
                    // Sound that runs into the length limit (TV, music) and
                    // phrases left behind while an earlier one was decoding
                    // can't be a live command, so they skip Whisper.
                    ? voicedSamples >= SampleRate / 10 && !hitLimit &&
                      input.QueuedBytes <= VoiceCommandStaleSeconds * CaptureBytesPerSecond
                    : voicedSamples >= SampleRate / 5;
                if (worthDecoding)
                {
                    if (source == AudioCaptureSource.Microphone && _reduceMicrophoneNoise)
                        noiseFilter ??= new MicrophoneNoiseFilter(SampleRate, arabic ? 0.6f : 0.35f);
                    else
                        noiseFilter = null;
                    noiseFilter?.Process(samples);
                    if (samples.Length < MinimumWhisperSamples) Array.Resize(ref samples, MinimumWhisperSamples);
                    // Whisper.net can end its result enumeration on cancellation
                    // before the native processing task has finished. Disposing the
                    // processor/model at that point can leave native code using
                    // freed memory. Let this bounded chunk finish before cleanup.
                    string? previousText = null;
                    await foreach (var segment in processor.ProcessAsync(samples, CancellationToken.None))
                    {
                        if (_stop.IsCancellationRequested) continue;
                        var text = TranscriptCleanup.Clean(segment.Text);
                        // A segment repeated within one phrase is Whisper looping,
                        // not the speaker repeating themselves.
                        if (text.Length == 0 || text == previousText ||
                            TranscriptCleanup.IsLikelyHallucination(text)) continue;
                        previousText = text;
                        Interlocked.Increment(ref _recognizedPhrases);
                        TextRecognized?.Invoke(text);
                        ReportProgress();
                    }
                }
                if (endOfStream) break;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!started.TrySetException(ex) && !_stop.IsCancellationRequested)
                Failed?.Invoke($"Local transcription stopped ({ex.GetType().Name}).");
        }
        finally
        {
            _recorder = null;
            input.Complete();
            if (recorder != null)
            {
                try { recorder.StopRecording(); } catch { /* output device may have gone away */ }
                try { await recorder.DisposeAsync(); } catch { /* output device may have gone away */ }
            }
            _input = null;
            if (!started.Task.IsCompleted) started.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var recorder = _recorder;
        if (source == AudioCaptureSource.Microphone && recorder != null)
        {
            Interlocked.Exchange(ref _finishing, 1);
            try { recorder.StopRecording(); } catch { /* input device may have gone away */ }
            _input?.Complete();
        }
        else _stop.Cancel();
        if (_worker != null) await _worker;
        _stop.Dispose();
    }

    private static ISampleProvider ToWhisperInput(WaveStream raw)
    {
        var samples = raw.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = samples.ToMono(0.5f, 0.5f);
        else if (samples.WaveFormat.Channels != 1)
            throw new InvalidDataException("The audio device did not provide mono or stereo capture.");
        return samples.WaveFormat.SampleRate == SampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, SampleRate);
    }

    private async Task<WhisperFactory> CreateFactoryAsync(SpeechModel model, bool useGpu)
    {
        var modelBytes = await PlaybackModelStore.ReadVerifiedBytesAsync(model, _stop.Token);
        return WhisperFactory.FromBuffer(modelBytes,
            new WhisperFactoryOptions { UseGpu = useGpu, UseFlashAttention = useGpu });
    }

    private static readonly string[] CpuRuntime =
        ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];
    private static readonly string[] GpuRuntime =
        ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-vulkan-whisper.dll", "ggml-whisper.dll", "whisper.dll"];

    private static void EnsureNativeRuntimeLoaded()
    {
        lock (RuntimeLock)
        {
            if (runtimeHandles != null) return;

            // Both sets use the same file names, and Windows binds an import
            // to an already-loaded module of that name, so one set is chosen
            // for the life of the process. The Vulkan set in gpu\ also has a
            // CPU backend, so base-model sessions keep working on it.
            var gpuDirectory = Path.Combine(AppContext.BaseDirectory, "gpu");
            if (Directory.Exists(gpuDirectory) && PlaybackModelStore.IsInstalled(SpeechModel.LargeV3Turbo) &&
                VulkanGpuProbe.FindDedicatedGpu() is { } gpu)
            {
                try
                {
                    // Hybrid laptops list the integrated GPU first, and the
                    // runtime would use it. Restrict it to the probed device.
                    NativeEnvironment.Set("GGML_VK_VISIBLE_DEVICES", gpu.Index.ToString(CultureInfo.InvariantCulture));
                    runtimeHandles = LoadRuntime(gpuDirectory, VulkanGpuProbe.LoaderPath, GpuRuntime);
                    RuntimeOptions.LoadedLibrary = RuntimeLibrary.Vulkan;
                    gpuName = gpu.Name;
                    return;
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileNotFoundException)
                {
                    // An unusable Vulkan driver: fall back to the CPU set.
                }
            }
            runtimeHandles = LoadRuntime(AppContext.BaseDirectory, null, CpuRuntime);
            RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu;
        }
    }

    // The signed install keeps each pinned set in a fixed folder. Whisper.net's
    // automatic loader searches runtimes/win-x64 instead, so load the exact set
    // in dependency order and use its supported manual-load mode.
    // NativeLibraryCheck has verified and held each installed DLL open before
    // this code runs.
    private static IntPtr[] LoadRuntime(string directory, string? systemLibrary, string[] names)
    {
        var loaded = new List<IntPtr>();
        try
        {
            // vulkan-1.dll by its System32 path first, so the GPU runtime's
            // import binds to it instead of a same-named file elsewhere.
            if (systemLibrary != null) loaded.Add(NativeLibrary.Load(systemLibrary));
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path))
                    throw new FileNotFoundException("The local speech runtime is missing a file.", path);
                loaded.Add(NativeLibrary.Load(path));
            }

            // Manual mode imports "libwhisper", while the runtime package
            // calls the actual Windows file "whisper.dll". Resolve only
            // that import to the exact absolute-path handle loaded above.
            // The resolver can be set once, so only after a complete load.
            var whisperHandle = loaded[^1];
            NativeLibrary.SetDllImportResolver(typeof(WhisperFactory).Assembly,
                (libraryName, _, _) =>
                    libraryName == "libwhisper" ? whisperHandle : IntPtr.Zero);
            return loaded.ToArray();
        }
        catch
        {
            for (var i = loaded.Count - 1; i >= 0; i--) NativeLibrary.Free(loaded[i]);
            throw;
        }
    }

    private void ReportProgress() => ProgressChanged?.Invoke(
        Volatile.Read(ref _recognizedPhrases), 0, Interlocked.Read(ref _capturedBytes));

    public ValueTask AbortAsync()
    {
        // A completed command does not need buffered microphone audio. Native
        // Whisper processing still finishes before the model is disposed.
        _stop.Cancel();
        return DisposeAsync();
    }

    // Groups 16 kHz audio into phrases using 20 ms frame energy. A phrase
    // starts at the first loud frame (with a short lead-in so the first
    // consonant is kept) and ends at a pause: pauseSamples once it is
    // minimumPhraseSamples long, or longPauseSamples at any length. Speech
    // longer than maximumSamples is cut at the quietest frame of its last
    // 1.5 seconds and the rest begins the next phrase, so a long sentence is
    // not split in the middle of a word.
    private sealed class SpeechPhraseReader(Func<float[], int> readFrame, int maximumSamples,
        int pauseSamples, int minimumPhraseSamples, int longPauseSamples, float startThreshold)
    {
        public const int FrameSamples = SampleRate / 50;
        private const int LeadInSamples = SampleRate / 4;
        private const int CutSearchSamples = SampleRate * 3 / 2;
        private readonly float[] _frame = new float[FrameSamples];
        private float[] _carry = [];
        private float _noiseFloor = 0.0015f;

        public (float[] Samples, int VoicedSamples, bool EndOfStream, bool HitLimit) ReadPhrase()
        {
            var captured = new float[maximumSamples];
            var count = 0;
            var voiced = 0;
            var started = false;
            var endOfStream = false;
            var hitLimit = false;
            var leadIn = new float[LeadInSamples];
            var leadInCount = 0;
            var quietSamples = 0;
            var bestCut = -1;
            var bestCutRms = float.MaxValue;

            if (_carry.Length > 0)
            {
                // The remainder of a phrase cut at the length limit is speech.
                Array.Copy(_carry, captured, _carry.Length);
                count = voiced = _carry.Length;
                started = true;
                _carry = [];
            }

            while (true)
            {
                var available = readFrame(_frame);
                if (available == 0) { endOfStream = true; break; }
                var energy = 0f;
                for (var i = 0; i < available; i++) energy += _frame[i] * _frame[i];
                var rms = MathF.Sqrt(energy / available);
                var loud = rms >= MathF.Max(startThreshold, _noiseFloor * 3);

                if (!started)
                {
                    if (!loud)
                    {
                        _noiseFloor = Math.Clamp(_noiseFloor * 0.98f + rms * 0.02f, 0.0005f, 0.02f);
                        if (leadInCount + available > leadIn.Length)
                        {
                            var shift = leadInCount + available - leadIn.Length;
                            Array.Copy(leadIn, shift, leadIn, 0, leadInCount - shift);
                            leadInCount -= shift;
                        }
                        Array.Copy(_frame, 0, leadIn, leadInCount, available);
                        leadInCount += available;
                        if (available < FrameSamples) { endOfStream = true; break; }
                        continue;
                    }
                    Array.Copy(leadIn, captured, leadInCount);
                    count = leadInCount;
                    started = true;
                }

                if (count + available > captured.Length)
                {
                    var cut = bestCut > 0 ? bestCut : count;
                    _carry = new float[count - cut + available];
                    Array.Copy(captured, cut, _carry, 0, count - cut);
                    Array.Copy(_frame, 0, _carry, count - cut, available);
                    count = cut;
                    hitLimit = true;
                    break;
                }

                Array.Copy(_frame, 0, captured, count, available);
                count += available;
                if (loud) voiced += available;
                if (count >= captured.Length - CutSearchSamples && rms < bestCutRms)
                {
                    bestCutRms = rms;
                    bestCut = count;
                }
                quietSamples = rms < MathF.Max(startThreshold * 0.75f, _noiseFloor * 2) ? quietSamples + available : 0;
                if (quietSamples >= longPauseSamples ||
                    (quietSamples >= pauseSamples && count >= minimumPhraseSamples)) break;
                if (available < FrameSamples) { endOfStream = true; break; }
            }

            if (!started) return ([], 0, endOfStream, false);
            Array.Resize(ref captured, count);
            return (captured, Math.Min(voiced, count), endOfStream, hitLimit);
        }
    }

    private sealed class PcmQueueStream(CancellationToken cancellationToken, int maxQueuedBytes,
        bool dropOldestWhenFull) : Stream
    {
        // A sanity bound only; maxQueuedBytes is the real limit.
        private const int MaxPackets = 65536;
        private readonly BlockingCollection<byte[]> _packets = new(MaxPackets);
        private byte[]? _current;
        private int _offset;
        private int _queuedBytes;

        // Audio captured but not yet read by the phrase reader.
        public int QueuedBytes => Volatile.Read(ref _queuedBytes);

        public bool TryWrite(ReadOnlySpan<byte> pcm)
        {
            if (_packets.IsAddingCompleted) return true;
            var total = Interlocked.Add(ref _queuedBytes, pcm.Length);
            try
            {
                while (total > maxQueuedBytes && dropOldestWhenFull && _packets.TryTake(out var oldest))
                    total = Interlocked.Add(ref _queuedBytes, -oldest.Length);
                if (total > maxQueuedBytes)
                {
                    Interlocked.Add(ref _queuedBytes, -pcm.Length);
                    return false;
                }
                if (_packets.TryAdd(pcm.ToArray())) return true;
                Interlocked.Add(ref _queuedBytes, -pcm.Length);
                return false;
            }
            catch (InvalidOperationException)
            {
                Interlocked.Add(ref _queuedBytes, -pcm.Length);
                return true; // capture raced with shutdown
            }
        }
        public void Complete() => _packets.CompleteAdding();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                if (_current == null || _offset == _current.Length)
                {
                    try { _current = _packets.Take(cancellationToken); }
                    catch (InvalidOperationException) { break; }
                    Interlocked.Add(ref _queuedBytes, -_current.Length);
                    _offset = 0;
                }
                var length = Math.Min(count - total, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + total, length);
                _offset += length;
                total += length;
            }
            return total;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _packets.Dispose();
            base.Dispose(disposing);
        }
    }
}

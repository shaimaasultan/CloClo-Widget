using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    bool useHighResolutionMicrophone = false, string? recognitionPrompt = null) : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int HighResolutionSampleRate = 48000;
    private const int ChunkSeconds = 10;
    private const int VoiceCommandFallbackChunkSeconds = 3;
    private const int VoiceCommandMaximumSeconds = 8;
    private const int MaxQueuedPackets = 512;
    private static readonly object RuntimeLock = new();
    // Whisper.net's manual-load mode binds native imports for the life of the
    // process. Keep these handles alive too; the model and recorder remain
    // scoped to each transcription session.
    private static IntPtr[]? runtimeHandles;
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

    public event Action<string>? TextRecognized;
    public event Action<int, int, long>? ProgressChanged;
    public event Action<string>? Failed;
    public string? DeviceName => Volatile.Read(ref _deviceName);
    public string? CaptureFormat => Volatile.Read(ref _captureFormat);
    public int CaptureBytesPerSecond => Volatile.Read(ref _captureBytesPerSecond);

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
        using var input = new PcmQueueStream(MaxQueuedPackets, _stop.Token,
            highResolution ? 8 * 1024 * 1024 : 2 * 1024 * 1024);
        _input = input;
        WasapiRecorder? recorder = null;
        try
        {
            var modelBytes = await PlaybackModelStore.ReadVerifiedBytesAsync(_stop.Token);
            EnsureNativeRuntimeLoaded();
            using var factory = WhisperFactory.FromBuffer(modelBytes);
            var builder = factory.CreateBuilder()
                .WithLanguage(languageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en");
            if (recognitionPrompt != null) builder.WithPrompt(recognitionPrompt);
            using var processor = builder.Build();

            _stop.Token.ThrowIfCancellationRequested();
            void AttachRecorderEvents(WasapiRecorder capture)
            {
                capture.DataAvailable += (buffer, _, _, _) =>
                {
                    if (Volatile.Read(ref _finishing) != 0) return;
                    if (buffer.Length == 0) return;
                    if (buffer.Length > 65536 || !input.TryWrite(buffer))
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

            WasapiRecorder BuildRecorder(bool communicationsMode)
            {
                var format = highResolution
                    ? new WaveFormat(HighResolutionSampleRate, 24, 1)
                    : new WaveFormat(SampleRate, 16, 1);
                var builder = new WasapiRecorderBuilder().WithFormat(format);
                if (source == AudioCaptureSource.Playback) builder.WithLoopbackCapture();
                else if (communicationsMode) builder.WithCommunicationsMode();
                // Without loopback the recorder uses the default capture device.
                return builder.Build();
            }

            _stop.Token.ThrowIfCancellationRequested();
            try
            {
                recorder = BuildRecorder(source == AudioCaptureSource.Microphone);
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
                recorder = BuildRecorder(false);
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

            // Keep the 24-bit stream intact until this managed converter reads
            // it. WDL low-pass resampling produces the 16 kHz mono floats that
            // Whisper requires; no audio or intermediate file leaves memory.
            using var rawStream = highResolution ? new RawSourceWaveStream(input, captureFormat) : null;
            ISampleProvider? resampler = highResolution
                ? new WdlResamplingSampleProvider(new Pcm24BitToSampleProvider(rawStream!), SampleRate)
                : null;
            started.TrySetResult();

            // Wake phrases and short commands should reach the matcher promptly.
            // Dictation and playback keep the longer context that helps accuracy.
            var chunkSeconds = recognitionPrompt == null ? ChunkSeconds : VoiceCommandFallbackChunkSeconds;
            var pcm = new byte[SampleRate * 2 * chunkSeconds];
            // Only microphone audio can use this optional in-memory filter.
            // Keep its state across Whisper chunks to avoid clicks at boundaries.
            MicrophoneNoiseFilter? noiseFilter = null;
            while (!_stop.IsCancellationRequested)
            {
                float[] samples;
                int sampleCount;
                var voiceCommandEndOfStream = false;
                if (recognitionPrompt != null && resampler == null)
                {
                    (samples, voiceCommandEndOfStream) = ReadVoiceCommandPhrase(input);
                    sampleCount = samples.Length;
                    if (sampleCount == 0) break;
                }
                else if (resampler != null)
                {
                    samples = new float[SampleRate * chunkSeconds];
                    sampleCount = 0;
                    while (sampleCount < samples.Length)
                    {
                        var read = resampler.Read(samples.AsSpan(sampleCount));
                        if (read == 0) break;
                        sampleCount += read;
                    }
                    if (sampleCount == 0) break;
                    if (sampleCount != samples.Length) Array.Resize(ref samples, sampleCount);
                }
                else
                {
                    var count = input.Read(pcm, 0, pcm.Length);
                    if (count == 0) break;
                    sampleCount = count / sizeof(short);
                    samples = new float[sampleCount];
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
                }
                if (recognitionPrompt == null && sampleCount < SampleRate * chunkSeconds)
                {
                    // A stopped microphone still has the last spoken phrase.
                    // Send only the captured samples; padding the rest of our
                    // audio window adds silence, not speech context.
                    // Very short fragments are skipped to avoid hallucinations.
                    // A confirmed voice command may be shorter than a second.
                    var minimumSamples = recognitionPrompt == null ? SampleRate : SampleRate / 2;
                    if (source != AudioCaptureSource.Microphone || sampleCount < minimumSamples) break;
                }
                if (source == AudioCaptureSource.Microphone && _reduceMicrophoneNoise)
                    noiseFilter ??= new MicrophoneNoiseFilter(SampleRate);
                else
                    noiseFilter = null;
                noiseFilter?.Process(samples);
                // Whisper.net can end its result enumeration on cancellation
                // before the native processing task has finished. Disposing the
                // processor/model at that point can leave native code using
                // freed memory. Let this bounded chunk finish before cleanup.
                await foreach (var segment in processor.ProcessAsync(samples, CancellationToken.None))
                {
                    if (_stop.IsCancellationRequested) continue;
                    var text = segment.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    Interlocked.Increment(ref _recognizedPhrases);
                    TextRecognized?.Invoke(text);
                    ReportProgress();
                }
                if (voiceCommandEndOfStream ||
                    (recognitionPrompt == null && sampleCount < SampleRate * chunkSeconds)) break;
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

    private static void EnsureNativeRuntimeLoaded()
    {
        lock (RuntimeLock)
        {
            if (runtimeHandles != null) return;

            // The signed install keeps these four pinned DLLs beside the exe.
            // Whisper.net's automatic loader searches runtimes/win-x64 instead,
            // so load this exact set in dependency order and use its supported
            // manual-load mode. NativeLibraryCheck has verified and held each
            // installed DLL open before this code runs.
            string[] names =
            [
                "ggml-base-whisper.dll", "ggml-cpu-whisper.dll",
                "ggml-whisper.dll", "whisper.dll"
            ];
            var loaded = new IntPtr[names.Length];
            var count = 0;
            try
            {
                foreach (var name in names)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, name);
                    if (!File.Exists(path))
                        throw new FileNotFoundException("The local speech runtime is missing a file.", path);
                    loaded[count++] = NativeLibrary.Load(path);
                }

                // Manual mode imports "libwhisper", while the runtime package
                // calls the actual Windows file "whisper.dll". Resolve only
                // that import to the exact absolute-path handle loaded above.
                var whisperHandle = loaded[^1];
                NativeLibrary.SetDllImportResolver(typeof(WhisperFactory).Assembly,
                    (libraryName, _, _) =>
                        libraryName == "libwhisper" ? whisperHandle : IntPtr.Zero);
                RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu;
                runtimeHandles = loaded;
            }
            catch
            {
                runtimeHandles = null;
                for (var i = count - 1; i >= 0; i--)
                    if (loaded[i] != IntPtr.Zero) NativeLibrary.Free(loaded[i]);
                throw;
            }
        }
    }

    private void ReportProgress() => ProgressChanged?.Invoke(
        Volatile.Read(ref _recognizedPhrases), 0, Interlocked.Read(ref _capturedBytes));

    private static (float[] Samples, bool EndOfStream) ReadVoiceCommandPhrase(PcmQueueStream input)
    {
        // Read microphone PCM in 20 ms frames. A short pause ends the current
        // phrase so Whisper can recognize "now" without waiting for a fixed
        // five-second window. The bounded maximum handles continuous noise.
        const int frameSamples = SampleRate / 50;
        const int trailingSilenceSamples = SampleRate * 2 / 5;
        var frame = new byte[frameSamples * sizeof(short)];
        var leadIn = new float[SampleRate / 5];
        var leadInCount = 0;
        var captured = new float[SampleRate * VoiceCommandMaximumSeconds];
        var count = 0;
        var quietSamples = 0;
        var noiseFloor = 0.0015f;
        var started = false;
        var endOfStream = false;

        while (count < captured.Length)
        {
            var bytes = input.Read(frame, 0, frame.Length);
            if (bytes == 0) { endOfStream = true; break; }
            var available = bytes / sizeof(short);
            var energy = 0f;
            for (var i = 0; i < available; i++)
            {
                var value = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i * 2, 2)) / 32768f;
                energy += value * value;
            }
            var rms = MathF.Sqrt(energy / available);
            if (!started)
            {
                if (rms < MathF.Max(0.004f, noiseFloor * 3))
                {
                    noiseFloor = Math.Clamp(noiseFloor * 0.98f + rms * 0.02f, 0.0005f, 0.02f);
                    if (leadInCount + available > leadIn.Length)
                    {
                        var shift = leadInCount + available - leadIn.Length;
                        Array.Copy(leadIn, shift, leadIn, 0, leadInCount - shift);
                        leadInCount -= shift;
                    }
                    for (var i = 0; i < available; i++)
                        leadIn[leadInCount++] = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i * 2, 2)) / 32768f;
                    continue;
                }
                Array.Copy(leadIn, captured, leadInCount);
                count = leadInCount;
                started = true;
            }

            var toCopy = Math.Min(available, captured.Length - count);
            for (var i = 0; i < toCopy; i++)
                captured[count++] = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i * 2, 2)) / 32768f;
            quietSamples = rms < MathF.Max(0.003f, noiseFloor * 2)
                ? quietSamples + toCopy : 0;
            if (quietSamples >= trailingSilenceSamples) break;
            if (bytes < frame.Length) { endOfStream = true; break; }
        }

        if (!started) return ([], endOfStream);
        Array.Resize(ref captured, count);
        return (captured, endOfStream);
    }

    public ValueTask AbortAsync()
    {
        // A completed command does not need buffered microphone audio. Native
        // Whisper processing still finishes before the model is disposed.
        _stop.Cancel();
        return DisposeAsync();
    }

    private sealed class PcmQueueStream(int capacity, CancellationToken cancellationToken, int maxQueuedBytes) : Stream
    {
        private readonly BlockingCollection<byte[]> _packets = new(capacity);
        private byte[]? _current;
        private int _offset;
        private int _queuedBytes;

        public bool TryWrite(ReadOnlySpan<byte> pcm)
        {
            if (_packets.IsAddingCompleted) return true;
            var total = Interlocked.Add(ref _queuedBytes, pcm.Length);
            if (total > maxQueuedBytes)
            {
                Interlocked.Add(ref _queuedBytes, -pcm.Length);
                return false;
            }
            try
            {
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

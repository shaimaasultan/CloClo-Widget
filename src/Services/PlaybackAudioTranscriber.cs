using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CloCloWidget;

// Playback PCM stays in a bounded memory queue. The local model is loaded only
// for a session and disposed when the toggle is turned off or the window closes.
internal sealed class PlaybackAudioTranscriber : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int ChunkSeconds = 10;
    private const int MaxQueuedPackets = 512;
    private static readonly object RuntimeLock = new();
    // Whisper.net's manual-load mode binds native imports for the life of the
    // process. Keep these handles alive too; the model and recorder remain
    // scoped to each transcription session.
    private static IntPtr[]? runtimeHandles;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private long _capturedBytes;
    private long _lastProgressBytes;
    private int _recognizedPhrases;

    public event Action<string>? TextRecognized;
    public event Action<int, int, long>? ProgressChanged;
    public event Action<string>? Failed;

    public Task StartAsync(string languageTag)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = Task.Run(() => RunAsync(languageTag, started));
        return started.Task;
    }

    private async Task RunAsync(string languageTag, TaskCompletionSource started)
    {
        using var input = new PcmQueueStream(MaxQueuedPackets, _stop.Token);
        WasapiRecorder? recorder = null;
        try
        {
            var modelBytes = await PlaybackModelStore.ReadVerifiedBytesAsync(_stop.Token);
            EnsureNativeRuntimeLoaded();
            using var factory = WhisperFactory.FromBuffer(modelBytes);
            using var processor = factory.CreateBuilder()
                .WithLanguage(languageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en")
                .Build();

            recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithFormat(new WaveFormat(SampleRate, 16, 1))
                .Build();
            recorder.DataAvailable += (buffer, _, _, _) =>
            {
                if (buffer.Length == 0) return;
                if (buffer.Length > 65536 || !input.TryWrite(buffer))
                {
                    Failed?.Invoke("Playback transcription cannot keep up with the audio. Turn it off and try again.");
                    _stop.Cancel();
                    return;
                }
                var captured = Interlocked.Add(ref _capturedBytes, buffer.Length);
                var last = Interlocked.Read(ref _lastProgressBytes);
                if (captured - last >= SampleRate * 2 * 5 &&
                    Interlocked.CompareExchange(ref _lastProgressBytes, captured, last) == last)
                    ReportProgress();
            };
            recorder.RecordingStopped += (_, _) =>
            {
                if (_stop.IsCancellationRequested) return;
                Failed?.Invoke("Playback audio capture stopped. Check the default output device.");
                _stop.Cancel();
            };
            recorder.StartRecording();
            started.TrySetResult();

            var pcm = new byte[SampleRate * 2 * ChunkSeconds];
            while (!_stop.IsCancellationRequested)
            {
                if (input.Read(pcm, 0, pcm.Length) != pcm.Length) break;
                var samples = new float[SampleRate * ChunkSeconds];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
                await foreach (var segment in processor.ProcessAsync(samples, _stop.Token))
                {
                    var text = segment.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    Interlocked.Increment(ref _recognizedPhrases);
                    TextRecognized?.Invoke(text);
                    ReportProgress();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!started.TrySetException(ex) && !_stop.IsCancellationRequested)
                Failed?.Invoke($"Local playback transcription stopped ({ex.GetType().Name}).");
        }
        finally
        {
            input.Complete();
            if (recorder != null)
            {
                try { recorder.StopRecording(); } catch { /* output device may have gone away */ }
                try { await recorder.DisposeAsync(); } catch { /* output device may have gone away */ }
            }
            if (!started.Task.IsCompleted) started.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
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

    private sealed class PcmQueueStream(int capacity, CancellationToken cancellationToken) : Stream
    {
        private const int MaxQueuedBytes = 2 * 1024 * 1024;
        private readonly BlockingCollection<byte[]> _packets = new(capacity);
        private byte[]? _current;
        private int _offset;
        private int _queuedBytes;

        public bool TryWrite(ReadOnlySpan<byte> pcm)
        {
            if (_packets.IsAddingCompleted) return true;
            var total = Interlocked.Add(ref _queuedBytes, pcm.Length);
            if (total > MaxQueuedBytes)
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

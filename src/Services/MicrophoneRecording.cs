using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace CloCloWidget;

// One microphone recording into memory, at 16 kHz mono, for the voice test
// pages. Nothing is written to disk.
internal sealed class MicrophoneRecording : IAsyncDisposable
{
    // The longest voice test records for six seconds. Keep capture bounded
    // even if the UI thread is busy and its stop timer is delayed.
    private const int MaxRecordingSamples = 6 * VoiceSamples.SampleRate;
    private readonly WasapiRecorder _recorder;
    private readonly List<float> _samples = new();
    private readonly object _lock = new();
    // The microphone clicks as it starts (full-scale samples in the first
    // few ms); the first 100 ms are dropped so the click never counts as the
    // loudest part of a word.
    private int _skip = VoiceSamples.SampleRate / 10;
    private int _listening;

    // Raised once, on the capture thread, when sound is actually coming in -
    // the moment to say "speak now". Speaking before it is cut off.
    public event Action? Listening;

    private MicrophoneRecording(WasapiRecorder recorder)
    {
        _recorder = recorder;
        recorder.DataAvailable += (buffer, _, _, _) =>
        {
            lock (_lock)
            {
                for (var i = 0; i + 1 < buffer.Length; i += 2)
                {
                    if (_skip > 0) { _skip--; continue; }
                    if (_samples.Count >= MaxRecordingSamples) break;
                    _samples.Add(BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(i, 2)) / 32768f);
                }
                if (_skip > 0 || Interlocked.Exchange(ref _listening, 1) != 0) return;
            }
            Listening?.Invoke();
        };
    }

    // The default microphone; Windows converts it to 16 kHz mono.
    public static MicrophoneRecording Start()
    {
        var recorder = new WasapiRecorderBuilder().WithFormat(new WaveFormat(VoiceSamples.SampleRate, 16, 1)).Build();
        var recording = new MicrophoneRecording(recorder);
        recorder.StartRecording();
        return recording;
    }

    public float[] Snapshot()
    {
        lock (_lock) return _samples.ToArray();
    }

    public async Task<float[]> StopAsync()
    {
        await DisposeAsync();
        return Snapshot();
    }

    public async ValueTask DisposeAsync()
    {
        try { await _recorder.DisposeAsync(); }
        catch { /* already stopped */ }
    }
}

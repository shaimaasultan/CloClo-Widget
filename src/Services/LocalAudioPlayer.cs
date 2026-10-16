using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CloCloWidget;

// Plays only a user-selected local audio file. It never invokes a shell,
// follows a URL, or treats recognized speech or settings text as a command.
public sealed class LocalAudioPlayer : IDisposable
{
    private const long MaxAudioBytes = 30L * 1024 * 1024;
    // WPF's MediaPlayer, used before, played at half volume by default.
    // Keep that loudness; the Windows mixer still controls CloClo's volume.
    private const float PlaybackGain = 0.5f;
    // WasapiPlayer joins CloClo's default audio session every time. WPF's
    // MediaPlayer created a new session for each file it opened, so each
    // azan or reminder added another "CloClo" row to the Windows mixer.
    private WasapiPlayer? _player;
    private MediaFoundationReader? _reader;
    private bool _disposed;
    public bool IsPlaying { get; private set; }
    // Raised on the creating (UI) thread when a file ends or fails, not
    // after Stop.
    public event Action? Finished;

    public static bool IsAllowedLocalAudioFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            return (full.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                    full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) &&
                   string.Equals(full, path, StringComparison.OrdinalIgnoreCase) &&
                   new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                   PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryPlay(string? path)
    {
        if (_disposed || !IsPlayable(path)) return false;
        try
        {
            var file = new FileInfo(path!);
            Stop();
            _reader = new MediaFoundationReader(file.FullName);
            var gain = new VolumeSampleProvider(_reader.ToSampleProvider()) { Volume = PlaybackGain };
            _player = new WasapiPlayerBuilder().Build();
            _player.PlaybackStopped += OnPlaybackStopped;
            _player.Init(gain.ToWaveProvider());
            IsPlaying = true;
            _player.Play();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or COMException)
        {
            Stop();
            return false;
        }
    }

    public static bool IsPlayable(string? path)
    {
        if (!IsAllowedLocalAudioFile(path)) return false;
        try
        {
            var file = new FileInfo(path!);
            return file.Exists && file.Length > 0 && file.Length <= MaxAudioBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        IsPlaying = false;
        var player = _player;
        var reader = _reader;
        _player = null;
        _reader = null;
        if (player != null)
        {
            // Detach first: a deliberate stop is not "finished".
            player.PlaybackStopped -= OnPlaybackStopped;
            try { player.Dispose(); } catch (COMException) { /* output device went away */ }
        }
        reader?.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Posted to the UI thread; ignore a player that was replaced since.
        if (!ReferenceEquals(sender, _player)) return;
        Stop();
        Finished?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        Finished = null;
        _disposed = true;
    }
}

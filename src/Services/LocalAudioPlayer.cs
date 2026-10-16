using System;
using System.IO;
using System.Windows.Media;

namespace CloCloWidget;

// Plays only a user-selected local audio file. It never invokes a shell,
// follows a URL, or treats recognized speech or settings text as a command.
public sealed class LocalAudioPlayer : IDisposable
{
    private const long MaxAudioBytes = 30L * 1024 * 1024;
    private readonly MediaPlayer _player = new();
    private bool _disposed;
    public bool IsPlaying { get; private set; }
    public event Action? Finished;

    public LocalAudioPlayer()
    {
        _player.MediaEnded += OnFinished;
        _player.MediaFailed += OnFailed;
    }

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
            _player.Open(new Uri(file.FullName, UriKind.Absolute));
            IsPlaying = true;
            _player.Play();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException)
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
        _player.Stop();
        _player.Close();
    }

    private void OnFinished(object? sender, EventArgs e)
    {
        Stop();
        Finished?.Invoke();
    }

    private void OnFailed(object? sender, ExceptionEventArgs e)
    {
        Stop();
        Finished?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _player.MediaEnded -= OnFinished;
        _player.MediaFailed -= OnFailed;
        Finished = null;
        _disposed = true;
    }
}

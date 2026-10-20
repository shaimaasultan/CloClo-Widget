using System;
using System.Threading;

namespace CloCloWidget;

// One CloClo on the whole PC. The first copy owns a named mutex; a second
// copy - from a shortcut, the Start Menu, "Start with Windows" or a double-
// click on the exe, under any file name or folder - finds it taken, signals
// the running copy to show itself (it may have been hidden from the tray) and
// exits without opening anything, so it never loads a speech model.
//
// The mutex lives in the Global\ namespace, so it is shared by every Windows
// session: while one user's CloClo runs, another signed-in user (or the same
// user in a Remote Desktop session) can't start a second one. The name is a
// constant here, never read from the registry, an environment variable or a
// setting - anything that can be configured can be changed to slip past it.
// Keep it the same in every version.
//
// The show event stays in the session's Local\ namespace: only a copy in the
// same session can bring the widget up. A copy in another session (or a non-
// elevated copy while an elevated one runs) can't open the mutex at all -
// Windows denies it access to an object another user created - which counts
// as "already running" too.
//
// If a running copy crashes, Windows releases its mutex (the next copy gets
// it as "abandoned" and simply takes over), so a crash never blocks the next
// start.
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\CloClo.Widget.SingleInstance";
    private const string ShowEventName = @"Local\CloClo.Widget.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequested;
    private RegisteredWaitHandle? _listener;
    private volatile bool _held = true;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequested)
    {
        _mutex = mutex;
        _showRequested = showRequested;
    }

    // True from TryAcquire until Dispose. Anything that loads a speech model
    // checks it first (see LocalAudioTranscriber.CreateFactoryAsync).
    public bool IsHeld => _held;

    // Null when another copy is already running, in this session or another.
    public static SingleInstance? TryAcquire()
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, created by another user's (or an elevated) copy.
            return null;
        }
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous copy ended without releasing it (a crash) - this
            // copy owns it now.
        }
        var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return new SingleInstance(mutex, showRequested);
    }

    // Asks the copy that's already running to show itself. False when it
    // runs in another session (or can't be reached), so it can't be shown.
    public static bool SignalRunningCopy()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showRequested))
            {
                using (showRequested) showRequested.Set();
                return true;
            }
        }
        catch
        {
            // Nothing more to do - the running copy stays as it is.
        }
        return false;
    }

    // Called by the running copy: onShow runs (on a thread-pool thread) each
    // time a second copy was started.
    public void ListenForShowRequests(Action onShow)
    {
        _listener = ThreadPool.RegisterWaitForSingleObject(_showRequested, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _held = false;
        _listener?.Unregister(null);
        _showRequested.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned any more */ }
        _mutex.Dispose();
    }
}

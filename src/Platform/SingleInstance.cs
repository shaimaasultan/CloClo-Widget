using System;
using System.Threading;

namespace CloCloWidget;

// One CloClo per Windows session. The first copy owns a named mutex; a second
// copy - from a shortcut, the Start Menu, "Start with Windows" or a double-
// click on the exe - finds it taken, signals the running copy to show itself
// (it may have been hidden from the tray) and exits without opening anything.
//
// The names live in the session's Local\ namespace, so other signed-in users
// each get their own CloClo. If a running copy crashes, Windows releases its
// mutex (the next copy gets it as "abandoned" and simply takes over), so a
// crash never blocks the next start.
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CloClo.Widget.SingleInstance";
    private const string ShowEventName = @"Local\CloClo.Widget.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequested;
    private RegisteredWaitHandle? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequested)
    {
        _mutex = mutex;
        _showRequested = showRequested;
    }

    // Null when another copy is already running.
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
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

    // Asks the copy that's already running to show itself.
    public static void SignalRunningCopy()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showRequested))
            {
                using (showRequested) showRequested.Set();
            }
        }
        catch
        {
            // Nothing more to do - the running copy stays as it is.
        }
    }

    // Called by the running copy: onShow runs (on a thread-pool thread) each
    // time a second copy was started.
    public void ListenForShowRequests(Action onShow)
    {
        _listener = ThreadPool.RegisterWaitForSingleObject(_showRequested, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        _showRequested.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned any more */ }
        _mutex.Dispose();
    }
}

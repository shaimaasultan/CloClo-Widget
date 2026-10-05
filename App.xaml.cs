using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace CloCloWidget;

public partial class App : System.Windows.Application
{
    // A widget shouldn't vanish because one menu action failed — and a
    // crash would also hand Windows Error Reporting a memory dump that can
    // hold clipboard, selection or notification text. Errors from the UI
    // thread are logged and the widget keeps running; the log records only
    // the exception type and stack trace, never its message (which can
    // quote file paths or text).
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception);
            e.SetObserved();
        };
        // An exception on a background thread can't be caught - the runtime
        // ends the process. Ending it here first, after logging, means no
        // Windows Error Reporting crash dump gets written.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogError(ex);
            if (e.IsTerminating) TerminateProcess(GetCurrentProcess(), 1);
        };
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    private static readonly object LogLock = new();
    private const long MaxLogBytes = 256 * 1024;

    private static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloCloWidget");
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "errors.log");
            lock (LogLock)
            {
                if (File.Exists(log) && new FileInfo(log).Length > MaxLogBytes) File.Delete(log);
                File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best-effort; never let it throw from a handler.
        }
    }
}


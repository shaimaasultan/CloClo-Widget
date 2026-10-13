using System;
using System.Runtime.CompilerServices;

namespace CloCloWidget;

// A hand-written entry point instead of the one WPF generates from
// App.xaml, so the extracted native DLLs can be verified (see
// NativeLibraryCheck.cs) before anything in WPF gets a chance to load them.
public static class Program
{
    // This copy's hold on "the one running CloClo" (see SingleInstance.cs).
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ProcessHardening.Apply(NativeLibraryCheck.ExtractionDirectories());
        if (!NativeLibraryCheck.EnsureTrusted()) return 1;

        // A sign-in launch waits without holding the single-instance mutex.
        // A manual launch can start immediately while this one is waiting.
        if (args.Length == 1 && args[0] == StartupRegistration.SecurityWaitArgument)
            StartupReadiness.WaitForSecurityServices();

        // Taken only after the checks: their "re-extract and restart once"
        // path starts a fresh copy, which then needs to be the one that runs.
        Instance = SingleInstance.TryAcquire();
        if (Instance == null)
        {
            SingleInstance.SignalRunningCopy();
            return 0;
        }
        try
        {
            return Run();
        }
        finally
        {
            Instance.Dispose();
        }
    }

    // Kept out of Main — and never inlined into it — so that compiling Main
    // can't touch any WPF type before the check above has run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

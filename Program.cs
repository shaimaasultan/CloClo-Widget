using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
            // In another session the running copy can't be brought up here,
            // so say why nothing opens.
            if (!SingleInstance.SignalRunningCopy())
                MessageBoxW(IntPtr.Zero,
                    "CloClo is already running on this PC - for another Windows user, or as administrator. Only one copy can run at a time.",
                    "CloClo", MB_OK | MB_ICONINFORMATION);
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

    // user32's own message box: no WPF type is touched before Run.
    private const uint MB_OK = 0x0, MB_ICONINFORMATION = 0x40;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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

using System;
using System.Runtime.CompilerServices;

namespace CloCloWidget;

// A hand-written entry point instead of the one WPF generates from
// App.xaml, so the extracted native DLLs can be verified (see
// NativeLibraryCheck.cs) before anything in WPF gets a chance to load them.
public static class Program
{
    [STAThread]
    public static int Main()
    {
        if (!NativeLibraryCheck.EnsureTrusted()) return 1;
        return Run();
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

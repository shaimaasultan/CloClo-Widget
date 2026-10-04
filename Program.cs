using System;

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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

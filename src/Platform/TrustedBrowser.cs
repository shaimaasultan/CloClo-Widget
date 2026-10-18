using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace CloCloWidget;

// Opens a web link from a trending list in Microsoft Edge from Program Files,
// after checking Edge's Microsoft signature - the same path the "YouTube"
// voice command uses. It never goes through the shell or a per-user protocol
// handler, so another program can't step in as "the browser". The link must
// be an absolute https:// URL; it is passed as one argument, and since it
// starts with "https://" Edge can't read it as a command-line switch.
internal static class TrustedBrowser
{
    public static string? OpenHttps(string url)
    {
        var safe = TrendsService.SafeHttpsUrl(url);
        if (safe == null) return "Only https:// links can be opened.";
        try
        {
            foreach (var folder in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var edge = Path.Combine(folder, "Microsoft", "Edge", "Application", "msedge.exe");
                if (!File.Exists(edge)) continue;
                if (!NativeLibraryCheck.IsTrustedMicrosoftExecutable(edge))
                    return "Microsoft Edge's signature could not be verified.";
                var start = new ProcessStartInfo(edge) { UseShellExecute = false };
                start.ArgumentList.Add(safe);
                using var process = Process.Start(start);
                return process == null ? "Windows did not start Microsoft Edge." : null;
            }
            return "Microsoft Edge was not found in Program Files.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"Could not open the link ({ex.GetType().Name}).";
        }
    }
}

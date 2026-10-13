using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CloCloWidget;

internal enum ApprovedVoiceApp { Notepad, Paint, OneDrive }

// Speech is never interpreted as a path, argument, URI, or shell command.
// Only these fixed phrases can select a fixed app, and the widget asks the
// user for confirmation before any activation.
internal static class ApprovedVoiceAppLauncher
{
    internal const string RecognitionPrompt = "CloClo. CloClo open Notepad now. CloClo open Paint now. CloClo open OneDrive now. CloClo stop voice now.";
    private static readonly string[] WakeNames = ["cloclo", "clo clo", "clock low", "clock lo", "clo low"];

    internal static bool IsWakePhrase(string transcript) =>
        TryStripWakeName(Normalize(transcript), out var remainder) && remainder.Length == 0;

    internal static bool StartsWithWakeWord(string transcript)
    {
        return TryStripWakeName(Normalize(transcript), out var remainder) && remainder.Length != 0;
    }

    internal static bool HasFinishWord(string transcript) =>
        Normalize(transcript).EndsWith(" now", StringComparison.Ordinal);

    internal static bool IsStopVoiceCommand(string transcript) =>
        TryStripWakeName(Normalize(transcript), out var remainder) &&
        remainder == "stop voice now";

    internal static bool TryMatchFinishedCommand(string transcript, out ApprovedVoiceApp app)
    {
        app = default;
        var phrase = Normalize(transcript);
        if (phrase.Length > 160 || !phrase.EndsWith(" now", StringComparison.Ordinal)) return false;
        if (!TryStripWakeName(phrase, out var remainder) ||
            remainder.Length <= 4 || !remainder.EndsWith(" now", StringComparison.Ordinal)) return false;
        return TryMatch(remainder[..^4], out app);
    }

    private static bool TryStripWakeName(string phrase, out string remainder)
    {
        foreach (var name in WakeNames)
        {
            if (phrase == name)
            {
                remainder = "";
                return true;
            }
            if (phrase.StartsWith(name + " ", StringComparison.Ordinal))
            {
                remainder = phrase[(name.Length + 1)..];
                return true;
            }
        }
        remainder = "";
        return false;
    }

    internal static string DisplayName(ApprovedVoiceApp app) => app switch
    {
        ApprovedVoiceApp.Notepad => "Notepad",
        ApprovedVoiceApp.Paint => "Paint",
        ApprovedVoiceApp.OneDrive => "OneDrive",
        _ => throw new ArgumentOutOfRangeException(nameof(app))
    };

    internal static bool TryMatch(string transcript, out ApprovedVoiceApp app)
    {
        app = default;
        if (transcript.Length > 160) return false;
        var phrase = Normalize(transcript);
        foreach (var verb in new[] { "open ", "launch ", "start " })
        {
            if (!phrase.StartsWith(verb, StringComparison.Ordinal)) continue;
            phrase = phrase[verb.Length..];
            break;
        }
        foreach (var suffix in new[] { " application", " app" })
        {
            if (!phrase.EndsWith(suffix, StringComparison.Ordinal)) continue;
            phrase = phrase[..^suffix.Length];
            break;
        }
        switch (phrase)
        {
            case "notepad" or "note pad" or "not bad" or "note bad":
                app = ApprovedVoiceApp.Notepad; return true;
            case "paint" or "paintbrush" or "paint brush" or "bent brush":
                app = ApprovedVoiceApp.Paint; return true;
            case "onedrive" or "one drive":
                app = ApprovedVoiceApp.OneDrive; return true;
            default: return false;
        }
    }

    private static string Normalize(string transcript)
    {
        if (transcript.Length > 160) return "";
        var words = new char[transcript.Length];
        for (var i = 0; i < transcript.Length; i++)
        {
            var c = char.ToLowerInvariant(transcript[i]);
            words[i] = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : ' ';
        }
        return string.Join(' ', new string(words).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string? Launch(ApprovedVoiceApp app)
    {
        try
        {
            switch (app)
            {
                case ApprovedVoiceApp.Notepad:
                    return ActivatePackagedApp("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
                case ApprovedVoiceApp.Paint:
                    return ActivatePackagedApp("Microsoft.Paint_8wekyb3d8bbwe!App");
                case ApprovedVoiceApp.OneDrive:
                    foreach (var folder in new[] {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                    {
                        var path = Path.Combine(folder, "Microsoft OneDrive", "OneDrive.exe");
                        if (File.Exists(path)) return StartTrustedExecutable(path);
                    }
                    return "OneDrive was not found in Program Files.";
                default:
                    return "That app is not allowed.";
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
                                   or COMException or InvalidOperationException or ArgumentException)
        {
            return $"Could not open {DisplayName(app)} ({ex.GetType().Name}).";
        }
    }

    private static string? StartTrustedExecutable(string path)
    {
        if (!File.Exists(path) || !NativeLibraryCheck.IsTrustedMicrosoftExecutable(path))
            return "The Microsoft app is missing or its signature could not be verified.";
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
        return process == null ? "Windows did not start the app." : null;
    }

    private static string? ActivatePackagedApp(string appUserModelId)
    {
        // Callers supply only fixed Microsoft package identities, avoiding a
        // per-user app alias or protocol handler that another program could replace.
        var activationType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var manager = Activator.CreateInstance(activationType)!;
        try
        {
            var result = ((IApplicationActivationManager)manager).ActivateApplication(
                appUserModelId, null, 0, out _);
            Marshal.ThrowExceptionForHR(result);
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CloCloWidget;

internal enum ApprovedVoiceApp { Notepad, Paint, OneDrive, SnippingTool, Settings, YouTube, Spotify, Word }

// Speech is never interpreted as a path, argument, URI, or shell command.
// Only these fixed names can select a fixed app.
internal static class ApprovedVoiceAppLauncher
{
    // Phrases are compared after TranscriptCleanup.MatchingKey, so Arabic
    // entries here use its unified spelling (ا for أ/إ/آ, ه for ة, ي for ى).
    // CloClo's name, a verb and "now" are tolerated around a held name.
    private static readonly string[] WakeNames =
        ["cloclo", "clo clo", "كلوكلو", "كلو كلو"];
    private static readonly string[] CommandVerbs =
        ["open ", "launch ", "start ", "افتح لي ", "افتحلي ", "افتح ", "شغل "];
    private static readonly string[] FinishWords = ["now", "دلوقتي", "دلوقت", "الان"];

    // Hold to talk: the user says only the app name ("Paint", "Paintbrush",
    // "YouTube", "وورد"); the held mouse button is the intent. An "open" verb,
    // the wake name or a finish word is tolerated but not needed. The prompt
    // lists only the names, which steers Whisper toward their spelling.
    internal const string RecognitionPrompt =
        "Notepad. Paint. Paintbrush. OneDrive. Snipping Tool. Settings. YouTube. Spotify. Word. Shut down. Close all. Force close. Cancel.";
    internal const string ArabicRecognitionPrompt =
        "المفكرة. الرسام. ون درايف. أداة القص. الإعدادات. يوتيوب. سبوتيفاي. وورد. اقفل الجهاز. اقفلهم. اقفلهم غصب. إلغاء.";

    // Shutting down can't be undone like opening an app, so only these exact
    // phrases count (after the same wake name and "now" tolerance); a sound-
    // alike never shuts the PC down.
    private static readonly string[] ShutdownPhrases =
    [
        "shut down", "shutdown", "shut it down", "power off", "turn off",
        "shut down the computer", "shut down computer", "shut down the pc", "shut down pc",
        "shut down the laptop", "shut down laptop", "shut down windows",
        "turn off the computer", "turn off computer", "turn off the pc", "turn off pc",
        "turn off the laptop", "turn off laptop",
        "اقفل الجهاز", "قفل الجهاز", "اطفي الجهاز", "طفي الجهاز", "اغلق الجهاز",
        "اقفل الكمبيوتر", "قفل الكمبيوتر", "اطفي الكمبيوتر", "طفي الكمبيوتر",
        "اقفل اللابتوب", "اطفي اللابتوب", "ايقاف التشغيل",
    ];
    private static readonly string[] CancelPhrases =
    [
        "cancel", "cancel it", "cancel shutdown", "cancel the shutdown", "stop", "stop it", "don t", "no",
        "الغاء", "الغي", "الغيه", "وقف", "لا", "بلاش",
    ];

    // Answers while a shutdown waits for open apps to be closed.
    private static readonly string[] CloseAppsPhrases =
    [
        "close", "close all", "close them", "close them all", "close all apps", "close the apps", "close apps",
        "yes", "yes close", "confirm", "ok", "okay",
        "اقفل", "اقفلهم", "اقفلها", "اقفل الكل", "اقفل كله", "اقفل البرامج", "ايوه", "اه", "تمام", "موافق", "اكد",
    ];
    private static readonly string[] ForceClosePhrases =
    [
        "force close", "force close all", "force close them", "force", "close anyway", "close without saving",
        "اقفلهم غصب", "اقفل غصب", "اقفلها غصب", "اقفل من غير حفظ", "اقفلهم من غير حفظ", "اقفل بالقوه",
    ];

    internal static bool IsCloseAppsCommand(string transcript) =>
        Array.IndexOf(CloseAppsPhrases, CommandPhrase(transcript)) >= 0;

    internal static bool IsForceCloseCommand(string transcript) =>
        Array.IndexOf(ForceClosePhrases, CommandPhrase(transcript)) >= 0;

    internal static bool IsShutdownCommand(string transcript) =>
        Array.IndexOf(ShutdownPhrases, CommandPhrase(transcript)) >= 0;

    internal static bool IsCancelCommand(string transcript) =>
        Array.IndexOf(CancelPhrases, CommandPhrase(transcript)) >= 0;

    private static string CommandPhrase(string transcript)
    {
        var phrase = Normalize(transcript);
        if (TryStripWakeName(phrase, out var remainder)) phrase = remainder;
        if (TryStripFinishWord(phrase, out var command)) phrase = command;
        foreach (var word in new[] { "please ", "لو سمحت ", "من فضلك " })
            if (phrase.StartsWith(word, StringComparison.Ordinal)) phrase = phrase[word.Length..];
        foreach (var word in new[] { " please", " لو سمحت", " من فضلك" })
            if (phrase.EndsWith(word, StringComparison.Ordinal)) phrase = phrase[..^word.Length];
        return phrase;
    }

    internal static bool TryMatchHeldCommand(string transcript, out ApprovedVoiceApp app)
    {
        app = default;
        var phrase = Normalize(transcript);
        if (TryStripWakeName(phrase, out var remainder)) phrase = remainder;
        if (TryStripFinishWord(phrase, out var command)) phrase = command;
        foreach (var verb in CommandVerbs)
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
        // What Whisper writes for silence or a breath is never a name.
        if (phrase.Length == 0 || phrase is "thank you" or "thanks" or "you" or "bye" or "شكرا") return false;
        // Exact names and known mishearings first, then a looser sound match:
        // only a name is expected here, so "ward" can be Word and "bent"
        // Paint. Two apps sounding equally close still match neither.
        return TryMatch("open " + phrase, out app) || TryMatchBySound(phrase, out app, loose: true);
    }

    private static bool TryStripFinishWord(string phrase, out string command)
    {
        foreach (var word in FinishWords)
        {
            if (phrase.Length <= word.Length + 1 || !phrase.EndsWith(" " + word, StringComparison.Ordinal)) continue;
            command = phrase[..^(word.Length + 1)];
            return true;
        }
        command = "";
        return false;
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
        ApprovedVoiceApp.SnippingTool => "Snipping Tool",
        ApprovedVoiceApp.Settings => "Settings",
        ApprovedVoiceApp.YouTube => "YouTube",
        ApprovedVoiceApp.Spotify => "Spotify",
        ApprovedVoiceApp.Word => "Word",
        _ => throw new ArgumentOutOfRangeException(nameof(app))
    };

    internal static string ArabicDisplayName(ApprovedVoiceApp app) => app switch
    {
        ApprovedVoiceApp.Notepad => "المفكرة",
        ApprovedVoiceApp.Paint => "الرسام",
        ApprovedVoiceApp.OneDrive => "ون درايف",
        ApprovedVoiceApp.SnippingTool => "أداة القص",
        ApprovedVoiceApp.Settings => "الإعدادات",
        ApprovedVoiceApp.YouTube => "يوتيوب",
        ApprovedVoiceApp.Spotify => "سبوتيفاي",
        ApprovedVoiceApp.Word => "وورد",
        _ => throw new ArgumentOutOfRangeException(nameof(app))
    };

    internal static bool TryMatch(string transcript, out ApprovedVoiceApp app)
    {
        app = default;
        if (transcript.Length > 160) return false;
        var phrase = Normalize(transcript);
        var hasVerb = false;
        foreach (var verb in CommandVerbs)
        {
            if (!phrase.StartsWith(verb, StringComparison.Ordinal)) continue;
            phrase = phrase[verb.Length..];
            hasVerb = true;
            break;
        }
        if (!hasVerb) return false;
        foreach (var suffix in new[] { " application", " app" })
        {
            if (!phrase.EndsWith(suffix, StringComparison.Ordinal)) continue;
            phrase = phrase[..^suffix.Length];
            break;
        }
        foreach (var prefix in new[] { "برنامج ", "تطبيق " })
        {
            if (!phrase.StartsWith(prefix, StringComparison.Ordinal)) continue;
            phrase = phrase[prefix.Length..];
            break;
        }
        switch (phrase)
        {
            case "notepad" or "note pad" or "not bad" or "note bad" or
                 "المفكره" or "مفكره" or "نوت باد" or "نوتباد":
                app = ApprovedVoiceApp.Notepad; return true;
            case "paint" or "paintbrush" or "paint brush" or "bent brush" or
                 "الرسام" or "رسام" or "بينت" or "باينت":
                app = ApprovedVoiceApp.Paint; return true;
            case "onedrive" or "one drive" or "ون درايف" or "وان درايف" or "ون دريف":
                app = ApprovedVoiceApp.OneDrive; return true;
            case "snipping tool" or "sniping tool" or "اداه القص" or "اداه قص" or "سنيبنج تول":
                app = ApprovedVoiceApp.SnippingTool; return true;
            case "settings" or "windows settings" or "الاعدادات" or "اعدادات":
                app = ApprovedVoiceApp.Settings; return true;
            case "youtube" or "you tube" or "u tube" or "يوتيوب" or "اليوتيوب" or "يو تيوب":
                app = ApprovedVoiceApp.YouTube; return true;
            case "spotify" or "spot if i" or "سبوتيفاي" or "سبوتيفي" or "سبوتفاي":
                app = ApprovedVoiceApp.Spotify; return true;
            case "word" or "microsoft word" or "وورد" or "الوورد" or "مايكروسوفت وورد":
                app = ApprovedVoiceApp.Word; return true;
            default: return TryMatchBySound(phrase, out app);
        }
    }

    // Whisper spells app names inconsistently ("nodepad", "notepadd", "one
    // drive"). Names are also compared by a sound key; only these fixed names
    // can match.
    private static readonly (ApprovedVoiceApp App, string Name)[] SpokenNames =
    [
        (ApprovedVoiceApp.Notepad, "notepad"), (ApprovedVoiceApp.Notepad, "المفكره"),
        (ApprovedVoiceApp.Notepad, "نوت باد"),
        (ApprovedVoiceApp.Paint, "paint"), (ApprovedVoiceApp.Paint, "paintbrush"),
        (ApprovedVoiceApp.Paint, "الرسام"),
        (ApprovedVoiceApp.Paint, "باينت"),
        (ApprovedVoiceApp.OneDrive, "onedrive"), (ApprovedVoiceApp.OneDrive, "ون درايف"),
        (ApprovedVoiceApp.SnippingTool, "snipping tool"), (ApprovedVoiceApp.SnippingTool, "اداه القص"),
        (ApprovedVoiceApp.Settings, "settings"), (ApprovedVoiceApp.Settings, "الاعدادات"),
        (ApprovedVoiceApp.YouTube, "youtube"), (ApprovedVoiceApp.YouTube, "يوتيوب"),
        (ApprovedVoiceApp.Spotify, "spotify"), (ApprovedVoiceApp.Spotify, "سبوتيفاي"),
        (ApprovedVoiceApp.Word, "word"), (ApprovedVoiceApp.Word, "وورد"),
    ];

    private static bool TryMatchBySound(string phrase, out ApprovedVoiceApp app, bool loose = false)
    {
        app = default;
        foreach (var brand in new[] { "microsoft ", "windows ", "مايكروسوفت " })
        {
            if (!phrase.StartsWith(brand, StringComparison.Ordinal)) continue;
            phrase = phrase[brand.Length..];
            break;
        }
        var key = SoundKey(phrase);
        if (key.Length < 2) return false;
        var best = int.MaxValue;
        var tied = false;
        foreach (var (candidate, name) in SpokenNames)
        {
            var target = SoundKey(name);
            // Short names must sound the same; longer ones may differ by
            // one sound (two for the longest), as in "sniping tool". A bare
            // held name lets short ones differ by one too ("ward" for Word).
            var allowed = target.Length >= 7 ? 2 : target.Length >= 5 ? 1 : loose && target.Length >= 3 ? 1 : 0;
            var distance = EditDistance(key, target);
            if (distance > allowed || distance > best) continue;
            if (distance == best && candidate != app) tied = true;
            else if (distance < best) tied = false;
            best = distance;
            app = candidate;
        }
        // Two different apps sounding equally close is not a choice.
        return best != int.MaxValue && !tied;
    }

    // A rough sound key for MatchingKey text, English or Arabic: letters that
    // Whisper confuses share one symbol, vowels after the first letter are
    // dropped, word gaps are ignored and repeated sounds count once.
    private static string SoundKey(string phrase)
    {
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            if (words[i].Length > 3 && words[i].StartsWith("ال", StringComparison.Ordinal)) words[i] = words[i][2..];
        var text = string.Concat(words);
        var key = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var first = key.Length == 0;
            char sound = c switch
            {
                'a' or 'e' or 'i' or 'o' or 'u' => first ? 'a' : '\0',
                'h' or 'w' or 'y' => first ? c : '\0',
                'p' when next == 'h' => 'f',
                'c' when next is 'e' or 'i' or 'y' => 's',
                'b' => 'p',
                'd' => 't',
                'c' or 'g' or 'k' or 'q' => 'k',
                'v' => 'f',
                'z' => 's',
                'x' => 'k',
                'ا' or 'ع' or 'ء' => first ? 'ا' : '\0',
                'و' or 'ي' => first ? c : '\0',
                'ط' => 'ت',
                'ض' => 'د',
                'ظ' or 'ذ' => 'ز',
                'ص' or 'ث' => 'س',
                'ق' => 'ك',
                'ح' => 'ه',
                _ => c,
            };
            if (sound == '\0' || (key.Length > 0 && key[^1] == sound)) continue;
            key.Append(sound);
            if (c == 'x') key.Append('s');
        }
        return key.ToString();
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1,
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static string Normalize(string transcript) =>
        transcript.Length > 160 ? "" : TranscriptCleanup.MatchingKey(transcript);

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
                case ApprovedVoiceApp.SnippingTool:
                    return ActivatePackagedApp("Microsoft.ScreenSketch_8wekyb3d8bbwe!App");
                case ApprovedVoiceApp.Settings:
                    return ActivatePackagedApp("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
                case ApprovedVoiceApp.Spotify:
                    // Only the Store package has a fixed identity; the desktop
                    // installer puts an unsigned-by-Microsoft exe in a folder the
                    // user (and any program running as them) can replace.
                    try { return ActivatePackagedApp("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"); }
                    catch (COMException) { return "Spotify from the Microsoft Store is not installed."; }
                case ApprovedVoiceApp.YouTube:
                    foreach (var folder in new[] {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
                    {
                        var path = Path.Combine(folder, "Microsoft", "Edge", "Application", "msedge.exe");
                        if (File.Exists(path)) return StartTrustedExecutable(path, "https://www.youtube.com/");
                    }
                    return "Microsoft Edge was not found in Program Files.";
                case ApprovedVoiceApp.Word:
                    foreach (var folder in new[] {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                    {
                        // Click-to-Run (Microsoft 365, retail) and MSI (volume
                        // licensed) Office use different fixed folders.
                        foreach (var path in new[] {
                            Path.Combine(folder, "Microsoft Office", "root", "Office16", "WINWORD.EXE"),
                            Path.Combine(folder, "Microsoft Office", "Office16", "WINWORD.EXE") })
                            if (File.Exists(path)) return StartTrustedExecutable(path);
                    }
                    return "Microsoft Word was not found in Program Files.";
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

    private static string? StartTrustedExecutable(string path, string? fixedArgument = null)
    {
        if (!File.Exists(path) || !NativeLibraryCheck.IsTrustedMicrosoftExecutable(path))
            return "The Microsoft app is missing or its signature could not be verified.";
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        if (fixedArgument != null) start.ArgumentList.Add(fixedArgument);
        using var process = Process.Start(start);
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

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace CloCloWidget;

// One-shot voice dictation via Windows' own speech recognition, appended to
// a running text file rather than shown ephemerally in the label — nothing
// to copy/paste out by hand. Each language needs its own installed Windows
// Speech Recognition pack, which turned out to be a separate feature from
// text-to-speech voices (a machine can have Arabic TTS without Arabic
// speech recognition, or vice versa) — checked and handled explicitly
// rather than assumed, since SpeechReader.cs already hit a similar gap.
public static class SpeechToText
{
    public const string EnglishLanguageTag = "en-US";
    public const string ArabicLanguageTag = "ar-SA";

    public static async Task<(string? Text, string? Error)> RecognizeAsync(string languageTag)
    {
        var language = new Language(languageTag);
        var installed = SpeechRecognizer.SupportedTopicLanguages.Any(l => l.LanguageTag == language.LanguageTag);
        if (!installed)
        {
            return (null, $"{language.DisplayName} speech recognition isn't installed. Add it in " +
                "Settings > Time & language > Language & region.");
        }

        try
        {
            using var recognizer = new SpeechRecognizer(language);
            await recognizer.CompileConstraintsAsync();
            var result = await recognizer.RecognizeAsync();
            if (result.Status != SpeechRecognitionResultStatus.Success || string.IsNullOrWhiteSpace(result.Text))
            {
                return (null, "Didn't catch that — try again.");
            }
            return (result.Text, null);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80045509))
        {
            // Distinct from a plain mic-permission problem: Windows requires
            // accepting its online speech recognition privacy policy at
            // least once (Settings > Privacy & security > Speech) before
            // ANY app can use SpeechRecognizer at all — found by hitting
            // this exact failure while testing, not documented anywhere
            // obvious.
            return (null, "Windows' speech recognition isn't set up yet — go to Settings > Privacy & security " +
                "> Speech and turn on \"Online speech recognition\", then try again.");
        }
        catch (Exception ex)
        {
            return (null, $"Microphone unavailable — check Settings > Privacy & security > Microphone. ({ex.Message})");
        }
    }

    // Per-user and private: %LocalAppData%\CloCloWidget\Dictation. Not
    // Documents — Controlled Folder Access (Windows' ransomware protection)
    // blocks unrecognized apps from writing there by default — and not next
    // to the exe either, which fails under Program Files and can leak
    // dictated text into whatever synced or shared folder the exe sits in.
    // LocalAppData is neither protected by CFA nor roamed/synced.
    public static string DictationFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloCloWidget", "Dictation");

    // settings.json only ever points dictation inside DictationFolder — a
    // path anywhere else (an older build's location, or a hand-edited
    // settings file) gets a fresh file here instead of being appended to.
    public static bool IsInDictationFolder([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var folder = Path.GetFullPath(DictationFolder) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // The new file must not exist yet (CreateNew): the name is predictable,
    // so something else could have put a file or a link there first to have
    // dictation written - or an existing file emptied - somewhere else. A
    // name that's taken (also by a second new file within the same second,
    // which used to empty the first) gets a number added instead.
    public static string CreateNewFile()
    {
        Directory.CreateDirectory(DictationFolder);
        var stamp = $"Dictation-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(DictationFolder, n == 1 ? $"{stamp}.txt" : $"{stamp}-{n}.txt");
            try
            {
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
                return path;
            }
            catch (IOException) when (n < 100 && Path.Exists(path))
            {
                // Taken - try the next number.
            }
        }
    }

    // The current dictation file is only appended to while it's still a plain
    // file: one replaced by a link since it was created gets a new file instead.
    public static bool IsPlainFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Append(string filePath, string text)
    {
        Directory.CreateDirectory(DictationFolder);
        File.AppendAllText(filePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
    }
}

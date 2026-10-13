using System;
using System.Text;

namespace CloCloWidget;

// Local text cleanup for Whisper segments. It only rewrites or drops text;
// it never turns recognized words into an action.
internal static class TranscriptCleanup
{
    private const char Tatweel = 'ـ';

    // Text Whisper is known to invent from silence, music, or noise: subtitle
    // credits and channel outros learned from its training data. A segment
    // containing one of these is dropped whatever else it contains.
    private static readonly string[] HallucinationMarkers =
    [
        "نانسي قنقر", "amara org", "اشتركوا في القناه", "اشترك في القناه",
        "لا تنسوا الاشتراك", "subtitles by",
    ];

    // Dropped only when they make up the whole segment, since each can also
    // be said deliberately inside a longer sentence.
    private static readonly string[] HallucinationSegments =
    [
        "ترجمه", "شكرا للمشاهده", "شكرا على المشاهده", "شكرا لكم على المشاهده",
        "موسيقى", "تصفيق", "thanks for watching", "thank you for watching",
        "thank you so much for watching", "music",
    ];

    // Removes tatweel and repeated diacritics, uses Arabic punctuation after
    // Arabic words, and tidies spaces before punctuation. Text without
    // Arabic letters only has its spacing tidied.
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var output = new StringBuilder(text.Length);
        var lastLetterIsArabic = false;
        foreach (var raw in text.Trim())
        {
            var c = char.IsWhiteSpace(raw) ? ' ' : raw;
            if (c == Tatweel) continue;
            if (IsDiacritic(c) && output.Length > 0 && output[^1] == c) continue;
            if (IsArabicLetter(c)) lastLetterIsArabic = true;
            else if (char.IsLetter(c)) lastLetterIsArabic = false;
            if (lastLetterIsArabic)
                c = c switch { ',' => '،', '?' => '؟', ';' => '؛', _ => c };
            if (IsClosingPunctuation(c))
                while (output.Length > 0 && output[^1] == ' ') output.Length--;
            if (c == ' ' && output.Length > 0 && output[^1] == ' ') continue;
            output.Append(c);
        }
        return output.ToString().Trim();
    }

    public static bool IsLikelyHallucination(string text)
    {
        var trimmed = text.Trim();
        // "[Music]", "(تصفيق)", "♪ ... ♪": sound descriptions, not speech.
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '[' && trimmed[^1] == ']') || (trimmed[0] == '(' && trimmed[^1] == ')') ||
             (trimmed[0] == '♪' && trimmed[^1] == '♪')))
            return true;
        var key = MatchingKey(trimmed);
        if (key.Length == 0) return true;
        foreach (var marker in HallucinationMarkers)
            if ((" " + key + " ").Contains(" " + marker + " ", StringComparison.Ordinal)) return true;
        return Array.IndexOf(HallucinationSegments, key) >= 0;
    }

    // Lowercase ASCII letters and digits plus Arabic letters with common
    // spelling variants unified (أ/إ/آ→ا, ى→ي, ة→ه, ؤ→و, ئ→ي) and
    // diacritics removed. Everything else separates words.
    public static string MatchingKey(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            if (IsDiacritic(raw) || raw == Tatweel) continue;
            var c = char.ToLowerInvariant(raw) switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                >= '٠' and <= '٩' and var digit => (char)('0' + (digit - '٠')),
                var other => other,
            };
            output.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' || IsArabicLetter(c) ? c : ' ');
        }
        return string.Join(' ', output.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool ContainsArabic(string text)
    {
        foreach (var c in text)
            if (IsArabicLetter(c)) return true;
        return false;
    }

    private static bool IsArabicLetter(char c) => c is >= 'ء' and <= 'غ' or >= 'ف' and <= 'ي';
    private static bool IsDiacritic(char c) => c is >= 'ً' and <= 'ْ' or 'ٰ';
    private static bool IsClosingPunctuation(char c) => c is '.' or ',' or '!' or '?' or ':' or ';' or '،' or '؟' or '؛';
}

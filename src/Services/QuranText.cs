using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace CloCloWidget;

public sealed record Surah(int Number, int AyahCount, string ArabicName, string Name, string Meaning);

// The Quran text (Tanzil Uthmani 1.1), surah names (Tanzil metadata) and
// Saheeh International's English meaning, all from tanzil.net and compiled
// unchanged into the exe, so memorization works fully offline. Each is read
// once, on first use.
public static class QuranText
{
    public const int AyahTotal = 6236;
    public const string Attribution = "Quran text: Tanzil Project (tanzil.net), Uthmani 1.1, CC BY 3.0, unchanged.";
    public const string EnglishAttribution = "English: Saheeh International, via tanzil.net, for non-commercial use.";

    private static readonly Lazy<(Surah[] Surahs, string[][] Ayat)> Data = new(Load);
    // The English is read only when first shown.
    private static readonly Lazy<string[][]> English = new(LoadEnglish);

    public static IReadOnlyList<Surah> Surahs => Data.Value.Surahs;

    public static Surah GetSurah(int number) => Data.Value.Surahs[number - 1];

    public static bool IsValid(int surah, int ayah) =>
        surah is >= 1 and <= 114 && ayah >= 1 && ayah <= Data.Value.Surahs[surah - 1].AyahCount;

    // The ayah, with the basmala that opens the first ayah of every surah but
    // Al-Fatiha and At-Tawba returned separately: it is shown above the surah,
    // not memorized as part of its first ayah. (The basmala is always its first
    // four words; two surahs write it with an extra shadda, so it is split by
    // words rather than matched.)
    public static (string? Basmala, string Text) GetAyah(int surah, int ayah)
    {
        var text = Data.Value.Ayat[surah - 1][ayah - 1];
        if (ayah != 1 || surah is 1 or 9) return (null, text);
        var cut = -1;
        for (var words = 0; words < 4; words++)
        {
            cut = text.IndexOf(' ', cut + 1);
            if (cut < 0) return (null, text);
        }
        return (text[..cut], text[(cut + 1)..]);
    }

    // Saheeh International's English meaning of the ayah (it has no basmala
    // on first ayat, matching how the Arabic is shown).
    public static string GetEnglish(int surah, int ayah) => English.Value[surah - 1][ayah - 1];

    // Position of an ayah in the whole Quran, 0 to 6235, for saving progress.
    public static int GlobalIndex(int surah, int ayah) =>
        Data.Value.Surahs.Take(surah - 1).Sum(s => s.AyahCount) + ayah - 1;

    private static (Surah[], string[][]) Load()
    {
        var assembly = typeof(QuranText).Assembly;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        XDocument meta;
        using (var stream = assembly.GetManifestResourceStream("CloCloWidget.Quran.quran-data.xml")
                            ?? throw new InvalidDataException("The Quran surah list is missing."))
        using (var reader = XmlReader.Create(stream, settings))
            meta = XDocument.Load(reader);
        var surahs = meta.Descendants("sura")
            .Select(s => new Surah((int)s.Attribute("index")!, (int)s.Attribute("ayas")!,
                (string)s.Attribute("name")!, (string)s.Attribute("tname")!, (string)s.Attribute("ename")!))
            .OrderBy(s => s.Number)
            .ToArray();
        if (surahs.Length != 114 || surahs.Select((s, i) => s.Number == i + 1).Any(ok => !ok))
            throw new InvalidDataException("The Quran surah list is incomplete.");

        return (surahs, ReadAyat("CloCloWidget.Quran.quran-uthmani.txt", surahs));
    }

    private static string[][] LoadEnglish() => ReadAyat("CloCloWidget.Quran.en.sahih.txt", Data.Value.Surahs);

    // A Tanzil "surah|ayah|text" file, checked to hold every ayah exactly once.
    private static string[][] ReadAyat(string resource, Surah[] surahs)
    {
        var ayat = surahs.Select(s => new string[s.AyahCount]).ToArray();
        var count = 0;
        using (var stream = typeof(QuranText).Assembly.GetManifestResourceStream(resource)
                            ?? throw new InvalidDataException("A Quran text file is missing."))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // The licence block at the end starts with '#'.
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('|', 3);
                if (parts.Length != 3 || !int.TryParse(parts[0], out var s) || !int.TryParse(parts[1], out var a) ||
                    s < 1 || s > 114 || a < 1 || a > surahs[s - 1].AyahCount || ayat[s - 1][a - 1] != null)
                    throw new InvalidDataException("A Quran text file is malformed.");
                ayat[s - 1][a - 1] = parts[2];
                count++;
            }
        }
        if (count != AyahTotal) throw new InvalidDataException("A Quran text file is incomplete.");
        return ayat;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace CloCloWidget;

public sealed record TrendLink(string Title, string Source, string Url);

// One row of a list. Url is where the title leads (a Google search for the
// term, the Wikipedia article, or the arXiv abstract page); Links are
// Google's related headlines or a paper's PDF; Detail is a paper's abstract.
public sealed record TrendItem(int Rank, string Title, string Measure, string? Change, string? Description,
    string Url, IReadOnlyList<TrendLink> Links, string? Detail = null);

public sealed record TrendList(IReadOnlyList<TrendItem> Items, DateOnly? Day);

// Today's trending topics and newest papers from three public sources,
// fetched only when the person opens their window or presses Refresh, and
// only while that source's Privacy opt-in is on. Fixed HTTPS hosts through
// the shared client (no cookies, no redirects, 15-second timeout, 1 MB
// limit); the request carries only a country code (Google), a language and
// date (Wikipedia) or a subject (arXiv). All text is shown as plain text, and
// every link is checked to be HTTPS before it can be opened.
public static class TrendsService
{
    private const int MaxItems = 25;
    private const int MaxLinksPerItem = 3;
    private const string GoogleNamespace = "https://trends.google.com/trending/rss";
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";
    private const string ArxivNamespace = "http://arxiv.org/schemas/atom";

    // arXiv asks API clients to wait about three seconds between requests.
    private static readonly TimeSpan ArxivSpacing = TimeSpan.FromSeconds(3);
    private static DateTime _lastArxivRequestUtc = DateTime.MinValue;

    // Countries Google Trends publishes a daily feed for, as offered in the
    // window. The first one matching Windows' region is the default.
    public static readonly (string Code, string Name)[] GoogleCountries =
    {
        ("US", "United States"), ("CA", "Canada"), ("GB", "United Kingdom"), ("AU", "Australia"),
        ("IN", "India"), ("EG", "Egypt"), ("SA", "Saudi Arabia"), ("AE", "United Arab Emirates"),
        ("DE", "Germany"), ("FR", "France"), ("ES", "Spain"), ("IT", "Italy"), ("BR", "Brazil"),
        ("MX", "Mexico"), ("JP", "Japan"), ("TR", "Türkiye"), ("NG", "Nigeria"), ("ZA", "South Africa"),
    };

    // Wikipedias whose public feed carries a most-read list (checked against
    // the live feed; others, such as Portuguese and Japanese, have none).
    public static readonly (string Code, string Name)[] WikipediaLanguages =
    {
        ("en", "English"), ("ar", "العربية"), ("fr", "Français"), ("de", "Deutsch"), ("es", "Español"),
        ("it", "Italiano"),
    };

    // arXiv subjects, as its own category names (a trailing ".*" takes every
    // sub-field). Each was checked to return papers.
    public static readonly (string Code, string Name)[] ArxivSubjects =
    {
        ("math.*", "Mathematics"), ("cs.*", "Computer science"), ("cs.AI", "Artificial intelligence"),
        ("cs.LG", "Machine learning"), ("physics.*", "Physics"), ("quant-ph", "Quantum physics"),
        ("astro-ph.*", "Astrophysics"), ("stat.*", "Statistics"), ("q-bio.*", "Quantitative biology"),
        ("q-fin.*", "Quantitative finance"), ("econ.*", "Economics"), ("eess.*", "Electrical engineering"),
    };

    public static bool IsArxivSubject(string? code) => code != null && ArxivSubjects.Any(s => s.Code == code);

    public static bool IsGoogleCountry(string? code) => code != null && GoogleCountries.Any(c => c.Code == code);

    public static bool IsWikipediaLanguage(string? code) => code != null && WikipediaLanguages.Any(l => l.Code == code);

    public static string DefaultGoogleCountry()
    {
        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant();
            if (IsGoogleCountry(region)) return region;
        }
        catch (ArgumentException) { /* no usable Windows region */ }
        return "US";
    }

    public static async Task<TrendList> GetGoogleAsync(string country)
    {
        if (!IsGoogleCountry(country)) throw new ArgumentException("Unsupported country.", nameof(country));
        var xml = await NetworkClient.Shared.GetStringAsync($"https://trends.google.com/trending/rss?geo={country}");

        var doc = LoadXml(xml);
        XNamespace ht = GoogleNamespace;
        var items = new List<TrendItem>();
        foreach (var item in doc.Descendants("item").Take(MaxItems))
        {
            var title = Clean(item.Element("title")?.Value, 120);
            if (title == null) continue;
            var traffic = Clean(item.Element(ht + "approx_traffic")?.Value, 20);
            var links = item.Elements(ht + "news_item")
                .Select(news => (Title: Clean(news.Element(ht + "news_item_title")?.Value, 200),
                                 Source: Clean(news.Element(ht + "news_item_source")?.Value, 60) ?? "",
                                 Url: SafeHttpsUrl(news.Element(ht + "news_item_url")?.Value)))
                .Where(news => news.Title != null && news.Url != null)
                .Take(MaxLinksPerItem)
                .Select(news => new TrendLink(news.Title!, news.Source, news.Url!))
                .ToList();
            var search = "https://www.google.com/search?q=" + Uri.EscapeDataString(title);
            items.Add(new TrendItem(items.Count + 1, title,
                traffic != null ? $"{traffic} searches" : "Trending", null, null, search, links));
        }
        return new TrendList(items, DateOnly.FromDateTime(DateTime.Now));
    }

    // Wikipedia publishes each day's most-read list a little after that day
    // ends in UTC, so the newest list is usually yesterday's or the day
    // before. Ask for today's feed and step back until one carries the list.
    public static async Task<TrendList> GetWikipediaAsync(string language)
    {
        if (!IsWikipediaLanguage(language)) throw new ArgumentException("Unsupported language.", nameof(language));
        var today = DateTime.UtcNow.Date;
        for (var back = 0; back <= 3; back++)
        {
            var day = today.AddDays(-back);
            var url = $"https://{language}.wikipedia.org/api/rest_v1/feed/featured/" +
                day.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);
            using var doc = JsonDocument.Parse(await NetworkClient.Shared.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("mostread", out var mostRead) ||
                !mostRead.TryGetProperty("articles", out var articles) ||
                articles.ValueKind != JsonValueKind.Array) continue;

            DateOnly? listDay = null;
            if (mostRead.TryGetProperty("date", out var dateValue) &&
                DateOnly.TryParseExact(dateValue.GetString()?.TrimEnd('Z'), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                listDay = parsed;

            var items = new List<TrendItem>();
            foreach (var article in articles.EnumerateArray())
            {
                if (items.Count >= MaxItems) break;
                var title = Clean(GetString(article, "normalizedtitle"), 120);
                var page = article.TryGetProperty("content_urls", out var urls) &&
                           urls.TryGetProperty("desktop", out var desktop)
                    ? SafeHttpsUrl(GetString(desktop, "page")) : null;
                if (title == null || page == null) continue;
                var views = article.TryGetProperty("views", out var v) && v.TryGetInt64(out var n) ? n : 0;
                var description = Clean(GetString(article, "description"), 160);
                items.Add(new TrendItem(items.Count + 1, title,
                    views.ToString("N0", CultureInfo.CurrentCulture) + " views",
                    ViewChange(article, views), description, page, Array.Empty<TrendLink>()));
            }
            if (items.Count > 0) return new TrendList(items, listDay);
        }
        return new TrendList(Array.Empty<TrendItem>(), null);
    }

    // The newest submissions in one subject, newest first. arXiv announces
    // papers on weekdays, so over a weekend the newest can be a day or two old.
    public static async Task<TrendList> GetArxivAsync(string subject)
    {
        if (!IsArxivSubject(subject)) throw new ArgumentException("Unsupported subject.", nameof(subject));
        var wait = _lastArxivRequestUtc + ArxivSpacing - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        _lastArxivRequestUtc = DateTime.UtcNow;
        var xml = await NetworkClient.Shared.GetStringAsync(
            $"https://export.arxiv.org/api/query?search_query=cat:{subject}&sortBy=submittedDate&sortOrder=descending&max_results={MaxItems}");

        XNamespace atom = AtomNamespace;
        XNamespace arxiv = ArxivNamespace;
        var items = new List<TrendItem>();
        DateOnly? newest = null;
        foreach (var entry in LoadXml(xml).Descendants(atom + "entry").Take(MaxItems))
        {
            var title = Clean(entry.Element(atom + "title")?.Value, 220);
            var page = entry.Elements(atom + "link")
                .Where(l => (string?)l.Attribute("rel") == "alternate")
                .Select(l => SafeHttpsUrl((string?)l.Attribute("href")))
                .FirstOrDefault(u => u != null);
            if (title == null || page == null) continue;

            var authors = entry.Elements(atom + "author")
                .Select(a => Clean(a.Element(atom + "name")?.Value, 60))
                .Where(n => n != null)
                .ToList();
            var byline = authors.Count switch
            {
                0 => null,
                <= 3 => string.Join(", ", authors),
                _ => string.Join(", ", authors.Take(3)) + $" and {authors.Count - 3} more",
            };
            var category = Clean((string?)entry.Element(arxiv + "primary_category")?.Attribute("term"), 20);
            DateTime? published = DateTime.TryParse(entry.Element(atom + "published")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;
            if (published is { } p && (newest == null || DateOnly.FromDateTime(p.ToLocalTime()) > newest))
                newest = DateOnly.FromDateTime(p.ToLocalTime());
            var measure = string.Join(" · ", new[]
            {
                category,
                published?.ToLocalTime().ToString("ddd, MMM d", CultureInfo.CurrentCulture),
            }.Where(x => x != null));

            var pdf = entry.Elements(atom + "link")
                .Where(l => (string?)l.Attribute("title") == "pdf")
                .Select(l => SafeHttpsUrl((string?)l.Attribute("href")))
                .FirstOrDefault(u => u != null);
            var links = pdf != null ? new[] { new TrendLink("PDF", "arXiv", pdf) } : Array.Empty<TrendLink>();
            items.Add(new TrendItem(items.Count + 1, title, measure.Length > 0 ? measure : "arXiv", null, byline,
                page, links, Clean(entry.Element(atom + "summary")?.Value, 420)));
        }
        return new TrendList(items, newest);
    }

    private static XDocument LoadXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    // "▲ 12×" against the day before, from the list's own view history.
    private static string? ViewChange(JsonElement article, long views)
    {
        if (!article.TryGetProperty("view_history", out var history) || history.ValueKind != JsonValueKind.Array) return null;
        var days = history.EnumerateArray().ToList();
        if (days.Count < 2 || !days[^2].TryGetProperty("views", out var before) || !before.TryGetInt64(out var previous)) return null;
        if (previous <= 0) return "▲ new";
        var ratio = (double)views / previous;
        return ratio switch
        {
            >= 10 => $"▲ {ratio.ToString("N0", CultureInfo.CurrentCulture)}×",
            >= 2 => $"▲ {ratio.ToString("0.#", CultureInfo.CurrentCulture)}×",
            >= 1.05 => $"▲ {((ratio - 1) * 100).ToString("0", CultureInfo.CurrentCulture)}%",
            <= 0.95 => $"▼ {((1 - ratio) * 100).ToString("0", CultureInfo.CurrentCulture)}%",
            _ => "steady",
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Plain one-line text: control characters dropped, whitespace collapsed,
    // length capped.
    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = string.Join(' ', new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    // Only absolute https:// links with a host and no user name or password.
    public static string? SafeHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.AbsoluteUri;
    }
}

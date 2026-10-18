using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Xml;

namespace CloCloWidget;

public enum TrendSource { Google, Wikipedia, Arxiv }

// Today's trending topics, or the newest papers, from one online source, as
// ranked cards. It fetches
// only when it opens or on Refresh - nothing runs in the background - and
// the main window closes it if that source's Privacy opt-in is turned off.
public partial class TrendsWindow : Window
{
    private sealed record Choice(string Code, string Name);

    public sealed record LinkRow(string Title, string SourceText, string Url, FlowDirection Flow);

    public sealed record TrendRow(int Rank, string Title, FlowDirection TitleFlow, string Url, string UrlHint,
        string Measure, string ChangeText, Brush ChangeBrush, Brush RankBrush,
        string? Description, FlowDirection DescriptionFlow, Visibility DescriptionVisibility,
        IReadOnlyList<LinkRow> Links, Visibility LinksVisibility, string? Detail, Visibility DetailVisibility);

    private static readonly Brush Gold = Frozen(Color.FromRgb(0xe6, 0xc4, 0x76));
    private static readonly Brush Sand = Frozen(Color.FromRgb(0xcb, 0xbf, 0xa8));
    private static readonly Brush Rise = Frozen(Color.FromRgb(0x9f, 0xd4, 0x9f));
    private static readonly Brush Fall = Frozen(Color.FromRgb(0xe8, 0xa0, 0x8a));

    private readonly TrendSource _source;
    private readonly WidgetSettings _settings;
    private bool _loading;
    private bool _ready;

    public TrendSource Source => _source;

    public TrendsWindow(TrendSource source, WidgetSettings settings)
    {
        InitializeComponent();
        _source = source;
        _settings = settings;

        if (source == TrendSource.Google)
        {
            Title = "CloClo — Google Trends";
            HeaderTitle.Text = "Google Trends";
            SourceGlyph.Text = "\uE721";
            ChoiceBox.ItemsSource = TrendsService.GoogleCountries.Select(c => new Choice(c.Code, c.Name)).ToList();
            var country = settings.GoogleTrendsCountry ?? TrendsService.DefaultGoogleCountry();
            ChoiceBox.SelectedItem = ((List<Choice>)ChoiceBox.ItemsSource).First(c => c.Code == country);
            FooterText.Text = "From Google Trends' public daily feed. Only the country code is sent. " +
                "Click a topic to search it, or a headline to read it, in Microsoft Edge.";
        }
        else if (source == TrendSource.Arxiv)
        {
            Title = "CloClo — arXiv newest papers";
            HeaderTitle.Text = "arXiv newest papers";
            SourceGlyph.Text = "\uE7BE";
            ChoiceBox.ItemsSource = TrendsService.ArxivSubjects.Select(s => new Choice(s.Code, s.Name)).ToList();
            ChoiceBox.SelectedItem = ((List<Choice>)ChoiceBox.ItemsSource).First(s => s.Code == settings.ArxivSubject);
            FooterText.Text = "From arXiv's public API: the newest submissions, not peer reviewed. Only the subject is sent. " +
                "Click a title for the abstract page, or PDF for the paper, in Microsoft Edge.";
        }
        else
        {
            Title = "CloClo — Wikipedia most-read";
            HeaderTitle.Text = "Wikipedia most-read";
            SourceGlyph.Text = "\uE8F1";
            ChoiceBox.ItemsSource = TrendsService.WikipediaLanguages.Select(l => new Choice(l.Code, l.Name)).ToList();
            ChoiceBox.SelectedItem = ((List<Choice>)ChoiceBox.ItemsSource).First(l => l.Code == settings.WikipediaTrendsLanguage);
            FooterText.Text = "From Wikipedia's public feed of the most-read articles. Only the language and date are sent. " +
                "Click an article to open it in Microsoft Edge.";
        }
        _ready = true;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ChoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ChoiceBox.SelectedItem is not Choice choice) return;
        if (_source == TrendSource.Google) _settings.GoogleTrendsCountry = choice.Code;
        else if (_source == TrendSource.Arxiv) _settings.ArxivSubject = choice.Code;
        else _settings.WikipediaTrendsLanguage = choice.Code;
        _settings.Save();
        if (IsLoaded) await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_loading || ChoiceBox.SelectedItem is not Choice choice) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        ChoiceBox.IsEnabled = false;
        StatusText.Text = "Loading…";
        try
        {
            var list = _source switch
            {
                TrendSource.Google => await TrendsService.GetGoogleAsync(choice.Code),
                TrendSource.Arxiv => await TrendsService.GetArxivAsync(choice.Code),
                _ => await TrendsService.GetWikipediaAsync(choice.Code),
            };
            ShowList(list, choice.Name);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   or XmlException or InvalidOperationException or KeyNotFoundException)
        {
            var name = _source switch { TrendSource.Google => "Google Trends", TrendSource.Arxiv => "arXiv", _ => "Wikipedia" };
            StatusText.Text = $"Couldn't load {name} ({ex.GetType().Name}). Check the internet connection, then press Refresh.";
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            ChoiceBox.IsEnabled = true;
        }
    }

    private void ShowList(TrendList list, string choiceName)
    {
        var day = list.Day?.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        if (_source == TrendSource.Google)
        {
            HeaderSubtitle.Text = $"Trending searches in {choiceName}" + (day != null ? $" · {day}" : "");
        }
        else if (_source == TrendSource.Arxiv)
        {
            HeaderSubtitle.Text = $"Newest papers in {choiceName}" + (day != null ? $" · latest from {day}" : "");
        }
        else
        {
            var newest = list.Day != null && list.Day < DateOnly.FromDateTime(DateTime.UtcNow)
                ? " (newest list published)" : "";
            HeaderSubtitle.Text = $"Most-read on {choiceName} Wikipedia" + (day != null ? $" · {day}{newest}" : "");
        }

        TrendList.ItemsSource = list.Items.Select(ToRow).ToList();
        StatusText.Text = list.Items.Count == 0
            ? "No list is available right now. Try again later."
            : $"{list.Items.Count} {(_source == TrendSource.Arxiv ? "papers" : "topics")} · updated {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}";
    }

    private TrendRow ToRow(TrendItem item)
    {
        var change = item.Change ?? "";
        var changeBrush = change.StartsWith('▲') ? Rise : change.StartsWith('▼') ? Fall : Sand;
        var links = item.Links
            .Select(l => new LinkRow(l.Title, string.IsNullOrEmpty(l.Source) ? "" : $"  · {l.Source}", l.Url, FlowOf(l.Title)))
            .ToList();
        var hint = _source == TrendSource.Google ? $"Search Google for “{item.Title}”" : item.Url;
        return new TrendRow(item.Rank, item.Title, FlowOf(item.Title), item.Url, hint, item.Measure,
            change.Length > 0 ? $"  {change}" : "", changeBrush,
            item.Rank <= 3 ? Gold : Sand,
            item.Description, FlowOf(item.Description), item.Description != null ? Visibility.Visible : Visibility.Collapsed,
            links, links.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
            item.Detail, item.Detail != null ? Visibility.Visible : Visibility.Collapsed);
    }

    // Arabic (or Hebrew) text is laid out right to left, so brackets and
    // punctuation land where a reader expects them. The first letter decides.
    private static FlowDirection FlowOf(string? text)
    {
        foreach (var c in text ?? "")
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
                return FlowDirection.RightToLeft;
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var error = TrustedBrowser.OpenHttps(e.Uri.OriginalString);
        if (error != null) StatusText.Text = error;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

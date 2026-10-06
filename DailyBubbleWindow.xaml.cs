using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace CloCloWidget;

public partial class DailyBubbleWindow : Window
{
    private static readonly string[] Headings =
    {
        "💬  Quote of the day", "🔤  Arabic word of the day", "😄  Joke of the day", "💡  Did you know?",
    };

    private int _page;

    public DailyBubbleWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        ShowPage(0);
    }

    // Beside the widget, on whichever side has room, with the tail pointing
    // at the avatar's head; kept on screen. widget is the main window.
    public void PlaceBeside(Window widget, double headY)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 200;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // Overlaps the widget's transparent margin a little, so the tail
        // reaches towards the avatar instead of floating in space.
        const double overlap = 22;
        var onLeft = widget.Left - width + overlap >= screenLeft || widget.Left + widget.Width - overlap + width > screenRight;
        Left = onLeft ? widget.Left - width + overlap : widget.Left + widget.Width - overlap;
        TailRight.Visibility = onLeft ? Visibility.Visible : Visibility.Collapsed;
        TailLeft.Visibility = onLeft ? Visibility.Collapsed : Visibility.Visible;

        // The tail sits 35 px down the bubble (its margin plus half its height).
        Top = Math.Max(screenTop, Math.Min(widget.Top + headY - 35, screenBottom - height));
    }

    private void ShowPage(int page)
    {
        _page = (page % Headings.Length + Headings.Length) % Headings.Length;
        Heading.Text = Headings[_page];
        Dots.Text = string.Join(" ", Array.ConvertAll(Headings, h => h == Headings[_page] ? "●" : "○"));
        Body.Children.Clear();
        var today = DateTime.Today;

        switch (_page)
        {
            case 0:
                var quote = DailyContent.TodaysQuote(today);
                Body.Children.Add(Text($"“{quote.Text}”", 14.5));
                if (quote.Arabic != null) Body.Children.Add(Arabic(quote.Arabic, 17, top: 6));
                Body.Children.Add(Text("— " + quote.Author, 12.5, muted: true, top: 6, alignRight: true));
                break;
            case 1:
                var word = DailyContent.TodaysWord(today);
                var pair = new Grid();
                pair.ColumnDefinitions.Add(new ColumnDefinition());
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var english = Text(word.English, 19, bold: true);
                english.VerticalAlignment = VerticalAlignment.Center;
                var arabic = Arabic(word.Arabic, 26, accent: true);
                arabic.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(arabic, 1);
                pair.Children.Add(english);
                pair.Children.Add(arabic);
                Body.Children.Add(pair);
                Body.Children.Add(Text(word.Transliteration, 13, muted: true, italic: true, alignRight: true));
                Body.Children.Add(Text(word.Example, 13.5, top: 8));
                Body.Children.Add(Arabic(word.ExampleArabic, 16, top: 2));
                break;
            case 2:
                var joke = DailyContent.TodaysJoke(today);
                Body.Children.Add(Text(joke.Setup, 14.5));
                Body.Children.Add(Text(joke.Punchline, 14.5, bold: true, accent: true, top: 6));
                break;
            default:
                Body.Children.Add(Text(DailyContent.TodaysFact(today), 14.5));
                break;
        }
    }

    private TextBlock Text(string text, double size, bool bold = false, bool italic = false, bool muted = false,
        bool accent = false, double top = 0, bool alignRight = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        Foreground = (Brush)FindResource(accent ? "Accent" : muted ? "Muted" : "Text"),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left,
        Margin = new Thickness(0, top, 0, 0),
    };

    // Arabic runs right to left, so it's laid out that way and lines up on
    // the right.
    private TextBlock Arabic(string text, double size, bool accent = false, double top = 0)
    {
        var block = Text(text, size, accent: accent, top: top);
        block.FlowDirection = System.Windows.FlowDirection.RightToLeft;
        block.FontFamily = new FontFamily("Segoe UI, Arial");
        block.Language = System.Windows.Markup.XmlLanguage.GetLanguage("ar");
        return block;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ShowPage(_page + 1);

    private void Close_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Right or Key.Space or Key.Enter: ShowPage(_page + 1); break;
            case Key.Left: ShowPage(_page - 1); break;
            default: return;
        }
        e.Handled = true;
    }
}

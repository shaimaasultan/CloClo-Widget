using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Controls = System.Windows.Controls;
using MediaColor = System.Windows.Media.Color;

namespace CloCloWidget;

// A snapshot of Windows' own Clipboard History, pulled fresh the moment
// this opens (see ShowClipboardHistoryAsync in MainWindow.xaml.cs) —
// newest first. Click a row to put that text back on the clipboard;
// there's nothing to edit here, so the window just closes once you have.
public partial class ClipboardHistoryWindow : Window
{
    public ClipboardHistoryWindow(IReadOnlyList<string> history)
    {
        InitializeComponent();

        foreach (var text in history)
        {
            ItemsPanel.Children.Add(BuildRow(text));
        }
    }

    private Controls.Border BuildRow(string text)
    {
        const int previewLimit = 80;
        var preview = text.Length > previewLimit ? text[..previewLimit] + "…" : text;

        var textBlock = new Controls.TextBlock
        {
            Text = preview,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xf3, 0xec, 0xdd)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var border = new Controls.Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1e, 0x16, 0x10)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x8a, 0x6a, 0x2f)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = textBlock,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (SafeClipboard.TrySetText(text)) Close();
        };
        return border;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

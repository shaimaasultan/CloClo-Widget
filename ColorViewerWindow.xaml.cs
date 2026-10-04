using System.Windows;
using System.Windows.Media;
using Controls = System.Windows.Controls;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace CloCloWidget;

// Shows the actual picked color as a real swatch, plus the same value
// written a few common ways (hex, rgb(), hsl()) each with its own copy
// button — the eyedropper icon itself just auto-copies hex to clipboard
// for the fast path, this is for when a different format or a visual
// check of the color is what's actually needed.
public partial class ColorViewerWindow : Window
{
    public ColorViewerWindow(DrawingColor color)
    {
        InitializeComponent();
        Swatch.Background = new SolidColorBrush(MediaColor.FromRgb(color.R, color.G, color.B));

        AddFormatRow("HEX", ColorPicker.ToHex(color));
        AddFormatRow("RGB", ColorPicker.ToRgbString(color));
        AddFormatRow("HSL", ColorPicker.ToHslString(color));
    }

    private void AddFormatRow(string label, string value)
    {
        var grid = new Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = GridLength.Auto });

        var labelBlock = new Controls.TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xf3, 0xec, 0xdd)),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
        };
        Controls.Grid.SetColumn(labelBlock, 0);

        var textBox = new Controls.TextBox { Text = value, Margin = new Thickness(0, 0, 6, 0) };
        Controls.Grid.SetColumn(textBox, 1);

        var copyButton = new Controls.Button { Content = "Copy" };
        Controls.Grid.SetColumn(copyButton, 2);
        copyButton.Click += (_, _) =>
        {
            copyButton.Content = SafeClipboard.TrySetText(value) ? "Copied!" : "Clipboard busy";
        };

        grid.Children.Add(labelBlock);
        grid.Children.Add(textBox);
        grid.Children.Add(copyButton);
        FormatsPanel.Children.Add(grid);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

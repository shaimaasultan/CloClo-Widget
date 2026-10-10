using System;
using System.Windows;

namespace CloCloWidget;

public partial class SelectionDetailsWindow : Window
{
    public SelectionDetailsWindow(string text)
    {
        InitializeComponent();

        var words = 0;
        var lines = 1;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) inWord = false;
            else if (!inWord) { words++; inWord = true; }
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
                lines++;
        }

        CountText.Text = $"{words:N0} words  ·  {text.Length:N0} characters  ·  {lines:N0} lines";
        PreviewText.Text = text.Length > 300 ? text[..300] + "…" : text;
        Closed += (_, _) =>
        {
            PreviewText.Text = string.Empty;
            CountText.Text = string.Empty;
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

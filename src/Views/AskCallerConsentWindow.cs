using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CloCloWidget;

// Asked the first time a program uses the ask pipe (AskPipeServer): may this
// program send CloClo a text and a question and read the answer? Shows the
// program's full path and publisher (or that it is unsigned). "Don't allow"
// has the focus and is what Escape, the close button and silence choose;
// after a minute without an answer the window closes as "Don't allow".
internal sealed class AskCallerConsentWindow : Window
{
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromMinutes(1);

    // Programs that run whatever scripts they are given: allowing one allows
    // every script it runs.
    private static readonly string[] ScriptHosts =
    [
        "powershell.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "python.exe",
        "pythonw.exe", "py.exe", "node.exe", "java.exe", "javaw.exe", "ruby.exe", "perl.exe", "bash.exe", "wsl.exe",
    ];

    public AskCallerConsentWindow(PipeCaller caller, bool replacesEarlier)
    {
        Title = "CloClo - Allow a program to ask?";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;

        var heading = new TextBlock
        {
            Text = $"Allow {caller.FileName} to ask CloClo about a text?",
            FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        var detail = new TextBlock { Text = caller.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var meaning = new TextBlock
        {
            Text = "It could send CloClo a text and a question and read the answer, whenever \"Let other apps ask about " +
                   "a text\" is on. It can't make CloClo do anything else. You can remove it later from Privacy -> Apps allowed to ask." +
                   (replacesEarlier ? "\n\nYou allowed a program at this path before, but its file or publisher has changed since." : "") +
                   (Array.Exists(ScriptHosts, h => h.Equals(caller.FileName, StringComparison.OrdinalIgnoreCase))
                       ? $"\n\n{caller.FileName} runs scripts: allowing it allows every script it runs to ask."
                       : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };

        var allow = new Button { Content = "Allow", Padding = new Thickness(14, 5, 14, 5), MinWidth = 90 };
        var deny = new Button
        {
            Content = "Don't allow", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 90, IsCancel = true,
        };
        allow.Click += (_, _) => { DialogResult = true; };
        deny.Click += (_, _) => { DialogResult = false; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            DialogResult = false;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(heading);
        content.Children.Add(detail);
        content.Children.Add(meaning);
        content.Children.Add(buttons);
        Content = content;

        Loaded += (_, _) => deny.Focus();
        var timeout = new DispatcherTimer { Interval = AnswerWithin };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            if (IsVisible) DialogResult = false;
        };
        timeout.Start();
        Closed += (_, _) => timeout.Stop();
    }
}

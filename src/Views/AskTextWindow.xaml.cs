using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloCloWidget;

// Paste or type a text, ask a question about it, and CloClo's local language
// model answers from that text only (LlmService). The answer is shown and,
// optionally, read aloud with Windows' own voice; it is never acted on.
//
// The size limit is the model's, in tokens: the window counts the prompt
// with the model's own tokenizer as you type (Arabic with diacritics uses
// about three times the tokens of the same length of English) and won't ask
// while it is over.
public partial class AskTextWindow : Window
{
    private static readonly Brush CountBrush = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC4));
    private static readonly Brush OverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));

    private readonly DispatcherTimer _countDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _asking;
    private CancellationTokenSource? _counting;
    private bool _overLimit;

    public AskTextWindow()
    {
        InitializeComponent();
        SourceBox.MaxLength = LlmService.MaxTextChars;
        QuestionBox.MaxLength = LlmService.MaxQuestionChars;
        _countDelay.Tick += (_, _) => { _countDelay.Stop(); CountTokens(); };
        if (LlmService.UnavailableReason is { } reason)
        {
            AskButton.IsEnabled = false;
            StatusText.Text = reason;
            TextCount.Text = "";
        }
        else
        {
            TextCount.Text = $"0 / {LlmService.PromptLimit:N0} tokens";
        }
        Closed += (_, _) =>
        {
            _countDelay.Stop();
            _counting?.Cancel();
            _asking?.Cancel();
            SpeechReader.Stop();
        };
    }

    private void Text_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SourceBox.FlowDirection = DirectionOf(SourceBox.Text);
        QueueCount();
    }

    private void Question_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        QuestionBox.FlowDirection = DirectionOf(QuestionBox.Text);
        QueueCount();
    }

    // Right to left when the first letter is Arabic (or Hebrew), so Arabic
    // lines start on the right and their punctuation ends there.
    private static FlowDirection DirectionOf(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
                return FlowDirection.RightToLeft;
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }

    private void QueueCount()
    {
        if (LlmService.UnavailableReason != null) return;
        _countDelay.Stop();
        _countDelay.Start();
    }

    private async void CountTokens()
    {
        _counting?.Cancel();
        using var counting = new CancellationTokenSource();
        _counting = counting;
        if (SourceBox.Text.Length > 0 && TextCount.Text.StartsWith("0 /", StringComparison.Ordinal))
            TextCount.Text = "Counting tokens…";
        try
        {
            var tokens = await LlmService.CountPromptTokensAsync(SourceBox.Text, QuestionBox.Text, counting.Token);
            if (counting.IsCancellationRequested) return;
            _overLimit = tokens > LlmService.PromptLimit;
            TextCount.Text = $"{tokens:N0} / {LlmService.PromptLimit:N0} tokens" + (_overLimit ? " - too long, shorten the text" : "");
            TextCount.Foreground = _overLimit ? OverBrush : CountBrush;
            if (_asking == null) AskButton.IsEnabled = !_overLimit;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FileNotFoundException)
        {
            TextCount.Text = "";
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            TextCount.Text = "";
            StatusText.Text = "Couldn't count the text's tokens: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_counting, counting)) _counting = null;
        }
    }

    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !AskButton.IsEnabled) return;
        e.Handled = true;
        Ask_Click(sender, e);
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_asking != null || _overLimit) return;
        SpeechReader.Stop();
        using var asking = new CancellationTokenSource();
        _asking = asking;
        AskButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        AnswerBox.Text = "";
        StatusText.Text = "Thinking… (the first question also loads the model, a few seconds)";
        try
        {
            var answer = await LlmService.AnswerAsync(SourceBox.Text, QuestionBox.Text, asking.Token);
            AnswerBox.FlowDirection = DirectionOf(answer);
            AnswerBox.Text = answer;
            StatusText.Text = $"Answered on this PC by {LlmService.Processor ?? "the CPU"}, from the text above only.";
            if (SpeakCheck.IsChecked == true && answer.Length > 0)
                SpeechReader.Speak(answer, onFailed: () => Dispatcher.BeginInvoke(() =>
                    StatusText.Text = "Answered, but Windows couldn't read it aloud."));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or FileNotFoundException)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "The language model couldn't answer: " + ex.Message;
        }
        finally
        {
            _asking = null;
            StopButton.IsEnabled = false;
            AskButton.IsEnabled = LlmService.UnavailableReason == null && !_overLimit;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _asking?.Cancel();
        SpeechReader.Stop();
    }
}

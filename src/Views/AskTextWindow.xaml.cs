using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace CloCloWidget;

// Paste or type a text, ask a question about it, and CloClo's local language
// model answers from that text only (LlmService). The answer is shown and,
// optionally, read aloud with Windows' own voice; it is never acted on.
public partial class AskTextWindow : Window
{
    private CancellationTokenSource? _asking;

    public AskTextWindow()
    {
        InitializeComponent();
        SourceBox.MaxLength = LlmService.MaxTextChars;
        QuestionBox.MaxLength = LlmService.MaxQuestionChars;
        UpdateCount();
        if (LlmService.UnavailableReason is { } reason)
        {
            AskButton.IsEnabled = false;
            StatusText.Text = reason;
        }
        Closed += (_, _) =>
        {
            _asking?.Cancel();
            SpeechReader.Stop();
        };
    }

    private void Text_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateCount();

    private void UpdateCount() =>
        TextCount.Text = $"{SourceBox.Text.Length:N0} / {LlmService.MaxTextChars:N0} characters";

    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !AskButton.IsEnabled) return;
        e.Handled = true;
        Ask_Click(sender, e);
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_asking != null) return;
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
            AnswerBox.Text = answer;
            StatusText.Text = "Answered on this PC, from the text above only.";
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
            AskButton.IsEnabled = LlmService.UnavailableReason == null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _asking?.Cancel();
        SpeechReader.Stop();
    }
}

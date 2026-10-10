using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CloCloWidget;

public partial class ScientificCalculatorWindow : Window
{
    private bool _degrees = true;
    private double _lastAnswer;
    private Button? _angleButton;

    public ScientificCalculatorWindow()
    {
        InitializeComponent();
        var keys = new[]
        {
            "DEG", "sin", "cos", "tan", "C",
            "asin", "acos", "atan", "ln", "log",
            "√", "x²", "xʸ", "n!", "exp",
            "1/x", "(", ")", "%", "⌫",
            "7", "8", "9", "÷", "π",
            "4", "5", "6", "×", "e",
            "1", "2", "3", "−", "Ans",
            "±", "0", ".", "+", "=",
        };
        foreach (var key in keys)
        {
            var style = key switch
            {
                "C" => "ClearButton",
                "=" => "EqualsButton",
                "+" or "−" or "×" or "÷" or "xʸ" or "x²" or "±" => "OperatorButton",
                "DEG" or "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or
                    "ln" or "log" or "√" or "n!" or "exp" or "1/x" or "(" or ")" or
                    "%" or "⌫" or "π" or "e" or "Ans" => "FunctionButton",
                _ => "KeyButton",
            };
            var button = new Button { Content = key, Style = (Style)FindResource(style) };
            button.Click += Key_Click;
            Keypad.Children.Add(button);
            if (key == "DEG") _angleButton = button;
        }
    }

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Content is not string key) return;
        switch (key)
        {
            case "DEG":
            case "RAD":
                _degrees = !_degrees;
                if (_angleButton != null) _angleButton.Content = _degrees ? "DEG" : "RAD";
                UpdateResult();
                break;
            case "C":
                ExpressionBox.Clear();
                ExpressionBox.Focus();
                break;
            case "⌫":
                Backspace();
                break;
            case "=":
                CommitResult();
                break;
            case "±":
                WrapExpression("-(");
                break;
            case "1/x":
                WrapExpression("1/(");
                break;
            default:
                Insert(key switch
                {
                    "√" => "sqrt(",
                    "x²" => "^2",
                    "xʸ" => "^",
                    "n!" => "!",
                    "÷" => "/",
                    "×" => "*",
                    "−" => "-",
                    "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or
                        "ln" or "log" or "exp" => key + "(",
                    _ => key,
                });
                break;
        }
    }

    private void Insert(string value)
    {
        var start = ExpressionBox.SelectionStart;
        ExpressionBox.SelectedText = value;
        ExpressionBox.SelectionStart = Math.Min(start + value.Length, ExpressionBox.Text.Length);
        ExpressionBox.Focus();
    }

    private void WrapExpression(string prefix)
    {
        var selected = ExpressionBox.SelectedText;
        if (selected.Length > 0)
        {
            Insert(prefix + selected + ")");
        }
        else if (ExpressionBox.Text.Length > 0)
        {
            ExpressionBox.Text = prefix + ExpressionBox.Text + ")";
            ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
            ExpressionBox.Focus();
        }
        else Insert(prefix[..^1]);
    }

    private void Backspace()
    {
        if (ExpressionBox.SelectionLength > 0)
        {
            ExpressionBox.SelectedText = "";
        }
        else if (ExpressionBox.SelectionStart > 0)
        {
            var start = ExpressionBox.SelectionStart - 1;
            ExpressionBox.Text = ExpressionBox.Text.Remove(start, 1);
            ExpressionBox.SelectionStart = start;
        }
        ExpressionBox.Focus();
    }

    private void ExpressionBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateResult();

    private void ExpressionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitResult();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) Close();
    }

    private void UpdateResult()
    {
        if (ResultText == null) return;
        ResultText.Text = ScientificCalculator.TryEvaluate(ExpressionBox.Text, _degrees, _lastAnswer,
            out var result, out _)
            ? Format(result) : "—";
    }

    private void CommitResult()
    {
        if (!ScientificCalculator.TryEvaluate(ExpressionBox.Text, _degrees, _lastAnswer,
                out var result, out var error))
        {
            ResultText.Text = error;
            return;
        }
        _lastAnswer = result;
        ExpressionBox.Text = Format(result);
        ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
        ExpressionBox.Focus();
    }

    private static string Format(double value) => value.ToString("G15", CultureInfo.InvariantCulture);
}

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CloCloWidget;

internal sealed class CustomReminderEditorWindow : Window
{
    private readonly TextBox _message = new() { MaxLength = 120, MinHeight = 58, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
    private static readonly int[] IntervalValues = { 15, 30, 45, 60, 90, 120 };
    private readonly ComboBox _interval = new() { MinWidth = 110 };
    private readonly ComboBox _startHour = new() { MinWidth = 95 };
    private readonly ComboBox _endHour = new() { MinWidth = 95 };
    private readonly CheckBox _enabled = new() { Content = "Reminder enabled", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _speak = new() { Content = "Speak the message as well as displaying it" };

    public CustomReminderSettings Reminder { get; private set; }

    public CustomReminderEditorWindow(CustomReminderSettings reminder)
    {
        Reminder = reminder.Copy();
        Title = "Edit custom reminder";
        Width = 390;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        };

        foreach (var minutes in IntervalValues)
            _interval.Items.Add(minutes switch
            {
                60 => "1 hour",
                120 => "2 hours",
                _ => $"{minutes} minutes",
            });
        foreach (var hour in Enumerable.Range(0, 24))
        {
            var label = $"{hour:00}:00";
            _startHour.Items.Add(label);
            _endHour.Items.Add(label);
        }

        _message.Text = Reminder.Message;
        _interval.SelectedIndex = System.Array.IndexOf(IntervalValues, Reminder.IntervalMinutes);
        _startHour.SelectedIndex = Reminder.StartHour;
        _endHour.SelectedIndex = Reminder.EndHour;
        _enabled.IsChecked = Reminder.Enabled;
        _speak.IsChecked = Reminder.Speak;

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "Message", Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(_message);
        content.Children.Add(new TextBlock { Text = "Repeat every", Margin = new Thickness(0, 12, 0, 4) });
        content.Children.Add(_interval);

        var hours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var from = new StackPanel();
        from.Children.Add(new TextBlock { Text = "Active from", Margin = new Thickness(0, 0, 0, 4) });
        from.Children.Add(_startHour);
        var to = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        to.Children.Add(new TextBlock { Text = "Active until", Margin = new Thickness(0, 0, 0, 4) });
        to.Children.Add(_endHour);
        hours.Children.Add(from);
        hours.Children.Add(to);
        content.Children.Add(hours);
        content.Children.Add(_enabled);
        content.Children.Add(_speak);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var save = new Button { Content = "Save", MinWidth = 72, IsDefault = true };
        save.Click += Save_Click;
        buttons.Children.Add(save);
        content.Children.Add(buttons);
        Content = content;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_message.Text) || _message.Text.Any(char.IsControl))
        {
            MessageBox.Show(this, "Enter a message without control characters.", "Custom reminder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_interval.SelectedIndex < 0 || _interval.SelectedIndex >= IntervalValues.Length ||
            _startHour.SelectedIndex < 0 || _startHour.SelectedIndex > 23 ||
            _endHour.SelectedIndex < 0 || _endHour.SelectedIndex > 23)
        {
            MessageBox.Show(this, "Choose an interval and both active hours.", "Custom reminder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var interval = IntervalValues[_interval.SelectedIndex];
        var start = _startHour.SelectedIndex;
        var end = _endHour.SelectedIndex;
        if (start == end)
        {
            MessageBox.Show(this, "Choose different start and end hours.", "Custom reminder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Reminder = new CustomReminderSettings
        {
            Message = _message.Text.Trim(),
            IntervalMinutes = interval,
            StartHour = start,
            EndHour = end,
            Enabled = _enabled.IsChecked == true,
            Speak = _speak.IsChecked == true,
        };
        DialogResult = true;
    }
}

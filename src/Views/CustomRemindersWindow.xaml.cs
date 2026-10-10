using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CloCloWidget;

public partial class CustomRemindersWindow : Window
{
    private readonly ObservableCollection<CustomReminderSettings> _reminders;
    public CustomReminderSettings[] Reminders => _reminders.Select(reminder => reminder.Copy()).ToArray();

    public CustomRemindersWindow(IEnumerable<CustomReminderSettings> reminders)
    {
        InitializeComponent();
        _reminders = new ObservableCollection<CustomReminderSettings>(reminders.Select(reminder => reminder.Copy()));
        ReminderList.ItemsSource = _reminders;
        ReminderList.SelectedIndex = _reminders.Count > 0 ? 0 : -1;
        AddButton.IsEnabled = _reminders.Count < 5;
    }

    private void ReminderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ReminderList.SelectedItem as CustomReminderSettings;
        ReminderDetails.Text = selected?.Describe() ?? "Select a reminder to edit its settings.";
        EditButton.IsEnabled = ToggleButton.IsEnabled = RemoveButton.IsEnabled = selected != null;
        ToggleButton.Content = selected?.Enabled == true ? "Disable" : "Enable";
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new CustomReminderEditorWindow(new CustomReminderSettings()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _reminders.Add(editor.Reminder);
        ReminderList.SelectedItem = editor.Reminder;
        AddButton.IsEnabled = _reminders.Count < 5;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderList.SelectedItem is not CustomReminderSettings selected) return;
        var index = ReminderList.SelectedIndex;
        var editor = new CustomReminderEditorWindow(selected.Copy()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _reminders[index] = editor.Reminder;
        ReminderList.SelectedIndex = index;
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderList.SelectedItem is not CustomReminderSettings selected) return;
        selected.Enabled = !selected.Enabled;
        ReminderList.Items.Refresh();
        ReminderDetails.Text = selected.Describe();
        ToggleButton.Content = selected.Enabled ? "Disable" : "Enable";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderList.SelectedItem is not CustomReminderSettings selected) return;
        var index = ReminderList.SelectedIndex;
        _reminders.Remove(selected);
        AddButton.IsEnabled = _reminders.Count < 5;
        ReminderList.SelectedIndex = _reminders.Count == 0 ? -1 : System.Math.Min(index, _reminders.Count - 1);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

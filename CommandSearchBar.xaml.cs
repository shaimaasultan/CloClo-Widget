using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace CloCloWidget;

public sealed record WidgetSearchAction(string Label, string Keywords, Action Execute);

public partial class CommandSearchBar : UserControl
{
    private readonly IReadOnlyList<WidgetSearchAction> _actions;

    public event Action<WidgetSearchAction>? CommandRequested;
    public event Action? SuggestionsOpened;

    public CommandSearchBar(IReadOnlyList<WidgetSearchAction> actions)
    {
        _actions = actions;
        InitializeComponent();
        SuggestionsPopup.CustomPopupPlacementCallback = PlaceSuggestions;
        System.Windows.Automation.AutomationProperties.SetName(SearchText, "Search widget actions");
    }

    // Place the action list below input when it fits, otherwise beside it.
    // Windows word suggestions occupy the space directly above the text box.
    private static CustomPopupPlacement[] PlaceSuggestions(Size popupSize, Size targetSize, Point offset) =>
    [
        new(new Point(0, targetSize.Height + 5), PopupPrimaryAxis.Horizontal),
        new(new Point(-popupSize.Width - 6, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical),
        new(new Point(targetSize.Width + 6, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical),
    ];

    private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSuggestions();
    }

    private void SearchText_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => RefreshSuggestions();

    private void RefreshSuggestions()
    {
        var query = SearchText.Text.Trim();
        var matches = _actions.Where(command =>
            command.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            command.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (query.Length == 0) matches = matches.Take(6).ToArray();
        Suggestions.ItemsSource = matches;
        Suggestions.SelectedIndex = matches.Length > 0 ? 0 : -1;
        SuggestionsPopup.IsOpen = SearchText.IsKeyboardFocusWithin && matches.Length > 0;
    }

    private void SearchText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            SearchText.Clear();
            SuggestionsPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RunSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Suggestions.Items.Count > 0)
        {
            Suggestions.SelectedIndex = Math.Min(Suggestions.SelectedIndex + 1, Suggestions.Items.Count - 1);
            Suggestions.ScrollIntoView(Suggestions.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Suggestions.Items.Count > 0)
        {
            Suggestions.SelectedIndex = Math.Max(Suggestions.SelectedIndex - 1, 0);
            Suggestions.ScrollIntoView(Suggestions.SelectedItem);
            e.Handled = true;
        }
    }

    private void Suggestions_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(Suggestions, source) is not ListBoxItem item ||
            item.DataContext is not WidgetSearchAction action) return;
        Suggestions.SelectedItem = action;
        RunSelected();
    }

    private void RunSelected()
    {
        if (Suggestions.SelectedItem is not WidgetSearchAction selected) return;
        Keyboard.ClearFocus();
        SearchText.Clear();
        SuggestionsPopup.IsOpen = false;
        CommandRequested?.Invoke(selected);
    }

    public void CloseSuggestions() => SuggestionsPopup.IsOpen = false;

    public IntPtr SuggestionsWindowHandle =>
        SuggestionsPopup.IsOpen && PresentationSource.FromVisual(Suggestions) is HwndSource source
            ? source.Handle : IntPtr.Zero;

    private void SuggestionsPopup_Opened(object sender, EventArgs e) => SuggestionsOpened?.Invoke();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace CloCloWidget;

public enum WidgetSearchActionKind
{
    OpenWindow,
    OpenFolder,
    Toggle,
    Set,
    Run,
    CreateFile,
    Hide,
    Exit,
}

public sealed record WidgetSearchAction(string Label, string Keywords, WidgetSearchActionKind Kind, Action Execute, Func<bool>? IsOn = null)
{
    public string KindLabel => Kind switch
    {
        WidgetSearchActionKind.OpenWindow => "Open window",
        WidgetSearchActionKind.OpenFolder => "Open folder",
        WidgetSearchActionKind.Toggle => "Toggle",
        WidgetSearchActionKind.Set => "Set",
        WidgetSearchActionKind.CreateFile => "Create file",
        WidgetSearchActionKind.Hide => "Hide",
        WidgetSearchActionKind.Exit => "Exit",
        _ => "Run",
    };
}

public sealed record WidgetSearchSuggestion(WidgetSearchAction Action, string BadgeLabel, string BadgeColor)
{
    public string Label => Action.Label;
}

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
            command.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            command.KindLabel.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (query.Length == 0) matches = matches.Take(6).ToArray();
        Suggestions.ItemsSource = matches.Select(CreateSuggestion).ToArray();
        Suggestions.SelectedIndex = matches.Length > 0 ? 0 : -1;
        SuggestionsPopup.IsOpen = SearchText.IsKeyboardFocusWithin && matches.Length > 0;
    }

    private static WidgetSearchSuggestion CreateSuggestion(WidgetSearchAction action)
    {
        var isOn = action.Kind == WidgetSearchActionKind.Toggle && action.IsOn?.Invoke() == true;
        var badgeLabel = action.Kind == WidgetSearchActionKind.Toggle
            ? $"Toggle · {(isOn ? "On" : "Off")}" : action.KindLabel;
        var badgeColor = action.Kind switch
        {
            WidgetSearchActionKind.OpenWindow => "#FF285D99",
            WidgetSearchActionKind.OpenFolder => "#FF79509A",
            WidgetSearchActionKind.Toggle => isOn ? "#FF237A48" : "#FF59636B",
            WidgetSearchActionKind.Set => "#FF146F78",
            WidgetSearchActionKind.CreateFile => "#FF4E60B2",
            WidgetSearchActionKind.Hide => "#FF4D6273",
            WidgetSearchActionKind.Exit => "#FFA13737",
            _ => "#FF8A5A1A",
        };
        return new WidgetSearchSuggestion(action, badgeLabel, badgeColor);
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
            item.DataContext is not WidgetSearchSuggestion suggestion) return;
        Suggestions.SelectedItem = suggestion;
        RunSelected();
    }

    private void RunSelected()
    {
        if (Suggestions.SelectedItem is not WidgetSearchSuggestion selected) return;
        Keyboard.ClearFocus();
        SearchText.Clear();
        SuggestionsPopup.IsOpen = false;
        CommandRequested?.Invoke(selected.Action);
    }

    public void CloseSuggestions() => SuggestionsPopup.IsOpen = false;

    public IntPtr SuggestionsWindowHandle =>
        SuggestionsPopup.IsOpen && PresentationSource.FromVisual(Suggestions) is HwndSource source
            ? source.Handle : IntPtr.Zero;

    private void SuggestionsPopup_Opened(object sender, EventArgs e) => SuggestionsOpened?.Invoke();
}

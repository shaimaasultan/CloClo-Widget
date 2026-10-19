using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace CloCloWidget;

// The visible side of a voice shutdown (MainWindow.PushToTalk): what CloClo
// is saying, the apps that are open, and buttons that do the same as the
// spoken answers. It only reports clicks; MainWindow runs the shutdown.
// Screen readers announce the heading as it changes.
internal sealed class ShutdownConfirmWindow : Window
{
    private readonly TextBlock _heading = new()
    {
        FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ListBox _apps = new()
    {
        Margin = new Thickness(0, 12, 0, 0), MaxHeight = 220, Focusable = false,
    };
    private readonly Button _confirm = new() { Padding = new Thickness(14, 5, 14, 5), MinWidth = 90 };
    private readonly Button _force = new()
    {
        Content = "Force close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly Button _cancel = new()
    {
        Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0),
        MinWidth = 90, IsCancel = true,
    };
    private bool _closingFromCode;

    public event Action? ConfirmRequested;
    public event Action? ForceCloseRequested;
    // Also raised when the window is closed with its X or Escape.
    public event Action? CancelRequested;

    public ShutdownConfirmWindow()
    {
        Title = "Shut down";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;

        AutomationProperties.SetLiveSetting(_heading, AutomationLiveSetting.Assertive);
        AutomationProperties.SetName(_apps, "Open apps");
        _confirm.Click += (_, _) => ConfirmRequested?.Invoke();
        _force.Click += (_, _) => ForceCloseRequested?.Invoke();
        _cancel.Click += (_, _) => Close();
        Closing += (_, _) =>
        {
            if (!_closingFromCode) CancelRequested?.Invoke();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(_confirm);
        buttons.Children.Add(_force);
        buttons.Children.Add(_cancel);

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(_heading);
        content.Children.Add(_detail);
        content.Children.Add(_apps);
        content.Children.Add(buttons);
        Content = content;
    }

    // confirmText null hides the confirm button (nothing to confirm, as
    // while apps are closing); the app list hides when it's empty.
    public void ShowStep(string heading, string detail, IEnumerable<string> apps,
        string? confirmText, bool offerForceClose)
    {
        _heading.Text = heading;
        _detail.Text = detail;
        _apps.Items.Clear();
        foreach (var app in apps) _apps.Items.Add(app);
        _apps.Visibility = _apps.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _confirm.Content = confirmText ?? "";
        _confirm.Visibility = confirmText != null ? Visibility.Visible : Visibility.Collapsed;
        _force.Visibility = offerForceClose ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, heading);
        if (IsLoaded && System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(_heading) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    // Closes without counting as "cancel".
    public void CloseFromCode()
    {
        _closingFromCode = true;
        Close();
    }
}

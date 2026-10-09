using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace CloCloWidget;

public partial class WindowsStatusWindow : Window
{
    private bool _closed;

    public WindowsStatusWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            ConnectionText.Text = string.Empty;
            DrivesText.Text = string.Empty;
        };
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await Task.Run(WindowsStatus.Read);
            if (_closed) return;
            ConnectionText.Text = snapshot.NetworkAvailable switch
            {
                true => "A network connection is available",
                false => "No network connection is available",
                _ => "Connection status unavailable",
            };
            DrivesText.Text = snapshot.Drives.Count == 0
                ? "No fixed drive information available"
                : string.Join(Environment.NewLine, snapshot.Drives.Select(drive =>
                    $"{drive.Root}  {drive.FreeBytes / 1073741824d:N1} GiB available / {drive.TotalBytes / 1073741824d:N1} GiB total"));
        }
        catch
        {
            if (!_closed)
            {
                ConnectionText.Text = "Connection status unavailable";
                DrivesText.Text = "Drive information unavailable";
            }
        }
        finally
        {
            if (!_closed) RefreshButton.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

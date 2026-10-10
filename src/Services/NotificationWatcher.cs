using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace CloCloWidget;

public readonly record struct LatestNotification(string AppName, string Title, string Body, byte[]? IconPng, uint Id, DateTimeOffset CreatedAt);

// The most recent toast notification, via UserNotificationListener — the
// same store Windows' own Action Center reads from. This API has
// historically required a package identity, which an unpackaged Win32 exe
// like this one doesn't have — but RequestAccessAsync() returned Allowed
// without incident when tested on this machine, so it's evidently not a
// hard requirement on this Windows version.
public static class NotificationWatcher
{
    public static async Task<LatestNotification?> GetLatestAsync()
    {
        try
        {
            var listener = UserNotificationListener.Current;
            var status = listener.GetAccessStatus();
            if (status == UserNotificationListenerAccessStatus.Unspecified)
                status = await listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed) return null;

            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var latest = notifications.OrderByDescending(n => n.CreationTime).FirstOrDefault();
            if (latest == null) return null;
            return await ReadNotificationAsync(latest, includeDetails: true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<LatestNotification?> ReadNotificationAsync(UserNotification latest, bool includeDetails)
    {
        try
        {
            var appInfo = latest.AppInfo;
            var appName = appInfo?.DisplayInfo?.DisplayName ?? "Notification";
            var lines = latest.Notification.Visual
                .GetBinding(KnownNotificationBindings.ToastGeneric)
                ?.GetTextElements()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray() ?? Array.Empty<string>();

            var title = lines.Length > 0 ? lines[0] : appName;
            var body = includeDetails && lines.Length > 1 ? string.Join(" ", lines.Skip(1)) : "";
            var icon = includeDetails ? await TryGetIconAsync(appInfo) : null;
            return new LatestNotification(appName, title, body, icon, latest.Id, latest.CreationTime);
        }
        catch
        {
            return null;
        }
    }

    // The raw image bytes of the sending app's 32x32 logo — decoded for
    // display by KeeperView.DecodeIcon.
    private static async Task<byte[]?> TryGetIconAsync(Windows.ApplicationModel.AppInfo? appInfo)
    {
        try
        {
            var logo = appInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(32, 32));
            if (logo == null) return null;

            using var stream = await logo.OpenReadAsync();
            using var reader = new DataReader(stream);
            // A 32x32 logo is a few KB; anything far bigger isn't worth
            // reading into memory from another app's package.
            const ulong maxLogoBytes = 512 * 1024;
            if (stream.Size == 0 || stream.Size > maxLogoBytes) return null;
            var size = (uint)stream.Size;

            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}

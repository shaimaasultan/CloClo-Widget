using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace CloCloWidget;

public readonly record struct NowPlayingInfo(string Title, string Artist);

// What's playing in one specific app, via the same system media session
// manager Windows itself uses for the volume flyout's media controls.
// Unpackaged desktop apps can call this without any special permission
// prompt on modern Windows.
public static class NowPlaying
{
    // aumidMatches are substrings checked against each session's
    // SourceAppUserModelId — e.g. Spotify's AUMID always contains
    // "spotify" regardless of Store vs. Win32 install. A browser doesn't
    // have a per-site AUMID, so "YouTube" is approximated as "whatever's
    // playing in Edge or Chrome" — the session's own title/artist still
    // comes from the actual page, so it reads correctly either way.
    public static async Task<NowPlayingInfo?> GetForAppAsync(params string[] aumidMatches)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions().FirstOrDefault(s =>
                aumidMatches.Any(m => s.SourceAppUserModelId?.Contains(m, StringComparison.OrdinalIgnoreCase) == true));
            if (session == null) return null;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

            return new NowPlayingInfo(props.Title, props.Artist ?? "");
        }
        catch
        {
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CloCloWidget;

// The one way CloClo reaches the internet. It makes a small number of public
// GET requests and doesn't need cookies or cross-host redirects. One
// connection pool is shared by the process, and connections are renewed
// periodically so DNS changes are seen.
//
// Every request passes two checks, and anything not on AllowedHosts is
// refused before a byte leaves the PC:
// - AllowListHandler, on the request: HTTPS GET on port 443 to an allowed
//   host name, no user name or password in the URL.
// - ConnectAsync, on the connection itself: again an allowed host on 443,
//   and only to public internet addresses - a name that resolves into the
//   local network (a router, a NAS, 127.0.0.1) is refused, so a changed DNS
//   answer can't point CloClo at a device at home.
// No proxy is used: the connection must go straight to the allowed host,
// where the second check sees it. Windows Firewall rules added at install
// (see the README's Security section) then hold the whole process - this
// code, the speech and language model runtimes, anything else in it - to
// HTTPS on the public internet.
internal static class NetworkClient
{
    // CloClo's whole list of internet destinations. Adding a feature that
    // goes online means adding its host here, nowhere else.
    private static readonly HashSet<string> AllowedHosts = new(
        new[]
        {
            "api.open-meteo.com",            // weather
            "geocoding-api.open-meteo.com",  // place search
            "nominatim.openstreetmap.org",   // "Use my location" (place name)
            "trends.google.com",             // Google Trends (opt-in)
            "export.arxiv.org",              // arXiv newest papers (opt-in)
        }.Concat(TrendsService.WikipediaLanguages.Select(l => $"{l.Code}.wikipedia.org")), // most-read (opt-in)
        StringComparer.OrdinalIgnoreCase);

    private const int HttpsPort = 443;

    private static readonly HttpClient Client = CreateClient();

    public static HttpClient Shared => Client;

    // For the README and tests: the hosts CloClo may contact.
    public static IReadOnlyCollection<string> Hosts => AllowedHosts;

    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Port == HttpsPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        AllowedHosts.Contains(uri.IdnHost);

    private static HttpClient CreateClient()
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectCallback = ConnectAsync,
        };

        var client = new HttpClient(new AllowListHandler(sockets))
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloCloWidget/1.0 (desktop weather widget)");
        return client;
    }

    private sealed class AllowListHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || request.Method != HttpMethod.Get || !IsAllowed(uri))
                throw Refused(request.RequestUri?.IdnHost ?? "(no address)");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var target = context.DnsEndPoint;
        if (target.Port != HttpsPort || !AllowedHosts.Contains(target.Host))
            throw Refused(target.Host);

        var addresses = (await Dns.GetHostAddressesAsync(target.Host, cancellationToken))
            .Where(IsPublic).ToArray();
        if (addresses.Length == 0)
            throw new HttpRequestException($"{target.Host} didn't resolve to a public internet address, so CloClo didn't connect.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, HttpsPort, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static HttpRequestException Refused(string host) =>
        new($"CloClo refused a request to {host}: only HTTPS GET requests to the hosts on its allowed list may leave this PC.");

    // Not loopback, private, link-local, carrier-grade NAT, multicast or
    // otherwise reserved: only addresses out on the internet.
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224 ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                     (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            // Only global unicast (2000::/3) is public.
            return (b[0] & 0xE0) == 0x20;
        }
        return false;
    }
}

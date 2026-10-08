using System;
using System.Net.Http;

namespace CloCloWidget;

// The app makes a small number of public GET requests and doesn't need
// cookies or cross-host redirects. Share one connection pool for the
// process, while periodically renewing connections so DNS changes are seen.
internal static class NetworkClient
{
    private static readonly HttpClient Client = CreateClient();

    public static HttpClient Shared => Client;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloCloWidget/1.0 (desktop weather widget)");
        return client;
    }
}

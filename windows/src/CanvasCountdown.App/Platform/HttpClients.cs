using System.Net;
using System.Net.Http.Headers;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// The app's connections to the outside, made in one place.
/// </summary>
/// <remarks>
/// There are only ever four destinations: the Canvas feed address the user
/// gave, GitHub's release list, the update file it points to, and — if the
/// assistant is switched on — the AI service the user chose. Nothing else is
/// contacted, and no request carries anything that identifies the person.
/// </remarks>
internal static class HttpClients
{
    private static readonly ProductInfoHeaderValue UserAgent = new("CanvasCountdown", AppEnvironment.Version);

    /// <summary>For the assistant, the local model server and the release check.</summary>
    public static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            // The longest single request is a model answering on a slow PC;
            // each caller sets its own tighter limit where it has one.
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        return client;
    }

    /// <summary>
    /// For the Canvas feed. Redirects are not followed automatically: the
    /// fetcher follows them itself, so it can refuse one that leaves HTTPS.
    /// </summary>
    public static HttpMessageHandler CreateFeedHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        UseCookies = false,
    };
}

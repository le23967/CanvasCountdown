using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace CanvasCountdown;

/// <summary>Downloads a calendar feed.</summary>
public interface IFeedFetcher
{
    Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken = default);
}

public enum CanvasFeedFetchError
{
    NonHttpResponse,
    InsecureRedirect,
    HttpStatus,
    EmptyResponse,
    ResponseTooLarge,
    NetworkFailure,
}

/// <summary>
/// Why a download failed. The message is for people; <see cref="DiagnosticCode"/>
/// is for exported diagnostics and never contains the private request URL or
/// the underlying network error text.
/// </summary>
public sealed class CanvasFeedFetchException : Exception
{
    private CanvasFeedFetchException(CanvasFeedFetchError error, string message, int? status = null, int? maximumBytes = null, int? networkCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
        Status = status;
        MaximumBytes = maximumBytes;
        NetworkCode = networkCode;
    }

    public CanvasFeedFetchError Error { get; }

    public int? Status { get; }

    public int? MaximumBytes { get; }

    public int? NetworkCode { get; }

    public string DiagnosticCode => Error switch
    {
        CanvasFeedFetchError.NonHttpResponse => "fetch.non-http-response",
        CanvasFeedFetchError.InsecureRedirect => "fetch.insecure-redirect",
        CanvasFeedFetchError.HttpStatus => $"fetch.http.{Status}",
        CanvasFeedFetchError.EmptyResponse => "fetch.empty-response",
        CanvasFeedFetchError.ResponseTooLarge => "fetch.response-too-large",
        _ => NetworkCode is { } code ? $"fetch.url-error.{code}" : "fetch.network-error",
    };

    public static CanvasFeedFetchException NonHttpResponse() => new(CanvasFeedFetchError.NonHttpResponse, "Canvas returned an unexpected response.");

    public static CanvasFeedFetchException InsecureRedirect() => new(CanvasFeedFetchError.InsecureRedirect, "Canvas redirected the calendar request to an insecure address.");

    public static CanvasFeedFetchException HttpStatus(int status) => new(CanvasFeedFetchError.HttpStatus, $"Canvas returned an error (HTTP {status}).", status: status);

    public static CanvasFeedFetchException EmptyResponse() => new(CanvasFeedFetchError.EmptyResponse, "Canvas returned an empty calendar feed.");

    public static CanvasFeedFetchException ResponseTooLarge(int maximumBytes)
        => new(CanvasFeedFetchError.ResponseTooLarge, $"The calendar feed exceeds the {maximumBytes / 1_048_576} MB safety limit.", maximumBytes: maximumBytes);

    public static CanvasFeedFetchException NetworkFailure(int? code = null, Exception? inner = null)
        => new(CanvasFeedFetchError.NetworkFailure, "The Canvas calendar feed could not be downloaded.", networkCode: code, inner: inner);
}

/// <summary>
/// Downloads a calendar feed over HTTPS, reading the body in the chunks the
/// network delivers and stopping as soon as the safety limit would be passed.
/// </summary>
/// <remarks>
/// Redirects are followed here rather than by the handler, so that one which
/// leaves HTTPS is refused instead of quietly downgrading a request that
/// carries a private token in its address. No cookies are kept and nothing is
/// cached: every refresh asks Canvas again.
/// </remarks>
public sealed class CanvasFeedFetcher : IFeedFetcher, IDisposable
{
    public const int DefaultMaximumResponseSize = 10 * 1_048_576;
    private const int MaximumRedirects = 10;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResourceTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient client;
    private readonly int maximumResponseSize;

    /// <param name="handler">Must not follow redirects by itself (the app passes one that does not).</param>
    public CanvasFeedFetcher(HttpMessageHandler handler, int maximumResponseSize = DefaultMaximumResponseSize, bool disposeHandler = true)
    {
        client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        this.maximumResponseSize = Math.Max(1, maximumResponseSize);
    }

    /// <summary>
    /// Number of body chunks the last completed download appended. A per-byte
    /// implementation would report one chunk per byte.
    /// </summary>
    public int LastResponseChunkCount { get; private set; }

    public async Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var current = FeedUrlValidator.Validated(url);
        using var resource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        resource.CancelAfter(ResourceTimeout);

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.9));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var response = await SendAsync(request, resource.Token, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (hop >= MaximumRedirects || response.Headers.Location is not { } location)
                {
                    throw CanvasFeedFetchException.HttpStatus((int)response.StatusCode);
                }
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw CanvasFeedFetchException.InsecureRedirect();
                }
                current = next;
                continue;
            }

            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                throw CanvasFeedFetchException.HttpStatus(status);
            }
            if (response.Content.Headers.ContentLength is { } declared && declared > maximumResponseSize)
            {
                throw CanvasFeedFetchException.ResponseTooLarge(maximumResponseSize);
            }

            return await ReadBodyAsync(response, resource.Token, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken resourceToken, CancellationToken callerToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(resourceToken);
        requestTimeout.CancelAfter(RequestTimeout);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            // URLSession's timed-out code, so a timeout reads the same in
            // diagnostics on both platforms.
            throw CanvasFeedFetchException.NetworkFailure(-1001, exception);
        }
        catch (HttpRequestException exception)
        {
            throw CanvasFeedFetchException.NetworkFailure((int?)exception.HttpRequestError, exception);
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken resourceToken, CancellationToken callerToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        var chunks = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(resourceToken);
            using var body = new MemoryStream(capacity: (int)Math.Min(response.Content.Headers.ContentLength ?? 0, maximumResponseSize));
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), resourceToken);
                if (read == 0)
                {
                    break;
                }
                if (body.Length + read > maximumResponseSize)
                {
                    throw CanvasFeedFetchException.ResponseTooLarge(maximumResponseSize);
                }
                body.Write(buffer, 0, read);
                chunks++;
            }
            LastResponseChunkCount = chunks;
            if (body.Length == 0)
            {
                throw CanvasFeedFetchException.EmptyResponse();
            }
            return body.ToArray();
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw CanvasFeedFetchException.NetworkFailure(-1001, exception);
        }
        catch (IOException exception)
        {
            throw CanvasFeedFetchException.NetworkFailure(null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw CanvasFeedFetchException.NetworkFailure((int?)exception.HttpRequestError, exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() => client.Dispose();
}

/// <summary>
/// Refuses every download, for an isolated launch: it cannot reach Canvas, so
/// it cannot read anyone's feed.
/// </summary>
public sealed class OfflineFeedFetcher : IFeedFetcher
{
    public Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken = default)
        => Task.FromException<byte[]>(CanvasFeedFetchException.NetworkFailure());
}

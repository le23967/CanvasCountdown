using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CanvasCountdown;

public interface IReleaseChecking
{
    /// <summary>
    /// The newest published Windows release, or null when the project has never
    /// published one.
    /// </summary>
    Task<AppRelease?> LatestReleaseAsync(CancellationToken cancellationToken = default);
}

public enum ReleaseCheckError
{
    NonHttpResponse,
    HttpStatus,
    RateLimited,
    UnreadableResponse,
    NetworkFailure,
}

public sealed class ReleaseCheckException : Exception
{
    public ReleaseCheckException(ReleaseCheckError error, int? statusCode = null, int? code = null, Exception? innerException = null)
        : base(Describe(error, statusCode), innerException)
    {
        Error = error;
        StatusCode = statusCode;
        Code = code;
    }

    public ReleaseCheckError Error { get; }

    /// <summary>The HTTP status, for <see cref="ReleaseCheckError.HttpStatus"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// What went wrong underneath a <see cref="ReleaseCheckError.NetworkFailure"/>,
    /// as an <see cref="HttpRequestError"/> value, when there is one. For
    /// diagnostics only; never shown.
    /// </summary>
    public int? Code { get; }

    private static string Describe(ReleaseCheckError error, int? statusCode) => error switch
    {
        ReleaseCheckError.NonHttpResponse => "The update service returned an unexpected response.",
        ReleaseCheckError.HttpStatus => $"The update check failed (HTTP {statusCode}).",
        ReleaseCheckError.RateLimited => "GitHub is rate limiting update checks. Try again later.",
        ReleaseCheckError.UnreadableResponse => "The newest release could not be read.",
        ReleaseCheckError.NetworkFailure => "The update check could not reach GitHub.",
        _ => "The update check failed.",
    };
}

/// <summary>Asks GitHub what the newest Windows release is.</summary>
/// <remarks>
/// <para>
/// Deliberately unauthenticated: this reads a public list of releases and
/// nothing else, so sending anyone's credentials to do it would be collecting a
/// risk for no gain. Unauthenticated callers get 60 requests an hour, and this
/// asks for one a day.
/// </para>
/// <para>
/// The repository publishes the Mac app too, tagged <c>v1.3.0</c>, and GitHub's
/// "latest release" is whichever was published last — usually the Mac one. So
/// this lists releases and takes the highest <c>win-v</c> version that is
/// neither a draft nor a pre-release, and never looks at a <c>v</c> tag.
/// </para>
/// </remarks>
public sealed class GitHubReleaseChecker : IReleaseChecking, IDisposable
{
    /// <summary>
    /// Where the app publishes. Held here rather than in preferences: pointing
    /// an update check somewhere else is not a setting, it is a compromise.
    /// </summary>
    public const string Repository = "le23967/CanvasCountdown";

    /// <summary>Windows releases are tagged <c>win-v1.0.0</c>, <c>win-v1.1.0</c>, …</summary>
    public const string TagPrefix = "win-v";

    /// <summary>
    /// GitHub refuses API requests that do not say who is asking. The product
    /// name is enough; nothing about the person is sent.
    /// </summary>
    public const string UserAgent = "CanvasCountdown-Windows";

    /// <summary>One page, newest first, is years of releases from both platforms.</summary>
    public const int PageSize = 100;

    /// <summary>A check that has not answered in this long has failed.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient client;
    private readonly UpdateArchitecture architecture;
    private readonly string repository;
    private readonly TimeSpan timeout;

    /// <param name="architecture">Which installer to look for: this PC's.</param>
    /// <param name="handler">The transport. Tests pass a fake; null uses the network.</param>
    /// <param name="repository">The repository to read.</param>
    /// <param name="timeout">How long to wait for an answer; <see cref="RequestTimeout"/> unless a test needs less.</param>
    public GitHubReleaseChecker(
        UpdateArchitecture architecture,
        HttpMessageHandler? handler = null,
        string repository = Repository,
        TimeSpan? timeout = null)
    {
        this.architecture = architecture;
        this.repository = repository;
        this.timeout = timeout ?? RequestTimeout;
        client = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Lets go of the connection. A transport passed in stays the caller's.</summary>
    public void Dispose() => client.Dispose();

    public async Task<AppRelease?> LatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases?per_page={PageSize}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, null));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        HttpStatusCode status;
        string body;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new ReleaseCheckException(ReleaseCheckError.NetworkFailure, innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ReleaseCheckException(ReleaseCheckError.NetworkFailure, code: (int)exception.HttpRequestError, innerException: exception);
        }
        catch (IOException exception)
        {
            throw new ReleaseCheckException(ReleaseCheckError.NetworkFailure, innerException: exception);
        }

        switch ((int)status)
        {
            case 200:
                break;
            case 403 or 429:
                throw new ReleaseCheckException(ReleaseCheckError.RateLimited, statusCode: (int)status);
            case 404:
                // Nothing published yet. Not an error worth showing anyone.
                return null;
            default:
                throw new ReleaseCheckException(ReleaseCheckError.HttpStatus, statusCode: (int)status);
        }

        return Parse(body, architecture, repository);
    }

    /// <summary>The installer's published name for a version and processor.</summary>
    public static string InstallerFileName(AppVersion version, UpdateArchitecture architecture)
        => $"CanvasCountdown-Windows-{version}-{architecture.InstallerToken()}-setup.exe";

    /// <summary>
    /// Reads the release list, and refuses anything it cannot make a version
    /// out of rather than presenting an update it cannot name.
    /// </summary>
    /// <returns>The highest Windows release that is neither a draft nor a pre-release, or null when there is none.</returns>
    /// <exception cref="ReleaseCheckException">The response is not a list of releases.</exception>
    public static AppRelease? Parse(string json, UpdateArchitecture architecture, string repository = Repository)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ReleaseCheckException(ReleaseCheckError.UnreadableResponse, innerException: exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ReleaseCheckException(ReleaseCheckError.UnreadableResponse);
            }

            JsonElement? newest = null;
            AppVersion? newestVersion = null;
            string? newestTag = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || IsTrue(release, "draft")
                    || IsTrue(release, "prerelease")
                    || StringValue(release, "tag_name") is not { } tag
                    || !tag.StartsWith(TagPrefix, StringComparison.Ordinal)
                    || AppVersion.Parse(tag["win-".Length..]) is not { } version)
                {
                    continue;
                }
                if (newestVersion is null || version > newestVersion)
                {
                    newest = release;
                    newestVersion = version;
                    newestTag = tag;
                }
            }

            if (newest is not { } chosen || newestVersion is null || newestTag is null)
            {
                return null;
            }

            var pageUrl = AbsoluteUri(StringValue(chosen, "html_url"))
                ?? new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(newestTag)}");

            return new AppRelease
            {
                Version = newestVersion,
                Tag = newestTag,
                Name = StringValue(chosen, "name") ?? newestTag,
                Notes = StringValue(chosen, "body") ?? string.Empty,
                Installer = Installer(chosen, InstallerFileName(newestVersion, architecture)),
                PageUrl = pageUrl,
            };
        }
    }

    /// <summary>
    /// The named installer, with the size and SHA-256 digest GitHub published
    /// for it. An installer without both cannot be checked after downloading,
    /// so it is treated as no installer at all, and the release page is
    /// offered instead.
    /// </summary>
    private static ReleaseInstaller? Installer(JsonElement release, string fileName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !string.Equals(StringValue(asset, "name"), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var url = AbsoluteUri(StringValue(asset, "browser_download_url"));
            var digest = Sha256(StringValue(asset, "digest"));
            var hasSize = asset.TryGetProperty("size", out var sizeElement)
                && sizeElement.ValueKind == JsonValueKind.Number
                && sizeElement.TryGetInt64(out var size)
                && size >= 0;
            if (url is null || digest is null || !hasSize)
            {
                return null;
            }
            return new ReleaseInstaller
            {
                FileName = fileName,
                Url = url,
                Size = sizeElement.GetInt64(),
                Sha256 = digest,
            };
        }
        return null;
    }

    /// <summary>GitHub writes <c>"sha256:&lt;64 hex digits&gt;"</c>. Anything else is not a digest this app can check.</summary>
    private static string? Sha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static bool IsTrue(JsonElement container, string key)
        => container.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? StringValue(JsonElement container, string key)
        => container.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? AbsoluteUri(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
}

/// <summary>
/// Never reaches the network. Used by automated runs, so no test depends on
/// what happens to be published today.
/// </summary>
public sealed class InertReleaseChecker : IReleaseChecking
{
    public Task<AppRelease?> LatestReleaseAsync(CancellationToken cancellationToken = default) => Task.FromResult<AppRelease?>(null);
}

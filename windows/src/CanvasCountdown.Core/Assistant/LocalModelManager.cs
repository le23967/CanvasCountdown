using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CanvasCountdown;

/// <summary>A model already downloaded on this PC.</summary>
public sealed record LocalModel
{
    public required string Name { get; init; }

    public required long SizeBytes { get; init; }

    public string Id => Name;

    /// <summary>The size in the units a file browser uses: "4.7 GB", not a count of bytes.</summary>
    public string SizeDescription(CultureInfo culture) => DescribeSize(SizeBytes, culture);

    /// <summary>
    /// Foundation's file-style byte count: decimal units, no fraction for
    /// kilobytes, one for megabytes and two above, halves rounded up, trailing
    /// zeros dropped, and a size that rounds up to a thousand of one unit
    /// written as one of the next.
    /// </summary>
    public static string DescribeSize(long bytes, CultureInfo culture)
    {
        if (bytes == 0)
        {
            return "Zero KB";
        }
        if (bytes is > -1000 and < 1000)
        {
            return bytes.ToString(culture) + (bytes is 1 or -1 ? " byte" : " bytes");
        }

        decimal value = bytes;
        var magnitude = Math.Abs(value);
        var index = 0;
        while (index < SizeUnits.Length - 1 && magnitude >= SizeUnits[index + 1].Size)
        {
            index++;
        }
        var rounded = Math.Round(value / SizeUnits[index].Size, SizeUnits[index].Digits, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) >= 1000 && index < SizeUnits.Length - 1)
        {
            index++;
            rounded = Math.Round(value / SizeUnits[index].Size, SizeUnits[index].Digits, MidpointRounding.AwayFromZero);
        }
        var format = SizeUnits[index].Digits switch
        {
            0 => "0",
            1 => "0.#",
            _ => "0.##",
        };
        return rounded.ToString(format, culture) + " " + SizeUnits[index].Name;
    }

    private static readonly (string Name, decimal Size, int Digits)[] SizeUnits =
    [
        ("KB", 1e3m, 0),
        ("MB", 1e6m, 1),
        ("GB", 1e9m, 2),
        ("TB", 1e12m, 2),
        ("PB", 1e15m, 2),
        ("EB", 1e18m, 2),
    ];
}

/// <summary>
/// A model the app offers to fetch, so nobody has to go hunting on a model
/// hosting site to get started.
/// </summary>
public sealed record SuggestedModel
{
    public required string Name { get; init; }

    /// <summary>
    /// Approximate download size. Stated as approximate because the real figure
    /// depends on the quantisation the registry serves.
    /// </summary>
    public required string ApproximateSize { get; init; }

    public required string Note { get; init; }

    public string Id => Name;

    /// <summary>
    /// Small, generally-available models that behave reasonably on the sort of
    /// short extraction this app asks for. Ordered smallest first, because a
    /// student on a laptop cares about the download.
    /// </summary>
    public static ValueList<SuggestedModel> All { get; } =
    [
        new SuggestedModel
        {
            Name = "llama3.2:3b",
            ApproximateSize = "about 2 GB",
            Note = "Smallest and quickest. Weakest at reading dates.",
        },
        new SuggestedModel
        {
            Name = "qwen2.5:7b",
            ApproximateSize = "about 4.7 GB",
            Note = "Good at producing structured answers.",
        },
        new SuggestedModel
        {
            Name = "llama3.1:8b",
            ApproximateSize = "about 4.7 GB",
            Note = "A balanced default.",
        },
        new SuggestedModel
        {
            Name = "mistral:7b",
            ApproximateSize = "about 4.1 GB",
            Note = "Fast, competent at short instructions.",
        },
    ];
}

public enum LocalModelError
{
    ServerUnreachable,
    RequestFailed,
    Cancelled,
}

public static class LocalModelErrorExtensions
{
    public static string Description(this LocalModelError error) => error switch
    {
        LocalModelError.ServerUnreachable => "Ollama is not responding on this PC.",
        LocalModelError.RequestFailed => "That model operation did not complete.",
        LocalModelError.Cancelled => "The download was cancelled.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}

public sealed class LocalModelException : Exception
{
    public LocalModelException(LocalModelError error)
        : base(error.Description())
    {
        Error = error;
    }

    public LocalModelError Error { get; }
}

/// <summary>
/// Lists, downloads and removes local models.
/// </summary>
/// <remarks>
/// This manages <em>models</em>, not the model server itself. The app does not
/// install or launch another program — the macOS build is sandboxed and cannot,
/// and this one keeps the same boundary — so it detects whether the server is
/// running and otherwise shows the commands to run; everything after that
/// happens here over the server's local HTTP API, without
/// anyone visiting a model hosting site.
/// </remarks>
public interface ILocalModelManaging
{
    Task<bool> IsReachableAsync(Uri baseUrl, CancellationToken cancellationToken = default);

    Task<ValueList<LocalModel>> InstalledModelsAsync(Uri baseUrl, CancellationToken cancellationToken = default);

    Task PullAsync(string name, Uri baseUrl, IProgress<double> progress, CancellationToken cancellationToken = default);

    Task DeleteAsync(string name, Uri baseUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ILocalModelManaging"/> over the local server's model API, which
/// sits beside its chat endpoint.
/// </summary>
/// <remarks>
/// Holds nothing that changes, so one instance can serve concurrent requests.
/// Every failure surfaces as a <see cref="LocalModelException"/>, except that
/// asking whether the server is there simply answers no.
/// </remarks>
public sealed class OllamaModelManager : ILocalModelManaging
{
    /// <summary>How long a request may go without hearing anything back.</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Asking whether the server is there is quick, or it is not there.</summary>
    public static TimeSpan ReachabilityTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>A multi-gigabyte pull needs a long overall budget.</summary>
    public static TimeSpan DownloadTimeout { get; } = TimeSpan.FromSeconds(3_600);

    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;
    private readonly TimeSpan reachabilityTimeout;
    private readonly TimeSpan downloadTimeout;

    /// <param name="handler">
    /// The transport, for tests. Without one, requests go through a shared
    /// client that keeps no cookies.
    /// </param>
    public OllamaModelManager(HttpMessageHandler? handler = null)
        : this(handler, RequestTimeout, ReachabilityTimeout, DownloadTimeout)
    {
    }

    internal OllamaModelManager(
        HttpMessageHandler? handler,
        TimeSpan requestTimeout,
        TimeSpan reachabilityTimeout,
        TimeSpan downloadTimeout)
    {
        client = AssistantHttp.ClientFor(handler);
        this.requestTimeout = requestTimeout;
        this.reachabilityTimeout = reachabilityTimeout;
        this.downloadTimeout = downloadTimeout;
    }

    /// <summary>The API sits beside the chat endpoint: <c>.../v1</c> becomes the root.</summary>
    public static Uri ApiRoot(Uri baseUrl)
        => AssistantEndpoint.LastPathComponent(baseUrl) == "v1"
            ? AssistantEndpoint.DeletingLastPathComponent(baseUrl)
            : baseUrl;

    public async Task<bool> IsReachableAsync(Uri baseUrl, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(reachabilityTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(baseUrl, "api/tags"));
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            return IsSuccess(response);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<ValueList<LocalModel>> InstalledModelsAsync(Uri baseUrl, CancellationToken cancellationToken = default)
    {
        byte[] data;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(requestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(baseUrl, "api/tags"));
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!IsSuccess(response))
                {
                    throw new LocalModelException(LocalModelError.ServerUnreachable);
                }
                data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (LocalModelException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new LocalModelException(LocalModelError.ServerUnreachable);
            }
        }

        var models = new List<LocalModel>();
        try
        {
            using var document = JsonDocument.Parse(data, AssistantJson.DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !AssistantJson.TryGetFirst(document.RootElement, "models", out var entries)
                || !AssistantJson.IsArrayOfObjects(entries))
            {
                throw new LocalModelException(LocalModelError.RequestFailed);
            }
            foreach (var entry in entries.EnumerateArray())
            {
                if (AssistantJson.StringValue(entry, "name") is not { } name)
                {
                    continue;
                }
                models.Add(new LocalModel { Name = name, SizeBytes = AssistantJson.WholeNumberValue(entry, "size") ?? 0 });
            }
        }
        catch (JsonException)
        {
            throw new LocalModelException(LocalModelError.RequestFailed);
        }
        return models.OrderBy(model => model.Name, StringComparer.Ordinal).ToValueList();
    }

    /// <summary>Streams progress so a multi-gigabyte download is not a frozen button.</summary>
    /// <remarks>
    /// The download has <see cref="DownloadTimeout"/> overall, and fails as
    /// unreachable if the server goes <see cref="RequestTimeout"/> without
    /// sending anything.
    /// </remarks>
    public async Task PullAsync(string name, Uri baseUrl, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(downloadTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl, "api/pull"))
            {
                Content = AssistantJson.Content(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", name);
                    writer.WriteBoolean("stream", true);
                    writer.WriteEndObject();
                }),
            };
            using var response = await WithinIdleTimeoutAsync(
                token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
                overall.Token).ConfigureAwait(false);
            if (!IsSuccess(response))
            {
                throw new LocalModelException(LocalModelError.ServerUnreachable);
            }

            var stream = await response.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                // One JSON object per line, each reporting how far along it is.
                while (await WithinIdleTimeoutAsync(token => reader.ReadLineAsync(token).AsTask(), overall.Token)
                           .ConfigureAwait(false) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReadProgressLine(line) is not { } update)
                    {
                        continue;
                    }
                    if (update.Fraction is { } fraction)
                    {
                        progress.Report(fraction);
                    }
                    if (update.Failed)
                    {
                        throw new LocalModelException(LocalModelError.RequestFailed);
                    }
                }
            }
            progress.Report(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new LocalModelException(LocalModelError.Cancelled);
        }
        catch (LocalModelException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LocalModelException(LocalModelError.ServerUnreachable);
        }
    }

    public async Task DeleteAsync(string name, Uri baseUrl, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint(baseUrl, "api/delete"))
            {
                Content = AssistantJson.Content(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", name);
                    writer.WriteEndObject();
                }),
            };
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!IsSuccess(response))
            {
                throw new LocalModelException(LocalModelError.RequestFailed);
            }
        }
        catch (LocalModelException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LocalModelException(LocalModelError.RequestFailed);
        }
    }

    private static Uri Endpoint(Uri baseUrl, string path) => AssistantEndpoint.RequestUri(ApiRoot(baseUrl), path);

    private static bool IsSuccess(HttpResponseMessage response) => (int)response.StatusCode is >= 200 and <= 299;

    private async Task<T> WithinIdleTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken outer)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(outer);
        idle.CancelAfter(requestTimeout);
        return await operation(idle.Token).ConfigureAwait(false);
    }

    /// <summary>What one progress line says, or null when it is not a JSON object.</summary>
    private static (double? Fraction, bool Failed)? ReadProgressLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, AssistantJson.DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            double? fraction = null;
            if (AssistantJson.NumberValue(root, "total") is { } total
                && AssistantJson.NumberValue(root, "completed") is { } completed
                && total > 0)
            {
                fraction = Math.Min(1, completed / total);
            }
            var failed = AssistantJson.StringValue(root, "error") is { Length: > 0 };
            return (fraction, failed);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What the panel knows about the local server after asking it.</summary>
public sealed record LocalModelInventory
{
    public required bool IsReachable { get; init; }

    public required ValueList<LocalModel> Installed { get; init; }

    public static LocalModelInventory Unreachable { get; } = new() { IsReachable = false, Installed = [] };

    /// <summary>
    /// Refreshes what is installed, and whether the server is answering at all.
    /// </summary>
    /// <remarks>
    /// A server that does not answer lists nothing, rather than whatever it
    /// listed last time. One that answers but cannot list its models is still
    /// reachable, with nothing installed shown.
    /// </remarks>
    public static async Task<LocalModelInventory> RefreshAsync(
        ILocalModelManaging manager,
        Uri? baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (baseUrl is null || !await manager.IsReachableAsync(baseUrl, cancellationToken).ConfigureAwait(false))
        {
            return Unreachable;
        }
        try
        {
            var installed = await manager.InstalledModelsAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            return new LocalModelInventory { IsReachable = true, Installed = installed };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new LocalModelInventory { IsReachable = true, Installed = [] };
        }
    }
}

/// <summary>
/// What to run when the local model server is not installed or not started.
/// </summary>
/// <remarks>
/// The app does not do this itself: the macOS build is sandboxed and may not
/// install or launch another program, and this one keeps the same boundary.
/// Showing the exact commands, ready to copy, is the honest substitute for
/// pretending otherwise. On Windows the server is installed from the download
/// page or with winget, rather than with Homebrew.
/// </remarks>
public static class OllamaSetup
{
    public const string DownloadPage = "https://ollama.com/download";

    public const string WingetCommand = "winget install Ollama.Ollama";

    public const string ServeCommand = "ollama serve";

    /// <summary>What Settings says above the commands when nothing answers.</summary>
    public const string Instructions =
        "Canvas Countdown can manage models for you, but it does not install or start Ollama itself. Run these once:";

    /// <summary>The commands to show, in the order to run them.</summary>
    public static ValueList<string> SetupCommands { get; } = [WingetCommand, ServeCommand];

    public static string PullCommand(string model) => $"ollama pull {model}";
}

using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CanvasCountdown;

public interface IUpdateDownloading
{
    /// <summary>
    /// Fetches the installer, checks it is exactly the file that was published,
    /// and leaves it somewhere the person can find it, returning where it went.
    /// </summary>
    Task<string> DownloadAsync(AppRelease release, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the downloaded installer on. The Mac opened its disk image here;
    /// what Windows does with the installer is the app's decision.
    /// </summary>
    void Reveal(string file);
}

public enum UpdateDownloadError
{
    NoInstaller,
    HttpStatus,
    NetworkFailure,
    CouldNotSave,

    /// <summary>
    /// The file that arrived is not the one GitHub published — a different
    /// size or digest. It has been deleted.
    /// </summary>
    VerificationFailed,
}

public sealed class UpdateDownloadException : Exception
{
    public UpdateDownloadException(UpdateDownloadError error, int? statusCode = null, int? code = null, Exception? innerException = null)
        : base(Describe(error, statusCode), innerException)
    {
        Error = error;
        StatusCode = statusCode;
        Code = code;
    }

    public UpdateDownloadError Error { get; }

    /// <summary>The HTTP status, for <see cref="UpdateDownloadError.HttpStatus"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// What went wrong underneath a <see cref="UpdateDownloadError.NetworkFailure"/>,
    /// for diagnostics only; never shown.
    /// </summary>
    public int? Code { get; }

    private static string Describe(UpdateDownloadError error, int? statusCode) => error switch
    {
        UpdateDownloadError.NoInstaller => "That release does not have an installer to download.",
        UpdateDownloadError.HttpStatus => $"The download failed (HTTP {statusCode}).",
        UpdateDownloadError.NetworkFailure => "The download could not be completed.",
        UpdateDownloadError.CouldNotSave => "The download could not be saved to your Downloads folder.",
        UpdateDownloadError.VerificationFailed => "The download did not match the published release, so it was deleted.",
        _ => "The download failed.",
    };
}

/// <summary>Downloads the new installer into Downloads, and checks it.</summary>
/// <remarks>
/// <para>
/// The download is written to a partial file beside its destination while its
/// size is counted and its SHA-256 digest computed. Only a file that matches
/// both of the values GitHub published is moved to its real name; anything
/// else is deleted, so nothing unverified is ever left where it could be run.
/// </para>
/// <para>
/// It stops at a verified file on disk. Installing it is a separate step.
/// </para>
/// </remarks>
public sealed class DownloadsFolderUpdateDownloader : IUpdateDownloading, IDisposable
{
    /// <summary>A connection that sends nothing for this long is given up on.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const string PartialSuffix = ".partial";

    private readonly HttpClient client;
    private readonly string downloadsDirectory;
    private readonly Action<string> reveal;
    private readonly TimeSpan idleTimeout;

    /// <param name="downloadsDirectory">The user's Downloads folder, as Windows reports it (it can be moved).</param>
    /// <param name="handler">The transport. Tests pass a fake; null uses the network.</param>
    /// <param name="reveal">What <see cref="Reveal"/> does with a downloaded installer. Nothing, when null.</param>
    /// <param name="idleTimeout">How long a silent connection is waited on; <see cref="IdleTimeout"/> unless a test needs less.</param>
    public DownloadsFolderUpdateDownloader(
        string downloadsDirectory,
        HttpMessageHandler? handler = null,
        Action<string>? reveal = null,
        TimeSpan? idleTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsDirectory);
        this.downloadsDirectory = downloadsDirectory;
        this.reveal = reveal ?? (_ => { });
        this.idleTimeout = idleTimeout ?? IdleTimeout;
        client = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Reveal(string file) => reveal(file);

    /// <summary>Lets go of the connection. A transport passed in stays the caller's.</summary>
    public void Dispose() => client.Dispose();

    public async Task<string> DownloadAsync(AppRelease release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var installer = release.Installer ?? throw new UpdateDownloadException(UpdateDownloadError.NoInstaller);

        string destination;
        try
        {
            Directory.CreateDirectory(downloadsDirectory);
            destination = Destination(downloadsDirectory, installer.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UpdateDownloadException(UpdateDownloadError.CouldNotSave, innerException: exception);
        }

        var partial = destination + PartialSuffix;
        try
        {
            await DownloadVerifiedAsync(installer, partial, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(partial, destination, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new UpdateDownloadException(UpdateDownloadError.CouldNotSave, innerException: exception);
            }
            return destination;
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }
    }

    /// <summary>
    /// A name that does not overwrite a file already sitting in Downloads.
    /// Somebody who downloaded this by hand an hour ago should not have that
    /// replaced without being asked.
    /// </summary>
    internal static string Destination(string directory, string name)
    {
        var fileName = Path.GetFileName(name);
        if (fileName.Length == 0 || fileName != name || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Not a file name.", nameof(name));
        }

        var candidate = Path.Combine(directory, fileName);
        if (!Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var next = Path.Combine(directory, $"{stem} {suffix}{extension}");
            if (!Exists(next))
            {
                return next;
            }
        }
        throw new IOException("Every name for the download is taken.");
    }

    private async Task DownloadVerifiedAsync(ReleaseInstaller installer, string partial, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, installer.Url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(GitHubReleaseChecker.UserAgent, null));

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            throw NetworkFailure(exception);
        }

        using (response)
        {
            if ((int)response.StatusCode != 200)
            {
                throw new UpdateDownloadException(UpdateDownloadError.HttpStatus, statusCode: (int)response.StatusCode);
            }

            Stream source;
            try
            {
                source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                throw NetworkFailure(exception);
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (source.ConfigureAwait(false))
            {
                FileStream file;
                try
                {
                    file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new UpdateDownloadException(UpdateDownloadError.CouldNotSave, innerException: exception);
                }

                await using (file.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        idle.CancelAfter(idleTimeout);
                        int read;
                        try
                        {
                            read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
                        {
                            throw NetworkFailure(exception);
                        }
                        if (read == 0)
                        {
                            break;
                        }

                        received += read;
                        if (received > installer.Size)
                        {
                            // More than was published: stop before it fills the disk.
                            throw new UpdateDownloadException(UpdateDownloadError.VerificationFailed);
                        }
                        hash.AppendData(buffer, 0, read);
                        try
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new UpdateDownloadException(UpdateDownloadError.CouldNotSave, innerException: exception);
                        }
                    }

                    try
                    {
                        // To the disk itself, not just the system's cache: the
                        // file is about to be renamed into place as verified.
                        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                        file.Flush(flushToDisk: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new UpdateDownloadException(UpdateDownloadError.CouldNotSave, innerException: exception);
                    }
                }
            }

            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (received != installer.Size || !string.Equals(digest, installer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateDownloadException(UpdateDownloadError.VerificationFailed);
            }
        }
    }

    /// <summary>
    /// Anything the transport throws, including an idle timeout — but not the
    /// caller's own cancellation, which stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or IOException => true,
            _ => false,
        };

    private static UpdateDownloadException NetworkFailure(Exception exception)
        => new(
            UpdateDownloadError.NetworkFailure,
            code: exception is HttpRequestException http ? (int)http.HttpRequestError : null,
            innerException: exception);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind with its ".partial" name, which nobody will mistake
            // for an installer.
        }
    }
}

/// <summary>Writes nothing and opens nothing.</summary>
public sealed class InertUpdateDownloader : IUpdateDownloading
{
    public Task<string> DownloadAsync(AppRelease release, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new UpdateDownloadException(UpdateDownloadError.NoInstaller));

    public void Reveal(string file)
    {
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CanvasCountdown.Tests.Updates;

/// <summary>
/// Answers HTTP requests from a script, and remembers what was asked. No test
/// reaches the network.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        this.respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHttpHandler Returning(HttpStatusCode status, string body = "")
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    public static StubHttpHandler Returning(byte[] body)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));

    public static StubHttpHandler Returning(Func<Stream> body)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) }));

    public static StubHttpHandler Throwing(Exception exception)
        => new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}

/// <summary>A stream that gives up part way, as a dropped connection does.</summary>
internal sealed class FailingStream(byte[] before) : Stream
{
    private int position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position >= before.Length)
        {
            throw new IOException("The connection was reset.");
        }
        var copied = Math.Min(count, before.Length - position);
        Array.Copy(before, position, buffer, offset, copied);
        position += copied;
        return copied;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class ReleaseCheckerStub : IReleaseChecking
{
    private int calls;

    public AppRelease? Release { get; set; }

    public Exception? Failure { get; set; }

    public int CallCount => Volatile.Read(ref calls);

    public Task<AppRelease?> LatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        return Failure is null ? Task.FromResult(Release) : Task.FromException<AppRelease?>(Failure);
    }
}

internal sealed class UpdateDownloaderSpy : IUpdateDownloading
{
    public Exception? Failure { get; set; }

    public string? Revealed { get; private set; }

    public Task<string> DownloadAsync(AppRelease release, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException<string>(Failure);
        }
        if (release.Installer is not { } installer)
        {
            return Task.FromException<string>(new UpdateDownloadException(UpdateDownloadError.NoInstaller));
        }
        // Where a real one would put it, without writing anything.
        return Task.FromResult(Path.Combine(Path.GetTempPath(), "Downloads", installer.FileName));
    }

    public void Reveal(string file) => Revealed = file;
}

internal static class UpdateFixtures
{
    public const string Notes = "Copyright © 2026 le23967. MIT Licence.\n\n**A made-up change** for this test.";

    public static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("An invented installer, for tests only.");

    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static AppRelease Release(string version, bool withInstaller = true) => new()
    {
        Version = AppVersion.Parse(version)!,
        Tag = $"win-v{version}",
        Name = $"Canvas Countdown {version}",
        Notes = Notes,
        Installer = withInstaller
            ? new ReleaseInstaller
            {
                FileName = $"CanvasCountdown-Windows-{version}-x64-setup.exe",
                Url = new Uri($"https://example.invalid/CanvasCountdown-Windows-{version}-x64-setup.exe"),
                Size = InstallerBytes.Length,
                Sha256 = Sha256Of(InstallerBytes),
            }
            : null,
        PageUrl = new Uri($"https://github.com/le23967/CanvasCountdown/releases/tag/win-v{version}"),
    };
}

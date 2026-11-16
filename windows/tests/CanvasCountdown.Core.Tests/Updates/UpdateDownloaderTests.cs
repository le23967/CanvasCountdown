using System.Net;
using System.Text;

namespace CanvasCountdown.Tests.Updates;

/// <summary>
/// Downloading ends with a file that is exactly the one GitHub published, or
/// with nothing at all. Each test has a Downloads folder of its own; none
/// reaches the network.
/// </summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private const string FileName = "CanvasCountdown-Windows-1.3.0-x64-setup.exe";

    private readonly string downloads = Path.Combine(Path.GetTempPath(), "cc-downloads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(downloads))
        {
            Directory.Delete(downloads, recursive: true);
        }
    }

    [Fact]
    public async Task AVerifiedDownloadLandsInDownloads()
    {
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(UpdateFixtures.InstallerBytes));

        var file = await downloader.DownloadAsync(UpdateFixtures.Release("1.3.0"));

        Assert.Equal(Path.Combine(downloads, FileName), file);
        Assert.Equal(UpdateFixtures.InstallerBytes, await File.ReadAllBytesAsync(file));
        Assert.Equal([FileName], Directory.GetFiles(downloads).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ASizeMismatchDeletesThePartialFileAndFails()
    {
        var release = WithInstaller(installer => installer with { Size = UpdateFixtures.InstallerBytes.Length + 1 });
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(UpdateFixtures.InstallerBytes));

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(release));

        Assert.Equal(UpdateDownloadError.VerificationFailed, error.Error);
        Assert.Equal("The download did not match the published release, so it was deleted.", error.Message);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task ADigestMismatchDeletesThePartialFileAndFails()
    {
        var tampered = Encoding.UTF8.GetBytes("An invented installer, for tests only!");
        Assert.Equal(UpdateFixtures.InstallerBytes.Length, tampered.Length);
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(tampered));

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.VerificationFailed, error.Error);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    /// <summary>More than was published is stopped as it arrives, not after it has filled the disk.</summary>
    [Fact]
    public async Task MoreThanWasPublishedIsCutOff()
    {
        var oversized = new byte[1_000_000];
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(oversized));

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.VerificationFailed, error.Error);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    /// <summary>
    /// Somebody who downloaded this by hand an hour ago should not have that
    /// replaced without being asked.
    /// </summary>
    [Fact]
    public async Task AFileAlreadyInDownloadsIsNotOverwritten()
    {
        Directory.CreateDirectory(downloads);
        var existing = Path.Combine(downloads, FileName);
        await File.WriteAllTextAsync(existing, "kept");
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(UpdateFixtures.InstallerBytes));

        var file = await downloader.DownloadAsync(UpdateFixtures.Release("1.3.0"));

        Assert.Equal(Path.Combine(downloads, "CanvasCountdown-Windows-1.3.0-x64-setup 2.exe"), file);
        Assert.Equal("kept", await File.ReadAllTextAsync(existing));
        Assert.Equal(UpdateFixtures.InstallerBytes, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public void WhenEveryNameIsTakenNothingIsOverwritten()
    {
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, FileName), "1");
        for (var suffix = 2; suffix <= 99; suffix++)
        {
            File.WriteAllText(Path.Combine(downloads, $"CanvasCountdown-Windows-1.3.0-x64-setup {suffix}.exe"), "n");
        }

        Assert.Throws<IOException>(() => DownloadsFolderUpdateDownloader.Destination(downloads, FileName));
    }

    [Fact]
    public async Task AFailedResponseLeavesNothingBehind()
    {
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.HttpStatus, error.Error);
        Assert.Equal(404, error.StatusCode);
        Assert.Equal("The download failed (HTTP 404).", error.Message);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task AConnectionThatDropsLeavesNothingBehind()
    {
        var downloader = new DownloadsFolderUpdateDownloader(
            downloads,
            StubHttpHandler.Returning(() => new FailingStream(UpdateFixtures.InstallerBytes[..10])));

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.NetworkFailure, error.Error);
        Assert.Equal("The download could not be completed.", error.Message);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task AnUnreachableServerIsANetworkFailure()
    {
        var handler = StubHttpHandler.Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "Refused."));
        var downloader = new DownloadsFolderUpdateDownloader(downloads, handler);

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.NetworkFailure, error.Error);
        Assert.Equal((int)HttpRequestError.ConnectionError, error.Code);
    }

    [Fact]
    public async Task ASilentConnectionIsGivenUpOn()
    {
        var handler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var downloader = new DownloadsFolderUpdateDownloader(downloads, handler, idleTimeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.NetworkFailure, error.Error);
        Assert.Equal(TimeSpan.FromSeconds(60), DownloadsFolderUpdateDownloader.IdleTimeout);
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNothingBehind()
    {
        using var stop = new CancellationTokenSource();
        var handler = new StubHttpHandler(async (_, token) =>
        {
            await stop.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(UpdateFixtures.InstallerBytes) };
        });
        var downloader = new DownloadsFolderUpdateDownloader(downloads, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0"), stop.Token));

        Assert.False(Directory.Exists(downloads) && Directory.GetFiles(downloads).Length > 0);
    }

    [Fact]
    public async Task AReleaseWithoutAnInstallerCannotBeDownloaded()
    {
        var handler = StubHttpHandler.Returning(UpdateFixtures.InstallerBytes);
        var downloader = new DownloadsFolderUpdateDownloader(downloads, handler);

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => downloader.DownloadAsync(UpdateFixtures.Release("1.3.0", withInstaller: false)));

        Assert.Equal(UpdateDownloadError.NoInstaller, error.Error);
        Assert.Equal("That release does not have an installer to download.", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheDownloadSaysWhoIsAsking()
    {
        var handler = StubHttpHandler.Returning(UpdateFixtures.InstallerBytes);

        await new DownloadsFolderUpdateDownloader(downloads, handler).DownloadAsync(UpdateFixtures.Release("1.3.0"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://example.invalid/{FileName}", request.RequestUri?.AbsoluteUri);
        Assert.Equal("CanvasCountdown-Windows", Assert.Single(request.Headers.UserAgent).Product?.Name);
    }

    [Fact]
    public void RevealingHandsTheFileOnAndNothingElse()
    {
        var revealed = new List<string>();
        var downloader = new DownloadsFolderUpdateDownloader(downloads, StubHttpHandler.Returning(UpdateFixtures.InstallerBytes), revealed.Add);

        downloader.Reveal(Path.Combine(downloads, FileName));

        Assert.Equal([Path.Combine(downloads, FileName)], revealed);
        new DownloadsFolderUpdateDownloader(downloads).Reveal(Path.Combine(downloads, FileName));
    }

    [Fact]
    public async Task TheInertDownloaderWritesNothing()
    {
        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => new InertUpdateDownloader().DownloadAsync(UpdateFixtures.Release("1.3.0")));

        Assert.Equal(UpdateDownloadError.NoInstaller, error.Error);
        Assert.False(Directory.Exists(downloads));
    }

    [Fact]
    public void ANameThatIsNotJustAFileNameIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DownloadsFolderUpdateDownloader.Destination(downloads, "../" + FileName));
        Assert.Throws<ArgumentException>(() => DownloadsFolderUpdateDownloader.Destination(downloads, string.Empty));
    }

    [Fact]
    public void FailuresSayWhat()
    {
        Assert.Equal("The download could not be saved to your Downloads folder.", new UpdateDownloadException(UpdateDownloadError.CouldNotSave).Message);
    }

    private static AppRelease WithInstaller(Func<ReleaseInstaller, ReleaseInstaller> change)
    {
        var release = UpdateFixtures.Release("1.3.0");
        return release with { Installer = change(release.Installer!) };
    }
}

using System.Net;
using System.Runtime.InteropServices;

namespace CanvasCountdown.Tests.Updates;

/// <summary>
/// Finding the newest Windows release in a repository that publishes the Mac
/// app too. All fixtures are invented; no test reaches the network.
/// </summary>
public sealed class ReleaseCheckerTests
{
    private static readonly string DigestX64 = new('c', 64);

    // Which release

    /// <summary>
    /// The Mac's "latest release" is whichever was published last, and in this
    /// repository that is usually the Mac app. Its tags never count here, even
    /// when their numbers are higher.
    /// </summary>
    [Fact]
    public void MacReleasesAreIgnoredEvenWhenTheirNumbersAreHigher()
    {
        var json = List(Release("v9.0.0"), Release("v1.3.1"), Release("win-v1.2.0"));

        var release = GitHubReleaseChecker.Parse(json, UpdateArchitecture.X64);

        Assert.Equal("win-v1.2.0", release?.Tag);
    }

    /// <summary>The highest version, compared as numbers: 1.10.0 after 1.9.0, whatever order GitHub lists them in.</summary>
    [Fact]
    public void TheHighestWindowsVersionIsChosenNotTheFirstListed()
    {
        var json = List(Release("win-v1.9.0"), Release("win-v1.10.0"), Release("win-v1.2.0"));

        Assert.Equal(AppVersion.Parse("1.10.0"), GitHubReleaseChecker.Parse(json, UpdateArchitecture.X64)?.Version);
    }

    [Fact]
    public void ADraftOrPrereleaseIsPassedOverForTheNextRelease()
    {
        var json = List(Release("win-v2.0.0", prerelease: true), Release("win-v1.9.0", draft: true), Release("win-v1.8.0"));

        Assert.Equal("win-v1.8.0", GitHubReleaseChecker.Parse(json, UpdateArchitecture.X64)?.Tag);
    }

    [Fact]
    public void AWindowsTagThatIsNotAVersionIsSkipped()
    {
        Assert.Equal("win-v1.0.0", GitHubReleaseChecker.Parse(List(Release("win-vnext"), Release("win-v1.0.0")), UpdateArchitecture.X64)?.Tag);
        Assert.Null(GitHubReleaseChecker.Parse(List(Release("win-v1.1.0-beta")), UpdateArchitecture.X64));
        Assert.Null(GitHubReleaseChecker.Parse(List(Release("Win-v1.1.0")), UpdateArchitecture.X64));
    }

    [Fact]
    public void AnEmptyListMeansNothingPublished()
    {
        Assert.Null(GitHubReleaseChecker.Parse("[]", UpdateArchitecture.X64));
        Assert.Null(GitHubReleaseChecker.Parse(List(Release("v1.3.1")), UpdateArchitecture.X64));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"releases\"")]
    [InlineData("[")]
    public void ARootThatIsNotAListIsUnreadable(string json)
    {
        var error = Assert.Throws<ReleaseCheckException>(() => GitHubReleaseChecker.Parse(json, UpdateArchitecture.X64));

        Assert.Equal(ReleaseCheckError.UnreadableResponse, error.Error);
        Assert.Equal("The newest release could not be read.", error.Message);
    }

    // Which installer

    [Theory]
    [InlineData(UpdateArchitecture.X64, "CanvasCountdown-Windows-1.3.0-x64-setup.exe", 1024)]
    [InlineData(UpdateArchitecture.Arm64, "CanvasCountdown-Windows-1.3.0-arm64-setup.exe", 2048)]
    public void TheInstallerForThisProcessorIsChosen(UpdateArchitecture architecture, string fileName, long size)
    {
        var installer = GitHubReleaseChecker.Parse(List(Release("win-v1.3.0")), architecture)?.Installer;

        Assert.NotNull(installer);
        Assert.Equal(fileName, installer.FileName);
        Assert.Equal($"https://example.invalid/{fileName}", installer.Url.AbsoluteUri);
        Assert.Equal(size, installer.Size);
    }

    [Fact]
    public void TheDigestIsCarriedInLowerCase()
    {
        var upper = Release("win-v1.3.0", x64Digest: "sha256:" + new string('C', 64));

        var installer = GitHubReleaseChecker.Parse(List(upper), UpdateArchitecture.X64)?.Installer;

        Assert.Equal(DigestX64, installer?.Sha256);
    }

    /// <summary>
    /// An installer that cannot be checked once it arrives is never offered for
    /// download: the button opens the release page instead.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha512:cccc")]
    [InlineData("sha256:not-hex")]
    [InlineData("sha256:cccc")]
    public void AnInstallerWithoutAUsableDigestIsNotOffered(string? digest)
    {
        var release = GitHubReleaseChecker.Parse(List(Release("win-v1.3.0", x64Digest: digest)), UpdateArchitecture.X64);

        Assert.NotNull(release);
        Assert.Null(release.Installer);
        Assert.Null(release.DownloadUrl);
        Assert.Equal("https://github.com/le23967/CanvasCountdown/releases/tag/win-v1.3.0", release.PageUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("\"1024\"")]
    [InlineData("10.5")]
    public void AnInstallerWithoutAUsableSizeIsNotOffered(string? size)
    {
        var release = GitHubReleaseChecker.Parse(List(Release("win-v1.3.0", x64Size: size)), UpdateArchitecture.X64);

        Assert.Null(release?.Installer);
    }

    [Fact]
    public void AnInstallerOverAnUnsafeSchemeIsNotOffered()
    {
        var release = GitHubReleaseChecker.Parse(List(Release("win-v1.3.0", x64Url: "file:///C:/Windows/notepad.exe")), UpdateArchitecture.X64);

        Assert.Null(release?.Installer);
    }

    [Fact]
    public void WithoutAPageAddressTheReleasesOwnTagPageIsUsed()
    {
        var json = List(Release("win-v1.3.0").Replace("\"html_url\"", "\"other_url\"", StringComparison.Ordinal));

        var release = GitHubReleaseChecker.Parse(json, UpdateArchitecture.X64);

        Assert.Equal("https://github.com/le23967/CanvasCountdown/releases/tag/win-v1.3.0", release?.PageUrl.AbsoluteUri);
    }

    [Fact]
    public void ANamelessReleaseIsCalledByItsTag()
    {
        var json = List(Release("win-v1.3.0")
            .Replace("\"name\": \"Canvas Countdown\"", "\"name\": null", StringComparison.Ordinal)
            .Replace("\"body\": \"Notes go here.\"", "\"body\": null", StringComparison.Ordinal));

        var release = GitHubReleaseChecker.Parse(json, UpdateArchitecture.X64);

        Assert.Equal("win-v1.3.0", release?.Name);
        Assert.Equal(string.Empty, release?.Notes);
    }

    [Fact]
    public void ProcessorsMapToInstallers()
    {
        Assert.Equal(UpdateArchitecture.X64, UpdateArchitectureExtensions.From(Architecture.X64));
        Assert.Equal(UpdateArchitecture.Arm64, UpdateArchitectureExtensions.From(Architecture.Arm64));
        Assert.Null(UpdateArchitectureExtensions.From(Architecture.X86));
        Assert.Equal("x64", UpdateArchitecture.X64.InstallerToken());
        Assert.Equal("arm64", UpdateArchitecture.Arm64.InstallerToken());
        Assert.Equal(
            "CanvasCountdown-Windows-1.10.0-arm64-setup.exe",
            GitHubReleaseChecker.InstallerFileName(AppVersion.Parse("1.10.0")!, UpdateArchitecture.Arm64));
    }

    // Asking GitHub

    [Fact]
    public async Task TheRequestListsReleasesAndSaysWhoIsAsking()
    {
        var handler = StubHttpHandler.Returning(HttpStatusCode.OK, List(Release("win-v1.3.0")));
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, handler);

        var release = await checker.LatestReleaseAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/le23967/CanvasCountdown/releases?per_page=100", request.RequestUri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal("CanvasCountdown-Windows", Assert.Single(request.Headers.UserAgent).Product?.Name);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("win-v1.3.0", release?.Tag);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RateLimitingIsReportedAsSuch(HttpStatusCode status)
    {
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, StubHttpHandler.Returning(status));

        var error = await Assert.ThrowsAsync<ReleaseCheckException>(() => checker.LatestReleaseAsync());

        Assert.Equal(ReleaseCheckError.RateLimited, error.Error);
        Assert.Equal("GitHub is rate limiting update checks. Try again later.", error.Message);
    }

    /// <summary>Nothing published yet. Not an error worth showing anyone.</summary>
    [Fact]
    public async Task NotFoundMeansNothingPublished()
    {
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, StubHttpHandler.Returning(HttpStatusCode.NotFound));

        Assert.Null(await checker.LatestReleaseAsync());
    }

    [Fact]
    public async Task OtherStatusesAreErrors()
    {
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, StubHttpHandler.Returning(HttpStatusCode.BadGateway));

        var error = await Assert.ThrowsAsync<ReleaseCheckException>(() => checker.LatestReleaseAsync());

        Assert.Equal(ReleaseCheckError.HttpStatus, error.Error);
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("The update check failed (HTTP 502).", error.Message);
    }

    [Fact]
    public async Task AnUnreachableServerIsANetworkFailure()
    {
        var handler = StubHttpHandler.Throwing(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host."));
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, handler);

        var error = await Assert.ThrowsAsync<ReleaseCheckException>(() => checker.LatestReleaseAsync());

        Assert.Equal(ReleaseCheckError.NetworkFailure, error.Error);
        Assert.Equal((int)HttpRequestError.NameResolutionError, error.Code);
        Assert.Equal("The update check could not reach GitHub.", error.Message);
    }

    [Fact]
    public async Task ASlowServerIsANetworkFailure()
    {
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, NeverAnswering(), timeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<ReleaseCheckException>(() => checker.LatestReleaseAsync());

        Assert.Equal(ReleaseCheckError.NetworkFailure, error.Error);
        Assert.Equal(TimeSpan.FromSeconds(20), GitHubReleaseChecker.RequestTimeout);
    }

    /// <summary>Stopping a check is not a failure to report: it stays a cancellation.</summary>
    [Fact]
    public async Task ACancelledCheckIsNotDisguisedAsAFailure()
    {
        var checker = new GitHubReleaseChecker(UpdateArchitecture.X64, NeverAnswering());
        using var stop = new CancellationTokenSource();

        var pending = checker.LatestReleaseAsync(stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task TheInertCheckerFindsNothing()
    {
        Assert.Null(await new InertReleaseChecker().LatestReleaseAsync());
    }

    private static StubHttpHandler NeverAnswering() => new(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static string Release(
        string tag,
        bool draft = false,
        bool prerelease = false,
        string? x64Digest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        string? x64Size = "1024",
        string? x64Url = null)
    {
        var version = tag.StartsWith("win-v", StringComparison.Ordinal) ? tag["win-v".Length..] : tag.TrimStart('v');
        var x64Name = $"CanvasCountdown-Windows-{version}-x64-setup.exe";
        var digestField = x64Digest is null ? string.Empty : $",\"digest\":\"{x64Digest}\"";
        var sizeField = x64Size is null ? string.Empty : $",\"size\":{x64Size}";
        return $$"""
            {
              "tag_name": "{{tag}}",
              "name": "Canvas Countdown",
              "body": "Notes go here.",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://github.com/le23967/CanvasCountdown/releases/tag/{{tag}}",
              "assets": [
                {"name":"CanvasCountdown-{{version}}.dmg","browser_download_url":"https://example.invalid/CanvasCountdown-{{version}}.dmg","size":4096,"digest":"sha256:{{new string('a', 64)}}"},
                {"name":"CanvasCountdown-Windows-{{version}}-arm64-setup.exe","browser_download_url":"https://example.invalid/CanvasCountdown-Windows-{{version}}-arm64-setup.exe","size":2048,"digest":"sha256:{{new string('b', 64)}}"},
                {"name":"{{x64Name}}","browser_download_url":"{{x64Url ?? "https://example.invalid/" + x64Name}}"{{sizeField}}{{digestField}} }
              ]
            }
            """;
    }
}

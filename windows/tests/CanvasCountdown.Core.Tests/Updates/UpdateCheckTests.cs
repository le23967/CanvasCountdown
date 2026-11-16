using CanvasCountdown.Tests.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace CanvasCountdown.Tests.Updates;

/// <summary>
/// Knowing a new version exists, without becoming something people learn to
/// ignore.
/// </summary>
/// <remarks>
/// No test makes a real network request, and none of them ask GitHub what is
/// published today: the suite must not pass or fail on somebody else's release.
/// All fixtures are invented. Like the Mac's, the checks go through the main
/// view model, which is what the app runs — with the download revealed rather
/// than installed, as on the Mac; the Windows install path has its own tests
/// in UndoAndUpdateTests.
/// </remarks>
public sealed class UpdateCheckTests
{
    private static readonly DateTimeOffset Today = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // Version numbers are compared as numbers

    /// <summary>
    /// The failure this exists to prevent: as text, "1.10.0" sorts before
    /// "1.9.0", so everyone on 1.9.0 would be told they were current for the
    /// rest of the app's life, and no later release could ever correct it.
    /// </summary>
    [Fact]
    public void TenIsNewerThanNine()
    {
        var ten = AppVersion.Parse("1.10.0")!;
        var nine = AppVersion.Parse("1.9.0")!;

        Assert.True(ten > nine);
        Assert.False(nine > ten);
    }

    [Fact]
    public void TheTagMayCarryItsV()
    {
        Assert.Equal(AppVersion.Parse("1.2.0"), AppVersion.Parse("v1.2.0"));
        Assert.Equal(AppVersion.Parse("1.2.0"), AppVersion.Parse("1.2"));
        Assert.True(AppVersion.Parse("2.0")! > AppVersion.Parse("1.99.99")!);
    }

    /// <summary>
    /// A version it cannot read is refused rather than guessed at, because a
    /// guess here means telling somebody to install something.
    /// </summary>
    [Fact]
    public void SomethingThatIsNotAVersionIsRefused()
    {
        Assert.Null(AppVersion.Parse(""));
        Assert.Null(AppVersion.Parse("v"));
        Assert.Null(AppVersion.Parse("nightly"));
        Assert.Null(AppVersion.Parse("1.2.x"));
        Assert.Null(AppVersion.Parse("1..2"));
    }

    // Reading what GitHub sends back

    [Fact]
    public void TheDiskImageIsPickedOutOfTheAssets()
    {
        var release = GitHubReleaseChecker.Parse(ReleaseList(ReleaseJson("win-v1.3.0")), UpdateArchitecture.X64)!;

        Assert.Equal(AppVersion.Parse("1.3.0"), release.Version);
        Assert.Equal("win-v1.3.0", release.Tag);
        Assert.True(
            release.DownloadUrl?.Segments[^1] == "CanvasCountdown-Windows-1.3.0-x64-setup.exe",
            "The Mac disk image, the other processor's installer and the zipped source are not what this PC can install");
    }

    /// <summary>Nothing half-finished is offered to anyone.</summary>
    [Fact]
    public void DraftsAndPrereleasesAreNotOffered()
    {
        Assert.Null(GitHubReleaseChecker.Parse(ReleaseList(ReleaseJson("win-v9.9.9", draft: true)), UpdateArchitecture.X64));
        Assert.Null(GitHubReleaseChecker.Parse(ReleaseList(ReleaseJson("win-v9.9.9", prerelease: true)), UpdateArchitecture.X64));
    }

    [Fact]
    public void AReleaseWithNoImageStillOffersItsPage()
    {
        var release = GitHubReleaseChecker.Parse(ReleaseList(ReleaseJson("win-v1.3.0", assets: false)), UpdateArchitecture.X64)!;

        Assert.Null(release.DownloadUrl);
        Assert.Equal("https://github.com/le23967/CanvasCountdown/releases/tag/win-v1.3.0", release.PageUrl.AbsoluteUri);
    }

    [Fact]
    public void NonsenseIsAnErrorRatherThanAnUpdate()
    {
        var error = Assert.Throws<ReleaseCheckException>(() => GitHubReleaseChecker.Parse("not json", UpdateArchitecture.X64));

        Assert.Equal(ReleaseCheckError.UnreadableResponse, error.Error);
    }

    // When something is said, and when nothing is

    [Fact]
    public async Task ANewerVersionIsOffered()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");

        await harness.Coordinator.CheckForUpdateAsync();

        Assert.Equal(AppVersion.Parse("1.3.0"), harness.Coordinator.AvailableUpdate?.Version);
    }

    [Fact]
    public async Task TheVersionYouAreOnIsNotAnUpdate()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.2.0");

        await harness.Coordinator.CheckForUpdateAsync();

        Assert.Null(harness.Coordinator.AvailableUpdate);
    }

    /// <summary>
    /// Somebody who built from source, or rolled back on purpose, is not
    /// nagged to install something older than what they are running.
    /// </summary>
    [Fact]
    public async Task AnOlderPublishedVersionIsNotOffered()
    {
        var harness = MakeHarness(running: "1.4.0", published: "1.3.0");

        await harness.Coordinator.CheckForUpdateAsync();

        Assert.Null(harness.Coordinator.AvailableUpdate);
    }

    /// <summary>Being offline is not news, and it is not the user's problem to solve.</summary>
    [Fact]
    public async Task ALaunchCheckThatFailsSaysNothing()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        harness.Checker.Failure = new ReleaseCheckException(ReleaseCheckError.RateLimited);

        await harness.Coordinator.CheckForUpdateAsync();

        Assert.Null(harness.Coordinator.AvailableUpdate);
        Assert.True(
            harness.Coordinator.UpdateCheckAnnouncement is null,
            "A check nobody asked for does not report its own failure");
    }

    /// <summary>A button that says nothing back is a button people press twice.</summary>
    [Fact]
    public async Task AManualCheckAlwaysAnswers()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.2.0");

        await harness.Coordinator.CheckForUpdateAsync(force: true);

        Assert.Equal("1.2.0 is the newest version", harness.Coordinator.UpdateCheckAnnouncement);

        harness.Checker.Failure = new ReleaseCheckException(ReleaseCheckError.RateLimited);
        await harness.Coordinator.CheckForUpdateAsync(force: true);

        Assert.Equal("GitHub is rate limiting update checks. Try again later.", harness.Coordinator.UpdateCheckAnnouncement);
    }

    // Asked once

    [Fact]
    public async Task ADismissedVersionIsNotOfferedAgain()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: Today);
        Assert.NotNull(harness.Coordinator.AvailableUpdate);

        harness.Coordinator.DismissUpdate();
        Assert.Null(harness.Coordinator.AvailableUpdate);

        await harness.Coordinator.CheckForUpdateAsync(force: false, at: AWeekAfter(Today));

        Assert.True(
            harness.Coordinator.AvailableUpdate is null,
            "Asking again about the version just waved away is how a notice becomes noise");
    }

    /// <summary>Waving one version away is not a decision about every version after it.</summary>
    [Fact]
    public async Task TheNextVersionIsStillOffered()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: Today);
        harness.Coordinator.DismissUpdate();

        harness.Checker.Release = UpdateFixtures.Release("1.4.0");
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: AWeekAfter(Today));

        Assert.Equal(AppVersion.Parse("1.4.0"), harness.Coordinator.AvailableUpdate?.Version);
    }

    /// <summary>Pressing the button is a change of mind, and it is answered.</summary>
    [Fact]
    public async Task AManualCheckReconsidersADismissedVersion()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync();
        harness.Coordinator.DismissUpdate();

        await harness.Coordinator.CheckForUpdateAsync(force: true);

        Assert.Equal(AppVersion.Parse("1.3.0"), harness.Coordinator.AvailableUpdate?.Version);
    }

    // Once a day, not once a launch

    [Fact]
    public async Task AsecondCheckWithinTheDayDoesNotAskAgain()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");

        await harness.Coordinator.CheckForUpdateAsync();
        Assert.Equal(1, harness.Checker.CallCount);

        harness.Clock.Advance(TimeSpan.FromHours(5));
        await harness.Coordinator.CheckForUpdateAsync();

        Assert.True(harness.Checker.CallCount == 1, "Somebody who opens the app six times a day is not six checks");
    }

    [Fact]
    public async Task ADayLaterItAsksAgain()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: Today);
        Assert.Equal(1, harness.Checker.CallCount);

        await harness.Coordinator.CheckForUpdateAsync(force: false, at: Today + MainViewModel.UpdateCheckInterval + TimeSpan.FromSeconds(1));

        Assert.Equal(2, harness.Checker.CallCount);
    }

    /// <summary>
    /// A clock moved backwards must not lock the check out until it catches
    /// up. Somebody whose PC had the wrong date would otherwise stop being
    /// told about updates for as long as the stamp stayed ahead.
    /// </summary>
    [Fact]
    public async Task ACheckStampedInTheFutureDoesNotBlockForever()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        var future = Today;
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: future);
        Assert.Equal(1, harness.Checker.CallCount);

        // The clock is put back a month; the stamp is now in the future.
        var corrected = future - TimeSpan.FromSeconds(86_400 * 30);

        Assert.True(harness.Coordinator.ShouldCheckForUpdate(corrected));
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: corrected);
        Assert.Equal(2, harness.Checker.CallCount);
    }

    /// <summary>The button means now, whatever the clock says about the last one.</summary>
    [Fact]
    public async Task TheButtonIgnoresTheDailyGate()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync();

        await harness.Coordinator.CheckForUpdateAsync(force: true);

        Assert.Equal(2, harness.Checker.CallCount);
    }

    // Downloading, and handing the installer on

    [Fact]
    public async Task DownloadingLeavesTheImageWhereItCanBeFound()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync();

        await harness.Coordinator.DownloadUpdateAsync();

        Assert.True(
            Path.GetFileName(harness.Downloader.Revealed) == "CanvasCountdown-Windows-1.3.0-x64-setup.exe",
            "The verified installer is handed on");
        Assert.Equal("1.3.0 is in your Downloads. Open it to finish.", harness.Coordinator.UpdateMessage);
        Assert.False(harness.Coordinator.IsDownloadingUpdate);
    }

    /// <summary>
    /// A failed download says so and keeps the offer up, rather than clearing
    /// the banner as though it had worked.
    /// </summary>
    [Fact]
    public async Task AFailedDownloadKeepsTheOfferAndSaysWhy()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync();
        harness.Downloader.Failure = new UpdateDownloadException(UpdateDownloadError.NetworkFailure, code: -1009);

        await harness.Coordinator.DownloadUpdateAsync();

        Assert.NotNull(harness.Coordinator.AvailableUpdate);
        Assert.Null(harness.Downloader.Revealed);
        Assert.Equal(new UpdateDownloadException(UpdateDownloadError.NetworkFailure, code: -1009).Message, harness.Coordinator.UpdateMessage);
        Assert.Equal("The download could not be completed.", harness.Coordinator.UpdateMessage);
    }

    /// <summary>
    /// A release published without an installer cannot be installed from here,
    /// so the button takes them where they can see what happened.
    /// </summary>
    [Fact]
    public async Task AReleaseWithNoImageOpensThePageInstead()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0", withInstaller: false);
        await harness.Coordinator.CheckForUpdateAsync();

        await harness.Coordinator.DownloadUpdateAsync();

        Assert.Null(harness.Downloader.Revealed);
        Assert.Equal("https://github.com/le23967/CanvasCountdown/releases/tag/win-v1.3.0", Assert.Single(harness.Opened).AbsoluteUri);
    }

    // Windows additions

    [Fact]
    public async Task AFailedCheckDoesNotCountAsTheDaysCheck()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        harness.Checker.Failure = new ReleaseCheckException(ReleaseCheckError.NetworkFailure);

        await harness.Coordinator.CheckForUpdateAsync();

        Assert.Null(harness.Record.LastUpdateCheck);
        Assert.True(harness.Coordinator.ShouldCheckForUpdate(harness.Clock.GetUtcNow()));
    }

    /// <summary>No version to compare against means every answer would be a guess.</summary>
    [Fact]
    public async Task WithoutARunningVersionNothingIsAsked()
    {
        var harness = MakeHarness(running: null, published: "1.3.0");

        await harness.Coordinator.CheckForUpdateAsync(force: true);

        Assert.Equal(0, harness.Checker.CallCount);
        Assert.Null(harness.Coordinator.AvailableUpdate);
        Assert.Equal("unknown", harness.Coordinator.RunningVersionDescription);
    }

    [Fact]
    public async Task TheButtonChecksStraightAway()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        await harness.Coordinator.CheckForUpdateAsync();

        _ = harness.Coordinator.CheckForUpdateManuallyAsync();
        await WaitUntil(() => !harness.Coordinator.IsCheckingForUpdate && harness.Checker.CallCount == 2);

        Assert.Equal(AppVersion.Parse("1.3.0"), harness.Coordinator.AvailableUpdate?.Version);
    }

    [Fact]
    public async Task DismissingRemembersTheVersionAcrossLaunches()
    {
        var preferences = JsonPreferencesStore.InMemory();
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0", preferences: preferences);
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: Today);

        harness.Coordinator.DismissUpdate();

        Assert.Equal("1.3.0", preferences.Get<string?>(DismissedUpdateVersionKey, null));
        Assert.Equal(Today, preferences.GetOptional<DateTimeOffset>(LastUpdateCheckKey));

        var relaunched = MakeHarness(running: "1.2.0", published: "1.3.0", preferences: preferences);
        await relaunched.Coordinator.CheckForUpdateAsync(force: false, at: AWeekAfter(Today));
        Assert.Null(relaunched.Coordinator.AvailableUpdate);
    }

    private const string DismissedUpdateVersionKey = "CanvasCountdown.settings.dismissedUpdateVersion";

    private const string LastUpdateCheckKey = "CanvasCountdown.settings.lastUpdateCheck";

    /// <summary>The same keys as the Mac app, so the two read each other's notes.</summary>
    [Fact]
    public async Task TheRecordUsesTheMacKeys()
    {
        var preferences = JsonPreferencesStore.InMemory();
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0", preferences: preferences);
        await harness.Coordinator.CheckForUpdateAsync(force: false, at: Today);
        harness.Coordinator.DismissUpdate();

        Assert.Equal("1.3.0", preferences.Get<string?>(DismissedUpdateVersionKey, null));
        Assert.Equal(Today, preferences.GetOptional<DateTimeOffset>(LastUpdateCheckKey));

        // A newer version clears the old dismissal only when asked for.
        await harness.Coordinator.CheckForUpdateManuallyAsync();
        Assert.Null(preferences.Get<string?>(DismissedUpdateVersionKey, null));
    }

    [Fact]
    public async Task ClearingTheMessageClearsTheAnnouncementToo()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.2.0");
        await harness.Coordinator.CheckForUpdateAsync(force: true);
        Assert.NotNull(harness.Coordinator.UpdateCheckAnnouncement);

        harness.Coordinator.ClearUpdateMessage();

        Assert.Null(harness.Coordinator.UpdateCheckAnnouncement);
        Assert.Null(harness.Coordinator.UpdateMessage);
    }

    [Fact]
    public async Task TheOfferIsObservable()
    {
        var harness = MakeHarness(running: "1.2.0", published: "1.3.0");
        var changed = new List<string?>();
        harness.Coordinator.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await harness.Coordinator.CheckForUpdateAsync();

        // The view model announces a change to everything at once, which is
        // what the window's bindings listen for.
        Assert.Contains(changed, name => string.IsNullOrEmpty(name) || name == nameof(MainViewModel.AvailableUpdate));
        Assert.NotNull(harness.Coordinator.AvailableUpdate);
    }

    [Theory]
    [InlineData("1.2.0+4f1c2e9", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("v2.0+local", "2.0")]
    [InlineData("", null)]
    [InlineData("+abc", null)]
    public void TheRunningVersionIgnoresBuildMetadata(string informational, string? expected)
    {
        Assert.Equal(expected, AppVersion.FromInformationalVersion(informational)?.ToString());
    }

    [Fact]
    public void TheAppReadsItsOwnVersion()
    {
        var version = AppVersion.Current(typeof(AppVersion).Assembly);

        Assert.NotNull(version);
        Assert.True(version >= AppVersion.Parse("1.0.0")!);
    }

    [Fact]
    public void VersionsSortAsNumbers()
    {
        var sorted = new[] { "1.10.0", "1.9.0", "1.2", "2.0.0", "1.9.1", "1.9.0.1" }
            .Select(text => AppVersion.Parse(text)!)
            .Order()
            .Select(version => version.ToString());

        Assert.Equal(["1.2", "1.9.0", "1.9.0.1", "1.9.1", "1.10.0", "2.0.0"], sorted);
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData(" 1.2.0 ", "1.2.0")]
    [InlineData("V1.2", "1.2")]
    [InlineData("01.02", "01.02")]
    [InlineData("1.2.3.4.5", null)]
    [InlineData("+1.2", null)]
    [InlineData("1.-2", null)]
    [InlineData("٣.٤", null)]
    [InlineData("99999999999999999999.1", null)]
    [InlineData("1.2.0-beta", null)]
    [InlineData("win-v1.0.0", null)]
    public void VersionTextIsReadAsTheMacReadsIt(string text, string? expected)
    {
        Assert.Equal(expected, AppVersion.Parse(text)?.ToString());
    }

    [Fact]
    public void PaddedVersionsAreTheSameVersion()
    {
        var short1 = AppVersion.Parse("1.2")!;
        var long1 = AppVersion.Parse("1.2.0.0")!;

        Assert.Equal(short1, long1);
        Assert.True(short1 == long1);
        Assert.Equal(short1.GetHashCode(), long1.GetHashCode());
        Assert.Equal(0, short1.CompareTo(long1));
        Assert.True(short1 != AppVersion.Parse("1.2.0.1"));
    }

    [Fact]
    public void TheSummaryIsTheFirstParagraphAfterTheCopyright()
    {
        var release = UpdateFixtures.Release("1.3.0") with
        {
            Notes = "Copyright © 2026 le23967. MIT Licence.\n\n**A made-up change** with `code` and # heading\nand a second line.",
        };

        Assert.Equal("A made-up change with code and  heading and a second line.", release.Summary);
    }

    [Fact]
    public void ALongSummaryIsCut()
    {
        var release = UpdateFixtures.Release("1.3.0") with { Notes = string.Concat(Enumerable.Repeat("word ", 40)) };

        Assert.Equal(string.Join(" ", Enumerable.Repeat("word", 30)) + "…", release.Summary);
        Assert.Equal(150, release.Summary.Length);
    }

    [Theory]
    [InlineData("Copyright only", "")]
    [InlineData("\n\n  \n\nReal one\n\nlater", "Real one")]
    [InlineData("First\r\n\r\nSecond", "First\r \r Second")]
    public void SummariesReadAsTheMacReadThem(string notes, string summary)
    {
        Assert.Equal(summary, (UpdateFixtures.Release("1.3.0") with { Notes = notes }).Summary);
    }

    private static DateTimeOffset AWeekAfter(DateTimeOffset date) => date + TimeSpan.FromSeconds(86_400 * 7);

    private static string ReleaseList(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, bool assets = true)
    {
        var version = tag["win-v".Length..];
        var assetList = assets
            ? $$"""
              {"name":"CanvasCountdown-1.3.0.dmg","browser_download_url":"https://example.invalid/CanvasCountdown-1.3.0.dmg","size":4096,"digest":"sha256:{{new string('a', 64)}}"},
              {"name":"CanvasCountdown-Windows-{{version}}-arm64-setup.exe","browser_download_url":"https://example.invalid/CanvasCountdown-Windows-{{version}}-arm64-setup.exe","size":2048,"digest":"sha256:{{new string('b', 64)}}"},
              {"name":"CanvasCountdown-Windows-{{version}}-x64-setup.exe","browser_download_url":"https://example.invalid/CanvasCountdown-Windows-{{version}}-x64-setup.exe","size":1024,"digest":"sha256:{{new string('c', 64)}}"}
              """
            : string.Empty;
        return $$"""
            {
              "tag_name": "{{tag}}",
              "name": "Canvas Countdown",
              "body": "Notes go here.",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://github.com/le23967/CanvasCountdown/releases/tag/{{tag}}",
              "assets": [{{assetList}}]
            }
            """;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private sealed record Harness(
        MainViewModel Coordinator,
        ReleaseCheckerStub Checker,
        UpdateDownloaderSpy Downloader,
        SettingsStore Record,
        List<Uri> Opened,
        FakeTimeProvider Clock);

    private static Harness MakeHarness(string? running, string published, bool withInstaller = true, JsonPreferencesStore? preferences = null)
    {
        var owner = new ViewModelHarness(
            dependencies => dependencies with
            {
                // Never the entry assembly: under a test runner that is the
                // runner, whose version has nothing to do with the app's.
                CurrentVersion = () => AppVersion.Parse(running),
                UpdateInstaller = null,
            },
            preferences);
        owner.Releases.Release = UpdateFixtures.Release(published, withInstaller);
        owner.Clock.SetUtcNow(Today);
        return new Harness(owner.ViewModel, owner.Releases, owner.Downloads, owner.Settings, owner.OpenedUrls, owner.Clock);
    }
}

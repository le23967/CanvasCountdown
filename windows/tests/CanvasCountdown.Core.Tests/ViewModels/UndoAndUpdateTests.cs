using CanvasCountdown.Tests.Updates;

namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// Two Windows paths with no Mac test to port: taking back what was just
/// added, and the update that downloads, verifies and installs itself.
/// </summary>
public sealed class UndoAndUpdateTests
{
    // ------------------------------------------------------------------
    // Undoing an addition

    private static ManualEventDraft Draft(string title, int days) => new() { Title = title, DueDate = ViewModelHarness.Day(days) };

    [Fact]
    public void AnEventAddedByHandCanBeTakenBack() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.SaveManualEventAsync(Draft("Essay", 3));

        Assert.True(harness.ViewModel.CanUndoAddition);
        Assert.True(harness.ViewModel.IsUndoToastVisible);
        Assert.Equal("Undo Adding “Essay”", harness.ViewModel.UndoAdditionMenuTitle);

        await harness.ViewModel.UndoLastAdditionAsync();

        Assert.Empty(harness.ViewModel.Assignments);
        Assert.Empty(await harness.Repository.FetchAllAsync());
        Assert.False(harness.ViewModel.CanUndoAddition);
        Assert.Null(harness.Dock.LastDays);
        Assert.Equal("1 event removed", harness.ViewModel.StatusMessage);
    });

    [Fact]
    public void UndoRemovesOnlyWhatThatAdditionCreated() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddManualAsync("Keep me", ViewModelHarness.Day(2));
        await harness.AddImportedAsync("Imported", ViewModelHarness.Day(4), "COMP2050");
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.SaveManualEventAsync(Draft("Mistake", 3));
        await harness.ViewModel.UndoLastAdditionAsync();

        Assert.Equal(["Keep me", "Imported"], harness.ViewModel.Assignments.Select(item => item.Title));
    });

    [Fact]
    public void EditingAnEventIsNotOfferedAsAnAddition() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddManualAsync("Essay", ViewModelHarness.Day(2));
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.SaveManualEventAsync(ManualEventDraft.From(harness.Item("Essay")) with { Title = "Essay draft" });

        // Undoing an edit would delete an event the user had for days.
        Assert.False(harness.ViewModel.CanUndoAddition);
        Assert.Equal("Event updated", harness.ViewModel.StatusMessage);
    });

    [Fact]
    public void ANewerAdditionReplacesTheOfferRatherThanQueueing() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.SaveManualEventAsync(Draft("First", 2));
        await harness.ViewModel.SaveManualEventAsync(Draft("Second", 3));
        Assert.Equal("Undo Adding “Second”", harness.ViewModel.UndoAdditionMenuTitle);

        await harness.ViewModel.UndoLastAdditionAsync();

        Assert.Equal(["First"], harness.ViewModel.Assignments.Select(item => item.Title));
        Assert.False(harness.ViewModel.CanUndoAddition);
    });

    [Fact]
    public void TheToastCountsDownAndTheCommandBarKeepsTheWayBack() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.SaveManualEventAsync(Draft("Essay", 3));
        Assert.Equal(MainViewModel.UndoToastDuration, harness.ViewModel.UndoToastSecondsRemaining);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));
        await ViewModelHarness.WaitUntil(() => harness.ViewModel.UndoToastSecondsRemaining == MainViewModel.UndoToastDuration - 3, "three seconds have gone");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(MainViewModel.UndoToastDuration));
        await ViewModelHarness.WaitUntil(() => !harness.ViewModel.IsUndoToastVisible, "the toast has gone");

        // The toast leaves; the undo itself stays until something replaces it.
        Assert.True(harness.ViewModel.CanUndoAddition);
    });

    [Fact]
    public void DismissingTheToastKeepsTheEventAndTheWayBack() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        await harness.ViewModel.SaveManualEventAsync(Draft("Essay", 3));

        harness.ViewModel.DismissUndoToast();

        Assert.False(harness.ViewModel.IsUndoToastVisible);
        Assert.True(harness.ViewModel.CanUndoAddition);
        Assert.Single(harness.ViewModel.Assignments);
    });

    [Fact]
    public void AnEventAlreadyDeletedByHandIsNotAnError() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        await harness.ViewModel.SaveManualEventAsync(Draft("Essay", 3));
        await harness.ViewModel.DeleteManualEventAsync(harness.Item("Essay"));

        await harness.ViewModel.UndoLastAdditionAsync();

        Assert.Null(harness.ViewModel.ErrorMessage);
        Assert.Equal("0 events removed", harness.ViewModel.StatusMessage);
    });

    [Fact]
    public void AScreenshotImportIsTakenBackByItsOwnUndo() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        var added = await harness.AddImportedAsync("From a screenshot", ViewModelHarness.Day(6), "STAT1010");
        var commit = new ScreenshotImportCommit
        {
            Summary = ScreenshotImportSummary.Empty with { Added = 1 },
            Added = [added],
        };
        ScreenshotImportCommit? undone = null;

        await harness.ViewModel.ScreenshotImportDidFinishAsync(commit, async received =>
        {
            undone = received;
            await harness.Repository.DeleteAsync(added.Id);
        });
        Assert.Equal("Undo Importing 1 Event", harness.ViewModel.UndoAdditionMenuTitle);
        Assert.Single(harness.ViewModel.Assignments);

        await harness.ViewModel.UndoLastAdditionAsync();

        Assert.Same(commit, undone);
        Assert.Empty(harness.ViewModel.Assignments);
        Assert.Equal("Import undone", harness.ViewModel.StatusMessage);
    });

    [Fact]
    public void AScreenshotImportThatChangedNothingOffersNoUndo() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.ScreenshotImportDidFinishAsync(
            new ScreenshotImportCommit { Summary = ScreenshotImportSummary.Empty with { Skipped = 2 } },
            _ => Task.CompletedTask);

        Assert.False(harness.ViewModel.CanUndoAddition);
    });

    // ------------------------------------------------------------------
    // Updates

    [Fact]
    public void TheLaunchCheckOffersANewerVersion() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Release = UpdateFixtures.Release("1.0.1");

        await harness.ViewModel.CheckForUpdateAsync();

        Assert.Equal("1.0.1", harness.ViewModel.AvailableUpdate?.Version.ToString());
        Assert.Equal(ViewModelHarness.Start, harness.Settings.LastUpdateCheck);
        // The launch check is silent apart from the offer itself.
        Assert.Null(harness.ViewModel.UpdateCheckAnnouncement);
    });

    [Fact]
    public void TheLaunchCheckAsksAtMostOnceADay() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Release = UpdateFixtures.Release("1.0.0");

        await harness.ViewModel.CheckForUpdateAsync();
        harness.Clock.Advance(TimeSpan.FromHours(23));
        await harness.ViewModel.CheckForUpdateAsync();
        Assert.Equal(1, harness.Releases.CallCount);

        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.ViewModel.CheckForUpdateAsync();
        Assert.Equal(2, harness.Releases.CallCount);
    });

    [Fact]
    public void TheButtonAlwaysAnswers() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Release = UpdateFixtures.Release("1.0.0");

        await harness.ViewModel.CheckForUpdateManuallyAsync();

        Assert.Null(harness.ViewModel.AvailableUpdate);
        Assert.Equal("1.0.0 is the newest version", harness.ViewModel.UpdateCheckAnnouncement);
    });

    [Fact]
    public void ADismissedVersionStaysDismissedUntilAskedFor() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Release = UpdateFixtures.Release("1.0.1");
        await harness.ViewModel.CheckForUpdateAsync();

        Assert.True(harness.ViewModel.HasAvailableUpdate);
        harness.ViewModel.DismissUpdate();
        Assert.Null(harness.ViewModel.AvailableUpdate);
        Assert.False(harness.ViewModel.HasAvailableUpdate);
        Assert.Equal("1.0.1", new SettingsStore(harness.Preferences).DismissedUpdateVersion);

        harness.Clock.Advance(TimeSpan.FromDays(1));
        await harness.ViewModel.CheckForUpdateAsync();
        Assert.Null(harness.ViewModel.AvailableUpdate);

        // Asking is a change of mind.
        await harness.ViewModel.CheckForUpdateManuallyAsync();
        Assert.Equal("1.0.1", harness.ViewModel.AvailableUpdate?.Version.ToString());
    });

    [Fact]
    public void BeingOfflineIsOnlyMentionedWhenAsked() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Failure = new HttpRequestException("GitHub could not be reached.");

        await harness.ViewModel.CheckForUpdateAsync();
        Assert.Null(harness.ViewModel.UpdateCheckAnnouncement);
        Assert.Null(harness.ViewModel.ErrorMessage);

        await harness.ViewModel.CheckForUpdateManuallyAsync();
        Assert.Equal("GitHub could not be reached.", harness.ViewModel.UpdateCheckAnnouncement);
    });

    [Fact]
    public void DownloadAndInstallHandsTheVerifiedFileToTheInstaller() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        var release = UpdateFixtures.Release("1.0.1");
        harness.Releases.Release = release;
        await harness.ViewModel.CheckForUpdateAsync();

        await harness.ViewModel.DownloadUpdateAsync();

        var install = Assert.Single(harness.Installer.Installs);
        Assert.EndsWith(release.Installer!.FileName, install.Path, StringComparison.Ordinal);
        Assert.Equal(release.Installer.Sha256, install.Sha256);
        Assert.StartsWith("Installing 1.0.1", harness.ViewModel.UpdateMessage, StringComparison.Ordinal);
        Assert.False(harness.ViewModel.IsDownloadingUpdate);
    });

    [Fact]
    public void AFailedDownloadKeepsTheOfferAndSaysWhy() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Release = UpdateFixtures.Release("1.0.1");
        await harness.ViewModel.CheckForUpdateAsync();
        harness.Downloads.Failure = new UpdateDownloadException(UpdateDownloadError.VerificationFailed);

        await harness.ViewModel.DownloadUpdateAsync();

        Assert.Empty(harness.Installer.Installs);
        Assert.Equal(harness.Downloads.Failure.Message, harness.ViewModel.UpdateMessage);
        Assert.NotNull(harness.ViewModel.AvailableUpdate);
        Assert.False(harness.ViewModel.IsDownloadingUpdate);
    });

    [Fact]
    public void AnInstallerThatCannotStartSaysSo() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        harness.Releases.Release = UpdateFixtures.Release("1.0.1");
        await harness.ViewModel.CheckForUpdateAsync();
        harness.Installer.Failure = new InvalidOperationException("The update could not be started. Nothing was changed.");

        await harness.ViewModel.DownloadUpdateAsync();

        Assert.Equal("The update could not be started. Nothing was changed.", harness.ViewModel.UpdateMessage);
        Assert.NotNull(harness.ViewModel.AvailableUpdate);
    });

    [Fact]
    public void AReleaseWithoutAnInstallerOpensItsPage() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        var release = UpdateFixtures.Release("1.0.1", withInstaller: false);
        harness.Releases.Release = release;
        await harness.ViewModel.CheckForUpdateAsync();

        await harness.ViewModel.DownloadUpdateAsync();

        Assert.Equal([release.PageUrl!], harness.OpenedUrls);
        Assert.Empty(harness.Installer.Installs);
    });

    [Fact]
    public void WithoutAnInstallerTheDownloadIsShownInExplorer() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness(dependencies => dependencies with { UpdateInstaller = null });
        harness.Releases.Release = UpdateFixtures.Release("1.0.1");
        await harness.ViewModel.CheckForUpdateAsync();

        await harness.ViewModel.DownloadUpdateAsync();

        Assert.NotNull(harness.Downloads.Revealed);
        Assert.Equal("1.0.1 is in your Downloads. Open it to finish.", harness.ViewModel.UpdateMessage);
    });

    [Fact]
    public void TheInstallersVerdictIsSaidOnceAfterTheRestart() => ViewModelHarness.OnUiThread(async () =>
    {
        using var updated = new ViewModelHarness();
        updated.ViewModel.AnnounceUpdateResult(succeeded: true);
        Assert.Equal("Updated to 1.0.0", updated.ViewModel.StatusMessage);

        using var failed = new ViewModelHarness();
        failed.ViewModel.AnnounceUpdateResult(succeeded: false);
        Assert.Equal(
            "The update could not be installed, so nothing was changed. You are still on 1.0.0.",
            failed.ViewModel.ErrorMessage);
        await Task.CompletedTask;
    });
}

using System.Text;

namespace CanvasCountdown.Tests.Feed;

/// <summary>
/// A single incomplete Canvas response must never destroy stored assignments
/// or the completed and ignored decisions attached to them.
/// </summary>
public sealed class FeedReconciliationTests
{
    private static FeedHarness Harness(int archiveThreshold, params string[] payloads)
        => new(ScriptedFetcher.Returning(payloads), archiveThreshold);

    private static async Task RefreshAsync(FeedHarness harness, int day, RefreshTrigger trigger = RefreshTrigger.Automatic)
        => await harness.Coordinator.RefreshAsync(FeedHarness.Day(day), trigger);

    [Fact]
    public async Task AbsentEventSurvivesOneIncompleteRefresh()
    {
        using var harness = Harness(3, Ics.Full, Ics.Partial);

        await RefreshAsync(harness, 0, RefreshTrigger.Manual);
        await RefreshAsync(harness, 1);

        var stored = await harness.Repository.FetchAllAsync();
        Assert.Equal(2, stored.Count);
        var absent = stored.Single(item => item.ExternalId == "beta@example.edu");
        Assert.False(absent.IsArchived);
        Assert.Equal(1, absent.MissingRefreshCount);
        Assert.True(absent.IsMissingFromFeed);
        var present = stored.Single(item => item.ExternalId == "alpha@example.edu");
        Assert.Equal(0, present.MissingRefreshCount);
        Assert.Equal(FeedHarness.Day(1), present.LastSeenInFeedAt);
    }

    [Fact]
    public async Task AbsentEventIsArchivedOnlyAfterThresholdRefreshes()
    {
        using var harness = Harness(3, Ics.Full, Ics.Partial, Ics.Partial, Ics.Partial);

        await RefreshAsync(harness, 0, RefreshTrigger.Manual);
        await RefreshAsync(harness, 1);
        await RefreshAsync(harness, 2);
        Assert.Equal(2, (await harness.Repository.FetchAllAsync()).Count);

        await RefreshAsync(harness, 3);

        Assert.Equal(["alpha@example.edu"], (await harness.Repository.FetchAllAsync()).Select(item => item.ExternalId));
        var retained = await harness.Repository.FetchAllAsync(includingArchived: true);
        Assert.Equal(2, retained.Count);
        var archived = retained.Single(item => item.ExternalId == "beta@example.edu");
        Assert.True(archived.IsArchived);
        Assert.Equal(FeedHarness.Day(3), archived.ArchivedAt);
        Assert.Equal("Beta task", archived.Title);
    }

    [Fact]
    public async Task ArchivingPreservesCompletedAndIgnoredHistory()
    {
        using var harness = Harness(3, Ics.Full, Ics.Partial, Ics.Partial, Ics.Partial);
        await RefreshAsync(harness, 0, RefreshTrigger.Manual);
        var beta = (await harness.Repository.FetchAllAsync()).Single(item => item.ExternalId == "beta@example.edu");
        await harness.Repository.UpdateStatusAsync(beta.Id, isCompleted: true, isIgnored: true, FeedHarness.Day(0));

        for (var day = 1; day <= 3; day++)
        {
            await RefreshAsync(harness, day);
        }

        var archived = (await harness.Repository.FetchAllAsync(includingArchived: true)).Single(item => item.ExternalId == "beta@example.edu");
        Assert.True(archived.IsArchived);
        Assert.True(archived.IsCompleted);
        Assert.True(archived.IsIgnored);
    }

    [Fact]
    public async Task ReturningEventIsRestoredWithItsLocalState()
    {
        using var harness = Harness(3, Ics.Full, Ics.Partial, Ics.Partial, Ics.Partial, Ics.Full);
        await RefreshAsync(harness, 0, RefreshTrigger.Manual);
        var beta = (await harness.Repository.FetchAllAsync()).Single(item => item.ExternalId == "beta@example.edu");
        await harness.Repository.UpdateStatusAsync(beta.Id, isCompleted: true, isIgnored: null, FeedHarness.Day(0));
        for (var day = 1; day <= 3; day++)
        {
            await RefreshAsync(harness, day);
        }
        Assert.Single(await harness.Repository.FetchAllAsync());

        await RefreshAsync(harness, 4);

        var visible = await harness.Repository.FetchAllAsync();
        Assert.Equal(2, visible.Count);
        var restored = visible.Single(item => item.ExternalId == "beta@example.edu");
        Assert.False(restored.IsArchived);
        Assert.Equal(0, restored.MissingRefreshCount);
        Assert.True(restored.IsCompleted);
        Assert.Equal(beta.Id, restored.Id);
    }

    [Fact]
    public async Task NetworkFailureNeverCountsAsAbsence()
    {
        var fetcher = new ScriptedFetcher(
            () => Encoding.UTF8.GetBytes(Ics.Full),
            () => throw CanvasFeedFetchException.NetworkFailure(-1009),
            () => throw CanvasFeedFetchException.ResponseTooLarge(1),
            () => throw CanvasFeedFetchException.InsecureRedirect(),
            () => throw CanvasFeedFetchException.HttpStatus(503));
        using var harness = new FeedHarness(fetcher);
        await RefreshAsync(harness, 0, RefreshTrigger.Manual);

        for (var day = 1; day <= 4; day++)
        {
            await Assert.ThrowsAsync<CanvasFeedFetchException>(() => RefreshAsync(harness, day));
        }

        var stored = await harness.Repository.FetchAllAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item =>
        {
            Assert.Equal(0, item.MissingRefreshCount);
            Assert.False(item.IsArchived);
        });
    }

    [Fact]
    public async Task ParserFailureNeverCountsAsAbsence()
    {
        using var harness = Harness(3, Ics.Full, "not an ical document", "not an ical document", "not an ical document");
        await RefreshAsync(harness, 0, RefreshTrigger.Manual);

        for (var day = 1; day <= 3; day++)
        {
            await Assert.ThrowsAsync<IcsParserException>(() => RefreshAsync(harness, day));
        }

        var stored = await harness.Repository.FetchAllAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item => Assert.Equal(0, item.MissingRefreshCount));
    }

    [Fact]
    public async Task ValidButEmptyFeedIsTreatedAsSuspiciousRatherThanAuthoritative()
    {
        using var harness = Harness(3, Ics.Full, Ics.Empty, Ics.Empty, Ics.Empty, Ics.Empty);
        await RefreshAsync(harness, 0, RefreshTrigger.Manual);

        for (var day = 1; day <= 4; day++)
        {
            await RefreshAsync(harness, day);
        }

        var stored = await harness.Repository.FetchAllAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item => Assert.Equal(0, item.MissingRefreshCount));
    }

    [Fact]
    public async Task CancellationTombstoneArchivesImmediatelyWithoutDeleting()
    {
        var cancellation = Ics.Calendar(
            Ics.Event("alpha@example.edu", "20260810T100000Z", "Assignment: Alpha task [COMP1000]") + "\r\n"
            + "BEGIN:VEVENT\r\nUID:beta@example.edu\r\nSEQUENCE:2\r\nSTATUS:CANCELLED\r\nEND:VEVENT");
        using var harness = Harness(3, Ics.Full, cancellation);

        await RefreshAsync(harness, 0, RefreshTrigger.Manual);
        await RefreshAsync(harness, 1);

        Assert.Equal(["alpha@example.edu"], (await harness.Repository.FetchAllAsync()).Select(item => item.ExternalId));
        var retained = await harness.Repository.FetchAllAsync(includingArchived: true);
        Assert.Equal(2, retained.Count);
        var cancelled = retained.Single(item => item.ExternalId == "beta@example.edu");
        Assert.True(cancelled.IsArchived);
        Assert.Equal(FeedHarness.Day(1), cancelled.ArchivedAt);
    }

    [Fact]
    public async Task ManualAssignmentsAreNeverAffectedByFeedAbsence()
    {
        using var harness = Harness(3, Ics.Full, Ics.Partial, Ics.Partial, Ics.Partial);
        await harness.Repository.SaveManualAsync(new ManualAssignmentDraft { Title = "Hand written", DueDate = FeedHarness.Day(9) }, FeedHarness.Day(0));

        for (var day = 0; day <= 3; day++)
        {
            await RefreshAsync(harness, day);
        }

        var manual = (await harness.Repository.FetchAllAsync()).Single(item => item.Source == AssignmentSource.Manual);
        Assert.False(manual.IsArchived);
        Assert.Equal(0, manual.MissingRefreshCount);
    }

    [Fact]
    public async Task ReconciliationOutcomeReportsWhatHappened()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync(
            [
                new AssignmentImportRecord { ExternalId = "alpha@example.edu", Title = "Alpha task", DueDate = FeedHarness.Day(0) },
                new AssignmentImportRecord { ExternalId = "beta@example.edu", Title = "Beta task", DueDate = FeedHarness.Day(1) },
            ],
            FeedHarness.Day(0));

        var outcome = await repository.ReconcileCanvasFeedAsync(new FeedReconciliationRequest
        {
            ActiveExternalIds = ["alpha@example.edu"],
            ObservedAt = FeedHarness.Day(1),
            ArchiveThreshold = 1,
        });

        Assert.Equal(1, outcome.ConfirmedCount);
        Assert.Equal(1, outcome.NewlyMissingCount);
        Assert.Equal(1, outcome.ArchivedCount);
        Assert.Equal(0, outcome.RestoredCount);
    }
}

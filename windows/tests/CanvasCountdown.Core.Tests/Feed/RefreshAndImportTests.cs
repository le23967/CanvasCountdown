using System.Text;

namespace CanvasCountdown.Tests.Feed;

/// <summary>The refresh coordinator's import, refresh and course-removal rules.</summary>
public sealed class RefreshAndImportTests
{
    private static async Task RefreshAsync(FeedHarness harness, int day)
        => await harness.Coordinator.RefreshAsync(FeedHarness.Day(day), RefreshTrigger.Automatic);

    [Fact]
    public async Task PreviewExcludesAvailabilityMarkersAndImportsSelection()
    {
        var feed = Ics.Calendar(
            Ics.Event("availability-1", "20260728T100000Z", "Final report available from Monday") + "\r\n"
            + Ics.Event("assignment-1", "20260729T090000Z", "Assignment: Final report [COMP1000]"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(feed), savesFeedUrl: false);

        var preview = await harness.Coordinator.PreviewAsync(FeedHarness.FeedUrl, FeedHarness.Day(0));

        Assert.Equal(["assignment-1"], preview.Events.Select(item => item.Uid));
        Assert.Equal(1, preview.ExcludedEventCount);

        var result = await harness.Coordinator.ImportSelectedAsync(preview.Events, FeedHarness.FeedUrl, FeedHarness.Day(0));

        Assert.Equal(1, result.ImportResult.InsertedCount);
        var stored = Assert.Single(await harness.Repository.FetchAllAsync());
        Assert.Equal("Final report", stored.Title);
        Assert.Equal("COMP1000", stored.CourseName);
        Assert.Equal(FeedHarness.FeedUrl, await harness.FeedUrlStore.LoadFeedUrlAsync());
    }

    [Fact]
    public async Task ImportDoesNotSaveFeedUrlWhenRepositoryMergeFails()
    {
        using var harness = new FeedHarness(ScriptedFetcher.Returning(Ics.Full), savesFeedUrl: false);
        var broken = new ParsedCalendarEvent
        {
            Uid = "assignment-1",
            Summary = "   ",
            StartDate = FeedHarness.Day(1),
            EndDate = null,
            Description = null,
            Url = null,
            LastModified = null,
            IsAllDay = false,
            TimeZoneIdentifier = null,
        };

        await Assert.ThrowsAsync<AssignmentRepositoryException>(
            () => harness.Coordinator.ImportSelectedAsync([broken], FeedHarness.FeedUrl, FeedHarness.Day(0)));

        Assert.Null(await harness.FeedUrlStore.LoadFeedUrlAsync());
        Assert.Empty(await harness.Repository.FetchAllAsync());
    }

    [Fact]
    public async Task OnboardingDeselectionDoesNotReturnOnNextRefresh()
    {
        using var harness = new FeedHarness(ScriptedFetcher.Returning(Ics.Full), savesFeedUrl: false);
        var preview = await harness.Coordinator.PreviewAsync(FeedHarness.FeedUrl, FeedHarness.Day(0));
        var chosen = preview.Events.Where(item => item.Uid == "alpha@example.edu").ToArray();

        await harness.Coordinator.ImportSelectedAsync(chosen, FeedHarness.FeedUrl, FeedHarness.Day(0));
        await RefreshAsync(harness, 1);
        await RefreshAsync(harness, 2);

        Assert.Equal(["alpha@example.edu"], (await harness.Repository.FetchAllAsync()).Select(item => item.ExternalId));
    }

    [Fact]
    public async Task ChangedDeadlinePreservesCompletedAndIgnoredState()
    {
        var moved = Ics.Calendar(
            Ics.Event("alpha@example.edu", "20260815T100000Z", "Assignment: Alpha task [COMP1000]") + "\r\n"
            + Ics.Event("beta@example.edu", "20260811T100000Z", "Assignment: Beta task [COMP1000]"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(Ics.Full, moved));
        await RefreshAsync(harness, 0);
        var alpha = (await harness.Repository.FetchAllAsync()).Single(item => item.ExternalId == "alpha@example.edu");
        await harness.Repository.UpdateStatusAsync(alpha.Id, isCompleted: true, isIgnored: true, FeedHarness.Day(0));
        await harness.Repository.SetLabelAsync(alpha.Id, Guid.Parse("11111111-2222-3333-4444-555555555555"), FeedHarness.Day(0));

        await RefreshAsync(harness, 1);

        var after = (await harness.Repository.FetchAllAsync()).Single(item => item.ExternalId == "alpha@example.edu");
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero), after.DueDate);
        Assert.True(after.IsCompleted);
        Assert.True(after.IsIgnored);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), after.LabelId);
        Assert.Equal(alpha.Id, after.Id);
    }

    [Fact]
    public async Task MalformedRefreshDoesNotMutateExistingAssignments()
    {
        using var harness = new FeedHarness(ScriptedFetcher.Returning(Ics.Full, "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:alpha@example.edu\r\n"));
        await RefreshAsync(harness, 0);
        var before = await harness.Repository.FetchAllAsync();

        try
        {
            await RefreshAsync(harness, 1);
        }
        catch (IcsParserException)
        {
        }

        Assert.Equal(before, await harness.Repository.FetchAllAsync());
    }

    [Fact]
    public async Task DistinctUidsSharingSourceUrlRemainDistinct()
    {
        var shared = Ics.Calendar(
            Ics.Event("quiz-a", "20260810T100000Z", "Quiz part A [STAT1010]", "URL:https://canvas.example.edu/courses/1/quizzes/9") + "\r\n"
            + Ics.Event("quiz-b", "20260812T100000Z", "Quiz part B [STAT1010]", "URL:https://canvas.example.edu/courses/1/quizzes/9"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(shared, shared));

        await RefreshAsync(harness, 0);
        await RefreshAsync(harness, 1);

        var stored = await harness.Repository.FetchAllAsync();
        Assert.Equal(["quiz-a", "quiz-b"], stored.Select(item => item.ExternalId));
    }

    [Fact]
    public async Task RejectedFeedUrlAndDiagnosticNeverExposeSecret()
    {
        using var harness = new FeedHarness(ScriptedFetcher.Returning(Ics.Full), savesFeedUrl: false);
        var insecure = new Uri("http://canvas.example.edu/feeds/calendar.ics?feed=supersecret");

        var error = await Assert.ThrowsAsync<FeedUrlValidationException>(() => harness.Coordinator.PreviewAsync(insecure, FeedHarness.Day(0)));
        var diagnostic = await harness.Coordinator.DiagnosticSnapshotAsync();

        Assert.DoesNotContain("supersecret", error.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic);
        Assert.DoesNotContain("supersecret", diagnostic!.ExportText, StringComparison.Ordinal);
        Assert.Contains("error=url.https-required", diagnostic.ExportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefreshWhileAnotherIsRunningIsRefusedNotQueued()
    {
        var gate = new TaskCompletionSource<byte[]>();
        var fetcher = new BlockingFetcher(gate.Task);
        using var harness = new FeedHarness(fetcher);

        var first = harness.Coordinator.RefreshAsync(FeedHarness.Day(0), RefreshTrigger.Manual);
        var second = await Assert.ThrowsAsync<RefreshCoordinatorException>(() => harness.Coordinator.RefreshAsync(FeedHarness.Day(0), RefreshTrigger.Automatic));
        gate.SetResult(Encoding.UTF8.GetBytes(Ics.Full));
        await first;

        Assert.Equal(RefreshCoordinatorError.OperationAlreadyInProgress, second.Error);
    }

    [Fact]
    public async Task OverdueEventsOlderThanThirtyDaysAreLeftOut()
    {
        var feed = Ics.Calendar(
            Ics.Event("old", "20260601T100000Z", "Long gone [HIST1000]") + "\r\n"
            + Ics.Event("recent", "20260720T100000Z", "Just missed [HIST1000]"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(feed));

        await RefreshAsync(harness, 0);

        Assert.Equal(["recent"], (await harness.Repository.FetchAllAsync()).Select(item => item.ExternalId));
    }

    // ------------------------------------------------------------------
    // Removing a course blocks the name, not just the events

    [Fact]
    public async Task ARefreshDoesNotBringABlockedCourseBack()
    {
        var feed = Ics.Calendar(
            Ics.Event("a", "20260810T100000Z", "Essay [ENGL1001]") + "\r\n"
            + Ics.Event("b", "20260811T100000Z", "Lab [CHEM1001]"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(feed));
        await RefreshAsync(harness, 0);

        await harness.Blocklist.BlockAsync("engl1001 ");
        await harness.Repository.DeleteEventsInCourseAsync("ENGL1001");
        await RefreshAsync(harness, 1);

        Assert.Equal(["CHEM1001"], (await harness.Repository.FetchAllAsync()).Select(item => item.CourseName));
    }

    [Fact]
    public async Task TheImportPreviewAlsoLeavesABlockedCourseOut()
    {
        var feed = Ics.Calendar(
            Ics.Event("a", "20260810T100000Z", "Essay [ENGL1001]") + "\r\n"
            + Ics.Event("b", "20260811T100000Z", "Lab [CHEM1001]"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(feed), savesFeedUrl: false);
        await harness.Blocklist.BlockAsync("ENGL1001");

        var preview = await harness.Coordinator.PreviewAsync(FeedHarness.FeedUrl, FeedHarness.Day(0));

        Assert.Equal(["b"], preview.Events.Select(item => item.Uid));
    }

    [Fact]
    public async Task AllowingACourseAgainLetsTheNextRefreshImportIt()
    {
        var feed = Ics.Calendar(Ics.Event("a", "20260810T100000Z", "Essay [ENGL1001]"));
        using var harness = new FeedHarness(ScriptedFetcher.Returning(feed));
        await harness.Blocklist.BlockAsync("ENGL1001");
        await RefreshAsync(harness, 0);
        Assert.Empty(await harness.Repository.FetchAllAsync());

        await harness.Blocklist.UnblockAsync("Engl1001");
        await RefreshAsync(harness, 1);

        Assert.Single(await harness.Repository.FetchAllAsync());
    }

    [Theory]
    [InlineData("Assignment: Final report [COMP1000]", "Final report", "COMP1000")]
    [InlineData("assignment:   Essay [ENGL 1001 Tutorial]", "Essay", "ENGL 1001 Tutorial")]
    [InlineData("Club meeting", "Club meeting", null)]
    [InlineData("[COMP1000]", "[COMP1000]", null)]
    [InlineData("Reading [ ]", "Reading [ ]", null)]
    public void CanvasTitlesAreSplitIntoTitleAndCourse(string summary, string title, string? course)
        => Assert.Equal((title, course), RefreshCoordinator.AssignmentMetadata(summary));

    private sealed class BlockingFetcher(Task<byte[]> result) : IFeedFetcher
    {
        public Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken = default) => result;
    }
}

/// <summary>The blocklist store on its own: spelling rules and reversibility.</summary>
public sealed class CourseBlocklistTests
{
    private static PreferencesCourseBlocklistStore Store() => new(JsonPreferencesStore.InMemory());

    [Fact]
    public async Task CourseNamesMatchWhateverTheirCaseAndSpacing()
    {
        var store = Store();
        await store.BlockAsync("  PHYS2001 Physics Lab ");

        Assert.True(await store.IsBlockedAsync("PHYS2001 physics lab"));
        Assert.True(await store.IsBlockedAsync("PHYS2001 PHYSICS LAB  "));
    }

    [Fact]
    public async Task AnEmptyCourseNameIsNeverBlocked()
    {
        var store = Store();
        await store.BlockAsync("   ");

        Assert.Empty(await store.LoadBlockedCoursesAsync());
        Assert.False(await store.IsBlockedAsync(null));
        Assert.False(await store.IsBlockedAsync(""));
    }

    [Fact]
    public async Task UnblockingIsExactAndLeavesOtherCoursesAlone()
    {
        var store = Store();
        await store.BlockAsync("COMP1000");
        await store.BlockAsync("COMP1001");

        await store.UnblockAsync("comp1000");

        Assert.Equal(["COMP1001"], await store.LoadBlockedCoursesAsync());
    }

    [Fact]
    public async Task ACourseIsShownBackTheWayItWasWritten()
    {
        var store = Store();
        await store.BlockAsync(" PHYS2001 Physics Lab ");

        Assert.Equal(["PHYS2001 Physics Lab"], await store.LoadBlockedCoursesAsync());
    }

    [Fact]
    public async Task BlockingTheSameCourseTwiceDoesNotListItTwice()
    {
        var store = Store();
        await store.BlockAsync("COMP1000");
        await store.BlockAsync("comp1000");

        Assert.Single(await store.LoadBlockedCoursesAsync());
    }

    [Fact]
    public async Task DeletingACourseTakesEveryOneOfItsEventsIncludingArchived()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync(
            [
                new AssignmentImportRecord { ExternalId = "a", Title = "One", CourseName = "HIST1000", DueDate = FeedHarness.Day(1) },
                new AssignmentImportRecord { ExternalId = "b", Title = "Two", CourseName = "hist1000 ", DueDate = FeedHarness.Day(2) },
                new AssignmentImportRecord { ExternalId = "c", Title = "Keep", CourseName = "MATH1000", DueDate = FeedHarness.Day(3) },
            ],
            FeedHarness.Day(0));
        await repository.ReconcileCanvasFeedAsync(new FeedReconciliationRequest
        {
            ActiveExternalIds = ["a", "c"],
            ExcludedExternalIds = ["b"],
            ObservedAt = FeedHarness.Day(0),
        });
        await repository.SaveManualAsync(new ManualAssignmentDraft { Title = "No course", DueDate = FeedHarness.Day(4) }, FeedHarness.Day(0));

        var removed = await repository.DeleteEventsInCourseAsync("HIST1000");

        Assert.Equal(2, removed);
        var left = await repository.FetchAllAsync(includingArchived: true);
        Assert.Equal(["Keep", "No course"], left.Select(item => item.Title));
        Assert.Equal(0, await repository.DeleteEventsInCourseAsync("NOPE1000"));
    }

    /// <summary>
    /// Allowing a course again brings back only what the feed still publishes,
    /// so an event typed in by hand would be gone for good. It is left alone,
    /// as Settings promises.
    /// </summary>
    [Fact]
    public async Task DeletingACourseLeavesEventsTypedInByHand()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync(
            [new AssignmentImportRecord { ExternalId = "a", Title = "Imported", CourseName = "HIST1000", DueDate = FeedHarness.Day(1) }],
            FeedHarness.Day(0));
        await repository.SaveManualAsync(new ManualAssignmentDraft { Title = "My own reading", CourseName = "HIST1000", DueDate = FeedHarness.Day(2) }, FeedHarness.Day(0));

        var removed = await repository.DeleteEventsInCourseAsync("HIST1000");

        Assert.Equal(1, removed);
        Assert.Equal(["My own reading"], (await repository.FetchAllAsync()).Select(item => item.Title));
    }
}

/// <summary>The store itself: merging, local state, and keeping instants exact.</summary>
public sealed class AssignmentRepositoryTests
{
    [Fact]
    public async Task DuplicateCanvasUidUpdatesInsteadOfInserting()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync([Record("uid-1", "Essay draft", FeedHarness.Day(1))], FeedHarness.Day(0));

        var result = await repository.UpsertAsync([Record(" UID-1 ", "Essay final", FeedHarness.Day(2))], FeedHarness.Day(1));

        Assert.Equal(new ImportResult(0, 1, 0), result);
        var stored = Assert.Single(await repository.FetchAllAsync());
        Assert.Equal("Essay final", stored.Title);
        Assert.Equal(FeedHarness.Day(2), stored.DueDate);
    }

    [Fact]
    public async Task CompletedStateSurvivesCanvasRefresh()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync([Record("uid-1", "Essay", FeedHarness.Day(1))], FeedHarness.Day(0));
        var stored = Assert.Single(await repository.FetchAllAsync());
        await repository.SetCompletedAsync(stored.Id, true);

        await repository.UpsertAsync([Record("uid-1", "Essay (edited)", FeedHarness.Day(1))], FeedHarness.Day(1));

        Assert.True(Assert.Single(await repository.FetchAllAsync()).IsCompleted);
    }

    [Fact]
    public async Task IgnoredStateSurvivesCanvasRefresh()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync([Record("uid-1", "Essay", FeedHarness.Day(1))], FeedHarness.Day(0));
        var stored = Assert.Single(await repository.FetchAllAsync());
        await repository.SetIgnoredAsync(stored.Id, true);

        await repository.UpsertAsync([Record("uid-1", "Essay", FeedHarness.Day(3))], FeedHarness.Day(1));

        Assert.True(Assert.Single(await repository.FetchAllAsync()).IsIgnored);
    }

    [Fact]
    public async Task AnUnchangedRecordCountsAsUnchanged()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync([Record("uid-1", "Essay", FeedHarness.Day(1))], FeedHarness.Day(0));

        var result = await repository.UpsertAsync([Record("uid-1", "Essay", FeedHarness.Day(1))], FeedHarness.Day(1));

        Assert.Equal(new ImportResult(0, 0, 1), result);
    }

    [Fact]
    public async Task InstantsAndEveryFieldSurviveReopeningTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cc-repo-{Guid.NewGuid():N}.sqlite");
        try
        {
            var due = new DateTimeOffset(2026, 10, 4, 2, 30, 15, 123, TimeSpan.FromHours(10)).AddTicks(4567);
            Guid id;
            using (var repository = new SqliteAssignmentRepository(path))
            {
                var saved = await repository.SaveManualAsync(new ManualAssignmentDraft { Title = " Lab write-up ", CourseName = " CHEM1001 ", DueDate = due, SourceUrl = "https://example.edu/x" }, FeedHarness.Day(0));
                id = saved.Id;
                await repository.SetLabelAsync(id, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), FeedHarness.Day(0));
            }

            using var reopened = new SqliteAssignmentRepository(path);
            var stored = Assert.Single(await reopened.FetchAllAsync());
            Assert.Equal(id, stored.Id);
            Assert.Equal("Lab write-up", stored.Title);
            Assert.Equal("CHEM1001", stored.CourseName);
            Assert.Equal(due, stored.DueDate);
            Assert.Equal(due.UtcTicks, stored.DueDate.UtcTicks);
            Assert.Equal(AssignmentSource.Manual, stored.Source);
            Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), stored.LabelId);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ImportedEventsCannotBeEditedAsManual()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        await repository.UpsertAsync([Record("uid-1", "Essay", FeedHarness.Day(1))], FeedHarness.Day(0));
        var stored = Assert.Single(await repository.FetchAllAsync());

        var error = await Assert.ThrowsAsync<AssignmentRepositoryException>(
            () => repository.SaveManualAsync(new ManualAssignmentDraft { Id = stored.Id, Title = "Mine now", DueDate = FeedHarness.Day(1) }, FeedHarness.Day(1)));

        Assert.Equal(AssignmentRepositoryError.CannotEditImportedAssignmentAsManual, error.Error);
    }

    [Fact]
    public async Task ABlankTitleIsRefused()
    {
        using var repository = SqliteAssignmentRepository.InMemory();

        var error = await Assert.ThrowsAsync<AssignmentRepositoryException>(
            () => repository.SaveManualAsync(new ManualAssignmentDraft { Title = "   ", DueDate = FeedHarness.Day(1) }, FeedHarness.Day(0)));

        Assert.Equal(AssignmentRepositoryError.EmptyTitle, error.Error);
        Assert.Empty(await repository.FetchAllAsync());
    }

    [Fact]
    public async Task ClearingALabelTakesItOffEverythingCarryingIt()
    {
        using var repository = SqliteAssignmentRepository.InMemory();
        var label = Guid.NewGuid();
        var first = await repository.SaveManualAsync(new ManualAssignmentDraft { Title = "One", DueDate = FeedHarness.Day(1) }, FeedHarness.Day(0));
        var second = await repository.SaveManualAsync(new ManualAssignmentDraft { Title = "Two", DueDate = FeedHarness.Day(2) }, FeedHarness.Day(0));
        await repository.SetLabelAsync(first.Id, label, FeedHarness.Day(0));
        await repository.SetLabelAsync(second.Id, label, FeedHarness.Day(0));

        Assert.Equal(2, await repository.ClearLabelAsync(label, FeedHarness.Day(1)));
        Assert.All(await repository.FetchAllAsync(), item => Assert.Null(item.LabelId));
    }

    [Fact]
    public async Task DeletingSomethingThatIsGoneSaysSo()
    {
        using var repository = SqliteAssignmentRepository.InMemory();

        var error = await Assert.ThrowsAsync<AssignmentRepositoryException>(() => repository.DeleteAsync(Guid.NewGuid()));

        Assert.Equal(AssignmentRepositoryError.AssignmentNotFound, error.Error);
    }

    private static AssignmentImportRecord Record(string uid, string title, DateTimeOffset due)
        => new() { ExternalId = uid, Title = title, CourseName = "ENGL1001", DueDate = due };
}

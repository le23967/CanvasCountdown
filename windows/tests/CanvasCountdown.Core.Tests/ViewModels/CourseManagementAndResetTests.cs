namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// Removing a course and letting it back in, resetting everything, and what
/// a first launch shows. The course tests are ported from the view model half
/// of the Mac app's CourseManagementTests, with invented courses.
/// </summary>
public sealed class CourseManagementAndResetTests
{
    private const string Studio = "DESN1500 Studio Practice";

    private static string Feed() => string.Join(
        "\r\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "BEGIN:VEVENT",
        "UID:video-1",
        "DTSTART:20260814T000000Z",
        $"SUMMARY:Video 1 [{Studio}]",
        "END:VEVENT",
        "BEGIN:VEVENT",
        "UID:video-2",
        "DTSTART:20260815T000000Z",
        $"SUMMARY:Video 2 [{Studio}]",
        "END:VEVENT",
        "BEGIN:VEVENT",
        "UID:lab-report",
        "DTSTART:20260820T000000Z",
        "SUMMARY:Lab report [PHYS200]",
        "END:VEVENT",
        "END:VCALENDAR");

    /// <summary>Connected to an invented feed, imported, and started.</summary>
    private static async Task<ViewModelHarness> ConnectedAsync()
    {
        var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.Fetcher.Payload = Feed();
        var preview = await harness.ViewModel.PreviewFeedAsync(ViewModelHarness.FeedUrl);
        await harness.ViewModel.ImportFeedAsync(ViewModelHarness.FeedUrl, preview.Select(item => item.Id).ToHashSet());
        return harness;
    }

    private static async Task RefreshAsync(ViewModelHarness harness)
    {
        var before = harness.ViewModel.LastRefreshDate;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.ViewModel.RefreshManually();
        await ViewModelHarness.WaitUntil(
            () => harness.ViewModel.LastRefreshDate != before && !harness.ViewModel.IsRefreshing,
            "the refresh has finished");
    }

    // ------------------------------------------------------------------
    // Courses

    [Fact]
    public void SettingsListsEachCourseOnceWithItsEventCount() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await ConnectedAsync();

        Assert.Equal([Studio, "PHYS200"], harness.ViewModel.ManagedCourses.Select(course => course.Name));
        Assert.Equal([2, 1], harness.ViewModel.ManagedCourses.Select(course => course.EventCount));
        Assert.Equal("2 events", harness.ViewModel.ManagedCourses[0].EventCountDescription);
        Assert.Equal("1 event", harness.ViewModel.ManagedCourses[1].EventCountDescription);
    });

    [Fact]
    public void RemovingACourseClearsItsEventsAndBlocksIt() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await ConnectedAsync();

        await harness.ViewModel.RemoveCourseAsync(Studio);

        Assert.Equal(["PHYS200"], harness.ViewModel.ManagedCourses.Select(course => course.Name));
        Assert.Equal([Studio], harness.ViewModel.BlockedCourses);
        Assert.DoesNotContain(harness.ViewModel.Assignments, item => item.NormalizedCourseName == Studio);
        Assert.Equal($"Removed “{Studio}” and 2 events", harness.ViewModel.StatusMessage);
    });

    [Fact]
    public void ARefreshDoesNotBringARemovedCourseBackButAllowingItDoes() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await ConnectedAsync();
        await harness.ViewModel.RemoveCourseAsync(Studio);

        await RefreshAsync(harness);
        Assert.DoesNotContain(harness.ViewModel.Assignments, item => item.NormalizedCourseName == Studio);

        await harness.ViewModel.AllowCourseAsync(Studio);
        Assert.Empty(harness.ViewModel.BlockedCourses);
        Assert.Equal($"“{Studio}” will return on the next refresh", harness.ViewModel.StatusMessage);

        await RefreshAsync(harness);
        Assert.Equal(2, harness.ViewModel.Assignments.Count(item => item.NormalizedCourseName == Studio));
    });

    [Fact]
    public void RemovingACourseDropsItFromTheSavedScopeAndTheFilter() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await ConnectedAsync();
        harness.ViewModel.UpdateSettings(form => form with
        {
            DockCourseScope = DockCourseScopeOption.SelectedCourses,
            SelectedCourses = new[] { "PHYS200", Studio }.ToValueSet(),
        });
        harness.ViewModel.SelectCourse(Studio);

        await harness.ViewModel.RemoveCourseAsync(Studio);

        // A removed course must not linger in the tray's scope or the toolbar.
        Assert.Equal(["PHYS200"], harness.Settings.SelectedCourses.ToArray());
        Assert.Equal(["PHYS200"], harness.ViewModel.SettingsForm.SelectedCourses.ToArray());
        Assert.Null(harness.ViewModel.SelectedCourse);
    });

    [Fact]
    public void RemovingACourseKeepsEventsTypedInByHand() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await ConnectedAsync();
        await harness.ViewModel.SaveManualEventAsync(new ManualEventDraft { Title = "My own notes", CourseName = Studio, DueDate = ViewModelHarness.Day(9) });
        var course = harness.ViewModel.ManagedCourses.Single(entry => entry.Name == Studio);
        Assert.Equal(3, course.EventCount);
        Assert.Equal(2, course.ImportedEventCount);
        // The confirmation counts only what will really go.
        Assert.Equal(
            $"The 2 events Canvas imported for “{Studio}” will be deleted from this PC, and the Canvas feed will stop importing it. The 1 event you added yourself stays. You can allow it again later.",
            course.RemovalMessage);

        await harness.ViewModel.RemoveCourseAsync(Studio);

        Assert.Equal(["My own notes"], harness.ViewModel.Assignments.Where(item => item.NormalizedCourseName == Studio).Select(item => item.Title));
        Assert.Equal($"Removed “{Studio}” and 2 events", harness.ViewModel.StatusMessage);
    });

    [Fact]
    public void TheConfirmationForAnAllCanvasCourseNamesItsEvents()
    {
        var course = new ManagedCourse("PHYS200", 1) { ImportedEventCount = 1 };

        Assert.Equal(
            "“PHYS200” and its 1 event will be deleted from this PC, and the Canvas feed will stop importing it. You can allow it again later.",
            course.RemovalMessage);
    }

    [Fact]
    public void AllowingACourseWithoutAFeedSaysSoPlainly() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddImportedAsync("Lab report", ViewModelHarness.Day(3), "PHYS200");
        await harness.ViewModel.StartAsync();
        await harness.ViewModel.RemoveCourseAsync("PHYS200");

        await harness.ViewModel.AllowCourseAsync("PHYS200");

        Assert.Equal("“PHYS200” is no longer blocked", harness.ViewModel.StatusMessage);
    });

    // ------------------------------------------------------------------
    // Resetting everything

    [Fact]
    public void ResettingLocalDataLeavesNothingBehind() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await ConnectedAsync();
        await harness.ViewModel.RemoveCourseAsync("PHYS200");
        await harness.ViewModel.SaveManualEventAsync(new ManualEventDraft { Title = "Mine", DueDate = ViewModelHarness.Day(2) });
        harness.ViewModel.UpdateSettings(form => form with { RefreshInterval = RefreshInterval.Daily, LaunchAtLogin = true });

        await harness.ViewModel.ResetLocalDataAsync();

        Assert.Empty(harness.ViewModel.Assignments);
        Assert.Empty(await harness.Repository.FetchAllAsync(includingArchived: true));
        Assert.Null(await harness.FeedUrlStore.LoadFeedUrlAsync());
        Assert.False(harness.ViewModel.HasConfiguredFeed);
        Assert.Empty(harness.ViewModel.BlockedCourses);
        Assert.False(harness.LaunchAtLogin.IsEnabled);
        Assert.Equal(RefreshInterval.EverySixHours, new SettingsStore(harness.Preferences).RefreshInterval);
        Assert.Equal(1, harness.Notifications.CancelAllCount);
        Assert.Null(harness.Dock.LastDays);
        // Nothing survives a reset, so there is nothing left to undo.
        Assert.False(harness.ViewModel.CanUndoAddition);
        Assert.Equal("Local data reset", harness.ViewModel.StatusMessage);
    });

    // ------------------------------------------------------------------
    // First launch

    [Fact]
    public void AFirstLaunchOffersToConnectTheFeed() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness(dependencies => dependencies with { AutomaticActivityEnabled = true });

        await harness.ViewModel.StartAsync();

        Assert.True(harness.ViewModel.IsShowingFeedImport);
        Assert.True(harness.ViewModel.FeedImportIsOnboarding);
        harness.ViewModel.Dispose();
    });

    [Fact]
    public void ALaunchWithEventsButNoFeedDoesNotNag() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness(dependencies => dependencies with { AutomaticActivityEnabled = true });
        await harness.AddManualAsync("Mine", ViewModelHarness.Day(2));

        await harness.ViewModel.StartAsync();

        Assert.False(harness.ViewModel.IsShowingFeedImport);
        harness.ViewModel.Dispose();
    });

    /// <summary>
    /// The isolated launch the UI tour uses: no refresh, no reminders, no
    /// update check, whatever is stored.
    /// </summary>
    [Fact]
    public void WithoutAutomaticActivityStartingReachesNothingOutside() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.SaveFeedAsync(Feed());

        await harness.ViewModel.StartAsync();

        Assert.Equal(0, harness.Fetcher.CallCount);
        Assert.Equal(0, harness.Releases.CallCount);
        Assert.Empty(harness.Notifications.Reschedules);
        Assert.False(harness.ViewModel.IsShowingFeedImport);
    });

    [Fact]
    public void WithAutomaticActivityALaunchRefreshesAndChecksForUpdates() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness(dependencies => dependencies with { AutomaticActivityEnabled = true });
        await harness.SaveFeedAsync(Feed());

        await harness.ViewModel.StartAsync();

        Assert.Equal(1, harness.Fetcher.CallCount);
        Assert.Equal(1, harness.Releases.CallCount);
        Assert.Equal(3, harness.ViewModel.Assignments.Count);
        Assert.NotEmpty(harness.Notifications.Reschedules);
        harness.ViewModel.Dispose();
    });
}

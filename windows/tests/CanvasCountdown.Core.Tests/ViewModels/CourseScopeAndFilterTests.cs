namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// The saved course scope (Settings) and the toolbar's course filter. The
/// scope focuses Upcoming, its count, the nearest-deadline card and the tray
/// countdown together, while All Events and Completed stay complete; the
/// filter narrows the list within the scope and never moves the tray. Ported
/// from the Mac app's CourseScopeTests and CourseFilterMenuTests.
/// </summary>
public sealed class CourseScopeAndFilterTests
{
    private const string LongCourse = "99999 88888 Example Long Course Title For Layout Testing Only";

    private static async Task<ViewModelHarness> StartedAsync(bool includingCompleted = false, string physics = "PHYS200")
    {
        var harness = new ViewModelHarness();
        await harness.AddImportedAsync("Maths sheet", ViewModelHarness.Day(1), "MATH100");
        await harness.AddImportedAsync("Physics lab", ViewModelHarness.Day(3), physics);
        await harness.AddManualAsync("Personal errand", ViewModelHarness.Day(5));
        if (includingCompleted)
        {
            await harness.AddAsync("Old maths quiz", ViewModelHarness.Day(-4), "MATH100", completed: true);
        }
        await harness.ViewModel.StartAsync();
        return harness;
    }

    private static void Scope(ViewModelHarness harness, params string[] courses)
        => harness.ViewModel.UpdateSettings(form => form with
        {
            DockCourseScope = DockCourseScopeOption.SelectedCourses,
            SelectedCourses = courses.ToValueSet(),
        });

    private static string[] Titles(IEnumerable<AssignmentListItem> items) => items.Select(item => item.Title).ToArray();

    // ------------------------------------------------------------------
    // The saved scope

    [Fact]
    public void AllAssignmentsScopeShowsEveryCourse() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        Assert.Equal(["Maths sheet", "Physics lab", "Personal errand"], Titles(harness.ViewModel.FilteredAssignments));
        Assert.Equal(3, harness.ViewModel.UpcomingCount);
        Assert.Equal("Maths sheet", harness.ViewModel.NearestAssignment?.Title);
    });

    [Fact]
    public void SelectedCoursesScopeFiltersUpcomingAndItsCount() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Scope(harness, "PHYS200");
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        // Only the chosen course, plus entries with no course.
        Assert.Equal(["Physics lab", "Personal errand"], Titles(harness.ViewModel.FilteredAssignments));
        Assert.Equal(2, harness.ViewModel.UpcomingCount);
        Assert.Equal("Physics lab", harness.ViewModel.NearestAssignment?.Title);
    });

    [Fact]
    public void AssignmentsWithoutACourseAreAlwaysIncluded() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Scope(harness);
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        // A manual event with no course must never silently disappear.
        Assert.Equal(["Personal errand"], Titles(harness.ViewModel.FilteredAssignments));
        Assert.Equal(1, harness.ViewModel.UpcomingCount);
    });

    [Fact]
    public void AllEventsRemainsACompleteLibrary() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Scope(harness, "PHYS200");
        harness.ViewModel.SidebarSelection = SidebarDestination.AllEvents;

        Assert.Equal(["Maths sheet", "Physics lab", "Personal errand"], Titles(harness.ViewModel.FilteredAssignments));
    });

    [Fact]
    public void CompletedRemainsACompleteHistory() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync(includingCompleted: true);
        Scope(harness, "PHYS200");
        harness.ViewModel.SidebarSelection = SidebarDestination.Completed;

        // A completed assignment from a course outside the scope stays visible.
        Assert.Equal(["Old maths quiz"], Titles(harness.ViewModel.FilteredAssignments));
    });

    [Fact]
    public void SearchReachesPastTheCourseScopeButTheListDoesNot() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Scope(harness, "PHYS200");
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;
        harness.ViewModel.PresentSearch();

        // Hiding a result because of a filter set elsewhere would look like
        // the event no longer exists.
        harness.ViewModel.SearchText = "Maths";
        Assert.Equal(["Maths sheet"], Titles(harness.ViewModel.SearchResults));

        // Course names are searchable.
        harness.ViewModel.SearchText = "PHYS";
        Assert.Equal(["Physics lab"], Titles(harness.ViewModel.SearchResults));

        // Closing the panel leaves the scoped list exactly as it was.
        harness.ViewModel.DismissSearch();
        Assert.Equal(["Physics lab", "Personal errand"], Titles(harness.ViewModel.FilteredAssignments));
    });

    [Fact]
    public void ChangingTheScopeUpdatesTheTrayImmediately() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Assert.Equal(1, harness.Dock.LastDays);

        Scope(harness, "PHYS200");

        // The tray follows the same scope as Upcoming.
        Assert.Equal(3, harness.Dock.LastDays);
        Assert.Equal("Physics lab", harness.ViewModel.NearestAssignment?.Title);
        Assert.Equal(2, harness.ViewModel.UpcomingCount);
    });

    [Fact]
    public void ScopeSelectionIsPersisted() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        Scope(harness, "PHYS200");

        var reloaded = new SettingsStore(harness.Preferences);
        Assert.Equal(DockCountMode.SelectedCourses, reloaded.DockCountMode);
        Assert.Equal(["PHYS200"], reloaded.SelectedCourses.ToArray());
    });

    [Fact]
    public void ScopeNeverRemovesStoredAssignments() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        Scope(harness, "PHYS200");

        // Filtering is a view concern only.
        Assert.Equal(3, (await harness.Repository.FetchAllAsync()).Count);
        Assert.Equal(3, harness.ViewModel.Assignments.Count);
    });

    // ------------------------------------------------------------------
    // The toolbar filter

    [Fact]
    public void AllCoursesIsSelectableAndShowsEverything() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        harness.ViewModel.SelectCourse("PHYS200");
        harness.ViewModel.SelectCourse(null);

        Assert.Null(harness.ViewModel.SelectedCourse);
        Assert.Equal(["Maths sheet", "Physics lab", "Personal errand"], Titles(harness.ViewModel.FilteredAssignments));
    });

    [Fact]
    public void EachCourseIsSelectableAndFiltersImmediately() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        foreach (var course in harness.ViewModel.AvailableCourses)
        {
            harness.ViewModel.SelectCourse(course);

            Assert.Equal(course, harness.ViewModel.SelectedCourse);
            Assert.NotEmpty(harness.ViewModel.FilteredAssignments);
            Assert.All(harness.ViewModel.FilteredAssignments, item => Assert.Equal(course, item.NormalizedCourseName));
        }
    });

    /// <summary>
    /// The filled icon is the only thing left saying a filter is on, so it has
    /// to answer for both rules, or turning one on and forgetting looks
    /// exactly like events going missing.
    /// </summary>
    [Fact]
    public void TheFilterReadsAsActiveFromEitherRule() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Assert.False(harness.ViewModel.IsFilterActive);

        harness.ViewModel.ShowCompletedAndIgnored = true;
        Assert.True(harness.ViewModel.IsFilterActive);

        harness.ViewModel.ShowCompletedAndIgnored = false;
        harness.ViewModel.SelectCourse("PHYS200");
        Assert.True(harness.ViewModel.IsFilterActive);
    });

    [Fact]
    public void TheDescriptionNamesBothRules() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Assert.Equal("Filter, showing all courses", harness.ViewModel.CourseFilterDescription);

        harness.ViewModel.SelectCourse("PHYS200");
        harness.ViewModel.ShowCompletedAndIgnored = true;

        Assert.Equal("Filter, showing PHYS200, including completed and ignored", harness.ViewModel.CourseFilterDescription);
    });

    [Fact]
    public void SelectionUpdatesTheSidebarCountAndNearestCard() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Assert.Equal(3, harness.ViewModel.UpcomingCount);
        Assert.Equal("Maths sheet", harness.ViewModel.NearestAssignment?.Title);

        harness.ViewModel.SelectCourse("PHYS200");
        Assert.Equal(1, harness.ViewModel.UpcomingCount);
        Assert.Equal("Physics lab", harness.ViewModel.NearestAssignment?.Title);

        harness.ViewModel.SelectCourse(null);
        Assert.Equal(3, harness.ViewModel.UpcomingCount);
        Assert.Equal("Maths sheet", harness.ViewModel.NearestAssignment?.Title);
    });

    [Fact]
    public void TheFilterNarrowsWithinTheSavedCourseScope() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Scope(harness, "MATH100");

        harness.ViewModel.SelectCourse("PHYS200");

        // A toolbar choice cannot reach past the saved scope.
        Assert.Empty(harness.ViewModel.VisibleUpcomingAssignments);
    });

    [Fact]
    public void TheTrayKeepsFollowingTheSavedScopeNotTheToolbarFilter() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        Assert.Equal(1, harness.Dock.LastDays);
        var rendersBefore = harness.Dock.Renders.Count;

        harness.ViewModel.SelectCourse("PHYS200");

        // Browsing the list must not repaint the tray, which still counts
        // down to the real nearest deadline.
        Assert.Equal(rendersBefore, harness.Dock.Renders.Count);
        Assert.Equal(1, harness.Dock.LastDays);
    });

    [Fact]
    public void SelectionIsClearedWhenTheCourseDisappears() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SelectCourse("PHYS200");

        await harness.Repository.DeleteAllAsync();
        await harness.ViewModel.SaveManualEventAsync(ManualEventDraft.New(ViewModelHarness.Start, ViewModelHarness.Sydney) with { Title = "Only manual" });

        // A filter pointing at a course that no longer exists must clear.
        Assert.Null(harness.ViewModel.SelectedCourse);
    });

    [Fact]
    public void TheMenuOffersOneFlatLevelOfChoices() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        string?[] entries = [null, .. harness.ViewModel.AvailableCourses];

        Assert.Equal(3, entries.Length);
        foreach (var entry in entries)
        {
            harness.ViewModel.SelectCourse(entry);
            Assert.Equal(entry, harness.ViewModel.SelectedCourse);
        }
    });

    [Fact]
    public void TheToolbarTitleStaysCompactForLongCourseNames() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync(physics: LongCourse);
        var longCourse = harness.ViewModel.AvailableCourses.First(course => course.Length > 40);

        harness.ViewModel.SelectCourse(longCourse);

        // A long Canvas title must not stretch the toolbar…
        Assert.True(harness.ViewModel.CourseFilterButtonTitle.Length <= 18, harness.ViewModel.CourseFilterButtonTitle);
        Assert.EndsWith("…", harness.ViewModel.CourseFilterButtonTitle, StringComparison.Ordinal);
        // …and the full title stays available to Narrator and the tooltip.
        Assert.Contains(longCourse, harness.ViewModel.CourseFilterDescription, StringComparison.Ordinal);
    });

    [Fact]
    public void TheMenuTitleTruncatesOnlyWhenNecessary()
    {
        Assert.Equal("All Courses", MainViewModel.MenuTitle(null));
        Assert.Equal("PHYS200", MainViewModel.MenuTitle("PHYS200"));

        var truncated = MainViewModel.MenuTitle(new string('A', 80));

        Assert.Equal(44, truncated.Length);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultSelectionIsAllCourses() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        Assert.Null(harness.ViewModel.SelectedCourse);
        Assert.Equal("All Courses", harness.ViewModel.CourseFilterButtonTitle);
    });
}

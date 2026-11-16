namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// Search (Ctrl+F): a panel over the list that looks through everything,
/// never a filter on the list itself, and that gets out of the way of every
/// other action. Ported from the Mac app's ToolbarSearchTests.
/// </summary>
public sealed class SearchTests
{
    private static async Task<ViewModelHarness> StartedAsync(bool includingCompleted = false)
    {
        var harness = new ViewModelHarness();
        await harness.AddImportedAsync("Maths sheet", ViewModelHarness.Day(1), "MATH100");
        await harness.AddImportedAsync("Physics lab", ViewModelHarness.Day(3), "PHYS200");
        await harness.AddManualAsync("Personal errand", ViewModelHarness.Day(5));
        if (includingCompleted)
        {
            await harness.AddAsync("Old maths quiz", ViewModelHarness.Day(-4), "MATH100", completed: true);
        }
        await harness.ViewModel.StartAsync();
        return harness;
    }

    private static string[] Titles(IEnumerable<AssignmentListItem> items) => items.Select(item => item.Title).ToArray();

    [Fact]
    public void SearchStartsClosedAndUnfocused() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        Assert.False(harness.ViewModel.IsSearchModeActive);
        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.Empty(harness.ViewModel.SearchText);
    });

    [Fact]
    public void OpeningSearchShowsAndFocusesTheField() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        harness.ViewModel.PresentSearch();

        Assert.True(harness.ViewModel.IsSearchModeActive);
        Assert.True(harness.ViewModel.IsSearchFieldFocused);
    });

    [Fact]
    public void TheSearchButtonTogglesSearchClosedAgain() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        harness.ViewModel.ToggleSearch();
        Assert.True(harness.ViewModel.IsSearchModeActive);

        harness.ViewModel.ToggleSearch();
        Assert.False(harness.ViewModel.IsSearchModeActive);
        Assert.False(harness.ViewModel.IsSearchFieldFocused);
    });

    [Fact]
    public void DismissingSearchClearsTheQuery() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        harness.ViewModel.SearchText = "maths";
        Assert.NotEmpty(harness.ViewModel.SearchResults);

        harness.ViewModel.DismissSearch();

        Assert.False(harness.ViewModel.IsSearchModeActive);
        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.Empty(harness.ViewModel.SearchText);
        Assert.Empty(harness.ViewModel.SearchResults);
        Assert.Null(harness.ViewModel.HighlightedSearchResultId);
    });

    [Theory]
    [InlineData("lab")]
    [InlineData("")]
    public void EscapeLeavesSearchAndClearsTheQuery(string query) => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        harness.ViewModel.SearchText = query;

        harness.ViewModel.HandleSearchEscape();

        Assert.False(harness.ViewModel.IsSearchModeActive);
        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.Empty(harness.ViewModel.SearchText);
    });

    [Fact]
    public void AnotherToolbarActionReleasesFocusAndStillRuns() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        Assert.True(harness.ViewModel.IsSearchFieldFocused);

        harness.ViewModel.PrepareForToolbarAction();
        harness.ViewModel.PresentNewManualEvent();

        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.True(harness.ViewModel.IsShowingManualEditor);
    });

    [Fact]
    public void ChangingSectionDismissesSearch() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        harness.ViewModel.SearchText = "lab";

        harness.ViewModel.SidebarSelection = SidebarDestination.AllEvents;

        // A query must not follow the user into another section.
        Assert.False(harness.ViewModel.IsSearchModeActive);
        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.Empty(harness.ViewModel.SearchText);
    });

    [Fact]
    public void ReselectingTheSameSectionKeepsSearchOpen() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;
        harness.ViewModel.PresentSearch();

        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        Assert.True(harness.ViewModel.IsSearchModeActive);
    });

    [Fact]
    public void TheQueryFillsThePanelAndLeavesTheListAlone() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;
        harness.ViewModel.PresentSearch();

        harness.ViewModel.SearchText = "lab";

        Assert.Equal(["Physics lab"], Titles(harness.ViewModel.SearchResults));
        Assert.Equal(3, harness.ViewModel.FilteredAssignments.Count);

        // Clearing is not the same as dismissing.
        harness.ViewModel.ClearSearchQuery();
        Assert.Empty(harness.ViewModel.SearchResults);
        Assert.True(harness.ViewModel.IsSearchModeActive);
    });

    [Fact]
    public void SearchLooksThroughEverythingNotJustTheSectionOnScreen() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync(includingCompleted: true);
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;
        harness.ViewModel.PresentSearch();

        harness.ViewModel.SearchText = "old maths";

        // A completed assignment is still something you can look for.
        Assert.Equal(["Old maths quiz"], Titles(harness.ViewModel.SearchResults));
    });

    [Fact]
    public void WhatIsStillAheadComesFirst() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync(includingCompleted: true);
        harness.ViewModel.PresentSearch();

        harness.ViewModel.SearchText = "maths";

        Assert.Equal(["Maths sheet", "Old maths quiz"], Titles(harness.ViewModel.SearchResults));
    });

    [Fact]
    public void TheResultListIsBounded() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        for (var index = 0; index < MainViewModel.MaximumSearchResults + 5; index++)
        {
            await harness.AddManualAsync($"Exercise {index + 1}", ViewModelHarness.Day(index + 1));
        }
        await harness.ViewModel.StartAsync();
        harness.ViewModel.PresentSearch();

        harness.ViewModel.SearchText = "exercise";

        Assert.Equal(MainViewModel.MaximumSearchResults, harness.ViewModel.SearchResults.Count);
    });

    [Fact]
    public void ArrowKeysMoveTheHighlightAndWrapAround() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        harness.ViewModel.SearchText = "a";
        harness.ViewModel.SearchQueryDidChange();
        var results = harness.ViewModel.SearchResults;
        Assert.True(results.Count >= 2);

        Assert.Equal(results[0].Id, harness.ViewModel.HighlightedSearchResultId);
        harness.ViewModel.MoveSearchHighlight(1);
        Assert.Equal(results[1].Id, harness.ViewModel.HighlightedSearchResultId);
        harness.ViewModel.MoveSearchHighlight(-1);
        Assert.Equal(results[0].Id, harness.ViewModel.HighlightedSearchResultId);

        // Holding an arrow key must not dead-end.
        harness.ViewModel.MoveSearchHighlight(-1);
        Assert.Equal(results[^1].Id, harness.ViewModel.HighlightedSearchResultId);
    });

    [Fact]
    public void EnterOpensTheHighlightedResultAndClosesThePanel() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        harness.ViewModel.SearchText = "physics";
        harness.ViewModel.SearchQueryDidChange();

        Assert.True(harness.ViewModel.OpenHighlightedSearchResult());

        // The search is over once it has been answered.
        Assert.False(harness.ViewModel.IsSearchModeActive);
        Assert.Equal("Physics lab", harness.ViewModel.SelectedItem?.Title);
        Assert.Empty(harness.ViewModel.SearchText);
    });

    [Fact]
    public void EnterWithNoResultsDoesNothing() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();
        harness.ViewModel.SearchText = "nothing matches this";
        harness.ViewModel.SearchQueryDidChange();

        Assert.False(harness.ViewModel.OpenHighlightedSearchResult());
        Assert.True(harness.ViewModel.IsSearchModeActive);
    });

    [Fact]
    public void RowsStillOpenWhileSearchIsShowing() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.PresentSearch();

        harness.ViewModel.DismissSearchFocus();
        harness.ViewModel.PresentEditor(harness.ViewModel.Assignments.First(item => item.IsManual));

        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.True(harness.ViewModel.IsShowingManualEditor);
    });

    [Fact]
    public void TheCourseFilterWorksImmediatelyWhileSearchIsOpen() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;
        harness.ViewModel.PresentSearch();

        harness.ViewModel.PrepareForToolbarAction();
        harness.ViewModel.SelectCourse("PHYS200");

        Assert.False(harness.ViewModel.IsSearchFieldFocused);
        Assert.Equal("PHYS200", harness.ViewModel.SelectedCourse);
        Assert.Equal(["Physics lab"], Titles(harness.ViewModel.FilteredAssignments));
        // Using the toolbar does not close the panel.
        Assert.True(harness.ViewModel.IsSearchModeActive);
    });

    [Fact]
    public void TheCompletedAndIgnoredSwitchStillFiltersImmediately() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SidebarSelection = SidebarDestination.Upcoming;

        await harness.ViewModel.ToggleCompletedAsync(harness.Item("Maths sheet"));
        Assert.Equal(2, harness.ViewModel.FilteredAssignments.Count);

        harness.ViewModel.ShowCompletedAndIgnored = true;
        Assert.Equal(3, harness.ViewModel.FilteredAssignments.Count);
    });
}

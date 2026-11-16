namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// Opening an event, what each kind of event allows, and the keyboard
/// equivalents. Ported from the Mac app's EventInteractionTests.
/// </summary>
public sealed class EventInteractionTests
{
    private static async Task<ViewModelHarness> StartedAsync()
    {
        var harness = new ViewModelHarness();
        await harness.AddImportedAsync("Imported quiz", ViewModelHarness.Day(3), "PHYS200");
        await harness.AddManualAsync("Hand written", ViewModelHarness.Day(4));
        await harness.ViewModel.StartAsync();
        return harness;
    }

    [Fact]
    public void OpeningAManualEventPresentsTheEditor() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var manual = harness.ViewModel.Assignments.First(item => item.IsManual);

        harness.ViewModel.PresentEditor(manual);

        Assert.True(harness.ViewModel.IsShowingManualEditor);
        Assert.False(harness.ViewModel.IsShowingImportedDetails);
        Assert.Equal(manual.Id, harness.ViewModel.ManualEventDraft.EventId);
        Assert.Equal(manual.Title, harness.ViewModel.ManualEventDraft.Title);
    });

    [Fact]
    public void OpeningAnImportedEventPresentsDetailsRatherThanTheEditor() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);

        harness.ViewModel.PresentEditor(imported);

        Assert.True(harness.ViewModel.IsShowingImportedDetails);
        // Canvas owns the title and deadline, so they are not freely editable.
        Assert.False(harness.ViewModel.IsShowingManualEditor);
        Assert.Equal(imported.Id, harness.ViewModel.CurrentImportedEventDetails?.Id);
    });

    [Fact]
    public void ImportedDetailsFollowTheLatestLocalState() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);
        harness.ViewModel.PresentEditor(imported);
        Assert.False(harness.ViewModel.CurrentImportedEventDetails?.IsCompleted);

        await harness.ViewModel.ToggleCompletedAsync(imported);

        // The open details reflect the change at once.
        Assert.True(harness.ViewModel.CurrentImportedEventDetails?.IsCompleted);
    });

    [Fact]
    public void ReturnOpensTheFocusedRow() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SelectedEventId = harness.ViewModel.Assignments.First(item => item.IsManual).Id;

        harness.ViewModel.OpenSelectedEvent();

        Assert.True(harness.ViewModel.IsShowingManualEditor);
    });

    [Fact]
    public void ReturnWithoutAFocusedRowDoesNothing() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        harness.ViewModel.SelectedEventId = null;

        harness.ViewModel.OpenSelectedEvent();

        Assert.False(harness.ViewModel.IsShowingManualEditor);
        Assert.False(harness.ViewModel.IsShowingImportedDetails);
    });

    [Fact]
    public void DeleteOffersOnlyManualEvents() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var manual = harness.ViewModel.Assignments.First(item => item.IsManual);
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);

        harness.ViewModel.SelectedEventId = manual.Id;
        Assert.Equal(manual.Id, harness.ViewModel.DeletionCandidate()?.Id);

        // A Canvas assignment is not the app's to delete.
        harness.ViewModel.SelectedEventId = imported.Id;
        Assert.Null(harness.ViewModel.DeletionCandidate());

        harness.ViewModel.SelectedEventId = null;
        Assert.Null(harness.ViewModel.DeletionCandidate());
    });

    [Fact]
    public void SelectedItemTracksTheFocusedRow() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);

        harness.ViewModel.SelectedEventId = imported.Id;
        Assert.Equal(imported.Id, harness.ViewModel.SelectedItem?.Id);

        // A stale selection must not resolve to the wrong row.
        harness.ViewModel.SelectedEventId = Guid.NewGuid();
        Assert.Null(harness.ViewModel.SelectedItem);
    });

    [Fact]
    public void CompletedAndIgnoredRemainAvailableForImportedEvents() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);

        await harness.ViewModel.ToggleIgnoredAsync(imported);
        Assert.True(harness.Item("Imported quiz").IsIgnored);

        await harness.ViewModel.ToggleCompletedAsync(harness.Item("Imported quiz"));
        Assert.True(harness.Item("Imported quiz").IsCompleted);
    });

    [Fact]
    public void DeletingAnImportedEventThroughTheViewModelIsRefused() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);

        await harness.ViewModel.DeleteManualEventAsync(imported);

        // Only manual events may be deleted.
        Assert.Contains(harness.ViewModel.Assignments, item => item.Id == imported.Id);
        Assert.Contains(await harness.Repository.FetchAllAsync(), snapshot => snapshot.Id == imported.Id);
    });

    [Fact]
    public void DeletingAManualEventRemovesItFromTheListAndTheDatabase() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var manual = harness.ViewModel.Assignments.First(item => item.IsManual);

        await harness.ViewModel.DeleteManualEventAsync(manual);

        Assert.DoesNotContain(harness.ViewModel.Assignments, item => item.Id == manual.Id);
        Assert.DoesNotContain(await harness.Repository.FetchAllAsync(), snapshot => snapshot.Id == manual.Id);
    });

    [Fact]
    public void AReminderLinkOpensItsEvent() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();
        var imported = harness.ViewModel.Assignments.First(item => !item.IsManual);

        harness.ViewModel.OpenEvent(imported.Id);

        Assert.True(harness.ViewModel.IsShowingImportedDetails);
        Assert.Equal(imported.Id, harness.ViewModel.CurrentImportedEventDetails?.Id);
    });

    [Fact]
    public void AReminderLinkForAnEventThatIsGoneOpensNothing() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = await StartedAsync();

        harness.ViewModel.OpenEvent(Guid.NewGuid());

        Assert.False(harness.ViewModel.IsShowingImportedDetails);
        Assert.False(harness.ViewModel.IsShowingManualEditor);
    });
}

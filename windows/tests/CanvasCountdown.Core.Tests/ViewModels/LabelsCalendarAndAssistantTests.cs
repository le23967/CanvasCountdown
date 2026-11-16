namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// The view model's side of labels, calendar navigation and the assistant:
/// the rules themselves are tested in their own modules; these check that the
/// window's actions reach them and come back as the window expects.
/// </summary>
public sealed class LabelsCalendarAndAssistantTests
{
    // ------------------------------------------------------------------
    // Labels

    [Fact]
    public void ALabelCanBeMadeAndPutOnAnEvent() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        var essay = await harness.AddImportedAsync("Essay", ViewModelHarness.Day(3), "COMP2050");
        await harness.ViewModel.StartAsync();

        Assert.Null(harness.ViewModel.AddEventLabel("Exam week", "#8764B8"));
        var label = harness.ViewModel.EventLabels.Single(entry => entry.Name == "Exam week");
        await harness.ViewModel.SetLabelAsync(label.Id, harness.Item("Essay"));

        Assert.Equal("Exam week", harness.ViewModel.Label(harness.Item("Essay"))?.Name);
        Assert.Equal(1, harness.ViewModel.LabelUsageCount(label.Id));
        Assert.Equal(label.Id, (await harness.Repository.FetchAllAsync()).Single(snapshot => snapshot.Id == essay.Id).LabelId);
        Assert.Equal("Exam week", new SettingsStore(harness.Preferences).EventLabels.Label(label.Id)?.Name);
    });

    [Fact]
    public void TwoLabelsCannotShareAName() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        var before = harness.ViewModel.EventLabels.Count;

        Assert.Null(harness.ViewModel.AddEventLabel("Exam week", "#8764B8"));
        Assert.Equal(EventLabelNameError.Duplicate, harness.ViewModel.AddEventLabel(" exam WEEK ", "#0F6CBD"));
        // The built-in ones count too.
        Assert.Equal(EventLabelNameError.Duplicate, harness.ViewModel.AddEventLabel("Important", "#0F6CBD"));

        Assert.Equal(before + 1, harness.ViewModel.EventLabels.Count);
    });

    [Fact]
    public void RenamingAndRecolouringAreKept() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.ViewModel.AddEventLabel("Exam week", "#8764B8");
        var id = harness.ViewModel.EventLabels.Single(entry => entry.Name == "Exam week").Id;

        Assert.Null(harness.ViewModel.RenameEventLabel(id, "Urgent"));
        harness.ViewModel.RecolorEventLabel(id, "#0F6CBD");

        var stored = new SettingsStore(harness.Preferences).EventLabels.Label(id);
        Assert.Equal("Urgent", stored?.Name);
        Assert.Equal("#0F6CBD", stored?.ColorHex, ignoreCase: true);
    });

    [Fact]
    public void DeletingALabelTakesItOffEveryEvent() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddImportedAsync("Essay", ViewModelHarness.Day(3), "COMP2050");
        await harness.AddManualAsync("Reading", ViewModelHarness.Day(4));
        await harness.ViewModel.StartAsync();
        harness.ViewModel.AddEventLabel("Exam week", "#8764B8");
        var id = harness.ViewModel.EventLabels.Single(entry => entry.Name == "Exam week").Id;
        await harness.ViewModel.SetLabelAsync(id, harness.Item("Essay"));
        await harness.ViewModel.SetLabelAsync(id, harness.Item("Reading"));

        await harness.ViewModel.DeleteEventLabelAsync(id);

        Assert.DoesNotContain(harness.ViewModel.EventLabels, entry => entry.Id == id);
        Assert.All(harness.ViewModel.Assignments, item => Assert.Null(item.LabelId));
        Assert.All(await harness.Repository.FetchAllAsync(), snapshot => Assert.Null(snapshot.LabelId));
        Assert.Equal("Deleted “Exam week” and removed it from 2 events", harness.ViewModel.StatusMessage);
    });

    // ------------------------------------------------------------------
    // Calendar

    [Fact]
    public void TypingADateGoesThere() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.ViewModel.ShowCalendarSection(CalendarScale.Month);
        harness.ViewModel.PresentCalendarDateEntry();

        harness.ViewModel.CalendarDateEntry = "15 December";

        Assert.True(harness.ViewModel.CommitCalendarDateEntry());
        Assert.Equal(new DateOnly(2026, 12, 15), ViewModelHarness.Sydney.DateOf(harness.ViewModel.CalendarAnchor));
        Assert.Contains("December", harness.ViewModel.CalendarTitle, StringComparison.Ordinal);
        Assert.False(harness.ViewModel.IsShowingCalendarDateEntry);
    });

    [Fact]
    public void ADateThatCannotBeReadKeepsTheEntryOpen() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.ViewModel.ShowCalendarSection(CalendarScale.Month);
        harness.ViewModel.PresentCalendarDateEntry();
        var anchor = harness.ViewModel.CalendarAnchor;

        harness.ViewModel.CalendarDateEntry = "the thirty-ninth of Smarch";

        Assert.False(harness.ViewModel.CommitCalendarDateEntry());
        Assert.True(harness.ViewModel.IsShowingCalendarDateEntry);
        Assert.Equal(anchor, harness.ViewModel.CalendarAnchor);
    });

    [Fact]
    public void TodayComesBackFromAnywhere() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.ViewModel.ShowCalendarSection(CalendarScale.Week);

        harness.ViewModel.ShowCalendarDate(ViewModelHarness.Day(40));
        Assert.False(harness.ViewModel.IsShowingToday);

        harness.ViewModel.ShowToday();
        Assert.True(harness.ViewModel.IsShowingToday);
    });

    [Fact]
    public void TheChosenScaleIsRemembered() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        harness.ViewModel.ShowCalendarScale(CalendarScale.Year);

        Assert.Equal(CalendarScale.Year, harness.ViewModel.CalendarScale);
        Assert.Equal(CalendarScale.Year, new SettingsStore(harness.Preferences).CalendarScale);
    });

    // ------------------------------------------------------------------
    // The assistant

    private static AssistantDraftTask Draft(string title, DateTimeOffset? due, bool include = true, string? course = null)
        => new() { Title = title, DueDate = due, CourseName = course, Include = include, SourceText = "essay and quiz" };

    [Fact]
    public void DraftsAreReviewedBeforeAnythingIsSavedAndCanBeTakenBack() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.Assistant.Drafts = [Draft("Essay", ViewModelHarness.Day(3), course: "COMP2050"), Draft("Quiz", ViewModelHarness.Day(4))];

        await harness.ViewModel.DraftTaskAsync("essay friday, quiz saturday");

        // Proposed, not saved.
        Assert.Equal(2, harness.ViewModel.AssistantDrafts.Count);
        Assert.Empty(await harness.Repository.FetchAllAsync());

        harness.ViewModel.RequestSaveAssistantDrafts();
        Assert.True(harness.ViewModel.IsConfirmingAssistantSave);
        await harness.ViewModel.SaveAssistantDraftsAsync(harness.ViewModel.AssistantDrafts);

        Assert.Equal(["Essay", "Quiz"], harness.ViewModel.Assignments.Select(item => item.Title));
        Assert.All(harness.ViewModel.Assignments, item => Assert.True(item.IsManual));
        Assert.Empty(harness.ViewModel.AssistantDrafts);
        Assert.Equal("Undo Adding 2 Tasks", harness.ViewModel.UndoAdditionMenuTitle);

        // A batch that was not looked at closely can go in one step.
        await harness.ViewModel.UndoLastAdditionAsync();
        Assert.Empty(harness.ViewModel.Assignments);
    });

    [Fact]
    public void LeftOutAndUndatedDraftsAreNotSaved() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        await harness.ViewModel.SaveAssistantDraftsAsync(
        [
            Draft("Left out", ViewModelHarness.Day(3), include: false),
            Draft("No date", null),
            Draft("Kept", ViewModelHarness.Day(5)),
        ]);

        Assert.Equal(["Kept"], harness.ViewModel.Assignments.Select(item => item.Title));
        Assert.Equal("Undo Adding 1 Task", harness.ViewModel.UndoAdditionMenuTitle);
    });

    [Fact]
    public void ADraftThatReadsAsNothingSaysSoAndKeepsTheSentence() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.Assistant.Drafts = [];
        harness.ViewModel.AssistantComposerInput = "hmm";

        await harness.ViewModel.DraftTaskAsync("hmm");

        Assert.Empty(harness.ViewModel.AssistantDrafts);
        Assert.NotNull(harness.ViewModel.AssistantErrorMessage);
        Assert.Equal("hmm", harness.ViewModel.AssistantComposerInput);
    });

    [Fact]
    public void AQuestionIsAnsweredFromTheDeadlinesAndKept() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddImportedAsync("Essay", ViewModelHarness.Day(3), "COMP2050");
        await harness.ViewModel.StartAsync();
        harness.Assistant.Answer = "Essay (COMP2050) is due in 3 days.";

        await harness.ViewModel.AskAsync("What is due first?");

        Assert.Equal(["What is due first?"], harness.Assistant.Questions);
        Assert.Contains(harness.Assistant.DigestsSeen.Single(), digest => digest.Title == "Essay");
        Assert.Equal(
            ["What is due first?", "Essay (COMP2050) is due in 3 days."],
            harness.ViewModel.Conversation.Select(message => message.Text));
        // Kept across launches, as the Mac keeps it.
        Assert.Equal(2, new SettingsStore(harness.Preferences).AssistantConversation.Messages.Count);
    });
}

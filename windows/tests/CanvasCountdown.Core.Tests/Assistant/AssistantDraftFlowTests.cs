namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// Drafting a task is a conversation, not a single shot.
/// </summary>
/// <remarks>
/// Correcting one wrong row used to mean retyping the whole sentence, and the
/// reply was a fresh guess that threw away whatever had already been fixed by
/// hand. These cover the flow that replaced that: what was asked stays on
/// screen, a follow-up edits the drafts that exist, and a follow-up that fails
/// leaves the review exactly as it was.
///
/// The macOS file drives the view model. The tests here are the ones whose
/// subject is the sentence reader, the composer's role or the review itself;
/// the view model's own flow is left for its port. No test makes a real
/// network request. All fixtures are invented.
/// </remarks>
public sealed class AssistantDraftFlowTests
{
    private const string Sentence = "gym monday wednesday friday";

    /// <summary>The two workouts the stand-in model always drafts.</summary>
    private static ValueList<AssistantDraftTask> Workouts(string text = Sentence) =>
    [
        new AssistantDraftTask
        {
            Title = "Workout 1",
            DueDate = AssistantFixtures.Date(2026, 8, 3, hour: 23, minute: 59),
            SourceText = text,
        },
        new AssistantDraftTask
        {
            Title = "Workout 2",
            DueDate = AssistantFixtures.Date(2026, 8, 5, hour: 23, minute: 59),
            SourceText = text,
        },
    ];

    private static AssistantDraftReview Drafted() => AssistantDraftReview.Started(Workouts(), Sentence)!;

    // Reading the sentence, so nobody has to choose first

    /// <summary>
    /// The sentences the panel itself offers, and the one printed in its own
    /// placeholder. If automatic cannot read these, it has no business being
    /// the default.
    /// </summary>
    [Fact]
    public void TheObviousSentencesAreReadTheObviousWay()
    {
        foreach (var question in AssistantComposer.Suggestions)
        {
            Assert.True(AssistantRequestReader.ReadsAsQuestion(question), $"{question} is a question");
        }

        foreach (var question in new[]
                 {
                     "how many are due this week",
                     "should I start the essay today",
                     "what is left in 12345",
                     "tell me what is urgent",
                 })
        {
            Assert.True(AssistantRequestReader.ReadsAsQuestion(question), $"{question} is a question even without the mark");
        }

        foreach (var task in new[]
                 {
                     "essay draft due next Friday at 5pm",
                     "gym monday wednesday friday",
                     "add a workout session tuesday 5pm",
                     "remind me to email the tutor tomorrow",
                     "quiz 2 on 26/8",
                 })
        {
            Assert.False(AssistantRequestReader.ReadsAsQuestion(task), $"{task} names something to put on the list");
        }
    }

    /// <summary>
    /// Nothing said about when means nothing that could become a task with a
    /// due date, so it goes to the side that can answer it.
    /// </summary>
    [Fact]
    public void ASentenceWithNoTimeIsAnswered()
    {
        Assert.True(AssistantRequestReader.ReadsAsQuestion(""));
        Assert.True(AssistantRequestReader.ReadsAsQuestion("   "));
        Assert.True(AssistantRequestReader.ReadsAsQuestion("the essay"));
        Assert.True(
            AssistantRequestReader.ReadsAsQuestion("anything for 12345"),
            "No day, no time — the drafter would only ask for one");
    }

    /// <summary>
    /// Automatic is a default, not a verdict. Picking a side means the sentence
    /// goes there however it reads.
    /// </summary>
    [Fact]
    public void ChoosingASideOverridesTheReading()
    {
        Assert.Equal(
            AssistantComposerRole.AddTask,
            AssistantComposer.Role(AssistantComposerMode.Automatic, "gym tuesday 5pm", hasDrafts: false));

        Assert.Equal(
            AssistantComposerRole.Ask,
            AssistantComposer.Role(AssistantComposerMode.Ask, "gym tuesday 5pm", hasDrafts: false));

        Assert.Equal(
            AssistantComposerRole.AddTask,
            AssistantComposer.Role(AssistantComposerMode.AddTask, "what is most urgent?", hasDrafts: false));
    }

    /// <summary>
    /// What the panel prints under the box while it is being typed, so a wrong
    /// reading is caught before Enter rather than after.
    /// </summary>
    [Fact]
    public void TheReadingFollowsTheSentenceAsItIsTyped()
    {
        AssistantComposerMode mode = default;
        Assert.Equal(AssistantComposerMode.Automatic, mode);
        Assert.Equal(AssistantComposerRole.Ask, AssistantComposer.Role(mode, "", hasDrafts: false));

        Assert.Equal(AssistantComposerRole.AddTask, AssistantComposer.Role(mode, "gym tuesday 5pm", hasDrafts: false));

        Assert.Equal(AssistantComposerRole.Ask, AssistantComposer.Role(mode, "what is due tuesday?", hasDrafts: false));
    }

    // One box, pointed by what is on screen

    /// <summary>
    /// The rule that made the third field unnecessary: while a review is open,
    /// a sentence is about that review, whatever mode was chosen before it.
    /// </summary>
    [Fact]
    public void DraftsOnScreenTakeTheBoxOver()
    {
        var review = AssistantDraftReview.Empty;
        Assert.Equal(AssistantComposerRole.Ask, AssistantComposer.Role(AssistantComposerMode.Automatic, "", !review.IsEmpty));

        var mode = AssistantComposerMode.AddTask;
        Assert.Equal(AssistantComposerRole.AddTask, AssistantComposer.Role(mode, "", !review.IsEmpty));

        review = Drafted();

        // A sentence typed now is about the drafts, not a new request.
        Assert.Equal(AssistantComposerRole.ReviseDrafts, AssistantComposer.Role(mode, "", !review.IsEmpty));

        review = AssistantDraftReview.Empty;
        Assert.Equal(AssistantComposerRole.AddTask, AssistantComposer.Role(mode, "", !review.IsEmpty));
    }

    [Fact]
    public void AnEmptyBoxSendsNothing()
    {
        Assert.False(AssistantComposer.CanSend("   \n ", isBusy: false));
    }

    /// <summary>
    /// The tick and the label are never sent, so a follow-up cannot have meant
    /// to change them. This is the whole-flow version of that promise.
    /// </summary>
    [Fact]
    public void AFollowUpKeepsCorrectionsMadeByHand()
    {
        var review = Drafted();
        review = review.Updating(review.Drafts[0] with { Include = false });

        // Answered the way the real service answers: through the parser, from
        // JSON that carries no tick and no label.
        var drafts = review.Drafts;
        var reply = $$"""
            {"tasks":[
              {"id":"{{drafts[0].Id}}","title":"{{drafts[0].Title}}","course":"Gym","due":"2026-08-03T23:59"},
              {"id":"{{drafts[1].Id}}","title":"{{drafts[1].Title}}","course":"Gym","due":"2026-08-05T23:59"}
            ]}
            """;
        review = review.Revised(
            ChatCompletionsAssistantService.ParseRevisedDrafts(reply, drafts, AssistantFixtures.Sydney),
            "call them Gym")!;

        Assert.False(review.Drafts[0].Include, "A row unticked by hand stays unticked through a follow-up");
        Assert.True(review.Drafts[1].Include);
        Assert.Equal(new string?[] { "Gym", "Gym" }, review.Drafts.Select(draft => draft.CourseName));
    }

    // When the follow-up does not work

    /// <summary>
    /// Silence would read as "it worked" and the drafts would look wrong for no
    /// reason anyone could see.
    /// </summary>
    [Fact]
    public void AFollowUpThatChangesNothingSaysSo()
    {
        var review = Drafted();

        var revised = review.Revised(review.Drafts, "make it better");

        Assert.Null(revised);
        Assert.NotEmpty(AssistantDraftReview.NoChangeMessage);
        Assert.Equal([Sentence], review.History);
    }

    [Fact]
    public async Task AFollowUpNeedsDraftsToChange()
    {
        Assert.False(AssistantDraftReview.Empty.CanRevise(isBusy: false));

        var refusing = new RefusingHandler();
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, AssistantFixtures.Sydney, refusing);
        var revised = await service.ReviseDraftsAsync([], "put them under COMP2050", AssistantFixtures.Date(2026, 7, 28));

        Assert.Empty(revised);
        Assert.Equal(0, refusing.Calls);

        Assert.True(Drafted().CanRevise(isBusy: false));
    }

    // Fixing a row without asking

    /// <summary>
    /// Dropping one row by hand is quicker than asking for it, and it cannot be
    /// misunderstood as meaning a different row.
    /// </summary>
    [Fact]
    public void ARowCanBeDroppedWithoutTheModel()
    {
        var review = Drafted();
        var dropped = review.Drafts[0].Id;

        review = review.Removing(dropped);

        Assert.Single(review.Drafts);
        Assert.DoesNotContain(review.Drafts, draft => draft.Id == dropped);
    }
}

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// Decisions the macOS panel and view model made about the assistant's types,
/// now worked out in Core so every view of the panel says the same thing. All
/// fixtures are invented.
/// </summary>
public sealed class AssistantComposerTests
{
    [Fact]
    public void TheSuggestionsAreTheOnesThePanelOffers()
    {
        Assert.Equal(["What is most urgent?", "How many are due this week?", "What should I start today?"], AssistantComposer.Suggestions);
        Assert.Equal("Add a task in your own words", AssistantComposer.AddTaskSuggestion);
    }

    [Fact]
    public void ThePlaceholderSaysWhatTheBoxIsFor()
    {
        Assert.Equal("Ask about your deadlines, or describe a task", AssistantComposer.Placeholder(AssistantComposerMode.Automatic, AssistantComposerRole.Ask));
        Assert.Equal("Ask about your deadlines, or describe a task", AssistantComposer.Placeholder(AssistantComposerMode.Automatic, AssistantComposerRole.AddTask));
        Assert.Equal("Ask about your deadlines", AssistantComposer.Placeholder(AssistantComposerMode.Ask, AssistantComposerRole.Ask));
        Assert.Equal("Describe a task — essay draft due next Friday at 5pm", AssistantComposer.Placeholder(AssistantComposerMode.AddTask, AssistantComposerRole.AddTask));
        Assert.Equal("Change these — “put them under COMP2050”", AssistantComposer.Placeholder(AssistantComposerMode.Ask, AssistantComposerRole.ReviseDrafts));
    }

    [Fact]
    public void TheHintSaysWhatAutomaticReadTheSentenceAs()
    {
        static string? Hint(AssistantComposerMode mode, string input, int drafts = 0)
            => AssistantComposer.Hint(mode, AssistantComposer.Role(mode, input, drafts > 0), input, drafts);

        Assert.Equal("will add a task", Hint(AssistantComposerMode.Automatic, "gym tuesday 5pm"));
        Assert.Equal("will answer", Hint(AssistantComposerMode.Automatic, "what is due tuesday?"));
        Assert.Null(Hint(AssistantComposerMode.Automatic, "  "));
        Assert.Null(Hint(AssistantComposerMode.Ask, "gym tuesday 5pm"));
        Assert.Null(Hint(AssistantComposerMode.AddTask, "gym tuesday 5pm"));
        Assert.Equal("Changing the draft above · ticks and labels kept", Hint(AssistantComposerMode.Automatic, "", drafts: 1));
        Assert.Equal("Changing the 3 drafts above · ticks and labels kept", Hint(AssistantComposerMode.Ask, "make it 6pm", drafts: 3));
    }

    [Fact]
    public void AScreenReaderHearsTheReadingInWords()
    {
        Assert.Equal(
            "Read as a task to add",
            AssistantComposer.AutomaticReadingAccessibilityLabel(AssistantComposerMode.Automatic, AssistantComposerRole.AddTask, "gym 5pm"));
        Assert.Equal(
            "Read as a question to answer",
            AssistantComposer.AutomaticReadingAccessibilityLabel(AssistantComposerMode.Automatic, AssistantComposerRole.Ask, "what now"));
        Assert.Null(AssistantComposer.AutomaticReadingAccessibilityLabel(AssistantComposerMode.Ask, AssistantComposerRole.Ask, "what now"));
        Assert.Equal("Composer mode, Add task", AssistantComposer.ModeAccessibilityLabel(AssistantComposerMode.AddTask));
    }

    [Fact]
    public void TheBoxAndItsSendControlSayWhatTheyWillDo()
    {
        Assert.Equal("Ask the assistant", AssistantComposer.FieldDescription(AssistantComposerRole.Ask));
        Assert.Equal("Describe a task", AssistantComposer.FieldDescription(AssistantComposerRole.AddTask));
        Assert.Equal("Change the drafts above", AssistantComposer.FieldDescription(AssistantComposerRole.ReviseDrafts));
        Assert.Equal("Send this question", AssistantComposer.SendDescription(AssistantComposerRole.Ask));
        Assert.Equal("Read tasks out of this sentence", AssistantComposer.SendDescription(AssistantComposerRole.AddTask));
        Assert.Equal("Rewrite the drafts above using this sentence", AssistantComposer.SendDescription(AssistantComposerRole.ReviseDrafts));
    }

    [Fact]
    public void TheModesAreNamedInTwoWordsAtMost()
    {
        Assert.Equal(["Automatic", "Ask", "Add task"], Enum.GetValues<AssistantComposerMode>().Select(mode => mode.Title()));
    }

    [Fact]
    public void NothingIsSentWhileTheAssistantIsBusy()
    {
        Assert.True(AssistantComposer.CanSend("what is most urgent?", isBusy: false));
        Assert.False(AssistantComposer.CanSend("what is most urgent?", isBusy: true));
        Assert.True(AssistantComposer.HasTypedText(" x "));
        Assert.False(AssistantComposer.HasTypedText(" \n\t "));
    }
}

public sealed class AssistantDraftReviewTests
{
    private static readonly DateTimeOffset Now = AssistantFixtures.Date(2026, 8, 3, hour: 10);

    private static AssistantDraftTask Draft(string title, string? course = null, bool dated = true, bool include = true)
        => new()
        {
            Include = include,
            Title = title,
            CourseName = course,
            DueDate = dated ? AssistantFixtures.Date(2026, 8, 7, hour: 17) : null,
            SourceText = "invented",
        };

    [Fact]
    public void AReviewRemembersTheSentenceThatStartedIt()
    {
        var review = AssistantDraftReview.Started([Draft("Essay")], "  essay due friday 5pm  ")!;

        Assert.Equal(["essay due friday 5pm"], review.History);
        Assert.Equal(["Essay"], review.Drafts.Select(draft => draft.Title));
    }

    [Fact]
    public void ASentenceThatProducesNothingOpensNoReview()
    {
        Assert.Null(AssistantDraftReview.Started([], "something vague"));
        Assert.Equal("Nothing could be read from that. Try naming the task and when it is due.", AssistantDraftReview.NothingReadMessage);
        Assert.True(AssistantDraftReview.Empty.IsEmpty);
        Assert.Empty(AssistantDraftReview.Empty.History);
    }

    [Fact]
    public void ARevisionReplacesTheDraftsAndAddsItsSentence()
    {
        var review = AssistantDraftReview.Started([Draft("Workout 1"), Draft("Workout 2")], "gym monday wednesday")!;
        var revised = review.Drafts.Select(draft => draft with { CourseName = "Gym" }).ToValueList();

        var after = review.Revised(revised, " call them Gym ")!;

        Assert.Equal(revised, after.Drafts);
        Assert.Equal(["gym monday wednesday", "call them Gym"], after.History);
        Assert.Equal("That did not change anything. Try naming what should be different.", AssistantDraftReview.NoChangeMessage);
    }

    [Fact]
    public void ACorrectionByHandChangesOnlyTheDraftItIsFor()
    {
        var review = AssistantDraftReview.Started([Draft("One"), Draft("Two"), Draft("Three")], "three things")!;

        var corrected = review.Updating(review.Drafts[1] with { Title = "Two, fixed" });
        var stranger = review.Updating(Draft("Not on screen"));

        Assert.Equal(["One", "Two, fixed", "Three"], corrected.Drafts.Select(draft => draft.Title));
        Assert.Equal(review, stranger);
        Assert.Equal(review, review.Removing(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(true, true, "Essay", true)]
    [InlineData(false, true, "Essay", false)]
    [InlineData(true, false, "Essay", false)]
    [InlineData(true, true, "   ", false)]
    public void WhatIsSavedIsTickedNamedAndDated(bool include, bool dated, string title, bool saved)
    {
        var draft = Draft(title, dated: dated, include: include);
        var review = AssistantDraftReview.Started([draft], "x")!;

        Assert.Equal(saved, draft.WillBeSaved);
        Assert.Equal(saved, review.HasSaveableDrafts);
        Assert.Equal(saved ? 1 : 0, review.Summary("").SavingCount);
    }

    [Fact]
    public void AReviseWaitsForTheLastOneToFinish()
    {
        var review = AssistantDraftReview.Started([Draft("Essay")], "x")!;

        Assert.True(review.CanRevise(isBusy: false));
        Assert.False(review.CanRevise(isBusy: true));
    }

    [Fact]
    public void ACourseFieldLeftBlankMeansNoCourse()
    {
        var draft = Draft("Essay", course: "DESN1500");

        Assert.Null(draft.WithCourseText("   ").CourseName);
        Assert.Equal(" COMP2050 ", draft.WithCourseText(" COMP2050 ").CourseName);
    }

    /// <summary>
    /// The model gave no date and the app will not invent one; asked to set
    /// one, it starts the picker at this time tomorrow — by the calendar, so
    /// the night the clocks change does not move it an hour.
    /// </summary>
    [Fact]
    public void SettingADateStartsAtThisTimeTomorrow()
    {
        var now = AssistantFixtures.Date(2026, 10, 3, hour: 10);

        var dated = Draft("Essay", dated: false).WithPlaceholderDate(now, AssistantFixtures.Sydney);

        Assert.Equal(new DateTime(2026, 10, 4, 10, 0, 0), AssistantFixtures.Sydney.ToLocal(dated.DueDate!.Value));
        Assert.Equal(TimeSpan.FromHours(23), dated.DueDate!.Value - now);
    }

    [Fact]
    public async Task TheUndoWordingNamesWhatWasAdded()
    {
        var one = await AssistantDraftReview.SaveAsync(new RecordingRepository(), [Draft("Essay")], Now);
        var two = await AssistantDraftReview.SaveAsync(new RecordingRepository(), [Draft("Essay"), Draft("Quiz")], Now);

        Assert.Equal("1 task added", one.UndoMessage);
        Assert.Equal("Undo Adding 1 Task", one.UndoMenuTitle);
        Assert.Equal("2 tasks added", two.UndoMessage);
        Assert.Equal("Undo Adding 2 Tasks", two.UndoMenuTitle);
        Assert.True(two.OffersUndo);
    }

    /// <summary>
    /// As on macOS: the event is written before its label, so one whose label
    /// could not be set is still there, the failure is reported, and it is
    /// not among the events an undo takes back.
    /// </summary>
    [Fact]
    public async Task ALabelThatCannotBeSetIsReported()
    {
        var repository = new RecordingRepository { FailsToLabel = true };

        var outcome = await AssistantDraftReview.SaveAsync(
            repository,
            [Draft("Essay") with { LabelId = Guid.NewGuid() }],
            Now);

        Assert.Single(outcome.Errors);
        Assert.Empty(outcome.SavedIds);
        Assert.Single(repository.Events);
    }

    [Fact]
    public void TheSummaryNamesEachCourseOnceInTheOrderTheyAppear()
    {
        var summary = AssistantSaveSummary.For(
            [
                Draft("One", "COMP2050"),
                Draft("Two", " STAT1010 "),
                Draft("Three", "COMP2050"),
                Draft("Four"),
                Draft("Five", "Café Studies"),
                Draft("Six", "Café Studies"),
                Draft("Seven", "DESN1500", include: false),
            ],
            "");

        Assert.Equal(["COMP2050", "STAT1010", "Café Studies"], summary.Courses);
        Assert.Equal(1, summary.WithoutCourseCount);
        Assert.Equal(6, summary.SavingCount);
        Assert.Equal(1, summary.SkippedCount);
    }

    [Fact]
    public void TheDetailLinesComeInTheOrderTheyMatter()
    {
        var summary = new AssistantSaveSummary
        {
            SavingCount = 3,
            SkippedCount = 2,
            Courses = ["COMP2050", "STAT1010"],
            WithoutCourseCount = 1,
            Earliest = AssistantFixtures.Date(2026, 8, 8),
            Latest = AssistantFixtures.Date(2026, 8, 8, hour: 23),
            HasUnappliedChange = true,
        };

        Assert.Equal(
            [
                "You typed a change that has not been applied yet. Saving now leaves it out.",
                "2 drafts are not ticked or have no date, and will not be added.",
                "Under COMP2050, STAT1010, and 1 without a course.",
                "Due Aug 8.",
                "They are added as manual events, and can be undone.",
            ],
            summary.DetailLines(AssistantFixtures.Sydney, AssistantFixtures.EnglishUS));
    }

    [Theory]
    [InlineData(new string[0], 1, "Without a course.")]
    [InlineData(new string[0], 3, "All 3 without a course.")]
    [InlineData(new[] { "COMP2050" }, 0, "Under COMP2050.")]
    [InlineData(new[] { "COMP2050" }, 1, "Under COMP2050, and 1 without a course.")]
    [InlineData(new[] { "COMP2050", "STAT1010" }, 0, "Under COMP2050, STAT1010.")]
    public void TheCourseLineSaysWhereTheyGo(string[] courses, int without, string expected)
    {
        var summary = new AssistantSaveSummary
        {
            SavingCount = courses.Length + without,
            SkippedCount = 1,
            Courses = courses.ToValueList(),
            WithoutCourseCount = without,
            Earliest = null,
            Latest = null,
            HasUnappliedChange = false,
        };

        var lines = summary.DetailLines(AssistantFixtures.Sydney, AssistantFixtures.EnglishUS);

        Assert.Equal("1 draft is not ticked or has no date, and will not be added.", lines[0]);
        Assert.Equal(expected, lines[1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("Due ", StringComparison.Ordinal));
    }
}

public sealed class AssistantProfileLibraryTests
{
    [Fact]
    public void ARenameIsTrimmedAndMayKeepItsOwnName()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq), out var id);

        library = library.Rename(id!.Value, "  Groq at home  ", out var error);
        Assert.Null(error);
        Assert.Equal("Groq at home", library.Profile(id)!.Name);

        library = library.Rename(id.Value, "GROQ AT HOME", out error);
        Assert.Null(error);
        Assert.Equal("GROQ AT HOME", library.Profile(id)!.Name);
    }

    [Fact]
    public void RenamingAModelThatIsNotThereChangesNothing()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq));

        var renamed = library.Rename(Guid.NewGuid(), "Anything", out var error);

        Assert.Null(error);
        Assert.Equal(library, renamed);
    }

    [Fact]
    public void NamesHaveALimitAndTheErrorsSaySo()
    {
        var library = AssistantProfileLibrary.Empty;

        Assert.Null(library.Validate(new string('n', 40)));
        Assert.Equal(AssistantProfileNameError.TooLong, library.Validate(new string('n', 41)));
        Assert.Equal(AssistantProfileNameError.Empty, library.Validate(" \t "));
        Assert.Equal("Give the model a name.", AssistantProfileNameError.Empty.Message());
        Assert.Equal("Names can be up to 40 characters.", AssistantProfileNameError.TooLong.Message());
        Assert.Equal("There is already a model with that name.", AssistantProfileNameError.Duplicate.Message());
    }

    [Fact]
    public void ABlankNameStartsFromModel()
    {
        var library = AssistantProfileLibrary.Empty;

        Assert.Equal("Model", library.AvailableName("   "));

        library = library.Add(AssistantProfile.Blank() with { Name = "Model" });

        Assert.Equal("Model 2", library.AvailableName(""));
    }

    [Fact]
    public void AModelWithNoNameIsNotKept()
    {
        // As on macOS, a nameless profile is refused before a free name is
        // looked for, rather than being saved as "Model".
        var library = AssistantProfileLibrary.Empty.Add(AssistantProfile.Blank() with { Name = "  " }, out var id);

        Assert.Null(id);
        Assert.Empty(library.Profiles);
    }

    [Fact]
    public void ACopyIsNamedAsACopy()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.DeepSeek), out var id);

        library = library.Duplicate(id!.Value, out var first);
        library = library.Duplicate(id.Value, out var second);

        Assert.Equal("DeepSeek Copy", library.Profile(first)!.Name);
        Assert.Equal("DeepSeek Copy 2", library.Profile(second)!.Name);
        Assert.Equal(library, library.Duplicate(Guid.NewGuid(), out var none));
        Assert.Null(none);
    }

    [Fact]
    public void ABlankModelStartsLocal()
    {
        var blank = AssistantProfile.Blank();

        Assert.Equal("New Model", blank.Name);
        Assert.Equal(AssistantProvider.Local, blank.Provider);
        Assert.True(blank.StaysOnThisMachine);
        Assert.Equal(AssistantSettings.Defaults.Model, blank.Model);
    }

    [Fact]
    public void AKnownServiceIsLocalOnlyWhenItIsOnThisMachine()
    {
        Assert.Equal(AssistantProvider.Local, new AssistantProfile(AssistantService.Ollama).Provider);
        Assert.All(AssistantService.CloudServices, service => Assert.Equal(AssistantProvider.Cloud, new AssistantProfile(service).Provider));
        Assert.Equal("claude-haiku-4-5", new AssistantProfile(AssistantService.Anthropic).Model);
    }

    [Fact]
    public void UsingAServiceReadsItsAddress()
    {
        var hosted = AssistantSettings.Defaults.UsingService(AssistantService.Qwen);
        var local = hosted.UsingService(AssistantService.Ollama);

        Assert.Equal(AssistantProvider.Cloud, hosted.Provider);
        Assert.Equal(AssistantService.Qwen.BaseUrl, hosted.BaseUrl);
        Assert.Equal("qwen-plus", hosted.Model);
        Assert.Equal(AssistantProvider.Local, local.Provider);
        Assert.Equal("llama3.1:8b", local.Model);

        var unknown = hosted.UsingService(AssistantService.Qwen with { Models = [] });
        Assert.Equal("qwen-plus", unknown.Model);
    }

    [Fact]
    public void SelectingTheModelInUseOrOneThatIsGoneChangesNothing()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq), out var id);
        var settings = AssistantSettings.Applying(library.Profile(id)!, AssistantSettings.Defaults);

        Assert.Null(library.Selecting(id!.Value, settings));
        Assert.Null(library.Selecting(Guid.NewGuid(), settings));
    }

    [Fact]
    public void TheModelChipNamesTheModelInUse()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq), out var id);

        Assert.Equal("AI model, using Groq", library.ModelDescription(id));
        Assert.Equal("Groq", library.ModelChipTitle(id));
        Assert.Equal("Choose which AI model to use", library.ModelDescription(null));
        Assert.Equal("Model", library.ModelChipTitle(Guid.NewGuid()));
    }

    [Fact]
    public void WhereToGetAKeyIsOfferedOnlyForAKnownHostedService()
    {
        Assert.Equal(AssistantService.Groq.KeysUrl, (AssistantFixtures.EnabledCloud with { BaseUrl = AssistantService.Groq.BaseUrl }).KeysUrl);
        Assert.Null((AssistantFixtures.EnabledCloud with { BaseUrl = "https://ai.example.edu/v1" }).KeysUrl);
        Assert.Null(AssistantSettings.Defaults.KeysUrl);
    }

    [Fact]
    public void AModelWithNoModelNameSaysSo()
    {
        var profile = AssistantProfile.Blank() with { Model = "  " };

        Assert.Equal("No model set", profile.Subtitle);
        Assert.Equal("llama3.1:8b", (profile with { Model = " llama3.1:8b " }).Subtitle);
    }

    [Fact]
    public void SyncingWhenNothingDiffersKeepsTheLibraryAsItIs()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq), out var id);
        var settings = AssistantSettings.Applying(library.Profile(id)!, AssistantSettings.Defaults);

        Assert.Same(library, library.Syncing(settings));
        Assert.Same(library, library.Syncing(settings with { ActiveProfileId = null, Model = "other" }));
        Assert.Same(library, library.Syncing(settings with { ActiveProfileId = Guid.NewGuid(), Model = "other" }));
    }

    [Fact]
    public async Task DeletingTheLastModelLeavesNoneSelected()
    {
        var keys = new AssistantKeyStore(new InMemorySecretStore());
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq), out var id);
        var settings = AssistantSettings.Applying(library.Profile(id)!, AssistantSettings.Defaults);

        (library, settings) = await library.DeletingAsync(id!.Value, settings, keys);

        Assert.Empty(library.Profiles);
        Assert.Null(settings.ActiveProfileId);
    }

    [Fact]
    public async Task DeletingAModelNotInUseKeepsTheSelection()
    {
        var keys = new AssistantKeyStore(new InMemorySecretStore());
        var library = AssistantProfileLibrary.Empty
            .Add(new AssistantProfile(AssistantService.Groq), out var inUse)
            .Add(new AssistantProfile(AssistantService.OpenAI), out var other);
        var settings = AssistantSettings.Applying(library.Profile(inUse)!, AssistantSettings.Defaults);

        var (after, kept) = await library.DeletingAsync(other!.Value, settings, keys);

        Assert.Equal(settings, kept);
        Assert.Equal([inUse!.Value], after.Profiles.Select(profile => profile.Id));
    }

    [Fact]
    public void UpdatingAModelThatIsNotThereSaysSo()
    {
        var library = AssistantProfileLibrary.Empty;

        Assert.Equal(library, library.Update(Guid.NewGuid(), AssistantProvider.Cloud, "https://ai.example.edu/v1", "m", out var updated));
        Assert.False(updated);
    }
}

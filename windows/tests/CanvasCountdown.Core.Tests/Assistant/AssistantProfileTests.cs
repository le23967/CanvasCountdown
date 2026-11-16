namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// Saved models, what the assistant is allowed to add, and what an addition
/// leaves to take back.
/// </summary>
/// <remarks>
/// No test makes a real network request or touches the real Credential
/// Manager. Every fixture is invented. The macOS tests that drove the view
/// model here are ported against the Core decision the view model calls.
/// </remarks>
public sealed class AssistantProfileTests
{
    private static DateTimeOffset Date(int year, int month, int day) => AssistantFixtures.Date(year, month, day, hour: 17);

    private static readonly DateTimeOffset Now = AssistantFixtures.Date(2026, 8, 1, hour: 10);

    private static AssistantDraftTask Draft(string title, int day)
        => new() { Title = title, DueDate = Date(2026, 8, day), SourceText = "invented" };

    private static AssistantKeyStore Keys() => new(new InMemorySecretStore());

    /// <summary>What the first launch after saved models arrived leaves behind.</summary>
    private static async Task<(AssistantProfileLibrary Library, AssistantSettings Settings)> Started(
        AssistantSettings? settings = null,
        AssistantKeyStore? keys = null)
    {
        var migrated = await AssistantProfileLibrary.MigrateIfNeededAsync(
            AssistantProfileLibrary.Empty,
            settings ?? AssistantSettings.Defaults,
            keys ?? Keys());
        Assert.NotNull(migrated);
        return migrated.Value;
    }

    /// <summary>What adding a model from Settings does: add it, then select it.</summary>
    private static (AssistantProfileLibrary Library, AssistantSettings Settings, Guid Id) AddingAndSelecting(
        AssistantProfileLibrary library,
        AssistantSettings settings,
        AssistantProfile profile)
    {
        library = library.Add(profile, out var id);
        Assert.NotNull(id);
        return (library, AssistantSettings.Applying(library.Profile(id)!, settings), id.Value);
    }

    // The library

    [Fact]
    public void NamesAreUniqueWhateverTheirCase()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq));

        Assert.Equal(AssistantProfileNameError.Duplicate, library.Validate("groq"));
        Assert.Null(library.Validate("Groq at home"));
    }

    [Fact]
    public void AddingTheSameServiceTwiceNumbersTheSecond()
    {
        var library = AssistantProfileLibrary.Empty
            .Add(new AssistantProfile(AssistantService.Groq))
            .Add(new AssistantProfile(AssistantService.Groq));

        Assert.Equal(["Groq", "Groq 2"], library.Profiles.Select(profile => profile.Name));
    }

    [Fact]
    public void DuplicatingCopiesTheSettingsButNotTheName()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.OpenAI), out var id);
        Assert.NotNull(id);

        library = library.Duplicate(id.Value, out var copyId);
        Assert.NotNull(copyId);
        var original = library.Profile(id)!;
        var copy = library.Profile(copyId)!;

        Assert.Equal(original.BaseUrl, copy.BaseUrl);
        Assert.Equal(original.Model, copy.Model);
        Assert.NotEqual(original.Name, copy.Name);
        Assert.NotEqual(original.Id, copy.Id);
    }

    [Fact]
    public void RenamingToATakenNameIsRefusedAndChangesNothing()
    {
        var library = AssistantProfileLibrary.Empty
            .Add(new AssistantProfile(AssistantService.Groq))
            .Add(new AssistantProfile(AssistantService.OpenAI), out var secondId);
        Assert.NotNull(secondId);

        var renamed = library.Rename(secondId.Value, "Groq", out var error);

        Assert.Equal(AssistantProfileNameError.Duplicate, error);
        Assert.Equal("OpenAI", renamed.Profile(secondId)?.Name);
    }

    [Fact]
    public void AProfileWithNoUsableAddressIsNotKept()
    {
        var library = AssistantProfileLibrary.Empty.Add(
            new AssistantProfile
            {
                Name = "Broken",
                Provider = AssistantProvider.Cloud,
                BaseUrl = "not a url at all",
                Model = "x",
            },
            out var id);

        Assert.Null(id);
        Assert.Empty(library.Profiles);
    }

    [Fact]
    public void TheLibraryStopsAtItsLimit()
    {
        var library = AssistantProfileLibrary.Empty;
        for (var index = 0; index < AssistantProfileLibrary.MaximumCount + 3; index++)
        {
            library = library.Add(new AssistantProfile
            {
                Name = $"Model {index}",
                Provider = AssistantProvider.Cloud,
                BaseUrl = "https://example.com/v1",
                Model = "m",
            });
        }

        Assert.Equal(AssistantProfileLibrary.MaximumCount, library.Profiles.Count);
        Assert.True(library.IsFull);
    }

    /// <summary>
    /// The same rule the countdown presets and the labels follow: a library
    /// written by a newer build is shown as empty rather than half-read and
    /// rewritten.
    /// </summary>
    [Fact]
    public void ALibraryFromANewerBuildIsLeftAlone()
    {
        const string stored = """{"version":99,"profiles":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","name":"Future","provider":"cloud","baseURL":"https://example.com/v1","model":"x"}]}""";

        var decoded = JsonDefaults.Deserialize<AssistantProfileLibrary>(stored)!;

        Assert.Empty(decoded.Profiles);
    }

    [Fact]
    public void PrivacyIsJudgedByTheAddressNotTheName()
    {
        var liar = new AssistantProfile
        {
            Name = "On this PC",
            Provider = AssistantProvider.Local,
            BaseUrl = "https://example.com/v1",
            Model = "x",
        };

        Assert.False(liar.StaysOnThisMachine, "Calling it local does not keep the data here");
        Assert.True(new AssistantProfile(AssistantService.Ollama).StaysOnThisMachine);
    }

    // Upgrading from the single configuration

    [Fact]
    public void TheExistingConfigurationBecomesTheFirstSavedModel()
    {
        var settings = AssistantSettings.Defaults with
        {
            Provider = AssistantProvider.Cloud,
            BaseUrl = AssistantService.Groq.BaseUrl,
            Model = "llama-3.3-70b-versatile",
        };

        var library = AssistantProfileLibrary.Migrated(settings);

        Assert.Single(library.Profiles);
        Assert.Equal("Groq", library.Profiles[0].Name);
        Assert.Equal(settings.BaseUrl, library.Profiles[0].BaseUrl);
        Assert.Equal(settings.Model, library.Profiles[0].Model);
        Assert.Equal(AssistantProvider.Cloud, library.Profiles[0].Provider);
    }

    [Fact]
    public void AnUnrecognisedAddressStillMigratesUnderTheProviderName()
    {
        var settings = AssistantSettings.Defaults with
        {
            Provider = AssistantProvider.Cloud,
            BaseUrl = "https://ai.example.edu/v1",
            Model = "campus-model",
        };

        var library = AssistantProfileLibrary.Migrated(settings);

        Assert.Single(library.Profiles);
        Assert.Equal("AI (Cloud)", library.Profiles[0].Name);
        Assert.Equal("https://ai.example.edu/v1", library.Profiles[0].BaseUrl);
    }

    [Fact]
    public async Task TheFirstLaunchNamesTheExistingSetupAndSelectsIt()
    {
        var (library, settings) = await Started(
            AssistantSettings.Applying(AssistantProvider.Cloud, AssistantSettings.Defaults));

        Assert.Single(library.Profiles);
        // An upgrade must not leave the assistant pointing at nothing.
        Assert.Equal(library.Profiles[0].Id, library.Profile(settings.ActiveProfileId)?.Id);
    }

    [Fact]
    public async Task TheMigrationRunsOnceAndDoesNotKeepAddingModels()
    {
        var keys = Keys();
        var (library, settings) = await Started(keys: keys);
        var firstId = library.Profiles[0].Id;

        var second = await AssistantProfileLibrary.MigrateIfNeededAsync(library, settings, keys);

        Assert.Null(second);
        Assert.Single(library.Profiles);
        Assert.Equal(firstId, library.Profiles[0].Id);
    }

    // Switching between them

    [Fact]
    public async Task SwitchingModelCarriesItsAddressAndModelName()
    {
        var (library, settings) = await Started();
        (library, settings, var groqId) = AddingAndSelecting(library, settings, new AssistantProfile(AssistantService.Groq));
        var localId = library.Profiles.First(profile => profile.Id != groqId).Id;

        settings = library.Selecting(localId, settings)!;

        Assert.Equal(localId, settings.ActiveProfileId);
        Assert.True(settings.StaysOnThisMachine);

        settings = library.Selecting(groqId, settings)!;

        Assert.Equal(AssistantService.Groq.BaseUrl, settings.BaseUrl);
        Assert.False(settings.StaysOnThisMachine);
    }

    /// <summary>
    /// There is no separate save step, so the fields and the saved model must
    /// never be allowed to disagree.
    /// </summary>
    [Fact]
    public async Task EditingTheAddressEditsTheModelItBelongsTo()
    {
        var (library, settings) = await Started();
        var activeId = settings.ActiveProfileId;
        Assert.NotNull(activeId);

        var form = settings with { BaseUrl = "http://localhost:1234/v1", Model = "something-else" };
        library = library.Syncing(form);

        var stored = library.Profiles.First(profile => profile.Id == activeId);
        Assert.Equal("http://localhost:1234/v1", stored.BaseUrl);
        Assert.Equal("something-else", stored.Model);
    }

    [Fact]
    public async Task DeletingTheModelInUseMovesToAnotherRatherThanNone()
    {
        var keys = Keys();
        var (library, settings) = await Started(keys: keys);
        (library, settings, var addedId) = AddingAndSelecting(library, settings, new AssistantProfile(AssistantService.Groq));
        Assert.Equal(addedId, settings.ActiveProfileId);

        (library, settings) = await library.DeletingAsync(addedId, settings, keys);

        Assert.Single(library.Profiles);
        // The assistant must never be left pointing at a deleted model.
        Assert.Equal(library.Profiles[0].Id, settings.ActiveProfileId);
    }

    [Fact]
    public async Task TheAssistantOffersAPickerOnlyOnceThereIsSomethingToSwitchTo()
    {
        var (library, settings) = await Started();

        Assert.False(library.ShowsModelPicker(settings), "One model is just the model; a picker would be noise");

        (library, settings, _) = AddingAndSelecting(library, settings, new AssistantProfile(AssistantService.Groq));

        Assert.True(library.ShowsModelPicker(settings));
    }

    [Fact]
    public async Task TurningTheAssistantOffTakesThePickerWithIt()
    {
        var (library, settings) = await Started();
        (library, settings, _) = AddingAndSelecting(library, settings, new AssistantProfile(AssistantService.Groq));
        Assert.True(library.ShowsModelPicker(settings));

        var form = settings with { IsEnabled = false };

        Assert.False(library.ShowsModelPicker(form));
    }

    // What the assistant adds, and what it leaves to take back

    [Fact]
    public async Task ADraftKeepsItsCourseAndLabelWhenItIsSaved()
    {
        var repository = new RecordingRepository();
        var label = Guid.NewGuid();

        await AssistantDraftReview.SaveAsync(
            repository,
            [
                new AssistantDraftTask
                {
                    Title = "  Essay draft  ",
                    CourseName = "  DESN1500 Studio Practice  ",
                    LabelId = label,
                    DueDate = Date(2026, 8, 20),
                    SourceText = "essay draft due next friday",
                },
            ],
            Now);

        var saved = Assert.Single(repository.Events);
        Assert.Equal("Essay draft", saved.Title);
        Assert.Equal(
            "DESN1500 Studio Practice",
            AssignmentListItem.FromSnapshot(saved, treatsMidnightAsEndOfDay: false, AssistantFixtures.Sydney).NormalizedCourseName);
        Assert.Equal(label, saved.LabelId);
    }

    [Fact]
    public async Task DraftsTheUserUncheckedAreNeverSaved()
    {
        var repository = new RecordingRepository();

        await AssistantDraftReview.SaveAsync(
            repository,
            [
                new AssistantDraftTask { Include = false, Title = "Not this one", DueDate = Date(2026, 8, 20), SourceText = "x" },
                new AssistantDraftTask { Title = "This one", DueDate = Date(2026, 8, 21), SourceText = "x" },
            ],
            Now);

        Assert.Equal(["This one"], repository.Events.Select(snapshot => snapshot.Title));
    }

    [Fact]
    public async Task ADraftWithNoDateIsNeverSaved()
    {
        var repository = new RecordingRepository();

        var outcome = await AssistantDraftReview.SaveAsync(
            repository,
            [new AssistantDraftTask { Title = "No date given", DueDate = null, SourceText = "x" }],
            Now);

        Assert.Empty(repository.Events);
        Assert.False(outcome.OffersUndo, "Nothing was added, so there is nothing to offer to undo");
    }

    [Fact]
    public async Task SavingABatchOffersToTakeAllOfItBack()
    {
        var repository = new RecordingRepository();

        var outcome = await AssistantDraftReview.SaveAsync(
            repository,
            [Draft("One", day: 20), Draft("Two", day: 21), Draft("Three", day: 22)],
            Now);

        Assert.Equal(3, outcome.SavedIds.Count);
        Assert.Equal("3 tasks added", outcome.UndoMessage);
    }
}

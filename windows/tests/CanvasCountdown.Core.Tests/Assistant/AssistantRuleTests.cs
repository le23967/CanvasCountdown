using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// The rules about the assistant that a rewrite gets wrong. Each test here
/// fails if its rule is broken. All fixtures are invented.
/// </summary>
public sealed class AssistantRuleTests : IDisposable
{
    private static AppCalendar Calendar => AssistantFixtures.Sydney;

    private static readonly DateTimeOffset Now = AssistantFixtures.Date(2026, 8, 3, hour: 10);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "cc-assistant-rules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A stored provider the decoder does not recognise reads as local, never cloud.

    [Theory]
    [InlineData("something-new")]
    [InlineData("Cloud")]
    [InlineData("CLOUD")]
    [InlineData("Groq")]
    [InlineData("hosted")]
    [InlineData(" cloud")]
    [InlineData("")]
    public void AnUnrecognisedStoredProviderReadsAsLocal(string stored)
    {
        var json = $$"""{"isEnabled":true,"provider":{{JsonSerializer.Serialize(stored)}},"baseUrl":"https://ai.example.edu/v1","model":"campus-model","presentation":"sidebar"}""";

        var decoded = JsonDefaults.Deserialize<AssistantSettings>(json)!;

        Assert.Equal(AssistantProvider.Local, decoded.Provider);
        // The rest is kept: the provider was read as local, rather than the
        // whole value falling back.
        Assert.Equal("https://ai.example.edu/v1", decoded.BaseUrl);
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("groq")]
    public void OnlyTheTwoCloudSpellingsReadAsCloud(string stored)
    {
        var json = $$"""{"isEnabled":true,"provider":"{{stored}}","baseUrl":"https://ai.example.edu/v1","model":"m","presentation":"sidebar"}""";

        Assert.Equal(AssistantProvider.Cloud, JsonDefaults.Deserialize<AssistantSettings>(json)!.Provider);
    }

    [Fact]
    public void ASavedModelWithAnUnrecognisedProviderReadsAsLocal()
    {
        const string stored = """{"version":1,"profiles":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","name":"Campus","provider":"Cloud","baseURL":"https://ai.example.edu/v1","model":"m"}]}""";

        var decoded = JsonDefaults.Deserialize<AssistantProfileLibrary>(stored)!;

        Assert.Equal(AssistantProvider.Local, Assert.Single(decoded.Profiles).Provider);
    }

    [Fact]
    public void AProviderThatIsNotTextLeavesTheSettingsAtTheirLocalDefaults()
    {
        var store = JsonPreferencesStore.InMemory();
        store.Set(
            AssistantSettings.PreferencesKey,
            JsonNode.Parse("""{"isEnabled":true,"provider":1,"baseUrl":"https://ai.example.edu/v1","model":"m","presentation":"sidebar"}"""));

        var settings = store.Get(AssistantSettings.PreferencesKey, AssistantSettings.Defaults);

        Assert.Equal(AssistantSettings.Defaults, settings);
        Assert.Equal(AssistantProvider.Local, settings.Provider);
    }

    [Fact]
    public void AProviderIsWrittenInItsStoredSpelling()
    {
        var json = JsonDefaults.Serialize(AssistantFixtures.EnabledCloud);

        Assert.Contains("\"provider\":\"cloud\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"local\"", JsonDefaults.Serialize(AssistantSettings.Defaults), StringComparison.Ordinal);
    }

    // The privacy label is derived from the endpoint host actually configured,
    // never from the provider or the profile's name.

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://LOCALHOST:11434/v1", true)]
    [InlineData("  http://localhost:11434/v1  ", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("https://example.com/v1", false)]
    [InlineData("http://localhost.example.com/v1", false)]
    [InlineData("http://localhost@example.com/v1", false)]
    [InlineData("http://example.com/localhost/v1", false)]
    [InlineData("http://127.0.0.1.example.com/v1", false)]
    [InlineData("localhost:11434/v1", false)]
    [InlineData("not a url at all", false)]
    [InlineData("", false)]
    public void TheEndpointHostDecidesWhetherAnythingLeavesThisMachine(string baseUrl, bool local)
    {
        foreach (var provider in Enum.GetValues<AssistantProvider>())
        {
            var settings = AssistantSettings.Defaults with { Provider = provider, BaseUrl = baseUrl };

            Assert.Equal(local, settings.StaysOnThisMachine);
            Assert.Equal(local ? "On this PC" : "Cloud · AI (Cloud)", settings.PrivacyLabel);
            Assert.Equal(
                local ? AssistantProvider.Local.PrivacySummary() : AssistantProvider.Cloud.PrivacySummary(),
                settings.PrivacySummary);
        }
    }

    [Fact]
    public void ALocalProviderAtARemoteHostIsLabelledExactlyLikeACloudOne()
    {
        var local = AssistantSettings.Defaults with { Provider = AssistantProvider.Local, BaseUrl = "https://ai.example.edu/v1" };
        var cloud = local with { Provider = AssistantProvider.Cloud };

        Assert.Equal(cloud.PrivacyLabel, local.PrivacyLabel);
        Assert.DoesNotContain("On this PC", local.PrivacyLabel, StringComparison.Ordinal);
        Assert.Equal(cloud.PrivacySummary, local.PrivacySummary);
    }

    [Fact]
    public void ACloudProviderAtThisMachineIsLabelledAsStayingHere()
    {
        var settings = AssistantFixtures.EnabledCloud with { BaseUrl = "http://localhost:1234/v1" };

        Assert.True(settings.StaysOnThisMachine);
        Assert.Equal("On this PC", settings.PrivacyLabel);
    }

    [Fact]
    public void AModelNamedLocalButPointedAwayIsDescribedAsSendingData()
    {
        var profile = new AssistantProfile
        {
            Name = "On this PC",
            Provider = AssistantProvider.Local,
            BaseUrl = "https://example.com/v1",
            Model = "x",
        };

        Assert.False(profile.StaysOnThisMachine);
        Assert.Equal("Sends titles, courses and dates to this service", profile.PrivacyDescription);
        Assert.Equal("Cloud model", profile.PrivacyAccessibilityLabel);
        Assert.StartsWith("Cloud", AssistantSettings.Applying(profile, AssistantSettings.Defaults).PrivacyLabel, StringComparison.Ordinal);
    }

    /// <summary>
    /// The label is only honest if it reads the address the request is sent
    /// to. For each address, where the request actually went must agree with
    /// what the label claims.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.1:11434/v1")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:11434/v1")]
    [InlineData("https://ai.example.edu/v1")]
    [InlineData("http://localhost.example.com/v1")]
    [InlineData("http://localhost@example.com/v1")]
    public async Task TheLabelAgreesWithWhereTheRequestActuallyGoes(string baseUrl)
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("ok");
        var settings = AssistantFixtures.EnabledCloud with { BaseUrl = baseUrl };
        var service = new ChatCompletionsAssistantService(settings, "sk-invented", Calendar, handler);

        await service.AnswerAsync("what is most urgent?", [], [], Now);

        var host = handler.Single.Uri.DnsSafeHost;
        Assert.Equal(settings.StaysOnThisMachine, host is "localhost" or "127.0.0.1" or "::1");
        Assert.Equal(settings.StaysOnThisMachine, settings.PrivacyLabel == "On this PC");
    }

    // Counts and dates are computed by the app and handed to the model, never
    // asked of it.

    [Fact]
    public async Task TheModelIsHandedTheCountsAndDaysTheAppWorkedOut()
    {
        // Friday evening, two days before the clocks go forward: the lab report
        // is three calendar days away but only 62 hours.
        var now = AssistantFixtures.Date(2026, 10, 2, hour: 18);
        AssignmentListItem[] upcoming =
        [
            AssistantFixtures.Item("Lab report", "COMP2050", AssistantFixtures.Date(2026, 10, 5, hour: 9)),
            AssistantFixtures.Item("Poster", "DESN1500", AssistantFixtures.Date(2026, 10, 9, hour: 23, minute: 59)),
            AssistantFixtures.Item("Exam", "STAT1010", AssistantFixtures.Date(2026, 11, 2, hour: 10)),
            AssistantFixtures.Item("Essay", null, AssistantFixtures.Date(2026, 11, 3, hour: 10)),
        ];
        var handler = FakeHttpHandler.ReplyingWithCompletion("Start the lab report.");
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, Calendar, handler);

        var reply = await service.AnswerAsync(
            "how many are due this week?",
            [],
            AssistantAssignmentDigest.ForScope(upcoming, null, now),
            now);

        var system = Messages(handler.Single)[0].Content;
        Assert.Contains("Total upcoming: 4", system, StringComparison.Ordinal);
        Assert.Contains("Due within 7 days: 2", system, StringComparison.Ordinal);
        Assert.Contains("Due within 31 days: 3", system, StringComparison.Ordinal);
        Assert.Contains("Nearest: Lab report, in 3 days", system, StringComparison.Ordinal);
        Assert.Contains("- Lab report (COMP2050), due in 3 days", system, StringComparison.Ordinal);
        Assert.Contains("- Essay, due in 32 days", system, StringComparison.Ordinal);
        Assert.Contains("the counts are already correct, so do not recount them", system, StringComparison.Ordinal);
        // The answer is shown as given; nothing in it is read back as a number.
        Assert.Equal("Start the lab report.", reply);
    }

    [Fact]
    public void TheFactsCountCalendarDaysNotElapsedHours()
    {
        var now = AssistantFixtures.Date(2026, 8, 3, hour: 23, minute: 50);
        AssistantAssignmentDigest[] digests =
        [
            new(AssistantFixtures.Item("Tomorrow, ten minutes away", null, AssistantFixtures.Date(2026, 8, 4, hour: 0, minute: 0))),
            new(AssistantFixtures.Item("A week today", null, AssistantFixtures.Date(2026, 8, 10, hour: 23, minute: 59))),
            new(AssistantFixtures.Item("Eight days away", null, AssistantFixtures.Date(2026, 8, 11, hour: 0, minute: 1))),
            new(AssistantFixtures.Item("Yesterday", null, AssistantFixtures.Date(2026, 8, 2, hour: 23, minute: 59))),
        ];

        var facts = new AssistantFacts(digests, now, Calendar);

        Assert.Equal(4, facts.Total);
        Assert.Equal(2, facts.DueThisWeek);
        Assert.Equal(3, facts.DueThisMonth);
        Assert.Equal(1, facts.Overdue);
        Assert.Equal("Yesterday", facts.NearestTitle);
        Assert.Equal(-1, facts.NearestInDays);
        Assert.Equal(
            "Total upcoming: 4\nDue within 7 days: 2\nDue within 31 days: 3\nAlready past their date: 1\nNearest: Yesterday, in -1 days",
            facts.PromptLines);
    }

    // An unreadable or invented reply produces no task, never a task with a
    // guessed deadline.

    [Theory]
    [InlineData("Sure, I added that for you!")]
    [InlineData("""{"tasks":[{"title":"Essay","due":"2026-08-07T17:00"},"and also the reading"]}""")]
    [InlineData("""{"task":[{"title":"Essay","due":"2026-08-07T17:00"}]}""")]
    [InlineData("""[{"title":"Essay","due":"2026-08-07T17:00"}]""")]
    [InlineData("""{"tasks":[{"title":42,"due":"2026-08-07T17:00"}]}""")]
    [InlineData("""{"tasks":[{"title":"Essay","due":"2026-08-07T17:00"}] /* done */}""")]
    public async Task AnUnreadableReplyOpensNoReview(string content)
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion(content);
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, Calendar, handler);

        var drafts = await service.DraftTasksAsync("essay due friday 5pm", Now);

        Assert.Empty(drafts);
        Assert.Null(AssistantDraftReview.Started(drafts, "essay due friday 5pm"));
    }

    [Theory]
    [InlineData("2026-02-30T10:00")]
    [InlineData("2026-04-31T10:00")]
    [InlineData("2026-08-07T24:00")]
    [InlineData("2026-08-07T24:30")]
    [InlineData("2026-13-01T10:00")]
    [InlineData("2026-08-07T17:60")]
    [InlineData("2026-08-07T17:00Z")]
    [InlineData("2026-08-07T17:00:00")]
    [InlineData("2026-08-07t17:00")]
    [InlineData("2026-08-07 17:00")]
    [InlineData("26-08-07T17:00")]
    [InlineData("next Friday")]
    [InlineData("")]
    public void ADateThatIsNotARealOneArrivesAsNoDate(string due)
    {
        var reply = $$"""{"tasks":[{"title":"Essay","course":null,"due":{{JsonSerializer.Serialize(due)}}}]}""";

        var draft = Assert.Single(ChatCompletionsAssistantService.ParseDrafts(reply, "essay", Calendar));

        Assert.Null(draft.DueDate);
        Assert.False(draft.CanBeSaved);
        Assert.False(draft.WillBeSaved);
    }

    [Fact]
    public async Task ACompletionWithNoTextIsAnErrorNotAnAnswer()
    {
        var handler = FakeHttpHandler.Replying(
            System.Net.HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":null}}]}""");
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, Calendar, handler);

        var error = await Assert.ThrowsAsync<AssistantException>(() => service.DraftTasksAsync("essay friday", Now));

        Assert.Equal(AssistantError.BadResponse, error.Error);
    }

    // The assistant never edits or deletes existing events.

    [Fact]
    public async Task SavingDraftsOnlyEverAddsEvents()
    {
        var label = Guid.NewGuid();
        var imported = new AssignmentSnapshot
        {
            Title = "Lab 3",
            CourseName = "COMP2050",
            DueDate = AssistantFixtures.Date(2026, 8, 12, hour: 0),
            Source = AssignmentSource.CanvasCalendarFeed,
            ExternalId = "event-invented-3",
        };
        var typed = new AssignmentSnapshot
        {
            Title = "Revise for STAT1010 quiz",
            DueDate = AssistantFixtures.Date(2026, 8, 14, hour: 18),
            Source = AssignmentSource.Manual,
        };
        var repository = new RecordingRepository(imported, typed);
        var before = repository.Events.ToList();

        // A revision can hand back any id it likes. One that happens to match
        // an event already stored must still become a new event, never an edit.
        var outcome = await AssistantDraftReview.SaveAsync(
            repository,
            [
                new AssistantDraftTask { Id = imported.Id, Title = "Lab 3 write-up", LabelId = label, DueDate = AssistantFixtures.Date(2026, 8, 11), SourceText = "x" },
                new AssistantDraftTask { Id = typed.Id, Title = "Quiz practice", DueDate = AssistantFixtures.Date(2026, 8, 13), SourceText = "x" },
            ],
            Now);

        Assert.All(repository.SavedDrafts, draft => Assert.Null(draft.Id));
        Assert.DoesNotContain("SaveManualAsync(edit)", repository.Calls);
        Assert.DoesNotContain(repository.Calls, call => call is "DeleteAsync" or "DeleteAllAsync" or "UpdateStatusAsync"
            or "UpsertAsync" or "ClearLabelAsync" or "ReconcileCanvasFeedAsync" or "DeleteEventsInCourseAsync");
        Assert.All(repository.LabelsSet, set => Assert.Contains(set.Id, outcome.SavedIds));
        Assert.Equal(before, repository.Events.Take(2));
        Assert.Equal(4, repository.Events.Count);
        Assert.DoesNotContain(imported.Id, outcome.SavedIds);
        Assert.DoesNotContain(typed.Id, outcome.SavedIds);
    }

    [Fact]
    public void ADraftBecomesANewEventWhateverIdItCarries()
    {
        var draft = new AssistantDraftTask { Id = Guid.NewGuid(), Title = " Essay ", CourseName = " DESN1500 ", DueDate = Now, SourceText = "x" };

        var manual = draft.ToManualDraft()!;

        Assert.Null(manual.Id);
        Assert.Equal("Essay", manual.Title);
        Assert.Equal("DESN1500", manual.CourseName);
        Assert.Equal(Now, manual.DueDate);
    }

    [Fact]
    public void TheAssistantHasNoWayToReachTheStoredEvents()
    {
        const BindingFlags everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        static bool IsRepository(Type type) => typeof(IAssignmentRepository).IsAssignableFrom(type);

        foreach (var type in new[] { typeof(ChatCompletionsAssistantService), typeof(OllamaModelManager), typeof(AssistantKeyStore) })
        {
            Assert.DoesNotContain(type.GetFields(everything), field => IsRepository(field.FieldType));
            Assert.DoesNotContain(
                type.GetConstructors(everything).SelectMany(constructor => constructor.GetParameters()),
                parameter => IsRepository(parameter.ParameterType));
        }
        Assert.DoesNotContain(
            typeof(IAssistantServicing).GetMethods().SelectMany(method => method.GetParameters()),
            parameter => IsRepository(parameter.ParameterType));
    }

    [Fact]
    public async Task ADraftThatFailsToSaveDoesNotStopTheRest()
    {
        var repository = new RecordingRepository { FailsToSave = draft => draft.Title == "Broken" };

        var outcome = await AssistantDraftReview.SaveAsync(
            repository,
            [
                new AssistantDraftTask { Title = "Broken", DueDate = Now, SourceText = "x" },
                new AssistantDraftTask { Title = "Fine", DueDate = Now, SourceText = "x" },
            ],
            Now);

        Assert.IsType<AssignmentRepositoryException>(Assert.Single(outcome.Errors));
        Assert.Single(outcome.SavedIds);
        Assert.Equal(["Fine"], repository.Events.Select(snapshot => snapshot.Title));
    }

    // API keys go only through the secret store, one per saved model, and
    // never into preferences or the conversation.

    [Fact]
    public void EachModelKeepsItsKeyUnderItsOwnAccount()
    {
        var id = Guid.Parse("4c1d0e9a-0000-4000-a000-000000000001");

        Assert.Equal("assistant-api-key.4C1D0E9A-0000-4000-A000-000000000001", AssistantKeyStore.AccountFor(id));
        Assert.Equal(SecretAccounts.AssistantApiKey, AssistantKeyStore.AccountFor(null));
    }

    [Fact]
    public async Task TwoModelsHoldTwoKeysAndDeletingOneLeavesTheOther()
    {
        var secrets = new InMemorySecretStore();
        var keys = new AssistantKeyStore(secrets);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await keys.SaveAsync("  sk-invented-first  ", first);
        await keys.SaveAsync("sk-invented-second", second);

        Assert.Equal("sk-invented-first", await keys.LoadAsync(first));
        Assert.Equal("sk-invented-first", await secrets.ReadAsync(AssistantKeyStore.AccountFor(first)));
        Assert.Equal("sk-invented-second", await keys.LoadAsync(second));

        await keys.DeleteAsync(first);

        Assert.Null(await keys.LoadAsync(first));
        Assert.Equal("sk-invented-second", await keys.LoadAsync(second));
    }

    [Fact]
    public async Task SavingAnEmptyKeyRemovesTheStoredOne()
    {
        var keys = new AssistantKeyStore(new InMemorySecretStore());
        var id = Guid.NewGuid();
        await keys.SaveAsync("sk-invented", id);

        await keys.SaveAsync("   ", id);

        Assert.Null(await keys.LoadAsync(id));
    }

    [Fact]
    public async Task TheOldSingleKeyMovesOntoTheFirstModelAndLeavesNoCopy()
    {
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretAccounts.AssistantApiKey, "sk-invented-legacy");
        var keys = new AssistantKeyStore(secrets);

        var (_, settings) = (await AssistantProfileLibrary.MigrateIfNeededAsync(
            AssistantProfileLibrary.Empty,
            AssistantFixtures.EnabledCloud,
            keys))!.Value;

        Assert.Equal("sk-invented-legacy", await keys.LoadAsync(settings.ActiveProfileId));
        Assert.Null(await secrets.ReadAsync(SecretAccounts.AssistantApiKey));
    }

    [Fact]
    public async Task ALocalSetupLeavesTheOldKeyWhereItWas()
    {
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretAccounts.AssistantApiKey, "sk-invented-legacy");
        var keys = new AssistantKeyStore(secrets);

        var (_, settings) = (await AssistantProfileLibrary.MigrateIfNeededAsync(
            AssistantProfileLibrary.Empty,
            AssistantSettings.Defaults,
            keys))!.Value;

        Assert.Null(await keys.LoadAsync(settings.ActiveProfileId));
        Assert.Equal("sk-invented-legacy", await secrets.ReadAsync(SecretAccounts.AssistantApiKey));
    }

    [Fact]
    public async Task DeletingAModelTakesItsKeyWithIt()
    {
        var keys = new AssistantKeyStore(new InMemorySecretStore());
        var library = AssistantProfileLibrary.Empty
            .Add(new AssistantProfile(AssistantService.Groq), out var groq)
            .Add(new AssistantProfile(AssistantService.OpenAI), out var openAI);
        await keys.SaveAsync("sk-invented-one", groq);
        await keys.SaveAsync("sk-invented-two", openAI);
        var settings = AssistantSettings.Applying(library.Profile(groq)!, AssistantSettings.Defaults);

        (library, settings) = await library.DeletingAsync(groq!.Value, settings, keys);

        Assert.Null(await keys.LoadAsync(groq));
        Assert.Equal("sk-invented-two", await keys.LoadAsync(openAI));
        Assert.Equal(openAI, settings.ActiveProfileId);
    }

    [Fact]
    public async Task AKeyIsSentOnlyAsTheAuthorizationHeader()
    {
        const string key = "sk-invented-123";
        var handler = FakeHttpHandler.ReplyingWithCompletion("""{"tasks":[]}""");
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledCloud, key, Calendar, handler);
        var history = new[] { new AssistantMessage { Role = AssistantMessageRole.User, Text = "earlier", Date = Now } };

        await service.AnswerAsync("what is most urgent?", history, [], Now);
        await service.SummariseAsync([new AssistantAssignmentDigest(AssistantFixtures.Item("Essay", null, Now.AddDays(2)))], Now);
        await service.DraftTasksAsync("essay friday 5pm", Now);
        await service.ReviseDraftsAsync([new AssistantDraftTask { Title = "Essay", DueDate = Now, SourceText = "essay" }], "make it 6pm", Now);

        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer " + key, request.Authorization);
            Assert.DoesNotContain(key, request.Body!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AKeyNeverReachesPreferencesOrTheConversation()
    {
        const string key = "sk-invented-456";
        var path = Path.Combine(directory, "settings.json");
        var secrets = new InMemorySecretStore();
        var keys = new AssistantKeyStore(secrets);
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq), out var id);
        var settings = AssistantSettings.Applying(library.Profile(id)!, AssistantFixtures.EnabledCloud);
        await keys.SaveAsync(key, id);

        // What asking a question does: load the key, ask, and record both turns.
        var handler = FakeHttpHandler.ReplyingWithCompletion("Nothing is due this week.");
        var service = new ChatCompletionsAssistantService(settings, await keys.LoadAsync(settings.ActiveProfileId), Calendar, handler);
        var answer = await service.AnswerAsync("what is due this week?", [], [], Now);
        var log = AssistantConversationLog.Empty
            .Append(new AssistantMessage { Role = AssistantMessageRole.User, Text = "what is due this week?", Date = Now })
            .Append(new AssistantMessage { Role = AssistantMessageRole.Assistant, Text = answer, Date = Now });

        var store = new JsonPreferencesStore(path);
        store.Set(AssistantSettings.PreferencesKey, settings);
        store.Set(AssistantProfileLibrary.PreferencesKey, library);
        store.Set(AssistantConversationLog.PreferencesKey, log);

        Assert.Equal("Bearer " + key, handler.Single.Authorization);
        Assert.DoesNotContain(key, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(key, await secrets.ReadAsync(AssistantKeyStore.AccountFor(id)));
    }

    private static List<(string Role, string Content)> Messages(RecordedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => (message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!))
            .ToList();
    }
}

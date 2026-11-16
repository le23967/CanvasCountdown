using System.Reflection;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// The optional assistant: what may leave this PC, what happens to a bad
/// reply, and the promise that nothing it says is saved without review.
/// </summary>
/// <remarks>No test makes a real network request. All fixtures are invented.</remarks>
public sealed class AssistantTests
{
    private static AppCalendar Calendar => AssistantFixtures.Sydney;

    private static DateTimeOffset Date(int year, int month, int day, int hour = 9)
        => AssistantFixtures.Date(year, month, day, hour);

    private static AssignmentListItem Item(string title, string? course, DateTimeOffset due)
        => AssistantFixtures.Item(title, course, due);

    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    // Changing drafts instead of starting again

    private static AssistantDraftTask Draft(
        string title,
        int day,
        string? course = null,
        bool include = true,
        Guid? labelId = null)
        => new()
        {
            Include = include,
            Title = title,
            CourseName = course,
            LabelId = labelId,
            DueDate = Date(2026, 8, day, hour: 23),
            SourceText = "gym monday wednesday friday",
        };

    /// <summary>
    /// The whole point of a revision: the model is handed the list that exists,
    /// so "put them under 41021" edits those rows rather than guessing again.
    /// </summary>
    [Fact]
    public void ARevisionEditsTheDraftsItWasGiven()
    {
        AssistantDraftTask[] existing = [Draft("Workout 1", day: 3), Draft("Workout 2", day: 5)];
        var reply = $$"""
            {"tasks":[
              {"id":"{{Upper(existing[0].Id)}}","title":"Workout 1","course":"41021","due":"2026-08-03T23:59"},
              {"id":"{{Upper(existing[1].Id)}}","title":"Workout 2","course":"41021","due":"2026-08-05T23:59"}
            ]}
            """;

        var revised = ChatCompletionsAssistantService.ParseRevisedDrafts(reply, existing, Calendar);

        Assert.Equal(existing.Select(draft => draft.Id), revised.Select(draft => draft.Id));
        Assert.Equal(new string?[] { "41021", "41021" }, revised.Select(draft => draft.CourseName));
    }

    /// <summary>
    /// The model is never told about the tick or the label, so it cannot have
    /// meant to change them. Corrections made by hand survive a follow-up.
    /// </summary>
    [Fact]
    public void ARevisionKeepsTheTicksAndLabelsSetByHand()
    {
        var label = Guid.NewGuid();
        AssistantDraftTask[] existing =
        [
            Draft("Workout 1", day: 3, include: false),
            Draft("Workout 2", day: 5, labelId: label),
        ];
        var reply = $$"""
            {"tasks":[
              {"id":"{{Upper(existing[0].Id)}}","title":"Workout 1","course":"Gym","due":"2026-08-03T23:59"},
              {"id":"{{Upper(existing[1].Id)}}","title":"Workout 2","course":"Gym","due":"2026-08-05T23:59"}
            ]}
            """;

        var revised = ChatCompletionsAssistantService.ParseRevisedDrafts(reply, existing, Calendar);

        Assert.False(revised[0].Include, "An unticked draft stays unticked");
        Assert.Equal(label, revised[1].LabelId);
        Assert.Equal(new string?[] { "Gym", "Gym" }, revised.Select(draft => draft.CourseName));
    }

    [Fact]
    public void ARevisionCanDropAndAddTasks()
    {
        AssistantDraftTask[] existing = [Draft("Workout 1", day: 3), Draft("Workout 2", day: 5)];
        var reply = $$"""
            {"tasks":[
              {"id":"{{Upper(existing[0].Id)}}","title":"Workout 1","course":null,"due":"2026-08-03T23:59"},
              {"id":"not-a-uuid","title":"Workout 3","course":null,"due":"2026-08-07T23:59"}
            ]}
            """;

        var revised = ChatCompletionsAssistantService.ParseRevisedDrafts(reply, existing, Calendar);

        Assert.Equal(["Workout 1", "Workout 3"], revised.Select(draft => draft.Title));
        Assert.Equal(existing[0].Id, revised[0].Id);
        // An unreadable id becomes a new draft rather than overwriting one.
        Assert.NotEqual(existing[1].Id, revised[1].Id);
    }

    /// <summary>A follow-up that wipes the review would be worse than one that does nothing at all.</summary>
    [Fact]
    public void AnUnreadableRevisionLeavesTheDraftsAlone()
    {
        AssistantDraftTask[] existing = [Draft("Workout 1", day: 3)];

        Assert.Equal(
            existing,
            ChatCompletionsAssistantService.ParseRevisedDrafts("I'm sorry, I can't do that", existing, Calendar));
        Assert.Equal(
            existing,
            ChatCompletionsAssistantService.ParseRevisedDrafts("""{"tasks":[]}""", existing, Calendar));
    }

    /// <summary>Only the three fields it is allowed to change are ever sent.</summary>
    [Fact]
    public void TheDraftsSentForRevisionCarryNoLabelOrTick()
    {
        var json = ChatCompletionsAssistantService.DraftsJson(
            [Draft("Workout 1", day: 3, course: "Gym", include: false, labelId: Guid.NewGuid())],
            Calendar);

        Assert.Contains("Workout 1", json);
        Assert.Contains("Gym", json);
        Assert.DoesNotContain("label", json);
        Assert.DoesNotContain("include", json);
        Assert.DoesNotContain("sourceText", json);
    }

    /// <summary>
    /// A first draft has nothing to line up with, so an invented id there could
    /// collide with a row already on screen.
    /// </summary>
    [Fact]
    public void AFirstDraftIgnoresAnyIdTheModelSends()
    {
        var collision = Guid.NewGuid();
        var reply = $$"""
            {"tasks":[{"id":"{{Upper(collision)}}","title":"Essay","course":null,"due":"2026-08-03T23:59"}]}
            """;

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "essay", Calendar);

        Assert.Single(drafts);
        Assert.NotEqual(collision, drafts[0].Id);
    }

    [Fact]
    public async Task AnEmptyInstructionChangesNothingWithoutAskingTheModel()
    {
        var refusing = new RefusingHandler();
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, Calendar, refusing);
        AssistantDraftTask[] existing = [Draft("Workout 1", day: 3)];

        var revised = await service.ReviseDraftsAsync(existing, "   ", Date(2026, 7, 28));

        Assert.Equal(existing, revised);
        Assert.Equal(0, refusing.Calls);
    }

    // Defaults and privacy posture

    [Fact]
    public void DefaultsAreAvailableButNeverSendAnythingAway()
    {
        var defaults = AssistantSettings.Defaults;

        // Available out of the box, so the toolbar button does something when
        // pressed rather than leading to a dead end.
        Assert.True(defaults.IsEnabled);

        // What must never drift: the default configuration cannot upload
        // anything. Choosing a cloud provider is a separate, deliberate act.
        Assert.Equal(AssistantProvider.Local, defaults.Provider);
        Assert.True(defaults.StaysOnThisMachine, "The default must never reach off this PC");
        Assert.False(defaults.Provider.RequiresApiKey());
    }

    [Fact]
    public void SwitchingProviderCarriesItsOwnEndpointAndModel()
    {
        var cloud = AssistantSettings.Applying(AssistantProvider.Cloud, AssistantSettings.Defaults);

        Assert.Equal(AssistantProvider.Cloud, cloud.Provider);
        Assert.NotEmpty(cloud.BaseUrl);
        Assert.False(cloud.StaysOnThisMachine);
        Assert.True(cloud.Provider.RequiresApiKey());

        var backToLocal = AssistantSettings.Applying(AssistantProvider.Local, cloud);
        Assert.True(backToLocal.StaysOnThisMachine);
        Assert.False(backToLocal.Provider.RequiresApiKey());
    }

    /// <summary>
    /// The cloud option used to name one service. Anything stored under the old
    /// name has to keep working, or an upgrade silently turns the assistant off.
    /// </summary>
    [Fact]
    public void TheOldSingleServiceProviderStillDecodes()
    {
        const string stored = """{"isEnabled":true,"provider":"groq","baseURL":"https://api.groq.com/openai/v1","model":"llama-3.3-70b-versatile","presentation":"sidebar"}""";

        var decoded = JsonDefaults.Deserialize<AssistantSettings>(stored)!;

        Assert.Equal(AssistantProvider.Cloud, decoded.Provider);
        Assert.True(decoded.Provider.RequiresApiKey());
        Assert.Equal("https://api.groq.com/openai/v1", decoded.BaseUrl);
        Assert.Equal("llama-3.3-70b-versatile", decoded.Model);
    }

    /// <summary>
    /// An unreadable provider must land on the one setting that cannot upload
    /// anything, never on the one that can.
    /// </summary>
    [Fact]
    public void AnUnknownProviderFallsBackToStayingOnThisMac()
    {
        const string stored = """{"isEnabled":true,"provider":"something-new","baseURL":"http://localhost:11434/v1","model":"llama3.1:8b","presentation":"sidebar"}""";

        var decoded = JsonDefaults.Deserialize<AssistantSettings>(stored)!;

        Assert.Equal(AssistantProvider.Local, decoded.Provider);
        Assert.False(decoded.Provider.RequiresApiKey());
    }

    /// <summary>
    /// The suggestions are a convenience, not a contract, but a broken address
    /// in the list would fill the fields in with something that cannot work.
    /// </summary>
    [Fact]
    public void EverySuggestedServiceHasAUsableAddressAndModel()
    {
        foreach (var service in AssistantService.AllServices)
        {
            Assert.True(
                Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var url) && url.Host.Length > 0,
                $"{service.Name} has no usable address");
            Assert.False(service.Models.IsEmpty, $"{service.Name} suggests no model");
            Assert.NotEmpty(service.Note);
        }

        Assert.True(
            AssistantService.Ollama.BaseUrl.Contains("localhost", StringComparison.Ordinal),
            "The local suggestion must point at this PC");
        foreach (var service in AssistantService.CloudServices)
        {
            Assert.False(AssistantEndpoint.IsLocal(service.BaseUrl), $"{service.Name} is offered as a cloud service");
            Assert.True(service.KeysUrl is not null, $"{service.Name} needs a key, so it must say where to get one");
        }
    }

    [Fact]
    public void LocalProviderPointedAtARemoteHostIsNotClaimedAsPrivate()
    {
        var settings = AssistantSettings.Defaults with
        {
            Provider = AssistantProvider.Local,
            BaseUrl = "https://example.com/v1",
        };

        Assert.False(settings.StaysOnThisMachine, "Naming it local does not keep the data here; the address decides");
    }

    [Fact]
    public void DigestCarriesOnlyTheThreeAgreedFields()
    {
        var due = Date(2026, 8, 4, hour: 16);
        var source = Item("Example Quiz", "99999 Example Course", due);
        var digest = new AssistantAssignmentDigest(source);

        Assert.Equal("Example Quiz", digest.Title);
        Assert.Equal("99999 Example Course", digest.CourseName);
        Assert.Equal(due, digest.DueDate);

        // Completion and ignore state are local decisions and are not included.
        var fields = typeof(AssistantAssignmentDigest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet();
        Assert.Equal(new HashSet<string> { "Title", "CourseName", "DueDate" }, fields);
        Assert.DoesNotContain("IsCompleted", fields);
        Assert.DoesNotContain("IsIgnored", fields);
        Assert.DoesNotContain("Id", fields);
    }

    [Fact]
    public void DigestLineMentionsOnlyTitleCourseAndRemainingDays()
    {
        var now = Date(2026, 7, 28);
        var digest = new AssistantAssignmentDigest(Item("Example Quiz", "99999 Example Course", Date(2026, 8, 4)));

        var line = digest.Line(now, Calendar);

        Assert.Contains("Example Quiz", line);
        Assert.Contains("99999 Example Course", line);
        Assert.Contains("7 days", line);
    }

    // Reading the model's reply

    [Fact]
    public void WellFormedReplyBecomesDrafts()
    {
        const string reply = """{"tasks":[{"title":"Essay draft","course":null,"due":"2026-08-07T17:00"}]}""";

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "essay draft due next Friday 5pm", Calendar);

        Assert.Single(drafts);
        Assert.Equal("Essay draft", drafts[0].Title);
        Assert.Null(drafts[0].CourseName);
        Assert.NotNull(drafts[0].DueDate);
        Assert.Equal(17, Calendar.ToLocal(drafts[0].DueDate!.Value).Hour);
        Assert.True(drafts[0].CanBeSaved);
    }

    [Fact]
    public void ReplyWrappedInProseIsStillRead()
    {
        // Small local models often add commentary around the JSON.
        const string reply = """
            Sure! Here is the task you asked for:
            {"tasks":[{"title":"Reading","course":"99999 Example Course","due":"2026-08-09T23:59"}]}
            Let me know if you want more.
            """;

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "reading by Sunday", Calendar);

        Assert.Single(drafts);
        Assert.Equal("99999 Example Course", drafts[0].CourseName);
    }

    [Fact]
    public void MalformedReplyProducesNothingRatherThanAGuess()
    {
        foreach (var reply in new[] { "I could not work that out.", "{ not json", """{"tasks": "nope"}""", "" })
        {
            Assert.True(
                ChatCompletionsAssistantService.ParseDrafts(reply, "something", Calendar).IsEmpty,
                $"A reply that cannot be read must not become a deadline: {reply}");
        }
    }

    [Fact]
    public void TaskWithoutADateArrivesUnsaveable()
    {
        const string reply = """{"tasks":[{"title":"Read chapter 4","course":null,"due":null}]}""";

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "read chapter 4", Calendar);

        Assert.Single(drafts);
        Assert.Null(drafts[0].DueDate);
        Assert.False(drafts[0].CanBeSaved, "Review must supply a date; the app will not invent one");
    }

    [Fact]
    public void UnparseableDateLeavesTheTaskWithoutOne()
    {
        const string reply = """{"tasks":[{"title":"Essay","course":null,"due":"next Friday"}]}""";

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "essay", Calendar);

        Assert.Single(drafts);
        Assert.Null(drafts[0].DueDate);
        Assert.False(drafts[0].CanBeSaved);
    }

    [Fact]
    public void EntriesWithoutATitleAreDropped()
    {
        const string reply = """
            {"tasks":[{"title":"","due":"2026-08-07T17:00"},
                      {"title":"Good one","due":"2026-08-07T17:00"}]}
            """;

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "x", Calendar);

        Assert.Equal(["Good one"], drafts.Select(draft => draft.Title));
    }

    [Fact]
    public void SeveralTasksInOneReply()
    {
        const string reply = """
            {"tasks":[{"title":"First","due":"2026-08-07T17:00"},
                      {"title":"Second","due":"2026-08-09T23:59"}]}
            """;

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "two things", Calendar);

        Assert.Equal(["First", "Second"], drafts.Select(draft => draft.Title));
        Assert.All(drafts, draft => Assert.True(draft.Include));
    }

    // Errors

    [Fact]
    public async Task DisabledAssistantRefusesBeforeAnyRequest()
    {
        var refusing = new RefusingHandler();
        var service = new ChatCompletionsAssistantService(
            AssistantSettings.Defaults with { IsEnabled = false },
            null,
            Calendar,
            refusing);

        var error = await Assert.ThrowsAsync<AssistantException>(() => service.SummariseAsync(
            [new AssistantAssignmentDigest(Item("X", null, Date(2026, 8, 4)))],
            Date(2026, 7, 28)));

        Assert.Equal(AssistantError.NotConfigured, error.Error);
        Assert.Equal(0, refusing.Calls);
    }

    [Fact]
    public async Task ACloudModelWithoutAKeyRefusesBeforeAnyRequest()
    {
        var refusing = new RefusingHandler();
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledCloud, null, Calendar, refusing);

        var error = await Assert.ThrowsAsync<AssistantException>(() => service.DraftTasksAsync("essay", Date(2026, 7, 28)));

        Assert.Equal(AssistantError.MissingApiKey, error.Error);
        Assert.Equal(0, refusing.Calls);
    }

    [Fact]
    public async Task EmptyWorkloadIsAnsweredWithoutAskingTheModel()
    {
        var refusing = new RefusingHandler();
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, Calendar, refusing);

        var summary = await service.SummariseAsync([], Date(2026, 7, 28));

        Assert.Equal("Nothing is coming up.", summary);
        Assert.Equal(0, refusing.Calls);
    }

    [Fact]
    public async Task EmptyInputProducesNoDraftsWithoutAskingTheModel()
    {
        var refusing = new RefusingHandler();
        var service = new ChatCompletionsAssistantService(AssistantFixtures.EnabledLocal, null, Calendar, refusing);

        var drafts = await service.DraftTasksAsync("   ", Date(2026, 7, 28));

        Assert.Empty(drafts);
        Assert.Equal(0, refusing.Calls);
    }

    [Fact]
    public void ErrorsCarryCodesNotContent()
    {
        foreach (var error in Enum.GetValues<AssistantError>())
        {
            Assert.StartsWith("assistant.", error.DiagnosticCode(), StringComparison.Ordinal);
            // A diagnostic code carries no prose that could include a prompt.
            Assert.DoesNotContain(" ", error.DiagnosticCode());
            Assert.False(string.IsNullOrEmpty(error.Description()));
            Assert.Equal(error.Description(), new AssistantException(error).Message);
        }
    }
}

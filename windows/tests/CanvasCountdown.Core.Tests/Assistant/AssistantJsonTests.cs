using System.Text.Json;
using System.Text.Json.Nodes;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// What the assistant writes to preferences, and how forgivingly it reads it
/// back: exactly as forgiving as the macOS decoders, and no more.
/// </summary>
public sealed class AssistantJsonTests
{
    private static readonly Guid ProfileId = Guid.Parse("4c1d0e9a-0000-4000-a000-000000000001");

    private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();

    // The settings

    [Fact]
    public void SettingsAreWrittenAsTheirSevenFieldsAndNothingWorkedOut()
    {
        var settings = AssistantSettings.Defaults with { SidebarWidth = 360, ActiveProfileId = ProfileId };

        var written = Object(JsonDefaults.Serialize(settings));

        Assert.Equal(
            ["isEnabled", "provider", "baseUrl", "model", "presentation", "sidebarWidth", "activeProfileId"],
            written.Select(property => property.Key));
        Assert.Equal("local", (string?)written["provider"]);
        Assert.Equal("sidebar", (string?)written["presentation"]);
        Assert.Equal(settings, JsonDefaults.Deserialize<AssistantSettings>(written.ToJsonString()));
    }

    [Fact]
    public void TheMacFieldNamesAreReadToo()
    {
        const string stored = """{"isEnabled":false,"provider":"cloud","baseURL":"https://ai.example.edu/v1","model":"campus-model","presentation":"popover","sidebarWidth":320,"activeProfileID":"4C1D0E9A-0000-4000-A000-000000000001"}""";

        var decoded = JsonDefaults.Deserialize<AssistantSettings>(stored)!;

        Assert.False(decoded.IsEnabled);
        Assert.Equal("https://ai.example.edu/v1", decoded.BaseUrl);
        Assert.Equal(AssistantPresentationPreference.Popover, decoded.Presentation);
        Assert.Equal(320, decoded.SidebarWidth);
        Assert.Equal(ProfileId, decoded.ActiveProfileId);
    }

    [Fact]
    public void AMissingWidthOrModelChoiceReadsAsNone()
    {
        const string stored = """{"isEnabled":true,"provider":"local","baseUrl":"http://localhost:11434/v1","model":"llama3.1:8b","presentation":"sidebar"}""";

        var decoded = JsonDefaults.Deserialize<AssistantSettings>(stored)!;

        Assert.Null(decoded.SidebarWidth);
        Assert.Null(decoded.ActiveProfileId);
        Assert.Equal(AssistantSettings.Defaults, decoded);
    }

    [Theory]
    [InlineData("isEnabled")]
    [InlineData("provider")]
    [InlineData("baseUrl")]
    [InlineData("model")]
    [InlineData("presentation")]
    public void SettingsMissingAFieldAreUnreadableAndFallBackToTheDefaults(string missing)
    {
        var stored = Object("""{"isEnabled":false,"provider":"cloud","baseUrl":"https://ai.example.edu/v1","model":"m","presentation":"popover"}""");
        stored.Remove(missing);

        Assert.Throws<JsonException>(() => JsonDefaults.Deserialize<AssistantSettings>(stored.ToJsonString()));

        var store = JsonPreferencesStore.InMemory();
        store.Set(AssistantSettings.PreferencesKey, stored);
        Assert.Equal(AssistantSettings.Defaults, store.Get(AssistantSettings.PreferencesKey, AssistantSettings.Defaults));
    }

    [Theory]
    [InlineData("\"popover\"", AssistantPresentationPreference.Popover)]
    [InlineData("\"sidebar\"", AssistantPresentationPreference.Sidebar)]
    [InlineData("\"automatic\"", AssistantPresentationPreference.Sidebar)]
    [InlineData("\"Popover\"", AssistantPresentationPreference.Sidebar)]
    [InlineData("\"window\"", AssistantPresentationPreference.Sidebar)]
    public void AStoredPresentationIsReadOrFallsBackToTheSidebar(string stored, AssistantPresentationPreference expected)
    {
        var json = $$"""{"isEnabled":true,"provider":"local","baseUrl":"http://localhost:11434/v1","model":"m","presentation":{{stored}}}""";

        Assert.Equal(expected, JsonDefaults.Deserialize<AssistantSettings>(json)!.Presentation);
    }

    [Fact]
    public void APresentationThatIsNotTextIsUnreadable()
    {
        const string stored = """{"isEnabled":true,"provider":"local","baseUrl":"http://localhost:11434/v1","model":"m","presentation":1}""";

        Assert.Throws<JsonException>(() => JsonDefaults.Deserialize<AssistantSettings>(stored));
    }

    // The saved models

    [Fact]
    public void TheLibraryIsWrittenWithItsVersionAndReadBackWhole()
    {
        var library = AssistantProfileLibrary.Empty
            .Add(new AssistantProfile(AssistantService.Groq))
            .Add(AssistantProfile.Blank() with { Model = "" });

        var written = Object(JsonDefaults.Serialize(library));

        Assert.Equal(["version", "profiles"], written.Select(property => property.Key));
        Assert.Equal(1, (int?)written["version"]);
        Assert.Equal(
            ["id", "name", "provider", "baseUrl", "model"],
            written["profiles"]![0]!.AsObject().Select(property => property.Key));
        Assert.Equal(library, JsonDefaults.Deserialize<AssistantProfileLibrary>(written.ToJsonString()));
    }

    [Fact]
    public void ALibraryFromANewerBuildKeepsItsVersionSoItIsNeverPassedOffAsCurrent()
    {
        const string stored = """{"version":99,"profiles":"whatever a later build keeps here"}""";

        var decoded = JsonDefaults.Deserialize<AssistantProfileLibrary>(stored)!;

        Assert.Empty(decoded.Profiles);
        Assert.Equal(99, decoded.Version);
        Assert.Equal(99, (int?)Object(JsonDefaults.Serialize(decoded))["version"]);
    }

    [Theory]
    [InlineData("""{"profiles":[]}""", 1)]
    [InlineData("""{"version":null,"profiles":[]}""", 1)]
    [InlineData("""{"version":1.0,"profiles":[]}""", 1)]
    [InlineData("""{"version":0}""", 0)]
    [InlineData("""{}""", 1)]
    public void AVersionIsReadAsAWholeNumberOrTakenAsCurrent(string stored, int version)
    {
        var decoded = JsonDefaults.Deserialize<AssistantProfileLibrary>(stored)!;

        Assert.Equal(version, decoded.Version);
        Assert.Empty(decoded.Profiles);
    }

    [Theory]
    [InlineData("""{"version":"1","profiles":[]}""")]
    [InlineData("""{"version":1.5,"profiles":[]}""")]
    [InlineData("""{"version":1,"profiles":{}}""")]
    [InlineData("""{"version":1,"profiles":[{"name":"No id","provider":"local","baseUrl":"http://localhost:11434/v1","model":"m"}]}""")]
    [InlineData("""{"version":1,"profiles":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","provider":"local","baseUrl":"http://localhost:11434/v1","model":"m"}]}""")]
    [InlineData("""["not","a","library"]""")]
    public void AMalformedLibraryIsUnreadableRatherThanHalfRead(string stored)
    {
        Assert.Throws<JsonException>(() => JsonDefaults.Deserialize<AssistantProfileLibrary>(stored));
    }

    [Fact]
    public void ModelsThatCannotBeSelectedAreDroppedOnRead()
    {
        const string stored = """
            {"version":1,"profiles":[
              {"id":"4C1D0E9A-0000-4000-A000-000000000001","name":"Kept","provider":"local","baseURL":"http://localhost:11434/v1","model":""},
              {"id":"4C1D0E9A-0000-4000-A000-000000000002","name":"   ","provider":"local","baseURL":"http://localhost:11434/v1","model":"m"},
              {"id":"4C1D0E9A-0000-4000-A000-000000000003","name":"No address","provider":"cloud","baseURL":"not a url at all","model":"m"},
              {"id":"4C1D0E9A-0000-4000-A000-000000000004","name":"No host","provider":"cloud","baseURL":"localhost:11434/v1","model":"m"}
            ]}
            """;

        var decoded = JsonDefaults.Deserialize<AssistantProfileLibrary>(stored)!;

        Assert.Equal(["Kept"], decoded.Profiles.Select(profile => profile.Name));
    }

    // The conversation

    [Fact]
    public void TheConversationIsWrittenWithDatesAndRoles()
    {
        var said = AssistantFixtures.Date(2026, 8, 3, hour: 9, minute: 41);
        var log = AssistantConversationLog.Empty.Append(new AssistantMessage
        {
            Id = ProfileId,
            Role = AssistantMessageRole.Assistant,
            Text = "The prototype video.",
            Date = said,
        });

        var written = Object(JsonDefaults.Serialize(log));
        var message = written["messages"]![0]!.AsObject();

        Assert.Equal(["version", "messages"], written.Select(property => property.Key));
        Assert.Equal(["id", "role", "text", "date"], message.Select(property => property.Key));
        Assert.Equal("assistant", (string?)message["role"]);
        Assert.Equal(said, DateTimeOffset.Parse((string)message["date"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(log, JsonDefaults.Deserialize<AssistantConversationLog>(written.ToJsonString()));
    }

    [Fact]
    public void ALogFromANewerBuildIsNotEvenRead()
    {
        const string stored = """{"version":2,"messages":"whatever a later build keeps here"}""";

        var decoded = JsonDefaults.Deserialize<AssistantConversationLog>(stored)!;

        Assert.True(decoded.IsEmpty);
        Assert.Equal(2, decoded.Version);
    }

    [Fact]
    public void EmptyTurnsAndTurnsPastTheLimitAreDroppedOnRead()
    {
        var messages = new JsonArray();
        var start = AssistantFixtures.Date(2026, 8, 3);
        messages.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["role"] = "user", ["text"] = "  ", ["date"] = start.ToString("O") });
        for (var index = 0; index < AssistantConversationLog.MaximumMessages + 5; index++)
        {
            messages.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["role"] = "user",
                ["text"] = $"question {index}",
                ["date"] = start.AddMinutes(index).ToString("O"),
            });
        }
        var stored = new JsonObject { ["version"] = 1, ["messages"] = messages }.ToJsonString();

        var decoded = JsonDefaults.Deserialize<AssistantConversationLog>(stored)!;

        Assert.Equal(AssistantConversationLog.MaximumMessages, decoded.Messages.Count);
        Assert.Equal("question 5", decoded.Messages[0].Text);
    }

    [Theory]
    [InlineData("""{"version":1,"messages":[{"role":"user","text":"no id","date":"2026-08-03T09:00:00+10:00"}]}""")]
    [InlineData("""{"version":1,"messages":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","role":"user","text":"no date"}]}""")]
    [InlineData("""{"version":1,"messages":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","role":"tutor","text":"?","date":"2026-08-03T09:00:00+10:00"}]}""")]
    [InlineData("""{"version":1,"messages":{}}""")]
    public void AMalformedLogIsUnreadable(string stored)
    {
        Assert.Throws<JsonException>(() => JsonDefaults.Deserialize<AssistantConversationLog>(stored));
    }

    // Unreadable values fail the way the preferences store expects

    /// <summary>
    /// The preferences store treats a value it cannot read as absent, but only
    /// when reading fails as a JSON error. Anything else thrown would take the
    /// whole settings screen down with it.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("\"not json\"")]
    [InlineData("[]")]
    [InlineData("""{"version":"x"}""")]
    [InlineData("""{"version":1,"profiles":[1],"messages":[1]}""")]
    public void AnUnreadableValueReadsAsAbsentRatherThanThrowing(string stored)
    {
        var store = JsonPreferencesStore.InMemory();
        store.Set("value", JsonNode.Parse(stored));

        Assert.False(store.TryGet<AssistantSettings>("value", out _));
        Assert.False(store.TryGet<AssistantProfileLibrary>("value", out _));
        Assert.False(store.TryGet<AssistantConversationLog>("value", out _));
    }

    [Fact]
    public void SettingsOfTheWrongTypeReadAsAbsent()
    {
        var store = JsonPreferencesStore.InMemory();
        store.Set("value", JsonNode.Parse("""{"isEnabled":"yes","provider":"local","baseUrl":"http://localhost:11434/v1","model":"m","presentation":"sidebar"}"""));

        Assert.False(store.TryGet<AssistantSettings>("value", out _));
        // Neither field the library or the log needs is there, so to them this
        // is an empty one, as it is on macOS.
        Assert.True(store.Get("value", AssistantProfileLibrary.Empty).Profiles.IsEmpty);
        Assert.True(store.Get("value", AssistantConversationLog.Empty).IsEmpty);
    }
}

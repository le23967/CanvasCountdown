namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// What was asked of the assistant is kept, so it is possible to tell whether
/// something has already been asked and to read the answer again tomorrow.
/// </summary>
/// <remarks>
/// No test makes a real network request. All fixtures are invented. Where the
/// macOS test asked through the view model, these append the two turns an
/// answered question records.
/// </remarks>
public sealed class AssistantConversationHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Morning = AssistantFixtures.Date(2026, 8, 3, hour: 9);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "cc-assistant-log-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AssistantMessage Message(AssistantMessageRole role, string text, int minute = 0)
        => new() { Role = role, Text = text, Date = Morning.AddMinutes(minute) };

    /// <summary>The two turns an answered question leaves in the record.</summary>
    private static AssistantConversationLog Asked(AssistantConversationLog log, string question, int minute = 0)
        => log
            .Append(Message(AssistantMessageRole.User, question, minute))
            .Append(Message(AssistantMessageRole.Assistant, "Nothing is due before the prototype video.", minute));

    // It survives the window closing

    [Fact]
    public void AConversationIsStillThereOnTheNextLaunch()
    {
        var first = Asked(AssistantConversationLog.Empty, "how many are due this week?");
        new JsonPreferencesStore(FilePath).Set(AssistantConversationLog.PreferencesKey, first);

        Assert.Equal(2, first.Messages.Count);

        // A second store over the same file is what the next launch is.
        var second = new JsonPreferencesStore(FilePath)
            .Get(AssistantConversationLog.PreferencesKey, AssistantConversationLog.Empty);

        Assert.Equal(first.Messages.Select(message => message.Text), second.Messages.Select(message => message.Text));
        Assert.Equal([AssistantMessageRole.User, AssistantMessageRole.Assistant], second.Messages.Select(message => message.Role));
    }

    // A long record does not bury the panel

    /// <summary>
    /// The field to ask again, and the whole drafting section, sit below this.
    /// Weeks of scrollback in front of them would be worse than no record.
    /// </summary>
    [Fact]
    public void OnlyTheRecentExchangesAreShownAtFirst()
    {
        var log = AssistantConversationLog.Empty;
        for (var index = 0; index < 8; index++)
        {
            log = Asked(log, $"question {index}", minute: index);
        }

        Assert.Equal(16, log.Messages.Count);
        Assert.Equal(AssistantConversationLog.RecentConversationLimit, log.RecentMessages.Count);
        Assert.Equal(16 - AssistantConversationLog.RecentConversationLimit, log.EarlierMessageCount);
    }

    /// <summary>Shown newest-last, so the last thing on screen is the last thing said.</summary>
    [Fact]
    public void TheRecentOnesAreTheNewestOnes()
    {
        var log = AssistantConversationLog.Empty;
        for (var index = 0; index < 8; index++)
        {
            log = Asked(log, $"question {index}", minute: index);
        }

        Assert.Equal(log.Messages[^1].Text, log.RecentMessages[^1].Text);
        Assert.Equal("question 5", log.RecentMessages[0].Text);
    }

    [Fact]
    public void AShortConversationIsShownWhole()
    {
        var log = Asked(AssistantConversationLog.Empty, "what is most urgent?");

        Assert.Equal(0, log.EarlierMessageCount);
        Assert.Equal(2, log.RecentMessages.Count);
    }

    // The log itself

    [Fact]
    public void TheOldestGoWhenItGetsTooLong()
    {
        var log = AssistantConversationLog.Empty;
        var total = AssistantConversationLog.MaximumMessages + 10;
        for (var index = 0; index < total; index++)
        {
            log = log.Append(Message(AssistantMessageRole.User, $"message {index}"));
        }

        Assert.Equal(AssistantConversationLog.MaximumMessages, log.Messages.Count);
        Assert.Equal("message 10", log.Messages[0].Text);
        Assert.Equal($"message {total - 1}", log.Messages[^1].Text);
    }

    [Fact]
    public void AnEmptyTurnIsNotKept()
    {
        var log = AssistantConversationLog.Empty.Append(Message(AssistantMessageRole.Assistant, "   \n "));

        Assert.True(log.IsEmpty, "There is nothing to read back");
    }

    [Fact]
    public void ItSurvivesBeingWrittenAndReadAgain()
    {
        var log = AssistantConversationLog.Empty
            .Append(Message(AssistantMessageRole.User, "what is most urgent?"))
            .Append(Message(AssistantMessageRole.Assistant, "The prototype video."));

        var json = JsonDefaults.Serialize(log);
        var decoded = JsonDefaults.Deserialize<AssistantConversationLog>(json);

        Assert.Equal(log, decoded);
        Assert.Equal([AssistantMessageRole.User, AssistantMessageRole.Assistant], decoded!.Messages.Select(message => message.Role));
    }

    /// <summary>
    /// The same promise the labels make: something written by a newer build is
    /// left alone rather than rewritten over with what this build understands.
    /// </summary>
    [Fact]
    public void ALogFromANewerBuildReadsAsEmpty()
    {
        var json = $$"""
            {"version": {{AssistantConversationLog.CurrentVersion + 1}},
             "messages": [{"id":"{{Guid.NewGuid()}}","role":"user",
                           "text":"from the future","date":"2001-01-01T00:00:00Z"}]}
            """;

        var decoded = JsonDefaults.Deserialize<AssistantConversationLog>(json)!;

        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void AnUnreadableLogDoesNotTakeTheRestOfSettingsWithIt()
    {
        var store = new JsonPreferencesStore(FilePath);
        store.Set(AssistantConversationLog.PreferencesKey, "not json");
        store.Set("CanvasCountdown.settings.launchAtLogin", true);

        var reopened = new JsonPreferencesStore(FilePath);

        Assert.True(reopened.Get(AssistantConversationLog.PreferencesKey, AssistantConversationLog.Empty).IsEmpty);
        // The rest of the settings still load.
        Assert.True(reopened.Get("CanvasCountdown.settings.launchAtLogin", false));
    }
}

using System.Globalization;
using System.Text;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// The places a straight translation to .NET would read the model, the user's
/// text or the clock differently from macOS. All fixtures are invented.
/// </summary>
public sealed class AssistantPortingTests
{
    private static AppCalendar Calendar => AssistantFixtures.Sydney;

    private static readonly CultureInfo EnglishAU = CultureInfo.GetCultureInfo("en-AU");

    private static AssistantDraftTask DueAt(string due)
        => Assert.Single(ChatCompletionsAssistantService.ParseDrafts(
            $$"""{"tasks":[{"title":"Essay","due":"{{due}}"}]}""",
            "essay",
            Calendar));

    // The model's dates, on this calendar's clock

    [Fact]
    public void ADueTimeIsReadOnTheCalendarsClock()
    {
        var due = DueAt("2026-08-07T17:00").DueDate!.Value;

        Assert.Equal(new DateTimeOffset(2026, 8, 7, 7, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void ADueTimeTheClocksSkipLandsTheLengthOfTheJumpLater()
    {
        var due = DueAt("2026-10-04T02:30").DueDate!.Value;

        Assert.Equal(new DateTime(2026, 10, 4, 3, 30, 0), Calendar.ToLocal(due));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 16, 30, 0, TimeSpan.Zero), due);
    }

    /// <summary>
    /// macOS reads the model's dates with a date formatter, which takes the
    /// second of two 02:30s when the clocks go back — not the first, which is
    /// what the calendar arithmetic everywhere else in the app picks.
    /// </summary>
    [Fact]
    public void ADueTimeThatHappensTwiceIsReadAsItsSecondOccurrence()
    {
        var due = DueAt("2026-04-05T02:30").DueDate!.Value;

        Assert.Equal(new DateTimeOffset(2026, 4, 4, 16, 30, 0, TimeSpan.Zero), due);
        Assert.NotEqual(Calendar.FromLocal(2026, 4, 5, 2, 30), due);
    }

    [Theory]
    [InlineData("2026-8-7T5:00", 2026, 8, 7, 5, 0)]
    [InlineData("2026-08-07T17:0", 2026, 8, 7, 17, 0)]
    [InlineData(" 2026-08-07T17:00 ", 2026, 8, 7, 17, 0)]
    [InlineData("2026-08-07T23:59", 2026, 8, 7, 23, 59)]
    public void ShortFieldsAndSurroundingSpacesAreRead(string due, int year, int month, int day, int hour, int minute)
    {
        Assert.Equal(new DateTime(year, month, day, hour, minute, 0), Calendar.ToLocal(DueAt(due).DueDate!.Value));
    }

    [Fact]
    public void TheDatesSentForRevisionAreWrittenOnTheCalendarsClock()
    {
        var draft = new AssistantDraftTask
        {
            Id = Guid.Parse("4c1d0e9a-0000-4000-a000-000000000001"),
            Title = "Lab",
            DueDate = new DateTimeOffset(2026, 8, 3, 13, 59, 0, TimeSpan.Zero),
            SourceText = "lab",
        };

        var json = ChatCompletionsAssistantService.DraftsJson([draft], Calendar);

        Assert.Equal("""{"tasks":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","title":"Lab","course":null,"due":"2026-08-03T23:59"}]}""", json);
    }

    [Fact]
    public void TextIsSentAsWrittenRatherThanEscaped()
    {
        var draft = new AssistantDraftTask { Title = "Café <draft> & 'notes' + Gym/Pool", DueDate = null, SourceText = "x" };

        var json = ChatCompletionsAssistantService.DraftsJson([draft], Calendar);

        Assert.Contains("Café <draft> & 'notes' + Gym/Pool", json, StringComparison.Ordinal);
    }

    // The model's JSON, read the way the macOS reader reads it

    [Fact]
    public void AnIdWithSpacesAroundItIsNotHonoured()
    {
        var existing = new AssistantDraftTask { Include = false, Title = "Workout", DueDate = null, SourceText = "gym" };
        var reply = $$"""{"tasks":[{"id":" {{existing.Id}} ","title":"Workout","due":null}]}""";

        var revised = Assert.Single(ChatCompletionsAssistantService.ParseRevisedDrafts(reply, [existing], Calendar));

        Assert.NotEqual(existing.Id, revised.Id);
        Assert.True(revised.Include, "Not lined up with the draft, so it is a new one");
    }

    [Fact]
    public void ALowerCaseIdIsHonoured()
    {
        var existing = new AssistantDraftTask { Include = false, Title = "Workout", DueDate = null, SourceText = "gym" };
        var reply = $$"""{"tasks":[{"id":"{{existing.Id.ToString("D").ToLowerInvariant()}}","title":"Workout","due":null}]}""";

        var revised = Assert.Single(ChatCompletionsAssistantService.ParseRevisedDrafts(reply, [existing], Calendar));

        Assert.Equal(existing.Id, revised.Id);
        Assert.False(revised.Include);
    }

    [Fact]
    public void TheFirstOfTwoKeysWithTheSameNameIsTheOneRead()
    {
        const string reply = """{"tasks":[{"title":"First","title":"Second","due":"2026-08-07T17:00"}]}""";

        Assert.Equal("First", Assert.Single(ChatCompletionsAssistantService.ParseDrafts(reply, "x", Calendar)).Title);
    }

    [Fact]
    public void ATrailingCommaInTheReplyIsForgiven()
    {
        const string reply = """{"tasks":[{"title":"Essay","due":"2026-08-07T17:00",},],}""";

        Assert.Single(ChatCompletionsAssistantService.ParseDrafts(reply, "x", Calendar));
    }

    [Fact]
    public void ACourseGivenAsANumberIsNotACourse()
    {
        const string reply = """{"tasks":[{"title":"Essay","course":12345,"due":"2026-08-07T17:00"},{"title":"Quiz","course":"   ","due":null}]}""";

        var drafts = ChatCompletionsAssistantService.ParseDrafts(reply, "x", Calendar);

        Assert.All(drafts, draft => Assert.Null(draft.CourseName));
    }

    [Fact]
    public void TheSentenceAndCourseAreKeptTrimmed()
    {
        var drafts = ChatCompletionsAssistantService.ParseDrafts(
            """{"tasks":[{"title":"  Essay  ","course":"  DESN1500 ","due":"2026-08-07T17:00"}]}""",
            "essay due friday",
            Calendar);

        Assert.Equal("Essay", drafts[0].Title);
        Assert.Equal("DESN1500", drafts[0].CourseName);
        Assert.Equal("essay due friday", drafts[0].SourceText);
    }

    [Fact]
    public void ARevisionKeepsTheSentenceThatStartedTheRound()
    {
        var existing = new AssistantDraftTask { Title = "Workout", DueDate = null, SourceText = "gym monday" };

        var revised = ChatCompletionsAssistantService.ParseRevisedDrafts(
            """{"tasks":[{"title":"Workout","due":null},{"title":"Stretch","due":null}]}""",
            [existing],
            Calendar);

        Assert.All(revised, draft => Assert.Equal("gym monday", draft.SourceText));
    }

    // Text measured in characters, as a person sees them

    [Fact]
    public void ANameIsMeasuredInCharactersNotCodeUnits()
    {
        var accented = string.Concat(Enumerable.Repeat("é", AssistantProfileLibrary.MaximumNameLength));

        Assert.Equal(80, accented.Length);
        Assert.Null(AssistantProfileLibrary.Empty.Validate(accented));
        Assert.Equal(AssistantProfileNameError.TooLong, AssistantProfileLibrary.Empty.Validate(accented + "é"));
    }

    [Fact]
    public void AnAccentTypedTwoWaysIsTheSameName()
    {
        var library = AssistantProfileLibrary.Empty.Add(new AssistantProfile(AssistantService.Groq, name: "Café"));

        Assert.Equal(AssistantProfileNameError.Duplicate, library.Validate("Café"));
        Assert.Equal(AssistantProfileNameError.Duplicate, library.Validate("CAFÉ"));
    }

    [Fact]
    public void AChipTitleIsCutBetweenCharactersNeverInsideOne()
    {
        var coder = "\U0001F469‍\U0001F4BB";
        var context = new AssistantContext(AssistantFixtures.Item(string.Concat(Enumerable.Repeat(coder, 40)), null, AssistantFixtures.Date(2026, 8, 3)));

        var chip = context.ChipTitle;

        Assert.Equal(32, new StringInfo(chip).LengthInTextElements);
        Assert.Equal(string.Concat(Enumerable.Repeat(coder, 31)) + "…", chip);
        Assert.True(chip.EnumerateRunes().All(rune => rune != Rune.ReplacementChar));
    }

    [Fact]
    public void AShortTitleIsLeftWhole()
    {
        var title = new string('A', 32);

        Assert.Equal(title, new AssistantContext(AssistantFixtures.Item(title, null, AssistantFixtures.Date(2026, 8, 3))).ChipTitle);
    }

    // Dates written for people

    [Fact]
    public void TheConfirmationDatesFollowTheCulture()
    {
        var summary = new AssistantSaveSummary
        {
            SavingCount = 2,
            SkippedCount = 0,
            Courses = [],
            WithoutCourseCount = 2,
            Earliest = AssistantFixtures.Date(2026, 8, 8, hour: 23, minute: 59),
            Latest = AssistantFixtures.Date(2026, 8, 15, hour: 23, minute: 59),
            HasUnappliedChange = false,
        };

        Assert.Contains("Due Aug 8 to Aug 15.", summary.DetailLines(Calendar, AssistantFixtures.EnglishUS));
        Assert.Contains("Due 8 Aug to 15 Aug.", summary.DetailLines(Calendar, EnglishAU));
    }

    [Fact]
    public void TheConfirmationReadsDaysOnTheCalendarsClock()
    {
        // One UTC day, two Sydney days: 23:59 on the 8th and half past midnight on the 9th.
        var summary = new AssistantSaveSummary
        {
            SavingCount = 2,
            SkippedCount = 0,
            Courses = [],
            WithoutCourseCount = 2,
            Earliest = new DateTimeOffset(2026, 8, 8, 13, 59, 0, TimeSpan.Zero),
            Latest = new DateTimeOffset(2026, 8, 8, 14, 30, 0, TimeSpan.Zero),
            HasUnappliedChange = false,
        };

        Assert.Contains("Due Aug 8 to Aug 9.", summary.DetailLines(Calendar, AssistantFixtures.EnglishUS));
        Assert.Contains("Due Aug 8.", summary.DetailLines(AppCalendar.Utc, AssistantFixtures.EnglishUS));
    }

    [Fact]
    public void EachDayOfTheConversationIsHeadedOnce()
    {
        var now = AssistantFixtures.Date(2026, 8, 3, hour: 10);
        AssistantMessage Said(int day, int hour, int minute)
            => new() { Role = AssistantMessageRole.User, Text = "q", Date = AssistantFixtures.Date(2026, 8, day, hour, minute) };

        var headings = AssistantConversationLog.DayHeadings(
            [Said(1, 9, 0), Said(2, 23, 30), Said(2, 23, 50), Said(3, 0, 10)],
            now,
            Calendar,
            AssistantFixtures.EnglishUS);

        Assert.Equal(["Sat, Aug 1", "Yesterday", null, "Today"], headings);
    }

    [Fact]
    public void TheFirstMessageShownAlwaysSaysItsDay()
    {
        var now = AssistantFixtures.Date(2026, 8, 3, hour: 10);
        var message = new AssistantMessage { Role = AssistantMessageRole.Assistant, Text = "a", Date = now.AddHours(-1) };

        Assert.Equal(["Today"], AssistantConversationLog.DayHeadings([message], now, Calendar, AssistantFixtures.EnglishUS));
        Assert.Equal(["Sat, 1 Aug"], AssistantConversationLog.DayHeadings(
            [message with { Date = AssistantFixtures.Date(2026, 8, 1) }], now, Calendar, EnglishAU));
    }

    [Fact]
    public void AMessagesTimeIsReadOnTheCalendarsClock()
    {
        var message = new AssistantMessage
        {
            Role = AssistantMessageRole.User,
            Text = "q",
            Date = new DateTimeOffset(2026, 8, 3, 13, 59, 0, TimeSpan.Zero),
        };

        // The gap before "PM" is a plain or a narrow space depending on the
        // machine's culture data, so only the parts are compared.
        var time = message.TimeText(Calendar, AssistantFixtures.EnglishUS);
        Assert.StartsWith("11:59", time, StringComparison.Ordinal);
        Assert.EndsWith("PM", time, StringComparison.Ordinal);
    }

    [Fact]
    public void ADigestCountsCalendarDaysAcrossTheClockChange()
    {
        var now = AssistantFixtures.Date(2026, 10, 3, hour: 12);
        var due = AssistantFixtures.Date(2026, 10, 5, hour: 0, minute: 30);
        var digest = new AssistantAssignmentDigest(AssistantFixtures.Item("Lab report", "COMP2050", due));

        Assert.Equal(TimeSpan.FromHours(35.5), due - now);
        Assert.Equal("- Lab report (COMP2050), due in 2 days", digest.Line(now, Calendar));
    }

    [Fact]
    public void ADigestOfSomethingPastSaysSo()
    {
        var now = AssistantFixtures.Date(2026, 8, 3, hour: 9);
        var digest = new AssistantAssignmentDigest(AssistantFixtures.Item("Quiz", "  ", AssistantFixtures.Date(2026, 8, 1, hour: 9)));

        Assert.Null(digest.CourseName);
        Assert.Equal("- Quiz, due in -2 days", digest.Line(now, Calendar));
    }

    // Sizes written like a file browser

    [Theory]
    [InlineData(0L, "Zero KB")]
    [InlineData(1L, "1 byte")]
    [InlineData(2L, "2 bytes")]
    [InlineData(999L, "999 bytes")]
    [InlineData(1_000L, "1 KB")]
    [InlineData(1_500L, "2 KB")]
    [InlineData(2_500L, "3 KB")]
    [InlineData(999_499L, "999 KB")]
    [InlineData(999_500L, "1 MB")]
    [InlineData(999_999L, "1 MB")]
    [InlineData(1_234_567L, "1.2 MB")]
    [InlineData(1_250_000L, "1.3 MB")]
    [InlineData(1_990_000L, "2 MB")]
    [InlineData(12_345_678L, "12.3 MB")]
    [InlineData(999_949_999L, "999.9 MB")]
    [InlineData(999_950_000L, "1 GB")]
    [InlineData(1_234_000_000L, "1.23 GB")]
    [InlineData(4_700_000_000L, "4.7 GB")]
    [InlineData(4_712_345_678L, "4.71 GB")]
    [InlineData(4_715_000_000L, "4.72 GB")]
    [InlineData(4_995_000_000L, "5 GB")]
    [InlineData(100_500_000_000L, "100.5 GB")]
    [InlineData(999_999_999_999L, "1 TB")]
    [InlineData(1_234_567_890_123L, "1.23 TB")]
    [InlineData(123_456_789_012_345L, "123.46 TB")]
    [InlineData(5_000_000_000_000_000L, "5 PB")]
    [InlineData(1_000_000_000_000_000_000L, "1 EB")]
    [InlineData(long.MaxValue, "9.22 EB")]
    [InlineData(-1L, "-1 byte")]
    [InlineData(-1_500L, "-2 KB")]
    [InlineData(-4_700_000_000L, "-4.7 GB")]
    public void SizesAreWrittenAsAMacFileBrowserWritesThem(long bytes, string expected)
    {
        Assert.Equal(expected, LocalModel.DescribeSize(bytes, AssistantFixtures.EnglishUS));
    }

    [Fact]
    public void SizesUseTheCulturesDecimalMark()
    {
        Assert.Equal("4,7 GB", LocalModel.DescribeSize(4_700_000_000, CultureInfo.GetCultureInfo("de-DE")));
    }

    // The sentence reader, character by character

    [Theory]
    [InlineData("quiz ２", false)]
    [InlineData("quiz 26/8", false)]
    [InlineData("quiz 26 / 8", false)]
    [InlineData("lab 17:00", false)]
    [InlineData("anything for 12345", true)]
    [InlineData("whats due", true)]
    [InlineData("what's due friday", true)]
    [InlineData("am I behind", true)]
    [InlineData("essay 5pm", false)]
    [InlineData("essay pm", false)]
    [InlineData("essay 123pm", false)]
    [InlineData("essay ampm", true)]
    [InlineData("practice may", true)]
    [InlineData("practice ¿?", true)]
    [InlineData("Essay due Friday？", true)]
    public void TheReaderReadsWordsAsMacOSDoes(string sentence, bool question)
    {
        Assert.Equal(question, AssistantRequestReader.ReadsAsQuestion(sentence));
    }
}

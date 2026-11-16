namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// Saving drafts is the one step that leaves the panel and changes the list,
/// and it happens to several tasks at once. It asks first, and what it asks
/// says what is about to happen — including the sentence you typed and never
/// applied, which used to be thrown away silently.
/// </summary>
/// <remarks>
/// The macOS file drives the view model; the tests here are the ones about
/// what the confirmation counts and says, which Core now works out. No test
/// makes a real network request. All fixtures are invented.
/// </remarks>
public sealed class AssistantSaveConfirmationTests
{
    private static AppCalendar Calendar => AssistantFixtures.Sydney;

    private static DateTimeOffset Date(int day) => AssistantFixtures.Date(2026, 8, day, hour: 23, minute: 59);

    /// <summary>The review after drafting "gym monday wednesday".</summary>
    private static AssistantDraftReview Drafted() => AssistantDraftReview.Started(
        [
            new AssistantDraftTask { Title = "Workout 1", DueDate = Date(3), SourceText = "gym monday wednesday" },
            new AssistantDraftTask { Title = "Workout 2", DueDate = Date(5), SourceText = "gym monday wednesday" },
        ],
        "gym monday wednesday")!;

    // Nothing is written on the click

    [Fact]
    public void ThereIsNothingToConfirmWhenNothingWouldBeSaved()
    {
        var review = Drafted();
        foreach (var draft in review.Drafts)
        {
            review = review.Updating(draft with { Include = false });
        }

        Assert.False(review.HasSaveableDrafts);
        Assert.False(review.Summary("").CanSave);
    }

    // What the question counts

    [Fact]
    public void TheCountIgnoresRowsThatWouldNotBeSaved()
    {
        var review = Drafted();
        review = review.Updating(review.Drafts[0] with { Include = false });

        var summary = review.Summary("");

        Assert.Equal(1, summary.SavingCount);
        Assert.Equal(1, summary.SkippedCount);
        Assert.Equal("Add 1 task?", summary.Title);
    }

    [Fact]
    public void ARowWithNoDateIsCountedAsLeftOut()
    {
        var review = Drafted();
        review = review.Updating(review.Drafts[0] with { DueDate = null });

        var summary = review.Summary("");

        Assert.Equal(1, summary.SavingCount);
        Assert.Equal(1, summary.SkippedCount);
    }

    /// <summary>
    /// The mistake this exists to catch: a follow-up typed but never applied,
    /// then Save clicked. Saving would drop the sentence without a word.
    /// </summary>
    [Fact]
    public void ATypedChangeThatWasNeverAppliedIsCalledOut()
    {
        var review = Drafted();

        Assert.False(review.Summary("").HasUnappliedChange);

        var summary = review.Summary("put them under 41021");

        Assert.True(summary.HasUnappliedChange);
        // It is said first, because it is the surprise.
        Assert.Equal(
            "You typed a change that has not been applied yet. Saving now leaves it out.",
            summary.DetailLines(Calendar, AssistantFixtures.EnglishUS)[0]);
    }

    [Fact]
    public void WhitespaceInTheFollowUpFieldIsNotAChange()
    {
        Assert.False(Drafted().Summary("   \n ").HasUnappliedChange);
    }

    [Fact]
    public void TheCourseIsNamedSoItCanBeChecked()
    {
        var review = Drafted();
        foreach (var draft in review.Drafts)
        {
            review = review.Updating(draft with { CourseName = "41021 Interaction Design Studio" });
        }

        var lines = review.Summary("").DetailLines(Calendar, AssistantFixtures.EnglishUS);

        Assert.Contains("Under 41021 Interaction Design Studio.", lines);
    }

    // The wording itself

    [Fact]
    public void TheButtonRepeatsTheNumber()
    {
        Assert.Equal("Add 1 Task", Summary(saving: 1).ConfirmTitle);
        Assert.Equal("Add 6 Tasks", Summary(saving: 6).ConfirmTitle);
        Assert.Equal("Add 1 task?", Summary(saving: 1).Title);
        Assert.Equal("Add 6 tasks?", Summary(saving: 6).Title);
    }

    [Fact]
    public void TheWayBackIsMentioned()
    {
        var lines = Summary(saving: 3).DetailLines(Calendar, AssistantFixtures.EnglishUS);

        Assert.Equal("They are added as manual events, and can be undone.", lines[^1]);
    }

    [Fact]
    public void ASingleDayIsNotShownAsARange()
    {
        var lines = Summary(saving: 2, earliest: Date(8), latest: Date(8)).DetailLines(Calendar, AssistantFixtures.EnglishUS);

        Assert.Contains(lines, line => line.StartsWith("Due ", StringComparison.Ordinal) && !line.Contains(" to ", StringComparison.Ordinal));
    }

    [Fact]
    public void ASpanOfDaysIsShownAsARange()
    {
        var lines = Summary(saving: 2, earliest: Date(8), latest: Date(15)).DetailLines(Calendar, AssistantFixtures.EnglishUS);

        Assert.Contains(lines, line => line.StartsWith("Due ", StringComparison.Ordinal) && line.Contains(" to ", StringComparison.Ordinal));
    }

    [Fact]
    public void TasksWithoutACourseAreCounted()
    {
        var lines = Summary(saving: 3, courses: ["41021"], withoutCourse: 2).DetailLines(Calendar, AssistantFixtures.EnglishUS);

        Assert.Contains("Under 41021, and 2 without a course.", lines);
    }

    [Fact]
    public void NothingIsSaidAboutCoursesWhenThereAreNone()
    {
        var lines = Summary(saving: 2).DetailLines(Calendar, AssistantFixtures.EnglishUS);

        Assert.DoesNotContain(lines, line => line.Contains("Under ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptySelectionCannotBeSaved()
    {
        Assert.False(Summary(saving: 0).CanSave);
        Assert.True(Summary(saving: 1).CanSave);
    }

    private static AssistantSaveSummary Summary(
        int saving,
        int skipped = 0,
        ValueList<string> courses = default,
        int withoutCourse = 0,
        DateTimeOffset? earliest = null,
        DateTimeOffset? latest = null,
        bool unapplied = false)
        => new()
        {
            SavingCount = saving,
            SkippedCount = skipped,
            Courses = courses,
            WithoutCourseCount = withoutCourse,
            Earliest = earliest,
            Latest = latest,
            HasUnappliedChange = unapplied,
        };
}

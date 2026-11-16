namespace CanvasCountdown.Tests.Screenshots;

/// <summary>
/// Row grouping and candidate building. All fixtures are invented: no real
/// course, assignment or screenshot appears here.
/// </summary>
/// <remarks>
/// Boxes are top-left and normalised to 0–1, the convention
/// <see cref="OcrTextObservation"/> uses. The macOS fixtures were already in
/// it — the macOS recogniser flipped Vision's bottom-left boxes before the
/// parser saw them — so every number below is the macOS one, unchanged.
/// </remarks>
public sealed class CanvasScreenshotParserTests
{
    private static readonly AppCalendar Calendar = AppCalendar.ForTimeZone("Australia/Sydney");

    private readonly Guid screenshotId = Guid.NewGuid();
    private readonly CanvasScreenshotParser parser = new();

    private static DateTimeOffset ReferenceNow => Calendar.FromLocal(2026, 7, 28, 9, 0);

    /// <summary>
    /// An observation at a given vertical position, expressed as fractions so
    /// zoom and display scaling are irrelevant.
    /// </summary>
    private OcrTextObservation Line(
        string text,
        double y,
        double x = 0.08,
        double width = 0.5,
        double height = 0.012,
        double confidence = 0.92)
        => new()
        {
            Text = text,
            Confidence = confidence,
            BoundingBox = new NormalisedRect(x, y, width, height),
            ScreenshotId = screenshotId,
        };

    private ValueList<ScreenshotImportCandidate> Parse(IReadOnlyList<OcrTextObservation> observations)
        => parser.Candidates(observations, "example-screenshot.png", ReferenceNow, Calendar);

    private static DateTime Local(DateTimeOffset instant) => Calendar.ToLocal(instant);

    // Single assignment

    [Fact]
    public void OneAssignmentWithAvailabilityAndDueLines()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10),
            Line("Not available until 4 Aug at 14:40", y: 0.125),
            Line("Due 4 Aug at 16:00", y: 0.145),
        ]);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Example Quiz", candidate.Title);

        Assert.NotNull(candidate.DueDate);
        var parts = Local(candidate.DueDate.Value);
        Assert.Equal(8, parts.Month);
        Assert.Equal(4, parts.Day);
        Assert.True(parts.Hour == 16, "16:00 is the deadline; 14:40 is only when it opens");
        Assert.Equal(0, parts.Minute);
    }

    [Fact]
    public void AvailabilityLineNeverBecomesTheDeadline()
    {
        var candidates = Parse(
        [
            Line("Example Poster Submission", y: 0.10),
            Line("Not available until 16 Aug at 23:59", y: 0.125),
            Line("Due 30 Aug at 23:59", y: 0.145),
        ]);

        var due = candidates.FirstOrDefault()?.DueDate;
        Assert.NotNull(due);
        Assert.True(Local(due.Value).Day == 30, "The availability date must never be imported as the deadline");
    }

    // Several assignments

    [Fact]
    public void SeveralAssignmentsInOneScreenshot()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10),
            Line("Not available until 4 Aug at 14:40", y: 0.125),
            Line("Due 4 Aug at 16:00", y: 0.145),
            Line("Example Poster Submission", y: 0.22),
            Line("Not available until 16 Aug at 23:59", y: 0.245),
            Line("Due 30 Aug at 23:59", y: 0.265),
            Line("Example Reflection", y: 0.34),
            Line("Due 6 Sep at 23:59", y: 0.365),
        ]);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(
            ["Example Quiz", "Example Poster Submission", "Example Reflection"],
            candidates.Select(candidate => candidate.Title));
    }

    [Fact]
    public void AssignmentsSeparateEvenWhenTightlySpaced()
    {
        // No large gap between blocks: the split comes from a new title
        // following a due line.
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.100),
            Line("Due 4 Aug at 16:00", y: 0.120),
            Line("Example Reflection", y: 0.140),
            Line("Due 6 Sep at 23:59", y: 0.160),
        ]);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("Example Quiz", candidates[0].Title);
        Assert.Equal("Example Reflection", candidates[1].Title);
    }

    [Fact]
    public void WrappedTitleIsJoined()
    {
        var candidates = Parse(
        [
            Line("Example Poster Submission With A Very Long", y: 0.10),
            Line("Title That Wraps Onto A Second Line", y: 0.118),
            Line("Due 30 Aug at 23:59", y: 0.140),
        ]);

        var candidate = Assert.Single(candidates);
        Assert.Equal(
            "Example Poster Submission With A Very Long Title That Wraps Onto A Second Line",
            candidate.Title);
    }

    [Fact]
    public void ReadingOrderIsLeftToRightWithinARow()
    {
        var candidates = Parse(
        [
            Line("Second", y: 0.101, x: 0.40, width: 0.2),
            Line("First", y: 0.100, x: 0.08, width: 0.2),
            Line("Due 4 Aug at 16:00", y: 0.130),
        ]);

        Assert.Equal("First Second", Assert.Single(candidates).Title);
    }

    [Fact]
    public void BrowserZoomDoesNotChangeGrouping()
    {
        // The same layout at a different scale: fractions are unchanged, so the
        // result must be too.
        var tight = Parse(
        [
            Line("Example Quiz", y: 0.10, height: 0.008),
            Line("Due 4 Aug at 16:00", y: 0.115, height: 0.008),
            Line("Example Reflection", y: 0.20, height: 0.008),
            Line("Due 6 Sep at 23:59", y: 0.215, height: 0.008),
        ]);
        var loose = Parse(
        [
            Line("Example Quiz", y: 0.10, height: 0.02),
            Line("Due 4 Aug at 16:00", y: 0.128, height: 0.02),
            Line("Example Reflection", y: 0.24, height: 0.02),
            Line("Due 6 Sep at 23:59", y: 0.268, height: 0.02),
        ]);

        Assert.Equal(2, tight.Count);
        Assert.Equal(2, loose.Count);
        Assert.Equal(tight.Select(candidate => candidate.Title), loose.Select(candidate => candidate.Title));
    }

    // Page furniture

    [Fact]
    public void GlobalPageTextIsIgnored()
    {
        var candidates = Parse(
        [
            Line("Upcoming Assignments", y: 0.02),
            Line("Show By Date", y: 0.04),
            Line("Search", y: 0.05),
            Line("Example Quiz", y: 0.10),
            Line("Due 4 Aug at 16:00", y: 0.125),
        ]);

        Assert.Single(candidates);
        Assert.Equal("Example Quiz", candidates.First().Title);
    }

    [Fact]
    public void PointValuesAreNotTreatedAsDatesOrTitles()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10),
            Line("10 pts", y: 0.122),
            Line("Due 4 Aug at 16:00", y: 0.142),
        ]);

        var candidate = candidates.First();
        Assert.Equal("Example Quiz", candidate.Title);
        Assert.NotNull(candidate.DueDate);
    }

    // Warnings

    [Fact]
    public void BlockWithoutADueLineIsKeptAndFlagged()
    {
        var candidates = Parse(
        [
            Line("Example Ungraded Task", y: 0.10),
            Line("Not available until 4 Aug at 14:40", y: 0.125),
        ]);

        var candidate = candidates.First();
        Assert.True(candidate.DueDate is null, "No deadline may be invented");
        Assert.Contains(ScreenshotImportWarning.MissingDueDate, candidate.Warnings);
        Assert.Equal(ScreenshotCandidateStatus.Invalid, candidate.Status);
        Assert.False(candidate.CanBeImported);
    }

    [Fact]
    public void TwoDueLinesInOneBlockAreMarkedAmbiguous()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10),
            Line("Due 4 Aug at 16:00", y: 0.122),
            Line("Due 5 Aug at 16:00", y: 0.140),
        ]);

        var candidate = candidates.First();
        Assert.Contains(ScreenshotImportWarning.MultipleDueDates, candidate.Warnings);
        Assert.Equal(ScreenshotCandidateStatus.NeedsReview, candidate.Status);
    }

    [Fact]
    public void InferredYearIsCarriedOntoTheCandidate()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10),
            Line("Due 4 Aug at 16:00", y: 0.125),
        ]);

        var candidate = candidates.First();
        Assert.Equal(2026, candidate.InferredYear);
        Assert.Contains(ScreenshotImportWarning.YearInferred(2026), candidate.Warnings);
        Assert.Equal(ScreenshotCandidateStatus.NeedsReview, candidate.Status);
    }

    [Fact]
    public void LowConfidenceIsFlagged()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10, confidence: 0.3),
            Line("Due 4 Aug at 16:00", y: 0.125, confidence: 0.35),
        ]);

        Assert.Contains(ScreenshotImportWarning.LowConfidence, candidates.First().Warnings);
    }

    [Fact]
    public void OriginalTextIsPreservedForReview()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10),
            Line("Due 4 Aug al 16:00", y: 0.125),
        ]);

        var candidate = candidates.First();
        Assert.True(
            candidate.OriginalDueText == "Due 4 Aug al 16:00",
            "The review screen shows exactly what was recognised");
        Assert.NotNull(candidate.CorrectedOcrText);
    }

    [Fact]
    public void BoundingRegionCoversTheWholeBlock()
    {
        var candidates = Parse(
        [
            Line("Example Quiz", y: 0.10, height: 0.012),
            Line("Due 4 Aug at 16:00", y: 0.130, height: 0.012),
        ]);

        var region = candidates.First().BoundingRegion;
        Assert.True(region.MinY <= 0.10);
        Assert.True(region.MaxY >= 0.142);
    }

    [Fact]
    public void EmptyInputProducesNoCandidates()
    {
        Assert.Empty(Parse([]));
        Assert.Empty(Parse([Line("Upcoming Assignments", y: 0.02)]));
    }
}

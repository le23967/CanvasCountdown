using Microsoft.Extensions.Time.Testing;

namespace CanvasCountdown.Tests.Screenshots;

/// <summary>
/// The import workflow: recognition boundary, duplicates, review validation,
/// committing, and the privacy promises. Recognition itself is stubbed, because
/// recognition output varies between operating-system versions.
/// </summary>
public sealed class ScreenshotImportWorkflowTests
{
    private static readonly AppCalendar Calendar = AppCalendar.ForTimeZone("Australia/Sydney");

    private readonly Guid screenshotId = Guid.NewGuid();

    private static DateTimeOffset ReferenceNow => Calendar.FromLocal(2026, 7, 28, 9, 0);

    private OcrTextObservation Line(string text, double y)
        => new()
        {
            Text = text,
            Confidence = 0.9,
            BoundingBox = new NormalisedRect(0.08, y, 0.5, 0.012),
            ScreenshotId = screenshotId,
        };

    private ScreenshotSource Source()
        => new() { Id = screenshotId, DisplayName = "example-screenshot.png", ImageData = BlankImage() };

    /// <summary>Never decoded: the stub recogniser only reads the name.</summary>
    private static byte[] BlankImage() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private (ScreenshotImportCoordinator Coordinator, ScreenshotTestRepository Repository) MakeCoordinator(
        IEnumerable<OcrTextObservation> observations,
        ScreenshotTestRepository? repository = null)
    {
        repository ??= new ScreenshotTestRepository();
        return (new ScreenshotImportCoordinator(StubOcrService.Returning(observations), repository), repository);
    }

    // Recognition boundary

    [Fact]
    public async Task SuccessfulRecognitionProducesCandidates()
    {
        var (coordinator, _) = MakeCoordinator(
        [
            Line("Example Quiz", 0.10),
            Line("Not available until 4 Aug at 14:40", 0.125),
            Line("Due 4 Aug at 16:00", 0.145),
        ]);

        var session = await coordinator.BuildSessionAsync([Source()], [], ReferenceNow, Calendar);

        Assert.Single(session.Candidates);
        Assert.Equal("Example Quiz", session.Candidates.First().Title);
    }

    [Fact]
    public async Task NoTextIsReportedRatherThanFailing()
    {
        var (coordinator, _) = MakeCoordinator([]);

        var session = await coordinator.BuildSessionAsync([Source()], [], ReferenceNow, Calendar);

        Assert.Empty(session.Candidates);
        Assert.Equal(["example-screenshot.png"], session.RecognisedNothingNames);
        Assert.Empty(session.FailedImageNames);
    }

    [Fact]
    public async Task UnreadableImageIsReportedWithoutLosingOthers()
    {
        var good = Source();
        var bad = new ScreenshotSource { DisplayName = "broken.png", ImageData = BlankImage() };
        var coordinator = new ScreenshotImportCoordinator(
            StubOcrService.Returning(
                [Line("Example Quiz", 0.10), Line("Due 4 Aug at 16:00", 0.125)],
                failingNames: ["broken.png"]),
            new ScreenshotTestRepository());

        var session = await coordinator.BuildSessionAsync([good, bad], [], ReferenceNow, Calendar);

        Assert.True(session.Candidates.Count == 1, "The good screenshot survives");
        Assert.Equal(["broken.png"], session.FailedImageNames);
    }

    [Fact]
    public async Task CancellationKeepsWhateverFinished()
    {
        var coordinator = new ScreenshotImportCoordinator(
            StubOcrService.Throwing(ScreenshotOcrError.Cancelled),
            new ScreenshotTestRepository());

        var session = await coordinator.BuildSessionAsync([Source()], [], ReferenceNow, Calendar);

        Assert.Empty(session.Candidates);
    }

    [Fact]
    public async Task SeveralScreenshotsAreCombined()
    {
        var first = Source();
        var second = new ScreenshotSource { DisplayName = "second.png", ImageData = BlankImage() };
        var coordinator = new ScreenshotImportCoordinator(
            StubOcrService.Returning([Line("Example Quiz", 0.10), Line("Due 4 Aug at 16:00", 0.125)]),
            new ScreenshotTestRepository());

        var session = await coordinator.BuildSessionAsync([first, second], [], ReferenceNow, Calendar);

        Assert.Equal(2, session.Candidates.Count);
        Assert.Equal(2, session.ScreenshotNames.Count);
    }

    // Duplicates

    private static AssignmentSnapshot StoredAssignment(
        string title,
        int day,
        string? course = null,
        bool isCompleted = false,
        bool isIgnored = false,
        AssignmentSource source = AssignmentSource.CanvasCalendarFeed)
        => new()
        {
            Title = title,
            CourseName = course,
            DueDate = Calendar.FromLocal(2026, 8, day, 16, 0),
            Source = source,
            IsCompleted = isCompleted,
            IsIgnored = isIgnored,
        };

    private ScreenshotImportCandidate Candidate(
        string title,
        DateTimeOffset? dueDate,
        string originalDueText,
        double parserConfidence = 0.9,
        bool include = true,
        ValueList<ScreenshotImportWarning> warnings = default)
        => new()
        {
            SourceScreenshotId = screenshotId,
            SourceImageName = "example.png",
            Include = include,
            Title = title,
            DueDate = dueDate,
            OriginalTitleText = title,
            OriginalDueText = originalDueText,
            OcrConfidence = 0.9,
            ParserConfidence = parserConfidence,
            Warnings = warnings,
        };

    [Fact]
    public async Task ExactDuplicateIsMarkedAndDefaultsToSkip()
    {
        var existing = StoredAssignment("Example Quiz", day: 4);
        var (coordinator, _) = MakeCoordinator([Line("Example Quiz", 0.10), Line("Due 4 Aug at 16:00", 0.125)]);

        var session = await coordinator.BuildSessionAsync([Source()], [existing], ReferenceNow, Calendar);
        var candidate = session.Candidates.FirstOrDefault();

        Assert.NotNull(candidate);
        Assert.True(candidate.PossibleDuplicate);
        Assert.Equal(existing.Id, candidate.DuplicateTargetId);
        Assert.True(
            candidate.SelectedDuplicateAction == DuplicateResolution.Skip,
            "Skipping is the choice that cannot lose anything");
        Assert.Equal(ScreenshotCandidateStatus.PossibleDuplicate, candidate.Status);
        Assert.False(candidate.WillBeImported);
    }

    [Fact]
    public void SpacingAndPunctuationDifferencesStillMatch()
    {
        var existing = StoredAssignment("Example  Quiz:  Week 1", day: 4);
        var candidate = Candidate("Example Quiz Week 1", existing.DueDate, "Due 4 Aug at 16:00");

        var annotated = ScreenshotDuplicateDetector.Annotate([candidate], [existing], Calendar);

        Assert.True(annotated[0].PossibleDuplicate);
    }

    [Fact]
    public void SameTitleOnADifferentDayIsNotADuplicate()
    {
        var existing = StoredAssignment("Example Quiz", day: 4);
        var candidate = Candidate("Example Quiz", Calendar.FromLocal(2026, 8, 11, 16, 0), "Due 11 Aug at 16:00");

        var annotated = ScreenshotDuplicateDetector.Annotate([candidate], [existing], Calendar);

        Assert.False(annotated[0].PossibleDuplicate, "A weekly task sharing a name is not the same assignment");
    }

    [Fact]
    public void DifferentTitlesOnTheSameDayAreNotDuplicates()
    {
        var existing = StoredAssignment("Example Quiz", day: 4);
        var candidate = Candidate("Example Poster Submission", existing.DueDate, "Due 4 Aug at 16:00");

        var annotated = ScreenshotDuplicateDetector.Annotate([candidate], [existing], Calendar);

        Assert.False(annotated[0].PossibleDuplicate);
    }

    [Fact]
    public void CompletedAndIgnoredStateIsNeverTouchedByDetection()
    {
        var completed = StoredAssignment("Example Quiz", day: 4, isCompleted: true);
        var ignored = StoredAssignment("Example Reflection", day: 5, isIgnored: true);
        ValueList<ScreenshotImportCandidate> candidates =
        [
            Candidate("Example Quiz", completed.DueDate, "Due 4 Aug at 16:00"),
        ];

        var annotated = ScreenshotDuplicateDetector.Annotate(candidates, [completed, ignored], Calendar);

        Assert.True(annotated[0].PossibleDuplicate);
        Assert.True(completed.IsCompleted, "Detection reads, it never writes");
        Assert.True(ignored.IsIgnored);
    }

    [Fact]
    public async Task ExistingCanvasFeedEventIsFlaggedNotOverwritten()
    {
        var feedEvent = StoredAssignment("Example Quiz", day: 4, source: AssignmentSource.CanvasCalendarFeed);
        var (coordinator, repository) = MakeCoordinator(
            [Line("Example Quiz", 0.10), Line("Due 4 Aug at 16:00", 0.125)],
            new ScreenshotTestRepository([feedEvent]));

        var session = await coordinator.BuildSessionAsync([Source()], [feedEvent], ReferenceNow, Calendar);
        var commit = await coordinator.CommitAsync(session.Candidates, ReferenceNow);

        Assert.Equal(0, commit.Summary.Added);
        Assert.Equal(1, commit.Summary.Skipped);
        var stored = await repository.FetchAllAsync();
        Assert.True(stored.Count == 1, "Nothing was added beside the feed event");
        Assert.Equal(AssignmentSource.CanvasCalendarFeed, stored[0].Source);
    }

    // Review validation and commit

    [Fact]
    public async Task OnlySelectedValidRowsAreImported()
    {
        var (coordinator, repository) = MakeCoordinator([]);
        var due = Calendar.FromLocal(2026, 8, 4, 16, 0);

        var ready = Candidate("Example Quiz", due, "Due 4 Aug at 16:00");
        var deselected = Candidate("Example Poster Submission", due, "Due 4 Aug at 16:00", include: false);
        var invalid = Candidate(
            "Example Ungraded Task",
            dueDate: null,
            originalDueText: "",
            parserConfidence: 0,
            warnings: [ScreenshotImportWarning.MissingDueDate]);
        ready = ready with { Include = true };

        var commit = await coordinator.CommitAsync([ready, deselected, invalid], ReferenceNow);

        Assert.Equal(1, commit.Summary.Added);
        Assert.True(commit.Summary.Skipped == 2, "Deselected and invalid rows are skipped");
        var stored = await repository.FetchAllAsync();
        Assert.Equal(["Example Quiz"], stored.Select(snapshot => snapshot.Title));
        Assert.Equal(AssignmentSource.Screenshot, stored[0].Source);
    }

    [Fact]
    public void InvalidCandidateCannotBeImported()
    {
        var candidate = Candidate(
            "Example Ungraded Task",
            dueDate: null,
            originalDueText: "",
            parserConfidence: 0,
            warnings: [ScreenshotImportWarning.MissingDueDate]);

        Assert.Equal(ScreenshotCandidateStatus.Invalid, candidate.Status);
        Assert.False(candidate.CanBeImported);
        Assert.False(candidate.WillBeImported);
    }

    [Fact]
    public void CorrectedCandidateBecomesReady()
    {
        var candidate = Candidate(
            "Example Quiz",
            dueDate: null,
            originalDueText: "",
            parserConfidence: 0,
            warnings: [ScreenshotImportWarning.MissingDueDate]);
        Assert.Equal(ScreenshotCandidateStatus.Invalid, candidate.Status);

        candidate = candidate with
        {
            DueDate = ReferenceNow,
            Warnings = candidate.Warnings.RemoveAll(warning => warning == ScreenshotImportWarning.MissingDueDate),
        };

        Assert.Equal(ScreenshotCandidateStatus.Ready, candidate.Status);
        Assert.True(candidate.WillBeImported);
    }

    [Fact]
    public async Task ReimportingTheSameScreenshotUpdatesRatherThanDuplicates()
    {
        var (coordinator, repository) = MakeCoordinator([]);
        var due = Calendar.FromLocal(2026, 8, 4, 16, 0);
        var candidate = Candidate("Example Quiz", due, "Due 4 Aug at 16:00");

        await coordinator.CommitAsync([candidate], ReferenceNow);
        await coordinator.CommitAsync([candidate], ReferenceNow);

        var stored = await repository.FetchAllAsync();
        Assert.True(stored.Count == 1, "A stable local identifier keeps a re-import from duplicating");
    }

    [Fact]
    public void LocalIdentifierIsDerivedOnlyFromConfirmedFields()
    {
        var identifier = ScreenshotImportCoordinator.LocalIdentifier(
            "Example Quiz",
            ReferenceNow,
            "99999 Example Interaction Course");

        Assert.StartsWith("screenshot|", identifier, StringComparison.Ordinal);
        Assert.Contains("example quiz", identifier, StringComparison.Ordinal);
        Assert.False(
            identifier.Contains("Due 4 Aug", StringComparison.Ordinal),
            "No recognised transcript may reach a stored identifier");
    }

    // Privacy

    [Fact]
    public async Task NoScreenshotBytesReachStorage()
    {
        var (coordinator, repository) = MakeCoordinator([]);
        var candidate = Candidate("Example Quiz", ReferenceNow, "Due 4 Aug at 16:00") with
        {
            SourceImageName = "example-screenshot.png",
        };

        await coordinator.CommitAsync([candidate], ReferenceNow);
        var stored = await repository.FetchAllAsync();
        var record = Assert.Single(stored);

        // The image name and the recognised line are both absent from every
        // stored field.
        string[] fields = [record.Title, record.CourseName ?? "", record.SourceUrl ?? "", record.ExternalId ?? ""];
        foreach (var field in fields)
        {
            Assert.DoesNotContain("example-screenshot.png", field, StringComparison.Ordinal);
            Assert.DoesNotContain("Due 4 Aug at 16:00", field, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DiscardingReleasesEveryRetainedImage()
    {
        var viewModel = new ScreenshotImportViewModel(
            new ScreenshotImportCoordinator(StubOcrService.Returning([]), new ScreenshotTestRepository()),
            () => Task.FromResult<IReadOnlyList<AssignmentSnapshot>>([]),
            new FakeTimeProvider(ReferenceNow),
            () => Calendar);

        viewModel.Discard();

        Assert.True(viewModel.RetainedImageCount == 0, "Images must not outlive the review session");
        Assert.Empty(viewModel.Candidates);
    }

    [Fact]
    public void SupportedFileTypesAreLimitedToImages()
    {
        Assert.True(ScreenshotImportViewModel.IsSupported("/tmp/a.png"));
        Assert.True(ScreenshotImportViewModel.IsSupported("/tmp/a.jpeg"));
        Assert.True(ScreenshotImportViewModel.IsSupported("/tmp/a.heic"));
        Assert.True(ScreenshotImportViewModel.IsSupported("/tmp/a.tiff"));
        Assert.False(ScreenshotImportViewModel.IsSupported("/tmp/a.pdf"));
        Assert.False(ScreenshotImportViewModel.IsSupported("/tmp/a.txt"));
    }

    [Fact]
    public void SessionSummaryCounts()
    {
        var due = ReferenceNow;
        ScreenshotImportCandidate Row(
            string title,
            DateTimeOffset? dueDate,
            ValueList<ScreenshotImportWarning> warnings = default,
            bool duplicate = false)
            => new()
            {
                SourceScreenshotId = screenshotId,
                SourceImageName = "example.png",
                Title = title,
                DueDate = dueDate,
                OriginalTitleText = title,
                OriginalDueText = "",
                OcrConfidence = 0.9,
                ParserConfidence = 0.9,
                Warnings = warnings,
                PossibleDuplicate = duplicate,
            };

        var session = new ScreenshotImportSession
        {
            Candidates =
            [
                Row("Ready", due),
                Row("Needs review", due, warnings: [ScreenshotImportWarning.YearInferred(2026)]),
                Row("Invalid", null, warnings: [ScreenshotImportWarning.MissingDueDate]),
                Row("Duplicate", due, duplicate: true),
            ],
        };

        Assert.Equal(1, session.ReadyCount);
        Assert.Equal(1, session.NeedsReviewCount);
        Assert.Equal(1, session.InvalidCount);
        Assert.Equal(1, session.DuplicateCount);
        Assert.True(session.ImportableCount == 2, "The duplicate defaults to skip and the invalid row cannot import");
    }
}

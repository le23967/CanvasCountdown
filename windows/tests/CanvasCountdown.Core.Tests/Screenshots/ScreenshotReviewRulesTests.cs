using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using static CanvasCountdown.Tests.Screenshots.ScreenshotFixtures;

namespace CanvasCountdown.Tests.Screenshots;

/// <summary>
/// Not in the macOS suite: the review rules, tested through the view model the
/// Windows sheet binds to, and the undo every batch save must have. Each test
/// fails if its rule is broken.
/// </summary>
public sealed class ScreenshotReviewRulesTests : IDisposable
{
    private readonly TemporaryFiles files = new();

    public void Dispose() => files.Dispose();

    private static ScreenshotImportViewModel MakeViewModel(IScreenshotOcrServicing ocr, ScreenshotTestRepository repository)
        => new(
            new ScreenshotImportCoordinator(ocr, repository),
            () => repository.FetchAllAsync(),
            new FakeTimeProvider(ReferenceNow),
            () => Sydney);

    private static AssignmentSnapshot FeedQuiz() => new()
    {
        ExternalId = "event-assignment-101",
        Title = "Example Quiz",
        DueDate = Sydney.FromLocal(2026, 8, 4, 16, 0),
        Source = AssignmentSource.CanvasCalendarFeed,
    };

    /// <summary>A row as the review leaves it: included, dated, nothing outstanding.</summary>
    private static ScreenshotImportCandidate Reviewed(string title, int day, int hour) => new()
    {
        SourceScreenshotId = Guid.NewGuid(),
        SourceImageName = "example-screenshot.png",
        Title = title,
        DueDate = Sydney.FromLocal(2026, 8, day, hour, 0),
        OriginalTitleText = title,
        OriginalDueText = "",
        OcrConfidence = 0.9,
        ParserConfidence = 1,
    };

    // Nothing recognised is saved without the user reviewing it.

    [Fact]
    public async Task ReadingAScreenshotWritesNothing()
    {
        var repository = new ScreenshotTestRepository([FeedQuiz()]);
        var stored = await repository.FetchAllAsync();
        var coordinator = new ScreenshotImportCoordinator(StubOcrService.Returning(QuizAndReflection), repository);
        var source = new ScreenshotSource { DisplayName = "example-screenshot.png", ImageData = new byte[] { 1 } };

        var session = await coordinator.BuildSessionAsync([source], stored, ReferenceNow, Sydney);

        Assert.Equal(2, session.Candidates.Count);
        Assert.Equal(0, repository.WriteCount);
        Assert.Equal(stored, await repository.FetchAllAsync());
    }

    [Fact]
    public async Task TheReviewIsTheOnlyWayIn()
    {
        var repository = new ScreenshotTestRepository();
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), repository);
        Assert.False(viewModel.ImportSelectedCommand.CanExecute(null));

        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);

        Assert.True(viewModel.IsReviewing);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.True(repository.WriteCount == 0, "Recognition stops at the review");
        Assert.True(viewModel.ImportSelectedCommand.CanExecute(null));
        Assert.Equal("Import 2 Selected", viewModel.ImportButtonTitle);

        await viewModel.ImportSelectedAsync();

        Assert.Equal(1, repository.WriteCount);
        Assert.True(viewModel.IsFinished);
        Assert.Equal("2 added, 0 updated", viewModel.FinishedMessage);
        Assert.Equal(["Example Quiz", "Example Reflection"], (await repository.FetchAllAsync()).Select(row => row.Title));

        viewModel.Discard();
        Assert.True(viewModel.LastCommit?.Added.Count == 2, "The way back outlives the sheet");
    }

    [Fact]
    public void ImportWaitsUntilTheReviewIsOnScreen() => UiThread.Run(async () =>
    {
        var repository = new ScreenshotTestRepository();
        var ocr = new GatedOcrService(QuizAndReflection, heldNames: ["second.png"]);
        var viewModel = MakeViewModel(ocr, repository);
        await viewModel.LoadAsync([files.Write("first.png")]);

        var reading = viewModel.LoadAsync([files.Write("second.png")]);
        await ocr.Holding;

        Assert.True(viewModel.IsRecognising);
        Assert.True(viewModel.CanImport, "The rows from the first screenshot are still there");
        Assert.False(viewModel.ImportSelectedCommand.CanExecute(null));
        await viewModel.ImportSelectedAsync();
        Assert.Equal(0, repository.WriteCount);

        ocr.Release();
        await reading;

        Assert.True(viewModel.IsReviewing);
        Assert.Equal(4, viewModel.Candidates.Count);
        Assert.True(viewModel.ImportSelectedCommand.CanExecute(null));
    });

    [Fact]
    public async Task OnlyRowsLeftIncludedAreSaved()
    {
        var repository = new ScreenshotTestRepository();
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), repository);
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);

        viewModel.Rows[1].Include = false;
        Assert.Equal("Import 1 Selected", viewModel.ImportButtonTitle);
        await viewModel.ImportSelectedAsync();

        Assert.Equal(["Example Quiz"], (await repository.FetchAllAsync()).Select(row => row.Title));
        Assert.Equal("1 added, 0 updated, 1 skipped", viewModel.FinishedMessage);
    }

    [Fact]
    public async Task ABatchIsNotWrittenWhenItsWayBackCannotBeKnown()
    {
        var repository = new ScreenshotTestRepository();
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), repository);
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);

        repository.FailsToRead = true;
        await viewModel.ImportSelectedAsync();

        Assert.Equal(0, repository.WriteCount);
        Assert.True(viewModel.IsReviewing, "The review stays on screen to try again");
        Assert.Equal("The selected events could not be imported.", viewModel.ErrorMessage);
        Assert.Null(viewModel.LastCommit);
    }

    // A row with no visible "Due" line is kept for review, never given a deadline.

    [Fact]
    public async Task ARowWithNoDueLineWaitsForADateRatherThanBeingGivenOne()
    {
        var repository = new ScreenshotTestRepository();
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndUngradedTask), repository);
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);

        var ungraded = viewModel.Rows[1];
        Assert.Equal("Example Ungraded Task", ungraded.Title);
        Assert.False(ungraded.HasDueDate);
        Assert.Null(ungraded.DueDay);
        Assert.Equal("Invalid", ungraded.StatusTitle);
        Assert.False(ungraded.CanBeImported);
        var warning = Assert.Single(ungraded.Warnings);
        Assert.Equal("No due date was found. Add one before importing.", warning.Message);
        Assert.True(warning.IsBlocking);
        Assert.Equal(1, viewModel.ImportableCount);

        await viewModel.ImportSelectedAsync();

        Assert.Equal(["Example Quiz"], (await repository.FetchAllAsync()).Select(row => row.Title));
    }

    [Fact]
    public async Task GivingTheRowADateAnswersItsWarning()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndUngradedTask), new ScreenshotTestRepository());
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        var ungraded = viewModel.Rows[1];

        ungraded.SetDueDateCommand.Execute(null);

        Assert.Same(ungraded, viewModel.Rows[1]);
        Assert.Equal(ReferenceNow.AddSeconds(86_400), ungraded.DueDate);
        Assert.Empty(ungraded.Warnings);
        Assert.Equal(ScreenshotCandidateStatus.Ready, ungraded.Status);
        Assert.Equal("Import 2 Selected", viewModel.ImportButtonTitle);
    }

    // A year that was not visible is inferred, and marked as inferred.

    [Fact]
    public async Task AnInferredYearIsShownOnTheRow()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), new ScreenshotTestRepository());
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);

        var quiz = viewModel.Rows[0];
        Assert.Equal("Needs review", quiz.StatusTitle);
        Assert.Contains("Year was not visible and was inferred as 2026.", quiz.Warnings.Select(warning => warning.Message));
        Assert.Equal(2026, quiz.Candidate.InferredYear);
        Assert.Equal(new DateOnly(2026, 8, 4), quiz.DueDay);
        Assert.Equal(new TimeOnly(16, 0), quiz.DueTimeOfDay);
    }

    // Availability lines are never read as deadlines.

    [Fact]
    public async Task AnAvailabilityTimeNeverReachesStorage()
    {
        var repository = new ScreenshotTestRepository();
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndUngradedTask), repository);
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        await viewModel.ImportSelectedAsync();

        var stored = Assert.Single(await repository.FetchAllAsync());
        Assert.Equal(Sydney.FromLocal(2026, 8, 4, 16, 0), stored.DueDate);
    }

    // Images and recognised text are held for the review only.

    [Fact]
    public async Task ImportingLetsGoOfTheImages()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), new ScreenshotTestRepository());
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        var screenshotId = viewModel.Candidates[0].SourceScreenshotId;
        Assert.Equal(1, viewModel.RetainedImageCount);
        Assert.NotNull(viewModel.ImageFor(screenshotId));

        await viewModel.ImportSelectedAsync();
        Assert.Equal(0, viewModel.RetainedImageCount);
        Assert.Null(viewModel.ImageFor(screenshotId));

        viewModel.Discard();
        Assert.Empty(viewModel.Candidates);
        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.ScreenshotNames);
    }

    [Fact]
    public void ClosingTheSheetMidReadingKeepsNothingItGoesOnToFind() => UiThread.Run(async () =>
    {
        var ocr = new GatedOcrService(QuizAndReflection, heldNames: ["example-screenshot.png"]);
        var viewModel = MakeViewModel(ocr, new ScreenshotTestRepository());
        var reading = viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        await ocr.Holding;
        Assert.True(viewModel.IsRecognising);

        viewModel.Discard();
        ocr.Release();
        await reading;

        Assert.True(viewModel.IsChoosing);
        Assert.Empty(viewModel.Candidates);
        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.ScreenshotNames);
        Assert.Equal(0, viewModel.RetainedImageCount);
    });

    [Fact]
    public void CancellingAReadingKeepsWhatFinishedAndStillFlagsDuplicates() => UiThread.Run(async () =>
    {
        var feedQuiz = FeedQuiz();
        var repository = new ScreenshotTestRepository([feedQuiz]);
        var ocr = new GatedOcrService(QuizAndReflection, heldNames: ["second.png"]);
        var viewModel = MakeViewModel(ocr, repository);

        var reading = viewModel.LoadAsync([files.Write("first.png"), files.Write("second.png")]);
        await ocr.Holding;
        Assert.Equal("Reading 2 screenshots…", viewModel.ProgressDisplayText);

        viewModel.CancelRecognition();
        await reading;

        Assert.True(viewModel.IsReviewing);
        Assert.Null(viewModel.ProgressText);
        Assert.Equal(["Example Quiz", "Example Reflection"], viewModel.Candidates.Select(candidate => candidate.Title));
        var quiz = viewModel.Candidates[0];
        Assert.True(quiz.PossibleDuplicate, "A row matching a stored event must not reach review looking ready");
        Assert.Equal(feedQuiz.Id, quiz.DuplicateTargetId);
        Assert.False(quiz.WillBeImported);
        Assert.True(viewModel.NoticeMessage is null, "A cancelled screenshot is not reported as unreadable");
        Assert.Equal(0, repository.WriteCount);
    });

    [Fact]
    public void RecognisedTextStaysOutOfAnythingLogged()
    {
        var title = Line("Example Quiz", 0.10);
        var due = Line("Due 4 Aug at 16.o0", 0.125);
        var source = new ScreenshotSource { DisplayName = "example-screenshot.png", ImageData = new byte[] { 1, 2, 3 } };
        var parsed = CanvasDateTextParser.Parse(due.Text, ReferenceNow, Sydney);
        Assert.NotNull(parsed);
        var candidate = Assert.Single(new CanvasScreenshotParser().Candidates([title, due], source.DisplayName, ReferenceNow, Sydney));
        Assert.Contains(candidate.Warnings, warning => warning.Kind == ScreenshotImportWarningKind.TimeCorrected);
        var session = new ScreenshotImportSession
        {
            Candidates = [candidate],
            ScreenshotNames = ImmutableDictionary<Guid, string>.Empty.Add(source.Id, source.DisplayName),
            FailedImageNames = ["broken-screenshot.png"],
        };
        var error = new ScreenshotOcrException(ScreenshotOcrError.UnreadableImage, source.DisplayName);

        string[] logged =
        [
            title.ToString(), due.ToString(), source.ToString(), parsed.ToString(), candidate.ToString(),
            session.ToString(), error.DiagnosticCode, .. candidate.Warnings.Select(warning => warning.ToString()),
        ];
        foreach (var text in logged)
        {
            Assert.DoesNotContain("Example Quiz", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("16.o0", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("screenshot.png", text, StringComparison.OrdinalIgnoreCase);
        }
        // The message is for the person in front of the app and names the file;
        // the code is what gets written down.
        Assert.Equal("“example-screenshot.png” could not be opened as an image.", error.Message);
        Assert.Equal("ocr.unreadable-image", error.DiagnosticCode);
        Assert.Equal(ScreenshotOcrError.UnreadableImage, error.Error);
    }

    [Fact]
    public async Task UpdateExistingNeverWritesIntoAFeedEvent()
    {
        // As on macOS, the commit matches rows by the screenshot's own
        // identifier, never by the duplicate it was flagged against, so
        // "Update existing" can only update an earlier screenshot import. Against
        // a feed event it adds a row beside it; the feed event is not touched.
        var feedQuiz = FeedQuiz();
        var repository = new ScreenshotTestRepository([feedQuiz]);
        var coordinator = new ScreenshotImportCoordinator(new InertScreenshotOcrService(), repository);
        var update = Reviewed("Example Quiz (Resubmission)", 4, 16) with
        {
            PossibleDuplicate = true,
            DuplicateTargetId = feedQuiz.Id,
            SelectedDuplicateAction = DuplicateResolution.UpdateExisting,
        };

        var commit = await coordinator.CommitAsync([update], ReferenceNow);

        Assert.Equal(1, commit.Summary.Added);
        var stored = await repository.FetchAllAsync();
        Assert.Equal(feedQuiz, Assert.Single(stored, row => row.Source == AssignmentSource.CanvasCalendarFeed));
        Assert.Single(stored, row => row.Source == AssignmentSource.Screenshot);
    }

    // The batch save can be undone.

    [Fact]
    public async Task ACommitSaysExactlyWhichRowsItAdded()
    {
        var repository = new ScreenshotTestRepository();
        var coordinator = new ScreenshotImportCoordinator(new InertScreenshotOcrService(), repository);

        var commit = await coordinator.CommitAsync([Reviewed("Example Quiz", 4, 16), Reviewed("Example Reflection", 6, 23)], ReferenceNow);

        var stored = await repository.FetchAllAsync();
        Assert.Equal(stored.Select(row => row.Id).Order(), commit.Added.Select(row => row.Id).Order());
        Assert.All(commit.Added, row => Assert.Equal(AssignmentSource.Screenshot, row.Source));
        Assert.Empty(commit.Replaced);
        Assert.True(commit.CanUndo);
    }

    [Fact]
    public async Task UndoingAnImportTakesAwayOnlyWhatItAdded()
    {
        var manual = new AssignmentSnapshot
        {
            Title = "STAT1010 study group",
            DueDate = Sydney.FromLocal(2026, 8, 5, 18, 0),
            Source = AssignmentSource.Manual,
        };
        var repository = new ScreenshotTestRepository([FeedQuiz(), manual]);
        var coordinator = new ScreenshotImportCoordinator(new InertScreenshotOcrService(), repository);
        var before = await repository.FetchAllAsync();
        var separately = Reviewed("Example Quiz", 4, 16) with
        {
            PossibleDuplicate = true,
            SelectedDuplicateAction = DuplicateResolution.ImportSeparately,
        };

        var commit = await coordinator.CommitAsync([separately, Reviewed("Example Reflection", 6, 23)], ReferenceNow);
        Assert.Equal(2, commit.Added.Count);
        var undo = await coordinator.UndoAsync(commit, ReferenceNow);

        Assert.Equal(new ScreenshotImportUndoResult(RemovedCount: 2, RestoredCount: 0), undo);
        Assert.Equal(before, await repository.FetchAllAsync());
    }

    [Fact]
    public async Task UndoingAReimportPutsBackWhatItReplaced()
    {
        var repository = new ScreenshotTestRepository();
        var coordinator = new ScreenshotImportCoordinator(new InertScreenshotOcrService(), repository);
        var first = await coordinator.CommitAsync([Reviewed("Example Quiz", 4, 16)], ReferenceNow);
        var original = Assert.Single(first.Added);

        // The same screenshot read again later, with its time corrected by hand.
        var second = await coordinator.CommitAsync([Reviewed("Example Quiz", 4, 17)], ReferenceNow.AddHours(1));
        Assert.Equal(1, second.Summary.Updated);
        Assert.Empty(second.Added);
        Assert.Equal(original, Assert.Single(second.Replaced));

        var undo = await coordinator.UndoAsync(second, ReferenceNow.AddHours(2));

        Assert.Equal(new ScreenshotImportUndoResult(RemovedCount: 0, RestoredCount: 1), undo);
        var restored = Assert.Single(await repository.FetchAllAsync());
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(Sydney.FromLocal(2026, 8, 4, 16, 0), restored.DueDate);
    }

    [Fact]
    public async Task UndoLeavesARowDeletedSinceDeleted()
    {
        var repository = new ScreenshotTestRepository();
        var coordinator = new ScreenshotImportCoordinator(new InertScreenshotOcrService(), repository);
        await coordinator.CommitAsync([Reviewed("Example Quiz", 4, 16)], ReferenceNow);
        var second = await coordinator.CommitAsync([Reviewed("Example Quiz", 4, 17)], ReferenceNow);
        await repository.DeleteAsync(Assert.Single(second.Replaced).Id);

        var undo = await coordinator.UndoAsync(second, ReferenceNow);

        Assert.Equal(new ScreenshotImportUndoResult(RemovedCount: 0, RestoredCount: 0), undo);
        Assert.Empty(await repository.FetchAllAsync());
    }

    [Fact]
    public async Task AnImportThatWroteNothingHasNothingToUndo()
    {
        var repository = new ScreenshotTestRepository([FeedQuiz()]);
        var coordinator = new ScreenshotImportCoordinator(new InertScreenshotOcrService(), repository);
        var skippedDuplicate = Reviewed("Example Quiz", 4, 16) with { PossibleDuplicate = true };

        var commit = await coordinator.CommitAsync([skippedDuplicate], ReferenceNow);

        Assert.False(commit.CanUndo);
        Assert.Equal(0, repository.WriteCount);
        Assert.Equal(new ScreenshotImportUndoResult(0, 0), await coordinator.UndoAsync(commit, ReferenceNow));
    }

    // Getting screenshots in.

    [Fact]
    public async Task FilesThatCannotBeUsedAreNamedNotDropped()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), new ScreenshotTestRepository());

        await viewModel.LoadAsync(
        [
            files.Write("notes.pdf"),
            files.PathFor("missing.png"),
            files.Write("empty.png", []),
            files.WriteSized("huge.png", ScreenshotImportViewModel.MaximumFileSizeBytes + 1),
            files.Write("example-screenshot.png"),
        ]);

        Assert.Equal(
            "These files could not be used: notes.pdf, missing.png, empty.png, huge.png. Use PNG, JPEG, HEIC or TIFF screenshots.",
            viewModel.ErrorMessage);
        Assert.Equal(["example-screenshot.png"], viewModel.ScreenshotNames.Values);
        Assert.True(viewModel.IsReviewing);
    }

    [Fact]
    public void TheExtensionDecidesWhatIsOffered()
    {
        Assert.Equal([".png", ".jpg", ".jpeg", ".jpe", ".heic", ".tif", ".tiff"], ScreenshotImportViewModel.SupportedFileExtensions);
        foreach (var path in new[] { "C:/Users/example/Pictures/Capture.PNG", "/tmp/a.JPG", "/tmp/a.jpe", "/tmp/a.tif", "/tmp/archive.v2.png" })
        {
            Assert.True(ScreenshotImportViewModel.IsSupported(path), path);
        }
        foreach (var path in new[] { "/tmp/.png", "/tmp/a.heif", "/tmp/a.gif", "/tmp/a.webp", "/tmp/a.bmp", "/tmp/a.png.zip", "/tmp/png" })
        {
            Assert.False(ScreenshotImportViewModel.IsSupported(path), path);
        }
    }

    [Fact]
    public async Task PastingNeedsAnImageOnTheClipboard()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), new ScreenshotTestRepository());

        await viewModel.PasteFromClipboardAsync(null);
        Assert.Equal("There is no image on the clipboard to paste.", viewModel.ErrorMessage);
        Assert.True(viewModel.IsChoosing);

        await viewModel.PasteFromClipboardAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        Assert.True(viewModel.IsReviewing);
        Assert.Equal(["Pasted screenshot"], viewModel.ScreenshotNames.Values);
    }

    [Fact]
    public async Task ScreenshotsThatYieldNothingAreNamed()
    {
        var ocr = StubOcrService.Returning([Line("Upcoming Assignments", 0.02)], failingNames: ["broken.png"]);
        var viewModel = MakeViewModel(ocr, new ScreenshotTestRepository());

        await viewModel.LoadAsync([files.Write("broken.png"), files.Write("blank.png")]);

        Assert.True(viewModel.IsChoosing);
        Assert.Equal(
            "Could not read: broken.png\nNo Canvas deadlines were detected in blank.png. "
                + "Make sure the screenshot includes the assignment title and the line beginning with “Due”.",
            viewModel.NoticeMessage);
    }

    // Editing in review.

    [Fact]
    public async Task EditsGoBackThroughTheReviewAndKeepTheRow()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), new ScreenshotTestRepository());
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        var row = viewModel.Rows[0];
        var changed = new List<string?>();
        row.PropertyChanged += (_, change) => changed.Add(change.PropertyName);

        row.Title = "Example Quiz (Resubmission)";

        Assert.Same(row, viewModel.Rows[0]);
        Assert.Equal("Example Quiz (Resubmission)", viewModel.Candidates[0].Title);
        Assert.Contains(nameof(row.Title), changed);
        Assert.Contains(nameof(row.IncludeAccessibilityLabel), changed);
        Assert.DoesNotContain(nameof(row.DueDate), changed);

        row.DueTimeOfDay = new TimeOnly(17, 30);
        Assert.Equal(Sydney.FromLocal(2026, 8, 4, 17, 30), viewModel.Candidates[0].DueDate);
        row.DueDay = new DateOnly(2026, 8, 5);
        Assert.Equal(Sydney.FromLocal(2026, 8, 5, 17, 30), viewModel.Candidates[0].DueDate);
    }

    [Fact]
    public async Task ACourseCanBeGivenToEveryRowFromOneScreenshot()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndReflection), new ScreenshotTestRepository());
        viewModel.ExistingCourseNames = ["COMP2050", "STAT1010"];
        await viewModel.LoadAsync([files.Write("first.png"), files.Write("second.png")]);
        var first = viewModel.Rows[0];
        Assert.Equal(["COMP2050", "STAT1010"], first.CourseChoices);
        Assert.Equal("No Course", first.CourseTitle);
        Assert.False(first.ApplyCourseToScreenshotCommand.CanExecute(null));

        first.ChooseCourseCommand.Execute("COMP2050");
        Assert.True(first.ApplyCourseToScreenshotCommand.CanExecute(null));
        first.ApplyCourseToScreenshotCommand.Execute(null);

        Assert.Equal(["COMP2050", "COMP2050", "No Course", "No Course"], viewModel.Rows.Select(row => row.CourseTitle));
        Assert.Equal(["COMP2050"], viewModel.AvailableCourseNames);
        Assert.Equal("Course, COMP2050", first.CourseAccessibilityLabel);
    }

    [Fact]
    public async Task SelectingAllReadyLeavesRowsThatNeedAttentionAlone()
    {
        var viewModel = MakeViewModel(
            StubOcrService.Returning(
            [
                Line("Example Quiz", 0.10),
                Line("Due 4 Aug 2026 at 16:00", 0.125),
                Line("Example Reflection", 0.25),
                Line("Due 6 Sep at 23:59", 0.275),
            ]),
            new ScreenshotTestRepository());
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        Assert.Equal((1, 1), (viewModel.ReadyCount, viewModel.NeedsReviewCount));

        viewModel.DeselectAll();
        Assert.Equal(0, viewModel.ImportableCount);
        Assert.False(viewModel.ImportSelectedCommand.CanExecute(null));

        viewModel.SelectAllReady();
        Assert.Equal([true, false], viewModel.Rows.Select(row => row.Include));
        Assert.Equal("Import 1 Selected", viewModel.ImportButtonTitle);
    }

    [Fact]
    public async Task TheRowShowsWhatWasRecognised()
    {
        var viewModel = MakeViewModel(StubOcrService.Returning(QuizAndUngradedTask), new ScreenshotTestRepository());
        await viewModel.LoadAsync([files.Write("example-screenshot.png")]);
        var quiz = viewModel.Rows[0];

        Assert.Equal("Confidence 92%", quiz.ConfidenceText);
        Assert.Equal("Recognised: Due 4 Aug at 16:00", quiz.RecognisedText);
        Assert.Null(viewModel.Rows[1].RecognisedText);
        Assert.Equal("Include Example Quiz", quiz.IncludeAccessibilityLabel);
        Assert.Equal("Course, none", quiz.CourseAccessibilityLabel);
        Assert.Equal(["Skip", "Update existing", "Import separately"], quiz.DuplicateChoices.Select(choice => choice.Title()));

        var unsure = MakeViewModel(
            StubOcrService.Returning([Line("Example Quiz", 0.10, confidence: null), Line("Due 4 Aug at 16:00", 0.125, confidence: null)]),
            new ScreenshotTestRepository());
        await unsure.LoadAsync([files.Write("example-screenshot.png")]);
        Assert.True(unsure.Rows[0].ConfidenceText is null, "No number is shown when the reader gave none");
    }

    [Fact]
    public void TheSummaryWordsTheOutcome()
    {
        Assert.Equal("0 added, 0 updated", ScreenshotImportSummary.Empty.Message);
        Assert.Equal(
            "2 added, 1 updated, 3 skipped, 1 failed",
            new ScreenshotImportSummary { Added = 2, Updated = 1, Skipped = 3, Failed = 1 }.Message);
    }
}

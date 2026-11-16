using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanvasCountdown;

public enum ScreenshotImportStageKind
{
    Choosing,
    Recognising,
    Reviewing,
    Finished,
}

/// <summary>Where the import sheet is: choosing images, reading them, reviewing rows, or done.</summary>
public sealed record ScreenshotImportStage
{
    private ScreenshotImportStage(ScreenshotImportStageKind kind, ScreenshotImportSummary? summary)
    {
        Kind = kind;
        Summary = summary;
    }

    public ScreenshotImportStageKind Kind { get; }

    /// <summary>What the import did, once it has finished.</summary>
    public ScreenshotImportSummary? Summary { get; }

    public static ScreenshotImportStage Choosing { get; } = new(ScreenshotImportStageKind.Choosing, null);

    public static ScreenshotImportStage Recognising { get; } = new(ScreenshotImportStageKind.Recognising, null);

    public static ScreenshotImportStage Reviewing { get; } = new(ScreenshotImportStageKind.Reviewing, null);

    public static ScreenshotImportStage Finished(ScreenshotImportSummary summary)
        => new(ScreenshotImportStageKind.Finished, summary ?? throw new ArgumentNullException(nameof(summary)));
}

/// <summary>
/// Drives the screenshot import sheet.
/// </summary>
/// <remarks>
/// <para>
/// Holds the images for the length of the review only. Nothing here is written
/// to disk, and <see cref="Discard"/> drops every image and every recognised
/// string the moment the sheet closes — including whatever a reading still
/// running at that moment goes on to find.
/// </para>
/// <para>
/// There is no path from recognition straight to storage: the only write is
/// <see cref="ImportSelectedAsync"/>, which runs from the review and saves only
/// the rows the review left included.
/// </para>
/// </remarks>
public sealed partial class ScreenshotImportViewModel : ObservableObject
{
    /// <summary>
    /// What the file picker offers and a drop accepts: PNG, JPEG, HEIC and
    /// TIFF, by every extension the macOS app accepts for them.
    /// </summary>
    public static ValueList<string> SupportedFileExtensions { get; } =
        [".png", ".jpg", ".jpeg", ".jpe", ".heic", ".tif", ".tiff"];

    /// <summary>A generous ceiling: a full-screen high-resolution capture is far below this.</summary>
    public const long MaximumFileSizeBytes = 60L * 1024 * 1024;

    /// <summary>The promise the sheet's header makes, under its title.</summary>
    public const string PrivacyNote = "Text recognition runs locally on this PC. Images are not uploaded.";

    private readonly IScreenshotImportCoordinating coordinator;
    private readonly Func<Task<IReadOnlyList<AssignmentSnapshot>>> existingProvider;
    private readonly TimeProvider timeProvider;
    private readonly Func<AppCalendar> calendarProvider;
    private readonly ObservableCollection<ScreenshotImportRowViewModel> rows = [];

    private ValueList<ScreenshotSource> sources = ValueList<ScreenshotSource>.Empty;
    private CancellationTokenSource? recognition;
    private int recognitionGeneration;

    /// <param name="coordinator">Recognises, checks and commits.</param>
    /// <param name="existingProvider">The stored events, for spotting duplicates.</param>
    /// <param name="timeProvider">The clock: "now" for year inference and for the commit.</param>
    /// <param name="calendarProvider">
    /// The calendar dates are read in, asked for at each use so a change of
    /// time zone while the app runs is honoured (<see cref="AppCalendar.Current"/>
    /// in the app, a fixed zone in tests).
    /// </param>
    public ScreenshotImportViewModel(
        IScreenshotImportCoordinating coordinator,
        Func<Task<IReadOnlyList<AssignmentSnapshot>>> existingProvider,
        TimeProvider timeProvider,
        Func<AppCalendar> calendarProvider)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.existingProvider = existingProvider ?? throw new ArgumentNullException(nameof(existingProvider));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.calendarProvider = calendarProvider ?? throw new ArgumentNullException(nameof(calendarProvider));
        Rows = new ReadOnlyObservableCollection<ScreenshotImportRowViewModel>(rows);
        Stage = ScreenshotImportStage.Choosing;
        Candidates = ValueList<ScreenshotImportCandidate>.Empty;
        ScreenshotNames = ImmutableDictionary<Guid, string>.Empty;
        ExistingCourseNames = ValueList<string>.Empty;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsRecognising), nameof(IsReviewing), nameof(IsFinished), nameof(FinishedMessage))]
    [NotifyCanExecuteChangedFor(nameof(ImportSelectedCommand))]
    public partial ScreenshotImportStage Stage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(ReadyCount),
        nameof(NeedsReviewCount),
        nameof(InvalidCount),
        nameof(DuplicateCount),
        nameof(ImportableCount),
        nameof(CanImport),
        nameof(AvailableCourseNames),
        nameof(ImportButtonTitle))]
    [NotifyCanExecuteChangedFor(nameof(ImportSelectedCommand))]
    public partial ValueList<ScreenshotImportCandidate> Candidates { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? NoticeMessage { get; set; }

    [ObservableProperty]
    public partial Guid? SelectedCandidateId { get; set; }

    [ObservableProperty]
    public partial bool IsTargetedForDrop { get; set; }

    [ObservableProperty]
    public partial ImmutableDictionary<Guid, string> ScreenshotNames { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressDisplayText))]
    public partial string? ProgressText { get; private set; }

    /// <summary>
    /// The courses already in the app, offered in each row's course menu. Set
    /// by the window when it opens the sheet.
    /// </summary>
    [ObservableProperty]
    public partial ValueList<string> ExistingCourseNames { get; set; }

    /// <summary>
    /// What the last confirmed import wrote, for the window to offer to take
    /// back (<see cref="IScreenshotImportCoordinating.UndoAsync"/>). Kept after
    /// the sheet closes, because the undo outlives it; it holds stored rows
    /// only, never an image or a recognised line.
    /// </summary>
    [ObservableProperty]
    public partial ScreenshotImportCommit? LastCommit { get; private set; }

    /// <summary>One bindable row per candidate, in the same order. A row survives edits to its candidate.</summary>
    public ReadOnlyObservableCollection<ScreenshotImportRowViewModel> Rows { get; }

    public int ReadyCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.Ready);

    public int NeedsReviewCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.NeedsReview);

    public int InvalidCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.Invalid);

    public int DuplicateCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.PossibleDuplicate);

    public int ImportableCount => Candidates.Count(candidate => candidate.WillBeImported);

    public bool CanImport => ImportableCount > 0;

    public ValueList<string> AvailableCourseNames => Candidates
        .Select(candidate => candidate.CourseName)
        .OfType<string>()
        .Distinct()
        .Order(TextOrder.Comparer)
        .ToValueList();

    /// <summary>The confirming button. Says the number, so the last thing read before the click is what is about to happen.</summary>
    public string ImportButtonTitle => string.Create(CultureInfo.InvariantCulture, $"Import {ImportableCount} Selected");

    public string ProgressDisplayText => ProgressText ?? "Reading screenshots…";

    public bool IsChoosing => Stage.Kind == ScreenshotImportStageKind.Choosing;

    public bool IsRecognising => Stage.Kind == ScreenshotImportStageKind.Recognising;

    public bool IsReviewing => Stage.Kind == ScreenshotImportStageKind.Reviewing;

    public bool IsFinished => Stage.Kind == ScreenshotImportStageKind.Finished;

    /// <summary>"1 added, 0 updated", once the import has finished.</summary>
    public string? FinishedMessage => Stage.Summary?.Message;

    internal AppCalendar Calendar => calendarProvider();

    internal DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>
    /// Accepts file paths from the picker or a drop. Unsupported or unreadable
    /// files are reported rather than silently dropped.
    /// </summary>
    public async Task LoadAsync(IEnumerable<string> paths)
    {
        var accepted = new List<ScreenshotSource>();
        var rejected = new List<string>();

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!IsSupported(path))
            {
                rejected.Add(name);
                continue;
            }
            if (await ReadImageAsync(path) is not { } data)
            {
                rejected.Add(name);
                continue;
            }
            accepted.Add(new ScreenshotSource { DisplayName = name, ImageData = data });
        }

        if (rejected.Count > 0)
        {
            ErrorMessage = "These files could not be used: "
                + string.Join(", ", rejected)
                + ". Use PNG, JPEG, HEIC or TIFF screenshots.";
        }
        if (accepted.Count == 0)
        {
            return;
        }
        await RecogniseAsync(accepted);
    }

    /// <summary>
    /// Accepts an image the window read off the clipboard, or null when there
    /// was none there.
    /// </summary>
    public async Task PasteFromClipboardAsync(ReadOnlyMemory<byte>? clipboardImage)
    {
        if (clipboardImage is not { IsEmpty: false } image)
        {
            ErrorMessage = "There is no image on the clipboard to paste.";
            return;
        }
        await RecogniseAsync([new ScreenshotSource { DisplayName = "Pasted screenshot", ImageData = image }]);
    }

    private async Task RecogniseAsync(IReadOnlyList<ScreenshotSource> newSources)
    {
        // A newer reading covers every screenshot so far, so one still running
        // has nothing left to add. It is marked stale before it is cancelled,
        // because cancelling can finish it on the spot.
        var generation = ++recognitionGeneration;
        recognition?.Cancel();
        using var run = new CancellationTokenSource();
        recognition = run;

        sources = sources.AddRange(newSources);
        var reading = sources;
        Stage = ScreenshotImportStage.Recognising;
        ProgressText = newSources.Count == 1
            ? "Reading 1 screenshot…"
            : string.Create(CultureInfo.InvariantCulture, $"Reading {newSources.Count} screenshots…");

        try
        {
            IReadOnlyList<AssignmentSnapshot> existing;
            try
            {
                existing = await existingProvider();
            }
            catch (Exception)
            {
                existing = [];
            }

            var session = await coordinator.BuildSessionAsync(reading, existing, Now, Calendar, run.Token);

            // Discarded, or overtaken by a newer reading, while this one ran:
            // nothing it found may outlive that.
            if (generation != recognitionGeneration)
            {
                return;
            }

            ProgressText = null;
            ScreenshotNames = session.ScreenshotNames;
            Candidates = session.Candidates;
            Stage = session.Candidates.IsEmpty ? ScreenshotImportStage.Choosing : ScreenshotImportStage.Reviewing;

            var notices = new List<string>();
            if (!session.FailedImageNames.IsEmpty)
            {
                notices.Add("Could not read: " + string.Join(", ", session.FailedImageNames));
            }
            if (!session.RecognisedNothingNames.IsEmpty)
            {
                notices.Add(
                    "No Canvas deadlines were detected in "
                    + string.Join(", ", session.RecognisedNothingNames)
                    + ". Make sure the screenshot includes the assignment title and the line beginning with “Due”.");
            }
            NoticeMessage = notices.Count == 0 ? null : string.Join("\n", notices);
        }
        finally
        {
            if (ReferenceEquals(recognition, run))
            {
                recognition = null;
            }
        }
    }

    [RelayCommand]
    public void CancelRecognition()
    {
        recognition?.Cancel();
        recognition = null;
        ProgressText = null;
        Stage = Candidates.IsEmpty ? ScreenshotImportStage.Choosing : ScreenshotImportStage.Reviewing;
    }

    /// <summary>The candidate as its row currently reads it, or null once it has gone.</summary>
    public ScreenshotImportCandidate? BindingFor(Guid id) => Candidates.FirstOrDefault(candidate => candidate.Id == id);

    public void Update(ScreenshotImportCandidate candidate)
    {
        var index = Candidates.FindIndex(stored => stored.Id == candidate.Id);
        if (index < 0)
        {
            return;
        }
        var updated = candidate;
        // A hand-typed correction answers the warnings that prompted it.
        if (updated.DueDate is not null)
        {
            updated = updated with
            {
                Warnings = updated.Warnings.RemoveAll(warning => warning == ScreenshotImportWarning.MissingDueDate),
            };
        }
        Candidates = Candidates.SetItem(index, updated);
    }

    [RelayCommand]
    public void SelectAllReady()
    {
        Candidates = Candidates
            .Select(candidate => candidate.Status == ScreenshotCandidateStatus.Ready ? candidate with { Include = true } : candidate)
            .ToValueList();
    }

    [RelayCommand]
    public void DeselectAll()
    {
        Candidates = Candidates.Select(candidate => candidate with { Include = false }).ToValueList();
    }

    /// <summary>
    /// Applies one course to every row from the same screenshot, which is the
    /// common case: one screenshot is usually one subject.
    /// </summary>
    public void ApplyCourse(string? course, Guid screenshotId)
    {
        Candidates = Candidates
            .Select(candidate => candidate.SourceScreenshotId == screenshotId ? candidate with { CourseName = course } : candidate)
            .ToValueList();
    }

    [RelayCommand(CanExecute = nameof(CanImportSelected))]
    public async Task ImportSelectedAsync()
    {
        if (!CanImportSelected())
        {
            return;
        }
        try
        {
            var commit = await coordinator.CommitAsync(Candidates, Now);
            LastCommit = commit;
            Stage = ScreenshotImportStage.Finished(commit.Summary);
            // The images have done their job.
            ReleaseImages();
        }
        catch (Exception)
        {
            ErrorMessage = "The selected events could not be imported.";
        }
    }

    /// <summary>Only from the review itself, and only when something would be written.</summary>
    private bool CanImportSelected() => IsReviewing && CanImport;

    /// <summary>Called when the sheet closes, however it closes.</summary>
    [RelayCommand]
    public void Discard()
    {
        // Marked stale first: cancelling can finish a running reading on the
        // spot, and what it found must not land after the sheet has gone.
        recognitionGeneration++;
        recognition?.Cancel();
        recognition = null;
        Candidates = ValueList<ScreenshotImportCandidate>.Empty;
        ScreenshotNames = ImmutableDictionary<Guid, string>.Empty;
        ReleaseImages();
        Stage = ScreenshotImportStage.Choosing;
    }

    private void ReleaseImages()
    {
        sources = ValueList<ScreenshotSource>.Empty;
        OnPropertyChanged(nameof(RetainedImageCount));
    }

    /// <summary>Only for tests and the preview overlay: the images never leave the session.</summary>
    public int RetainedImageCount => sources.Count;

    public ReadOnlyMemory<byte>? ImageFor(Guid screenshotId)
        => sources.FirstOrDefault(source => source.Id == screenshotId)?.ImageData;

    public static bool IsSupported(string path)
        => SupportedFileExtensions.Contains("." + PathExtension(path).ToLowerInvariant());

    /// <summary>
    /// The extension the way Foundation reads one: after the last full stop of
    /// the last path component, where a leading full stop marks a hidden file
    /// rather than an extension (".png" has none).
    /// </summary>
    private static string PathExtension(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\')).TrimStart('.');
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..];
    }

    /// <summary>
    /// The file's bytes, or null when it is missing, empty, unreadable or over
    /// <see cref="MaximumFileSizeBytes"/>. The file is read once and let go; the
    /// app never holds on to the user's folder.
    /// </summary>
    private static async Task<byte[]?> ReadImageAsync(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            if (size == 0 || size > MaximumFileSizeBytes)
            {
                return null;
            }
            var data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return data.Length == 0 ? null : data;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    partial void OnCandidatesChanged(ValueList<ScreenshotImportCandidate> value)
    {
        var sameRows = rows.Count == value.Count
            && rows.Select(row => row.Id).SequenceEqual(value.Select(candidate => candidate.Id));
        if (sameRows)
        {
            for (var index = 0; index < value.Count; index++)
            {
                rows[index].Refresh(value[index]);
            }
            return;
        }
        rows.Clear();
        foreach (var candidate in value)
        {
            rows.Add(new ScreenshotImportRowViewModel(this, candidate));
        }
    }

    partial void OnExistingCourseNamesChanged(ValueList<string> value)
    {
        foreach (var row in rows)
        {
            row.CourseChoicesChanged();
        }
    }
}

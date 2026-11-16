using System.Collections.Immutable;
using System.Globalization;

namespace CanvasCountdown;

/// <summary>
/// One line of text a recogniser read, with everything the parser needs to
/// reason about where it sat on screen.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BoundingBox"/> is normalised to 0–1 with the origin at the
/// <b>top left</b> (see <see cref="NormalisedRect"/>). Converting once, in the
/// recogniser, keeps the flip out of the parser, where an inverted comparison
/// would silently sort rows upside down.
/// </para>
/// <para>
/// Lines may arrive in any order. Reading order is decided by
/// <see cref="CanvasScreenshotParser.ReadingOrder"/>, never by the recogniser.
/// </para>
/// <para>
/// <see cref="Confidence"/> is null when the recogniser does not report one;
/// Windows.Media.Ocr does not. A recogniser must not invent a number to fill
/// it: an unknown confidence raises no warning and claims no certainty.
/// </para>
/// <para>
/// The text is held for the review only. <see cref="ToString"/> leaves it out,
/// so logging an observation cannot carry recognised text into diagnostics.
/// </para>
/// </remarks>
public sealed record OcrTextObservation
{
    public required string Text { get; init; }

    /// <summary>From 0 to 1, or null when the recogniser does not say.</summary>
    public required double? Confidence { get; init; }

    public required NormalisedRect BoundingBox { get; init; }

    public ValueList<string> Alternatives { get; init; } = ValueList<string>.Empty;

    public Guid ScreenshotId { get; init; } = Guid.NewGuid();

    public RecognitionPass Pass { get; init; } = RecognitionPass.Original;

    public double VerticalCentre => BoundingBox.MidY;

    public override string ToString()
        => $"{nameof(OcrTextObservation)} {{ {nameof(BoundingBox)} = {BoundingBox}, {nameof(Pass)} = {Pass} }}";
}

/// <summary>
/// Which recognition attempt produced an observation. Recorded so a candidate
/// can say where it came from rather than quietly mixing passes.
/// </summary>
public enum RecognitionPass
{
    Original,
    Enhanced,
}

public enum ScreenshotImportWarningKind
{
    YearInferred,
    TimeCorrected,
    MultipleDueDates,
    MissingDueDate,
    MissingTime,
    DateInPast,
    LowConfidence,
    TitleUncertain,
}

/// <summary>
/// Something the app changed, guessed, or could not resolve. Every one of these
/// is shown in review; none of them is applied silently.
/// </summary>
public sealed record ScreenshotImportWarning
{
    private ScreenshotImportWarning(ScreenshotImportWarningKind kind, int? year = null, string? from = null, string? to = null)
    {
        Kind = kind;
        Year = year;
        From = from;
        To = to;
    }

    public ScreenshotImportWarningKind Kind { get; }

    /// <summary>The year chosen, for <see cref="ScreenshotImportWarningKind.YearInferred"/>.</summary>
    public int? Year { get; }

    /// <summary>The time as recognised, for <see cref="ScreenshotImportWarningKind.TimeCorrected"/>.</summary>
    public string? From { get; }

    /// <summary>The time it was read as, for <see cref="ScreenshotImportWarningKind.TimeCorrected"/>.</summary>
    public string? To { get; }

    public static ScreenshotImportWarning YearInferred(int year) => new(ScreenshotImportWarningKind.YearInferred, year: year);

    public static ScreenshotImportWarning TimeCorrected(string from, string to)
        => new(ScreenshotImportWarningKind.TimeCorrected, from: from, to: to);

    public static ScreenshotImportWarning MultipleDueDates { get; } = new(ScreenshotImportWarningKind.MultipleDueDates);

    public static ScreenshotImportWarning MissingDueDate { get; } = new(ScreenshotImportWarningKind.MissingDueDate);

    public static ScreenshotImportWarning MissingTime { get; } = new(ScreenshotImportWarningKind.MissingTime);

    public static ScreenshotImportWarning DateInPast { get; } = new(ScreenshotImportWarningKind.DateInPast);

    public static ScreenshotImportWarning LowConfidence { get; } = new(ScreenshotImportWarningKind.LowConfidence);

    public static ScreenshotImportWarning TitleUncertain { get; } = new(ScreenshotImportWarningKind.TitleUncertain);

    public string Id => Message;

    public string Message => Kind switch
    {
        ScreenshotImportWarningKind.YearInferred
            => string.Create(CultureInfo.InvariantCulture, $"Year was not visible and was inferred as {Year}."),
        ScreenshotImportWarningKind.TimeCorrected => $"The recognised time “{From}” was interpreted as {To}.",
        ScreenshotImportWarningKind.MultipleDueDates => "More than one possible due date was detected.",
        ScreenshotImportWarningKind.MissingDueDate => "No due date was found. Add one before importing.",
        ScreenshotImportWarningKind.MissingTime => "No due time was found.",
        ScreenshotImportWarningKind.DateInPast => "The detected date is already in the past.",
        ScreenshotImportWarningKind.LowConfidence => "Text recognition was uncertain about this row.",
        ScreenshotImportWarningKind.TitleUncertain => "The assignment name may be incomplete.",
        _ => throw new InvalidOperationException($"Unknown warning kind {Kind}."),
    };

    /// <summary>Whether the row cannot be imported until the user fixes it.</summary>
    public bool IsBlocking => this == MissingDueDate;

    /// <summary>The kind only: a corrected time is recognised text, and stays out of anything logged.</summary>
    public override string ToString() => $"{nameof(ScreenshotImportWarning)} {{ {nameof(Kind)} = {Kind} }}";
}

public enum ScreenshotCandidateStatus
{
    Ready,
    NeedsReview,
    Invalid,
    PossibleDuplicate,
}

public static class ScreenshotCandidateStatusExtensions
{
    /// <summary>
    /// The words for a status. Shown beside an icon and a colour everywhere the
    /// status appears, so it never depends on colour alone. The icon itself is
    /// the Windows layer's choice: the macOS app's SF Symbols are licensed for
    /// Apple platforms only.
    /// </summary>
    public static string Title(this ScreenshotCandidateStatus status) => status switch
    {
        ScreenshotCandidateStatus.Ready => "Ready",
        ScreenshotCandidateStatus.NeedsReview => "Needs review",
        ScreenshotCandidateStatus.Invalid => "Invalid",
        ScreenshotCandidateStatus.PossibleDuplicate => "Possible duplicate",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>What to do about a row that looks like something already stored.</summary>
public enum DuplicateResolution
{
    Skip,
    UpdateExisting,
    ImportSeparately,
}

public static class DuplicateResolutionExtensions
{
    /// <summary>Every choice, in the order the review offers them.</summary>
    public static ValueList<DuplicateResolution> AllCases { get; } =
        [DuplicateResolution.Skip, DuplicateResolution.UpdateExisting, DuplicateResolution.ImportSeparately];

    public static string Id(this DuplicateResolution resolution) => resolution switch
    {
        DuplicateResolution.Skip => "skip",
        DuplicateResolution.UpdateExisting => "updateExisting",
        DuplicateResolution.ImportSeparately => "importSeparately",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null),
    };

    public static string Title(this DuplicateResolution resolution) => resolution switch
    {
        DuplicateResolution.Skip => "Skip",
        DuplicateResolution.UpdateExisting => "Update existing",
        DuplicateResolution.ImportSeparately => "Import separately",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null),
    };
}

/// <summary>
/// One row awaiting the user's decision. Everything here is editable in review;
/// nothing reaches storage without an explicit confirmation.
/// </summary>
/// <remarks>
/// The original texts are what was recognised, kept so the review can show
/// exactly what the screenshot said. They live only as long as the review:
/// nothing derived from them is stored, and <see cref="ToString"/> leaves every
/// text field out.
/// </remarks>
public sealed record ScreenshotImportCandidate
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid SourceScreenshotId { get; init; }

    public required string SourceImageName { get; init; }

    public bool Include { get; init; } = true;

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    public required DateTimeOffset? DueDate { get; init; }

    public required string OriginalTitleText { get; init; }

    public required string OriginalDueText { get; init; }

    /// <summary>The mean confidence of the row's lines, or null when the recogniser reported none.</summary>
    public required double? OcrConfidence { get; init; }

    public required double ParserConfidence { get; init; }

    public ValueList<ScreenshotImportWarning> Warnings { get; init; } = ValueList<ScreenshotImportWarning>.Empty;

    public int? InferredYear { get; init; }

    public string? CorrectedOcrText { get; init; }

    public bool PossibleDuplicate { get; init; }

    public Guid? DuplicateTargetId { get; init; }

    public DuplicateResolution SelectedDuplicateAction { get; init; } = DuplicateResolution.Skip;

    public NormalisedRect BoundingRegion { get; init; } = NormalisedRect.Zero;

    public string TrimmedTitle => Title.Trim();

    /// <summary>
    /// Confidence is only ever used to sort human attention. It never decides
    /// that a row is correct.
    /// </summary>
    public ScreenshotCandidateStatus Status
    {
        get
        {
            if (DueDate is null || TrimmedTitle.Length == 0)
            {
                return ScreenshotCandidateStatus.Invalid;
            }
            if (PossibleDuplicate)
            {
                return ScreenshotCandidateStatus.PossibleDuplicate;
            }
            return Warnings.IsEmpty ? ScreenshotCandidateStatus.Ready : ScreenshotCandidateStatus.NeedsReview;
        }
    }

    public bool CanBeImported => Status != ScreenshotCandidateStatus.Invalid;

    /// <summary>True when this row would actually write something on confirmation.</summary>
    public bool WillBeImported
    {
        get
        {
            if (!Include || !CanBeImported)
            {
                return false;
            }
            if (PossibleDuplicate && SelectedDuplicateAction == DuplicateResolution.Skip)
            {
                return false;
            }
            return true;
        }
    }

    public override string ToString()
        => $"{nameof(ScreenshotImportCandidate)} {{ {nameof(Id)} = {Id}, {nameof(Status)} = {Status}, {nameof(Include)} = {Include} }}";
}

/// <summary>
/// One review session. Held in memory only: the images live in the view model
/// and nowhere else, and are dropped when the session ends.
/// </summary>
public sealed record ScreenshotImportSession
{
    public ValueList<ScreenshotImportCandidate> Candidates { get; init; } = ValueList<ScreenshotImportCandidate>.Empty;

    /// <summary>Each screenshot's display name, by its id.</summary>
    public ImmutableDictionary<Guid, string> ScreenshotNames { get; init; } = ImmutableDictionary<Guid, string>.Empty;

    public ValueList<string> FailedImageNames { get; init; } = ValueList<string>.Empty;

    public ValueList<string> RecognisedNothingNames { get; init; } = ValueList<string>.Empty;

    public int ReadyCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.Ready);

    public int NeedsReviewCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.NeedsReview);

    public int InvalidCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.Invalid);

    public int DuplicateCount => Candidates.Count(candidate => candidate.Status == ScreenshotCandidateStatus.PossibleDuplicate);

    public int ImportableCount => Candidates.Count(candidate => candidate.WillBeImported);

    public bool IsEmpty => Candidates.IsEmpty;

    // Written out by hand only because the names are a dictionary, which a
    // record would otherwise compare by reference.
    public bool Equals(ScreenshotImportSession? other)
        => other is not null
            && Candidates == other.Candidates
            && FailedImageNames == other.FailedImageNames
            && RecognisedNothingNames == other.RecognisedNothingNames
            && ScreenshotNames.Count == other.ScreenshotNames.Count
            && ScreenshotNames.All(pair => other.ScreenshotNames.TryGetValue(pair.Key, out var name) && name == pair.Value);

    public override int GetHashCode()
    {
        var names = 0;
        foreach (var pair in ScreenshotNames)
        {
            names ^= HashCode.Combine(pair.Key, pair.Value);
        }
        return HashCode.Combine(Candidates, FailedImageNames, RecognisedNothingNames, names);
    }

    /// <summary>Counts only: the session holds recognised text and screenshot names, and neither belongs in anything logged.</summary>
    public override string ToString()
        => $"{nameof(ScreenshotImportSession)} {{ {nameof(Candidates)} = {Candidates.Count}, Screenshots = {ScreenshotNames.Count}, "
            + $"Failed = {FailedImageNames.Count}, RecognisedNothing = {RecognisedNothingNames.Count} }}";
}

/// <summary>Outcome shown after the user confirms.</summary>
public sealed record ScreenshotImportSummary
{
    public required int Added { get; init; }

    public required int Updated { get; init; }

    public required int Skipped { get; init; }

    public required int Failed { get; init; }

    public static ScreenshotImportSummary Empty { get; } = new() { Added = 0, Updated = 0, Skipped = 0, Failed = 0 };

    public string Message
    {
        get
        {
            var parts = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"{Added} added"),
                string.Create(CultureInfo.InvariantCulture, $"{Updated} updated"),
            };
            if (Skipped > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Skipped} skipped"));
            }
            if (Failed > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Failed} failed"));
            }
            return string.Join(", ", parts);
        }
    }
}

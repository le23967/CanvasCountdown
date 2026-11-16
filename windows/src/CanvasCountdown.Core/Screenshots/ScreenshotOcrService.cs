namespace CanvasCountdown;

/// <summary>
/// One screenshot being processed. Held only for the length of a review
/// session; nothing here is written to disk or to the database.
/// </summary>
/// <remarks>
/// A class rather than a record on purpose: it is compared by identity, as the
/// macOS type was, never by the bytes it carries.
/// </remarks>
public sealed class ScreenshotSource
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string DisplayName { get; init; }

    /// <summary>
    /// The image file exactly as read: PNG, JPEG, HEIC or TIFF. Decoding it is
    /// the recogniser's job, because only the platform can.
    /// </summary>
    public required ReadOnlyMemory<byte> ImageData { get; init; }

    public override string ToString() => $"{nameof(ScreenshotSource)} {{ {nameof(Id)} = {Id} }}";
}

public enum ScreenshotOcrError
{
    UnreadableImage,
    UnsupportedFileType,
    ImageTooLarge,
    RecognitionFailed,
    Cancelled,
}

public static class ScreenshotOcrErrorExtensions
{
    /// <summary>Safe for redacted diagnostics: an identifier only, never recognised text.</summary>
    public static string DiagnosticCode(this ScreenshotOcrError error) => error switch
    {
        ScreenshotOcrError.UnreadableImage => "ocr.unreadable-image",
        ScreenshotOcrError.UnsupportedFileType => "ocr.unsupported-type",
        ScreenshotOcrError.ImageTooLarge => "ocr.image-too-large",
        ScreenshotOcrError.RecognitionFailed => "ocr.recognition-failed",
        ScreenshotOcrError.Cancelled => "ocr.cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };
}

/// <summary>
/// A screenshot that could not be recognised. The message names the screenshot
/// and is for the person in front of the app; <see cref="DiagnosticCode"/> is
/// for anything written down.
/// </summary>
public sealed class ScreenshotOcrException : Exception
{
    /// <summary>For the errors that name no screenshot.</summary>
    public ScreenshotOcrException(ScreenshotOcrError error, Exception? innerException = null)
        : this(error, null, innerException)
    {
    }

    /// <summary>For the errors that name the screenshot they are about.</summary>
    public ScreenshotOcrException(ScreenshotOcrError error, string? name, Exception? innerException = null)
        : base(Describe(error, name), innerException)
    {
        Error = error;
        Name = name;
    }

    public ScreenshotOcrError Error { get; }

    /// <summary>The screenshot's display name, for the errors that carry one.</summary>
    public string? Name { get; }

    public string DiagnosticCode => Error.DiagnosticCode();

    private static string Describe(ScreenshotOcrError error, string? name) => error switch
    {
        ScreenshotOcrError.UnreadableImage => $"“{name}” could not be opened as an image.",
        ScreenshotOcrError.UnsupportedFileType => $"“{name}” is not a supported image type. Use PNG, JPEG, HEIC or TIFF.",
        ScreenshotOcrError.ImageTooLarge => $"“{name}” is too large to process. Crop it to the assignment list and try again.",
        ScreenshotOcrError.RecognitionFailed => "Text could not be recognised in that screenshot.",
        ScreenshotOcrError.Cancelled => "Text recognition was cancelled.",
        _ => "Text could not be recognised in that screenshot.",
    };
}

/// <summary>
/// Recognises text. Deliberately knows nothing about Canvas: every assumption
/// about rows, deadlines and availability lives in the parser, so this layer
/// can be swapped for a stub in tests without losing that logic.
/// </summary>
/// <remarks>
/// <para>
/// An implementation returns one <see cref="OcrTextObservation"/> per line of
/// text, each stamped with <see cref="ScreenshotSource.Id"/>, with its box
/// already converted to the top-left, 0–1 convention
/// (<see cref="NormalisedRect.FromPixels"/>; a line's box encloses its words,
/// <see cref="NormalisedRect.Enclosing"/>). The order of the lines does not
/// matter: reading order is decided by the parser.
/// </para>
/// <para>
/// It throws <see cref="ScreenshotOcrException"/> for a screenshot it cannot
/// read, and <see cref="OperationCanceledException"/> when cancelled. It never
/// keeps the image or the text after returning, and never writes either
/// anywhere.
/// </para>
/// </remarks>
public interface IScreenshotOcrServicing
{
    Task<IReadOnlyList<OcrTextObservation>> RecognizeTextAsync(
        ScreenshotSource source,
        CancellationToken cancellationToken = default);
}

/// <summary>The size rules a recogniser applies before it reads a screenshot.</summary>
public static class ScreenshotOcrLimits
{
    /// <summary>
    /// Beyond this, in either direction, the image is scaled down before it is
    /// read. Never up: a crisp screenshot is already ideal input. Canvas text
    /// stays legible well below it, and recognition slows sharply on very
    /// large screenshots.
    /// </summary>
    public const int MaximumPixelDimension = 4_000;

    /// <summary>
    /// A screenshot larger than this in either direction is rejected with
    /// <see cref="ScreenshotOcrError.ImageTooLarge"/> rather than chewed on.
    /// </summary>
    public const int AbsolutePixelLimit = 12_000;
}

/// <summary>
/// Recognition stub for automated runs.
/// </summary>
/// <remarks>
/// Recognition output changes between operating-system versions, so no test is
/// allowed to depend on it. Tests supply their own observation fixtures.
/// </remarks>
public sealed class InertScreenshotOcrService : IScreenshotOcrServicing
{
    public Task<IReadOnlyList<OcrTextObservation>> RecognizeTextAsync(
        ScreenshotSource source,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OcrTextObservation>>([]);
}

/// <summary>
/// Stands in for a real recogniser, so the review can be exercised without
/// depending on recognition output: every screenshot reads as the same lines,
/// or as the same failure.
/// </summary>
public sealed class StubOcrService : IScreenshotOcrServicing
{
    private readonly ValueList<OcrTextObservation> observations;
    private readonly ScreenshotOcrError? failure;
    private readonly ValueSet<string> failingNames;

    private StubOcrService(
        ValueList<OcrTextObservation> observations,
        ScreenshotOcrError? failure,
        ValueSet<string> failingNames)
    {
        this.observations = observations;
        this.failure = failure;
        this.failingNames = failingNames;
    }

    /// <summary>
    /// Reads every screenshot as <paramref name="observations"/>, except those
    /// named in <paramref name="failingNames"/>, which cannot be opened.
    /// </summary>
    public static StubOcrService Returning(
        IEnumerable<OcrTextObservation> observations,
        IEnumerable<string>? failingNames = null)
        => new(observations.ToValueList(), null, (failingNames ?? []).ToValueSet());

    /// <summary>
    /// Fails every screenshot with <paramref name="error"/>; a cancellation is
    /// reported the way a cancelled recogniser reports one.
    /// </summary>
    public static StubOcrService Throwing(ScreenshotOcrError error)
        => new(ValueList<OcrTextObservation>.Empty, error, ValueSet<string>.Empty);

    public Task<IReadOnlyList<OcrTextObservation>> RecognizeTextAsync(
        ScreenshotSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (failingNames.Contains(source.DisplayName))
        {
            throw new ScreenshotOcrException(ScreenshotOcrError.UnreadableImage, source.DisplayName);
        }
        if (failure is { } error)
        {
            if (error == ScreenshotOcrError.Cancelled)
            {
                throw new OperationCanceledException();
            }
            throw new ScreenshotOcrException(error, source.DisplayName);
        }
        // Re-stamped with this screenshot's identifier, so several sources
        // behave like several screenshots.
        IReadOnlyList<OcrTextObservation> stamped = observations
            .Select(observation => observation with { ScreenshotId = source.Id })
            .ToArray();
        return Task.FromResult(stamped);
    }
}

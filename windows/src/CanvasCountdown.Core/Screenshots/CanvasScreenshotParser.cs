using System.Text.RegularExpressions;

namespace CanvasCountdown;

public interface IScreenshotAssignmentParsing
{
    /// <summary>
    /// The reviewable rows in one screenshot's recognised lines, with every
    /// date read in <paramref name="calendar"/>'s time zone.
    /// </summary>
    ValueList<ScreenshotImportCandidate> Candidates(
        IReadOnlyList<OcrTextObservation> observations,
        string imageName,
        DateTimeOffset now,
        AppCalendar calendar);
}

/// <summary>
/// Turns recognised text into reviewable assignment rows.
/// </summary>
/// <remarks>
/// Deterministic and layout-driven: observations are put into reading order,
/// grouped by vertical position into blocks, then each block is read for a
/// title and a "Due" line. Nothing here guesses a deadline that was not on
/// screen, and an availability line is never promoted into one.
/// </remarks>
public sealed class CanvasScreenshotParser : IScreenshotAssignmentParsing
{
    /// <summary>
    /// Page furniture that appears in Canvas screenshots but is never an
    /// assignment.
    /// </summary>
    public static ValueList<string> IgnoredPhrases { get; } =
    [
        "upcoming assignments",
        "show by date",
        "show by type",
        "search",
        "assignments",
        "dashboard",
        "courses",
        "calendar",
        "inbox",
        "history",
        "help",
        "account",
        "past assignments",
        "undated assignments",
    ];

    /// <summary>
    /// Lines whose tops are no further apart than this, as a fraction of the
    /// image height, share a row of text and are read left to right.
    /// </summary>
    public const double RowTolerance = 0.004;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly Regex PointsLine = new(@"^\d+(\.\d+)?\s*(pts|points|point)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Fraction of image height treated as the same block. Generous enough for
    /// a wrapped title plus its metadata, tight enough not to swallow the next
    /// assignment. Expressed as a fraction so browser zoom and display scaling
    /// do not change the result.
    /// </summary>
    private readonly double blockGap;

    public CanvasScreenshotParser(double blockGap = 0.035)
    {
        this.blockGap = blockGap;
    }

    public ValueList<ScreenshotImportCandidate> Candidates(
        IReadOnlyList<OcrTextObservation> observations,
        string imageName,
        DateTimeOffset now,
        AppCalendar calendar)
    {
        var usable = ReadingOrder(observations.Where(observation => !IsIgnoredChrome(observation.Text)));

        return Blocks(usable)
            .Select(block => Candidate(block, imageName, now, calendar))
            .OfType<ScreenshotImportCandidate>()
            .ToValueList();
    }

    /// <summary>
    /// The lines as a person reads them: rows from top to bottom, and within a
    /// row from left to right. Decided here and never by the recogniser, so the
    /// rows found cannot depend on the order a recogniser happens to return.
    /// </summary>
    /// <remarks>
    /// Lines whose tops are within <see cref="RowTolerance"/> of the first line
    /// of a row belong to that row. The macOS app sorted with a pairwise
    /// comparison carrying the same tolerance, which is not a consistent order
    /// when three lines chain within it (each close to the next, the first and
    /// last not), and .NET's sort may then throw or answer differently from run
    /// to run. Anchoring each row on its first line gives the same result
    /// wherever rows are clearly apart, and one fixed answer where they are not.
    /// </remarks>
    public static ValueList<OcrTextObservation> ReadingOrder(IEnumerable<OcrTextObservation> observations)
    {
        var byTop = observations
            .OrderBy(observation => observation.BoundingBox.MinY)
            .ThenBy(observation => observation.BoundingBox.MinX)
            .ToArray();

        var ordered = new List<OcrTextObservation>(byTop.Length);
        var start = 0;
        while (start < byTop.Length)
        {
            var rowTop = byTop[start].BoundingBox.MinY;
            var end = start + 1;
            while (end < byTop.Length && !(Math.Abs(byTop[end].BoundingBox.MinY - rowTop) > RowTolerance))
            {
                end++;
            }
            ordered.AddRange(byTop[start..end].OrderBy(observation => observation.BoundingBox.MinX));
            start = end;
        }
        return ordered.ToValueList();
    }

    /// <summary>
    /// Groups observations that sit close together vertically. A block breaks
    /// when the next line is far below the previous one, or when a due line has
    /// already been seen and a new non-metadata line starts.
    /// </summary>
    public ValueList<ValueList<OcrTextObservation>> Blocks(IReadOnlyList<OcrTextObservation> observations)
    {
        var result = new List<ValueList<OcrTextObservation>>();
        var current = new List<OcrTextObservation>();
        double? previousBottom = null;
        var currentHasDue = false;

        foreach (var observation in observations)
        {
            var isMetadata = CanvasDateTextParser.IsDueLine(observation.Text)
                || CanvasDateTextParser.IsAvailabilityLine(observation.Text);

            var farBelow = previousBottom is { } bottom && observation.BoundingBox.MinY - bottom > blockGap;
            var startsNewAssignment = currentHasDue && !isMetadata;

            if (current.Count > 0 && (farBelow || startsNewAssignment))
            {
                result.Add(current.ToValueList());
                current = [];
                currentHasDue = false;
            }

            current.Add(observation);
            if (CanvasDateTextParser.IsDueLine(observation.Text))
            {
                currentHasDue = true;
            }
            previousBottom = observation.BoundingBox.MaxY;
        }

        if (current.Count > 0)
        {
            result.Add(current.ToValueList());
        }
        return result.ToValueList();
    }

    private static ScreenshotImportCandidate? Candidate(
        ValueList<OcrTextObservation> block,
        string imageName,
        DateTimeOffset now,
        AppCalendar calendar)
    {
        var dueLines = block.Where(observation => CanvasDateTextParser.IsDueLine(observation.Text)).ToArray();
        var titleLines = block
            .Where(observation => !CanvasDateTextParser.IsDueLine(observation.Text)
                && !CanvasDateTextParser.IsAvailabilityLine(observation.Text)
                && !IsPointsLine(observation.Text))
            .ToArray();

        var title = WhitespaceRun
            .Replace(string.Join(" ", titleLines.Select(line => line.Text)), " ")
            .Trim();

        // A block with neither a name nor a deadline is page furniture.
        if (title.Length == 0 && dueLines.Length == 0)
        {
            return null;
        }
        if (title.Length == 0)
        {
            return null;
        }

        var warnings = new List<ScreenshotImportWarning>();
        ParsedScreenshotDate? parsed = null;

        if (dueLines.Length == 0)
        {
            warnings.Add(ScreenshotImportWarning.MissingDueDate);
        }
        else
        {
            // Multiple "Due" phrases in one block cannot be resolved safely, so
            // the first is offered and the ambiguity is declared.
            if (dueLines.Length > 1)
            {
                warnings.Add(ScreenshotImportWarning.MultipleDueDates);
            }
            parsed = dueLines
                .Select(line => CanvasDateTextParser.Parse(line.Text, now, calendar))
                .FirstOrDefault(result => result is not null);
            if (parsed is null)
            {
                warnings.Add(ScreenshotImportWarning.MissingDueDate);
            }
        }

        if (parsed is not null)
        {
            warnings.AddRange(parsed.Warnings);
        }

        var ocrConfidence = MeanConfidence(block);
        if (ocrConfidence < 0.5)
        {
            warnings.Add(ScreenshotImportWarning.LowConfidence);
        }

        var dueText = string.Join(" · ", dueLines.Select(line => line.Text));

        return new ScreenshotImportCandidate
        {
            SourceScreenshotId = block[0].ScreenshotId,
            SourceImageName = imageName,
            Title = title,
            DueDate = parsed?.Date,
            OriginalTitleText = string.Join(" ", titleLines.Select(line => line.Text)),
            OriginalDueText = dueText,
            OcrConfidence = ocrConfidence,
            ParserConfidence = ParserConfidence(parsed is not null, warnings),
            Warnings = warnings.Distinct().OrderBy(warning => warning.Message, StringComparer.Ordinal).ToValueList(),
            InferredYear = parsed?.InferredYear,
            CorrectedOcrText = parsed?.CorrectedText,
            BoundingRegion = NormalisedRect.Enclosing(block.Select(observation => observation.BoundingBox)),
        };
    }

    public static bool IsIgnoredChrome(string text)
    {
        var normalised = CanvasDateTextParser.NormalisedForMatching(text);
        if (normalised.Length == 0)
        {
            return true;
        }
        return IgnoredPhrases.Contains(normalised);
    }

    /// <summary>"10 pts" is a score, never a date.</summary>
    public static bool IsPointsLine(string text)
        => PointsLine.IsMatch(CanvasDateTextParser.NormalisedForMatching(text));

    /// <summary>
    /// The mean over the lines whose confidence is known, or null when none is:
    /// a recogniser that reports no confidence is not taken to be uncertain.
    /// Summed in line order, as the macOS app summed it.
    /// </summary>
    private static double? MeanConfidence(IReadOnlyList<OcrTextObservation> block)
    {
        var total = 0.0;
        var known = 0;
        foreach (var observation in block)
        {
            if (observation.Confidence is { } confidence)
            {
                total += confidence;
                known++;
            }
        }
        return known == 0 ? null : total / known;
    }

    private static double ParserConfidence(bool hasDue, IReadOnlyCollection<ScreenshotImportWarning> warnings)
    {
        if (!hasDue)
        {
            return 0;
        }
        var penalty = warnings.Count * 0.15;
        return Math.Max(0.1, Math.Min(1, 1 - penalty));
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CanvasCountdown;

/// <summary>
/// Flags rows that look like something already stored.
/// </summary>
/// <remarks>
/// Marks only; never merges or deletes on its own. A screenshot is a lossy
/// picture of Canvas, so it is never allowed to overwrite authoritative feed
/// data without the user saying so.
/// </remarks>
public static class ScreenshotDuplicateDetector
{
    private static readonly Regex CurlyQuotes = new("[‘’“”]", RegexOptions.CultureInvariant);

    private static readonly Regex Punctuation = new(@"[\p{P}]", RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Same title on the same calendar day is treated as the same assignment.
    /// Requiring the exact minute would miss a screenshot read as 23:59 against
    /// a feed time of 23:59:59, while ignoring the day entirely would collapse
    /// weekly tasks that share a name.
    /// </summary>
    public static ValueList<ScreenshotImportCandidate> Annotate(
        IEnumerable<ScreenshotImportCandidate> candidates,
        IReadOnlyList<AssignmentSnapshot> existing,
        AppCalendar calendar)
        => candidates.Select(candidate => Annotate(candidate, existing, calendar)).ToValueList();

    private static ScreenshotImportCandidate Annotate(
        ScreenshotImportCandidate candidate,
        IReadOnlyList<AssignmentSnapshot> existing,
        AppCalendar calendar)
    {
        if (candidate.DueDate is not { } dueDate)
        {
            return candidate;
        }
        var title = NormalisedTitle(candidate.Title);
        if (title.Length == 0)
        {
            return candidate;
        }

        var match = existing.FirstOrDefault(stored =>
            NormalisedTitle(stored.Title) == title
            && calendar.IsSameDay(stored.DueDate, dueDate)
            && CoursesAgree(candidate.CourseName, stored.CourseName));

        if (match is null)
        {
            return candidate;
        }
        return candidate with
        {
            PossibleDuplicate = true,
            DuplicateTargetId = match.Id,
            // A strong match defaults to skipping, which is the choice that
            // cannot lose anything.
            SelectedDuplicateAction = DuplicateResolution.Skip,
        };
    }

    /// <summary>
    /// Only a stated disagreement counts. A screenshot usually has no course at
    /// all, and that absence should not stop a duplicate being spotted.
    /// </summary>
    public static bool CoursesAgree(string? left, string? right)
    {
        if (NormalisedOptional(left) is not { } leftCourse || NormalisedOptional(right) is not { } rightCourse)
        {
            return true;
        }
        return leftCourse == rightCourse;
    }

    /// <summary>
    /// Folds away the differences OCR introduces without merging genuinely
    /// different names.
    /// </summary>
    public static string NormalisedTitle(string title)
    {
        var lowered = title.ToLowerInvariant();
        lowered = CurlyQuotes.Replace(lowered, "'");
        lowered = Punctuation.Replace(lowered, " ");
        lowered = WhitespaceRun.Replace(lowered, " ");
        return lowered.Trim();
    }

    private static string? NormalisedOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var normalised = NormalisedTitle(value);
        return normalised.Length == 0 ? null : normalised;
    }
}

/// <summary>
/// What one confirmed import wrote, so that it can be taken back.
/// </summary>
/// <remarks>
/// A batch can get several rows wrong at once, so the way back has to undo the
/// batch, not one row at a time. This holds the stored rows themselves — the
/// confirmed fields only, never an image or a recognised line — and
/// <see cref="IScreenshotImportCoordinating.UndoAsync"/> reverses exactly them.
/// </remarks>
public sealed record ScreenshotImportCommit
{
    public required ScreenshotImportSummary Summary { get; init; }

    /// <summary>The rows this import created, as stored. Undoing deletes exactly these.</summary>
    public ValueList<AssignmentSnapshot> Added { get; init; } = ValueList<AssignmentSnapshot>.Empty;

    /// <summary>
    /// The rows this import changed, as they were before it — earlier imports
    /// of the same screenshot rows. Undoing writes these values back.
    /// </summary>
    public ValueList<AssignmentSnapshot> Replaced { get; init; } = ValueList<AssignmentSnapshot>.Empty;

    public bool CanUndo => !Added.IsEmpty || !Replaced.IsEmpty;
}

/// <summary>What undoing an import changed.</summary>
public sealed record ScreenshotImportUndoResult(int RemovedCount, int RestoredCount);

public interface IScreenshotImportCoordinating
{
    /// <summary>
    /// Recognises, parses and duplicate-checks, returning a session for review.
    /// Nothing is written to storage by this call.
    /// </summary>
    Task<ScreenshotImportSession> BuildSessionAsync(
        IReadOnlyList<ScreenshotSource> sources,
        IReadOnlyList<AssignmentSnapshot> existing,
        DateTimeOffset now,
        AppCalendar calendar,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes only the rows the user confirmed, and answers what was written so
    /// it can be undone.
    /// </summary>
    Task<ScreenshotImportCommit> CommitAsync(IReadOnlyList<ScreenshotImportCandidate> candidates, DateTimeOffset now);

    /// <summary>
    /// Takes back exactly what one commit wrote: deletes the rows it added and
    /// puts back the values it replaced. A row deleted since is left gone, and
    /// nothing else is touched.
    /// </summary>
    Task<ScreenshotImportUndoResult> UndoAsync(ScreenshotImportCommit commit, DateTimeOffset now);
}

/// <summary>
/// Runs a screenshot import: recognition, parsing and duplicate checks for the
/// review, then the one write the user confirms.
/// </summary>
/// <remarks>
/// Holds no state of its own beyond what it was given, so it is safe to call
/// from any thread. Every write goes through <see cref="IAssignmentRepository"/>,
/// the same store every other change uses.
/// </remarks>
public sealed class ScreenshotImportCoordinator : IScreenshotImportCoordinating
{
    /// <summary>Foundation's reference date, which the macOS identifier counts days from.</summary>
    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IScreenshotOcrServicing ocr;
    private readonly IScreenshotAssignmentParsing parser;
    private readonly IAssignmentRepository repository;

    public ScreenshotImportCoordinator(
        IScreenshotOcrServicing ocr,
        IAssignmentRepository repository,
        IScreenshotAssignmentParsing? parser = null)
    {
        this.ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.parser = parser ?? new CanvasScreenshotParser();
    }

    public async Task<ScreenshotImportSession> BuildSessionAsync(
        IReadOnlyList<ScreenshotSource> sources,
        IReadOnlyList<AssignmentSnapshot> existing,
        DateTimeOffset now,
        AppCalendar calendar,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<ScreenshotImportCandidate>();
        var names = ImmutableDictionary.CreateBuilder<Guid, string>();
        var failed = new List<string>();
        var recognisedNothing = new List<string>();

        foreach (var source in sources)
        {
            names[source.Id] = source.DisplayName;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observations = await ocr.RecognizeTextAsync(source, cancellationToken).ConfigureAwait(false);
                if (observations.Count == 0)
                {
                    recognisedNothing.Add(source.DisplayName);
                    continue;
                }
                var found = parser.Candidates(observations, source.DisplayName, now, calendar);
                if (found.IsEmpty)
                {
                    recognisedNothing.Add(source.DisplayName);
                }
                candidates.AddRange(found);
            }
            catch (OperationCanceledException)
            {
                // A cancelled run keeps whatever finished, and adds nothing.
                // What it keeps is still checked for duplicates below: a row
                // that matches a stored event must never reach review looking
                // ready to import.
                break;
            }
            catch (Exception)
            {
                // One unreadable screenshot must not lose the others.
                failed.Add(source.DisplayName);
            }
        }

        return new ScreenshotImportSession
        {
            Candidates = ScreenshotDuplicateDetector.Annotate(candidates, existing, calendar),
            ScreenshotNames = names.ToImmutable(),
            FailedImageNames = failed.ToValueList(),
            RecognisedNothingNames = recognisedNothing.ToValueList(),
        };
    }

    public async Task<ScreenshotImportCommit> CommitAsync(
        IReadOnlyList<ScreenshotImportCandidate> candidates,
        DateTimeOffset now)
    {
        var skipped = 0;
        var failed = 0;
        var records = new List<AssignmentImportRecord>();

        foreach (var candidate in candidates)
        {
            if (!candidate.WillBeImported || candidate.DueDate is not { } dueDate)
            {
                skipped++;
                continue;
            }
            var title = candidate.TrimmedTitle;
            if (title.Length == 0)
            {
                failed++;
                continue;
            }

            records.Add(new AssignmentImportRecord
            {
                // A stable local identifier, so re-importing the same
                // screenshot updates its row instead of duplicating it.
                ExternalId = LocalIdentifier(title, dueDate, candidate.CourseName),
                Title = title,
                CourseName = candidate.CourseName,
                DueDate = dueDate,
                Source = AssignmentSource.Screenshot,
            });
        }

        if (records.Count == 0)
        {
            return new ScreenshotImportCommit
            {
                Summary = new ScreenshotImportSummary { Added = 0, Updated = 0, Skipped = skipped, Failed = failed },
            };
        }

        var identifiers = records.Select(record => record.ExternalId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Read before writing, so the commit can say which rows it created and
        // what the rows it changed held before. If that cannot be known,
        // nothing is written: a batch is never saved without a way back.
        var before = await RowsWrittenAsync(identifiers).ConfigureAwait(false);

        // One write for the whole batch: a failure part-way leaves nothing
        // half-applied.
        var result = await repository.UpsertAsync(records, now).ConfigureAwait(false);

        var after = await RowsWrittenAsync(identifiers).ConfigureAwait(false);
        var beforeById = before.ToDictionary(row => row.Id);

        return new ScreenshotImportCommit
        {
            Summary = new ScreenshotImportSummary
            {
                Added = result.InsertedCount,
                Updated = result.UpdatedCount,
                Skipped = skipped + result.UnchangedCount,
                Failed = failed,
            },
            Added = after.Where(row => !beforeById.ContainsKey(row.Id)).ToValueList(),
            Replaced = after
                .Where(row => beforeById.TryGetValue(row.Id, out var previous) && ContentDiffers(previous, row))
                .Select(row => beforeById[row.Id])
                .ToValueList(),
        };
    }

    public async Task<ScreenshotImportUndoResult> UndoAsync(ScreenshotImportCommit commit, DateTimeOffset now)
    {
        var removed = 0;
        foreach (var row in commit.Added)
        {
            try
            {
                await repository.DeleteAsync(row.Id).ConfigureAwait(false);
                removed++;
            }
            catch (AssignmentRepositoryException error) when (error.Error == AssignmentRepositoryError.AssignmentNotFound)
            {
                // Already gone, by hand or by a reset. Nothing to undo.
            }
        }

        if (commit.Replaced.IsEmpty)
        {
            return new ScreenshotImportUndoResult(removed, 0);
        }

        var stillStored = (await repository.FetchAllAsync(includingArchived: true).ConfigureAwait(false))
            .Select(row => row.Id)
            .ToHashSet();
        var restores = commit.Replaced
            .Where(previous => stillStored.Contains(previous.Id))
            .Select(previous => new AssignmentImportRecord
            {
                ExternalId = previous.ExternalId,
                Title = previous.Title,
                CourseName = previous.CourseName,
                DueDate = previous.DueDate,
                Source = previous.Source,
                SourceUrl = previous.SourceUrl,
            })
            .ToList();
        if (restores.Count > 0)
        {
            await repository.UpsertAsync(restores, now).ConfigureAwait(false);
        }
        return new ScreenshotImportUndoResult(removed, restores.Count);
    }

    /// <summary>
    /// Derived from the confirmed fields only. No image bytes, no OCR
    /// transcript, nothing that could carry recognised text into storage.
    /// </summary>
    /// <remarks>
    /// The day is the number of whole UTC days since 1 January 2001, worked
    /// out exactly as the macOS app works it out, so the same confirmed row
    /// gets the same identifier on either platform. It names a row; it is not
    /// a calendar computation, and nothing counts days with it.
    /// </remarks>
    public static string LocalIdentifier(string title, DateTimeOffset dueDate, string? courseName)
    {
        var normalisedTitle = ScreenshotDuplicateDetector.NormalisedTitle(title);
        var course = ScreenshotDuplicateDetector.NormalisedTitle(courseName ?? "");
        var day = (long)((dueDate - ReferenceDate).TotalSeconds / 86_400);
        return string.Create(CultureInfo.InvariantCulture, $"screenshot|{normalisedTitle}|{course}|{day}");
    }

    /// <summary>Stored screenshot rows carrying one of these identifiers, as the repository matches them.</summary>
    private async Task<IReadOnlyList<AssignmentSnapshot>> RowsWrittenAsync(HashSet<string> identifiers)
    {
        var stored = await repository.FetchAllAsync(includingArchived: true).ConfigureAwait(false);
        return stored
            .Where(row => row.Source == AssignmentSource.Screenshot
                && row.ExternalId is { } externalId
                && identifiers.Contains(externalId.Trim()))
            .ToArray();
    }

    private static bool ContentDiffers(AssignmentSnapshot previous, AssignmentSnapshot current)
        => previous.ExternalId != current.ExternalId
            || previous.Title != current.Title
            || previous.CourseName != current.CourseName
            || previous.DueDate != current.DueDate
            || previous.SourceUrl != current.SourceUrl;
}

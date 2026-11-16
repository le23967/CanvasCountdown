namespace CanvasCountdown;

/// <summary>
/// Everything a repository needs to decide what a single successful feed
/// refresh implies for the stored Canvas rows.
/// </summary>
public sealed record FeedReconciliationRequest
{
    /// <summary>
    /// Consecutive authoritative refreshes an event must be absent from before
    /// it is archived. One incomplete feed must never be enough.
    /// </summary>
    public const int DefaultArchiveThreshold = 3;

    public required ValueSet<string> ActiveExternalIds { get; init; }

    public ValueSet<string> CancelledExternalIds { get; init; } = ValueSet<string>.Empty;

    public ValueSet<string> ExcludedExternalIds { get; init; } = ValueSet<string>.Empty;

    public required DateTimeOffset ObservedAt { get; init; }

    public int ArchiveThreshold { get; init; } = DefaultArchiveThreshold;

    /// <summary>
    /// False when the feed parsed but carried no events at all. Such a response
    /// is treated as suspicious rather than authoritative, so absence is not
    /// counted against anything already stored.
    /// </summary>
    public bool CountsAbsentEventsAsMissing { get; init; } = true;
}

public sealed record FeedReconciliationOutcome
{
    public int ConfirmedCount { get; init; }

    public int NewlyMissingCount { get; init; }

    public int ArchivedCount { get; init; }

    public int RestoredCount { get; init; }

    public static FeedReconciliationOutcome Empty { get; } = new();
}

/// <summary>
/// The store of events. Every write the app makes goes through here.
/// </summary>
public interface IAssignmentRepository
{
    /// <summary>Everything not archived, earliest deadline first.</summary>
    Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync();

    /// <summary>
    /// Archived rows are retained on disk but kept out of the working set, so
    /// nothing the user completed or ignored is ever lost to a feed change.
    /// </summary>
    Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync(bool includingArchived) => FetchAllAsync();

    Task<ImportResult> UpsertAsync(IReadOnlyList<AssignmentImportRecord> records, DateTimeOffset importedAt);

    Task<AssignmentSnapshot> SaveManualAsync(ManualAssignmentDraft draft, DateTimeOffset now);

    Task UpdateStatusAsync(Guid id, bool? isCompleted, bool? isIgnored, DateTimeOffset now);

    /// <summary>Puts the user's label on an event, or takes it off with null.</summary>
    Task SetLabelAsync(Guid id, Guid? labelId, DateTimeOffset now);

    /// <summary>
    /// Takes a deleted label off everything carrying it, so a colour cannot
    /// outlive the label it belonged to. Answers how many events changed.
    /// </summary>
    Task<int> ClearLabelAsync(Guid labelId, DateTimeOffset now);

    /// <summary>
    /// Records what an authoritative feed refresh said about the stored Canvas
    /// rows. Rows are archived, never deleted, and only after repeated absence.
    /// Manual events are never affected.
    /// </summary>
    /// <remarks>
    /// Test doubles and alternate repositories may opt out. The production
    /// repository implements authoritative reconciliation.
    /// </remarks>
    Task<FeedReconciliationOutcome> ReconcileCanvasFeedAsync(FeedReconciliationRequest request)
        => Task.FromResult(FeedReconciliationOutcome.Empty);

    /// <summary>
    /// Removes every event belonging to one course, archived ones included, and
    /// answers how many went. Used when a course wandered in from the Canvas
    /// feed and does not belong here at all.
    /// </summary>
    Task<int> DeleteEventsInCourseAsync(string courseName) => Task.FromResult(0);

    Task DeleteAsync(Guid id);

    Task DeleteAllAsync();
}

public static class AssignmentRepositoryExtensions
{
    public static Task<ImportResult> UpsertAsync(this IAssignmentRepository repository, IReadOnlyList<AssignmentImportRecord> records)
        => repository.UpsertAsync(records, DateTimeOffset.UtcNow);

    public static Task<AssignmentSnapshot> SaveManualAsync(this IAssignmentRepository repository, ManualAssignmentDraft draft)
        => repository.SaveManualAsync(draft, DateTimeOffset.UtcNow);

    public static Task SetCompletedAsync(this IAssignmentRepository repository, Guid id, bool isCompleted)
        => repository.UpdateStatusAsync(id, isCompleted, null, DateTimeOffset.UtcNow);

    public static Task SetIgnoredAsync(this IAssignmentRepository repository, Guid id, bool isIgnored)
        => repository.UpdateStatusAsync(id, null, isIgnored, DateTimeOffset.UtcNow);
}

public enum AssignmentRepositoryError
{
    EmptyTitle,
    AssignmentNotFound,
    CannotEditImportedAssignmentAsManual,
}

public sealed class AssignmentRepositoryException : Exception
{
    public AssignmentRepositoryException(AssignmentRepositoryError error)
        : base(Describe(error))
    {
        Error = error;
    }

    public AssignmentRepositoryError Error { get; }

    private static string Describe(AssignmentRepositoryError error) => error switch
    {
        AssignmentRepositoryError.EmptyTitle => "Enter an assignment name.",
        AssignmentRepositoryError.AssignmentNotFound => "The assignment no longer exists.",
        AssignmentRepositoryError.CannotEditImportedAssignmentAsManual => "Only manually created assignments can be edited this way.",
        _ => "The assignment could not be saved.",
    };
}

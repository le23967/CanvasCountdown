namespace CanvasCountdown;

public enum AssignmentSource
{
    CanvasCalendarFeed,
    Manual,
    Screenshot,
}

/// <summary>
/// One stored event, as read out of the database. Changing it means asking the
/// repository; this value is a copy.
/// </summary>
public sealed record AssignmentSnapshot : ICountdownEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string? ExternalId { get; init; }

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    /// <summary>As stored: whatever the feed or the user said. See <see cref="DueTimePolicy"/> for what is shown.</summary>
    public required DateTimeOffset DueDate { get; init; }

    public required AssignmentSource Source { get; init; }

    public string? SourceUrl { get; init; }

    public bool IsCompleted { get; init; }

    public bool IsIgnored { get; init; }

    /// <summary>
    /// The user's own label, if they gave it one. A feed refresh never writes
    /// it: it is the user's mark, not Canvas's.
    /// </summary>
    public Guid? LabelId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Feed presence bookkeeping. A Canvas row is never deleted just because
    /// one refresh did not mention it; it is counted, and only archived once it
    /// has been absent from several consecutive authoritative feeds.
    /// </summary>
    public DateTimeOffset? LastSeenInFeedAt { get; init; }

    public int MissingRefreshCount { get; init; }

    public bool IsArchived { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>
    /// True once a feed refresh has failed to mention this Canvas event, before
    /// it has been absent often enough to be archived.
    /// </summary>
    public bool IsMissingFromFeed => MissingRefreshCount > 0 && !IsArchived;

    /// <summary>
    /// Earliest deadline first. Equal deadlines sort by title the way a file
    /// browser does — ignoring case, with "Quiz 2" before "Quiz 10" — and then
    /// by id, so the order never flickers between two reads.
    /// </summary>
    public static int DueDateOrder(AssignmentSnapshot left, AssignmentSnapshot right)
    {
        var byDate = left.DueDate.CompareTo(right.DueDate);
        if (byDate != 0)
        {
            return byDate;
        }
        var byTitle = TextOrder.Compare(left.Title, right.Title);
        return byTitle != 0 ? byTitle : left.Id.CompareTo(right.Id);
    }
}

/// <summary>What an import (feed, screenshot) hands to the repository.</summary>
public sealed record AssignmentImportRecord
{
    public string? ExternalId { get; init; }

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    public required DateTimeOffset DueDate { get; init; }

    public AssignmentSource Source { get; init; } = AssignmentSource.CanvasCalendarFeed;

    public string? SourceUrl { get; init; }
}

/// <summary>
/// A manual event to create (no <see cref="Id"/>) or change. Everything the
/// assistant or a screenshot produces is saved through this same path, so
/// validation lives in one place.
/// </summary>
public sealed record ManualAssignmentDraft
{
    public Guid? Id { get; init; }

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    public required DateTimeOffset DueDate { get; init; }

    public string? SourceUrl { get; init; }
}

public sealed record ImportResult(int InsertedCount, int UpdatedCount, int UnchangedCount)
{
    public static ImportResult Empty { get; } = new(0, 0, 0);
}

namespace CanvasCountdown;

/// <summary>An event that may be reminded about.</summary>
public sealed record NotificationCandidate
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    /// <summary>The due date as shown, after <see cref="DueTimePolicy"/>.</summary>
    public required DateTimeOffset DueDate { get; init; }

    public bool IsCompleted { get; init; }

    public bool IsIgnored { get; init; }

    /// <summary>The candidate as stored. The due date is whatever the feed said.</summary>
    public static NotificationCandidate FromSnapshot(AssignmentSnapshot snapshot)
        => FromSnapshot(snapshot, treatsMidnightAsEndOfDay: false, AppCalendar.Current);

    /// <summary>
    /// Reminders count back from the deadline the user is shown, so the same
    /// correction has to reach them: "1 day before" a 23:59 deadline is the
    /// evening before, not the previous midnight.
    /// </summary>
    public static NotificationCandidate FromSnapshot(
        AssignmentSnapshot snapshot,
        bool treatsMidnightAsEndOfDay,
        AppCalendar calendar)
        => new()
        {
            Id = snapshot.Id,
            Title = snapshot.Title,
            CourseName = snapshot.CourseName,
            DueDate = DueTimePolicy.EffectiveDueDate(
                snapshot.DueDate,
                snapshot.Source == AssignmentSource.Manual,
                treatsMidnightAsEndOfDay,
                calendar),
            IsCompleted = snapshot.IsCompleted,
            IsIgnored = snapshot.IsIgnored,
        };
}

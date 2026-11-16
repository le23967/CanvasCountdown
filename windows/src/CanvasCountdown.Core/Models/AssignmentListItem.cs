namespace CanvasCountdown;

/// <summary>
/// An event as the lists, the calendar and the countdown show it.
/// </summary>
public sealed record AssignmentListItem : ICountdownEvent
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    /// <summary>The due date as shown, after <see cref="DueTimePolicy"/>.</summary>
    public required DateTimeOffset DueDate { get; init; }

    public bool IsCompleted { get; init; }

    public bool IsIgnored { get; init; }

    public bool IsManual { get; init; }

    /// <summary>
    /// The user's label, if any. Carried on the item so every list, grid and
    /// chip can colour itself without reaching back into settings.
    /// </summary>
    public Guid? LabelId { get; init; }

    public int RemainingDays(DateTimeOffset now, AppCalendar calendar) => calendar.DaysBetween(now, DueDate);

    public string? NormalizedCourseName
    {
        get
        {
            var trimmed = CourseName?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
    }

    /// <summary>The list item as stored. The due date is whatever the feed said.</summary>
    public static AssignmentListItem FromSnapshot(AssignmentSnapshot snapshot)
        => FromSnapshot(snapshot, treatsMidnightAsEndOfDay: false, AppCalendar.Current);

    /// <summary>
    /// The list item as shown, with a midnight Canvas deadline read as the end
    /// of that day. Applied here rather than in storage so the correction can be
    /// turned off and take effect at once, and so nothing the feed sent is lost.
    /// </summary>
    public static AssignmentListItem FromSnapshot(
        AssignmentSnapshot snapshot,
        bool treatsMidnightAsEndOfDay,
        AppCalendar calendar)
    {
        var isManual = snapshot.Source == AssignmentSource.Manual;
        return new AssignmentListItem
        {
            Id = snapshot.Id,
            Title = snapshot.Title,
            CourseName = snapshot.CourseName,
            DueDate = DueTimePolicy.EffectiveDueDate(
                snapshot.DueDate,
                isManual,
                treatsMidnightAsEndOfDay,
                calendar),
            IsCompleted = snapshot.IsCompleted,
            IsIgnored = snapshot.IsIgnored,
            IsManual = isManual,
            LabelId = snapshot.LabelId,
        };
    }
}

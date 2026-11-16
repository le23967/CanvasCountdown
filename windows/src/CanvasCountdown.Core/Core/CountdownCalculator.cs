namespace CanvasCountdown;

/// <summary>The minimal view of an assignment needed by the countdown engine.</summary>
public interface ICountdownEvent
{
    DateTimeOffset DueDate { get; }

    bool IsCompleted { get; }

    bool IsIgnored { get; }
}

public sealed record CountdownSelection<TEvent>(TEvent Event, int DaysRemaining);

public static class CountdownCalculator
{
    /// <summary>
    /// The difference between the two local calendar dates.
    /// </summary>
    /// <remarks>
    /// This intentionally does not divide elapsed seconds by 86,400, which would
    /// be incorrect around daylight-saving transitions and near midnight.
    /// </remarks>
    public static int CalendarDayDifference(DateTimeOffset start, DateTimeOffset end, AppCalendar calendar)
        => calendar.DaysBetween(start, end);

    public static int DaysRemaining(DateTimeOffset dueDate, DateTimeOffset now, AppCalendar calendar)
        => CalendarDayDifference(now, dueDate, calendar);

    /// <summary>
    /// The first chronologically nearest event that has not passed.
    /// </summary>
    /// <remarks>
    /// Completed and ignored events never participate. Equal due dates preserve
    /// input order, which keeps the UI stable across repeated recalculations.
    /// </remarks>
    public static CountdownSelection<TEvent>? NearestUpcoming<TEvent>(
        IEnumerable<TEvent> events,
        DateTimeOffset now,
        AppCalendar calendar)
        where TEvent : ICountdownEvent
    {
        TEvent? nearest = default;
        var found = false;

        foreach (var candidate in events)
        {
            if (candidate.IsCompleted || candidate.IsIgnored || candidate.DueDate < now)
            {
                continue;
            }
            if (!found || candidate.DueDate < nearest!.DueDate)
            {
                nearest = candidate;
                found = true;
            }
        }

        if (!found)
        {
            return null;
        }
        return new CountdownSelection<TEvent>(
            nearest!,
            DaysRemaining(nearest!.DueDate, now, calendar));
    }

    /// <summary>
    /// The next instant at which the countdown's selection or its calendar-day
    /// value can change.
    /// </summary>
    /// <remarks>
    /// A countdown changes at local midnight, while the selected assignment can
    /// change at its exact due time. Scheduling the earlier transition avoids
    /// leaving an overdue assignment in the countdown until the next refresh.
    /// </remarks>
    public static DateTimeOffset? NextTransitionDate<TEvent>(
        IEnumerable<TEvent> events,
        DateTimeOffset now,
        AppCalendar calendar)
        where TEvent : ICountdownEvent
    {
        var nextMidnight = calendar.StartOfNextDay(now);
        DateTimeOffset? nextDueDate = null;
        foreach (var candidate in events)
        {
            if (candidate.IsCompleted || candidate.IsIgnored || candidate.DueDate <= now)
            {
                continue;
            }
            if (nextDueDate is null || candidate.DueDate < nextDueDate)
            {
                nextDueDate = candidate.DueDate;
            }
        }

        return nextDueDate is { } dueDate && dueDate < nextMidnight ? dueDate : nextMidnight;
    }
}

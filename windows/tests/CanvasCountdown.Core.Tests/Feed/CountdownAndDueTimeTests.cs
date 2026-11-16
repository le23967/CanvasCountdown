namespace CanvasCountdown.Tests.Feed;

/// <summary>
/// The two rules a rewrite gets wrong first: the countdown counts calendar
/// days, and a Canvas midnight is the end of that day.
/// </summary>
public sealed class CountdownCalculatorTests
{
    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    private sealed record Event(DateTimeOffset DueDate, bool IsCompleted = false, bool IsIgnored = false, string Name = "") : ICountdownEvent;

    [Fact]
    public void LaterTodayIsZeroCalendarDaysAway()
        => Assert.Equal(0, CountdownCalculator.DaysRemaining(Sydney.FromLocal(2026, 8, 14, 23, 59), Sydney.FromLocal(2026, 8, 14, 0, 1), Sydney));

    [Fact]
    public void TomorrowIsOneCalendarDayAway()
        => Assert.Equal(1, CountdownCalculator.DaysRemaining(Sydney.FromLocal(2026, 8, 15, 23, 0), Sydney.FromLocal(2026, 8, 14, 9, 0), Sydney));

    [Fact]
    public void CrossingMidnightIsOneDayEvenWhenMinutesApart()
        => Assert.Equal(1, CountdownCalculator.DaysRemaining(Sydney.FromLocal(2026, 8, 15, 0, 2), Sydney.FromLocal(2026, 8, 14, 23, 58), Sydney));

    [Fact]
    public void CalendarDaysRemainCorrectOverSpringDst()
    {
        // 4 October 2026: Sydney's clocks jump forward, so the day is 23 hours.
        var now = Sydney.FromLocal(2026, 10, 3, 12, 0);
        var due = Sydney.FromLocal(2026, 10, 5, 11, 0);

        Assert.Equal(2, CountdownCalculator.DaysRemaining(due, now, Sydney));
        Assert.True((due - now).TotalHours < 48);
    }

    [Fact]
    public void CalendarDaysRemainCorrectOverAutumnDst()
    {
        // 5 April 2026: the clocks go back, so the day is 25 hours.
        var now = Sydney.FromLocal(2026, 4, 4, 0, 30);
        var due = Sydney.FromLocal(2026, 4, 5, 23, 45);

        Assert.Equal(1, CountdownCalculator.DaysRemaining(due, now, Sydney));
        Assert.True((due - now).TotalHours > 48);
    }

    [Fact]
    public void NearestSelectionExcludesOverdueCompletedAndIgnoredEvents()
    {
        var now = Sydney.FromLocal(2026, 8, 14, 12, 0);
        Event[] events =
        [
            new(Sydney.FromLocal(2026, 8, 13, 23, 59), Name: "overdue"),
            new(Sydney.FromLocal(2026, 8, 15, 9, 0), IsCompleted: true, Name: "done"),
            new(Sydney.FromLocal(2026, 8, 15, 10, 0), IsIgnored: true, Name: "ignored"),
            new(Sydney.FromLocal(2026, 8, 20, 9, 0), Name: "later"),
            new(Sydney.FromLocal(2026, 8, 16, 9, 0), Name: "nearest"),
        ];

        var selection = CountdownCalculator.NearestUpcoming(events, now, Sydney);

        Assert.Equal("nearest", selection!.Event.Name);
        Assert.Equal(2, selection.DaysRemaining);
    }

    [Fact]
    public void EarlierTodayIsOverdueAndExcluded()
    {
        var now = Sydney.FromLocal(2026, 8, 14, 12, 0);

        Assert.Null(CountdownCalculator.NearestUpcoming([new Event(Sydney.FromLocal(2026, 8, 14, 9, 0))], now, Sydney));
    }

    [Fact]
    public void EqualDueDatesPreserveInputOrder()
    {
        var due = Sydney.FromLocal(2026, 8, 20, 17, 0);
        var now = Sydney.FromLocal(2026, 8, 14, 12, 0);

        var selection = CountdownCalculator.NearestUpcoming([new Event(due, Name: "first"), new Event(due, Name: "second")], now, Sydney);

        Assert.Equal("first", selection!.Event.Name);
    }

    [Fact]
    public void TheNextChangeIsMidnightOrTheNearestDeadlineWhicheverComesFirst()
    {
        var now = Sydney.FromLocal(2026, 8, 14, 12, 0);

        Assert.Equal(Sydney.FromLocal(2026, 8, 15), CountdownCalculator.NextTransitionDate([new Event(Sydney.FromLocal(2026, 8, 20, 9, 0))], now, Sydney));
        Assert.Equal(Sydney.FromLocal(2026, 8, 14, 17, 0), CountdownCalculator.NextTransitionDate([new Event(Sydney.FromLocal(2026, 8, 14, 17, 0))], now, Sydney));
    }
}

public sealed class DueTimePolicyTests
{
    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    [Fact]
    public void MidnightBecomesTheEndOfTheSameDay()
        => Assert.Equal(
            Sydney.FromLocal(2026, 8, 14, 23, 59),
            DueTimePolicy.EffectiveDueDate(Sydney.FromLocal(2026, 8, 14), isManual: false, treatsMidnightAsEndOfDay: true, Sydney));

    [Fact]
    public void TheCalendarDayNeverMoves()
    {
        var midnight = Sydney.FromLocal(2026, 8, 14);
        var adjusted = DueTimePolicy.EffectiveDueDate(midnight, isManual: false, treatsMidnightAsEndOfDay: true, Sydney);

        Assert.Equal(Sydney.DateOf(midnight), Sydney.DateOf(adjusted));
        var now = Sydney.FromLocal(2026, 8, 10, 9, 0);
        Assert.Equal(CountdownCalculator.DaysRemaining(midnight, now, Sydney), CountdownCalculator.DaysRemaining(adjusted, now, Sydney));
    }

    [Fact]
    public void AStatedTimeIsLeftExactlyAsCanvasSentIt()
    {
        var stated = Sydney.FromLocal(2026, 8, 14, 17, 0);

        Assert.Equal(stated, DueTimePolicy.EffectiveDueDate(stated, isManual: false, treatsMidnightAsEndOfDay: true, Sydney));
    }

    [Fact]
    public void AnEventTheUserTypedIsNeverAdjusted()
    {
        var typed = Sydney.FromLocal(2026, 8, 14);

        Assert.Equal(typed, DueTimePolicy.EffectiveDueDate(typed, isManual: true, treatsMidnightAsEndOfDay: true, Sydney));
    }

    [Fact]
    public void TurningItOffRestoresTheOriginalTime()
    {
        var midnight = Sydney.FromLocal(2026, 8, 14);

        Assert.Equal(midnight, DueTimePolicy.EffectiveDueDate(midnight, isManual: false, treatsMidnightAsEndOfDay: false, Sydney));
    }

    [Fact]
    public void MidnightIsJudgedInTheUsersOwnTimeZone()
    {
        // Midnight in UTC is 10:00 in Sydney in winter: not a midnight there.
        var utcMidnight = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(utcMidnight, DueTimePolicy.EffectiveDueDate(utcMidnight, isManual: false, treatsMidnightAsEndOfDay: true, Sydney));
        Assert.NotEqual(utcMidnight, DueTimePolicy.EffectiveDueDate(utcMidnight, isManual: false, treatsMidnightAsEndOfDay: true, AppCalendar.Utc));
    }

    [Fact]
    public void TheListShowsTheCorrectedTimeButTheSameDaysLeft()
    {
        var snapshot = new AssignmentSnapshot { Title = "Essay", DueDate = Sydney.FromLocal(2026, 8, 14), Source = AssignmentSource.CanvasCalendarFeed };
        var now = Sydney.FromLocal(2026, 8, 10, 9, 0);

        var shown = AssignmentListItem.FromSnapshot(snapshot, treatsMidnightAsEndOfDay: true, Sydney);
        var original = AssignmentListItem.FromSnapshot(snapshot, treatsMidnightAsEndOfDay: false, Sydney);

        Assert.Equal(Sydney.FromLocal(2026, 8, 14, 23, 59), shown.DueDate);
        Assert.Equal(snapshot.DueDate, original.DueDate);
        Assert.Equal(original.RemainingDays(now, Sydney), shown.RemainingDays(now, Sydney));
    }

    [Fact]
    public void AManualEventKeepsItsOwnMidnightInTheList()
    {
        var snapshot = new AssignmentSnapshot { Title = "Mine", DueDate = Sydney.FromLocal(2026, 8, 14), Source = AssignmentSource.Manual };

        Assert.Equal(snapshot.DueDate, AssignmentListItem.FromSnapshot(snapshot, treatsMidnightAsEndOfDay: true, Sydney).DueDate);
    }

    [Fact]
    public void RemindersCountBackFromTheCorrectedDeadline()
    {
        var snapshot = new AssignmentSnapshot { Title = "Essay", DueDate = Sydney.FromLocal(2026, 8, 14), Source = AssignmentSource.CanvasCalendarFeed };

        var candidate = NotificationCandidate.FromSnapshot(snapshot, treatsMidnightAsEndOfDay: true, Sydney);

        Assert.Equal(Sydney.FromLocal(2026, 8, 14, 23, 59), candidate.DueDate);
    }
}

using static CanvasCountdown.Tests.Calendar.CalendarTestData;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// The back and forward controls and the Today control, as the view model
/// will call them: normalise, then step; and "is today already on screen".
/// </summary>
public sealed class CalendarNavigationTests
{
    [Fact]
    public void SteppingADayLandsOnTheStartOfTheNextDay()
    {
        var anchor = At(2026, 7, 29, hour: 15, minute: 45);

        var next = AssignmentCalendar.Step(anchor, CalendarScale.Day, 1, Sydney);

        Assert.Equal(new DateTime(2026, 7, 30, 0, 0, 0), Sydney.ToLocal(next));
    }

    [Theory]
    // Wednesday 29 July 2026.
    [InlineData(DayOfWeek.Sunday, 2026, 8, 2)]
    [InlineData(DayOfWeek.Monday, 2026, 8, 3)]
    public void SteppingAWeekLandsOnTheFirstDayOfTheNextWeek(DayOfWeek firstDay, int year, int month, int day)
    {
        var calendar = WithFirstDay(firstDay);

        var next = AssignmentCalendar.Step(At(2026, 7, 29), CalendarScale.Week, 1, calendar);

        Assert.Equal(new DateTime(year, month, day, 0, 0, 0), calendar.ToLocal(next));
    }

    [Fact]
    public void SteppingAMonthFromThe31stLandsOnTheFirstOfTheNextMonth()
    {
        var anchor = At(2026, 3, 31);

        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0), Sydney.ToLocal(AssignmentCalendar.Step(anchor, CalendarScale.Month, 1, Sydney)));
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0), Sydney.ToLocal(AssignmentCalendar.Step(anchor, CalendarScale.Month, -1, Sydney)));
    }

    [Fact]
    public void PagingAYearOfMonthsFromThe31stComesBackToTheSameMonth()
    {
        var anchor = At(2026, 1, 31);
        for (var step = 0; step < 12; step++)
        {
            anchor = AssignmentCalendar.Step(anchor, CalendarScale.Month, 1, Sydney);
        }

        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0), Sydney.ToLocal(anchor));
    }

    [Fact]
    public void SteppingAYearLandsOnTheFirstOfJanuary()
    {
        var next = AssignmentCalendar.Step(At(2026, 7, 29), CalendarScale.Year, 1, Sydney);

        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0), Sydney.ToLocal(next));
    }

    [Fact]
    public void SteppingWeeksAcrossAChangeOfClocksKeepsMidnight()
    {
        // The week of Sunday 4 October 2026 is an hour short in Sydney.
        var anchor = At(2026, 9, 27);

        var once = AssignmentCalendar.Step(anchor, CalendarScale.Week, 1, Sydney);
        var twice = AssignmentCalendar.Step(once, CalendarScale.Week, 1, Sydney);

        Assert.Equal(new DateTime(2026, 10, 4, 0, 0, 0), Sydney.ToLocal(once));
        Assert.Equal(new DateTime(2026, 10, 11, 0, 0, 0), Sydney.ToLocal(twice));
        Assert.Equal(TimeSpan.FromHours(7 * 24 - 1), twice - once);
    }

    [Fact]
    public void TodayIsShowingWhenItsPeriodIsOnScreen()
    {
        var now = At(2026, 8, 12, hour: 23);

        Assert.True(AssignmentCalendar.IsShowingToday(CalendarScale.Day, At(2026, 8, 12, hour: 0), now, Sydney));
        Assert.False(AssignmentCalendar.IsShowingToday(CalendarScale.Day, At(2026, 8, 13, hour: 0), now, Sydney));

        Assert.True(AssignmentCalendar.IsShowingToday(CalendarScale.Month, At(2026, 8, 31), now, Sydney));
        Assert.False(AssignmentCalendar.IsShowingToday(CalendarScale.Month, At(2026, 9, 1), now, Sydney));
        Assert.False(AssignmentCalendar.IsShowingToday(CalendarScale.Month, At(2027, 8, 12), now, Sydney));

        Assert.True(AssignmentCalendar.IsShowingToday(CalendarScale.Year, At(2026, 1, 1, hour: 0), now, Sydney));
        Assert.False(AssignmentCalendar.IsShowingToday(CalendarScale.Year, At(2027, 1, 1, hour: 0), now, Sydney));
    }

    [Theory]
    // Today is Sunday 9 August 2026. Saturday the 8th ends the previous
    // Sunday-first week but shares a Monday-first one; Monday the 10th is the
    // other way round.
    [InlineData(DayOfWeek.Sunday, 8, false)]
    [InlineData(DayOfWeek.Sunday, 10, true)]
    [InlineData(DayOfWeek.Monday, 8, true)]
    [InlineData(DayOfWeek.Monday, 10, false)]
    public void TodayIsShowingInAWeekByTheFirstWeekday(DayOfWeek firstDay, int shownDay, bool expected)
    {
        var calendar = WithFirstDay(firstDay);

        var showing = AssignmentCalendar.IsShowingToday(
            CalendarScale.Week,
            anchor: calendar.FromLocal(2026, 8, shownDay, 12),
            now: calendar.FromLocal(2026, 8, 9, 12),
            calendar);

        Assert.Equal(expected, showing);
    }

    [Fact]
    public void TodayIsOfferedOnlyOnceTheCalendarHasMovedAway()
    {
        var now = At(2026, 8, 12);
        var anchor = now;
        Assert.True(AssignmentCalendar.IsShowingToday(CalendarScale.Month, anchor, now, Sydney));

        anchor = AssignmentCalendar.Step(anchor, CalendarScale.Month, 1, Sydney);
        Assert.False(AssignmentCalendar.IsShowingToday(CalendarScale.Month, anchor, now, Sydney));

        anchor = AssignmentCalendar.Step(anchor, CalendarScale.Month, -1, Sydney);
        Assert.True(AssignmentCalendar.IsShowingToday(CalendarScale.Month, anchor, now, Sydney));
    }

    [Fact]
    public void TheStartOfAPeriodIsFoundInTheCalendarsOwnZone()
    {
        // 09:00 on 1 August in Sydney is still 31 July in UTC.
        var morning = At(2026, 8, 1, hour: 9);

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0), Sydney.ToLocal(AssignmentCalendar.StartOfMonth(morning, Sydney)));
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0), AppCalendar.Utc.ToLocal(AssignmentCalendar.StartOfMonth(morning, AppCalendar.Utc)));
        Assert.Equal(AssignmentCalendar.StartOfWeek(At(2026, 8, 1), Sydney), AssignmentCalendar.StartOfGrid(At(2026, 8, 1), Sydney));
    }
}

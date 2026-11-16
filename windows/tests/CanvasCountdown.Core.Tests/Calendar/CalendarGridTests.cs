using static CanvasCountdown.Tests.Calendar.CalendarTestData;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// What fills the grids beyond what the Swift tests pinned: the padding days
/// for each first weekday, days the clocks change on, and the ends of the
/// range .NET can represent.
/// </summary>
public sealed class CalendarGridTests
{
    [Theory]
    // 1 August 2026 is a Saturday.
    [InlineData(DayOfWeek.Sunday, 2026, 7, 26, 6, 5)]
    [InlineData(DayOfWeek.Monday, 2026, 7, 27, 5, 6)]
    public void TheMonthGridPadsBeforeAndAfterByTheFirstWeekday(
        DayOfWeek firstDay, int startYear, int startMonth, int startDay, int leading, int trailing)
    {
        var calendar = WithFirstDay(firstDay);

        var days = AssignmentCalendar.Month(At(2026, 8, 20), [], calendar, now: At(2026, 8, 20));

        Assert.Equal(new DateOnly(startYear, startMonth, startDay), days[0].LocalDate);
        Assert.Equal(leading, days.TakeWhile(day => !day.IsInDisplayedMonth).Count());
        Assert.Equal(31, days.Count(day => day.IsInDisplayedMonth));
        Assert.Equal(trailing, days.Reverse().TakeWhile(day => !day.IsInDisplayedMonth).Count());
        Assert.Equal(
            Enumerable.Range(0, 42).Select(offset => days[0].LocalDate.AddDays(offset)),
            days.Select(day => day.LocalDate));
    }

    [Theory]
    // 1 February 2026 is a Sunday: no padding before it when weeks start on
    // Sunday, six days of January when they start on Monday.
    [InlineData(DayOfWeek.Sunday, 0, 14)]
    [InlineData(DayOfWeek.Monday, 6, 8)]
    public void AMonthStartingOnTheFirstWeekdayStillShowsSixWeeks(DayOfWeek firstDay, int leading, int trailing)
    {
        var days = AssignmentCalendar.Month(At(2026, 2, 10), [], WithFirstDay(firstDay), now: At(2026, 2, 10));

        Assert.Equal(42, days.Count);
        Assert.Equal(leading, days.TakeWhile(day => !day.IsInDisplayedMonth).Count());
        Assert.Equal(trailing, days.Reverse().TakeWhile(day => !day.IsInDisplayedMonth).Count());
    }

    [Fact]
    public void EverySquareStartsAtLocalMidnightAcrossAChangeOfClocks()
    {
        // October 2026 in Sydney has a 23-hour day on the 4th.
        var days = AssignmentCalendar.Month(At(2026, 10, 15), [], Sydney, now: At(2026, 10, 15));

        Assert.All(days, day =>
        {
            Assert.Equal(TimeOnly.MinValue, Sydney.TimeOf(day.Date));
            Assert.Equal(day.LocalDate, Sydney.DateOf(day.Date));
            Assert.Equal(day.LocalDate.Day, day.DayNumber);
        });
        var shortDay = days.Single(day => day.LocalDate == new DateOnly(2026, 10, 4));
        var nextDay = days.Single(day => day.LocalDate == new DateOnly(2026, 10, 5));
        Assert.Equal(TimeSpan.FromHours(23), nextDay.Date - shortDay.Date);
    }

    [Fact]
    public void ADayWhoseMidnightIsSkippedStartsAtItsFirstRealMoment()
    {
        // Santiago jumped from 00:00 to 01:00 on 6 September 2026.
        var santiago = AppCalendar.ForTimeZone("America/Santiago");
        var due = santiago.FromLocal(2026, 9, 6, 9, 0);

        var days = AssignmentCalendar.Month(due, [Item("STAT1010 Problem Set 3", due)], santiago, now: due);

        var skipped = days.Single(day => day.LocalDate == new DateOnly(2026, 9, 6));
        Assert.Equal(new TimeOnly(1, 0), santiago.TimeOf(skipped.Date));
        Assert.Equal(6, skipped.DayNumber);
        Assert.Equal(["STAT1010 Problem Set 3"], skipped.Items.Select(item => item.Title));
        Assert.True(skipped.IsToday);
    }

    [Fact]
    public void AMinuteBeforeMidnightStaysOnItsOwnDay()
    {
        var due = At(2026, 8, 4, hour: 23, minute: 59);

        var days = AssignmentCalendar.Month(due, [Item("DESN1500 Studio Journal", due)], Sydney, now: due);

        var cell = Assert.Single(days, day => !day.Items.IsEmpty);
        Assert.Equal(new DateOnly(2026, 8, 4), cell.LocalDate);
    }

    [Fact]
    public void AnEventInTheNextMonthsPaddingAppearsThereToo()
    {
        // 2 September 2026 sits in the last row of August's grid.
        var due = At(2026, 9, 2);

        var days = AssignmentCalendar.Month(At(2026, 8, 1), [Item("COMP2050 Lab 4", due)], Sydney, now: due);

        var cell = Assert.Single(days, day => !day.Items.IsEmpty);
        Assert.False(cell.IsInDisplayedMonth);
        Assert.Equal(new DateOnly(2026, 9, 2), cell.LocalDate);
    }

    [Fact]
    public void TheWeekAcrossTheTurnOfTheYearHoldsBothYears()
    {
        var days = AssignmentCalendar.Week(At(2026, 12, 31), [], Sydney, now: At(2026, 12, 31));

        Assert.Equal(new DateOnly(2026, 12, 27), days[0].LocalDate);
        Assert.Equal(new DateOnly(2027, 1, 2), days[^1].LocalDate);
        Assert.All(days, day => Assert.True(day.IsInDisplayedMonth));
    }

    [Fact]
    public void TheYearMarksTodayOnceAmongItsOwnDays()
    {
        // 2 August 2026 also sits in the padding of July's grid.
        var today = At(2026, 8, 2);

        var months = AssignmentCalendar.Year(today, [], Sydney, now: today, culture: English);

        var shownToday = months.SelectMany(month => month.Days).Where(day => day.IsInDisplayedMonth && day.IsToday);
        Assert.Equal(new DateOnly(2026, 8, 2), Assert.Single(shownToday).LocalDate);
        Assert.Equal(2, months.SelectMany(month => month.Days).Count(day => day.IsToday));
    }

    [Fact]
    public void TheYearsMonthsAreTitledByMonthAlone()
    {
        var months = AssignmentCalendar.Year(At(2026, 5, 5), [], Sydney, now: At(2026, 5, 5), culture: English);

        Assert.Equal(
            ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"],
            months.Select(month => month.Title));
        Assert.All(months, month => Assert.Equal(month.Start, month.Id));
    }

    [Fact]
    public void ItemsOnADayAreOnlyThatDaysEarliestFirst()
    {
        var late = Item("COMP2050 Assignment 2", At(2026, 8, 14, hour: 23, minute: 59));
        var early = Item("STAT1010 Quiz 5", At(2026, 8, 14, hour: 9));
        var nextDay = Item("DESN1500 Critique", At(2026, 8, 15, hour: 0));

        var items = AssignmentCalendar.Items(on: At(2026, 8, 14, hour: 18), [late, nextDay, early], Sydney);

        Assert.Equal([early, late], items);
    }

    [Fact]
    public void TheLastMonthDotNetCanRepresentLeavesOutWhatComesAfterIt()
    {
        var lastDay = Sydney.FromLocal(9999, 12, 31, 12, 0);

        var month = AssignmentCalendar.Month(lastDay, [], Sydney, now: lastDay);
        var week = AssignmentCalendar.Week(lastDay, [], Sydney, now: lastDay);
        var year = AssignmentCalendar.Year(lastDay, [], Sydney, now: lastDay, culture: English);

        Assert.Equal(new DateOnly(9999, 12, 31), month[^1].LocalDate);
        Assert.Equal(31, month.Count(day => day.IsInDisplayedMonth));
        Assert.Equal(new DateOnly(9999, 12, 31), week[^1].LocalDate);
        Assert.Equal(12, year.Count);
        Assert.Equal(lastDay, AssignmentCalendar.DateByAdding(CalendarScale.Year, 1, to: lastDay, calendar: Sydney));
        Assert.Equal(lastDay, AssignmentCalendar.DateByAdding(CalendarScale.Day, 1, to: lastDay, calendar: Sydney));
        Assert.Equal(lastDay, AssignmentCalendar.DateByAdding(CalendarScale.Week, int.MaxValue, to: lastDay, calendar: Sydney));
    }

    [Fact]
    public void TheFirstDayDotNetCanRepresentDoesNotTakeTheGridDown()
    {
        // 1 January 0001 is a Monday, so with Sunday-first weeks its grid would
        // start on a day that does not exist; and midnight that morning in
        // Sydney is before the first instant .NET has.
        var firstDay = Sydney.FromLocal(1, 1, 1, 12, 0);

        Assert.Empty(AssignmentCalendar.Month(firstDay, [], Sydney, now: firstDay));
        Assert.Empty(AssignmentCalendar.Week(firstDay, [], Sydney, now: firstDay));
        Assert.Null(AssignmentCalendar.StartOfWeek(firstDay, Sydney));

        var mondayFirst = AssignmentCalendar.Month(firstDay, [], SydneyMondayFirst, now: firstDay);
        Assert.Equal(42, mondayFirst.Count);
        Assert.Equal(new DateOnly(1, 1, 1), mondayFirst[0].LocalDate);
        Assert.Equal(DateTimeOffset.MinValue, mondayFirst[0].Date);
        Assert.Equal(new DateOnly(1, 1, 2), mondayFirst[1].LocalDate);

        var day = AssignmentCalendar.Day(firstDay, [], Sydney, now: firstDay);
        Assert.Equal(new DateOnly(1, 1, 1), day.LocalDate);
        Assert.Equal(DateTimeOffset.MinValue, day.Date);
        Assert.Equal(DateTimeOffset.MinValue, AssignmentCalendar.Normalized(firstDay, CalendarScale.Week, Sydney));
        Assert.Equal(firstDay, AssignmentCalendar.DateByAdding(CalendarScale.Month, -1, to: firstDay, calendar: Sydney));
    }
}

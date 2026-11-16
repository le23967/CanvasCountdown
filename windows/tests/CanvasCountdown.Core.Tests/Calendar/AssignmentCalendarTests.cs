using System.Globalization;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// The month grid, and its promise that the calendar shows exactly what the
/// list shows.
/// </summary>
/// <remarks>
/// Ported from AssignmentCalendarTests.swift. The tests there that drive the
/// main view model (the list/calendar agreement, navigation, Go to Date, the
/// menu) are not here; they belong with the view model's own port.
/// </remarks>
public sealed class AssignmentCalendarTests
{
    /// <summary>Sunday first, as the Swift tests set <c>firstWeekday = 1</c>.</summary>
    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    private static AppCalendar WithFirstDay(DayOfWeek firstDay) => new(Sydney.TimeZone, firstDay);

    private static DateTimeOffset Date(int year, int month, int day, int hour = 12)
        => Sydney.FromLocal(year, month, day, hour);

    private static AssignmentListItem Item(string title, DateTimeOffset due, bool isCompleted = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        CourseName = "99999 Example Interaction Course",
        DueDate = due,
        IsCompleted = isCompleted,
        IsIgnored = false,
        IsManual = false,
    };

    // Grid shape

    [Theory]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday)]
    public void GridIsAlwaysSixWeeks(DayOfWeek firstDay)
    {
        var calendar = WithFirstDay(firstDay);
        for (var month = 1; month <= 12; month++)
        {
            var days = AssignmentCalendar.Month(
                containing: Date(2026, month, 15),
                events: [],
                calendar: calendar,
                now: Date(2026, 7, 28));

            // A fixed height stops the grid jumping between months.
            Assert.Equal(42, days.Count);
        }
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday)]
    public void GridStartsOnTheUsersFirstWeekday(DayOfWeek firstDay)
    {
        var calendar = WithFirstDay(firstDay);

        var days = AssignmentCalendar.Month(
            containing: Date(2026, 8, 15),
            events: [],
            calendar: calendar,
            now: Date(2026, 7, 28));
        var first = days.FirstOrDefault();

        Assert.NotNull(first);
        Assert.Equal(calendar.FirstDayOfWeek, calendar.DateOf(first.Date).DayOfWeek);
    }

    [Fact]
    public void GridRespectsADifferentFirstWeekday()
    {
        var mondayFirst = WithFirstDay(DayOfWeek.Monday);

        var days = AssignmentCalendar.Month(
            containing: Date(2026, 8, 15),
            events: [],
            calendar: mondayFirst,
            now: Date(2026, 7, 28));
        var first = days.FirstOrDefault();

        Assert.NotNull(first);
        Assert.Equal(DayOfWeek.Monday, mondayFirst.DateOf(first.Date).DayOfWeek);
        var symbols = AssignmentCalendar.WeekdaySymbols(mondayFirst, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(7, symbols.Count);
        Assert.Equal("Mon", symbols[0]);
    }

    [Fact]
    public void DaysOutsideTheMonthAreMarked()
    {
        var days = AssignmentCalendar.Month(
            containing: Date(2026, 8, 15),
            events: [],
            calendar: Sydney,
            now: Date(2026, 7, 28));

        Assert.Contains(days, day => !day.IsInDisplayedMonth);
        // August has 31 days.
        Assert.Equal(31, days.Count(day => day.IsInDisplayedMonth));
    }

    [Fact]
    public void FebruaryInALeapYear()
    {
        var days = AssignmentCalendar.Month(
            containing: Date(2028, 2, 10),
            events: [],
            calendar: Sydney,
            now: Date(2026, 7, 28));

        Assert.Equal(29, days.Count(day => day.IsInDisplayedMonth));
    }

    [Fact]
    public void TodayIsMarkedOnlyOnce()
    {
        var today = Date(2026, 8, 12);

        var days = AssignmentCalendar.Month(containing: today, events: [], calendar: Sydney, now: today);

        Assert.Single(days, day => day.IsToday);
    }

    // Events in cells

    [Fact]
    public void EventsLandOnTheirOwnDay()
    {
        var due = Date(2026, 8, 4, hour: 16);

        var days = AssignmentCalendar.Month(
            containing: due,
            events: [Item("Example Quiz", due)],
            calendar: Sydney,
            now: Date(2026, 7, 28));

        var cell = Assert.Single(days, day => Sydney.IsSameDay(day.Date, due));
        Assert.Equal(["Example Quiz"], cell.Items.Select(item => item.Title));
        // An event appears on exactly one day.
        Assert.Single(days, day => !day.Items.IsEmpty);
    }

    [Fact]
    public void SeveralEventsOnADayAreSortedByTime()
    {
        var morning = Date(2026, 8, 4, hour: 9);
        var evening = Date(2026, 8, 4, hour: 21);

        var days = AssignmentCalendar.Month(
            containing: morning,
            events: [Item("Later", evening), Item("Earlier", morning)],
            calendar: Sydney,
            now: Date(2026, 7, 28));

        var cell = days.First(day => !day.Items.IsEmpty);
        Assert.Equal(["Earlier", "Later"], cell.Items.Select(item => item.Title));
    }

    [Fact]
    public void AnEventLateAtNightStaysOnItsOwnDay()
    {
        // 23:59 must not spill into the next square.
        var due = Date(2026, 8, 4, hour: 23);

        var days = AssignmentCalendar.Month(
            containing: due,
            events: [Item("Example Poster Submission", due)],
            calendar: Sydney,
            now: Date(2026, 7, 28));

        var cell = days.First(day => !day.Items.IsEmpty);
        Assert.Equal(4, Sydney.DateOf(cell.Date).Day);
    }

    [Fact]
    public void AMonthWithNoEventsHasEmptyCells()
    {
        var days = AssignmentCalendar.Month(
            containing: Date(2026, 12, 1),
            events: [Item("Example Quiz", Date(2026, 8, 4))],
            calendar: Sydney,
            now: Date(2026, 7, 28));

        Assert.DoesNotContain(days, day => !day.Items.IsEmpty);
    }
}

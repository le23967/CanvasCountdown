using System.Globalization;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// The day, week and year grids, the hour layout underneath them, and the date
/// field that jumps between them. All fixtures are invented.
/// </summary>
/// <remarks>Ported from CalendarScaleTests.swift.</remarks>
public sealed class CalendarScaleTests
{
    /// <summary>Sunday first, as the Swift tests set <c>firstWeekday = 1</c>.</summary>
    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    private static AppCalendar WithFirstDay(DayOfWeek firstDay) => new(Sydney.TimeZone, firstDay);

    private static DateTimeOffset Date(int year, int month, int day, int hour = 12, int minute = 0)
        => Sydney.FromLocal(year, month, day, hour, minute);

    private static AssignmentListItem Item(string title, DateTimeOffset due) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        CourseName = "99999 Example Interaction Course",
        DueDate = due,
        IsCompleted = false,
        IsIgnored = false,
        IsManual = false,
    };

    // Week

    [Theory]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday)]
    public void AWeekIsSevenDaysStartingOnTheUsersFirstWeekday(DayOfWeek firstDay)
    {
        var calendar = WithFirstDay(firstDay);

        var days = AssignmentCalendar.Week(
            containing: Date(2026, 7, 29),
            events: [],
            calendar: calendar,
            now: Date(2026, 7, 29));

        Assert.Equal(7, days.Count);
        Assert.Equal(calendar.FirstDayOfWeek, calendar.DateOf(days[0].Date).DayOfWeek);
    }

    [Fact]
    public void AWeekRespectsADifferentFirstWeekday()
    {
        var mondayFirst = WithFirstDay(DayOfWeek.Monday);

        var days = AssignmentCalendar.Week(
            containing: Date(2026, 7, 29),
            events: [],
            calendar: mondayFirst,
            now: Date(2026, 7, 29));

        var first = days.FirstOrDefault();
        Assert.NotNull(first);
        Assert.Equal(DayOfWeek.Monday, Sydney.DateOf(first.Date).DayOfWeek);
    }

    [Fact]
    public void AWeekCarriesTheEventsOfItsOwnDaysOnly()
    {
        var inside = Item("Example Quiz", Date(2026, 7, 30, hour: 16));
        var outside = Item("Example Poster", Date(2026, 8, 20, hour: 16));

        var days = AssignmentCalendar.Week(
            containing: Date(2026, 7, 29),
            events: [inside, outside],
            calendar: Sydney,
            now: Date(2026, 7, 29));

        Assert.Equal(["Example Quiz"], days.SelectMany(day => day.Items).Select(item => item.Title));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday)]
    public void TheWeekStraddlingAMonthKeepsBothMonths(DayOfWeek firstDay)
    {
        var calendar = WithFirstDay(firstDay);

        var days = AssignmentCalendar.Week(
            containing: Date(2026, 7, 30),
            events: [],
            calendar: calendar,
            now: Date(2026, 7, 29));

        var months = days.Select(day => calendar.DateOf(day.Date).Month).ToHashSet();
        Assert.Equal([7, 8], months.Order());
    }

    // Day

    [Fact]
    public void ADayCarriesOnlyItsOwnEvents()
    {
        var day = AssignmentCalendar.Day(
            containing: Date(2026, 8, 4),
            events:
            [
                Item("Example Quiz", Date(2026, 8, 4, hour: 16)),
                Item("Example Poster", Date(2026, 8, 5, hour: 16)),
            ],
            calendar: Sydney,
            now: Date(2026, 7, 29));

        Assert.Equal(["Example Quiz"], day.Items.Select(item => item.Title));
        Assert.False(day.IsToday);
    }

    [Fact]
    public void TodayIsMarkedAsToday()
    {
        var day = AssignmentCalendar.Day(
            containing: Date(2026, 7, 29, hour: 9),
            events: [],
            calendar: Sydney,
            now: Date(2026, 7, 29, hour: 23));

        Assert.True(day.IsToday);
    }

    // Year

    [Fact]
    public void AYearIsTwelveMonthsInOrder()
    {
        var months = AssignmentCalendar.Year(
            containing: Date(2026, 7, 29),
            events: [],
            calendar: Sydney,
            now: Date(2026, 7, 29),
            culture: CultureInfo.GetCultureInfo("en-AU"));

        Assert.Equal(12, months.Count);
        Assert.Equal(Enumerable.Range(1, 12), months.Select(month => Sydney.DateOf(month.Start).Month));
        Assert.All(months, month => Assert.Equal(2026, Sydney.DateOf(month.Start).Year));
        var january = months.FirstOrDefault();
        Assert.NotNull(january);
        Assert.Equal(42, january.Days.Count);
    }

    [Fact]
    public void AYearPlacesAnEventInItsOwnMonthOnly()
    {
        var months = AssignmentCalendar.Year(
            containing: Date(2026, 1, 1),
            events: [Item("Example Quiz", Date(2026, 8, 4, hour: 16))],
            calendar: Sydney,
            now: Date(2026, 7, 29),
            culture: CultureInfo.GetCultureInfo("en-AU"));

        // The padding days of a neighbouring month legitimately repeat it, so
        // this counts the months whose own days carry it.
        var owning = months.Where(month => month.Days.Any(day => day.IsInDisplayedMonth && !day.Items.IsEmpty));
        var only = Assert.Single(owning);
        Assert.Equal(8, Sydney.DateOf(only.Start).Month);
    }

    // Stepping

    [Fact]
    public void EachScaleStepsByItsOwnUnit()
    {
        var start = Date(2026, 7, 29);

        var day = AssignmentCalendar.DateByAdding(CalendarScale.Day, 1, to: start, calendar: Sydney);
        Assert.Equal(30, Sydney.DateOf(day).Day);

        var week = AssignmentCalendar.DateByAdding(CalendarScale.Week, 1, to: start, calendar: Sydney);
        Assert.Equal(5, Sydney.DateOf(week).Day);
        Assert.Equal(8, Sydney.DateOf(week).Month);

        var month = AssignmentCalendar.DateByAdding(CalendarScale.Month, 1, to: start, calendar: Sydney);
        Assert.Equal(8, Sydney.DateOf(month).Month);

        var year = AssignmentCalendar.DateByAdding(CalendarScale.Year, 1, to: start, calendar: Sydney);
        Assert.Equal(2027, Sydney.DateOf(year).Year);
    }

    [Fact]
    public void SteppingFromTheEndOfAMonthDoesNotWalkBackwards()
    {
        // Adding a month to the 31st lands on the 30th, so a calendar that kept
        // the day would lose one every time it paged. Normalising first is what
        // stops that, and this is the case it was written for.
        var anchor = AssignmentCalendar.Normalized(Date(2026, 3, 31), CalendarScale.Month, Sydney);
        for (var step = 0; step < 6; step++)
        {
            anchor = AssignmentCalendar.DateByAdding(CalendarScale.Month, 1, to: anchor, calendar: Sydney);
        }
        for (var step = 0; step < 6; step++)
        {
            anchor = AssignmentCalendar.DateByAdding(CalendarScale.Month, -1, to: anchor, calendar: Sydney);
        }

        var landed = Sydney.DateOf(anchor);
        Assert.Equal((2026, 3), (landed.Year, landed.Month));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday)]
    public void NormalisingLandsOnTheStartOfThePeriod(DayOfWeek firstDay)
    {
        var calendar = WithFirstDay(firstDay);
        var sample = calendar.FromLocal(2026, 3, 31, 23, 45);

        Assert.Equal(
            new DateTime(2026, 3, 31, 0, 0, 0),
            calendar.ToLocal(AssignmentCalendar.Normalized(sample, CalendarScale.Day, calendar)));
        Assert.Equal(
            new DateTime(2026, 3, 1, 0, 0, 0),
            calendar.ToLocal(AssignmentCalendar.Normalized(sample, CalendarScale.Month, calendar)));
        Assert.Equal(
            new DateTime(2026, 1, 1, 0, 0, 0),
            calendar.ToLocal(AssignmentCalendar.Normalized(sample, CalendarScale.Year, calendar)));
        Assert.Equal(
            calendar.FirstDayOfWeek,
            calendar.DateOf(AssignmentCalendar.Normalized(sample, CalendarScale.Week, calendar)).DayOfWeek);
    }

    // Hour layout

    [Fact]
    public void MidnightIsAnAllDayDeadlineRatherThanOhOhHundred()
    {
        var midnight = Item("Example Reflection", Date(2026, 8, 14, hour: 0));
        var afternoon = Item("Example Quiz", Date(2026, 8, 14, hour: 16));

        Assert.True(CalendarTimeGrid.IsAllDay(midnight, Sydney));
        Assert.False(CalendarTimeGrid.IsAllDay(afternoon, Sydney));

        Assert.Equal(
            ["Example Reflection"],
            CalendarTimeGrid.AllDayItems([midnight, afternoon], Sydney).Select(item => item.Title));
        Assert.Equal(
            ["Example Quiz"],
            CalendarTimeGrid.TimedEntries([midnight, afternoon], Sydney).Select(entry => entry.Item.Title));
    }

    [Fact]
    public void AnEventIsPlacedAtItsOwnMinute()
    {
        var entries = CalendarTimeGrid.TimedEntries(
            [Item("Example Quiz", Date(2026, 8, 4, hour: 16, minute: 30))],
            Sydney);

        var entry = Assert.Single(entries);
        Assert.Equal(16 * 60 + 30, entry.MinuteOfDay);
        Assert.Equal(0, entry.Column);
        // Alone, it gets the whole width.
        Assert.Equal(1, entry.ColumnCount);
    }

    [Fact]
    public void DeadlinesAtTheSameTimeShareTheWidth()
    {
        var entries = CalendarTimeGrid.TimedEntries(
            [
                Item("Example Quiz", Date(2026, 8, 4, hour: 16)),
                Item("Example Poster", Date(2026, 8, 4, hour: 16)),
                Item("Example Reflection", Date(2026, 8, 4, hour: 16)),
            ],
            Sydney);

        Assert.Equal(3, entries.Count);
        Assert.Equal([3], entries.Select(entry => entry.ColumnCount).Distinct());
        Assert.Equal([0, 1, 2], entries.Select(entry => entry.Column).Order());
    }

    [Fact]
    public void DeadlinesFarApartEachKeepTheWholeWidth()
    {
        var entries = CalendarTimeGrid.TimedEntries(
            [
                Item("Morning", Date(2026, 8, 4, hour: 9)),
                Item("Evening", Date(2026, 8, 4, hour: 18)),
            ],
            Sydney);

        Assert.Equal([1], entries.Select(entry => entry.ColumnCount).Distinct());
        Assert.Equal([0], entries.Select(entry => entry.Column).Distinct());
    }

    [Fact]
    public void AFreedColumnIsReusedByALaterDeadline()
    {
        // 09:00 and 09:20 overlap, so they split; 11:00 is clear of both and
        // must not inherit their split.
        var entries = CalendarTimeGrid.TimedEntries(
            [
                Item("First", Date(2026, 8, 4, hour: 9)),
                Item("Second", Date(2026, 8, 4, hour: 9, minute: 20)),
                Item("Third", Date(2026, 8, 4, hour: 11)),
            ],
            Sydney);

        var byTitle = entries.ToDictionary(entry => entry.Item.Title);
        Assert.Equal(2, byTitle["First"].ColumnCount);
        Assert.Equal(1, byTitle["Second"].Column);
        Assert.Equal(1, byTitle["Third"].ColumnCount);
    }

    [Fact]
    public void ThereIsALabelForEveryHour()
    {
        Assert.Equal(
            CalendarTimeGrid.HoursShown,
            CalendarTimeGrid.HourLabels(Sydney, CultureInfo.GetCultureInfo("en-AU"), now: Date(2026, 7, 29)).Count);
    }

    // Typing a date

    private static DateOnly? Parsed(string text, DateTimeOffset? reference = null, string culture = "en-AU")
    {
        var result = CalendarDateEntry.Date(
            text,
            reference: reference ?? Date(2026, 7, 29),
            calendar: Sydney,
            culture: CultureInfo.GetCultureInfo(culture),
            now: Date(2026, 7, 29));
        return result is { } day ? Sydney.DateOf(day) : null;
    }

    [Fact]
    public void AnISODateIsRead()
    {
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("2026-08-14"));
    }

    [Fact]
    public void AMonthNameIsReadInEitherOrder()
    {
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("14 aug"));
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("aug 14"));
        Assert.Equal(new DateOnly(2027, 8, 14), Parsed("August 14 2027"));
    }

    [Fact]
    public void TheLocaleDecidesWhatComesFirstInANumericDate()
    {
        // Australia writes the day first.
        Assert.Equal(new DateOnly(2026, 9, 8), Parsed("8/9", culture: "en-AU"));
        // The United States writes the month first.
        Assert.Equal(new DateOnly(2026, 8, 9), Parsed("8/9", culture: "en-US"));
    }

    [Fact]
    public void AYearFirstDateIsReadWhateverTheLocale()
    {
        Assert.Equal(new DateOnly(2027, 3, 4), Parsed("2027/3/4", culture: "en-US"));
    }

    [Fact]
    public void MissingPartsComeFromTheDayOnScreen()
    {
        // A bare number is a day of the month being looked at.
        Assert.Equal(new DateOnly(2026, 11, 14), Parsed("14", reference: Date(2026, 11, 2)));
        // A missing year is the year being looked at, not the next one.
        Assert.Equal(new DateOnly(2028, 3, 3), Parsed("3 mar", reference: Date(2028, 1, 1)));
    }

    [Fact]
    public void TodayAndTomorrowAreRead()
    {
        Assert.Equal(new DateOnly(2026, 7, 29), Parsed("today"));
        Assert.Equal(new DateOnly(2026, 7, 30), Parsed("tomorrow"));
        Assert.Equal(new DateOnly(2026, 7, 28), Parsed("yesterday"));
    }

    [Fact]
    public void NonsenseIsRefusedRatherThanGuessed()
    {
        Assert.Null(Parsed(""));
        Assert.Null(Parsed("   "));
        Assert.Null(Parsed("next term"));
        // A day that does not exist is not rolled forward.
        Assert.Null(Parsed("31 february"));
        Assert.Null(Parsed("2026-13-01"));
        Assert.Null(Parsed("hello"));
    }

    [Fact]
    public void TheResultIsTheStartOfTheDay()
    {
        var result = CalendarDateEntry.Date(
            "14 aug",
            reference: Date(2026, 7, 29),
            calendar: Sydney,
            culture: CultureInfo.GetCultureInfo("en-AU"),
            now: Date(2026, 7, 29));

        Assert.NotNull(result);
        Assert.Equal(Sydney.StartOfDay(result.Value), result.Value);
    }
}

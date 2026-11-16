using static CanvasCountdown.Tests.Calendar.CalendarTestData;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// The hour grid beyond the Swift tests: where overlap ends, how a run of
/// deadlines shares one width, where the grid opens, and when the all-day
/// strip is drawn at all.
/// </summary>
public sealed class CalendarTimeGridLayoutTests
{
    [Fact]
    public void DeadlinesExactlyAChipApartDoNotOverlap()
    {
        var entries = CalendarTimeGrid.TimedEntries(
            [
                Item("COMP2050 Lab 4", At(2026, 8, 4, hour: 9)),
                Item("STAT1010 Quiz 5", At(2026, 8, 4, hour: 9, minute: CalendarTimeGrid.EntryMinutes)),
            ],
            Sydney);

        Assert.All(entries, entry =>
        {
            Assert.Equal(0, entry.Column);
            Assert.Equal(1, entry.ColumnCount);
        });
    }

    [Fact]
    public void AChainOfOverlapsSharesOneWidth()
    {
        // 09:00 and 10:20 never touch, but both touch 09:40, so all three line
        // up at half width; 10:20 takes the column 09:00 has finished with.
        var entries = CalendarTimeGrid.TimedEntries(
            [
                Item("First", At(2026, 8, 4, hour: 9)),
                Item("Second", At(2026, 8, 4, hour: 9, minute: 40)),
                Item("Third", At(2026, 8, 4, hour: 10, minute: 20)),
            ],
            Sydney);

        Assert.Equal(["First", "Second", "Third"], entries.Select(entry => entry.Item.Title));
        Assert.Equal([0, 1, 0], entries.Select(entry => entry.Column));
        Assert.Equal([2, 2, 2], entries.Select(entry => entry.ColumnCount));
    }

    [Fact]
    public void DeadlinesAtOneMinuteAreOrderedByTitleAsAPersonWould()
    {
        var entries = CalendarTimeGrid.TimedEntries(
            [
                Item("Quiz 10", At(2026, 8, 4, hour: 16)),
                Item("quiz 2", At(2026, 8, 4, hour: 16)),
            ],
            Sydney);

        Assert.Equal(["quiz 2", "Quiz 10"], entries.Select(entry => entry.Item.Title));
        Assert.Equal([0, 1], entries.Select(entry => entry.Column));
    }

    [Fact]
    public void AMinuteBeforeMidnightIsTimedNotAllDay()
    {
        var item = Item("COMP2050 Assignment 2", At(2026, 8, 14, hour: 23, minute: 59));

        Assert.False(CalendarTimeGrid.IsAllDay(item, Sydney));
        Assert.Equal(23 * 60 + 59, Assert.Single(CalendarTimeGrid.TimedEntries([item], Sydney)).MinuteOfDay);
    }

    [Fact]
    public void TheMinuteOfTheDayIsTheWallClockOnADayTheClocksGoBack()
    {
        // 02:30 happens twice in Sydney on 5 April 2026; both are 150 minutes
        // into the day on the wall clock, which is how the grid is drawn.
        var first = Sydney.FromLocal(2026, 4, 5, 2, 30);
        var second = first.AddHours(1);

        Assert.Equal(new TimeOnly(2, 30), Sydney.TimeOf(second));
        Assert.Equal(150, CalendarTimeGrid.MinuteOfDay(first, Sydney));
        Assert.Equal(150, CalendarTimeGrid.MinuteOfDay(second, Sydney));
    }

    [Fact]
    public void TheGridOpensAnHourBeforeTheFirstDeadline()
    {
        var week = AssignmentCalendar.Week(
            At(2026, 8, 4),
            [
                Item("STAT1010 Quiz 5", At(2026, 8, 3, hour: 14)),
                Item("COMP2050 Lab 4", At(2026, 8, 6, hour: 10, minute: 59)),
                Item("DESN1500 Moodboard", At(2026, 8, 7, hour: 0)),
            ],
            Sydney,
            now: At(2026, 8, 4));

        // The midnight deadline sits in the all-day strip, so it does not pull
        // the grid up to midnight.
        Assert.Equal(9, CalendarTimeGrid.InitialHour(week, Sydney));
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(1, 59, 0)]
    [InlineData(9, 20, 8)]
    [InlineData(23, 59, 22)]
    public void TheOpeningHourNeverGoesAboveMidnight(int hour, int minute, int expected)
    {
        var day = AssignmentCalendar.Day(At(2026, 8, 4), [Item("COMP2050 Lab 4", At(2026, 8, 4, hour, minute))], Sydney, now: At(2026, 8, 4));

        Assert.Equal(expected, CalendarTimeGrid.InitialHour([day], Sydney));
    }

    [Fact]
    public void WithNothingTimedTheGridOpensAtEight()
    {
        var allDayOnly = AssignmentCalendar.Day(At(2026, 8, 4), [Item("DESN1500 Moodboard", At(2026, 8, 4, hour: 0))], Sydney, now: At(2026, 8, 4));
        var empty = AssignmentCalendar.Day(At(2026, 8, 4), [], Sydney, now: At(2026, 8, 4));

        Assert.Equal(8, CalendarTimeGrid.InitialHour([allDayOnly], Sydney));
        Assert.Equal(8, CalendarTimeGrid.InitialHour([empty], Sydney));
        Assert.Equal(CalendarTimeGrid.DefaultInitialHour, CalendarTimeGrid.InitialHour([], Sydney));
    }

    [Fact]
    public void TheAllDayStripIsDrawnOnlyWhenSomethingBelongsInIt()
    {
        var timedOnly = AssignmentCalendar.Week(At(2026, 8, 4), [Item("COMP2050 Lab 4", At(2026, 8, 4, hour: 16))], Sydney, now: At(2026, 8, 4));
        var withAllDay = AssignmentCalendar.Week(At(2026, 8, 4), [Item("DESN1500 Moodboard", At(2026, 8, 6, hour: 0))], Sydney, now: At(2026, 8, 4));

        Assert.False(CalendarTimeGrid.ShowsAllDayStrip(timedOnly, Sydney));
        Assert.True(CalendarTimeGrid.ShowsAllDayStrip(withAllDay, Sydney));
    }

    [Fact]
    public void AllDayChipsAreCompactOnlyWhenDaysShareTheWidth()
    {
        Assert.False(CalendarTimeGrid.AllDayChipsAreCompact(1));
        Assert.True(CalendarTimeGrid.AllDayChipsAreCompact(7));
    }
}

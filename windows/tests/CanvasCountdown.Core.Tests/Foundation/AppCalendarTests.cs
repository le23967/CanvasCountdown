namespace CanvasCountdown.Tests.Foundation;

public sealed class AppCalendarTests
{
    // Sydney moves its clocks forward at 02:00 on 4 October 2026 and back at
    // 03:00 on 5 April 2026, which makes both kinds of awkward day easy to name.
    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    [Fact]
    public void DaysBetweenCountsCalendarDaysNotElapsedHours()
    {
        var lateTonight = Sydney.FromLocal(2026, 8, 13, 23, 59);
        var earlyTomorrow = Sydney.FromLocal(2026, 8, 14, 0, 1);

        Assert.Equal(1, Sydney.DaysBetween(lateTonight, earlyTomorrow));
        Assert.Equal(0, Sydney.DaysBetween(earlyTomorrow, Sydney.FromLocal(2026, 8, 14, 23, 59)));
    }

    [Fact]
    public void DaysBetweenIsUnaffectedByTheShortDayOfSpringForward()
    {
        var before = Sydney.FromLocal(2026, 10, 3, 12, 0);
        var after = Sydney.FromLocal(2026, 10, 5, 0, 30);

        // 36.5 elapsed hours, but two calendar days.
        Assert.Equal(TimeSpan.FromHours(35.5), after - before);
        Assert.Equal(2, Sydney.DaysBetween(before, after));
    }

    [Fact]
    public void DaysBetweenIsUnaffectedByTheLongDayOfFallBack()
    {
        var start = Sydney.FromLocal(2026, 4, 5, 0, 0);
        var end = Sydney.FromLocal(2026, 4, 5, 23, 59);

        Assert.Equal(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(59), end - start);
        Assert.Equal(0, Sydney.DaysBetween(start, end));
    }

    [Fact]
    public void ASkippedWallClockTimeLandsTheLengthOfTheJumpLater()
    {
        var skipped = Sydney.FromLocal(2026, 10, 4, 2, 30);

        Assert.Equal(new DateTime(2026, 10, 4, 3, 30, 0), Sydney.ToLocal(skipped));
    }

    [Fact]
    public void ARepeatedWallClockTimeIsItsFirstOccurrence()
    {
        var repeated = Sydney.FromLocal(2026, 4, 5, 2, 30);

        Assert.Equal(TimeSpan.FromHours(11), repeated.Offset);
    }

    [Fact]
    public void StartOfDayIsLocalMidnight()
    {
        var afternoon = Sydney.FromLocal(2026, 8, 14, 15, 45);
        var start = Sydney.StartOfDay(afternoon);

        Assert.Equal(new DateTime(2026, 8, 14, 0, 0, 0), Sydney.ToLocal(start));
        Assert.True(Sydney.IsMidnight(start));
        Assert.False(Sydney.IsMidnight(afternoon));
    }

    [Fact]
    public void StartOfADayWhoseMidnightIsSkippedIsItsFirstRealMoment()
    {
        // Santiago jumped from 00:00 to 01:00 on 6 September 2026.
        var santiago = AppCalendar.ForTimeZone("America/Santiago");
        var noon = santiago.FromLocal(2026, 9, 6, 12, 0);

        var start = santiago.StartOfDay(noon);

        Assert.Equal(new DateOnly(2026, 9, 6), santiago.DateOf(start));
        Assert.Equal(new TimeOnly(1, 0), santiago.TimeOf(start));
    }

    [Fact]
    public void AddingDaysKeepsTheWallClockAcrossATransition()
    {
        var evening = Sydney.FromLocal(2026, 10, 3, 18, 0);

        var nextEvening = Sydney.AddDays(evening, 1);

        Assert.Equal(new DateTime(2026, 10, 4, 18, 0, 0), Sydney.ToLocal(nextEvening));
        Assert.Equal(TimeSpan.FromHours(23), nextEvening - evening);
    }

    [Fact]
    public void SettingTimeMovesOnlyTheClock()
    {
        var midnight = Sydney.FromLocal(2026, 8, 14);

        var endOfDay = Sydney.SettingTime(midnight, 23, 59);

        Assert.Equal(new DateTime(2026, 8, 14, 23, 59, 0), Sydney.ToLocal(endOfDay));
    }

    [Fact]
    public void InstantsCompareEqualWhateverOffsetTheyCarry()
    {
        var local = Sydney.FromLocal(2026, 8, 14, 9, 0);

        Assert.Equal(local, local.ToUniversalTime());
        Assert.True(local == local.ToOffset(TimeSpan.FromHours(-5)));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 2026, 8, 9)]
    [InlineData(DayOfWeek.Monday, 2026, 8, 10)]
    public void StartOfWeekFollowsTheFirstWeekday(DayOfWeek firstDay, int year, int month, int day)
    {
        var calendar = new AppCalendar(Sydney.TimeZone, firstDay);

        // Friday 14 August 2026.
        Assert.Equal(new DateOnly(year, month, day), calendar.StartOfWeek(new DateOnly(2026, 8, 14)));
    }

    [Fact]
    public void OrderedWeekdaysStartFromTheFirstWeekday()
    {
        var monday = new AppCalendar(Sydney.TimeZone, DayOfWeek.Monday);

        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            monday.OrderedWeekdays());
    }
}

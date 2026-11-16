using System.Globalization;
using static CanvasCountdown.Tests.Calendar.CalendarTestData;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// The words the calendar shows: headers, column headings, hour labels and
/// chip text, each in a culture pinned by the test rather than the machine's.
/// </summary>
public sealed class CalendarTextTests
{
    [Fact]
    public void EachScaleHasItsOwnHeader()
    {
        var day = At(2026, 8, 4);

        Assert.Equal("Tuesday, August 4, 2026", AssignmentCalendar.Title(CalendarScale.Day, day, Sydney, English));
        Assert.Equal("Aug 2 – Aug 8, 2026", AssignmentCalendar.Title(CalendarScale.Week, day, Sydney, English));
        Assert.Equal("August 2026", AssignmentCalendar.Title(CalendarScale.Month, day, Sydney, English));
        Assert.Equal("2026", AssignmentCalendar.Title(CalendarScale.Year, day, Sydney, English));
    }

    [Fact]
    public void TheHeaderFollowsTheCulturesOrder()
    {
        var day = At(2026, 8, 4);

        Assert.Equal("Tuesday, 4 August 2026", AssignmentCalendar.Title(CalendarScale.Day, day, Sydney, Australian));
        Assert.Equal("2 Aug – 8 Aug 2026", AssignmentCalendar.Title(CalendarScale.Week, day, Sydney, Australian));
        Assert.Equal("August 2026", AssignmentCalendar.MonthTitle(day, Sydney, Australian));
    }

    [Fact]
    public void AWeekAcrossTwoMonthsNamesBoth()
    {
        Assert.Equal("Jul 26 – Aug 1, 2026", AssignmentCalendar.WeekTitle(At(2026, 7, 29), Sydney, English));
        Assert.Equal("26 Jul – 1 Aug 2026", AssignmentCalendar.WeekTitle(At(2026, 7, 29), Sydney, Australian));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, "Dec 27, 2026 – Jan 2, 2027")]
    [InlineData(DayOfWeek.Monday, "Dec 28, 2026 – Jan 3, 2027")]
    public void AWeekAcrossTwoYearsNamesBothYears(DayOfWeek firstDay, string expected)
    {
        Assert.Equal(expected, AssignmentCalendar.WeekTitle(At(2026, 12, 31), WithFirstDay(firstDay), English));
    }

    [Fact]
    public void TheHeaderIsReadInTheCalendarsZoneNotUtc()
    {
        // 09:00 on 1 August in Sydney is still 31 July in UTC.
        var morning = At(2026, 8, 1, hour: 9);

        Assert.Equal("August 2026", AssignmentCalendar.MonthTitle(morning, Sydney, English));
        Assert.Equal("Saturday, August 1, 2026", AssignmentCalendar.Title(CalendarScale.Day, morning, Sydney, English));
    }

    [Fact]
    public void AMonthOnItsOwnIsInTheStandaloneForm()
    {
        var russian = CultureInfo.GetCultureInfo("ru-RU");
        var day = At(2026, 8, 4);

        // Russian writes the month differently beside a day: "4 августа".
        Assert.Equal("август", AssignmentCalendar.StandaloneMonthTitle(day, Sydney, russian));
        Assert.Contains("августа", AssignmentCalendar.Title(CalendarScale.Day, day, Sydney, russian));
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void TheHeaderIsGregorianLikeTheGrid(string cultureName)
    {
        // Thai counts years from the Buddha (2569) and Saudi Arabia's default
        // calendar is Hijri (1448); the squares under the header are Gregorian.
        var title = AssignmentCalendar.MonthTitle(At(2026, 8, 14), Sydney, CultureInfo.GetCultureInfo(cultureName));

        Assert.Contains("2026", title);
        Assert.DoesNotContain("2569", title);
        Assert.DoesNotContain("1448", title);
    }

    [Fact]
    public void ColumnHeadingsFollowTheFirstWeekday()
    {
        Assert.Equal(
            ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
            AssignmentCalendar.WeekdaySymbols(Sydney, English));
        Assert.Equal(
            ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
            AssignmentCalendar.WeekdaySymbols(SydneyMondayFirst, English));
    }

    [Fact]
    public void NarrowHeadingsAreTheShortestTheCultureHasInTheSameOrder()
    {
        var sundayFirst = AssignmentCalendar.NarrowWeekdaySymbols(Sydney, English);
        var mondayFirst = AssignmentCalendar.NarrowWeekdaySymbols(SydneyMondayFirst, English);

        Assert.Equal(["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"], sundayFirst);
        Assert.Equal(sundayFirst.Skip(1).Append(sundayFirst[0]), mondayFirst);
        Assert.All(sundayFirst, symbol => Assert.True(symbol.Length < "Sun".Length));
    }

    [Fact]
    public void HourLabelsAreInTheCulturesOwnClock()
    {
        var twelveHour = CalendarTimeGrid.HourLabels(Sydney, English, now: At(2026, 8, 4));
        var twentyFourHour = CalendarTimeGrid.HourLabels(Sydney, CultureInfo.GetCultureInfo("en-GB"), now: At(2026, 8, 4));

        Assert.Equal(["12 AM", "1 AM", "2 AM"], twelveHour.Take(3).Select(Plain));
        Assert.Equal("12 PM", Plain(twelveHour[12]));
        Assert.Equal("11 PM", Plain(twelveHour[23]));
        Assert.Equal("00", twentyFourHour[0]);
        Assert.Equal("16", twentyFourHour[16]);
    }

    [Fact]
    public void AnHourPatternOfOneLetterIsStillReadAsAnHour()
    {
        // Japanese writes the time "H:mm"; the hour alone is the single letter
        // "H", which .NET would otherwise reject as an unknown standard format.
        var labels = CalendarTimeGrid.HourLabels(Sydney, CultureInfo.GetCultureInfo("ja-JP"), now: At(2026, 8, 4));

        Assert.Equal("0", labels[0]);
        Assert.Equal("13", labels[13]);
    }

    [Fact]
    public void OnTheDayTheClocksGoForwardTheHourLabelsCountElapsedHours()
    {
        // Ported as the Swift has it: the labels are today's midnight plus
        // whole elapsed hours, so on 4 October 2026 in Sydney, when 02:00 does
        // not happen, the third row is labelled 3 AM and the last 12 AM.
        var labels = CalendarTimeGrid.HourLabels(Sydney, English, now: At(2026, 10, 4));

        Assert.Equal(["12 AM", "1 AM", "3 AM", "4 AM"], labels.Take(4).Select(Plain));
        Assert.Equal("12 AM", Plain(labels[23]));
    }

    [Fact]
    public void TheDayColumnsCarryTheirHeadingsAndTheirDeadlines()
    {
        var allDay = Item("DESN1500 Moodboard", At(2026, 8, 14, hour: 0));
        var timed = Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 16));
        var week = AssignmentCalendar.Week(At(2026, 8, 14), [allDay, timed], Sydney, now: At(2026, 8, 14));

        var columns = CalendarTimeGrid.Columns(week, Sydney, English);

        Assert.Equal(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], columns.Select(column => column.WeekdayText));
        var friday = columns[5];
        Assert.Equal(new DateOnly(2026, 8, 14), friday.Day.LocalDate);
        Assert.Equal([allDay], friday.AllDayItems);
        Assert.Equal([timed], friday.TimedEntries.Select(entry => entry.Item));
        Assert.Equal("All day on Aug 14, 2026", friday.AllDayAccessibilityLabel);
        Assert.All(columns.Where(column => column != friday), column => Assert.Empty(column.TimedEntries));
    }

    [Fact]
    public void AChipShowsItsTimeInTheCulturesClock()
    {
        var item = Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 16, minute: 30));

        Assert.Equal("4:30 PM", Plain(CalendarChip.TimeText(item, Sydney, English)));
        Assert.Equal("16:30", CalendarChip.TimeText(item, Sydney, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void AChipsTooltipNamesTheCourseOnlyWhenThereIsOne()
    {
        var due = At(2026, 8, 14, hour: 16, minute: 30);

        Assert.Equal(
            "Assignment 2\nCOMP2050 Data Structures\nAug 14, 2026 4:30 PM",
            Plain(CalendarChip.HelpText(Item("Assignment 2", due), Sydney, English)));
        Assert.Equal(
            "Assignment 2\nAug 14, 2026 4:30 PM",
            Plain(CalendarChip.HelpText(Item("Assignment 2", due, courseName: "   "), Sydney, English)));
    }

    [Fact]
    public void PatternsWithoutAWeekdayKeepTheirOwnPunctuation()
    {
        Assert.Equal("MMMM d, yyyy", CalendarDateFormats.WithoutWeekday("dddd, MMMM d, yyyy"));
        Assert.Equal("yyyy年M月d日", CalendarDateFormats.WithoutWeekday("yyyy年M月d日 dddd"));
        Assert.Equal("d MMMM yyyy 'г'.", CalendarDateFormats.WithoutWeekday("dddd, d MMMM yyyy 'г'."));
        Assert.Equal("d 'de' MMM 'de' yyyy", CalendarDateFormats.WithShortMonth("d 'de' MMMM 'de' yyyy"));
        Assert.Equal("h tt", CalendarDateFormats.HourOnly("h:mm tt"));
        Assert.Equal("tt h", CalendarDateFormats.HourOnly("tt h:mm"));
        Assert.Equal("HH", CalendarDateFormats.HourOnly("HH:mm:ss"));
        Assert.Equal("HH 'h'", CalendarDateFormats.HourOnly("HH 'h' mm"));
    }
}

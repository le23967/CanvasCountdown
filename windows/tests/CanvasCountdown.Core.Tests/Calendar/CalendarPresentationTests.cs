using static CanvasCountdown.Tests.Calendar.CalendarTestData;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// What the views used to decide for themselves — how many chips a square
/// shows, which colour a dot takes, how wide a column is — now decided once.
/// </summary>
public sealed class CalendarPresentationTests
{
    private static CalendarDay DayWith(params AssignmentListItem[] items)
        => AssignmentCalendar.Day(At(2026, 8, 14), items, Sydney, now: At(2026, 8, 1));

    // The month grid's squares

    [Theory]
    [InlineData(0, 0, 0, null)]
    [InlineData(1, 1, 0, null)]
    [InlineData(2, 2, 0, null)]
    [InlineData(3, 2, 1, "+1 more")]
    [InlineData(5, 2, 3, "+3 more")]
    public void ASquareShowsTwoChipsAndCountsTheRest(int itemCount, int visible, int hidden, string? overflow)
    {
        var items = Enumerable
            .Range(0, itemCount)
            .Select(index => Item($"STAT1010 Quiz {index + 1}", At(2026, 8, 14, hour: 9 + index)))
            .ToArray();
        var day = DayWith(items);

        var cell = Assert.Single(CalendarMonthCell.Cells([day], selectedDay: null, Sydney, English));

        Assert.Equal(items.Take(visible), cell.VisibleItems);
        Assert.Equal(hidden, cell.HiddenCount);
        Assert.Equal(overflow, cell.OverflowText);
    }

    [Fact]
    public void TheSelectedSquareIsFoundByItsDayNotItsInstant()
    {
        var days = AssignmentCalendar.Month(At(2026, 8, 14), [], Sydney, now: At(2026, 8, 1));

        var cells = CalendarMonthCell.Cells(days, selectedDay: At(2026, 8, 14, hour: 18, minute: 30), Sydney, English);

        var selected = Assert.Single(cells, cell => cell.IsSelected);
        Assert.Equal(new DateOnly(2026, 8, 14), selected.Day.LocalDate);
        Assert.DoesNotContain(CalendarMonthCell.Cells(days, selectedDay: null, Sydney, English), cell => cell.IsSelected);
    }

    [Fact]
    public void ClickingTheSelectedSquareAgainClosesIt()
    {
        var days = AssignmentCalendar.Month(At(2026, 8, 14), [], Sydney, now: At(2026, 8, 1));
        var cells = CalendarMonthCell.Cells(days, selectedDay: At(2026, 8, 14), Sydney, English);

        var selected = cells.Single(cell => cell.IsSelected);
        var other = cells.First(cell => !cell.IsSelected);

        Assert.Null(selected.SelectionAfterClick);
        Assert.Equal(other.Day.Date, other.SelectionAfterClick);
    }

    [Fact]
    public void ASquareReadsOutItsDayAndWhatIsDue()
    {
        var empty = CalendarMonthCell.Cells([DayWith()], null, Sydney, English)[0];
        var one = CalendarMonthCell.Cells([DayWith(Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 9)))], null, Sydney, English)[0];
        var several = CalendarMonthCell.Cells(
            [
                DayWith(
                    Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 9)),
                    Item("STAT1010 Quiz 5", At(2026, 8, 14, hour: 12)),
                    Item("DESN1500 Studio Journal", At(2026, 8, 14, hour: 17))),
            ],
            null,
            Sydney,
            English)[0];

        Assert.Equal("Friday, August 14, 2026", empty.DateText);
        Assert.Equal("Friday, August 14, 2026, no assignments", empty.AccessibilityLabel);
        Assert.Equal("Friday, August 14, 2026, 1 assignment: COMP2050 Lab 4", one.AccessibilityLabel);
        Assert.Equal(
            "Friday, August 14, 2026, 3 assignments: COMP2050 Lab 4, STAT1010 Quiz 5, DESN1500 Studio Journal",
            several.AccessibilityLabel);
    }

    // The year grid's squares

    [Fact]
    public void ADotTakesTheColourOfTheFirstLiveLabelledDeadline()
    {
        var label = Guid.NewGuid();
        var other = Guid.NewGuid();
        var doneButLabelled = Item("COMP2050 Lab 3", At(2026, 8, 14, hour: 8), isCompleted: true, labelId: other);
        var unlabelled = Item("STAT1010 Quiz 5", At(2026, 8, 14, hour: 9));
        var labelled = Item("DESN1500 Critique", At(2026, 8, 14, hour: 10), labelId: label);

        var dot = CalendarYearCell.DotFor(DayWith(doneButLabelled, unlabelled, labelled), item => item.LabelId is not null);

        Assert.Equal(new CalendarDot(CalendarTint.Label, labelled), dot);
    }

    [Fact]
    public void ALabelThatCannotBeFoundGivesNoColour()
    {
        var deleted = Guid.NewGuid();
        var orphaned = Item("COMP2050 Lab 3", At(2026, 8, 14, hour: 8), labelId: deleted);

        var dot = CalendarYearCell.DotFor(DayWith(orphaned), item => item.LabelId is { } id && id != deleted);

        Assert.Equal(new CalendarDot(CalendarTint.Accent), dot);
    }

    [Fact]
    public void ADayWhereEverythingIsSettledHasAQuietDot()
    {
        var done = Item("COMP2050 Lab 3", At(2026, 8, 14, hour: 8), isCompleted: true, labelId: Guid.NewGuid());
        var ignored = Item("STAT1010 Reading", At(2026, 8, 14, hour: 9), isIgnored: true);

        Assert.Equal(new CalendarDot(CalendarTint.Secondary), CalendarYearCell.DotFor(DayWith(done, ignored), _ => true));
        Assert.Equal(new CalendarDot(CalendarTint.Clear), CalendarYearCell.DotFor(DayWith(), _ => true));
    }

    [Fact]
    public void TheNeighbouringMonthsDaysKeepTheirSquareButAreHidden()
    {
        var months = AssignmentCalendar.Year(At(2026, 8, 14), [], Sydney, now: At(2026, 8, 14), culture: English);
        var august = months.Single(month => Sydney.DateOf(month.Start).Month == 8);

        var cells = CalendarYearCell.Cells(august, _ => false, English);

        Assert.Equal(42, cells.Count);
        Assert.Equal(11, cells.Count(cell => cell.IsHidden));
        Assert.All(cells.Where(cell => cell.IsHidden), cell => Assert.False(cell.Day.IsInDisplayedMonth));
    }

    [Fact]
    public void AYearSquareReadsOutHowManyThingsAreDue()
    {
        var month = new CalendarMonthSummary
        {
            Start = At(2026, 8, 1, hour: 0),
            Title = "August",
            Days =
            [
                DayWith(),
                DayWith(Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 9))),
                DayWith(Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 9)), Item("STAT1010 Quiz 5", At(2026, 8, 14, hour: 12))),
            ],
        };

        var labels = CalendarYearCell.Cells(month, _ => false, English).Select(cell => cell.AccessibilityLabel);

        Assert.Equal(
            ["Friday, August 14, 2026", "Friday, August 14, 2026, 1 assignment", "Friday, August 14, 2026, 2 assignments"],
            labels);
    }

    // Chips

    [Fact]
    public void ALabelWinsOverTheAccentUntilTheWorkIsDone()
    {
        var due = At(2026, 8, 14, hour: 9);

        Assert.Equal(new CalendarChipStyle(CalendarTint.Accent, false, false), CalendarChip.Style(Item("Lab", due), hasLabel: false));
        Assert.Equal(new CalendarChipStyle(CalendarTint.Label, false, false), CalendarChip.Style(Item("Lab", due), hasLabel: true));
        Assert.Equal(
            new CalendarChipStyle(CalendarTint.Secondary, IsDimmed: true, IsStruckThrough: true),
            CalendarChip.Style(Item("Lab", due, isCompleted: true), hasLabel: true));
        Assert.Equal(
            new CalendarChipStyle(CalendarTint.Secondary, IsDimmed: true, IsStruckThrough: false),
            CalendarChip.Style(Item("Lab", due, isIgnored: true), hasLabel: true));
    }

    // Layout

    [Theory]
    [InlineData(706, 100)]
    [InlineData(244, 34)]
    [InlineData(100, 34)]
    [InlineData(0, 34)]
    [InlineData(double.NaN, 34)]
    public void MonthColumnsShareTheWidthButNeverShrinkBelowTheirMinimum(double available, double expected)
    {
        Assert.Equal(expected, CalendarLayout.MonthColumnWidth(available), precision: 6);
    }

    [Theory]
    [InlineData(150, 1, 190)]
    [InlineData(300, 1, 300)]
    [InlineData(380, 1, 320)]
    [InlineData(404, 2, 190)]
    [InlineData(1000, 4, 232)]
    [InlineData(1500, 7, 193.714286)]
    public void TheYearFitsAsManyMonthsInARowAsHaveRoom(double available, int count, double width)
    {
        var columns = CalendarLayout.YearMonthColumns(available);

        Assert.Equal(count, columns.Count);
        Assert.Equal(width, columns.Width, precision: 5);
    }

    [Theory]
    [InlineData(232, 32.285714)]
    [InlineData(50, 14)]
    public void ASmallMonthsDaysShareItsWidth(double monthWidth, double expected)
    {
        Assert.Equal(expected, CalendarLayout.YearDayColumnWidth(monthWidth), precision: 5);
    }

    [Fact]
    public void OverlappingChipsSplitTheColumnAndSitAtTheirMinute()
    {
        var entry = new TimedCalendarEntry
        {
            Item = Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 16)),
            MinuteOfDay = 16 * 60,
            Column = 2,
            ColumnCount = 3,
        };

        var placement = CalendarLayout.TimedChip(entry, columnWidth: 300, hourHeight: 44);

        Assert.Equal(296.0 / 3 - 2, placement.Width, precision: 6);
        Assert.Equal(2 + 2 * (296.0 / 3), placement.X, precision: 6);
        Assert.Equal(704, placement.Y, precision: 6);
    }

    [Fact]
    public void ACrowdedSlotKeepsItsChipsWideEnoughToRead()
    {
        var entry = new TimedCalendarEntry
        {
            Item = Item("COMP2050 Lab 4", At(2026, 8, 14, hour: 9)),
            MinuteOfDay = 9 * 60,
            Column = 2,
            ColumnCount = 3,
        };

        var placement = CalendarLayout.TimedChip(entry, columnWidth: 60, hourHeight: 44);

        Assert.Equal(28, placement.Width);
        Assert.Equal(62, placement.X);
    }

    [Fact]
    public void TheHourGridIsMeasuredInHoursOfTheGivenHeight()
    {
        Assert.Equal(66, CalendarLayout.VerticalOffset(90, hourHeight: 44), precision: 6);
        Assert.Equal(1056, CalendarLayout.TimeGridHeight(hourHeight: 44), precision: 6);
    }
}

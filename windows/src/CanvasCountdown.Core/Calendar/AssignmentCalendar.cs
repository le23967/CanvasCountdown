using System.Globalization;

namespace CanvasCountdown;

/// <summary>One square in a grid, whatever grid it is in.</summary>
public sealed record CalendarDay
{
    /// <summary>The first instant of the day.</summary>
    public required DateTimeOffset Date { get; init; }

    /// <summary>
    /// The day itself, on the calendar the grid was built with, so the number
    /// in the square is always the day the square stands for.
    /// </summary>
    public required DateOnly LocalDate { get; init; }

    public required bool IsInDisplayedMonth { get; init; }

    public required bool IsToday { get; init; }

    public required ValueList<AssignmentListItem> Items { get; init; }

    public DateTimeOffset Id => Date;

    public int DayNumber => LocalDate.Day;
}

/// <summary>One of the twelve small grids in the year view.</summary>
public sealed record CalendarMonthSummary
{
    public required DateTimeOffset Start { get; init; }

    public required string Title { get; init; }

    public required ValueList<CalendarDay> Days { get; init; }

    public DateTimeOffset Id => Start;
}

/// <summary>
/// Builds every grid the calendar shows.
/// </summary>
/// <remarks>
/// Pure and calendar-driven: no arithmetic on 86,400-second days, so months of
/// any length and daylight-saving transitions come out right. The events it is
/// given are already filtered, so the calendar always shows exactly what the
/// list would show.
/// </remarks>
public static class AssignmentCalendar
{
    /// <summary>
    /// Six weeks of squares in the month grid, always. A fixed height stops the
    /// grid jumping as the user pages between months.
    /// </summary>
    public const int WeeksShown = 6;

    // Grids

    public static ValueList<CalendarDay> Month(
        DateTimeOffset containing,
        IEnumerable<AssignmentListItem> events,
        AppCalendar calendar,
        DateTimeOffset now)
        => Month(containing, GroupedByDay(events, calendar), calendar, now);

    public static ValueList<CalendarDay> Week(
        DateTimeOffset containing,
        IEnumerable<AssignmentListItem> events,
        AppCalendar calendar,
        DateTimeOffset now)
    {
        if (CalendarDates.FirstDayOfWeek(calendar.DateOf(containing), calendar) is not { } weekStart)
        {
            return ValueList<CalendarDay>.Empty;
        }
        var eventsByDay = GroupedByDay(events, calendar);
        var today = calendar.DateOf(now);

        var days = new List<CalendarDay>(7);
        for (var offset = 0; offset < 7; offset++)
        {
            if (CalendarDates.AddingDays(weekStart, offset) is not { } date)
            {
                break;
            }
            days.Add(MakeDay(date, isInDisplayedMonth: true, today, eventsByDay, calendar));
        }
        return days.ToValueList();
    }

    public static CalendarDay Day(
        DateTimeOffset containing,
        IEnumerable<AssignmentListItem> events,
        AppCalendar calendar,
        DateTimeOffset now)
    {
        var date = calendar.DateOf(containing);
        return new CalendarDay
        {
            Date = CalendarDates.StartOfDay(date, calendar),
            LocalDate = date,
            IsInDisplayedMonth = true,
            IsToday = date == calendar.DateOf(now),
            Items = GroupedByDay(events, calendar).GetValueOrDefault(date, ValueList<AssignmentListItem>.Empty),
        };
    }

    /// <summary>Twelve month grids for the year containing <paramref name="containing"/>.</summary>
    public static ValueList<CalendarMonthSummary> Year(
        DateTimeOffset containing,
        IEnumerable<AssignmentListItem> events,
        AppCalendar calendar,
        DateTimeOffset now,
        CultureInfo culture)
    {
        var year = calendar.DateOf(containing).Year;
        var eventsByDay = GroupedByDay(events, calendar);

        var months = new List<CalendarMonthSummary>(12);
        for (var month = 1; month <= 12; month++)
        {
            var monthStart = CalendarDates.StartOfDay(new DateOnly(year, month, 1), calendar);
            months.Add(new CalendarMonthSummary
            {
                Start = monthStart,
                Title = StandaloneMonthTitle(monthStart, calendar, culture),
                Days = Month(monthStart, eventsByDay, calendar, now),
            });
        }
        return months.ToValueList();
    }

    // Moving around

    /// <summary>One step of whatever the calendar is currently showing.</summary>
    /// <remarks>
    /// A day keeps its time of day across a change of clocks; a month or a year
    /// that lands past the end of a shorter month lands on its last day. A
    /// step past the last day .NET can represent stays where it is, as the
    /// Swift stayed put when its calendar could not answer.
    /// </remarks>
    public static DateTimeOffset DateByAdding(CalendarScale scale, int count, DateTimeOffset to, AppCalendar calendar)
    {
        try
        {
            return scale switch
            {
                CalendarScale.Day => calendar.AddDays(to, count),
                CalendarScale.Week => calendar.AddDays(to, checked(count * 7)),
                CalendarScale.Month => calendar.AddMonths(to, count),
                CalendarScale.Year => calendar.AddYears(to, count),
                _ => to,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return to;
        }
        catch (OverflowException)
        {
            return to;
        }
    }

    /// <summary>The first instant of the period a date belongs to.</summary>
    /// <remarks>
    /// Stepping works on this rather than on the day itself, because adding a
    /// month to the 31st lands on the 30th and the calendar would walk backwards
    /// through the months a day at a time. Which day the anchor holds is
    /// invisible in every scale above <see cref="CalendarScale.Day"/>, so
    /// nothing is lost by pinning it.
    /// </remarks>
    public static DateTimeOffset Normalized(DateTimeOffset date, CalendarScale scale, AppCalendar calendar)
    {
        var local = calendar.DateOf(date);
        var startOfDay = CalendarDates.StartOfDay(local, calendar);
        return scale switch
        {
            CalendarScale.Day => startOfDay,
            CalendarScale.Week => StartOfWeek(date, calendar) ?? startOfDay,
            CalendarScale.Month => StartOfMonth(date, calendar),
            CalendarScale.Year => CalendarDates.StartOfDay(AppCalendar.StartOfYear(local), calendar),
            _ => startOfDay,
        };
    }

    /// <summary>
    /// One step of whatever is on screen: a day in Day, a week in Week, and so
    /// on, so the arrows always mean what the switcher says. What the back and
    /// forward controls do to the anchor.
    /// </summary>
    /// <remarks>
    /// Anchored to the start of the period first, so paging through the months
    /// from the 31st does not slide a day earlier each time.
    /// </remarks>
    public static DateTimeOffset Step(DateTimeOffset anchor, CalendarScale scale, int steps, AppCalendar calendar)
        => DateByAdding(scale, steps, Normalized(anchor, scale, calendar), calendar);

    /// <summary>
    /// Whether the period on screen holds today — the day, the week, the month
    /// or the year — which is when the Today control has nothing to do.
    /// </summary>
    public static bool IsShowingToday(CalendarScale scale, DateTimeOffset anchor, DateTimeOffset now, AppCalendar calendar)
    {
        var shown = calendar.DateOf(anchor);
        var today = calendar.DateOf(now);
        return scale switch
        {
            CalendarScale.Day => shown == today,
            CalendarScale.Week => CalendarDates.WeekKey(shown, calendar) == CalendarDates.WeekKey(today, calendar),
            CalendarScale.Month => shown.Year == today.Year && shown.Month == today.Month,
            CalendarScale.Year => shown.Year == today.Year,
            _ => false,
        };
    }

    /// <summary>The events due on one day, earliest first: what a selected day lists.</summary>
    public static ValueList<AssignmentListItem> Items(
        DateTimeOffset on,
        IEnumerable<AssignmentListItem> events,
        AppCalendar calendar)
    {
        var day = calendar.DateOf(on);
        return events
            .Where(item => calendar.DateOf(item.DueDate) == day)
            .OrderBy(item => item.DueDate)
            .ToValueList();
    }

    public static DateTimeOffset StartOfMonth(DateTimeOffset date, AppCalendar calendar)
        => CalendarDates.StartOfDay(AppCalendar.StartOfMonth(calendar.DateOf(date)), calendar);

    /// <summary>
    /// Honours the user's first weekday rather than assuming Sunday or Monday.
    /// Null only when the week would begin before 1 January 0001.
    /// </summary>
    public static DateTimeOffset? StartOfWeek(DateTimeOffset date, AppCalendar calendar)
        => CalendarDates.FirstDayOfWeek(calendar.DateOf(date), calendar) is { } first
            ? CalendarDates.StartOfDay(first, calendar)
            : null;

    /// <summary>Backs up to the first day of the week containing the first of the month.</summary>
    public static DateTimeOffset? StartOfGrid(DateTimeOffset monthStart, AppCalendar calendar)
        => StartOfWeek(monthStart, calendar);

    // Titles

    /// <summary>Column headings in the user's own week order.</summary>
    public static ValueList<string> WeekdaySymbols(AppCalendar calendar, CultureInfo culture)
        => RotatedToFirstWeekday(CalendarDateFormats.For(culture).Format.AbbreviatedDayNames, calendar);

    /// <summary>
    /// The narrowest headings the culture has, for the twelve small grids where
    /// a three-letter heading would not fit.
    /// </summary>
    /// <remarks>
    /// The Mac has single letters for this; the narrowest Windows offers are
    /// the culture's shortest day names ("Su", "Mo" in English). They can
    /// repeat in some languages, as the Mac's single letters do in English, so
    /// a view must place them by position and never tell them apart by text.
    /// </remarks>
    public static ValueList<string> NarrowWeekdaySymbols(AppCalendar calendar, CultureInfo culture)
        => RotatedToFirstWeekday(CalendarDateFormats.For(culture).Format.ShortestDayNames, calendar);

    /// <summary>
    /// What the header says, which is different for each scale: a week has no
    /// single month, and a year has no month at all.
    /// </summary>
    public static string Title(CalendarScale scale, DateTimeOffset date, AppCalendar calendar, CultureInfo culture)
    {
        var formats = CalendarDateFormats.For(culture);
        return scale switch
        {
            CalendarScale.Day => formats.Text(calendar.ToLocal(date), formats.FullDate),
            CalendarScale.Week => WeekTitle(date, calendar, culture),
            CalendarScale.Year => formats.Text(calendar.ToLocal(date), CalendarDateFormats.YearOnly),
            _ => MonthTitle(date, calendar, culture),
        };
    }

    public static string MonthTitle(DateTimeOffset date, AppCalendar calendar, CultureInfo culture)
    {
        var formats = CalendarDateFormats.For(culture);
        return formats.Text(calendar.ToLocal(date), formats.MonthAndYear);
    }

    /// <summary>
    /// A range, because a week rarely sits inside one month and never inside
    /// one when it matters most — at the turn of a month or a year.
    /// </summary>
    /// <remarks>
    /// The first day leaves its year out when both ends share one; both ends
    /// always name their month, even within a single month.
    /// </remarks>
    public static string WeekTitle(DateTimeOffset date, AppCalendar calendar, CultureInfo culture)
    {
        if (CalendarDates.FirstDayOfWeek(calendar.DateOf(date), calendar) is not { } start
            || CalendarDates.AddingDays(start, 6) is not { } end)
        {
            return MonthTitle(date, calendar, culture);
        }
        var formats = CalendarDateFormats.For(culture);
        var sameYear = start.Year == end.Year;
        var leading = formats.Text(start, sameYear ? formats.DayAndShortMonth : formats.ShortDate);
        var trailing = formats.Text(end, formats.ShortDate);
        return $"{leading} – {trailing}";
    }

    /// <summary>
    /// "January" rather than "January 2026": the year is already in the header
    /// above the twelve grids.
    /// </summary>
    public static string StandaloneMonthTitle(DateTimeOffset date, AppCalendar calendar, CultureInfo culture)
        => CalendarDateFormats.For(culture).Text(calendar.ToLocal(date), CalendarDateFormats.StandaloneMonth);

    // Helpers

    private static ValueList<CalendarDay> Month(
        DateTimeOffset containing,
        IReadOnlyDictionary<DateOnly, ValueList<AssignmentListItem>> eventsByDay,
        AppCalendar calendar,
        DateTimeOffset now)
    {
        var monthStart = AppCalendar.StartOfMonth(calendar.DateOf(containing));
        if (CalendarDates.FirstDayOfWeek(monthStart, calendar) is not { } gridStart)
        {
            return ValueList<CalendarDay>.Empty;
        }
        var today = calendar.DateOf(now);

        var days = new List<CalendarDay>(WeeksShown * 7);
        for (var offset = 0; offset < WeeksShown * 7; offset++)
        {
            if (CalendarDates.AddingDays(gridStart, offset) is not { } date)
            {
                break;
            }
            days.Add(MakeDay(date, isInDisplayedMonth: date.Month == monthStart.Month, today, eventsByDay, calendar));
        }
        return days.ToValueList();
    }

    private static CalendarDay MakeDay(
        DateOnly date,
        bool isInDisplayedMonth,
        DateOnly today,
        IReadOnlyDictionary<DateOnly, ValueList<AssignmentListItem>> eventsByDay,
        AppCalendar calendar)
        => new()
        {
            Date = CalendarDates.StartOfDay(date, calendar),
            LocalDate = date,
            IsInDisplayedMonth = isInDisplayedMonth,
            IsToday = date == today,
            Items = eventsByDay.GetValueOrDefault(date, ValueList<AssignmentListItem>.Empty),
        };

    private static Dictionary<DateOnly, ValueList<AssignmentListItem>> GroupedByDay(
        IEnumerable<AssignmentListItem> events,
        AppCalendar calendar)
        => events
            .GroupBy(item => calendar.DateOf(item.DueDate))
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.DueDate).ToValueList());

    private static ValueList<string> RotatedToFirstWeekday(string[] symbols, AppCalendar calendar)
    {
        if (symbols.Length != 7)
        {
            return symbols.ToValueList();
        }
        var first = (int)calendar.FirstDayOfWeek;
        return symbols[first..].Concat(symbols[..first]).ToValueList();
    }
}

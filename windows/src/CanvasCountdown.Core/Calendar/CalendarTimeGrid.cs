using System.Globalization;

namespace CanvasCountdown;

/// <summary>One deadline placed on a day's timeline.</summary>
/// <remarks>
/// <see cref="Column"/> and <see cref="ColumnCount"/> are how two deadlines an
/// hour apart stop drawing on top of each other: everything that overlaps
/// shares the width of the day.
/// </remarks>
public sealed record TimedCalendarEntry
{
    public required AssignmentListItem Item { get; init; }

    public required int MinuteOfDay { get; init; }

    public required int Column { get; init; }

    public required int ColumnCount { get; init; }

    public Guid Id => Item.Id;
}

/// <summary>
/// One day's column in the day and week views, with everything it shows
/// already decided.
/// </summary>
public sealed record CalendarTimeColumn
{
    public required CalendarDay Day { get; init; }

    /// <summary>The abbreviated weekday over the day number: "Wed".</summary>
    public required string WeekdayText { get; init; }

    /// <summary>Everything due "on this day" with no time of its own, for the strip above the hours.</summary>
    public required ValueList<AssignmentListItem> AllDayItems { get; init; }

    public required ValueList<TimedCalendarEntry> TimedEntries { get; init; }

    /// <summary>"All day on Aug 14, 2026", read out for the strip.</summary>
    public required string AllDayAccessibilityLabel { get; init; }
}

/// <summary>
/// Turns a day's deadlines into positions on an hour grid.
/// </summary>
/// <remarks>
/// A deadline is an instant, not a meeting: nothing here reads a duration,
/// because there is none to read. <see cref="EntryMinutes"/> is the height a
/// chip is drawn at, and the only reason two deadlines are ever considered to
/// overlap.
///
/// One layout for the day and the week, because a week is seven day columns
/// and nothing else.
/// </remarks>
public static class CalendarTimeGrid
{
    public const int HoursShown = 24;

    public const int EntryMinutes = 50;

    /// <summary>Where the grid opens when nothing on screen has a time.</summary>
    public const int DefaultInitialHour = 8;

    /// <summary>
    /// Midnight means "this day" rather than "00:00", which is how Canvas
    /// writes a deadline with no time on it. Those belong in the strip above
    /// the grid, not squeezed into the first row.
    /// </summary>
    public static bool IsAllDay(AssignmentListItem item, AppCalendar calendar)
        => MinuteOfDay(item.DueDate, calendar) == 0;

    public static int MinuteOfDay(DateTimeOffset date, AppCalendar calendar)
    {
        var time = calendar.TimeOf(date);
        return time.Hour * 60 + time.Minute;
    }

    public static ValueList<AssignmentListItem> AllDayItems(IEnumerable<AssignmentListItem> items, AppCalendar calendar)
        => items.Where(item => IsAllDay(item, calendar)).ToValueList();

    /// <summary>Everything with a time on it, laid out into columns.</summary>
    public static ValueList<TimedCalendarEntry> TimedEntries(IEnumerable<AssignmentListItem> items, AppCalendar calendar)
    {
        var timed = items
            .Where(item => !IsAllDay(item, calendar))
            .Select(item => (Item: item, Minute: MinuteOfDay(item.DueDate, calendar)))
            .OrderBy(placed => placed.Minute)
            .ThenBy(placed => placed.Item.Title, TextOrder.Comparer)
            .ToList();

        var entries = new List<TimedCalendarEntry>(timed.Count);
        // A cluster is a run of deadlines that all touch each other, directly or
        // through a neighbour. Its width is decided once, when it ends, so every
        // chip in it lines up.
        var cluster = new List<(AssignmentListItem Item, int Minute, int Column)>();
        var columnEnds = new List<int>();

        void CloseCluster()
        {
            var width = Math.Max(columnEnds.Count, 1);
            foreach (var placed in cluster)
            {
                entries.Add(new TimedCalendarEntry
                {
                    Item = placed.Item,
                    MinuteOfDay = placed.Minute,
                    Column = placed.Column,
                    ColumnCount = width,
                });
            }
            cluster.Clear();
            columnEnds.Clear();
        }

        foreach (var (item, minute) in timed)
        {
            if (columnEnds.TrueForAll(end => end <= minute))
            {
                CloseCluster();
            }
            var column = columnEnds.FindIndex(end => end <= minute);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(minute + EntryMinutes);
            }
            else
            {
                columnEnds[column] = minute + EntryMinutes;
            }
            cluster.Add((item, minute, column));
        }
        CloseCluster();

        return entries.ToValueList();
    }

    /// <summary>The row labels down the left, in the user's own clock.</summary>
    /// <remarks>
    /// Counted in elapsed hours from the start of <paramref name="now"/>'s day,
    /// as the macOS app counts them, so on a day the clocks change the labels
    /// skip or repeat an hour.
    /// </remarks>
    public static ValueList<string> HourLabels(AppCalendar calendar, CultureInfo culture, DateTimeOffset now)
    {
        var formats = CalendarDateFormats.For(culture);
        var reference = calendar.StartOfDay(now);
        return Enumerable
            .Range(0, HoursShown)
            .Select(hour => formats.Text(calendar.ToLocal(reference.AddHours(hour)), formats.Hour))
            .ToValueList();
    }

    /// <summary>The day columns of a day or week view.</summary>
    public static ValueList<CalendarTimeColumn> Columns(
        IEnumerable<CalendarDay> days,
        AppCalendar calendar,
        CultureInfo culture)
    {
        var formats = CalendarDateFormats.For(culture);
        return days
            .Select(day => new CalendarTimeColumn
            {
                Day = day,
                WeekdayText = formats.AbbreviatedWeekday(day.LocalDate),
                AllDayItems = AllDayItems(day.Items, calendar),
                TimedEntries = TimedEntries(day.Items, calendar),
                AllDayAccessibilityLabel = $"All day on {formats.Text(day.LocalDate, formats.ShortDate)}",
            })
            .ToValueList();
    }

    /// <summary>
    /// Whether the all-day strip is drawn at all: only when some day on screen
    /// has something due with no time of its own.
    /// </summary>
    public static bool ShowsAllDayStrip(IEnumerable<CalendarDay> days, AppCalendar calendar)
        => days.Any(day => day.Items.Any(item => IsAllDay(item, calendar)));

    /// <summary>The all-day strip draws compact chips once more than one day shares the width.</summary>
    public static bool AllDayChipsAreCompact(int dayCount) => dayCount > 1;

    /// <summary>
    /// The hour the grid opens scrolled to: the first deadline on screen rather
    /// than midnight, with an hour of context above it.
    /// </summary>
    public static int InitialHour(IEnumerable<CalendarDay> days, AppCalendar calendar)
    {
        int? earliest = null;
        foreach (var day in days)
        {
            foreach (var item in day.Items)
            {
                if (IsAllDay(item, calendar))
                {
                    continue;
                }
                var minute = MinuteOfDay(item.DueDate, calendar);
                earliest = earliest is { } current ? Math.Min(current, minute) : minute;
            }
        }
        return earliest is { } first ? Math.Max(0, first / 60 - 1) : DefaultInitialHour;
    }
}

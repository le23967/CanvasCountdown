using System.Globalization;

namespace CanvasCountdown;

/// <summary>One square of the month grid, with what it shows decided.</summary>
public sealed record CalendarMonthCell
{
    /// <summary>
    /// Two entries fit comfortably; the rest are counted so the row height stays
    /// even across the month.
    /// </summary>
    public const int ChipLimit = 2;

    public required CalendarDay Day { get; init; }

    /// <summary>The square the user clicked last, outlined, with its popover open.</summary>
    public required bool IsSelected { get; init; }

    /// <summary>The day written out in full, for the heading of its popover.</summary>
    public required string DateText { get; init; }

    public required string AccessibilityLabel { get; init; }

    /// <summary>The chips drawn in the square.</summary>
    public ValueList<AssignmentListItem> VisibleItems => Day.Items.Take(ChipLimit).ToValueList();

    public int HiddenCount => Math.Max(0, Day.Items.Count - ChipLimit);

    /// <summary>"+3 more" under the chips, or null when every chip is drawn.</summary>
    public string? OverflowText => HiddenCount > 0 ? $"+{HiddenCount} more" : null;

    /// <summary>What a click selects: this day, or nothing when it was already selected.</summary>
    public DateTimeOffset? SelectionAfterClick => IsSelected ? null : Day.Date;

    public static ValueList<CalendarMonthCell> Cells(
        IEnumerable<CalendarDay> days,
        DateTimeOffset? selectedDay,
        AppCalendar calendar,
        CultureInfo culture)
    {
        var formats = CalendarDateFormats.For(culture);
        DateOnly? selected = selectedDay is { } instant ? calendar.DateOf(instant) : null;
        return days
            .Select(day =>
            {
                var dateText = formats.Text(day.LocalDate, formats.FullDate);
                return new CalendarMonthCell
                {
                    Day = day,
                    IsSelected = selected == day.LocalDate,
                    DateText = dateText,
                    AccessibilityLabel = AccessibilityLabelFor(day, dateText),
                };
            })
            .ToValueList();
    }

    private static string AccessibilityLabelFor(CalendarDay day, string dateText)
    {
        if (day.Items.IsEmpty)
        {
            return $"{dateText}, no assignments";
        }
        var count = day.Items.Count == 1 ? "1 assignment" : $"{day.Items.Count} assignments";
        return $"{dateText}, {count}: " + string.Join(", ", day.Items.Select(item => item.Title));
    }
}

/// <summary>The year view's mark for a day.</summary>
/// <param name="Tint">The colour of the dot; <see cref="CalendarTint.Clear"/> when nothing is due.</param>
/// <param name="LabelledItem">When the dot takes a label's colour, the deadline whose label it is.</param>
public sealed record CalendarDot(CalendarTint Tint, AssignmentListItem? LabelledItem = null);

/// <summary>One day in the year view's twelve small grids.</summary>
/// <remarks>
/// Nothing is written in the squares at this size, so a day that has something
/// due is marked with a dot instead. Clicking a day opens it; clicking a month
/// name opens the month.
/// </remarks>
public sealed record CalendarYearCell
{
    public required CalendarDay Day { get; init; }

    public required CalendarDot Dot { get; init; }

    public required string AccessibilityLabel { get; init; }

    /// <summary>A neighbouring month's day: it keeps its square but is neither drawn nor clickable.</summary>
    public bool IsHidden => !Day.IsInDisplayedMonth;

    /// <param name="month">One of the twelve grids.</param>
    /// <param name="hasLabel">Whether the user's label for a deadline was found, and so has a colour to give.</param>
    /// <param name="culture">The language the days are read out in.</param>
    public static ValueList<CalendarYearCell> Cells(
        CalendarMonthSummary month,
        Func<AssignmentListItem, bool> hasLabel,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(month);
        var formats = CalendarDateFormats.For(culture);
        return month.Days
            .Select(day => new CalendarYearCell
            {
                Day = day,
                Dot = DotFor(day, hasLabel),
                AccessibilityLabel = AccessibilityLabelFor(day, formats.Text(day.LocalDate, formats.FullDate)),
            })
            .ToValueList();
    }

    /// <summary>
    /// One dot for the day, so it takes the colour of the first labelled thing
    /// on it. At this size there is room to say "something is here", not to
    /// list what.
    /// </summary>
    public static CalendarDot DotFor(CalendarDay day, Func<AssignmentListItem, bool> hasLabel)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(hasLabel);
        if (day.Items.IsEmpty)
        {
            return new CalendarDot(CalendarTint.Clear);
        }
        var live = day.Items.Where(item => !item.IsCompleted && !item.IsIgnored).ToList();
        if (live.Count == 0)
        {
            return new CalendarDot(CalendarTint.Secondary);
        }
        if (live.FirstOrDefault(hasLabel) is { } labelled)
        {
            return new CalendarDot(CalendarTint.Label, labelled);
        }
        return new CalendarDot(CalendarTint.Accent);
    }

    private static string AccessibilityLabelFor(CalendarDay day, string dateText)
    {
        if (day.Items.IsEmpty)
        {
            return dateText;
        }
        return day.Items.Count == 1
            ? $"{dateText}, 1 assignment"
            : $"{dateText}, {day.Items.Count} assignments";
    }
}

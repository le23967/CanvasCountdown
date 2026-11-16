using System.Globalization;

namespace CanvasCountdown;

/// <summary>Which colour a chip or a dot is drawn in. The colours themselves belong to the view.</summary>
public enum CalendarTint
{
    /// <summary>Nothing is drawn.</summary>
    Clear,

    /// <summary>The secondary colour: everything on it is done or ignored.</summary>
    Secondary,

    /// <summary>The app's accent colour.</summary>
    Accent,

    /// <summary>The colour of the user's label.</summary>
    Label,
}

/// <param name="Tint">The colour of the bar and of the block behind the title.</param>
/// <param name="IsDimmed">The title is drawn in the secondary colour.</param>
/// <param name="IsStruckThrough">The title is struck through.</param>
public sealed record CalendarChipStyle(CalendarTint Tint, bool IsDimmed, bool IsStruckThrough);

/// <summary>
/// One deadline as it appears inside a grid: a tinted block with a colour bar.
/// </summary>
/// <remarks>
/// The same chip in the month, week and day grids, so a deadline looks like
/// itself wherever it is seen.
/// </remarks>
public static class CalendarChip
{
    /// <summary>
    /// The one place a deadline's colour is decided, so the grids do not each
    /// grow their own opinion of it. A label wins over the app's accent, which
    /// is the whole point of putting one on.
    /// </summary>
    /// <param name="item">The deadline.</param>
    /// <param name="hasLabel">Whether the user's label for it was found, and so has a colour to give.</param>
    public static CalendarChipStyle Style(AssignmentListItem item, bool hasLabel)
    {
        var isSettled = item.IsCompleted || item.IsIgnored;
        var tint = isSettled ? CalendarTint.Secondary : hasLabel ? CalendarTint.Label : CalendarTint.Accent;
        return new CalendarChipStyle(tint, IsDimmed: isSettled, IsStruckThrough: item.IsCompleted);
    }

    /// <summary>The time under the title in the day and week grids: "4:30 PM".</summary>
    public static string TimeText(AssignmentListItem item, AppCalendar calendar, CultureInfo culture)
    {
        var formats = CalendarDateFormats.For(culture);
        return formats.Text(calendar.ToLocal(item.DueDate), formats.Time);
    }

    /// <summary>What the chip says when pointed at: the title, the course when there is one, and when it is due.</summary>
    public static string HelpText(AssignmentListItem item, AppCalendar calendar, CultureInfo culture)
    {
        var formats = CalendarDateFormats.For(culture);
        var local = calendar.ToLocal(item.DueDate);
        var course = item.NormalizedCourseName is { } name ? $"{name}\n" : "";
        var due = $"{formats.Text(local, formats.ShortDate)} {formats.Text(local, formats.Time)}";
        return $"{item.Title}\n{course}{due}";
    }
}

namespace CanvasCountdown;

/// <param name="Count">How many columns fit side by side.</param>
/// <param name="Width">How wide each one is drawn.</param>
public sealed record CalendarGridColumns(int Count, double Width);

/// <summary>Where a timed chip sits inside its day column, measured from the column's top-left corner.</summary>
public sealed record CalendarChipPlacement(double X, double Y, double Width);

/// <summary>
/// The sizes the grids work out from the space they are given, as arithmetic
/// on that space so any width of window lays out the same way every time.
/// </summary>
/// <remarks>
/// Only the numbers that decide layout live here. How tall a row or a chip is
/// drawn, and in what colour, is the view's business; where a computation
/// needs a row height it is passed in.
/// </remarks>
public static class CalendarLayout
{
    /// <summary>
    /// A small minimum so seven columns still fit a narrow window; the cells
    /// share whatever width there is rather than pushing past the edge.
    /// </summary>
    public const double MonthColumnMinimumWidth = 34;

    public const double MonthColumnSpacing = 1;

    /// <summary>The narrowest a month may be drawn in the year view.</summary>
    public const double YearMonthMinimumWidth = 190;

    /// <summary>The widest a month may be drawn in the year view, however much room there is.</summary>
    public const double YearMonthMaximumWidth = 320;

    public const double YearMonthSpacing = 24;

    public const double YearDayMinimumWidth = 14;

    public const double YearDaySpacing = 1;

    /// <summary>
    /// Past this, chips sharing a crowded slot overlap their neighbour rather
    /// than shrink to a sliver nobody can read or click.
    /// </summary>
    public const double TimedChipMinimumWidth = 28;

    /// <summary>The space at each side of a day column's chips.</summary>
    public const double TimedChipEdgeInset = 2;

    /// <summary>The space between chips that share a slot.</summary>
    public const double TimedChipSpacing = 2;

    /// <summary>The width of each of the month grid's seven columns.</summary>
    public static double MonthColumnWidth(double availableWidth)
        => SharedWidth(availableWidth, 7, MonthColumnSpacing, MonthColumnMinimumWidth);

    /// <summary>
    /// How many months the year view puts in a row, and how wide each is:
    /// as many as fit at the minimum width, sharing the row, none wider than
    /// the maximum.
    /// </summary>
    public static CalendarGridColumns YearMonthColumns(double availableWidth)
    {
        var width = Usable(availableWidth);
        var count = Math.Max(1, (int)Math.Floor((width + YearMonthSpacing) / (YearMonthMinimumWidth + YearMonthSpacing)));
        var shared = (width - (count - 1) * YearMonthSpacing) / count;
        return new CalendarGridColumns(count, Math.Clamp(shared, YearMonthMinimumWidth, YearMonthMaximumWidth));
    }

    /// <summary>The width of each of a small month grid's seven columns.</summary>
    public static double YearDayColumnWidth(double monthWidth)
        => SharedWidth(monthWidth, 7, YearDaySpacing, YearDayMinimumWidth);

    /// <summary>
    /// Where a deadline's chip goes in a day column of the given width: its
    /// share of the width beside whatever overlaps it, at the height of its
    /// minute.
    /// </summary>
    public static CalendarChipPlacement TimedChip(TimedCalendarEntry entry, double columnWidth, double hourHeight)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var share = (Usable(columnWidth) - 2 * TimedChipEdgeInset) / entry.ColumnCount - TimedChipSpacing;
        var width = Math.Max(TimedChipMinimumWidth, share);
        return new CalendarChipPlacement(
            X: TimedChipEdgeInset + entry.Column * (width + TimedChipSpacing),
            Y: VerticalOffset(entry.MinuteOfDay, hourHeight),
            Width: width);
    }

    /// <summary>How far down the hour grid a minute of the day sits: where a chip or the now line is drawn.</summary>
    public static double VerticalOffset(int minuteOfDay, double hourHeight) => minuteOfDay / 60.0 * hourHeight;

    /// <summary>The height of the whole hour grid.</summary>
    public static double TimeGridHeight(double hourHeight) => CalendarTimeGrid.HoursShown * hourHeight;

    private static double SharedWidth(double availableWidth, int count, double spacing, double minimum)
        => Math.Max(minimum, (Usable(availableWidth) - (count - 1) * spacing) / count);

    /// <summary>A width that has not been measured yet (zero, negative, not a number) is treated as none.</summary>
    private static double Usable(double width) => double.IsFinite(width) && width > 0 ? width : 0;
}

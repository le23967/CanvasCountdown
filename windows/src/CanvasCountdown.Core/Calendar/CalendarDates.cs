namespace CanvasCountdown;

/// <summary>
/// Day arithmetic that answers null at the ends of the range .NET can
/// represent, where the macOS calendar answered nil.
/// </summary>
/// <remarks>
/// Foundation's calendar can name a day in any year, so the Swift grids only
/// had to skip a day its calendar failed to compute. .NET stops at
/// 31 December 9999 and 1 January 0001, and the date field accepts any year up
/// to 9999, so a month grid for December 9999 runs six weeks past the end of
/// <see cref="DateOnly"/>. Those squares are left out, the way the Swift left
/// out a day it could not compute, rather than taking the window down.
///
/// Only ever squares at the end. A grid is drawn by position, so a square
/// missing from the front would push every day after it under the wrong
/// weekday; a grid whose first square cannot exist is not built at all.
/// </remarks>
internal static class CalendarDates
{
    public static DateOnly? AddingDays(DateOnly date, long days)
    {
        var number = date.DayNumber + days;
        if (number < DateOnly.MinValue.DayNumber || number > DateOnly.MaxValue.DayNumber)
        {
            return null;
        }
        return DateOnly.FromDayNumber((int)number);
    }

    /// <summary>How many days into its week a date falls, counting from the calendar's first weekday.</summary>
    public static int DaysIntoWeek(DateOnly date, AppCalendar calendar)
        => ((int)date.DayOfWeek - (int)calendar.FirstDayOfWeek + 7) % 7;

    /// <summary>Honours the user's first weekday rather than assuming Sunday or Monday.</summary>
    public static DateOnly? FirstDayOfWeek(DateOnly date, AppCalendar calendar)
        => AddingDays(date, -DaysIntoWeek(date, calendar));

    /// <summary>
    /// A number that is the same for every day of one week and different for
    /// every other week, without building a date that may not exist.
    /// </summary>
    public static long WeekKey(DateOnly date, AppCalendar calendar)
        => (long)date.DayNumber - DaysIntoWeek(date, calendar);

    /// <summary>
    /// The first instant of a day. East of Greenwich, midnight on
    /// 1 January 0001 is before the first instant .NET has, so that day starts
    /// at the first instant there is — the same rule as a day whose midnight
    /// the clocks skip.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly date, AppCalendar calendar)
    {
        try
        {
            return calendar.StartOfDay(date);
        }
        catch (ArgumentOutOfRangeException) when (date == DateOnly.MinValue)
        {
            return calendar.InZone(DateTimeOffset.MinValue);
        }
    }
}

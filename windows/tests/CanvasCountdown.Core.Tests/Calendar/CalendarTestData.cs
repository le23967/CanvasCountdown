using System.Globalization;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>Fixed zones, cultures and invented coursework for the calendar tests.</summary>
internal static class CalendarTestData
{
    // Sydney moves its clocks forward at 02:00 on 4 October 2026 and back at
    // 03:00 on 5 April 2026.
    public static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    public static readonly AppCalendar SydneyMondayFirst = AppCalendar.ForTimeZone("Australia/Sydney", DayOfWeek.Monday);

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static readonly CultureInfo Australian = CultureInfo.GetCultureInfo("en-AU");

    public static AppCalendar WithFirstDay(DayOfWeek firstDay) => new(Sydney.TimeZone, firstDay);

    /// <summary>
    /// Text with the narrow no-break space newer culture data puts before "AM"
    /// read as a plain space, so a test reads the same on every version of
    /// Windows whichever its culture data writes.
    /// </summary>
    public static string Plain(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');

    public static DateTimeOffset At(int year, int month, int day, int hour = 12, int minute = 0)
        => Sydney.FromLocal(year, month, day, hour, minute);

    public static AssignmentListItem Item(
        string title,
        DateTimeOffset due,
        bool isCompleted = false,
        bool isIgnored = false,
        Guid? labelId = null,
        string? courseName = "COMP2050 Data Structures") => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            CourseName = courseName,
            DueDate = due,
            IsCompleted = isCompleted,
            IsIgnored = isIgnored,
            IsManual = false,
            LabelId = labelId,
        };
}

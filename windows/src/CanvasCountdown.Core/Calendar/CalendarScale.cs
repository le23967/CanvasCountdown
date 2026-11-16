namespace CanvasCountdown;

/// <summary>
/// How much of the calendar is on screen at once.
/// </summary>
/// <remarks>
/// The same four the system calendar offers, in the same order, because the
/// keyboard shortcuts and the muscle memory come with them. Stored by name —
/// <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, <c>"year"</c> — the raw values
/// the macOS app persists, so a setting written by either reads the same.
/// </remarks>
public enum CalendarScale
{
    Day,
    Week,
    Month,
    Year,
}

public static class CalendarScaleExtensions
{
    /// <summary>Every scale, in the order the switcher and the shortcuts list them.</summary>
    public static ValueList<CalendarScale> AllCases { get; } =
        [CalendarScale.Day, CalendarScale.Week, CalendarScale.Month, CalendarScale.Year];

    public static string Title(this CalendarScale scale) => scale switch
    {
        CalendarScale.Day => "Day",
        CalendarScale.Week => "Week",
        CalendarScale.Month => "Month",
        CalendarScale.Year => "Year",
        _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, null),
    };

    /// <summary>
    /// The key pressed with Ctrl to show this scale: 1 … 4, as the system
    /// calendar on the Mac uses them with ⌘.
    /// </summary>
    public static char Shortcut(this CalendarScale scale) => scale switch
    {
        CalendarScale.Day => '1',
        CalendarScale.Week => '2',
        CalendarScale.Month => '3',
        CalendarScale.Year => '4',
        _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, null),
    };

    /// <summary>What the back control is called: "Previous week".</summary>
    public static string PreviousTitle(this CalendarScale scale) => $"Previous {scale.Title().ToLowerInvariant()}";

    /// <summary>What the forward control is called: "Next week".</summary>
    public static string NextTitle(this CalendarScale scale) => $"Next {scale.Title().ToLowerInvariant()}";
}

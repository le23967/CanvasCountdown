namespace CanvasCountdown;

/// <summary>
/// What a due time of exactly midnight means.
/// </summary>
/// <remarks>
/// Canvas exports an assignment due "on Friday" as an all-day entry, which
/// lands at 00:00 on that Friday. Read literally that is the <em>first</em>
/// instant of the day, so the app showed "Fri, Aug 14 at 00:00" for work that
/// is really due at the end of Friday. Nearly every Canvas deadline is 23:59,
/// so the literal reading is the confusing one.
///
/// The correction only moves the clock, never the day: 00:00 on the 14th
/// becomes 23:59 on the 14th. The countdown counts whole days between the
/// starts of days, so no number in the list or the tray changes — only the
/// time shown, and when a reminder fires.
///
/// It is applied when the list is read and never written to storage, so
/// switching it off in Settings puts the original times straight back.
/// </remarks>
public static class DueTimePolicy
{
    /// <summary>One minute before midnight, matching what Canvas itself displays.</summary>
    public const int EndOfDayHour = 23;

    public const int EndOfDayMinute = 59;

    /// <summary>
    /// True when a stored date sits exactly on midnight, which is how an
    /// all-day Canvas entry arrives. A deadline genuinely set to 00:00:00.000
    /// is indistinguishable from one, and is treated the same way.
    /// </summary>
    public static bool IsMidnight(DateTimeOffset date, AppCalendar calendar) => calendar.IsMidnight(date);

    /// <summary>
    /// The due date to show and to schedule against.
    /// </summary>
    /// <remarks>
    /// Manual events are left alone. A time the user typed themselves is not a
    /// Canvas export artefact, and silently moving it would be the app
    /// overruling them.
    /// </remarks>
    public static DateTimeOffset EffectiveDueDate(
        DateTimeOffset dueDate,
        bool isManual,
        bool treatsMidnightAsEndOfDay,
        AppCalendar calendar)
    {
        if (!treatsMidnightAsEndOfDay || isManual || !IsMidnight(dueDate, calendar))
        {
            return dueDate;
        }
        return calendar.SettingTime(dueDate, EndOfDayHour, EndOfDayMinute);
    }

    /// <summary>
    /// The same correction for a date that has no stored event behind it yet,
    /// used by the import preview so what is shown before importing matches
    /// what is saved.
    /// </summary>
    public static DateTimeOffset EffectiveImportedDueDate(
        DateTimeOffset dueDate,
        bool treatsMidnightAsEndOfDay,
        AppCalendar calendar)
        => EffectiveDueDate(dueDate, isManual: false, treatsMidnightAsEndOfDay, calendar);
}

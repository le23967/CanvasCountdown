using System.Globalization;

namespace CanvasCountdown;

/// <summary>
/// Days, weeks and months in one time zone.
/// </summary>
/// <remarks>
/// The macOS app leans on Foundation's <c>Calendar</c> for every day boundary,
/// and .NET has no single type that answers "when does this day start, here".
/// Without one, each caller would reach for <c>DateTime.Date</c> or divide by
/// 24 hours, and both are wrong across daylight saving: a day can be 23 or 25
/// hours long, and a wall-clock time can be skipped or happen twice. Every day
/// boundary in the app goes through this type so those cases are decided once.
///
/// Instants are <see cref="DateTimeOffset"/>. Two values compare equal when
/// they are the same instant, whatever offset they carry, so the offset on a
/// returned value is a convenience for reading it, never part of its meaning.
/// </remarks>
public sealed class AppCalendar
{
    public AppCalendar(TimeZoneInfo timeZone, DayOfWeek firstDayOfWeek = DayOfWeek.Sunday)
    {
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        FirstDayOfWeek = firstDayOfWeek;
    }

    public TimeZoneInfo TimeZone { get; }

    public DayOfWeek FirstDayOfWeek { get; }

    /// <summary>
    /// The user's own calendar. Built fresh on every read, so a change of time
    /// zone while the app is running is honoured the next time anything asks.
    /// </summary>
    public static AppCalendar Current => new(
        TimeZoneInfo.Local,
        CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);

    public static AppCalendar Utc { get; } = new(TimeZoneInfo.Utc);

    /// <summary>A calendar for a named zone. Accepts IANA ids on every platform.</summary>
    public static AppCalendar ForTimeZone(string timeZoneId, DayOfWeek firstDayOfWeek = DayOfWeek.Sunday)
        => new(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), firstDayOfWeek);

    /// <summary>The wall-clock reading of an instant in this calendar's zone.</summary>
    public DateTime ToLocal(DateTimeOffset instant)
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime,
            DateTimeKind.Unspecified);

    /// <summary>The same instant, carrying this zone's offset.</summary>
    public DateTimeOffset InZone(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZone);

    public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant));

    public TimeOnly TimeOf(DateTimeOffset instant) => TimeOnly.FromDateTime(ToLocal(instant));

    /// <summary>
    /// The instant a wall-clock time names in this zone.
    /// </summary>
    /// <remarks>
    /// A time that the clocks skip (02:30 on a night they jump from 02:00 to
    /// 03:00) is read with the offset in force just before the jump, which
    /// lands it the length of the jump later: 03:30. That is what Foundation
    /// does, and it means the start of a day whose midnight is skipped is the
    /// first moment that day actually has. A time that happens twice is read
    /// as its first occurrence.
    /// </remarks>
    public DateTimeOffset FromLocal(DateTime wallClock)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        if (TimeZone.IsInvalidTime(local))
        {
            var probe = local;
            for (var step = 0; step < 48 && TimeZone.IsInvalidTime(probe); step++)
            {
                probe = probe.AddHours(-1);
            }
            var offsetBeforeGap = TimeZone.GetUtcOffset(probe);
            return InZone(new DateTimeOffset(local, offsetBeforeGap));
        }

        if (TimeZone.IsAmbiguousTime(local))
        {
            // The larger offset is the earlier of the two instants.
            var earliest = TimeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, earliest);
        }

        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    public DateTimeOffset FromLocal(DateOnly date, TimeOnly time) => FromLocal(date.ToDateTime(time));

    public DateTimeOffset FromLocal(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => FromLocal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified));

    /// <summary>The first instant of the day containing <paramref name="instant"/>.</summary>
    public DateTimeOffset StartOfDay(DateTimeOffset instant) => StartOfDay(DateOf(instant));

    public DateTimeOffset StartOfDay(DateOnly date) => FromLocal(date.ToDateTime(TimeOnly.MinValue));

    /// <summary>The first instant of the day after the one containing <paramref name="instant"/>.</summary>
    public DateTimeOffset StartOfNextDay(DateTimeOffset instant) => StartOfDay(DateOf(instant).AddDays(1));

    /// <summary>
    /// Whole calendar days from one date to another: zero for the same day, one
    /// for tomorrow, whatever the hours in between. Deliberately not elapsed
    /// time divided by 24 hours, which is wrong across daylight saving and near
    /// midnight.
    /// </summary>
    public int DaysBetween(DateTimeOffset from, DateTimeOffset to) => DateOf(to).DayNumber - DateOf(from).DayNumber;

    public bool IsSameDay(DateTimeOffset first, DateTimeOffset second) => DateOf(first) == DateOf(second);

    /// <summary>True when the instant reads exactly 00:00:00.0000000 on the wall clock.</summary>
    public bool IsMidnight(DateTimeOffset instant) => ToLocal(instant).TimeOfDay == TimeSpan.Zero;

    /// <summary>The same wall-clock time a number of calendar days later (or earlier).</summary>
    public DateTimeOffset AddDays(DateTimeOffset instant, int days) => FromLocal(ToLocal(instant).AddDays(days));

    /// <summary>The same wall-clock time a number of months later; the day is clamped to the month's length.</summary>
    public DateTimeOffset AddMonths(DateTimeOffset instant, int months) => FromLocal(ToLocal(instant).AddMonths(months));

    public DateTimeOffset AddYears(DateTimeOffset instant, int years) => FromLocal(ToLocal(instant).AddYears(years));

    /// <summary>The same day with the clock set to the given time.</summary>
    public DateTimeOffset SettingTime(DateTimeOffset instant, int hour, int minute, int second = 0)
        => FromLocal(DateOf(instant).ToDateTime(new TimeOnly(hour, minute, second)));

    /// <summary>The first day of the week containing <paramref name="date"/>, by this calendar's first weekday.</summary>
    public DateOnly StartOfWeek(DateOnly date)
    {
        var distance = ((int)date.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        return date.AddDays(-distance);
    }

    public static DateOnly StartOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    public static DateOnly StartOfYear(DateOnly date) => new(date.Year, 1, 1);

    /// <summary>The seven weekdays in display order, starting from <see cref="FirstDayOfWeek"/>.</summary>
    public IReadOnlyList<DayOfWeek> OrderedWeekdays()
        => Enumerable.Range(0, 7).Select(offset => (DayOfWeek)(((int)FirstDayOfWeek + offset) % 7)).ToArray();
}

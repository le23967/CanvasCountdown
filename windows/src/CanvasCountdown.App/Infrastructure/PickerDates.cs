namespace CanvasCountdown.App.Infrastructure;

/// <summary>Days in and out of a <c>CalendarDatePicker</c>.</summary>
/// <remarks>
/// The picker shows its value as a date in the PC's own time zone. Midnight
/// UTC, the obvious way to write down a bare date, is still the evening
/// before anywhere west of Greenwich, so the picker showed, and saved, the
/// day before the one meant. A day goes in as noon on that day where the PC
/// is, clear of any daylight-saving change, and comes back as the day the
/// picker was showing.
/// </remarks>
internal static class PickerDates
{
    public static DateTimeOffset ToPicker(DateOnly day)
    {
        var noon = day.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(noon, TimeZoneInfo.Local.GetUtcOffset(noon));
    }

    public static DateOnly FromPicker(DateTimeOffset value)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).DateTime);
}

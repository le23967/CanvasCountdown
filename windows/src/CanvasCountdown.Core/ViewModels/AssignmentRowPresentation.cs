using System.Globalization;

namespace CanvasCountdown;

/// <summary>How a count of days reads on screen.</summary>
public enum DayCountTone
{
    Normal,
    /// <summary>Due today: orange.</summary>
    Today,
    /// <summary>Past due: red.</summary>
    Late,
    /// <summary>Completed or ignored: grey, whatever the count.</summary>
    Muted,
}

/// <summary>
/// Dates written the way the macOS app writes them, in the reader's own
/// language and clock.
/// </summary>
public static class DateText
{
    /// <summary>"Fri, Aug 14, 11:59 PM" — the list rows.</summary>
    public static string Short(DateTimeOffset date, AppCalendar calendar, CultureInfo culture)
    {
        var local = calendar.ToLocal(date);
        var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        return $"{local.ToString("ddd", culture)}, {local.ToString(monthDay, culture)}, {local.ToString("t", culture)}";
    }

    /// <summary>"Friday, August 14, 11:59 PM" — the next-deadline card and the details.</summary>
    public static string Long(DateTimeOffset date, AppCalendar calendar, CultureInfo culture)
    {
        var local = calendar.ToLocal(date);
        return $"{local.ToString("dddd", culture)}, {local.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}, {local.ToString("t", culture)}";
    }

    /// <summary>"5 minutes ago", for when the feed was last refreshed.</summary>
    public static string Relative(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }
        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }
        var days = (int)elapsed.TotalDays;
        return days == 1 ? "yesterday" : $"{days} days ago";
    }
}

/// <summary>
/// One row of a list, worked out in full, so the window only lays out text.
/// </summary>
/// <remarks>
/// The wording follows the macOS rows: a count with "day", "days", "today" or
/// "late" under it, a minus sign rather than a hyphen for past deadlines, and
/// a screen-reader sentence that says the same thing in words.
/// </remarks>
public sealed record AssignmentRowPresentation
{
    public required AssignmentListItem Item { get; init; }

    public Guid Id => Item.Id;

    public required string DayCountText { get; init; }

    public required string DayCaption { get; init; }

    public required DayCountTone Tone { get; init; }

    public required string DueText { get; init; }

    public string? CourseText { get; init; }

    public string? LabelName { get; init; }

    public string? LabelColorHex { get; init; }

    public required string AccessibilityLabel { get; init; }

    /// <summary>"Edit…" for a manual event, "Details…" for one Canvas owns.</summary>
    public string OpenActionTitle => Item.IsManual ? "Edit…" : "Details…";

    public string CompletionActionTitle => Item.IsCompleted ? "Mark Incomplete" : "Mark Complete";

    public string IgnoreActionTitle => Item.IsIgnored ? "Stop Ignoring" : "Ignore";

    public static AssignmentRowPresentation From(
        AssignmentListItem item,
        EventLabel? label,
        DateTimeOffset now,
        AppCalendar calendar,
        CultureInfo culture)
    {
        var days = item.RemainingDays(now, calendar);
        var tone = item.IsCompleted || item.IsIgnored
            ? DayCountTone.Muted
            : days < 0 ? DayCountTone.Late : days == 0 ? DayCountTone.Today : DayCountTone.Normal;

        var timing = days switch
        {
            < 0 => $"{Math.Abs(days)} days overdue",
            0 => "due today",
            1 => "due in 1 day",
            _ => $"due in {days} days",
        };
        var course = item.NormalizedCourseName is { } name ? $", {name}" : string.Empty;
        var labelled = label is null ? string.Empty : $", labelled {label.Name}";
        var state = item.IsCompleted ? ", completed" : item.IsIgnored ? ", ignored" : string.Empty;

        return new AssignmentRowPresentation
        {
            Item = item,
            DayCountText = days >= 0 ? days.ToString(culture) : $"−{Math.Abs(days).ToString(culture)}",
            DayCaption = days switch
            {
                < 0 => "late",
                0 => "today",
                1 => "day",
                _ => "days",
            },
            Tone = tone,
            DueText = DateText.Short(item.DueDate, calendar, culture),
            CourseText = item.NormalizedCourseName,
            LabelName = label?.Name,
            LabelColorHex = label?.ColorHex,
            AccessibilityLabel = $"{item.Title}{course}{labelled}, {timing}{state}",
        };
    }
}

/// <summary>The big card above Upcoming.</summary>
public sealed record NextDeadlinePresentation
{
    public required AssignmentListItem Item { get; init; }

    public required int DaysRemaining { get; init; }

    public string DayCountText => DaysRemaining.ToString(CultureInfo.CurrentCulture);

    /// <summary>"day left" or "days left".</summary>
    public string DayLabel => DaysRemaining == 1 ? "day left" : "days left";

    public required string DueText { get; init; }

    public string AccessibilityLabel => DaysRemaining switch
    {
        0 => "Due today",
        1 => "1 day remaining",
        _ => $"{DaysRemaining} days remaining",
    };

    /// <summary>The one-line version shown above the calendar, where the full card would push the weeks off the window.</summary>
    public string CompactAccessibilityLabel => $"Next deadline, {Item.Title}, {DaysRemaining} days left";

    public static NextDeadlinePresentation From(AssignmentListItem item, DateTimeOffset now, AppCalendar calendar, CultureInfo culture) => new()
    {
        Item = item,
        DaysRemaining = item.RemainingDays(now, calendar),
        DueText = DateText.Long(item.DueDate, calendar, culture),
    };

    /// <summary>The details sheet's wording for a countdown.</summary>
    public static string CountdownDescription(int days) => days switch
    {
        0 => "Due today",
        1 => "Tomorrow",
        < 0 => Math.Abs(days) == 1 ? "1 day overdue" : $"{Math.Abs(days)} days overdue",
        _ => $"{days} days remaining",
    };
}

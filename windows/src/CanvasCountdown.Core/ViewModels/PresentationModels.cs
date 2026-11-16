using System.Text.RegularExpressions;

namespace CanvasCountdown;

public enum SidebarDestination
{
    Upcoming,
    AllEvents,
    Calendar,
    Completed,
    Settings,
}

public static class SidebarDestinationText
{
    public static string Title(this SidebarDestination destination) => destination switch
    {
        SidebarDestination.Upcoming => "Upcoming",
        SidebarDestination.AllEvents => "All Events",
        SidebarDestination.Calendar => "Calendar",
        SidebarDestination.Completed => "Completed",
        _ => "Settings",
    };
}

/// <summary>What the manual event editor is editing.</summary>
public sealed record ManualEventDraft
{
    public Guid? EventId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string CourseName { get; init; } = string.Empty;

    public required DateTimeOffset DueDate { get; init; }

    public string TrimmedTitle => Title.Trim();

    public string? TrimmedCourseName
    {
        get
        {
            var value = CourseName.Trim();
            return value.Length == 0 ? null : value;
        }
    }

    public bool CanSave => TrimmedTitle.Length > 0;

    /// <summary>A new event starts tomorrow at 17:00, which is rarely wrong by much.</summary>
    public static ManualEventDraft New(DateTimeOffset now, AppCalendar calendar)
        => new() { DueDate = calendar.SettingTime(calendar.AddDays(now, 1), 17, 0) };

    public static ManualEventDraft From(AssignmentListItem item) => new()
    {
        EventId = item.Id,
        Title = item.Title,
        CourseName = item.CourseName ?? string.Empty,
        DueDate = item.DueDate,
    };

    public ManualAssignmentDraft ToAssignmentDraft() => new()
    {
        Id = EventId,
        Title = TrimmedTitle,
        CourseName = TrimmedCourseName,
        DueDate = DueDate,
    };
}

/// <summary>A course as Settings manages it: what it is called, and how much removing it would take with it.</summary>
public sealed record ManagedCourse(string Name, int EventCount)
{
    public string EventCountDescription => EventCount == 1 ? "1 event" : $"{EventCount} events";
}

public sealed record ImportPreviewItem
{
    /// <summary>
    /// Stable across selection changes and suitable for mapping back to the
    /// parsed event. Canvas UID is preferred; a deterministic parser ID may be
    /// used when a feed omits UID.
    /// </summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? CourseName { get; init; }

    public required DateTimeOffset DueDate { get; init; }

    public string? Details { get; init; }
}

public sealed record ImportSummary(int Inserted, int Updated, int Skipped)
{
    public static ImportSummary From(ImportResult result) => new(result.InsertedCount, result.UpdatedCount, result.UnchangedCount);

    public string Message
    {
        get
        {
            var parts = new List<string> { $"{Inserted} added", $"{Updated} updated" };
            if (Skipped > 0)
            {
                parts.Add($"{Skipped} skipped");
            }
            return string.Join(", ", parts);
        }
    }
}

public enum DockLabelOption
{
    English,
    Chinese,
}

public enum DockCourseScopeOption
{
    AllAssignments,
    SelectedCourses,
}

public static class SettingsOptionText
{
    public static string Title(this DockLabelOption option) => option == DockLabelOption.Chinese ? "Chinese" : "English";

    public static string RenderedLabel(this DockLabelOption option) => option == DockLabelOption.Chinese ? "倒数日" : "DAYS";

    public static DockDisplayLanguage ModelValue(this DockLabelOption option)
        => option == DockLabelOption.Chinese ? DockDisplayLanguage.Chinese : DockDisplayLanguage.English;

    public static DockLabelOption ToOption(this DockDisplayLanguage language)
        => language == DockDisplayLanguage.Chinese ? DockLabelOption.Chinese : DockLabelOption.English;

    public static string Title(this DockCourseScopeOption option)
        => option == DockCourseScopeOption.SelectedCourses ? "Only selected courses" : "All assignments";

    public static DockCountMode ModelValue(this DockCourseScopeOption option)
        => option == DockCourseScopeOption.SelectedCourses ? DockCountMode.SelectedCourses : DockCountMode.AllAssignments;

    public static DockCourseScopeOption ToOption(this DockCountMode mode)
        => mode == DockCountMode.SelectedCourses ? DockCourseScopeOption.SelectedCourses : DockCourseScopeOption.AllAssignments;
}

/// <summary>Everything Settings edits, as one value, so a change is applied the moment it is made.</summary>
public sealed record SettingsFormState
{
    public string FeedUrl { get; init; } = string.Empty;

    public RefreshInterval RefreshInterval { get; init; } = RefreshInterval.EverySixHours;

    public ReminderSchedule ReminderSchedule { get; init; } = ReminderSchedule.Defaults;

    public DockAppearance DockAppearance { get; init; } = DockAppearance.Defaults;

    public AssistantSettings Assistant { get; init; } = AssistantSettings.Defaults;

    public DockLabelOption DockLabel { get; init; } = DockLabelOption.English;

    public DockCourseScopeOption DockCourseScope { get; init; } = DockCourseScopeOption.AllAssignments;

    public ValueSet<string> SelectedCourses { get; init; } = ValueSet<string>.Empty;

    public bool LaunchAtLogin { get; init; }

    public CalendarScale CalendarScale { get; init; } = CalendarScale.Month;

    public bool TreatsMidnightAsEndOfDay { get; init; } = true;

    public bool KeepsRunningInNotificationArea { get; init; } = true;

    public static SettingsFormState From(AppSettings settings, string? feedUrl) => new()
    {
        FeedUrl = feedUrl ?? string.Empty,
        RefreshInterval = settings.RefreshInterval,
        ReminderSchedule = settings.ReminderSchedule,
        DockAppearance = settings.DockAppearance,
        Assistant = settings.Assistant,
        DockLabel = settings.DockDisplayLanguage.ToOption(),
        DockCourseScope = settings.DockCountMode.ToOption(),
        SelectedCourses = settings.SelectedCourses,
        LaunchAtLogin = settings.LaunchAtLogin,
        CalendarScale = settings.CalendarScale,
        TreatsMidnightAsEndOfDay = settings.TreatsMidnightAsEndOfDay,
        KeepsRunningInNotificationArea = settings.KeepsRunningInNotificationArea,
    };
}

public static class FeedUrlPresentationPolicy
{
    /// <summary>
    /// Private Canvas feed URLs contain bearer-like tokens and must never be
    /// visible merely because an import sheet was opened.
    /// </summary>
    public const bool IsRevealedByDefault = false;
}

/// <summary>
/// The narrow surface the countdown is drawn on — the tray icon and taskbar
/// badge on Windows — so countdown coordination can be tested without one.
/// </summary>
public interface IDockRenderer
{
    /// <summary>Renders the current countdown. Pass null when there is no eligible event.</summary>
    void Render(int? daysRemaining, string label, AssignmentListItem? item);

    void Apply(DockAppearance appearance);
}

/// <summary>Draws nothing, for isolated launches and tests.</summary>
public sealed class InertDockRenderer : IDockRenderer
{
    public int? LastDaysRemaining { get; private set; }

    public string? LastLabel { get; private set; }

    public int RenderCount { get; private set; }

    public void Render(int? daysRemaining, string label, AssignmentListItem? item)
    {
        LastDaysRemaining = daysRemaining;
        LastLabel = label;
        RenderCount++;
    }

    public void Apply(DockAppearance appearance)
    {
    }
}

public static partial class DiagnosticRedactor
{
    /// <summary>
    /// Feed URLs contain bearer-like secrets in their paths and query values.
    /// Diagnostics do not need any URL, so every one is redacted defensively,
    /// along with non-URL values attached to common secret keys.
    /// </summary>
    public static string Redact(string value)
    {
        var result = UrlPattern().Replace(value, "[REDACTED URL]");
        return SecretKeyPattern().Replace(result, "$1$2[REDACTED]");
    }

    [GeneratedRegex(@"(?i)\b(?:https?|webcal)://[^\s""'<>]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?i)(feed[\s_-]*url|calendar[\s_-]*feed)(\s*[:=]\s*)[^,\n}\]]+")]
    private static partial Regex SecretKeyPattern();
}

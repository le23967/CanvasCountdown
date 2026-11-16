namespace CanvasCountdown;

public enum RefreshInterval
{
    Hourly = 3_600,
    EverySixHours = 21_600,
    EveryTwelveHours = 43_200,
    Daily = 86_400,
}

public enum DockDisplayLanguage
{
    Chinese,
    English,
}

public enum DockCountMode
{
    AllAssignments,
    SelectedCourses,
}

public static class SettingsEnumText
{
    public static string Title(this RefreshInterval interval) => interval switch
    {
        RefreshInterval.Hourly => "Every hour",
        RefreshInterval.EverySixHours => "Every 6 hours",
        RefreshInterval.EveryTwelveHours => "Every 12 hours",
        _ => "Daily",
    };

    public static TimeSpan Duration(this RefreshInterval interval) => TimeSpan.FromSeconds((int)interval);

    public static string Title(this DockDisplayLanguage language) => language switch
    {
        DockDisplayLanguage.Chinese => "中文（倒数日）",
        _ => "English (DAYS)",
    };

    /// <summary>The header drawn above the number.</summary>
    public static string Label(this DockDisplayLanguage language) => language switch
    {
        DockDisplayLanguage.Chinese => "倒数日",
        _ => "DAYS",
    };

    public static string Title(this DockCountMode mode) => mode switch
    {
        DockCountMode.SelectedCourses => "Selected courses",
        _ => "All assignments",
    };
}

public sealed record AppSettings
{
    public required RefreshInterval RefreshInterval { get; init; }

    public required ReminderSchedule ReminderSchedule { get; init; }

    public required DockAppearance DockAppearance { get; init; }

    public required AssistantSettings Assistant { get; init; }

    public required DockDisplayLanguage DockDisplayLanguage { get; init; }

    public required DockCountMode DockCountMode { get; init; }

    public required ValueSet<string> SelectedCourses { get; init; }

    public required bool LaunchAtLogin { get; init; }

    /// <summary>Which calendar grid to come back to.</summary>
    public required CalendarScale CalendarScale { get; init; }

    /// <summary>
    /// Whether an imported deadline that lands on midnight is read as the end
    /// of that day. On by default: almost every Canvas deadline is 23:59, and
    /// an all-day export arrives as 00:00.
    /// </summary>
    public required bool TreatsMidnightAsEndOfDay { get; init; }

    /// <summary>
    /// Whether closing the window leaves the app running in the notification
    /// area, so the countdown and the refreshes carry on. Windows only: on a
    /// Mac the Dock keeps an app alive without a window, and closing a window
    /// there never meant quitting.
    /// </summary>
    public required bool KeepsRunningInNotificationArea { get; init; }

    public static AppSettings Defaults { get; } = new()
    {
        RefreshInterval = RefreshInterval.EverySixHours,
        ReminderSchedule = ReminderSchedule.Defaults,
        DockAppearance = DockAppearance.Defaults,
        Assistant = AssistantSettings.Defaults,
        DockDisplayLanguage = DockDisplayLanguage.English,
        DockCountMode = DockCountMode.AllAssignments,
        SelectedCourses = ValueSet<string>.Empty,
        LaunchAtLogin = false,
        CalendarScale = CalendarScale.Month,
        TreatsMidnightAsEndOfDay = true,
        KeepsRunningInNotificationArea = true,
    };
}

/// <summary>
/// Stores non-sensitive preferences. The private Canvas feed URL deliberately
/// does not appear in this type and is kept in Credential Manager.
/// </summary>
/// <remarks>
/// Every property is written through as it is set, so nothing waits on a save
/// that a crash could skip. The user's own work — saved models, the
/// conversation, saved themes, labels — is kept beside the preferences but out
/// of <see cref="AppSettings"/> and out of <see cref="Reset"/>: those are
/// theirs, not preferences with a sensible default to fall back to.
/// </remarks>
public sealed class SettingsStore
{
    private readonly IPreferencesStore store;

    private RefreshInterval refreshInterval;
    private ReminderSchedule reminderSchedule;
    private DockAppearance dockAppearance;
    private AssistantSettings assistant;
    private AssistantProfileLibrary assistantProfiles;
    private AssistantConversationLog assistantConversation;
    private DockThemePresetLibrary dockThemePresets;
    private EventLabelLibrary eventLabels;
    private DockDisplayLanguage dockDisplayLanguage;
    private DockCountMode dockCountMode;
    private ValueSet<string> selectedCourses;
    private bool launchAtLogin;
    private CalendarScale calendarScale;
    private bool treatsMidnightAsEndOfDay;
    private bool keepsRunningInNotificationArea;
    private string? dismissedUpdateVersion;
    private DateTimeOffset? lastUpdateCheck;

    public SettingsStore(IPreferencesStore store)
    {
        this.store = store;
        var fallback = AppSettings.Defaults;

        refreshInterval = store.TryGet<int>(Key.RefreshInterval, out var seconds) && Enum.IsDefined(typeof(RefreshInterval), seconds)
            ? (RefreshInterval)seconds
            : fallback.RefreshInterval;
        reminderSchedule = LoadReminderSchedule(store, fallback.ReminderSchedule);
        dockAppearance = store.Get(Key.DockAppearance, fallback.DockAppearance);
        assistant = store.Get(Key.Assistant, fallback.Assistant);
        assistantProfiles = store.Get(Key.AssistantProfiles, AssistantProfileLibrary.Empty);
        assistantConversation = store.Get(Key.AssistantConversation, AssistantConversationLog.Empty);
        dockThemePresets = store.Get(Key.DockThemePresets, DockThemePresetLibrary.Empty);

        // Absent means a first run rather than an emptied list, and starting
        // with nothing to pick from would hide the feature entirely.
        var hasStoredLabels = store.TryGet<EventLabelLibrary>(Key.EventLabels, out var storedLabels);
        eventLabels = hasStoredLabels ? storedLabels! : EventLabelLibrary.Defaults;

        dockDisplayLanguage = store.Get(Key.DockDisplayLanguage, fallback.DockDisplayLanguage);
        dockCountMode = store.Get(Key.DockCountMode, fallback.DockCountMode);
        selectedCourses = store.Get(Key.SelectedCourses, ValueSet<string>.Empty);
        launchAtLogin = store.Get(Key.LaunchAtLogin, false);
        calendarScale = store.Get(Key.CalendarScale, fallback.CalendarScale);
        // Absent means a first run rather than a deliberate "off", and the
        // correction is what makes the times match Canvas.
        treatsMidnightAsEndOfDay = store.GetOptional<bool>(Key.TreatsMidnightAsEndOfDay) ?? fallback.TreatsMidnightAsEndOfDay;
        keepsRunningInNotificationArea = store.GetOptional<bool>(Key.KeepsRunningInNotificationArea) ?? fallback.KeepsRunningInNotificationArea;
        dismissedUpdateVersion = store.TryGet<string>(Key.DismissedUpdateVersion, out var dismissed) ? dismissed : null;
        lastUpdateCheck = store.GetOptional<DateTimeOffset>(Key.LastUpdateCheck);

        // Writing the starting labels out on the first run pins them: an event
        // labelled today keeps its colour even if a later version ships a
        // different starting set.
        if (!hasStoredLabels)
        {
            store.Set(Key.EventLabels, eventLabels);
        }
    }

    public RefreshInterval RefreshInterval
    {
        get => refreshInterval;
        set { refreshInterval = value; store.Set(Key.RefreshInterval, (int)value); }
    }

    public ReminderSchedule ReminderSchedule
    {
        get => reminderSchedule;
        set { reminderSchedule = value; store.Set(Key.ReminderSchedule, value); }
    }

    public DockAppearance DockAppearance
    {
        get => dockAppearance;
        set { dockAppearance = value; store.Set(Key.DockAppearance, value); }
    }

    /// <summary>The API key is never here: it lives in Credential Manager.</summary>
    public AssistantSettings Assistant
    {
        get => assistant;
        set { assistant = value; store.Set(Key.Assistant, value); }
    }

    public AssistantProfileLibrary AssistantProfiles
    {
        get => assistantProfiles;
        set { assistantProfiles = value; store.Set(Key.AssistantProfiles, value); }
    }

    public AssistantConversationLog AssistantConversation
    {
        get => assistantConversation;
        set { assistantConversation = value; store.Set(Key.AssistantConversation, value); }
    }

    public DockThemePresetLibrary DockThemePresets
    {
        get => dockThemePresets;
        set { dockThemePresets = value; store.Set(Key.DockThemePresets, value); }
    }

    public EventLabelLibrary EventLabels
    {
        get => eventLabels;
        set { eventLabels = value; store.Set(Key.EventLabels, value); }
    }

    public DockDisplayLanguage DockDisplayLanguage
    {
        get => dockDisplayLanguage;
        set { dockDisplayLanguage = value; store.Set(Key.DockDisplayLanguage, value); }
    }

    public DockCountMode DockCountMode
    {
        get => dockCountMode;
        set { dockCountMode = value; store.Set(Key.DockCountMode, value); }
    }

    public ValueSet<string> SelectedCourses
    {
        get => selectedCourses;
        set { selectedCourses = value; store.Set(Key.SelectedCourses, value); }
    }

    public bool LaunchAtLogin
    {
        get => launchAtLogin;
        set { launchAtLogin = value; store.Set(Key.LaunchAtLogin, value); }
    }

    public CalendarScale CalendarScale
    {
        get => calendarScale;
        set { calendarScale = value; store.Set(Key.CalendarScale, value); }
    }

    public bool TreatsMidnightAsEndOfDay
    {
        get => treatsMidnightAsEndOfDay;
        set { treatsMidnightAsEndOfDay = value; store.Set(Key.TreatsMidnightAsEndOfDay, value); }
    }

    public bool KeepsRunningInNotificationArea
    {
        get => keepsRunningInNotificationArea;
        set { keepsRunningInNotificationArea = value; store.Set(Key.KeepsRunningInNotificationArea, value); }
    }

    /// <summary>
    /// A version someone has said they are not interested in. Kept out of
    /// <see cref="AppSettings"/> and out of <see cref="Reset"/>: it is an
    /// answer they gave, not a preference, and asking again about the version
    /// they just waved away is the whole reason an update notice becomes
    /// something people learn to ignore.
    /// </summary>
    public string? DismissedUpdateVersion
    {
        get => dismissedUpdateVersion;
        set
        {
            dismissedUpdateVersion = value;
            if (value is null)
            {
                store.Remove(Key.DismissedUpdateVersion);
            }
            else
            {
                store.Set(Key.DismissedUpdateVersion, value);
            }
        }
    }

    /// <summary>When the last check happened, so a day's gap survives a relaunch.</summary>
    public DateTimeOffset? LastUpdateCheck
    {
        get => lastUpdateCheck;
        set
        {
            lastUpdateCheck = value;
            if (value is null)
            {
                store.Remove(Key.LastUpdateCheck);
            }
            else
            {
                store.Set(Key.LastUpdateCheck, value.Value);
            }
        }
    }

    public AppSettings Snapshot => new()
    {
        RefreshInterval = RefreshInterval,
        ReminderSchedule = ReminderSchedule,
        DockAppearance = DockAppearance,
        Assistant = Assistant,
        DockDisplayLanguage = DockDisplayLanguage,
        DockCountMode = DockCountMode,
        SelectedCourses = SelectedCourses,
        LaunchAtLogin = LaunchAtLogin,
        CalendarScale = CalendarScale,
        TreatsMidnightAsEndOfDay = TreatsMidnightAsEndOfDay,
        KeepsRunningInNotificationArea = KeepsRunningInNotificationArea,
    };

    public string DockLabel => DockDisplayLanguage.Label();

    public void Reset()
    {
        var fallback = AppSettings.Defaults;
        RefreshInterval = fallback.RefreshInterval;
        ReminderSchedule = fallback.ReminderSchedule;
        DockAppearance = fallback.DockAppearance;
        Assistant = fallback.Assistant;
        DockDisplayLanguage = fallback.DockDisplayLanguage;
        DockCountMode = fallback.DockCountMode;
        SelectedCourses = fallback.SelectedCourses;
        LaunchAtLogin = fallback.LaunchAtLogin;
        CalendarScale = fallback.CalendarScale;
        TreatsMidnightAsEndOfDay = fallback.TreatsMidnightAsEndOfDay;
        KeepsRunningInNotificationArea = fallback.KeepsRunningInNotificationArea;
    }

    private static ReminderSchedule LoadReminderSchedule(IPreferencesStore store, ReminderSchedule fallback)
    {
        if (store.TryGet<ReminderSchedule>(Key.ReminderSchedule, out var decoded))
        {
            return decoded;
        }
        // Migration path from the original whole-day offset list.
        if (store.TryGet<int[]>(Key.NotificationOffsets, out var legacy))
        {
            return ReminderSchedule.FromLegacyDayOffsets(legacy.ToValueSet());
        }
        return fallback;
    }

    internal static class Key
    {
        private const string Prefix = "CanvasCountdown.settings.";
        public const string RefreshInterval = Prefix + "refreshInterval";
        public const string NotificationOffsets = Prefix + "notificationOffsets";
        public const string ReminderSchedule = Prefix + "reminderSchedule";
        public const string DockAppearance = Prefix + "dockAppearance";
        public const string Assistant = Prefix + "assistant";
        public const string AssistantProfiles = Prefix + "assistantProfiles";
        public const string AssistantConversation = Prefix + "assistantConversation";
        public const string DockThemePresets = Prefix + "dockThemePresets";
        public const string EventLabels = Prefix + "eventLabels";
        public const string DockDisplayLanguage = Prefix + "dockDisplayLanguage";
        public const string DockCountMode = Prefix + "dockCountMode";
        public const string SelectedCourses = Prefix + "selectedCourses";
        public const string LaunchAtLogin = Prefix + "launchAtLogin";
        public const string CalendarScale = Prefix + "calendarScale";
        public const string TreatsMidnightAsEndOfDay = Prefix + "treatsMidnightAsEndOfDay";
        public const string KeepsRunningInNotificationArea = Prefix + "keepsRunningInNotificationArea";
        public const string DismissedUpdateVersion = Prefix + "dismissedUpdateVersion";
        public const string LastUpdateCheck = Prefix + "lastUpdateCheck";
    }
}

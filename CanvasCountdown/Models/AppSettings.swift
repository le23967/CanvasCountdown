import Foundation
import Observation

enum RefreshInterval: Int, CaseIterable, Codable, Identifiable, Sendable {
    case hourly = 3_600
    case everySixHours = 21_600
    case everyTwelveHours = 43_200
    case daily = 86_400

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .hourly:
            "Every hour"
        case .everySixHours:
            "Every 6 hours"
        case .everyTwelveHours:
            "Every 12 hours"
        case .daily:
            "Daily"
        }
    }

    var duration: Duration {
        .seconds(rawValue)
    }
}

enum DockDisplayLanguage: String, CaseIterable, Codable, Identifiable, Sendable {
    case chinese
    case english

    var id: String { rawValue }

    var title: String {
        switch self {
        case .chinese:
            "中文（倒数日）"
        case .english:
            "English (DAYS)"
        }
    }

    var label: String {
        switch self {
        case .chinese:
            "倒数日"
        case .english:
            "DAYS"
        }
    }
}

enum DockCountMode: String, CaseIterable, Codable, Identifiable, Sendable {
    case allAssignments
    case selectedCourses

    var id: String { rawValue }

    var title: String {
        switch self {
        case .allAssignments:
            "All assignments"
        case .selectedCourses:
            "Selected courses"
        }
    }
}

struct AppSettings: Equatable, Sendable {
    var refreshInterval: RefreshInterval
    var reminderSchedule: ReminderSchedule
    var dockAppearance: DockAppearance
    var assistant: AssistantSettings
    var dockDisplayLanguage: DockDisplayLanguage
    var dockCountMode: DockCountMode
    var selectedCourses: Set<String>
    var launchAtLogin: Bool
    /// Which calendar grid to come back to.
    var calendarScale: CalendarScale

    static let defaults = AppSettings(
        refreshInterval: .everySixHours,
        reminderSchedule: .defaults,
        dockAppearance: .defaults,
        assistant: .defaults,
        dockDisplayLanguage: .english,
        dockCountMode: .allAssignments,
        selectedCourses: [],
        launchAtLogin: false,
        calendarScale: .month
    )
}

/// Stores non-sensitive preferences. The private Canvas feed URL deliberately
/// does not appear in this type and is managed by `FeedURLStoring`.
@MainActor
@Observable
final class SettingsStore {
    var refreshInterval: RefreshInterval {
        didSet { persist() }
    }

    var reminderSchedule: ReminderSchedule {
        didSet { persist() }
    }

    var dockAppearance: DockAppearance {
        didSet { persist() }
    }

    var assistant: AssistantSettings {
        didSet { persist() }
    }

    /// Kept out of `AppSettings` and out of `reset()`: these are the user's own
    /// work, not a preference with a sensible default to fall back to.
    var dockThemePresets: DockThemePresetLibrary {
        didSet { persist() }
    }

    /// The user's own labels. Kept out of `AppSettings` and out of `reset()`
    /// alongside the Dock presets: these are their work, not a preference with
    /// a sensible default to fall back to.
    var eventLabels: EventLabelLibrary {
        didSet { persist() }
    }

    var dockDisplayLanguage: DockDisplayLanguage {
        didSet { persist() }
    }

    var dockCountMode: DockCountMode {
        didSet { persist() }
    }

    var selectedCourses: Set<String> {
        didSet { persist() }
    }

    var launchAtLogin: Bool {
        didSet { persist() }
    }

    var calendarScale: CalendarScale {
        didSet { persist() }
    }

    @ObservationIgnored
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults

        let fallback = AppSettings.defaults
        refreshInterval = defaults
            .integerValue(forKey: Key.refreshInterval)
            .flatMap(RefreshInterval.init(rawValue:))
            ?? fallback.refreshInterval
        reminderSchedule = Self.loadReminderSchedule(
            from: defaults,
            fallback: fallback.reminderSchedule
        )
        dockAppearance = Self.decode(
            DockAppearance.self,
            from: defaults,
            key: Key.dockAppearance
        ) ?? fallback.dockAppearance
        assistant = Self.decode(
            AssistantSettings.self,
            from: defaults,
            key: Key.assistant
        ) ?? fallback.assistant
        dockThemePresets = Self.decode(
            DockThemePresetLibrary.self,
            from: defaults,
            key: Key.dockThemePresets
        ) ?? .empty
        // Absent means a first run rather than an emptied list, and starting
        // with nothing to pick from would hide the feature entirely.
        let storedLabels = Self.decode(
            EventLabelLibrary.self,
            from: defaults,
            key: Key.eventLabels
        )
        eventLabels = storedLabels ?? .defaults
        dockDisplayLanguage = defaults
            .string(forKey: Key.dockDisplayLanguage)
            .flatMap(DockDisplayLanguage.init(rawValue:))
            ?? fallback.dockDisplayLanguage
        dockCountMode = defaults
            .string(forKey: Key.dockCountMode)
            .flatMap(DockCountMode.init(rawValue:))
            ?? fallback.dockCountMode
        selectedCourses = Set(
            defaults.stringArray(forKey: Key.selectedCourses) ?? []
        )
        launchAtLogin = defaults.bool(forKey: Key.launchAtLogin)
        calendarScale = defaults
            .string(forKey: Key.calendarScale)
            .flatMap(CalendarScale.init(rawValue:))
            ?? fallback.calendarScale

        // Writing the starting labels out on the first run pins them: an event
        // labelled today keeps its colour even if a later version ships a
        // different starting set.
        if storedLabels == nil {
            persist()
        }
    }

    var snapshot: AppSettings {
        AppSettings(
            refreshInterval: refreshInterval,
            reminderSchedule: reminderSchedule,
            dockAppearance: dockAppearance,
            assistant: assistant,
            dockDisplayLanguage: dockDisplayLanguage,
            dockCountMode: dockCountMode,
            selectedCourses: selectedCourses,
            launchAtLogin: launchAtLogin,
            calendarScale: calendarScale
        )
    }

    var dockLabel: String {
        dockDisplayLanguage.label
    }

    func reset() {
        let fallback = AppSettings.defaults
        refreshInterval = fallback.refreshInterval
        reminderSchedule = fallback.reminderSchedule
        dockAppearance = fallback.dockAppearance
        assistant = fallback.assistant
        dockDisplayLanguage = fallback.dockDisplayLanguage
        dockCountMode = fallback.dockCountMode
        selectedCourses = fallback.selectedCourses
        launchAtLogin = fallback.launchAtLogin
        calendarScale = fallback.calendarScale
        persist()
    }

    private static func decode<Value: Decodable>(
        _ type: Value.Type,
        from defaults: UserDefaults,
        key: String
    ) -> Value? {
        guard let data = defaults.data(forKey: key) else {
            return nil
        }
        return try? JSONDecoder().decode(type, from: data)
    }

    private static func loadReminderSchedule(
        from defaults: UserDefaults,
        fallback: ReminderSchedule
    ) -> ReminderSchedule {
        if let data = defaults.data(forKey: Key.reminderSchedule),
           let decoded = try? JSONDecoder().decode(
               ReminderSchedule.self,
               from: data
           ) {
            return decoded
        }
        // Migration from the original whole-day offset list.
        if let legacy = defaults.array(forKey: Key.notificationOffsets) as? [Int] {
            return .fromLegacyDayOffsets(Set(legacy))
        }
        return fallback
    }

    private func persist() {
        defaults.set(refreshInterval.rawValue, forKey: Key.refreshInterval)
        if let data = try? JSONEncoder().encode(reminderSchedule) {
            defaults.set(data, forKey: Key.reminderSchedule)
        }
        if let data = try? JSONEncoder().encode(dockAppearance) {
            defaults.set(data, forKey: Key.dockAppearance)
        }
        // The API key is never here: it lives in Keychain.
        if let data = try? JSONEncoder().encode(assistant) {
            defaults.set(data, forKey: Key.assistant)
        }
        if let data = try? JSONEncoder().encode(dockThemePresets) {
            defaults.set(data, forKey: Key.dockThemePresets)
        }
        if let data = try? JSONEncoder().encode(eventLabels) {
            defaults.set(data, forKey: Key.eventLabels)
        }
        defaults.set(
            dockDisplayLanguage.rawValue,
            forKey: Key.dockDisplayLanguage
        )
        defaults.set(dockCountMode.rawValue, forKey: Key.dockCountMode)
        defaults.set(selectedCourses.sorted(), forKey: Key.selectedCourses)
        defaults.set(launchAtLogin, forKey: Key.launchAtLogin)
        defaults.set(calendarScale.rawValue, forKey: Key.calendarScale)
    }

    private enum Key {
        static let prefix = "CanvasCountdown.settings."
        static let refreshInterval = prefix + "refreshInterval"
        static let notificationOffsets = prefix + "notificationOffsets"
        static let reminderSchedule = prefix + "reminderSchedule"
        static let dockAppearance = prefix + "dockAppearance"
        static let assistant = prefix + "assistant"
        static let dockThemePresets = prefix + "dockThemePresets"
        static let eventLabels = prefix + "eventLabels"
        static let dockDisplayLanguage = prefix + "dockDisplayLanguage"
        static let dockCountMode = prefix + "dockCountMode"
        static let selectedCourses = prefix + "selectedCourses"
        static let launchAtLogin = prefix + "launchAtLogin"
        static let calendarScale = prefix + "calendarScale"
    }
}

private extension UserDefaults {
    func integerValue(forKey key: String) -> Int? {
        object(forKey: key) == nil ? nil : integer(forKey: key)
    }
}

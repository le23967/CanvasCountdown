import Foundation

enum SidebarDestination: String, CaseIterable, Identifiable, Sendable {
    case upcoming
    case allEvents
    case calendar
    case completed
    case settings

    var id: Self { self }

    var title: String {
        switch self {
        case .upcoming:
            "Upcoming"
        case .allEvents:
            "All Events"
        case .calendar:
            "Calendar"
        case .completed:
            "Completed"
        case .settings:
            "Settings"
        }
    }

    var systemImage: String {
        switch self {
        case .upcoming:
            "calendar.badge.clock"
        case .allEvents:
            "list.bullet.rectangle"
        case .calendar:
            "calendar"
        case .completed:
            "checkmark.circle"
        case .settings:
            "gearshape"
        }
    }
}

struct AssignmentListItem: Identifiable, Hashable, Sendable {
    let id: UUID
    var title: String
    var courseName: String?
    var dueDate: Date
    var isCompleted: Bool
    var isIgnored: Bool
    var isManual: Bool

    func remainingDays(
        relativeTo now: Date = .now,
        calendar: Calendar = .current
    ) -> Int {
        let start = calendar.startOfDay(for: now)
        let due = calendar.startOfDay(for: dueDate)
        return calendar.dateComponents([.day], from: start, to: due).day ?? 0
    }

    var normalizedCourseName: String? {
        guard let courseName else {
            return nil
        }
        let trimmed = courseName.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}

struct ManualEventDraft: Hashable, Sendable {
    var eventID: UUID?
    var title = ""
    var courseName = ""
    var dueDate: Date

    init(
        eventID: UUID? = nil,
        title: String = "",
        courseName: String = "",
        dueDate: Date = Self.defaultDueDate
    ) {
        self.eventID = eventID
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
    }

    init(item: AssignmentListItem) {
        self.init(
            eventID: item.id,
            title: item.title,
            courseName: item.courseName ?? "",
            dueDate: item.dueDate
        )
    }

    var trimmedTitle: String {
        title.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var trimmedCourseName: String? {
        let value = courseName.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    var canSave: Bool {
        !trimmedTitle.isEmpty
    }

    private static var defaultDueDate: Date {
        let calendar = Calendar.current
        let tomorrow = calendar.date(byAdding: .day, value: 1, to: .now) ?? .now
        return calendar.date(
            bySettingHour: 17,
            minute: 0,
            second: 0,
            of: tomorrow
        ) ?? tomorrow
    }
}

struct ImportPreviewItem: Identifiable, Hashable, Sendable {
    /// Stable across selection changes and suitable for mapping back to the
    /// parsed event. Canvas UID is preferred; a deterministic parser ID may be
    /// used when a feed omits UID.
    let id: String
    let title: String
    let courseName: String?
    let dueDate: Date
    let details: String?
}

struct ImportSummary: Equatable, Sendable {
    let inserted: Int
    let updated: Int
    let skipped: Int

    var message: String {
        var parts = ["\(inserted) added", "\(updated) updated"]
        if skipped > 0 {
            parts.append("\(skipped) skipped")
        }
        return parts.joined(separator: ", ")
    }
}

enum RefreshIntervalOption: String, CaseIterable, Identifiable, Sendable {
    case hourly
    case sixHours
    case twelveHours
    case daily

    var id: Self { self }

    var title: String {
        switch self {
        case .hourly:
            "Every hour"
        case .sixHours:
            "Every 6 hours"
        case .twelveHours:
            "Every 12 hours"
        case .daily:
            "Daily"
        }
    }
}

enum DockLabelOption: String, CaseIterable, Identifiable, Sendable {
    case english
    case chinese

    var id: Self { self }

    var title: String {
        switch self {
        case .english:
            "English"
        case .chinese:
            "Chinese"
        }
    }

    var renderedLabel: String {
        switch self {
        case .english:
            "DAYS"
        case .chinese:
            "倒数日"
        }
    }
}

enum DockCourseScopeOption: String, CaseIterable, Identifiable, Sendable {
    case allAssignments
    case selectedCourses

    var id: Self { self }

    var title: String {
        switch self {
        case .allAssignments:
            "All assignments"
        case .selectedCourses:
            "Only selected courses"
        }
    }
}

struct SettingsFormState: Equatable, Sendable {
    var feedURL = ""
    var refreshInterval: RefreshIntervalOption = .sixHours
    var reminderSchedule: ReminderSchedule = .defaults
    var dockAppearance: DockAppearance = .defaults
    var assistant: AssistantSettings = .defaults
    var dockLabel: DockLabelOption = .english
    var dockCourseScope: DockCourseScopeOption = .allAssignments
    var selectedCourses: Set<String> = []
    var launchAtLogin = false
    var calendarScale: CalendarScale = .month
}

enum FeedURLPresentationPolicy {
    /// Private Canvas feed URLs contain bearer-like tokens and must never be
    /// visible merely because an import sheet was opened.
    static let isRevealedByDefault = false
}

extension AssignmentListItem: CountdownEvent {}

extension AssignmentListItem {
    init(snapshot: AssignmentSnapshot) {
        self.init(
            id: snapshot.id,
            title: snapshot.title,
            courseName: snapshot.courseName,
            dueDate: snapshot.dueDate,
            isCompleted: snapshot.isCompleted,
            isIgnored: snapshot.isIgnored,
            isManual: snapshot.source == .manual
        )
    }
}

extension ManualAssignmentDraft {
    init(presentation draft: ManualEventDraft) {
        self.init(
            id: draft.eventID,
            title: draft.trimmedTitle,
            courseName: draft.trimmedCourseName,
            dueDate: draft.dueDate
        )
    }
}

extension NotificationCandidate {
    init(snapshot: AssignmentSnapshot) {
        self.init(
            id: snapshot.id,
            title: snapshot.title,
            courseName: snapshot.courseName,
            dueDate: snapshot.dueDate,
            isCompleted: snapshot.isCompleted,
            isIgnored: snapshot.isIgnored
        )
    }
}

extension ImportSummary {
    init(_ result: ImportResult) {
        self.init(
            inserted: result.insertedCount,
            updated: result.updatedCount,
            skipped: result.unchangedCount
        )
    }
}

extension SettingsFormState {
    init(settings: AppSettings, feedURL: URL?) {
        self.init(
            feedURL: feedURL?.absoluteString ?? "",
            refreshInterval: RefreshIntervalOption(settings.refreshInterval),
            reminderSchedule: settings.reminderSchedule,
            dockAppearance: settings.dockAppearance,
            assistant: settings.assistant,
            dockLabel: DockLabelOption(settings.dockDisplayLanguage),
            dockCourseScope: DockCourseScopeOption(settings.dockCountMode),
            selectedCourses: settings.selectedCourses,
            launchAtLogin: settings.launchAtLogin,
            calendarScale: settings.calendarScale
        )
    }
}

extension RefreshIntervalOption {
    init(_ interval: RefreshInterval) {
        switch interval {
        case .hourly:
            self = .hourly
        case .everySixHours:
            self = .sixHours
        case .everyTwelveHours:
            self = .twelveHours
        case .daily:
            self = .daily
        }
    }

    var modelValue: RefreshInterval {
        switch self {
        case .hourly:
            .hourly
        case .sixHours:
            .everySixHours
        case .twelveHours:
            .everyTwelveHours
        case .daily:
            .daily
        }
    }
}

extension DockLabelOption {
    init(_ language: DockDisplayLanguage) {
        switch language {
        case .english:
            self = .english
        case .chinese:
            self = .chinese
        }
    }

    var modelValue: DockDisplayLanguage {
        switch self {
        case .english:
            .english
        case .chinese:
            .chinese
        }
    }
}

extension DockCourseScopeOption {
    init(_ mode: DockCountMode) {
        switch mode {
        case .allAssignments:
            self = .allAssignments
        case .selectedCourses:
            self = .selectedCourses
        }
    }

    var modelValue: DockCountMode {
        switch self {
        case .allAssignments:
            .allAssignments
        case .selectedCourses:
            .selectedCourses
        }
    }
}

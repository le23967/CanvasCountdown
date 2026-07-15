import Foundation

enum ReminderUnit: String, Codable, CaseIterable, Identifiable, Sendable {
    case days
    case hours

    var id: String { rawValue }

    var title: String {
        switch self {
        case .days:
            "days"
        case .hours:
            "hours"
        }
    }
}

/// One entry in the user's reminder schedule.
///
/// A rule of zero days is the due-day reminder. Everything else fires the given
/// distance before the deadline.
struct ReminderRule: Identifiable, Hashable, Codable, Sendable {
    /// A schedule longer than this is unmanageable and floods Notification Centre.
    static let maximumRuleCount = 10
    static let maximumDays = 365
    static let maximumHours = 23

    var id: UUID
    var amount: Int
    var unit: ReminderUnit
    var isEnabled: Bool

    init(
        id: UUID = UUID(),
        amount: Int,
        unit: ReminderUnit,
        isEnabled: Bool = true
    ) {
        self.id = id
        self.amount = amount
        self.unit = unit
        self.isEnabled = isEnabled
    }

    static let defaults: [ReminderRule] = [
        ReminderRule(amount: 7, unit: .days),
        ReminderRule(amount: 3, unit: .days),
        ReminderRule(amount: 1, unit: .days),
        ReminderRule(amount: 0, unit: .days),
    ]

    /// How far before the deadline this rule fires. Used as the identity for
    /// duplicate detection and for the notification request identifier, so two
    /// rules can never produce two requests for the same instant.
    var offsetMinutes: Int {
        switch unit {
        case .days:
            amount * 24 * 60
        case .hours:
            amount * 60
        }
    }

    var isDueDayReminder: Bool {
        offsetMinutes == 0
    }

    var title: String {
        if isDueDayReminder {
            return "On the due day"
        }
        switch unit {
        case .days:
            return amount == 1 ? "1 day before" : "\(amount) days before"
        case .hours:
            return amount == 1 ? "1 hour before" : "\(amount) hours before"
        }
    }

    /// Clamps a rule into the supported range. An hours rule of zero becomes the
    /// due-day reminder so the two spellings cannot both exist.
    func normalized() -> ReminderRule {
        var rule = self
        rule.amount = max(0, amount)
        switch unit {
        case .days:
            rule.amount = min(rule.amount, Self.maximumDays)
        case .hours:
            if rule.amount == 0 {
                rule.unit = .days
            } else {
                rule.amount = min(rule.amount, Self.maximumHours)
            }
        }
        return rule
    }
}

enum ReminderScheduleError: LocalizedError, Equatable, Sendable {
    case duplicateOffset
    case tooManyRules
    case invalidAmount

    var errorDescription: String? {
        switch self {
        case .duplicateOffset:
            "There is already a reminder at that time."
        case .tooManyRules:
            "You can keep up to \(ReminderRule.maximumRuleCount) reminders."
        case .invalidAmount:
            "Enter a reminder time within the supported range."
        }
    }
}

/// The user's whole reminder schedule, with the rules that keep it sane.
struct ReminderSchedule: Equatable, Codable, Sendable {
    private(set) var rules: [ReminderRule]

    init(rules: [ReminderRule] = ReminderRule.defaults) {
        self.rules = Self.deduplicated(rules.map { $0.normalized() })
    }

    static let defaults = ReminderSchedule()

    var enabledRules: [ReminderRule] {
        rules.filter(\.isEnabled)
    }

    var canAddRule: Bool {
        rules.count < ReminderRule.maximumRuleCount
    }

    func contains(offsetMinutes: Int, excluding id: UUID? = nil) -> Bool {
        rules.contains {
            $0.offsetMinutes == offsetMinutes && $0.id != id
        }
    }

    mutating func add(_ rule: ReminderRule) throws {
        let normalized = rule.normalized()
        guard normalized.amount >= 0 else {
            throw ReminderScheduleError.invalidAmount
        }
        guard canAddRule else {
            throw ReminderScheduleError.tooManyRules
        }
        guard !contains(offsetMinutes: normalized.offsetMinutes) else {
            throw ReminderScheduleError.duplicateOffset
        }
        rules.append(normalized)
        sort()
    }

    mutating func update(_ rule: ReminderRule) throws {
        let normalized = rule.normalized()
        guard let index = rules.firstIndex(where: { $0.id == normalized.id }) else {
            return
        }
        guard
            !contains(offsetMinutes: normalized.offsetMinutes, excluding: normalized.id)
        else {
            throw ReminderScheduleError.duplicateOffset
        }
        rules[index] = normalized
        sort()
    }

    mutating func remove(id: UUID) {
        rules.removeAll { $0.id == id }
    }

    mutating func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        rules.move(fromOffsets: source, toOffset: destination)
    }

    mutating func setEnabled(_ isEnabled: Bool, for id: UUID) {
        guard let index = rules.firstIndex(where: { $0.id == id }) else {
            return
        }
        rules[index].isEnabled = isEnabled
    }

    mutating func resetToDefaults() {
        rules = ReminderRule.defaults
    }

    /// Furthest-out reminder first, matching how the schedule reads on screen.
    private mutating func sort() {
        rules.sort { $0.offsetMinutes > $1.offsetMinutes }
    }

    private static func deduplicated(_ rules: [ReminderRule]) -> [ReminderRule] {
        var seen: Set<Int> = []
        var result: [ReminderRule] = []
        for rule in rules where !seen.contains(rule.offsetMinutes) {
            seen.insert(rule.offsetMinutes)
            result.append(rule)
        }
        return Array(result.prefix(ReminderRule.maximumRuleCount))
            .sorted { $0.offsetMinutes > $1.offsetMinutes }
    }

    /// Migration path from the original whole-day offset list.
    static func fromLegacyDayOffsets(_ offsets: Set<Int>) -> ReminderSchedule {
        guard !offsets.isEmpty else {
            return ReminderSchedule(rules: [])
        }
        return ReminderSchedule(
            rules: offsets.sorted(by: >).map {
                ReminderRule(amount: max(0, $0), unit: .days)
            }
        )
    }
}

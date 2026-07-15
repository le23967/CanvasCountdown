import Foundation
import UserNotifications

struct NotificationCandidate: Identifiable, Hashable, Sendable {
    let id: UUID
    let title: String
    let courseName: String?
    let dueDate: Date
    let isCompleted: Bool
    let isIgnored: Bool
}

protocol NotificationScheduling: Sendable {
    func authorizationStatus() async -> UNAuthorizationStatus
    func requestAuthorization() async throws -> Bool

    /// Replaces every pending request owned by Canvas Countdown. This makes
    /// refreshes idempotent and removes stale reminders when a due date changes.
    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) async throws

    func cancelAll() async
}

// There is deliberately no defaulted-argument extension for `reschedule`.
// A protocol extension with the same name recurses into itself whenever a
// conformer forgets to implement the requirement, turning a compile-time
// mistake into a hang. Callers pass `now` and `calendar` explicitly.

actor NotificationService: NotificationScheduling {
    private static let identifierPrefix = "canvas-countdown.assignment."

    private let center: UNUserNotificationCenter
    private var isScheduling = false
    private var schedulingWaiters: [CheckedContinuation<Void, Never>] = []

    init(center: UNUserNotificationCenter = .current()) {
        self.center = center
    }

    func authorizationStatus() async -> UNAuthorizationStatus {
        await center.notificationSettings().authorizationStatus
    }

    func requestAuthorization() async throws -> Bool {
        try await center.requestAuthorization(options: [.alert, .sound])
    }

    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) async throws {
        await acquireSchedulingLock()
        defer { releaseSchedulingLock() }

        await removePendingRequestsOwnedByApp()

        let status = await authorizationStatus()
        guard status == .authorized || status == .provisional else {
            return
        }

        let rules = schedule.enabledRules.sorted {
            $0.offsetMinutes > $1.offsetMinutes
        }

        for candidate in candidates where !candidate.isCompleted && !candidate.isIgnored {
            // Two rules can never describe the same instant, but a feed change
            // could still land two candidates on one identifier, so the set is
            // an extra guarantee that no duplicate request is submitted.
            var scheduledOffsets: Set<Int> = []

            for rule in rules {
                guard !scheduledOffsets.contains(rule.offsetMinutes) else {
                    continue
                }
                guard let fireDate = calendar.date(
                    byAdding: .minute,
                    value: -rule.offsetMinutes,
                    to: candidate.dueDate
                ), fireDate > now else {
                    continue
                }

                let content = UNMutableNotificationContent()
                content.title = notificationTitle(for: rule)
                content.body = notificationBody(candidate: candidate)
                content.sound = .default
                content.userInfo = [
                    "assignmentID": candidate.id.uuidString,
                    "reminderOffsetMinutes": rule.offsetMinutes,
                ]

                let components = calendar.dateComponents(
                    [.calendar, .timeZone, .year, .month, .day, .hour, .minute],
                    from: fireDate
                )
                let trigger = UNCalendarNotificationTrigger(
                    dateMatching: components,
                    repeats: false
                )
                let request = UNNotificationRequest(
                    identifier: Self.identifier(
                        assignmentID: candidate.id,
                        offsetMinutes: rule.offsetMinutes
                    ),
                    content: content,
                    trigger: trigger
                )
                try await center.add(request)
                scheduledOffsets.insert(rule.offsetMinutes)
            }
        }
    }

    func cancelAll() async {
        await acquireSchedulingLock()
        defer { releaseSchedulingLock() }
        await removePendingRequestsOwnedByApp()
    }

    private func acquireSchedulingLock() async {
        guard isScheduling else {
            isScheduling = true
            return
        }
        await withCheckedContinuation { continuation in
            schedulingWaiters.append(continuation)
        }
    }

    private func releaseSchedulingLock() {
        guard !schedulingWaiters.isEmpty else {
            isScheduling = false
            return
        }
        schedulingWaiters.removeFirst().resume()
    }

    private func removePendingRequestsOwnedByApp() async {
        let requests = await center.pendingNotificationRequests()
        let identifiers = requests
            .map(\.identifier)
            .filter { $0.hasPrefix(Self.identifierPrefix) }
        guard !identifiers.isEmpty else {
            return
        }
        center.removePendingNotificationRequests(withIdentifiers: identifiers)
    }

    private static func identifier(
        assignmentID: UUID,
        offsetMinutes: Int
    ) -> String {
        "\(identifierPrefix)\(assignmentID.uuidString).\(offsetMinutes)"
    }

    private func notificationTitle(for rule: ReminderRule) -> String {
        switch rule.unit {
        case .days:
            switch rule.amount {
            case 0:
                "Assignment due today"
            case 1:
                "Assignment due tomorrow"
            default:
                "Assignment due in \(rule.amount) days"
            }
        case .hours:
            rule.amount == 1
                ? "Assignment due in 1 hour"
                : "Assignment due in \(rule.amount) hours"
        }
    }

    private func notificationBody(
        candidate: NotificationCandidate
    ) -> String {
        if let courseName = candidate.courseName?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !courseName.isEmpty {
            return "\(candidate.title) · \(courseName)"
        }
        return candidate.title
    }
}

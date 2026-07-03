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
        reminderOffsets: Set<Int>,
        now: Date,
        calendar: Calendar
    ) async throws

    func cancelAll() async
}

extension NotificationScheduling {
    func reschedule(
        candidates: [NotificationCandidate],
        reminderOffsets: Set<Int>,
        now: Date = .now,
        calendar: Calendar = .current
    ) async throws {
        try await reschedule(
            candidates: candidates,
            reminderOffsets: reminderOffsets,
            now: now,
            calendar: calendar
        )
    }
}

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
        reminderOffsets: Set<Int>,
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

        let offsets = reminderOffsets
            .filter { $0 >= 0 }
            .sorted(by: >)

        for candidate in candidates where !candidate.isCompleted && !candidate.isIgnored {
            for offset in offsets {
                guard let fireDate = calendar.date(
                    byAdding: .day,
                    value: -offset,
                    to: candidate.dueDate
                ), fireDate > now else {
                    continue
                }

                let content = UNMutableNotificationContent()
                content.title = notificationTitle(offset: offset)
                content.body = notificationBody(candidate: candidate, offset: offset)
                content.sound = .default
                content.userInfo = [
                    "assignmentID": candidate.id.uuidString,
                    "reminderOffset": offset,
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
                        offset: offset
                    ),
                    content: content,
                    trigger: trigger
                )
                try await center.add(request)
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

    private static func identifier(assignmentID: UUID, offset: Int) -> String {
        "\(identifierPrefix)\(assignmentID.uuidString).\(offset)"
    }

    private func notificationTitle(offset: Int) -> String {
        switch offset {
        case 0:
            "Assignment due today"
        case 1:
            "Assignment due tomorrow"
        default:
            "Assignment due in \(offset) days"
        }
    }

    private func notificationBody(
        candidate: NotificationCandidate,
        offset: Int
    ) -> String {
        if let courseName = candidate.courseName?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !courseName.isEmpty {
            return "\(candidate.title) · \(courseName)"
        }
        return candidate.title
    }
}

import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// The reminder schedule: validation, editing, persistence, and the promise that
/// a change immediately rebuilds pending notifications without duplicates.
@MainActor
final class ReminderScheduleTests: XCTestCase {
    // MARK: - Schedule model

    func testDefaultsMatchTheDocumentedSchedule() {
        let schedule = ReminderSchedule.defaults

        XCTAssertEqual(
            schedule.rules.map(\.offsetMinutes),
            [7 * 1_440, 3 * 1_440, 1_440, 0]
        )
        XCTAssertTrue(schedule.rules.allSatisfy(\.isEnabled))
    }

    func testCustomDayAndHourOffsetsAreAccepted() throws {
        var schedule = ReminderSchedule(rules: [])

        try schedule.add(ReminderRule(amount: 14, unit: .days))
        try schedule.add(ReminderRule(amount: 6, unit: .hours))
        try schedule.add(ReminderRule(amount: 0, unit: .days))

        XCTAssertEqual(
            schedule.rules.map(\.offsetMinutes),
            [14 * 1_440, 360, 0],
            "Rules are held furthest-out first"
        )
        XCTAssertEqual(schedule.rules[1].title, "6 hours before")
        XCTAssertEqual(schedule.rules[2].title, "On the due day")
    }

    func testDuplicateOffsetsAreRejectedAcrossUnits() throws {
        var schedule = ReminderSchedule(rules: [])
        try schedule.add(ReminderRule(amount: 1, unit: .days))

        XCTAssertThrowsError(
            try schedule.add(ReminderRule(amount: 1, unit: .days))
        ) { error in
            XCTAssertEqual(error as? ReminderScheduleError, .duplicateOffset)
        }

        // Zero hours is the same instant as the due-day reminder.
        try schedule.add(ReminderRule(amount: 0, unit: .days))
        XCTAssertThrowsError(
            try schedule.add(ReminderRule(amount: 0, unit: .hours))
        ) { error in
            XCTAssertEqual(error as? ReminderScheduleError, .duplicateOffset)
        }
        XCTAssertEqual(schedule.rules.count, 2)
    }

    func testNegativeAndOversizedValuesAreClamped() {
        let negative = ReminderRule(amount: -5, unit: .days).normalized()
        let hugeDays = ReminderRule(amount: 9_999, unit: .days).normalized()
        let hugeHours = ReminderRule(amount: 400, unit: .hours).normalized()

        XCTAssertEqual(negative.amount, 0)
        XCTAssertEqual(hugeDays.amount, ReminderRule.maximumDays)
        XCTAssertEqual(hugeHours.amount, ReminderRule.maximumHours)
    }

    func testScheduleIsCappedAtTenRules() throws {
        var schedule = ReminderSchedule(rules: [])
        for day in 1...ReminderRule.maximumRuleCount {
            try schedule.add(ReminderRule(amount: day, unit: .days))
        }

        XCTAssertFalse(schedule.canAddRule)
        XCTAssertThrowsError(
            try schedule.add(ReminderRule(amount: 40, unit: .days))
        ) { error in
            XCTAssertEqual(error as? ReminderScheduleError, .tooManyRules)
        }
    }

    func testUpdateAndDeleteChangeTheSchedule() throws {
        var schedule = ReminderSchedule(rules: [])
        try schedule.add(ReminderRule(amount: 5, unit: .days))
        var rule = try XCTUnwrap(schedule.rules.first)

        rule.amount = 2
        rule.unit = .hours
        try schedule.update(rule)
        XCTAssertEqual(schedule.rules.map(\.offsetMinutes), [120])

        schedule.remove(id: rule.id)
        XCTAssertTrue(schedule.rules.isEmpty)
    }

    func testUpdateRejectsCollidingWithAnotherRule() throws {
        var schedule = ReminderSchedule(rules: [])
        try schedule.add(ReminderRule(amount: 5, unit: .days))
        try schedule.add(ReminderRule(amount: 2, unit: .days))
        var second = try XCTUnwrap(schedule.rules.last)

        second.amount = 5
        XCTAssertThrowsError(try schedule.update(second)) { error in
            XCTAssertEqual(error as? ReminderScheduleError, .duplicateOffset)
        }
    }

    func testDisabledRulesAreKeptButNotScheduled() throws {
        var schedule = ReminderSchedule.defaults
        let first = try XCTUnwrap(schedule.rules.first)

        schedule.setEnabled(false, for: first.id)

        XCTAssertEqual(schedule.rules.count, 4)
        XCTAssertEqual(schedule.enabledRules.count, 3)
    }

    func testLegacyDayOffsetsMigrate() {
        let migrated = ReminderSchedule.fromLegacyDayOffsets([3, 0, 7])

        XCTAssertEqual(migrated.rules.map(\.offsetMinutes), [7 * 1_440, 3 * 1_440, 0])
        XCTAssertTrue(migrated.rules.allSatisfy { $0.unit == .days })
    }

    func testScheduleSurvivesAStoreRoundTrip() throws {
        let suiteName = "ReminderScheduleTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let store = SettingsStore(defaults: defaults)
        var schedule = ReminderSchedule(rules: [])
        try schedule.add(ReminderRule(amount: 12, unit: .hours))
        try schedule.add(ReminderRule(amount: 21, unit: .days))
        store.reminderSchedule = schedule

        let reloaded = SettingsStore(defaults: defaults)
        XCTAssertEqual(
            reloaded.reminderSchedule.rules.map(\.offsetMinutes),
            [21 * 1_440, 720]
        )
    }

    // MARK: - View model behaviour

    func testAddingAReminderReschedulesImmediately() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        await context.notifications.resetCounts()

        try context.viewModel.addReminder(amount: 4, unit: .hours)
        try await Task.sleep(for: .seconds(1))

        let count = await context.notifications.rescheduleCount
        XCTAssertEqual(count, 1)
        let scheduled = await context.notifications.lastSchedule
        XCTAssertEqual(
            scheduled?.rules.map(\.offsetMinutes).contains(240),
            true
        )
    }

    func testDuplicateAdditionSurfacesAnErrorAndChangesNothing() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let before = context.viewModel.settingsForm.reminderSchedule

        XCTAssertThrowsError(
            try context.viewModel.addReminder(amount: 7, unit: .days)
        ) { error in
            XCTAssertEqual(error as? ReminderScheduleError, .duplicateOffset)
        }
        XCTAssertEqual(context.viewModel.settingsForm.reminderSchedule, before)
    }

    func testDeletingAReminderPersistsAndReschedules() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        await context.notifications.resetCounts()
        let rule = try XCTUnwrap(
            context.viewModel.settingsForm.reminderSchedule.rules.first
        )

        context.viewModel.removeReminder(id: rule.id)
        try await Task.sleep(for: .seconds(1))

        XCTAssertEqual(
            SettingsStore(defaults: context.defaults)
                .reminderSchedule.rules.count,
            3
        )
        let count = await context.notifications.rescheduleCount
        XCTAssertEqual(count, 1)
    }

    func testDisablingARuleStopsItBeingScheduled() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let rule = try XCTUnwrap(
            context.viewModel.settingsForm.reminderSchedule.rules.first
        )

        context.viewModel.setReminderEnabled(false, for: rule.id)
        try await Task.sleep(for: .seconds(1))

        let scheduled = await context.notifications.lastSchedule
        XCTAssertEqual(scheduled?.rules.count, 4, "The rule is kept")
        XCTAssertEqual(scheduled?.enabledRules.count, 3, "But not scheduled")
    }

    func testResetRestoresTheDefaultSchedule() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        try context.viewModel.addReminder(amount: 9, unit: .hours)

        context.viewModel.resetRemindersToDefaults()

        XCTAssertEqual(
            context.viewModel.settingsForm.reminderSchedule,
            .defaults
        )
    }

    func testMaximumRuleCountIsEnforcedThroughTheViewModel() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        for hours in 1...6 {
            try context.viewModel.addReminder(amount: hours, unit: .hours)
        }
        XCTAssertFalse(context.viewModel.canAddReminder)
        XCTAssertThrowsError(
            try context.viewModel.addReminder(amount: 12, unit: .hours)
        ) { error in
            XCTAssertEqual(error as? ReminderScheduleError, .tooManyRules)
        }
    }

    // MARK: - Permission handling

    func testDeniedPermissionSchedulesNothingAndIsNotAskedAgain() async throws {
        let context = try makeContext(authorization: .denied)
        await context.viewModel.start()

        XCTAssertEqual(context.viewModel.notificationPermission, .denied)
        XCTAssertFalse(context.viewModel.canRequestNotificationPermission)

        let granted = try await context.viewModel.requestNotificationPermission()
        XCTAssertFalse(granted)
        let requests = await context.notifications.authorizationRequestCount
        XCTAssertEqual(
            requests,
            0,
            "A denied user must never be prompted again"
        )
    }

    func testNotDeterminedPermissionCanStillBeRequested() async throws {
        let context = try makeContext(authorization: .notDetermined)
        await context.viewModel.start()

        XCTAssertTrue(context.viewModel.canRequestNotificationPermission)
        _ = try await context.viewModel.requestNotificationPermission()

        let requests = await context.notifications.authorizationRequestCount
        XCTAssertEqual(requests, 1)
    }

    // MARK: - Scheduling against real due dates

    func testDueDateChangeRebuildsRemindersFromTheNewDeadline() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        await context.notifications.resetCounts()

        let item = try XCTUnwrap(context.viewModel.assignments.first)
        let newDue = item.dueDate.addingTimeInterval(48 * 3_600)
        try await context.viewModel.saveManualEvent(
            ManualEventDraft(
                eventID: item.id,
                title: item.title,
                dueDate: newDue
            )
        )

        let candidates = await context.notifications.lastCandidates
        XCTAssertEqual(candidates?.first?.dueDate, newDue)
        let count = await context.notifications.rescheduleCount
        XCTAssertGreaterThanOrEqual(count, 1)
    }

    func testCompletedAndIgnoredCandidatesAreExcludedFromScheduling() async throws {
        let schedule = ReminderSchedule.defaults
        let now = Date()
        // Far enough out that every default rule still lies in the future; a
        // reminder whose moment has already passed is correctly skipped.
        let due = now.addingTimeInterval(10 * 86_400)
        let recorder = RecordingNotificationCenterService()

        try await recorder.reschedule(
            candidates: [
                NotificationCandidate(
                    id: UUID(),
                    title: "Active",
                    courseName: nil,
                    dueDate: due,
                    isCompleted: false,
                    isIgnored: false
                ),
                NotificationCandidate(
                    id: UUID(),
                    title: "Done",
                    courseName: nil,
                    dueDate: due,
                    isCompleted: true,
                    isIgnored: false
                ),
                NotificationCandidate(
                    id: UUID(),
                    title: "Ignored",
                    courseName: nil,
                    dueDate: due,
                    isCompleted: false,
                    isIgnored: true
                ),
            ],
            schedule: schedule,
            now: now,
            calendar: .current
        )

        let identifiers = await recorder.identifiers
        XCTAssertEqual(
            Set(identifiers).count,
            identifiers.count,
            "No duplicate notification request may be submitted"
        )
        XCTAssertEqual(
            identifiers.count,
            4,
            "Only the active assignment is scheduled, once per enabled rule"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
        let notifications: RecordingNotificationScheduler
    }

    private func makeContext(
        authorization: UNAuthorizationStatus = .authorized
    ) throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let due = calendar.date(byAdding: .day, value: 6, to: .now) ?? .now

        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Physics lab",
                    courseName: "PHYS200",
                    dueDate: due,
                    source: .manual
                ),
            ]
        )

        let suiteName = "ReminderScheduleTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let notifications = RecordingNotificationScheduler(status: authorization)
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: ReminderCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: ScopeDockSpy(),
            notificationScheduler: notifications,
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(
            viewModel: viewModel,
            defaults: defaults,
            notifications: notifications
        )
    }
}

actor RecordingNotificationScheduler: NotificationScheduling {
    private let status: UNAuthorizationStatus
    private(set) var rescheduleCount = 0
    private(set) var authorizationRequestCount = 0
    private(set) var lastSchedule: ReminderSchedule?
    private(set) var lastCandidates: [NotificationCandidate]?

    init(status: UNAuthorizationStatus) {
        self.status = status
    }

    func authorizationStatus() -> UNAuthorizationStatus { status }

    func requestAuthorization() -> Bool {
        authorizationRequestCount += 1
        return status == .authorized
    }

    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) {
        rescheduleCount += 1
        lastSchedule = schedule
        lastCandidates = candidates
    }

    func cancelAll() {}

    func resetCounts() {
        rescheduleCount = 0
        authorizationRequestCount = 0
    }
}

/// Applies the same rule-to-request expansion the production service performs,
/// so identifier uniqueness can be asserted without touching the real centre.
actor RecordingNotificationCenterService: NotificationScheduling {
    private(set) var identifiers: [String] = []

    func authorizationStatus() -> UNAuthorizationStatus { .authorized }
    func requestAuthorization() -> Bool { true }

    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) {
        identifiers.removeAll()
        for candidate in candidates
        where !candidate.isCompleted && !candidate.isIgnored {
            var used: Set<Int> = []
            for rule in schedule.enabledRules {
                guard !used.contains(rule.offsetMinutes),
                      let fire = calendar.date(
                          byAdding: .minute,
                          value: -rule.offsetMinutes,
                          to: candidate.dueDate
                      ),
                      fire > now else {
                    continue
                }
                used.insert(rule.offsetMinutes)
                identifiers.append(
                    "canvas-countdown.assignment.\(candidate.id.uuidString).\(rule.offsetMinutes)"
                )
            }
        }
    }

    func cancelAll() {
        identifiers.removeAll()
    }
}

private actor ReminderCoordinatorStub: FeedRefreshCoordinating {
    func preview(feedURL: URL, now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func preview(now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date
    ) throws -> RefreshResult {
        throw RefreshCoordinatorError.noEventsSelected
    }

    func refresh(now: Date, trigger: RefreshTrigger) throws -> RefreshResult {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func diagnosticSnapshot() -> FeedRefreshDiagnostic? { nil }
}

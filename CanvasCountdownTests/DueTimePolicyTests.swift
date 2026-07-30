import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// A Canvas deadline that arrives as midnight is read as the end of that day.
///
/// The rule the whole feature rests on: the correction moves the clock and
/// never the calendar day, so nothing anyone counts on — the days-left number,
/// the Dock tile, the order of the list — is allowed to move with it.
@MainActor
final class DueTimePolicyTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private func date(
        _ year: Int,
        _ month: Int,
        _ day: Int,
        _ hour: Int = 0,
        _ minute: Int = 0
    ) -> Date {
        calendar.date(
            from: DateComponents(
                year: year,
                month: month,
                day: day,
                hour: hour,
                minute: minute
            )
        )!
    }

    // MARK: - The rule itself

    func testMidnightBecomesTheEndOfTheSameDay() {
        let corrected = DueTimePolicy.effectiveDueDate(
            date(2026, 8, 14),
            isManual: false,
            treatsMidnightAsEndOfDay: true,
            calendar: calendar
        )

        XCTAssertEqual(corrected, date(2026, 8, 14, 23, 59))
    }

    func testTheCalendarDayNeverMoves() {
        let original = date(2026, 8, 14)
        let corrected = DueTimePolicy.effectiveDueDate(
            original,
            isManual: false,
            treatsMidnightAsEndOfDay: true,
            calendar: calendar
        )

        XCTAssertTrue(
            calendar.isDate(corrected, inSameDayAs: original),
            "Pushing a deadline into the next day would be a different bug"
        )
    }

    func testAStatedTimeIsLeftExactlyAsCanvasSentIt() {
        for (hour, minute) in [(23, 59), (17, 0), (0, 1), (9, 30)] {
            let stated = date(2026, 8, 14, hour, minute)
            XCTAssertEqual(
                DueTimePolicy.effectiveDueDate(
                    stated,
                    isManual: false,
                    treatsMidnightAsEndOfDay: true,
                    calendar: calendar
                ),
                stated,
                "\(hour):\(minute) is a real time and must not be rewritten"
            )
        }
    }

    func testAnEventTheUserTypedIsNeverAdjusted() {
        let midnight = date(2026, 8, 14)

        XCTAssertEqual(
            DueTimePolicy.effectiveDueDate(
                midnight,
                isManual: true,
                treatsMidnightAsEndOfDay: true,
                calendar: calendar
            ),
            midnight,
            "A time the user chose is not a Canvas export artefact"
        )
    }

    func testTurningItOffRestoresTheOriginalTime() {
        let midnight = date(2026, 8, 14)

        XCTAssertEqual(
            DueTimePolicy.effectiveDueDate(
                midnight,
                isManual: false,
                treatsMidnightAsEndOfDay: false,
                calendar: calendar
            ),
            midnight
        )
    }

    /// Midnight is a local idea. The same instant is midnight in one time zone
    /// and the middle of the afternoon in another, and only the local reading
    /// is the one the user sees.
    func testMidnightIsJudgedInTheUsersOwnTimeZone() {
        var london = Calendar(identifier: .gregorian)
        london.timeZone = TimeZone(identifier: "Europe/London")!

        let sydneyMidnight = date(2026, 8, 14)

        XCTAssertTrue(
            DueTimePolicy.isMidnight(sydneyMidnight, calendar: calendar)
        )
        XCTAssertFalse(
            DueTimePolicy.isMidnight(sydneyMidnight, calendar: london),
            "That instant is not midnight in London, so nothing is corrected"
        )
    }

    // MARK: - What the list and the countdown see

    func testTheListShowsTheCorrectedTimeButTheSameDaysLeft() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        let item = try XCTUnwrap(
            harness.viewModel.assignments.first { $0.title == "Video 1" }
        )
        XCTAssertEqual(item.dueDate, date(2026, 8, 14, 23, 59))

        let now = date(2026, 7, 31, 9)
        XCTAssertEqual(
            item.remainingDays(relativeTo: now, calendar: calendar),
            14,
            "The number on the row and in the Dock must not shift"
        )
    }

    func testTurningTheSettingOffPutsTheOriginalTimesBackAtOnce() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        var form = harness.viewModel.settingsForm
        form.treatsMidnightAsEndOfDay = false
        harness.viewModel.applySettings(form)
        // The reload the change kicks off is asynchronous.
        try await Task.sleep(for: .milliseconds(120))

        let item = try XCTUnwrap(
            harness.viewModel.assignments.first { $0.title == "Video 1" }
        )
        XCTAssertEqual(
            item.dueDate,
            date(2026, 8, 14),
            "Nothing Canvas sent was overwritten, so it comes straight back"
        )
    }

    func testAManualEventKeepsItsOwnMidnightInTheList() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        let item = try XCTUnwrap(
            harness.viewModel.assignments.first { $0.title == "Midnight run" }
        )
        XCTAssertEqual(item.dueDate, date(2026, 9, 1))
    }

    // MARK: - What the reminders see

    /// A reminder counts back from the deadline. If the list says 11:59 PM and
    /// the reminder counts from midnight, "1 day before" fires a whole day out.
    func testRemindersCountBackFromTheCorrectedDeadline() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        // Any ordinary change rebuilds the whole reminder schedule.
        let anyEvent = try XCTUnwrap(harness.viewModel.assignments.first)
        harness.viewModel.toggleCompleted(anyEvent)
        try await Task.sleep(for: .milliseconds(120))

        let scheduled = await harness.notifications.lastCandidates
        let candidate = try XCTUnwrap(
            scheduled.first { $0.title == "Video 1" }
        )
        XCTAssertEqual(candidate.dueDate, date(2026, 8, 14, 23, 59))

        let manual = try XCTUnwrap(
            scheduled.first { $0.title == "Midnight run" }
        )
        XCTAssertEqual(manual.dueDate, date(2026, 9, 1))
    }

    // MARK: - Harness

    private func makeHarness() throws -> Harness {
        let snapshots = [
            AssignmentSnapshot(
                externalID: "video-1",
                title: "Video 1",
                courseName: "41021 Interaction Design Studio",
                dueDate: date(2026, 8, 14),
                source: .canvasCalendarFeed
            ),
            AssignmentSnapshot(
                externalID: "reflection-1",
                title: "Reflection 1",
                courseName: "41021 Interaction Design Studio",
                dueDate: date(2026, 8, 17, 17, 0),
                source: .canvasCalendarFeed
            ),
            AssignmentSnapshot(
                title: "Midnight run",
                courseName: nil,
                dueDate: date(2026, 9, 1),
                source: .manual
            ),
        ]

        let suiteName = "DueTimePolicyTests.\(UUID().uuidString)"
        let store = UserDefaults(suiteName: suiteName)!
        store.removePersistentDomain(forName: suiteName)

        let notifications = DueTimeNotificationSpy()
        let viewModel = MainViewModel(
            repository: DueTimeRepositoryStub(snapshots: snapshots),
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: SettingsStore(defaults: store),
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: notifications,
            calendar: calendar,
            automaticActivityEnabled: false,
            courseBlocklist: IsolatedCourseBlocklistStore()
        )
        return Harness(viewModel: viewModel, notifications: notifications)
    }

    @MainActor
    struct Harness {
        let viewModel: MainViewModel
        let notifications: DueTimeNotificationSpy
    }
}

// MARK: - Doubles

actor DueTimeRepositoryStub: AssignmentRepository {
    private var snapshots: [AssignmentSnapshot]

    init(snapshots: [AssignmentSnapshot]) {
        self.snapshots = snapshots
    }

    func fetchAll() -> [AssignmentSnapshot] { snapshots }

    func upsert(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) -> ImportResult { .empty }

    func saveManual(
        _ draft: ManualAssignmentDraft,
        now: Date
    ) -> AssignmentSnapshot {
        let saved = AssignmentSnapshot(
            title: draft.title,
            courseName: draft.courseName,
            dueDate: draft.dueDate,
            source: .manual,
            createdAt: now,
            updatedAt: now
        )
        snapshots.append(saved)
        return saved
    }

    func updateStatus(
        id: UUID,
        isCompleted: Bool?,
        isIgnored: Bool?,
        now: Date
    ) {}

    func setLabel(id: UUID, labelID: UUID?, now: Date) {}

    @discardableResult
    func clearLabel(_ labelID: UUID, now: Date) -> Int { 0 }

    func delete(id: UUID) {
        snapshots.removeAll { $0.id == id }
    }

    func deleteAll() { snapshots.removeAll() }
}

actor DueTimeNotificationSpy: NotificationScheduling {
    private(set) var lastCandidates: [NotificationCandidate] = []

    func authorizationStatus() -> UNAuthorizationStatus { .authorized }
    func requestAuthorization() -> Bool { true }

    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) {
        lastCandidates = candidates
    }

    func cancelAll() {}
}

@MainActor
final class DueTimeDockStub: DockRendering {
    func render(daysRemaining: Int?, label: String) {}
}

actor DueTimeFeedURLStore: FeedURLStoring {
    func saveFeedURL(_ url: URL) {}
    func loadFeedURL() -> URL? { nil }
    func deleteFeedURL() {}
}

actor DueTimeCoordinatorStub: FeedRefreshCoordinating {
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

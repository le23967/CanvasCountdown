import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// Settings apply and persist the moment they change: there is no save step.
@MainActor
final class ImmediateSettingsTests: XCTestCase {
    func testRefreshIntervalPersistsWithoutAnExplicitSave() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.refreshInterval = .daily
        context.viewModel.applySettings(form)

        XCTAssertEqual(context.settings.refreshInterval, .daily)
        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).refreshInterval,
            .daily,
            "The change must already be on disk"
        )
    }

    func testDockLabelChangeRepaintsTheDockImmediately() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.dockLabel = .chinese
        context.viewModel.applySettings(form)

        XCTAssertEqual(context.dock.renders.last?.label, "倒数日")
        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).dockDisplayLanguage,
            .chinese
        )
    }

    func testCourseScopePersistsAndRepaintsWithoutSaving() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.dockCourseScope = .selectedCourses
        form.selectedCourses = ["PHYS200"]
        context.viewModel.applySettings(form)

        let reloaded = SettingsStore(defaults: context.defaults)
        XCTAssertEqual(reloaded.dockCountMode, .selectedCourses)
        XCTAssertEqual(reloaded.selectedCourses, ["PHYS200"])
        XCTAssertFalse(context.dock.renders.isEmpty)
    }

    func testTransientStatusIsShownAndThenCleared() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.refreshInterval = .hourly
        context.viewModel.applySettings(form)

        XCTAssertEqual(context.viewModel.statusMessage, "Settings updated")

        try await Task.sleep(for: .seconds(2.4))
        XCTAssertNil(
            context.viewModel.statusMessage,
            "A confirmation must not stay on screen permanently"
        )
    }

    func testTheFeedURLIsNeverWrittenBackFromTheSettingsForm() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.feedURL = "https://canvas.example.edu/tampered.ics"
        context.viewModel.applySettings(form)

        let stored = try await context.feedURLStore.loadFeedURL()
        XCTAssertNil(
            stored,
            "Only the preview and import flow may write the Keychain feed URL"
        )
    }

    func testNotificationChangesAreDebouncedIntoASingleReschedule() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        await context.notifications.resetCounts()

        for offsets in [Set([7, 3]), Set([7]), Set([7, 1]), Set([7, 1, 0])] {
            var form = context.viewModel.settingsForm
            form.notificationOffsets = offsets
            context.viewModel.applySettings(form)
        }

        try await Task.sleep(for: .seconds(1.2))

        let count = await context.notifications.rescheduleCount
        XCTAssertEqual(
            count,
            1,
            "Rapid edits must collapse into one reschedule, not one per keystroke"
        )
        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).notificationOffsets,
            [7, 1, 0],
            "The final selection is still persisted immediately"
        )
    }

    func testLaunchAtLoginStaysInertUnderAutomatedRuns() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.launchAtLogin = true
        context.viewModel.applySettings(form)

        XCTAssertFalse(
            SettingsStore(defaults: context.defaults).launchAtLogin,
            "An automated run never registers the real login item"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let settings: SettingsStore
        let defaults: UserDefaults
        let dock: ScopeDockSpy
        let notifications: CountingNotificationScheduler
        let feedURLStore: any FeedURLStoring
    }

    private func makeContext() throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        let start = calendar.startOfDay(for: .now)
        let due = calendar.date(byAdding: .day, value: 2, to: start) ?? start
        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Physics lab",
                    courseName: "PHYS200",
                    dueDate: due,
                    source: .canvasCalendarFeed
                ),
            ]
        )

        let suiteName = "ImmediateSettingsTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let settings = SettingsStore(defaults: defaults)
        let dock = ScopeDockSpy()
        let notifications = CountingNotificationScheduler()
        let feedURLStore = IsolatedFeedURLStore()

        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: SettingsCoordinatorStub(),
            feedURLStore: feedURLStore,
            settingsStore: settings,
            dockRenderer: dock,
            notificationScheduler: notifications,
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(
            viewModel: viewModel,
            settings: settings,
            defaults: defaults,
            dock: dock,
            notifications: notifications,
            feedURLStore: feedURLStore
        )
    }
}

actor CountingNotificationScheduler: NotificationScheduling {
    private(set) var rescheduleCount = 0
    private(set) var cancelCount = 0

    func authorizationStatus() -> UNAuthorizationStatus { .authorized }
    func requestAuthorization() -> Bool { true }

    func reschedule(
        candidates: [NotificationCandidate],
        reminderOffsets: Set<Int>,
        now: Date,
        calendar: Calendar
    ) {
        rescheduleCount += 1
    }

    func cancelAll() {
        cancelCount += 1
    }

    func resetCounts() {
        rescheduleCount = 0
        cancelCount = 0
    }
}

private actor SettingsCoordinatorStub: FeedRefreshCoordinating {
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

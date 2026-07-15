import AppKit
import Foundation
@preconcurrency import UserNotifications
import XCTest
@testable import CanvasCountdown

@MainActor
final class DockAndViewModelTests: XCTestCase {
    func testDockServiceInstallsCustomContentViewWithoutUsingBadge() {
        let dockTile = DockTileSurfaceSpy()

        let service = DockTileService(dockTile: dockTile)
        service.render(daysRemaining: 42, label: "DAYS")

        XCTAssertNotNil(dockTile.contentView)
        XCTAssertNil(dockTile.badgeLabel)
        XCTAssertEqual(dockTile.contentView?.frame.size, NSSize(width: 128, height: 128))
        XCTAssertEqual(dockTile.displayCount, 1)

        // The real application Dock tile must be untouched by the suite.
        XCTAssertNil(NSApplication.shared.dockTile.badgeLabel)
    }

    func testNextCountdownTransitionUsesExactDueTimeBeforeMidnight() throws {
        let calendar = testCalendar
        let now = try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: 2026,
                    month: 7,
                    day: 28,
                    hour: 10
                )
            )
        )
        let dueDate = try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: 2026,
                    month: 7,
                    day: 28,
                    hour: 14,
                    minute: 30
                )
            )
        )
        let event = snapshot(title: "Due today", dueDate: dueDate)

        XCTAssertEqual(
            CountdownCalculator.nextTransitionDate(
                from: [event],
                now: now,
                calendar: calendar
            ),
            dueDate
        )
    }

    func testNextMidnightTransitionAccountsForSpringDST() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(
            TimeZone(identifier: "America/New_York")
        )
        let now = try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: 2026,
                    month: 3,
                    day: 8,
                    hour: 0
                )
            )
        )
        let transition = try XCTUnwrap(
            CountdownCalculator.nextTransitionDate(
                from: [AssignmentSnapshot](),
                now: now,
                calendar: calendar
            )
        )

        XCTAssertEqual(transition.timeIntervalSince(now), 23 * 60 * 60, accuracy: 0.1)
        let components = calendar.dateComponents(
            [.year, .month, .day, .hour],
            from: transition
        )
        XCTAssertEqual(components.year, 2026)
        XCTAssertEqual(components.month, 3)
        XCTAssertEqual(components.day, 9)
        XCTAssertEqual(components.hour, 0)
    }

    func testStartRendersNearestEligibleAssignmentUsingCalendarDays() async throws {
        let calendar = testCalendar
        let nearest = snapshot(
            title: "Nearest",
            dueDate: try date(daysFromToday: 2, calendar: calendar)
        )
        let repository = RepositoryStub(
            snapshots: [
                snapshot(
                    title: "Overdue",
                    dueDate: try date(daysFromToday: -1, calendar: calendar)
                ),
                snapshot(
                    title: "Completed",
                    dueDate: try date(daysFromToday: 1, calendar: calendar),
                    isCompleted: true
                ),
                snapshot(
                    title: "Ignored",
                    dueDate: try date(daysFromToday: 1, calendar: calendar),
                    isIgnored: true
                ),
                nearest,
                snapshot(
                    title: "Later",
                    dueDate: try date(daysFromToday: 5, calendar: calendar)
                ),
            ]
        )
        let harness = try makeHarness(repository: repository, calendar: calendar)
        harness.settings.dockDisplayLanguage = .chinese

        await harness.viewModel.start()

        XCTAssertEqual(
            harness.dock.renders.last,
            DockRendererSpy.Render(daysRemaining: 2, label: "倒数日")
        )
    }

    func testImportAndManualRefreshRerenderDock() async throws {
        let calendar = testCalendar
        let repository = RepositoryStub()
        let importedEvent = ParsedCalendarEvent(
            uid: "imported",
            summary: "Imported",
            startDate: try date(daysFromToday: 4, calendar: calendar),
            endDate: nil,
            description: nil,
            url: nil,
            lastModified: nil,
            isAllDay: false,
            timeZoneIdentifier: calendar.timeZone.identifier
        )
        let coordinator = RefreshCoordinatorStub(
            repository: repository,
            previewEvents: [importedEvent]
        )
        let harness = try makeHarness(
            repository: repository,
            coordinator: coordinator,
            calendar: calendar
        )
        await harness.viewModel.start()

        let preview = try await harness.viewModel.previewFeed(
            try XCTUnwrap(URL(string: "https://canvas.example.edu/feed.ics"))
        )
        _ = try await harness.viewModel.importFeed(
            try XCTUnwrap(URL(string: "https://canvas.example.edu/feed.ics")),
            selectedIDs: Set(preview.map(\.id))
        )
        XCTAssertEqual(harness.dock.renders.last?.daysRemaining, 4)

        await coordinator.setRefreshSnapshots([
            snapshot(
                title: "Changed deadline",
                dueDate: try date(daysFromToday: 1, calendar: calendar),
                source: .canvasCalendarFeed
            ),
        ])
        harness.viewModel.refreshManually()

        try await waitUntil {
            harness.dock.renders.last?.daysRemaining == 1
                && harness.viewModel.lastRefreshDate != nil
        }
    }

    func testCompletionIgnoreAndDeletionRerenderDock() async throws {
        let calendar = testCalendar
        let first = snapshot(
            title: "First",
            dueDate: try date(daysFromToday: 1, calendar: calendar)
        )
        let second = snapshot(
            title: "Second",
            dueDate: try date(daysFromToday: 3, calendar: calendar)
        )
        let repository = RepositoryStub(snapshots: [first, second])
        let harness = try makeHarness(repository: repository, calendar: calendar)
        await harness.viewModel.start()
        XCTAssertEqual(harness.dock.renders.last?.daysRemaining, 1)

        harness.viewModel.toggleCompleted(try item(id: first.id, in: harness.viewModel))
        try await waitUntil {
            harness.dock.renders.last?.daysRemaining == 3
                && harness.viewModel.assignments.first(where: { $0.id == first.id })?
                    .isCompleted == true
        }

        harness.viewModel.toggleIgnored(try item(id: second.id, in: harness.viewModel))
        try await waitUntil {
            harness.dock.renders.last?.daysRemaining == nil
                && harness.viewModel.assignments.first(where: { $0.id == second.id })?
                    .isIgnored == true
        }

        harness.viewModel.toggleIgnored(try item(id: second.id, in: harness.viewModel))
        try await waitUntil {
            harness.dock.renders.last?.daysRemaining == 3
                && harness.viewModel.assignments.first(where: { $0.id == second.id })?
                    .isIgnored == false
        }

        harness.viewModel.deleteManualEvent(
            try item(id: second.id, in: harness.viewModel)
        )
        try await waitUntil {
            harness.dock.renders.last?.daysRemaining == nil
                && !harness.viewModel.assignments.contains(where: { $0.id == second.id })
        }
    }

    func testManualCreationAndDateEditRerenderDock() async throws {
        let calendar = testCalendar
        let repository = RepositoryStub()
        let harness = try makeHarness(repository: repository, calendar: calendar)
        await harness.viewModel.start()
        XCTAssertNil(harness.dock.renders.last?.daysRemaining)

        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(
                title: "Manual",
                dueDate: try date(daysFromToday: 5, calendar: calendar)
            )
        )
        XCTAssertEqual(harness.dock.renders.last?.daysRemaining, 5)

        let saved = try XCTUnwrap(harness.viewModel.assignments.first)
        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(
                eventID: saved.id,
                title: saved.title,
                dueDate: try date(daysFromToday: 1, calendar: calendar)
            )
        )
        XCTAssertEqual(harness.dock.renders.last?.daysRemaining, 1)
    }

    private var testCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_AU")
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")
            ?? TimeZone(secondsFromGMT: 10 * 60 * 60)
            ?? .current
        return calendar
    }

    private func date(daysFromToday days: Int, calendar: Calendar) throws -> Date {
        let start = calendar.startOfDay(for: .now)
        let day = try XCTUnwrap(
            calendar.date(byAdding: .day, value: days, to: start)
        )
        return try XCTUnwrap(
            calendar.date(bySettingHour: 12, minute: 0, second: 0, of: day)
        )
    }

    private func snapshot(
        title: String,
        dueDate: Date,
        source: AssignmentSource = .manual,
        isCompleted: Bool = false,
        isIgnored: Bool = false
    ) -> AssignmentSnapshot {
        AssignmentSnapshot(
            title: title,
            dueDate: dueDate,
            source: source,
            isCompleted: isCompleted,
            isIgnored: isIgnored
        )
    }

    private func item(
        id: UUID,
        in viewModel: MainViewModel
    ) throws -> AssignmentListItem {
        try XCTUnwrap(viewModel.assignments.first(where: { $0.id == id }))
    }

    private func makeHarness(
        repository: RepositoryStub,
        coordinator: RefreshCoordinatorStub? = nil,
        calendar: Calendar
    ) throws -> Harness {
        let defaultsName = "DockAndViewModelTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: defaultsName))
        defaults.removePersistentDomain(forName: defaultsName)

        let settings = SettingsStore(defaults: defaults)
        let dock = DockRendererSpy()
        let feedURLStore = FeedURLStoreStub()
        let refreshCoordinator = coordinator
            ?? RefreshCoordinatorStub(repository: repository)
        let notifications = NotificationSchedulerStub()
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: refreshCoordinator,
            feedURLStore: feedURLStore,
            settingsStore: settings,
            dockRenderer: dock,
            notificationScheduler: notifications,
            calendar: calendar
        )
        return Harness(
            viewModel: viewModel,
            settings: settings,
            dock: dock
        )
    }

    private func waitUntil(
        _ condition: @MainActor () -> Bool
    ) async throws {
        for _ in 0..<200 {
            if condition() {
                return
            }
            try await Task.sleep(for: .milliseconds(5))
        }
        XCTFail("Timed out waiting for the view model mutation to synchronize the Dock")
    }
}

@MainActor
private struct Harness {
    let viewModel: MainViewModel
    let settings: SettingsStore
    let dock: DockRendererSpy
}

@MainActor
private final class DockTileSurfaceSpy: DockTileSurface {
    var contentView: NSView?
    var badgeLabel: String?
    private(set) var displayCount = 0

    func display() {
        displayCount += 1
    }
}

@MainActor
private final class DockRendererSpy: DockRendering {
    struct Render: Equatable {
        let daysRemaining: Int?
        let label: String
    }

    private(set) var renders: [Render] = []

    func render(daysRemaining: Int?, label: String) {
        renders.append(Render(daysRemaining: daysRemaining, label: label))
    }
}

private actor RepositoryStub: AssignmentRepository {
    private var snapshots: [AssignmentSnapshot]

    init(snapshots: [AssignmentSnapshot] = []) {
        self.snapshots = snapshots
    }

    func fetchAll() -> [AssignmentSnapshot] {
        snapshots
    }

    func upsert(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) -> ImportResult {
        replaceWithImportRecords(records, importedAt: importedAt)
        return ImportResult(
            insertedCount: records.count,
            updatedCount: 0,
            unchangedCount: 0
        )
    }

    func saveManual(
        _ draft: ManualAssignmentDraft,
        now: Date
    ) throws -> AssignmentSnapshot {
        guard !draft.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw AssignmentRepositoryError.emptyTitle
        }

        if let id = draft.id {
            guard let index = snapshots.firstIndex(where: { $0.id == id }) else {
                throw AssignmentRepositoryError.assignmentNotFound
            }
            guard snapshots[index].source == .manual else {
                throw AssignmentRepositoryError.cannotEditImportedAssignmentAsManual
            }
            snapshots[index].title = draft.title
            snapshots[index].courseName = draft.courseName
            snapshots[index].dueDate = draft.dueDate
            snapshots[index].updatedAt = now
            return snapshots[index]
        }

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
    ) throws {
        guard let index = snapshots.firstIndex(where: { $0.id == id }) else {
            throw AssignmentRepositoryError.assignmentNotFound
        }
        if let isCompleted {
            snapshots[index].isCompleted = isCompleted
        }
        if let isIgnored {
            snapshots[index].isIgnored = isIgnored
        }
        snapshots[index].updatedAt = now
    }

    func delete(id: UUID) throws {
        guard let index = snapshots.firstIndex(where: { $0.id == id }) else {
            throw AssignmentRepositoryError.assignmentNotFound
        }
        snapshots.remove(at: index)
    }

    func deleteAll() {
        snapshots.removeAll()
    }

    func replace(with newSnapshots: [AssignmentSnapshot]) {
        snapshots = newSnapshots
    }

    private func replaceWithImportRecords(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) {
        snapshots = records.map { record in
            AssignmentSnapshot(
                externalID: record.externalID,
                title: record.title,
                courseName: record.courseName,
                dueDate: record.dueDate,
                source: record.source,
                sourceURL: record.sourceURL,
                createdAt: importedAt,
                updatedAt: importedAt
            )
        }
    }
}

private actor FeedURLStoreStub: FeedURLStoring {
    private var storedURL: URL?

    func saveFeedURL(_ url: URL) {
        storedURL = url
    }

    func loadFeedURL() -> URL? {
        storedURL
    }

    func deleteFeedURL() {
        storedURL = nil
    }
}

private actor RefreshCoordinatorStub: FeedRefreshCoordinating {
    private let repository: RepositoryStub
    private var previewEvents: [ParsedCalendarEvent]
    private var refreshSnapshots: [AssignmentSnapshot] = []

    init(
        repository: RepositoryStub,
        previewEvents: [ParsedCalendarEvent] = []
    ) {
        self.repository = repository
        self.previewEvents = previewEvents
    }

    func preview(feedURL: URL, now: Date) -> FeedPreview {
        FeedPreview(
            events: previewEvents,
            fetchedAt: now,
            receivedByteCount: 1,
            excludedEventCount: 0,
            activeExternalIDs: Set(previewEvents.compactMap(\.uid))
        )
    }

    func preview(now: Date) -> FeedPreview {
        FeedPreview(
            events: previewEvents,
            fetchedAt: now,
            receivedByteCount: 1,
            excludedEventCount: 0,
            activeExternalIDs: Set(previewEvents.compactMap(\.uid))
        )
    }

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date
    ) async -> RefreshResult {
        let records = events.map { event in
            AssignmentImportRecord(
                externalID: event.uid,
                title: event.summary,
                dueDate: event.startDate,
                source: .canvasCalendarFeed,
                sourceURL: event.url
            )
        }
        let result = await repository.upsert(records, importedAt: date)
        return RefreshResult(
            importResult: result,
            importedEventCount: events.count,
            refreshedAt: date
        )
    }

    func refresh(now: Date, trigger: RefreshTrigger) async -> RefreshResult {
        await repository.replace(with: refreshSnapshots)
        return RefreshResult(
            importResult: ImportResult(
                insertedCount: 0,
                updatedCount: refreshSnapshots.count,
                unchangedCount: 0
            ),
            importedEventCount: refreshSnapshots.count,
            refreshedAt: now
        )
    }

    func diagnosticSnapshot() -> FeedRefreshDiagnostic? {
        nil
    }

    func setRefreshSnapshots(_ snapshots: [AssignmentSnapshot]) {
        refreshSnapshots = snapshots
    }
}

private actor NotificationSchedulerStub: NotificationScheduling {
    func authorizationStatus() -> UNAuthorizationStatus {
        .denied
    }

    func requestAuthorization() -> Bool {
        false
    }

    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) {}

    func cancelAll() {}
}

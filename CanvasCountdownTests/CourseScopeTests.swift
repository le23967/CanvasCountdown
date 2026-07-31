import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// The selected-course preference focuses Upcoming, its sidebar count, the
/// nearest-assignment card and the Dock countdown together, while All Events
/// and Completed stay complete.
@MainActor
final class CourseScopeTests: XCTestCase {
    func testAllAssignmentsScopeShowsEveryCourse() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        harness.viewModel.sidebarSelection = .upcoming

        XCTAssertEqual(
            titles(harness.viewModel.filteredAssignments),
            ["Maths sheet", "Physics lab", "Personal errand"]
        )
        XCTAssertEqual(harness.viewModel.upcomingCount, 3)
        XCTAssertEqual(harness.viewModel.nearestAssignment?.title, "Maths sheet")
    }

    func testSelectedCoursesScopeFiltersUpcomingAndItsCount() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.selectCourses(["PHYS200"])

        harness.viewModel.sidebarSelection = .upcoming

        XCTAssertEqual(
            titles(harness.viewModel.filteredAssignments),
            ["Physics lab", "Personal errand"],
            "Only the enabled course, plus entries with no course"
        )
        XCTAssertEqual(harness.viewModel.upcomingCount, 2)
        XCTAssertEqual(harness.viewModel.nearestAssignment?.title, "Physics lab")
    }

    func testAssignmentsWithoutACourseAreAlwaysIncluded() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.selectCourses([])

        harness.viewModel.sidebarSelection = .upcoming

        XCTAssertEqual(
            titles(harness.viewModel.filteredAssignments),
            ["Personal errand"],
            "A manual event with no course must never silently disappear"
        )
        XCTAssertEqual(harness.viewModel.upcomingCount, 1)
    }

    func testAllEventsRemainsACompleteLibrary() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.selectCourses(["PHYS200"])

        harness.viewModel.sidebarSelection = .allEvents

        XCTAssertEqual(
            titles(harness.viewModel.filteredAssignments),
            ["Maths sheet", "Physics lab", "Personal errand"]
        )
    }

    func testCompletedRemainsACompleteHistory() async throws {
        let harness = try makeHarness(includingCompleted: true)
        await harness.viewModel.start()
        harness.selectCourses(["PHYS200"])

        harness.viewModel.sidebarSelection = .completed

        XCTAssertEqual(
            titles(harness.viewModel.filteredAssignments),
            ["Old maths quiz"],
            "A completed assignment from a deselected course stays visible"
        )
    }

    func testSearchReachesPastTheCourseScopeButTheListDoesNot() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.selectCourses(["PHYS200"])
        harness.viewModel.sidebarSelection = .upcoming

        harness.viewModel.presentSearch()
        harness.viewModel.searchText = "Maths"
        XCTAssertEqual(
            titles(harness.viewModel.searchResults),
            ["Maths sheet"],
            "Search deliberately reaches past the course scope: hiding a result because of a filter set elsewhere would look like the event no longer exists"
        )

        harness.viewModel.searchText = "PHYS"
        XCTAssertEqual(
            titles(harness.viewModel.searchResults),
            ["Physics lab"],
            "Course names are searchable"
        )

        harness.viewModel.dismissSearch()
        XCTAssertEqual(
            titles(harness.viewModel.filteredAssignments),
            ["Physics lab", "Personal errand"],
            "Closing the panel leaves the scoped list exactly as it was"
        )
    }

    func testChangingTheScopeUpdatesTheDockImmediately() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        XCTAssertEqual(harness.dock.renders.last?.daysRemaining, 1)

        var form = harness.viewModel.settingsForm
        form.dockCourseScope = .selectedCourses
        form.selectedCourses = ["PHYS200"]
        harness.viewModel.applySettings(form)

        XCTAssertEqual(
            harness.dock.renders.last?.daysRemaining,
            3,
            "The Dock follows the same scope as Upcoming"
        )
        XCTAssertEqual(harness.viewModel.nearestAssignment?.title, "Physics lab")
        XCTAssertEqual(harness.viewModel.upcomingCount, 2)
    }

    func testScopeSelectionIsPersisted() async throws {
        let defaultsName = "CourseScopeTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: defaultsName))
        defer { defaults.removePersistentDomain(forName: defaultsName) }

        let harness = try makeHarness(defaults: defaults)
        await harness.viewModel.start()

        var form = harness.viewModel.settingsForm
        form.dockCourseScope = .selectedCourses
        form.selectedCourses = ["PHYS200"]
        harness.viewModel.applySettings(form)

        let reloaded = SettingsStore(defaults: defaults)
        XCTAssertEqual(reloaded.dockCountMode, .selectedCourses)
        XCTAssertEqual(reloaded.selectedCourses, ["PHYS200"])
    }

    func testScopeNeverRemovesStoredAssignments() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        harness.selectCourses(["PHYS200"])

        let stored = try await harness.repository.fetchAll()
        XCTAssertEqual(stored.count, 3, "Filtering is a view concern only")
        XCTAssertEqual(harness.viewModel.assignments.count, 3)
    }

    // MARK: - Helpers

    private func titles(_ items: [AssignmentListItem]) -> [String] {
        items.map(\.title)
    }

    private func makeHarness(
        includingCompleted: Bool = false,
        defaults: UserDefaults? = nil
    ) throws -> Harness {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = calendar.startOfDay(for: .now)
            let day = calendar.date(byAdding: .day, value: days, to: start) ?? start
            return calendar.date(bySettingHour: 12, minute: 0, second: 0, of: day)
                ?? day
        }

        var snapshots = [
            AssignmentSnapshot(
                title: "Maths sheet",
                courseName: "MATH100",
                dueDate: due(inDays: 1),
                source: .canvasCalendarFeed
            ),
            AssignmentSnapshot(
                title: "Physics lab",
                courseName: "PHYS200",
                dueDate: due(inDays: 3),
                source: .canvasCalendarFeed
            ),
            AssignmentSnapshot(
                title: "Personal errand",
                courseName: nil,
                dueDate: due(inDays: 5),
                source: .manual
            ),
        ]
        if includingCompleted {
            snapshots.append(
                AssignmentSnapshot(
                    title: "Old maths quiz",
                    courseName: "MATH100",
                    dueDate: due(inDays: -4),
                    source: .canvasCalendarFeed,
                    isCompleted: true
                )
            )
        }

        let suiteName = "CourseScopeTests.\(UUID().uuidString)"
        let store = defaults ?? UserDefaults(suiteName: suiteName)!
        if defaults == nil {
            store.removePersistentDomain(forName: suiteName)
        }

        let repository = ScopeRepositoryStub(snapshots: snapshots)
        let settings = SettingsStore(defaults: store)
        let dock = ScopeDockSpy()
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: ScopeCoordinatorStub(),
            feedURLStore: ScopeFeedURLStore(),
            settingsStore: settings,
            dockRenderer: dock,
            notificationScheduler: ScopeNotificationStub(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Harness(
            viewModel: viewModel,
            settings: settings,
            dock: dock,
            repository: repository
        )
    }

    @MainActor
    struct Harness {
        let viewModel: MainViewModel
        let settings: SettingsStore
        let dock: ScopeDockSpy
        let repository: ScopeRepositoryStub

        func selectCourses(_ courses: Set<String>) {
            settings.dockCountMode = .selectedCourses
            settings.selectedCourses = courses
        }
    }
}

@MainActor
final class ScopeDockSpy: DockRendering {
    struct Render: Equatable {
        let daysRemaining: Int?
        let label: String
    }

    private(set) var renders: [Render] = []

    func render(daysRemaining: Int?, label: String) {
        renders.append(Render(daysRemaining: daysRemaining, label: label))
    }
}

actor ScopeRepositoryStub: AssignmentRepository {
    private var snapshots: [AssignmentSnapshot]

    init(snapshots: [AssignmentSnapshot]) {
        self.snapshots = snapshots
    }

    func fetchAll() -> [AssignmentSnapshot] {
        snapshots
    }

    /// Behaves like the real repository: matches on external identifier, then
    /// inserts or updates, leaving local completed and ignored state alone.
    func upsert(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) -> ImportResult {
        var result = ImportResult.empty
        for record in records {
            let existingIndex = snapshots.firstIndex { stored in
                guard let key = record.externalID, let storedKey = stored.externalID else {
                    return false
                }
                return key == storedKey && stored.source == record.source
            }

            if let index = existingIndex {
                let unchanged = snapshots[index].title == record.title
                    && snapshots[index].dueDate == record.dueDate
                    && snapshots[index].courseName == record.courseName
                snapshots[index].title = record.title
                snapshots[index].courseName = record.courseName
                snapshots[index].dueDate = record.dueDate
                snapshots[index].updatedAt = importedAt
                if unchanged {
                    result.unchangedCount += 1
                } else {
                    result.updatedCount += 1
                }
                continue
            }

            snapshots.append(
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
            )
            result.insertedCount += 1
        }
        return result
    }

    func saveManual(
        _ draft: ManualAssignmentDraft,
        now: Date
    ) throws -> AssignmentSnapshot {
        if let id = draft.id {
            guard let index = snapshots.firstIndex(where: { $0.id == id }) else {
                throw AssignmentRepositoryError.assignmentNotFound
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
    }

    func setLabel(id: UUID, labelID: UUID?, now: Date) throws {
        guard let index = snapshots.firstIndex(where: { $0.id == id }) else {
            throw AssignmentRepositoryError.assignmentNotFound
        }
        snapshots[index].labelID = labelID
    }

    @discardableResult
    func clearLabel(_ labelID: UUID, now: Date) throws -> Int {
        var cleared = 0
        for index in snapshots.indices where snapshots[index].labelID == labelID {
            snapshots[index].labelID = nil
            cleared += 1
        }
        return cleared
    }

    func delete(id: UUID) throws {}

    func deleteAll() {
        snapshots.removeAll()
    }
}

private actor ScopeFeedURLStore: FeedURLStoring {
    func saveFeedURL(_ url: URL) {}
    func loadFeedURL() -> URL? { nil }
    func deleteFeedURL() {}
}

private actor ScopeNotificationStub: NotificationScheduling {
    func authorizationStatus() -> UNAuthorizationStatus { .denied }
    func requestAuthorization() -> Bool { false }
    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) {}
    func cancelAll() {}
}

private actor ScopeCoordinatorStub: FeedRefreshCoordinating {
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

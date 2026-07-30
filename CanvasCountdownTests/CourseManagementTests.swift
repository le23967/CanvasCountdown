import Foundation
import SwiftData
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// Removing a course that the Canvas feed keeps bringing back.
///
/// Deleting the events alone would not hold: the next refresh imports them
/// again. The test that matters is the second one — that a refresh after a
/// removal stays away — because without it this is only a slower way to hide
/// something.
final class CourseManagementTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private func date(_ year: Int, _ month: Int, _ day: Int) -> Date {
        calendar.date(from: DateComponents(year: year, month: month, day: day))!
    }

    // MARK: - Spelling

    func testCourseNamesMatchWhateverTheirCaseAndSpacing() async {
        let store = IsolatedCourseBlocklistStore()
        await store.block("  41021 Interaction Design Studio  ")

        let isBlocked = await store.isBlocked(
            "41021 INTERACTION DESIGN STUDIO"
        )
        XCTAssertTrue(
            isBlocked,
            "A block written from Settings has to match what the parser sends"
        )
    }

    func testAnEmptyCourseNameIsNeverBlocked() async {
        let store = IsolatedCourseBlocklistStore()
        await store.block("   ")

        let blocked = await store.loadBlockedCourses()
        XCTAssertTrue(
            blocked.isEmpty,
            "Blocking nothing would silently hide every event without a course"
        )
    }

    func testUnblockingIsExactAndLeavesOtherCoursesAlone() async {
        let store = IsolatedCourseBlocklistStore()
        await store.block("MATH100")
        await store.block("PHYS200")

        await store.unblock("math100")

        let blocked = await store.loadBlockedCourses()
        XCTAssertEqual(blocked, ["PHYS200"])
    }

    /// Matching ignores case; showing does not. A course listed back to the
    /// user in a case they never typed reads as though the app mangled it.
    func testACourseIsShownBackTheWayItWasWritten() async {
        let store = IsolatedCourseBlocklistStore()
        await store.block("  68101 Physics Lab  ")

        let blocked = await store.loadBlockedCourses()
        XCTAssertEqual(blocked, ["68101 Physics Lab"])
        let keys = await store.blockedCourseKeys()
        XCTAssertEqual(keys, ["68101 physics lab"])
        let matches = await store.isBlocked("68101 PHYSICS LAB")
        XCTAssertTrue(matches)
    }

    func testBlockingTheSameCourseTwiceDoesNotListItTwice() async {
        let store = IsolatedCourseBlocklistStore()
        await store.block("68101 Physics Lab")
        await store.block("68101 physics lab")

        let blocked = await store.loadBlockedCourses()
        XCTAssertEqual(
            blocked,
            ["68101 physics lab"],
            "The most recent spelling wins, and there is only ever one row"
        )
    }

    // MARK: - Deleting a course's events

    func testDeletingACourseTakesEveryOneOfItsEventsIncludingArchived() async throws {
        let repository = try makeRepository()
        _ = try await repository.upsert(
            [
                record("Video 1", course: "41021 Interaction Design Studio", day: 14),
                record("Reflection 1", course: "41021 Interaction Design Studio", day: 17),
                record("Lab report", course: "PHYS200", day: 20),
            ],
            importedAt: date(2026, 7, 31)
        )
        // Archive one of them, the way three silent refreshes would.
        try await repository.reconcileCanvasFeed(
            FeedReconciliationRequest(
                activeExternalIDs: ["reflection-1", "lab-report"],
                cancelledExternalIDs: ["video-1"],
                observedAt: date(2026, 7, 31)
            )
        )
        let archived = try await repository.fetchAll(includingArchived: true)
        XCTAssertTrue(archived.contains { $0.title == "Video 1" && $0.isArchived })

        let removed = try await repository.deleteEvents(
            inCourse: "41021 interaction design studio"
        )

        XCTAssertEqual(removed, 2, "An archived row still belongs to the course")
        let remaining = try await repository.fetchAll(includingArchived: true)
        XCTAssertEqual(remaining.map(\.title), ["Lab report"])
    }

    func testDeletingACourseLeavesEventsWithNoCourseAlone() async throws {
        let repository = try makeRepository()
        _ = try await repository.saveManual(
            ManualAssignmentDraft(
                title: "Personal errand",
                courseName: nil,
                dueDate: date(2026, 8, 5)
            ),
            now: date(2026, 7, 31)
        )
        _ = try await repository.upsert(
            [record("Lab report", course: "PHYS200", day: 20)],
            importedAt: date(2026, 7, 31)
        )

        let removed = try await repository.deleteEvents(inCourse: "PHYS200")

        XCTAssertEqual(removed, 1)
        let remaining = try await repository.fetchAll()
        XCTAssertEqual(remaining.map(\.title), ["Personal errand"])
    }

    func testDeletingACourseThatIsNotThereChangesNothing() async throws {
        let repository = try makeRepository()
        _ = try await repository.upsert(
            [record("Lab report", course: "PHYS200", day: 20)],
            importedAt: date(2026, 7, 31)
        )

        let removed = try await repository.deleteEvents(inCourse: "GONE101")

        XCTAssertEqual(removed, 0)
        let remaining = try await repository.fetchAll()
        XCTAssertEqual(remaining.count, 1)
    }

    // MARK: - The refresh has to respect it

    func testARefreshDoesNotBringABlockedCourseBack() async throws {
        let repository = try makeRepository()
        let blocklist = IsolatedCourseBlocklistStore()
        let coordinator = makeCoordinator(
            repository: repository,
            blocklist: blocklist
        )

        _ = try await coordinator.refresh(
            now: date(2026, 7, 31),
            trigger: .manual
        )
        let firstPass = try await repository.fetchAll().map(\.title).sorted()
        XCTAssertEqual(firstPass, ["Lab report", "Video 1"])

        // Remove the old course, the way Settings does.
        await blocklist.block("41021 Interaction Design Studio")
        _ = try await repository.deleteEvents(
            inCourse: "41021 Interaction Design Studio"
        )

        _ = try await coordinator.refresh(
            now: date(2026, 7, 31),
            trigger: .manual
        )

        let afterBlocking = try await repository.fetchAll().map(\.title)
        XCTAssertEqual(
            afterBlocking,
            ["Lab report"],
            "The whole point of blocking is that the next refresh honours it"
        )
    }

    func testTheImportPreviewAlsoLeavesABlockedCourseOut() async throws {
        let repository = try makeRepository()
        let blocklist = IsolatedCourseBlocklistStore()
        await blocklist.block("PHYS200")
        let coordinator = makeCoordinator(
            repository: repository,
            blocklist: blocklist
        )

        let preview = try await coordinator.preview(
            feedURL: Self.feedURL,
            now: date(2026, 7, 31)
        )

        XCTAssertEqual(preview.events.map(\.summary), [
            "Video 1 [41021 Interaction Design Studio]",
        ])
        XCTAssertTrue(
            preview.activeExternalIDs.contains("lab-report"),
            "Reconciliation still needs the truth about what Canvas published"
        )
    }

    func testAllowingACourseAgainLetsTheNextRefreshImportIt() async throws {
        let repository = try makeRepository()
        let blocklist = IsolatedCourseBlocklistStore()
        await blocklist.block("PHYS200")
        let coordinator = makeCoordinator(
            repository: repository,
            blocklist: blocklist
        )

        _ = try await coordinator.refresh(now: date(2026, 7, 31), trigger: .manual)
        let whileBlocked = try await repository.fetchAll().map(\.title)
        XCTAssertEqual(whileBlocked, ["Video 1"])

        await blocklist.unblock("PHYS200")
        _ = try await coordinator.refresh(now: date(2026, 7, 31), trigger: .manual)

        let afterAllowing = try await repository.fetchAll().map(\.title).sorted()
        XCTAssertEqual(afterAllowing, ["Lab report", "Video 1"])
    }

    // MARK: - What Settings shows

    @MainActor
    func testSettingsListsEachCourseOnceWithItsEventCount() async throws {
        let harness = try makeViewModelHarness()
        await harness.viewModel.start()

        XCTAssertEqual(
            harness.viewModel.managedCourses.map(\.name),
            ["41021 Interaction Design Studio", "PHYS200"]
        )
        XCTAssertEqual(
            harness.viewModel.managedCourses.map(\.eventCount),
            [2, 1]
        )
        XCTAssertEqual(
            harness.viewModel.managedCourses[0].eventCountDescription,
            "2 events"
        )
        XCTAssertEqual(
            harness.viewModel.managedCourses[1].eventCountDescription,
            "1 event"
        )
    }

    @MainActor
    func testRemovingACourseClearsItsEventsAndBlocksIt() async throws {
        let harness = try makeViewModelHarness()
        await harness.viewModel.start()

        harness.viewModel.removeCourse("41021 Interaction Design Studio")
        try await Task.sleep(for: .milliseconds(150))

        XCTAssertEqual(harness.viewModel.managedCourses.map(\.name), ["PHYS200"])
        XCTAssertEqual(
            harness.viewModel.blockedCourses,
            ["41021 Interaction Design Studio"]
        )
        XCTAssertFalse(
            harness.viewModel.assignments.contains {
                $0.normalizedCourseName == "41021 Interaction Design Studio"
            }
        )
    }

    @MainActor
    func testRemovingACourseAlsoDropsItFromTheSavedCourseScope() async throws {
        let harness = try makeViewModelHarness()
        await harness.viewModel.start()
        harness.settings.dockCountMode = .selectedCourses
        harness.settings.selectedCourses = ["PHYS200", "41021 Interaction Design Studio"]
        harness.viewModel.settingsForm.selectedCourses = harness.settings.selectedCourses

        harness.viewModel.removeCourse("41021 Interaction Design Studio")
        try await Task.sleep(for: .milliseconds(150))

        XCTAssertEqual(
            harness.settings.selectedCourses,
            ["PHYS200"],
            "A removed course must not linger in the Dock's scope"
        )
    }

    @MainActor
    func testAllowingACourseAgainClearsTheBlock() async throws {
        let harness = try makeViewModelHarness()
        await harness.viewModel.start()
        harness.viewModel.removeCourse("PHYS200")
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertEqual(harness.viewModel.blockedCourses, ["PHYS200"])

        harness.viewModel.allowCourse("PHYS200")
        try await Task.sleep(for: .milliseconds(150))

        XCTAssertTrue(harness.viewModel.blockedCourses.isEmpty)
    }

    // MARK: - Helpers

    private static let feedURL = URL(string: "https://example.edu/feeds/a.ics")!

    private static let feed = """
    BEGIN:VCALENDAR
    VERSION:2.0
    BEGIN:VEVENT
    UID:video-1
    DTSTART:20260814T000000Z
    SUMMARY:Video 1 [41021 Interaction Design Studio]
    END:VEVENT
    BEGIN:VEVENT
    UID:lab-report
    DTSTART:20260820T000000Z
    SUMMARY:Lab report [PHYS200]
    END:VEVENT
    END:VCALENDAR
    """

    private func makeRepository() throws -> SwiftDataAssignmentRepository {
        let container = try ModelContainer(
            for: AssignmentEvent.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
        return SwiftDataAssignmentRepository(modelContainer: container)
    }

    private func makeCoordinator(
        repository: any AssignmentRepository,
        blocklist: any CourseBlocklisting
    ) -> RefreshCoordinator {
        RefreshCoordinator(
            fetcher: StaticFeedFetcher(body: Self.feed),
            parser: ICSParser(),
            repository: repository,
            feedURLStore: StaticFeedURLStore(url: Self.feedURL),
            exclusionStore: IsolatedFeedExclusionStore(),
            courseBlocklist: blocklist,
            defaultTimeZone: TimeZone(identifier: "Australia/Sydney")!,
            recentlyOverdueDayLimit: 3_650
        )
    }

    private func record(
        _ title: String,
        course: String,
        day: Int
    ) -> AssignmentImportRecord {
        AssignmentImportRecord(
            externalID: title.lowercased().replacingOccurrences(of: " ", with: "-"),
            title: title,
            courseName: course,
            dueDate: date(2026, 8, day),
            source: .canvasCalendarFeed
        )
    }

    @MainActor
    private func makeViewModelHarness() throws -> ViewModelHarness {
        let suiteName = "CourseManagementTests.\(UUID().uuidString)"
        let store = UserDefaults(suiteName: suiteName)!
        store.removePersistentDomain(forName: suiteName)

        let repository = try makeRepository()
        let settings = SettingsStore(defaults: store)
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: settings,
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            calendar: calendar,
            automaticActivityEnabled: false,
            courseBlocklist: IsolatedCourseBlocklistStore()
        )

        let seeded = [
            record("Video 1", course: "41021 Interaction Design Studio", day: 14),
            record("Reflection 1", course: "41021 Interaction Design Studio", day: 17),
            record("Lab report", course: "PHYS200", day: 20),
        ]
        let seedRepository = repository
        let seedDate = date(2026, 7, 1)
        let semaphore = DispatchSemaphore(value: 0)
        Task.detached {
            _ = try? await seedRepository.upsert(seeded, importedAt: seedDate)
            semaphore.signal()
        }
        semaphore.wait()

        return ViewModelHarness(viewModel: viewModel, settings: settings)
    }

    @MainActor
    private struct ViewModelHarness {
        let viewModel: MainViewModel
        let settings: SettingsStore
    }
}

// MARK: - Doubles

private struct StaticFeedFetcher: FeedFetching {
    let body: String

    func fetch(from url: URL) async throws -> Data {
        Data(body.utf8)
    }
}

private actor StaticFeedURLStore: FeedURLStoring {
    private var url: URL?

    init(url: URL?) {
        self.url = url
    }

    func saveFeedURL(_ url: URL) {
        self.url = url
    }

    func loadFeedURL() -> URL? { url }

    func deleteFeedURL() {
        url = nil
    }
}

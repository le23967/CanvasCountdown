import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// The toolbar course filter is one flat list of choices, and picking one takes
/// effect straight away.
@MainActor
final class CourseFilterMenuTests: XCTestCase {
    func testAllCoursesIsSelectableAndShowsEverything() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.selectCourse("PHYS200")

        context.viewModel.selectCourse(nil)

        XCTAssertNil(context.viewModel.selectedCourse)
        XCTAssertEqual(
            titles(context.viewModel.filteredAssignments),
            ["Maths sheet", "Physics lab", "Personal errand"]
        )
    }

    func testEachCourseIsSelectableAndFiltersImmediately() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming

        for course in context.viewModel.availableCourses {
            context.viewModel.selectCourse(course)

            XCTAssertEqual(context.viewModel.selectedCourse, course)
            XCTAssertFalse(
                context.viewModel.filteredAssignments.isEmpty,
                "\(course) should have at least one assignment"
            )
            XCTAssertTrue(
                context.viewModel.filteredAssignments.allSatisfy {
                    $0.normalizedCourseName == course
                },
                "\(course) must exclude every other course at once"
            )
        }
    }

    // MARK: - Completed and ignored, now that it lives in here

    /// The eye left the toolbar, so the only thing left saying a filter is on is
    /// the filled icon. It has to answer for both rules, or turning this on and
    /// forgetting looks exactly like events going missing.
    func testTheFilterReadsAsActiveFromEitherRule() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertFalse(context.viewModel.isFilterActive)

        context.viewModel.showCompletedAndIgnored = true
        XCTAssertTrue(context.viewModel.isFilterActive)

        context.viewModel.showCompletedAndIgnored = false
        context.viewModel.selectCourse("PHYS200")
        XCTAssertTrue(context.viewModel.isFilterActive)
    }

    /// The tooltip is where a filter that is on gets to explain itself.
    func testTheDescriptionNamesBothRules() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertEqual(
            context.viewModel.courseFilterDescription,
            "Filter, showing all courses"
        )

        context.viewModel.selectCourse("PHYS200")
        context.viewModel.showCompletedAndIgnored = true

        XCTAssertEqual(
            context.viewModel.courseFilterDescription,
            "Filter, showing PHYS200, including completed and ignored"
        )
    }

    func testSelectionUpdatesTheSidebarCountAndNearestCard() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        XCTAssertEqual(context.viewModel.upcomingCount, 3)
        XCTAssertEqual(
            context.viewModel.nearestAssignment?.title,
            "Maths sheet"
        )

        context.viewModel.selectCourse("PHYS200")

        XCTAssertEqual(
            context.viewModel.upcomingCount,
            1,
            "The sidebar count must follow the filter"
        )
        XCTAssertEqual(
            context.viewModel.nearestAssignment?.title,
            "Physics lab",
            "The nearest deadline card must follow the filter"
        )

        context.viewModel.selectCourse(nil)
        XCTAssertEqual(context.viewModel.upcomingCount, 3)
        XCTAssertEqual(
            context.viewModel.nearestAssignment?.title,
            "Maths sheet"
        )
    }

    func testTheFilterNarrowsWithinTheSavedCourseScope() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.dockCourseScope = .selectedCourses
        form.selectedCourses = ["MATH100"]
        context.viewModel.applySettings(form)

        context.viewModel.selectCourse("PHYS200")

        XCTAssertTrue(
            context.viewModel.visibleUpcomingAssignments.isEmpty,
            "A toolbar choice cannot reach past the saved scope"
        )
    }

    func testTheDockKeepsFollowingTheSavedScopeNotTheToolbarFilter() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        XCTAssertEqual(context.dock.renders.last?.daysRemaining, 1)
        let rendersBefore = context.dock.renders.count

        context.viewModel.selectCourse("PHYS200")

        XCTAssertEqual(
            context.dock.renders.count,
            rendersBefore,
            "Browsing the list must not repaint the Dock"
        )
        XCTAssertEqual(
            context.dock.renders.last?.daysRemaining,
            1,
            "The Dock still counts down to the real nearest deadline"
        )
    }

    func testSelectionIsClearedWhenTheCourseDisappears() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.selectCourse("PHYS200")

        await context.repository.deleteAll()
        try await context.viewModel.saveManualEvent(
            ManualEventDraft(title: "Only manual", dueDate: .now.addingTimeInterval(86_400))
        )

        XCTAssertNil(
            context.viewModel.selectedCourse,
            "A filter pointing at a course that no longer exists must clear"
        )
    }

    // MARK: - Menu shape

    func testMenuOffersOneFlatLevelOfChoices() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        // Every entry the menu shows is a course or the all-courses entry, so
        // there is nothing left to nest a second level around.
        let entries: [String?] = [nil] + context.viewModel.availableCourses
        XCTAssertEqual(entries.count, 3)
        for entry in entries {
            context.viewModel.selectCourse(entry)
            XCTAssertEqual(context.viewModel.selectedCourse, entry)
        }
    }

    func testNoIntermediateSubmenuStateExistsOnTheViewModel() {
        let mirror = Mirror(reflecting: MainViewModel.self)
        XCTAssertTrue(mirror.children.isEmpty)

        let names = Mirror(reflecting: SettingsFormState())
            .children
            .compactMap(\.label)
        XCTAssertFalse(names.contains("courseMenu"))
        XCTAssertFalse(names.contains("isShowingCourseMenu"))
        XCTAssertFalse(names.contains("courseSubmenuSelection"))
    }

    func testToolbarTitleStaysCompactForLongCourseNames() async throws {
        let context = try makeContext(longCourseName: true)
        await context.viewModel.start()
        let longCourse = try XCTUnwrap(
            context.viewModel.availableCourses.first { $0.count > 40 }
        )

        context.viewModel.selectCourse(longCourse)

        XCTAssertLessThanOrEqual(
            context.viewModel.courseFilterButtonTitle.count,
            18,
            "A long Canvas title must not stretch the toolbar"
        )
        XCTAssertTrue(context.viewModel.courseFilterButtonTitle.hasSuffix("…"))
        XCTAssertTrue(
            context.viewModel.courseFilterDescription.contains(longCourse),
            "The full title stays available to VoiceOver and the tooltip"
        )
    }

    func testMenuTitleTruncatesOnlyWhenNecessary() {
        XCTAssertEqual(MainViewModel.menuTitle(for: nil), "All Courses")
        XCTAssertEqual(MainViewModel.menuTitle(for: "PHYS200"), "PHYS200")

        let long = String(repeating: "A", count: 80)
        let truncated = MainViewModel.menuTitle(for: long)
        XCTAssertEqual(truncated.count, 44)
        XCTAssertTrue(truncated.hasSuffix("…"))
    }

    func testDefaultSelectionIsAllCourses() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertNil(context.viewModel.selectedCourse)
        XCTAssertEqual(
            context.viewModel.courseFilterButtonTitle,
            "All Courses"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let repository: ScopeRepositoryStub
        let dock: AppearanceRecordingDockSpy
    }

    private func titles(_ items: [AssignmentListItem]) -> [String] {
        items.map(\.title)
    }

    private func makeContext(longCourseName: Bool = false) throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = calendar.startOfDay(for: .now)
            let day = calendar.date(byAdding: .day, value: days, to: start) ?? start
            return calendar.date(bySettingHour: 12, minute: 0, second: 0, of: day)
                ?? day
        }

        // A synthetic title of the length Canvas can produce, used to check the
        // toolbar stays compact. Never a real course.
        let physicsCourse = longCourseName
            ? "99999 88888 Example Long Course Title For Layout Testing Only"
            : "PHYS200"
        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Maths sheet",
                    courseName: "MATH100",
                    dueDate: due(inDays: 1),
                    source: .canvasCalendarFeed
                ),
                AssignmentSnapshot(
                    title: "Physics lab",
                    courseName: physicsCourse,
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
        )

        let suiteName = "CourseFilterMenuTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let dock = AppearanceRecordingDockSpy()
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: CourseFilterCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: dock,
            notificationScheduler: InertNotificationScheduler(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel, repository: repository, dock: dock)
    }
}

private actor CourseFilterCoordinatorStub: FeedRefreshCoordinating {
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

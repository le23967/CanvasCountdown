import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// Search presentation, focus release and dismissal, plus the toolbar state
/// that must not depend on the macOS toolbar display mode.
@MainActor
final class ToolbarSearchTests: XCTestCase {
    // MARK: - Opening and focusing

    func testSearchStartsCollapsedAndUnfocused() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertFalse(context.viewModel.isSearchPresented)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertTrue(context.viewModel.searchText.isEmpty)
    }

    func testOpeningSearchPresentsAndFocusesTheField() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.presentSearch()

        XCTAssertTrue(context.viewModel.isSearchPresented)
        XCTAssertTrue(
            context.viewModel.isSearchFieldFocused,
            "The field must take focus when it appears"
        )
    }

    func testTheSearchIconTogglesSearchClosedAgain() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.toggleSearch()
        XCTAssertTrue(context.viewModel.isSearchPresented)

        context.viewModel.toggleSearch()
        XCTAssertFalse(context.viewModel.isSearchPresented)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
    }

    // MARK: - Dismissal

    func testClickingOutsideReleasesFocusButKeepsTheQuery() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()
        context.viewModel.searchText = "lab"

        // What the content-area gesture calls.
        context.viewModel.dismissSearchFocus()

        XCTAssertFalse(
            context.viewModel.isSearchFieldFocused,
            "A click outside must release keyboard focus"
        )
        XCTAssertTrue(
            context.viewModel.isSearchPresented,
            "The field stays visible so the query is not lost by a stray click"
        )
        XCTAssertEqual(context.viewModel.searchText, "lab")
    }

    func testEscapeReleasesFocusFirstThenCollapses() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()
        context.viewModel.searchText = "lab"

        XCTAssertEqual(
            context.viewModel.handleSearchEscape(),
            .releasedFocus
        )
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertTrue(context.viewModel.isSearchPresented)

        XCTAssertEqual(
            context.viewModel.handleSearchEscape(),
            .collapsed
        )
        XCTAssertFalse(context.viewModel.isSearchPresented)
        XCTAssertTrue(context.viewModel.searchText.isEmpty)
    }

    func testEscapeOnAnEmptyFieldCollapsesImmediately() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()

        XCTAssertEqual(context.viewModel.handleSearchEscape(), .collapsed)
        XCTAssertFalse(context.viewModel.isSearchPresented)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
    }

    func testAnotherToolbarActionReleasesSearchFocusAndStillRuns() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()
        XCTAssertTrue(context.viewModel.isSearchFieldFocused)

        // What every toolbar button does before its own work.
        context.viewModel.prepareForToolbarAction()
        context.viewModel.presentNewManualEvent()

        XCTAssertFalse(
            context.viewModel.isSearchFieldFocused,
            "The click must release focus"
        )
        XCTAssertTrue(
            context.viewModel.isShowingManualEditor,
            "and still perform the action"
        )
    }

    func testChangingSidebarSectionDismissesSearch() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()
        context.viewModel.searchText = "lab"

        context.viewModel.sidebarSelection = .allEvents

        XCTAssertFalse(context.viewModel.isSearchPresented)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertTrue(
            context.viewModel.searchText.isEmpty,
            "A query must not follow the user into another section"
        )
    }

    func testReselectingTheSameSectionDoesNotDismissSearch() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.presentSearch()

        context.viewModel.sidebarSelection = .upcoming

        XCTAssertTrue(context.viewModel.isSearchPresented)
    }

    // MARK: - Query behaviour

    func testQueryFiltersAndClearingRestoresEverything() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.presentSearch()

        context.viewModel.searchText = "lab"
        XCTAssertEqual(
            context.viewModel.filteredAssignments.map(\.title),
            ["Physics lab"]
        )

        context.viewModel.clearSearchQuery()
        XCTAssertEqual(
            context.viewModel.filteredAssignments.count,
            3,
            "Clearing the query restores the full list at once"
        )
        XCTAssertTrue(
            context.viewModel.isSearchPresented,
            "Clearing is not the same as dismissing"
        )
    }

    func testDismissingSearchClearsTheQueryAndRestoresTheList() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.presentSearch()
        context.viewModel.searchText = "maths"
        XCTAssertEqual(context.viewModel.filteredAssignments.count, 1)

        context.viewModel.dismissSearch()

        XCTAssertEqual(context.viewModel.filteredAssignments.count, 3)
    }

    // MARK: - Interaction with the rest of the window

    func testEventRowsStillOpenWhileSearchIsVisible() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()

        let manual = try XCTUnwrap(
            context.viewModel.assignments.first(where: \.isManual)
        )
        // The content gesture runs alongside the row's own tap, not instead
        // of it, so both happen on one click.
        context.viewModel.dismissSearchFocus()
        context.viewModel.presentEditor(for: manual)

        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertTrue(context.viewModel.isShowingManualEditor)
    }

    func testCourseFilterWorksImmediatelyWhileSearchIsActive() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.presentSearch()

        context.viewModel.prepareForToolbarAction()
        context.viewModel.selectCourse("PHYS200")

        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertEqual(context.viewModel.selectedCourse, "PHYS200")
        XCTAssertEqual(
            context.viewModel.filteredAssignments.map(\.title),
            ["Physics lab"]
        )
    }

    func testCompletedAndIgnoredToggleStillFiltersImmediately() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        let item = try XCTUnwrap(context.viewModel.assignments.first)
        context.viewModel.toggleCompleted(item)
        try await waitUntil {
            context.viewModel.assignments
                .first { $0.id == item.id }?.isCompleted == true
        }
        XCTAssertEqual(context.viewModel.filteredAssignments.count, 2)

        context.viewModel.showCompletedAndIgnored = true

        XCTAssertEqual(
            context.viewModel.filteredAssignments.count,
            3,
            "The toggle must still filter the moment it changes"
        )
    }

    // MARK: - The field must not compete for toolbar width

    func testSearchFieldIsLaidOutInTheContentAreaNotTheToolbar() {
        // Regression: the field used to be a toolbar item. A field wide enough
        // to type in could not be fitted beside the other controls in a normal
        // window, so macOS moved it into the toolbar's overflow menu and
        // clicking search appeared to do nothing at all. The placement is now
        // declared here and read by the view, so the two cannot disagree.
        XCTAssertEqual(
            SearchFieldPlacement.container,
            .contentArea,
            "A text field in the toolbar gets evicted into the overflow menu"
        )
        XCTAssertFalse(SearchFieldPlacement.isToolbarItem)
    }

    func testSearchFieldWidthCannotStretchTheToolbar() {
        // The field is bounded and lives outside the toolbar, so it can neither
        // push the icons apart nor be pushed out itself.
        XCTAssertGreaterThan(SearchFieldPlacement.maximumWidth, 220)
        XCTAssertLessThanOrEqual(SearchFieldPlacement.maximumWidth, 640)
    }

    // MARK: - Toolbar presentation

    func testToolbarStateCarriesNoDisplayModeDependency() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        // Every toolbar control is described by an icon plus a help and
        // accessibility string. Nothing in the view model supplies a visible
        // toolbar title that a Text Only mode could lay out.
        let names = Mirror(reflecting: context.viewModel)
            .children
            .compactMap(\.label)
        XCTAssertFalse(names.contains("toolbarDisplayMode"))
        XCTAssertFalse(names.contains("usesTextToolbar"))

        XCTAssertEqual(
            context.viewModel.courseFilterButtonTitle,
            "All Courses",
            "Still available for the tooltip, never rendered in the toolbar"
        )
        XCTAssertTrue(
            context.viewModel.courseFilterDescription
                .hasPrefix("Filter by course")
        )
    }

    func testCourseFilterDescriptionNamesTheSelectionForVoiceOver() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.selectCourse("PHYS200")

        XCTAssertEqual(
            context.viewModel.courseFilterDescription,
            "Filter by course, showing PHYS200"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
    }

    private func makeContext() throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = calendar.startOfDay(for: .now)
            let day = calendar.date(byAdding: .day, value: days, to: start) ?? start
            return calendar.date(bySettingHour: 12, minute: 0, second: 0, of: day)
                ?? day
        }

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
        )

        let suiteName = "ToolbarSearchTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: ToolbarCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: AppearanceRecordingDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel)
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
        XCTFail("Timed out waiting for the view model to update")
    }
}

private actor ToolbarCoordinatorStub: FeedRefreshCoordinating {
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

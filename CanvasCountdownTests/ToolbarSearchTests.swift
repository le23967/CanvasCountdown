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

        XCTAssertFalse(context.viewModel.isSearchModeActive)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertTrue(context.viewModel.searchText.isEmpty)
    }

    func testOpeningSearchPresentsAndFocusesTheField() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.presentSearch()

        XCTAssertTrue(context.viewModel.isSearchModeActive)
        XCTAssertTrue(
            context.viewModel.isSearchFieldFocused,
            "The field must take focus when it appears"
        )
    }

    func testTheSearchIconTogglesSearchClosedAgain() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.toggleSearch()
        XCTAssertTrue(context.viewModel.isSearchModeActive)

        context.viewModel.toggleSearch()
        XCTAssertFalse(context.viewModel.isSearchModeActive)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
    }

    // MARK: - Dismissal

    func testClickingBlankContentExitsSearchMode() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()
        context.viewModel.searchText = "lab"

        // What the content-area gesture calls.
        context.viewModel.dismissSearch()

        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertFalse(
            context.viewModel.isSearchModeActive,
            "The ordinary toolbar comes back"
        )
        XCTAssertTrue(context.viewModel.searchText.isEmpty)
    }

    func testEscapeExitsSearchModeAndClearsTheQuery() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()
        context.viewModel.searchText = "lab"

        context.viewModel.handleSearchEscape()

        XCTAssertFalse(context.viewModel.isSearchModeActive)
        XCTAssertFalse(context.viewModel.isSearchFieldFocused)
        XCTAssertTrue(
            context.viewModel.searchText.isEmpty,
            "Escape and Cancel leave search the same way"
        )
    }

    func testEscapeOnAnEmptyFieldExitsImmediately() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()

        context.viewModel.handleSearchEscape()

        XCTAssertFalse(context.viewModel.isSearchModeActive)
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

        XCTAssertFalse(context.viewModel.isSearchModeActive)
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

        XCTAssertTrue(context.viewModel.isSearchModeActive)
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
            context.viewModel.isSearchModeActive,
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

    func testSearchModeReplacesTheOrdinaryToolbarActions() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        XCTAssertTrue(context.viewModel.showsOrdinaryToolbarActions)
        XCTAssertFalse(context.viewModel.showsToolbarSearchField)

        context.viewModel.presentSearch()

        XCTAssertFalse(
            context.viewModel.showsOrdinaryToolbarActions,
            "The five actions and the search icon are gone, not merely hidden"
        )
        XCTAssertTrue(context.viewModel.showsToolbarSearchField)
    }

    func testLeavingSearchModeRestoresEveryToolbarAction() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()

        context.viewModel.dismissSearch()

        XCTAssertTrue(context.viewModel.showsOrdinaryToolbarActions)
        XCTAssertFalse(context.viewModel.showsToolbarSearchField)
    }

    func testHiddenToolbarActionsCannotBeDrivenWhileSearching() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()

        // The ordinary actions are not built at all in this mode, so the view
        // has nothing to route a click to.
        XCTAssertFalse(context.viewModel.showsOrdinaryToolbarActions)
        XCTAssertTrue(
            context.viewModel.isSearchModeActive,
            "Only the field and Cancel occupy the toolbar"
        )
    }

    func testSearchModeKeepsTheToolbarWithinTwoItems() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.presentSearch()

        // Two items instead of six is what keeps the field out of the overflow
        // menu at the supported minimum window width.
        XCTAssertEqual(context.viewModel.toolbarItemCount, 2)

        context.viewModel.dismissSearch()
        XCTAssertEqual(context.viewModel.toolbarItemCount, 6)
    }

    // MARK: - Toolbar presentation

    func testToolbarTitlesStayShortEnoughForIconAndText() async throws {
        let context = try makeContext(longCourseName: true)
        await context.viewModel.start()
        let longCourse = try XCTUnwrap(
            context.viewModel.availableCourses.first { $0.count > 40 }
        )
        context.viewModel.selectCourse(longCourse)

        // "Icon and Text" lays out each control's title, so a long Canvas course
        // name must never become one. The filter's visible title is a fixed
        // word; the selection lives in the tooltip and the menu checkmark.
        XCTAssertLessThanOrEqual(
            context.viewModel.courseFilterButtonTitle.count,
            18,
            "A long course title must not become a toolbar label"
        )
        XCTAssertTrue(
            context.viewModel.courseFilterDescription.contains(longCourse),
            "The full name is still available to VoiceOver and the tooltip"
        )

        let names = Mirror(reflecting: context.viewModel)
            .children
            .compactMap(\.label)
        XCTAssertFalse(
            names.contains("toolbarDisplayMode"),
            "The display mode is the user's choice, not app state"
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

    private func makeContext(longCourseName: Bool = false) throws -> Context {
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
                    courseName: longCourseName
                        ? "99999 88888 Example Long Course Title For Layout Testing Only"
                        : "PHYS200",
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

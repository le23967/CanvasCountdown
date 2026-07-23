import Foundation
import XCTest
@testable import CanvasCountdown

/// The month grid, and its promise that the calendar shows exactly what the
/// list shows.
@MainActor
final class AssignmentCalendarTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        calendar.firstWeekday = 1
        return calendar
    }

    private func date(_ year: Int, _ month: Int, _ day: Int, hour: Int = 12) -> Date {
        calendar.date(
            from: DateComponents(year: year, month: month, day: day, hour: hour)
        )!
    }

    private func item(_ title: String, _ due: Date, isCompleted: Bool = false) -> AssignmentListItem {
        AssignmentListItem(
            id: UUID(),
            title: title,
            courseName: "99999 Example Interaction Course",
            dueDate: due,
            isCompleted: isCompleted,
            isIgnored: false,
            isManual: false
        )
    }

    // MARK: - Grid shape

    func testGridIsAlwaysSixWeeks() {
        for month in 1...12 {
            let days = AssignmentCalendar.month(
                containing: date(2026, month, 15),
                events: [],
                calendar: calendar,
                now: date(2026, 7, 28)
            )
            XCTAssertEqual(
                days.count,
                42,
                "A fixed height stops the grid jumping between months"
            )
        }
    }

    func testGridStartsOnTheUsersFirstWeekday() {
        let days = AssignmentCalendar.month(
            containing: date(2026, 8, 15),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 28)
        )
        let first = try? XCTUnwrap(days.first)

        XCTAssertEqual(
            calendar.component(.weekday, from: first!.date),
            calendar.firstWeekday
        )
    }

    func testGridRespectsADifferentFirstWeekday() throws {
        var mondayFirst = calendar
        mondayFirst.firstWeekday = 2

        let days = AssignmentCalendar.month(
            containing: date(2026, 8, 15),
            events: [],
            calendar: mondayFirst,
            now: date(2026, 7, 28)
        )
        let first = try XCTUnwrap(days.first)

        XCTAssertEqual(mondayFirst.component(.weekday, from: first.date), 2)
        XCTAssertEqual(AssignmentCalendar.weekdaySymbols(calendar: mondayFirst).count, 7)
    }

    func testDaysOutsideTheMonthAreMarked() {
        let days = AssignmentCalendar.month(
            containing: date(2026, 8, 15),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 28)
        )

        XCTAssertTrue(days.contains { !$0.isInDisplayedMonth })
        XCTAssertEqual(
            days.filter(\.isInDisplayedMonth).count,
            31,
            "August has 31 days"
        )
    }

    func testFebruaryInALeapYear() {
        let days = AssignmentCalendar.month(
            containing: date(2028, 2, 10),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 28)
        )

        XCTAssertEqual(days.filter(\.isInDisplayedMonth).count, 29)
    }

    func testTodayIsMarkedOnlyOnce() {
        let today = date(2026, 8, 12)
        let days = AssignmentCalendar.month(
            containing: today,
            events: [],
            calendar: calendar,
            now: today
        )

        XCTAssertEqual(days.filter(\.isToday).count, 1)
    }

    // MARK: - Events in cells

    func testEventsLandOnTheirOwnDay() throws {
        let due = date(2026, 8, 4, hour: 16)
        let days = AssignmentCalendar.month(
            containing: due,
            events: [item("Example Quiz", due)],
            calendar: calendar,
            now: date(2026, 7, 28)
        )

        let cell = try XCTUnwrap(
            days.first { calendar.isDate($0.date, inSameDayAs: due) }
        )
        XCTAssertEqual(cell.items.map(\.title), ["Example Quiz"])
        XCTAssertEqual(
            days.filter { !$0.items.isEmpty }.count,
            1,
            "An event appears on exactly one day"
        )
    }

    func testSeveralEventsOnADayAreSortedByTime() throws {
        let morning = date(2026, 8, 4, hour: 9)
        let evening = date(2026, 8, 4, hour: 21)
        let days = AssignmentCalendar.month(
            containing: morning,
            events: [item("Later", evening), item("Earlier", morning)],
            calendar: calendar,
            now: date(2026, 7, 28)
        )

        let cell = try XCTUnwrap(days.first { !$0.items.isEmpty })
        XCTAssertEqual(cell.items.map(\.title), ["Earlier", "Later"])
    }

    func testAnEventLateAtNightStaysOnItsOwnDay() throws {
        // 23:59 must not spill into the next square.
        let due = date(2026, 8, 4, hour: 23)
        let days = AssignmentCalendar.month(
            containing: due,
            events: [item("Example Poster Submission", due)],
            calendar: calendar,
            now: date(2026, 7, 28)
        )

        let cell = try XCTUnwrap(days.first { !$0.items.isEmpty })
        XCTAssertEqual(calendar.component(.day, from: cell.date), 4)
    }

    func testAMonthWithNoEventsHasEmptyCells() {
        let days = AssignmentCalendar.month(
            containing: date(2026, 12, 1),
            events: [item("Example Quiz", date(2026, 8, 4))],
            calendar: calendar,
            now: date(2026, 7, 28)
        )

        XCTAssertTrue(days.allSatisfy(\.items.isEmpty))
    }

    // MARK: - Same events as the list

    func testCalendarShowsExactlyWhatTheListShows() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.assignmentViewMode = .calendar
        context.viewModel.calendarMonth = context.viewModel.currentDate

        let listTitles = Set(context.viewModel.filteredAssignments.map(\.title))
        let calendarTitles = Set(
            context.viewModel.calendarDays.flatMap(\.items).map(\.title)
        )

        XCTAssertEqual(calendarTitles, listTitles)
    }

    func testCourseFilterAppliesToTheCalendarToo() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.calendarMonth = context.viewModel.currentDate

        context.viewModel.selectCourse("PHYS200")

        let calendarTitles = context.viewModel.calendarDays
            .flatMap(\.items)
            .map(\.title)
        XCTAssertEqual(calendarTitles, ["Physics lab"])
    }

    func testSearchAppliesToTheCalendarToo() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.calendarMonth = context.viewModel.currentDate

        context.viewModel.searchText = "Maths"

        let calendarTitles = context.viewModel.calendarDays
            .flatMap(\.items)
            .map(\.title)
        XCTAssertEqual(calendarTitles, ["Maths sheet"])
    }

    // MARK: - Navigation

    func testMonthNavigationMovesAndReturns() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let start = context.viewModel.calendarMonth

        context.viewModel.showCalendarMonth(offsetBy: 1)
        XCTAssertGreaterThan(context.viewModel.calendarMonth, start)

        context.viewModel.showCalendarMonth(offsetBy: -1)
        XCTAssertEqual(
            Calendar.autoupdatingCurrent.component(
                .month,
                from: context.viewModel.calendarMonth
            ),
            Calendar.autoupdatingCurrent.component(.month, from: start)
        )
    }

    func testChangingMonthClearsTheSelectedDay() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.selectCalendarDay(context.viewModel.currentDate)
        XCTAssertNotNil(context.viewModel.selectedCalendarDay)

        context.viewModel.showCalendarMonth(offsetBy: 1)

        XCTAssertNil(
            context.viewModel.selectedCalendarDay,
            "A day selected in another month would be confusing"
        )
    }

    func testSelectedDayListsOnlyThatDay() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming
        let target = try XCTUnwrap(context.viewModel.filteredAssignments.first)

        context.viewModel.selectCalendarDay(target.dueDate)

        XCTAssertEqual(context.viewModel.selectedDayItems.map(\.title), [target.title])
    }

    func testViewModeDefaultsToListAndSwitches() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertEqual(context.viewModel.assignmentViewMode, .list)
        context.viewModel.assignmentViewMode = .calendar
        XCTAssertEqual(context.viewModel.assignmentViewMode, .calendar)
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
    }

    private func makeContext() throws -> Context {
        var testCalendar = Calendar(identifier: .gregorian)
        testCalendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = testCalendar.startOfDay(for: .now)
            let day = testCalendar.date(byAdding: .day, value: days, to: start) ?? start
            return testCalendar.date(bySettingHour: 12, minute: 0, second: 0, of: day) ?? day
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
            ]
        )

        let suiteName = "AssignmentCalendarTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: CalendarCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: AppearanceRecordingDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: testCalendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel)
    }
}

private actor CalendarCoordinatorStub: FeedRefreshCoordinating {
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

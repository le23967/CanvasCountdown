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
        context.viewModel.sidebarSelection = .calendar
        context.viewModel.calendarAnchor = context.viewModel.currentDate

        let listTitles = Set(context.viewModel.filteredAssignments.map(\.title))
        let calendarTitles = Set(
            context.viewModel.calendarDays.flatMap(\.items).map(\.title)
        )

        XCTAssertEqual(calendarTitles, listTitles)
    }

    func testCourseFilterAppliesToTheCalendarToo() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .calendar
        context.viewModel.calendarAnchor = context.viewModel.currentDate

        context.viewModel.selectCourse("PHYS200")

        let calendarTitles = context.viewModel.calendarDays
            .flatMap(\.items)
            .map(\.title)
        XCTAssertEqual(calendarTitles, ["Physics lab"])
    }

    /// Search is a panel over the window now, not a filter on what is behind
    /// it. A month grid that emptied itself as you typed would leave you
    /// somewhere you did not choose to be once the panel closed.
    func testSearchLeavesTheCalendarGridAlone() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .calendar
        context.viewModel.calendarAnchor = context.viewModel.currentDate
        let before = context.viewModel.calendarDays.flatMap(\.items).count

        context.viewModel.presentSearch()
        context.viewModel.searchText = "Maths"

        XCTAssertEqual(
            context.viewModel.calendarDays.flatMap(\.items).count,
            before
        )
        XCTAssertEqual(
            context.viewModel.searchResults.map(\.title),
            ["Maths sheet"],
            "The answer is in the panel"
        )
    }

    // MARK: - Navigation

    func testMonthNavigationMovesAndReturns() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.showCalendarScale(.month)
        let start = context.viewModel.calendarAnchor

        context.viewModel.showCalendar(offsetBy: 1)
        XCTAssertGreaterThan(context.viewModel.calendarAnchor, start)

        context.viewModel.showCalendar(offsetBy: -1)
        XCTAssertEqual(
            Calendar.autoupdatingCurrent.component(
                .month,
                from: context.viewModel.calendarAnchor
            ),
            Calendar.autoupdatingCurrent.component(.month, from: start)
        )
    }

    func testChangingMonthClearsTheSelectedDay() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.selectCalendarDay(context.viewModel.currentDate)
        XCTAssertNotNil(context.viewModel.selectedCalendarDay)

        context.viewModel.showCalendar(offsetBy: 1)

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

    func testCalendarIsItsOwnSidebarSection() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertTrue(SidebarDestination.allCases.contains(.calendar))
        XCTAssertEqual(SidebarDestination.calendar.title, "Calendar")

        context.viewModel.sidebarSelection = .calendar
        XCTAssertEqual(context.viewModel.sidebarSelection, .calendar)
    }

    func testCalendarSectionShowsTheWholeLibraryNotJustWhatIsAhead() async throws {
        // Upcoming hides anything already past. A month grid restricted the
        // same way would leave every earlier week blank.
        let context = try makeContext(includingPast: true)
        await context.viewModel.start()

        context.viewModel.sidebarSelection = .upcoming
        let upcoming = context.viewModel.filteredAssignments.count

        context.viewModel.sidebarSelection = .calendar
        let onCalendar = context.viewModel.filteredAssignments.count

        XCTAssertGreaterThan(
            onCalendar,
            upcoming,
            "A past deadline still belongs on the calendar"
        )
    }

    // MARK: - Scales

    func testEveryScaleShowsTheSameEventsAsTheList() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .calendar
        context.viewModel.calendarAnchor = context.viewModel.currentDate

        let listTitles = Set(context.viewModel.filteredAssignments.map(\.title))

        // The month and year grids reach beyond the events on hand, so each is
        // checked for carrying nothing the list would not show.
        for scale in CalendarScale.allCases {
            context.viewModel.showCalendarScale(scale)
            let shown: Set<String>
            switch scale {
            case .day:
                shown = Set(context.viewModel.calendarDay.items.map(\.title))
            case .week:
                shown = Set(context.viewModel.calendarWeekDays.flatMap(\.items).map(\.title))
            case .month:
                shown = Set(context.viewModel.calendarDays.flatMap(\.items).map(\.title))
            case .year:
                shown = Set(
                    context.viewModel.calendarYearMonths
                        .flatMap(\.days)
                        .flatMap(\.items)
                        .map(\.title)
                )
            }
            XCTAssertTrue(
                shown.isSubset(of: listTitles),
                "\(scale.title) showed something the list would not"
            )
        }
    }

    func testTheYearShowsEverythingTheListShows() async throws {
        let context = try makeContext(includingPast: true)
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .calendar
        context.viewModel.showCalendarScale(.year)

        let shown = Set(
            context.viewModel.calendarYearMonths
                .flatMap(\.days)
                .flatMap(\.items)
                .map(\.title)
        )

        XCTAssertEqual(
            shown,
            Set(context.viewModel.filteredAssignments.map(\.title))
        )
    }

    func testTheChosenScaleIsRemembered() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.showCalendarScale(.week)

        XCTAssertEqual(context.viewModel.calendarScale, .week)
        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).calendarScale,
            .week,
            "Coming back to the week view is the whole point of choosing it"
        )
    }

    func testTheArrowsStepByWhateverIsOnScreen() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let start = context.viewModel.calendarAnchor

        let today = context.calendar.startOfDay(for: start)
        context.viewModel.showCalendarScale(.day)
        context.viewModel.showCalendar(offsetBy: 1)
        XCTAssertEqual(
            context.calendar.dateComponents(
                [.day],
                from: today,
                to: context.viewModel.calendarAnchor
            ).day,
            1
        )

        context.viewModel.showToday()
        context.viewModel.showCalendarScale(.year)
        context.viewModel.showCalendar(offsetBy: 1)
        XCTAssertEqual(
            context.calendar.component(.year, from: context.viewModel.calendarAnchor),
            context.calendar.component(.year, from: start) + 1
        )
    }

    func testTodayIsOnlyOfferedWhenItIsSomewhereElse() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.showCalendarScale(.month)
        context.viewModel.showToday()

        XCTAssertTrue(context.viewModel.isShowingToday)

        context.viewModel.showCalendar(offsetBy: 1)
        XCTAssertFalse(context.viewModel.isShowingToday)

        context.viewModel.showToday()
        XCTAssertTrue(context.viewModel.isShowingToday)
    }

    func testDrillingFromTheYearOpensTheDay() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.showCalendarScale(.year)

        let target = try XCTUnwrap(context.viewModel.filteredAssignments.first)
        context.viewModel.showCalendarDate(target.dueDate, scale: .day)

        XCTAssertEqual(context.viewModel.calendarScale, .day)
        XCTAssertEqual(context.viewModel.calendarDay.items.map(\.title), [target.title])
    }

    // MARK: - Typing a date

    func testGoToDateMovesTheCalendarAndClearsTheField() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.calendarDateEntry = "2027-03-04"
        XCTAssertNotNil(context.viewModel.calendarDateEntryResult)
        XCTAssertTrue(context.viewModel.commitCalendarDateEntry())

        let parts = context.calendar.dateComponents(
            [.year, .month, .day],
            from: context.viewModel.calendarAnchor
        )
        XCTAssertEqual(parts.year, 2027)
        XCTAssertEqual(parts.month, 3)
        XCTAssertEqual(parts.day, 4)
        XCTAssertEqual(context.viewModel.calendarDateEntry, "")
    }

    func testGoToDateRefusesTextItCannotRead() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let anchor = context.viewModel.calendarAnchor

        context.viewModel.calendarDateEntry = "sometime next term"

        XCTAssertNil(context.viewModel.calendarDateEntryResult)
        XCTAssertFalse(context.viewModel.commitCalendarDateEntry())
        XCTAssertEqual(context.viewModel.calendarAnchor, anchor)
        XCTAssertEqual(
            context.viewModel.calendarDateEntry,
            "sometime next term",
            "A typo must stay in the field to be corrected"
        )
    }

    func testTheMenuOpensTheCalendarBeforeChangingIt() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.sidebarSelection = .upcoming

        context.viewModel.showCalendarSection(.week)

        XCTAssertEqual(context.viewModel.sidebarSelection, .calendar)
        XCTAssertEqual(context.viewModel.calendarScale, .week)

        context.viewModel.sidebarSelection = .upcoming
        context.viewModel.presentCalendarDateEntry()

        XCTAssertEqual(context.viewModel.sidebarSelection, .calendar)
        XCTAssertTrue(context.viewModel.isShowingCalendarDateEntry)
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
        /// The same calendar the view model was built with, so assertions about
        /// where it moved are not made in a different time zone from the move.
        let calendar: Calendar
    }

    private func makeContext(includingPast: Bool = false) throws -> Context {
        var testCalendar = Calendar(identifier: .gregorian)
        testCalendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = testCalendar.startOfDay(for: .now)
            let day = testCalendar.date(byAdding: .day, value: days, to: start) ?? start
            return testCalendar.date(bySettingHour: 12, minute: 0, second: 0, of: day) ?? day
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
        ]
        if includingPast {
            snapshots.append(
                AssignmentSnapshot(
                    title: "Last week's reading",
                    courseName: "MATH100",
                    dueDate: due(inDays: -6),
                    source: .canvasCalendarFeed
                )
            )
        }
        let repository = ScopeRepositoryStub(snapshots: snapshots)

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
        return Context(
            viewModel: viewModel,
            defaults: defaults,
            calendar: testCalendar
        )
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

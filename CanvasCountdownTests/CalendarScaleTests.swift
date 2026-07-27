import Foundation
import XCTest
@testable import CanvasCountdown

/// The day, week and year grids, the hour layout underneath them, and the date
/// field that jumps between them. All fixtures are invented.
@MainActor
final class CalendarScaleTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        calendar.firstWeekday = 1
        return calendar
    }

    private func date(
        _ year: Int,
        _ month: Int,
        _ day: Int,
        hour: Int = 12,
        minute: Int = 0
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

    private func item(_ title: String, _ due: Date) -> AssignmentListItem {
        AssignmentListItem(
            id: UUID(),
            title: title,
            courseName: "99999 Example Interaction Course",
            dueDate: due,
            isCompleted: false,
            isIgnored: false,
            isManual: false
        )
    }

    // MARK: - Week

    func testAWeekIsSevenDaysStartingOnTheUsersFirstWeekday() throws {
        let days = AssignmentCalendar.week(
            containing: date(2026, 7, 29),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 29)
        )

        XCTAssertEqual(days.count, 7)
        let first = try XCTUnwrap(days.first)
        XCTAssertEqual(
            calendar.component(.weekday, from: first.date),
            calendar.firstWeekday
        )
    }

    func testAWeekRespectsADifferentFirstWeekday() throws {
        var mondayFirst = calendar
        mondayFirst.firstWeekday = 2

        let days = AssignmentCalendar.week(
            containing: date(2026, 7, 29),
            events: [],
            calendar: mondayFirst,
            now: date(2026, 7, 29)
        )

        let first = try XCTUnwrap(days.first)
        XCTAssertEqual(calendar.component(.weekday, from: first.date), 2)
    }

    func testAWeekCarriesTheEventsOfItsOwnDaysOnly() throws {
        let inside = item("Example Quiz", date(2026, 7, 30, hour: 16))
        let outside = item("Example Poster", date(2026, 8, 20, hour: 16))

        let days = AssignmentCalendar.week(
            containing: date(2026, 7, 29),
            events: [inside, outside],
            calendar: calendar,
            now: date(2026, 7, 29)
        )

        XCTAssertEqual(days.flatMap(\.items).map(\.title), ["Example Quiz"])
    }

    func testTheWeekStraddlingAMonthKeepsBothMonths() {
        let days = AssignmentCalendar.week(
            containing: date(2026, 7, 30),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 29)
        )

        let months = Set(days.map { calendar.component(.month, from: $0.date) })
        XCTAssertEqual(months, [7, 8])
    }

    // MARK: - Day

    func testADayCarriesOnlyItsOwnEvents() {
        let day = AssignmentCalendar.day(
            containing: date(2026, 8, 4),
            events: [
                item("Example Quiz", date(2026, 8, 4, hour: 16)),
                item("Example Poster", date(2026, 8, 5, hour: 16)),
            ],
            calendar: calendar,
            now: date(2026, 7, 29)
        )

        XCTAssertEqual(day.items.map(\.title), ["Example Quiz"])
        XCTAssertFalse(day.isToday)
    }

    func testTodayIsMarkedAsToday() {
        let day = AssignmentCalendar.day(
            containing: date(2026, 7, 29, hour: 9),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 29, hour: 23)
        )

        XCTAssertTrue(day.isToday)
    }

    // MARK: - Year

    func testAYearIsTwelveMonthsInOrder() throws {
        let months = AssignmentCalendar.year(
            containing: date(2026, 7, 29),
            events: [],
            calendar: calendar,
            now: date(2026, 7, 29)
        )

        XCTAssertEqual(months.count, 12)
        XCTAssertEqual(
            months.map { calendar.component(.month, from: $0.start) },
            Array(1...12)
        )
        XCTAssertTrue(
            months.allSatisfy {
                calendar.component(.year, from: $0.start) == 2026
            }
        )
        let january = try XCTUnwrap(months.first)
        XCTAssertEqual(january.days.count, 42)
    }

    func testAYearPlacesAnEventInItsOwnMonthOnly() throws {
        let months = AssignmentCalendar.year(
            containing: date(2026, 1, 1),
            events: [item("Example Quiz", date(2026, 8, 4, hour: 16))],
            calendar: calendar,
            now: date(2026, 7, 29)
        )

        // The padding days of a neighbouring month legitimately repeat it, so
        // this counts the months whose own days carry it.
        let owning = months.filter { month in
            month.days.contains {
                $0.isInDisplayedMonth && !$0.items.isEmpty
            }
        }
        XCTAssertEqual(owning.count, 1)
        XCTAssertEqual(
            calendar.component(.month, from: try XCTUnwrap(owning.first).start),
            8
        )
    }

    // MARK: - Stepping

    func testEachScaleStepsByItsOwnUnit() {
        let start = date(2026, 7, 29)

        let day = AssignmentCalendar.date(byAdding: .day, count: 1, to: start, calendar: calendar)
        XCTAssertEqual(calendar.component(.day, from: day), 30)

        let week = AssignmentCalendar.date(byAdding: .week, count: 1, to: start, calendar: calendar)
        XCTAssertEqual(calendar.component(.day, from: week), 5)
        XCTAssertEqual(calendar.component(.month, from: week), 8)

        let month = AssignmentCalendar.date(byAdding: .month, count: 1, to: start, calendar: calendar)
        XCTAssertEqual(calendar.component(.month, from: month), 8)

        let year = AssignmentCalendar.date(byAdding: .year, count: 1, to: start, calendar: calendar)
        XCTAssertEqual(calendar.component(.year, from: year), 2027)
    }

    func testSteppingFromTheEndOfAMonthDoesNotWalkBackwards() {
        // Adding a month to the 31st lands on the 30th, so a calendar that kept
        // the day would lose one every time it paged. Normalising first is what
        // stops that, and this is the case it was written for.
        var anchor = AssignmentCalendar.normalized(
            date(2026, 3, 31),
            for: .month,
            calendar: calendar
        )
        for _ in 0..<6 {
            anchor = AssignmentCalendar.date(
                byAdding: .month,
                count: 1,
                to: anchor,
                calendar: calendar
            )
        }
        for _ in 0..<6 {
            anchor = AssignmentCalendar.date(
                byAdding: .month,
                count: -1,
                to: anchor,
                calendar: calendar
            )
        }

        XCTAssertEqual(
            calendar.dateComponents([.year, .month], from: anchor),
            DateComponents(year: 2026, month: 3)
        )
    }

    func testNormalisingLandsOnTheStartOfThePeriod() {
        let sample = date(2026, 3, 31, hour: 23, minute: 45)

        XCTAssertEqual(
            calendar.dateComponents(
                [.year, .month, .day, .hour],
                from: AssignmentCalendar.normalized(sample, for: .day, calendar: calendar)
            ),
            DateComponents(year: 2026, month: 3, day: 31, hour: 0)
        )
        XCTAssertEqual(
            calendar.dateComponents(
                [.year, .month, .day],
                from: AssignmentCalendar.normalized(sample, for: .month, calendar: calendar)
            ),
            DateComponents(year: 2026, month: 3, day: 1)
        )
        XCTAssertEqual(
            calendar.dateComponents(
                [.year, .month, .day],
                from: AssignmentCalendar.normalized(sample, for: .year, calendar: calendar)
            ),
            DateComponents(year: 2026, month: 1, day: 1)
        )
        XCTAssertEqual(
            calendar.component(
                .weekday,
                from: AssignmentCalendar.normalized(sample, for: .week, calendar: calendar)
            ),
            calendar.firstWeekday
        )
    }

    // MARK: - Hour layout

    func testMidnightIsAnAllDayDeadlineRatherThanOhOhHundred() {
        let midnight = item("Example Reflection", date(2026, 8, 14, hour: 0))
        let afternoon = item("Example Quiz", date(2026, 8, 14, hour: 16))

        XCTAssertTrue(CalendarTimeGrid.isAllDay(midnight, calendar: calendar))
        XCTAssertFalse(CalendarTimeGrid.isAllDay(afternoon, calendar: calendar))

        XCTAssertEqual(
            CalendarTimeGrid.allDayItems(
                in: [midnight, afternoon],
                calendar: calendar
            ).map(\.title),
            ["Example Reflection"]
        )
        XCTAssertEqual(
            CalendarTimeGrid.timedEntries(
                in: [midnight, afternoon],
                calendar: calendar
            ).map(\.item.title),
            ["Example Quiz"]
        )
    }

    func testAnEventIsPlacedAtItsOwnMinute() throws {
        let entries = CalendarTimeGrid.timedEntries(
            in: [item("Example Quiz", date(2026, 8, 4, hour: 16, minute: 30))],
            calendar: calendar
        )

        let entry = try XCTUnwrap(entries.first)
        XCTAssertEqual(entry.minuteOfDay, 16 * 60 + 30)
        XCTAssertEqual(entry.column, 0)
        XCTAssertEqual(entry.columnCount, 1, "Alone, it gets the whole width")
    }

    func testDeadlinesAtTheSameTimeShareTheWidth() {
        let entries = CalendarTimeGrid.timedEntries(
            in: [
                item("Example Quiz", date(2026, 8, 4, hour: 16)),
                item("Example Poster", date(2026, 8, 4, hour: 16)),
                item("Example Reflection", date(2026, 8, 4, hour: 16)),
            ],
            calendar: calendar
        )

        XCTAssertEqual(entries.count, 3)
        XCTAssertEqual(Set(entries.map(\.columnCount)), [3])
        XCTAssertEqual(Set(entries.map(\.column)), [0, 1, 2])
    }

    func testDeadlinesFarApartEachKeepTheWholeWidth() {
        let entries = CalendarTimeGrid.timedEntries(
            in: [
                item("Morning", date(2026, 8, 4, hour: 9)),
                item("Evening", date(2026, 8, 4, hour: 18)),
            ],
            calendar: calendar
        )

        XCTAssertEqual(Set(entries.map(\.columnCount)), [1])
        XCTAssertEqual(Set(entries.map(\.column)), [0])
    }

    func testAFreedColumnIsReusedByALaterDeadline() throws {
        // 09:00 and 09:20 overlap, so they split; 11:00 is clear of both and
        // must not inherit their split.
        let entries = CalendarTimeGrid.timedEntries(
            in: [
                item("First", date(2026, 8, 4, hour: 9)),
                item("Second", date(2026, 8, 4, hour: 9, minute: 20)),
                item("Third", date(2026, 8, 4, hour: 11)),
            ],
            calendar: calendar
        )

        let byTitle = Dictionary(uniqueKeysWithValues: entries.map { ($0.item.title, $0) })
        XCTAssertEqual(try XCTUnwrap(byTitle["First"]).columnCount, 2)
        XCTAssertEqual(try XCTUnwrap(byTitle["Second"]).column, 1)
        XCTAssertEqual(try XCTUnwrap(byTitle["Third"]).columnCount, 1)
    }

    func testThereIsALabelForEveryHour() {
        XCTAssertEqual(
            CalendarTimeGrid.hourLabels(calendar: calendar).count,
            CalendarTimeGrid.hoursShown
        )
    }

    // MARK: - Typing a date

    private func parsed(
        _ text: String,
        reference: Date? = nil,
        locale: Locale = Locale(identifier: "en_AU")
    ) -> DateComponents? {
        guard let result = CalendarDateEntry.date(
            from: text,
            reference: reference ?? date(2026, 7, 29),
            calendar: calendar,
            locale: locale,
            now: date(2026, 7, 29)
        ) else {
            return nil
        }
        return calendar.dateComponents([.year, .month, .day], from: result)
    }

    func testAnISODateIsRead() {
        XCTAssertEqual(parsed("2026-08-14"), DateComponents(year: 2026, month: 8, day: 14))
    }

    func testAMonthNameIsReadInEitherOrder() {
        XCTAssertEqual(parsed("14 aug"), DateComponents(year: 2026, month: 8, day: 14))
        XCTAssertEqual(parsed("aug 14"), DateComponents(year: 2026, month: 8, day: 14))
        XCTAssertEqual(
            parsed("August 14 2027"),
            DateComponents(year: 2027, month: 8, day: 14)
        )
    }

    func testTheLocaleDecidesWhatComesFirstInANumericDate() {
        XCTAssertEqual(
            parsed("8/9", locale: Locale(identifier: "en_AU")),
            DateComponents(year: 2026, month: 9, day: 8),
            "Australia writes the day first"
        )
        XCTAssertEqual(
            parsed("8/9", locale: Locale(identifier: "en_US")),
            DateComponents(year: 2026, month: 8, day: 9),
            "The United States writes the month first"
        )
    }

    func testAYearFirstDateIsReadWhateverTheLocale() {
        XCTAssertEqual(
            parsed("2027/3/4", locale: Locale(identifier: "en_US")),
            DateComponents(year: 2027, month: 3, day: 4)
        )
    }

    func testMissingPartsComeFromTheDayOnScreen() {
        XCTAssertEqual(
            parsed("14", reference: date(2026, 11, 2)),
            DateComponents(year: 2026, month: 11, day: 14),
            "A bare number is a day of the month being looked at"
        )
        XCTAssertEqual(
            parsed("3 mar", reference: date(2028, 1, 1)),
            DateComponents(year: 2028, month: 3, day: 3),
            "A missing year is the year being looked at, not the next one"
        )
    }

    func testTodayAndTomorrowAreRead() {
        XCTAssertEqual(parsed("today"), DateComponents(year: 2026, month: 7, day: 29))
        XCTAssertEqual(parsed("tomorrow"), DateComponents(year: 2026, month: 7, day: 30))
        XCTAssertEqual(parsed("yesterday"), DateComponents(year: 2026, month: 7, day: 28))
    }

    func testNonsenseIsRefusedRatherThanGuessed() {
        XCTAssertNil(parsed(""))
        XCTAssertNil(parsed("   "))
        XCTAssertNil(parsed("next term"))
        XCTAssertNil(parsed("31 february"), "A day that does not exist is not rolled forward")
        XCTAssertNil(parsed("2026-13-01"))
        XCTAssertNil(parsed("hello"))
    }

    func testTheResultIsTheStartOfTheDay() throws {
        let result = try XCTUnwrap(
            CalendarDateEntry.date(
                from: "14 aug",
                reference: date(2026, 7, 29),
                calendar: calendar,
                locale: Locale(identifier: "en_AU"),
                now: date(2026, 7, 29)
            )
        )

        XCTAssertEqual(result, calendar.startOfDay(for: result))
    }
}

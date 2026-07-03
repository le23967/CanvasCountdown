import Foundation
import XCTest
@testable import CanvasCountdown

final class CountdownCalculatorTests: XCTestCase {
    private struct Event: CountdownEvent, Equatable {
        let name: String
        let dueDate: Date
        var isCompleted = false
        var isIgnored = false
    }

    private var sydneyCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_AU")
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    func testLaterTodayIsZeroCalendarDaysAway() throws {
        let calendar = sydneyCalendar
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 8))
        )
        let due = try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: 2026,
                    month: 7,
                    day: 28,
                    hour: 23,
                    minute: 59
                )
            )
        )

        XCTAssertEqual(
            CountdownCalculator.daysRemaining(
                until: due,
                now: now,
                calendar: calendar
            ),
            0
        )
    }

    func testTomorrowIsOneCalendarDayAway() throws {
        let calendar = sydneyCalendar
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 8))
        )
        let due = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 29, hour: 8))
        )

        XCTAssertEqual(
            CountdownCalculator.daysRemaining(
                until: due,
                now: now,
                calendar: calendar
            ),
            1
        )
    }

    func testCrossingMidnightIsOneDayEvenWhenMinutesApart() throws {
        let calendar = sydneyCalendar
        let now = try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: 2026,
                    month: 7,
                    day: 28,
                    hour: 23,
                    minute: 59
                )
            )
        )
        let due = try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: 2026,
                    month: 7,
                    day: 29,
                    hour: 0,
                    minute: 1
                )
            )
        )

        XCTAssertEqual(
            CountdownCalculator.daysRemaining(
                until: due,
                now: now,
                calendar: calendar
            ),
            1
        )
    }

    func testCalendarDaysRemainCorrectOverSpringDST() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "America/New_York"))
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 3, day: 7, hour: 12))
        )
        let due = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 3, day: 9, hour: 12))
        )

        XCTAssertEqual(due.timeIntervalSince(now), 47 * 60 * 60, accuracy: 0.1)
        XCTAssertEqual(
            CountdownCalculator.daysRemaining(
                until: due,
                now: now,
                calendar: calendar
            ),
            2
        )
    }

    func testCalendarDaysRemainCorrectOverAutumnDST() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "America/New_York"))
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 10, day: 31, hour: 12))
        )
        let due = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 11, day: 2, hour: 12))
        )

        XCTAssertEqual(due.timeIntervalSince(now), 49 * 60 * 60, accuracy: 0.1)
        XCTAssertEqual(
            CountdownCalculator.daysRemaining(
                until: due,
                now: now,
                calendar: calendar
            ),
            2
        )
    }

    func testNearestSelectionExcludesOverdueCompletedAndIgnoredEvents() throws {
        let calendar = sydneyCalendar
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 10))
        )
        func date(_ day: Int, hour: Int = 12) throws -> Date {
            try XCTUnwrap(
                calendar.date(
                    from: DateComponents(year: 2026, month: 7, day: day, hour: hour)
                )
            )
        }

        let events = [
            Event(name: "overdue", dueDate: try date(27)),
            Event(
                name: "completed",
                dueDate: try date(28, hour: 11),
                isCompleted: true
            ),
            Event(
                name: "ignored",
                dueDate: try date(28, hour: 11),
                isIgnored: true
            ),
            Event(name: "later", dueDate: try date(30)),
            Event(name: "nearest", dueDate: try date(29)),
        ]

        let selection = try XCTUnwrap(
            CountdownCalculator.nearestUpcoming(
                from: events,
                now: now,
                calendar: calendar
            )
        )
        XCTAssertEqual(selection.event.name, "nearest")
        XCTAssertEqual(selection.daysRemaining, 1)
    }

    func testEarlierTodayIsOverdueAndExcluded() throws {
        let calendar = sydneyCalendar
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 10))
        )
        let event = Event(
            name: "past deadline",
            dueDate: try XCTUnwrap(
                calendar.date(
                    from: DateComponents(year: 2026, month: 7, day: 28, hour: 9)
                )
            )
        )

        XCTAssertNil(
            CountdownCalculator.nearestUpcoming(
                from: [event],
                now: now,
                calendar: calendar
            )
        )
    }

    func testEqualDueDatesPreserveInputOrder() throws {
        let calendar = sydneyCalendar
        let now = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 10))
        )
        let due = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 7, day: 29, hour: 12))
        )
        let events = [
            Event(name: "first", dueDate: due),
            Event(name: "second", dueDate: due),
        ]

        XCTAssertEqual(
            CountdownCalculator.nearestUpcoming(
                from: events,
                now: now,
                calendar: calendar
            )?.event.name,
            "first"
        )
    }
}

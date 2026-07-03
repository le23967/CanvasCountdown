import Foundation
import XCTest
@testable import CanvasCountdown

final class ICSParserTests: XCTestCase {
    private let parser = ICSParser()
    private let utc = TimeZone(secondsFromGMT: 0)!

    func testParsesUTCDateAndRelevantFields() throws {
        let result = try parser.parseResult(
            fixtureData(),
            defaultTimeZone: utc
        )
        let event = try XCTUnwrap(
            result.events.first { $0.uid == "utc-1@example.edu" }
        )

        XCTAssertEqual(event.summary, "Research proposal")
        XCTAssertEqual(
            event.startDate,
            date(
                year: 2026,
                month: 8,
                day: 3,
                hour: 6,
                minute: 30,
                timeZone: utc
            )
        )
        XCTAssertEqual(
            event.endDate,
            date(
                year: 2026,
                month: 8,
                day: 3,
                hour: 7,
                minute: 30,
                timeZone: utc
            )
        )
        XCTAssertEqual(
            event.lastModified,
            date(
                year: 2026,
                month: 7,
                day: 28,
                hour: 0,
                minute: 15,
                timeZone: utc
            )
        )
        XCTAssertEqual(
            event.url,
            "https://canvas.example.edu/courses/42/assignments/9"
        )
        XCTAssertEqual(event.timeZoneIdentifier, "UTC")
        XCTAssertFalse(event.isAllDay)
    }

    func testParsesAllDayDateInDefaultTimeZone() throws {
        let sydney = try XCTUnwrap(TimeZone(identifier: "Australia/Sydney"))
        let events = try parser.parse(
            fixtureData(),
            defaultTimeZone: sydney
        )
        let event = try XCTUnwrap(
            events.first { $0.uid == "all-day-1@example.edu" }
        )

        XCTAssertEqual(
            event.startDate,
            date(year: 2026, month: 8, day: 4, timeZone: sydney)
        )
        XCTAssertEqual(
            event.endDate,
            date(year: 2026, month: 8, day: 5, timeZone: sydney)
        )
        XCTAssertTrue(event.isAllDay)
        XCTAssertNil(event.timeZoneIdentifier)
    }

    func testParsesTZIDDateAcrossDST() throws {
        let events = try parser.parse(
            fixtureData(),
            defaultTimeZone: utc
        )
        let event = try XCTUnwrap(
            events.first { $0.uid == "tzid-1@example.edu" }
        )
        let newYork = try XCTUnwrap(TimeZone(identifier: "America/New_York"))

        XCTAssertEqual(
            event.startDate,
            date(
                year: 2026,
                month: 11,
                day: 2,
                hour: 9,
                minute: 45,
                timeZone: newYork
            )
        )
        XCTAssertEqual(event.timeZoneIdentifier, "America/New_York")
    }

    func testFloatingDateTimeUsesSuppliedMacTimeZone() throws {
        let sydney = try XCTUnwrap(TimeZone(identifier: "Australia/Sydney"))
        let text = calendar(
            """
            BEGIN:VEVENT
            UID:floating@example.edu
            DTSTART:20260812T235900
            SUMMARY:Floating deadline
            END:VEVENT
            """
        )

        let event = try XCTUnwrap(
            parser.parse(text, defaultTimeZone: sydney).first
        )
        XCTAssertEqual(
            event.startDate,
            date(
                year: 2026,
                month: 8,
                day: 12,
                hour: 23,
                minute: 59,
                timeZone: sydney
            )
        )
        XCTAssertNil(event.timeZoneIdentifier)
    }

    func testFoldedLinesAndEscapedText() throws {
        let event = try XCTUnwrap(
            parser.parse(fixtureData(), defaultTimeZone: utc)
                .first { $0.uid == "folded-1@example.edu" }
        )

        XCTAssertEqual(
            event.summary,
            "A very long assignment title, with detail"
        )
        XCTAssertEqual(
            event.description,
            "Line one\nLine two; bring notes, laptop\\charger"
        )
    }

    func testCancelledAndDatelessEventsAreIgnored() throws {
        let result = try parser.parseResult(
            fixtureData(),
            defaultTimeZone: utc
        )

        XCTAssertFalse(
            result.events.contains { $0.uid == "cancelled@example.edu" }
        )
        XCTAssertFalse(
            result.events.contains { $0.uid == "missing-date@example.edu" }
        )
        XCTAssertTrue(
            result.warnings.contains {
                $0.eventUID == "missing-date@example.edu" &&
                    $0.message.contains("without DTSTART")
            }
        )
    }

    func testVTimeZoneLocationResolvesCustomTZID() throws {
        let text = calendar(
            """
            BEGIN:VTIMEZONE
            TZID:University Time
            X-LIC-LOCATION:Australia/Sydney
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:alias@example.edu
            DTSTART;TZID="University Time":20261004T150000
            SUMMARY:Custom zone
            END:VEVENT
            """
        )
        let event = try XCTUnwrap(
            parser.parse(text, defaultTimeZone: utc).first
        )
        let sydney = try XCTUnwrap(TimeZone(identifier: "Australia/Sydney"))

        XCTAssertEqual(
            event.startDate,
            date(
                year: 2026,
                month: 10,
                day: 4,
                hour: 15,
                timeZone: sydney
            )
        )
        XCTAssertEqual(event.timeZoneIdentifier, "Australia/Sydney")
    }

    func testNestedAlarmDoesNotReplaceEventFields() throws {
        let text = calendar(
            """
            BEGIN:VEVENT
            UID:alarm@example.edu
            DTSTART:20260801T100000Z
            SUMMARY:Real title
            DESCRIPTION:Real description
            BEGIN:VALARM
            SUMMARY:Alarm title
            DESCRIPTION:Alarm description
            TRIGGER:-PT15M
            END:VALARM
            END:VEVENT
            """
        )
        let event = try XCTUnwrap(
            parser.parse(text, defaultTimeZone: utc).first
        )

        XCTAssertEqual(event.summary, "Real title")
        XCTAssertEqual(event.description, "Real description")
    }

    func testInvalidEventDateWarnsWithoutLosingValidEvents() throws {
        let text = calendar(
            """
            BEGIN:VEVENT
            UID:bad@example.edu
            DTSTART:tomorrow-ish
            SUMMARY:Bad
            END:VEVENT
            BEGIN:VEVENT
            UID:good@example.edu
            DTSTART:20260801T100000Z
            SUMMARY:Good
            END:VEVENT
            """
        )
        let result = try parser.parseResult(
            text,
            defaultTimeZone: utc
        )

        XCTAssertEqual(result.events.map(\.uid), ["good@example.edu"])
        XCTAssertEqual(result.warnings.count, 1)
        XCTAssertEqual(result.warnings.first?.eventUID, "bad@example.edu")
    }

    func testNonCalendarContentProducesClearError() {
        XCTAssertThrowsError(
            try parser.parse(
                "<html>Sign in</html>",
                defaultTimeZone: utc
            )
        ) { error in
            XCTAssertEqual(error as? ICSParserError, .notCalendarData)
        }
    }

    func testUnknownTZIDSkipsOnlyAffectedEvent() throws {
        let text = calendar(
            """
            BEGIN:VEVENT
            UID:unknown-zone@example.edu
            DTSTART;TZID=Moon/Base:20260801T100000
            SUMMARY:Unknown zone
            END:VEVENT
            BEGIN:VEVENT
            UID:valid@example.edu
            DTSTART:20260801T100000Z
            SUMMARY:Valid
            END:VEVENT
            """
        )
        let result = try parser.parseResult(
            text,
            defaultTimeZone: utc
        )

        XCTAssertEqual(result.events.map(\.uid), ["valid@example.edu"])
        XCTAssertEqual(
            result.warnings.first?.eventUID,
            "unknown-zone@example.edu"
        )
        XCTAssertTrue(
            result.warnings.first?.message.contains("not recognized") == true
        )
    }

    private func fixtureData() throws -> Data {
#if SWIFT_PACKAGE
        let bundle = Bundle.module
#else
        let bundle = Bundle(for: Self.self)
#endif
        let fixtureURL = bundle.url(
            forResource: "representative",
            withExtension: "ics",
            subdirectory: "Fixtures"
        ) ?? bundle.url(
            forResource: "representative",
            withExtension: "ics"
        )
        return try Data(contentsOf: XCTUnwrap(fixtureURL))
    }

    private func calendar(_ body: String) -> String {
        """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Canvas Countdown Tests//EN
        \(body)
        END:VCALENDAR
        """
    }

    private func date(
        year: Int,
        month: Int,
        day: Int,
        hour: Int = 0,
        minute: Int = 0,
        timeZone: TimeZone
    ) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar.date(
            from: DateComponents(
                year: year,
                month: month,
                day: day,
                hour: hour,
                minute: minute
            )
        )!
    }
}

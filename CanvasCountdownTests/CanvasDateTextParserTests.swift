import Foundation
import XCTest
@testable import CanvasCountdown

/// Reading deadlines out of OCR text. Every fixture here is invented.
final class CanvasDateTextParserTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private func now(_ year: Int, _ month: Int, _ day: Int) -> Date {
        calendar.date(
            from: DateComponents(year: year, month: month, day: day, hour: 9)
        )!
    }

    private func parse(_ text: String, from reference: Date? = nil) -> ParsedScreenshotDate? {
        CanvasDateTextParser.parse(
            text,
            now: reference ?? now(2026, 7, 28),
            calendar: calendar
        )
    }

    // MARK: - Formats

    func testDayFirstTwentyFourHourFormat() throws {
        let result = try XCTUnwrap(parse("Due 4 Aug at 16:00"))
        let parts = calendar.dateComponents(
            [.year, .month, .day, .hour, .minute],
            from: result.date
        )

        XCTAssertEqual(parts.day, 4)
        XCTAssertEqual(parts.month, 8)
        XCTAssertEqual(parts.hour, 16)
        XCTAssertEqual(parts.minute, 0)
        XCTAssertTrue(result.hasExplicitTime)
    }

    func testMonthFirstTwelveHourFormat() throws {
        let result = try XCTUnwrap(parse("Due Aug 4 at 4:00pm"))
        let parts = calendar.dateComponents([.month, .day, .hour], from: result.date)

        XCTAssertEqual(parts.month, 8)
        XCTAssertEqual(parts.day, 4)
        XCTAssertEqual(parts.hour, 16, "4:00pm is 16:00")
    }

    func testMiddayAndMidnightMeridiem() throws {
        let noon = try XCTUnwrap(parse("Due 4 Aug at 12:00pm"))
        let midnight = try XCTUnwrap(parse("Due 4 Aug at 12:00am"))

        XCTAssertEqual(calendar.component(.hour, from: noon.date), 12)
        XCTAssertEqual(calendar.component(.hour, from: midnight.date), 0)
    }

    func testExplicitYearIsUsed() throws {
        let result = try XCTUnwrap(parse("Due 4 August 2026 at 16:00"))

        XCTAssertEqual(calendar.component(.year, from: result.date), 2026)
        XCTAssertNil(result.inferredYear)
        XCTAssertFalse(result.warnings.contains(.yearInferred(2026)))
    }

    func testLateDeadlinesInTheSameYear() throws {
        for (text, month, day) in [
            ("Due 30 Aug at 23:59", 8, 30),
            ("Due 6 Sep at 23:59", 9, 6),
            ("Due 25 Oct at 23:59", 10, 25),
        ] {
            let result = try XCTUnwrap(parse(text))
            let parts = calendar.dateComponents([.month, .day, .hour, .minute], from: result.date)
            XCTAssertEqual(parts.month, month, text)
            XCTAssertEqual(parts.day, day, text)
            XCTAssertEqual(parts.hour, 23, text)
            XCTAssertEqual(parts.minute, 59, text)
        }
    }

    func testTodayAndTomorrow() throws {
        let reference = now(2026, 7, 28)
        let today = try XCTUnwrap(parse("Due Today at 23:59", from: reference))
        let tomorrow = try XCTUnwrap(parse("Due Tomorrow at 16:00", from: reference))

        XCTAssertEqual(calendar.component(.day, from: today.date), 28)
        XCTAssertEqual(calendar.component(.day, from: tomorrow.date), 29)
        XCTAssertEqual(calendar.component(.hour, from: tomorrow.date), 16)
    }

    // MARK: - Year inference

    func testYearIsInferredForwardAndReported() throws {
        // Read in December, a January deadline belongs to the next year.
        let result = try XCTUnwrap(parse("Due 15 Jan at 23:59", from: now(2026, 12, 20)))

        XCTAssertEqual(calendar.component(.year, from: result.date), 2027)
        XCTAssertEqual(result.inferredYear, 2027)
        XCTAssertTrue(result.warnings.contains(.yearInferred(2027)))
    }

    func testRecentlyPastDateKeepsThisYearAndIsFlagged() throws {
        let result = try XCTUnwrap(parse("Due 20 Jul at 23:59", from: now(2026, 7, 28)))

        XCTAssertEqual(calendar.component(.year, from: result.date), 2026)
        XCTAssertTrue(
            result.warnings.contains(.dateInPast),
            "A date already gone must be shown as such, never quietly moved"
        )
    }

    // MARK: - OCR noise

    func testAtIsRecoveredFromCommonMisreads() throws {
        let result = try XCTUnwrap(parse("Due 4 Aug al 16:00"))

        XCTAssertEqual(calendar.component(.hour, from: result.date), 16)
        XCTAssertNotNil(result.correctedText)
    }

    func testLetterOInsideATimeBecomesZero() throws {
        let result = try XCTUnwrap(parse("Due 4 Aug at 16.O0"))
        let parts = calendar.dateComponents([.hour, .minute], from: result.date)

        XCTAssertEqual(parts.hour, 16)
        XCTAssertEqual(parts.minute, 0)
        XCTAssertTrue(
            result.warnings.contains { warning in
                if case .timeCorrected = warning { return true }
                return false
            },
            "The substitution must be explained, not applied silently"
        )
    }

    func testBareFourDigitTime() throws {
        let result = try XCTUnwrap(parse("Due 4 Aug at 1600"))
        let parts = calendar.dateComponents([.hour, .minute], from: result.date)

        XCTAssertEqual(parts.hour, 16)
        XCTAssertEqual(parts.minute, 0)
    }

    func testRepeatedSpacingIsTolerated() throws {
        let result = try XCTUnwrap(parse("Due  4  Aug  at  16:00"))

        XCTAssertEqual(calendar.component(.hour, from: result.date), 16)
    }

    func testMissingTimeFallsBackToEndOfDayAndSaysSo() throws {
        let result = try XCTUnwrap(parse("Due 4 Aug"))
        let parts = calendar.dateComponents([.hour, .minute], from: result.date)

        XCTAssertEqual(parts.hour, 23)
        XCTAssertEqual(parts.minute, 59)
        XCTAssertFalse(result.hasExplicitTime)
        XCTAssertTrue(result.warnings.contains(.missingTime))
    }

    // MARK: - Availability must never become a deadline

    func testAvailabilityPhrasesAreRefused() {
        for text in [
            "Not available until 4 Aug at 14:40",
            "Available from 1 Aug at 09:00",
            "Available until 30 Aug at 23:59",
            "Opens 2 Aug at 08:00",
        ] {
            XCTAssertNil(
                parse(text),
                "\(text) is an availability window, not a deadline"
            )
            XCTAssertTrue(CanvasDateTextParser.isAvailabilityLine(text), text)
            XCTAssertFalse(CanvasDateTextParser.isDueLine(text), text)
        }
    }

    func testDueLineIsRecognised() {
        XCTAssertTrue(CanvasDateTextParser.isDueLine("Due 4 Aug at 16:00"))
        XCTAssertTrue(CanvasDateTextParser.isDueLine("due today at 23:59"))
        XCTAssertFalse(CanvasDateTextParser.isDueLine("Example Quiz"))
    }

    // MARK: - Invalid input

    func testImpossibleDatesAreRejected() {
        XCTAssertNil(parse("Due 31 Feb at 16:00"), "31 February does not exist")
        XCTAssertNil(parse("Due 4 Aug at 29:00"), "hour 29 does not exist")
        XCTAssertNil(parse("Due 4 Aug at 16:88"), "minute 88 does not exist")
        XCTAssertNil(parse("Due 32 Aug at 16:00"))
    }

    func testTextWithoutADateReturnsNothing() {
        XCTAssertNil(parse("Example Poster Submission"))
        XCTAssertNil(parse("10 pts"))
        XCTAssertNil(parse(""))
    }

    func testRealDateValidation() {
        XCTAssertTrue(CanvasDateTextParser.isRealDate(day: 29, month: 2, year: 2028, calendar: calendar))
        XCTAssertFalse(CanvasDateTextParser.isRealDate(day: 29, month: 2, year: 2026, calendar: calendar))
        XCTAssertFalse(CanvasDateTextParser.isRealDate(day: 31, month: 4, year: 2026, calendar: calendar))
    }

    func testDaylightSavingTransitionUsesTheCalendar() throws {
        // Sydney moves its clocks on 4 October 2026.
        let result = try XCTUnwrap(parse("Due 4 Oct at 23:59", from: now(2026, 9, 1)))
        let parts = calendar.dateComponents([.month, .day, .hour], from: result.date)

        XCTAssertEqual(parts.month, 10)
        XCTAssertEqual(parts.day, 4)
        XCTAssertEqual(parts.hour, 23)
    }
}

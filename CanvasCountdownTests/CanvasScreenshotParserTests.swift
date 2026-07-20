import CoreGraphics
import Foundation
import XCTest
@testable import CanvasCountdown

/// Row grouping and candidate building. All fixtures are invented: no real
/// course, assignment or screenshot appears here.
final class CanvasScreenshotParserTests: XCTestCase {
    private let screenshotID = UUID()
    private let parser = CanvasScreenshotParser()

    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private var referenceNow: Date {
        calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 9))!
    }

    /// Builds an observation at a given vertical position, expressed as
    /// fractions so zoom and Retina scaling are irrelevant.
    private func line(
        _ text: String,
        y: CGFloat,
        x: CGFloat = 0.08,
        width: CGFloat = 0.5,
        height: CGFloat = 0.012,
        confidence: Double = 0.92
    ) -> OCRTextObservation {
        OCRTextObservation(
            text: text,
            confidence: confidence,
            boundingBox: CGRect(x: x, y: y, width: width, height: height),
            screenshotID: screenshotID
        )
    }

    private func parse(_ observations: [OCRTextObservation]) -> [ScreenshotImportCandidate] {
        parser.candidates(
            from: observations,
            imageName: "example-screenshot.png",
            now: referenceNow
        )
    }

    // MARK: - Single assignment

    func testOneAssignmentWithAvailabilityAndDueLines() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10),
            line("Not available until 4 Aug at 14:40", y: 0.125),
            line("Due 4 Aug at 16:00", y: 0.145),
        ])

        XCTAssertEqual(candidates.count, 1)
        let candidate = try XCTUnwrap(candidates.first)
        XCTAssertEqual(candidate.title, "Example Quiz")

        let due = try XCTUnwrap(candidate.dueDate)
        let parts = calendar.dateComponents([.month, .day, .hour, .minute], from: due)
        XCTAssertEqual(parts.month, 8)
        XCTAssertEqual(parts.day, 4)
        XCTAssertEqual(
            parts.hour,
            16,
            "16:00 is the deadline; 14:40 is only when it opens"
        )
        XCTAssertEqual(parts.minute, 0)
    }

    func testAvailabilityLineNeverBecomesTheDeadline() throws {
        let candidates = parse([
            line("Example Poster Submission", y: 0.10),
            line("Not available until 16 Aug at 23:59", y: 0.125),
            line("Due 30 Aug at 23:59", y: 0.145),
        ])

        let due = try XCTUnwrap(candidates.first?.dueDate)
        XCTAssertEqual(
            calendar.component(.day, from: due),
            30,
            "The availability date must never be imported as the deadline"
        )
    }

    // MARK: - Several assignments

    func testSeveralAssignmentsInOneScreenshot() {
        let candidates = parse([
            line("Example Quiz", y: 0.10),
            line("Not available until 4 Aug at 14:40", y: 0.125),
            line("Due 4 Aug at 16:00", y: 0.145),
            line("Example Poster Submission", y: 0.22),
            line("Not available until 16 Aug at 23:59", y: 0.245),
            line("Due 30 Aug at 23:59", y: 0.265),
            line("Example Reflection", y: 0.34),
            line("Due 6 Sep at 23:59", y: 0.365),
        ])

        XCTAssertEqual(candidates.count, 3)
        XCTAssertEqual(
            candidates.map(\.title),
            ["Example Quiz", "Example Poster Submission", "Example Reflection"]
        )
    }

    func testAssignmentsSeparateEvenWhenTightlySpaced() {
        // No large gap between blocks: the split comes from a new title
        // following a due line.
        let candidates = parse([
            line("Example Quiz", y: 0.100),
            line("Due 4 Aug at 16:00", y: 0.120),
            line("Example Reflection", y: 0.140),
            line("Due 6 Sep at 23:59", y: 0.160),
        ])

        XCTAssertEqual(candidates.count, 2)
        XCTAssertEqual(candidates[0].title, "Example Quiz")
        XCTAssertEqual(candidates[1].title, "Example Reflection")
    }

    func testWrappedTitleIsJoined() throws {
        let candidates = parse([
            line("Example Poster Submission With A Very Long", y: 0.10),
            line("Title That Wraps Onto A Second Line", y: 0.118),
            line("Due 30 Aug at 23:59", y: 0.140),
        ])

        XCTAssertEqual(candidates.count, 1)
        XCTAssertEqual(
            try XCTUnwrap(candidates.first).title,
            "Example Poster Submission With A Very Long Title That Wraps Onto A Second Line"
        )
    }

    func testReadingOrderIsLeftToRightWithinARow() throws {
        let candidates = parse([
            line("Second", y: 0.101, x: 0.40, width: 0.2),
            line("First", y: 0.100, x: 0.08, width: 0.2),
            line("Due 4 Aug at 16:00", y: 0.130),
        ])

        XCTAssertEqual(try XCTUnwrap(candidates.first).title, "First Second")
    }

    func testBrowserZoomDoesNotChangeGrouping() {
        // The same layout at a different scale: fractions are unchanged, so the
        // result must be too.
        let tight = parse([
            line("Example Quiz", y: 0.10, height: 0.008),
            line("Due 4 Aug at 16:00", y: 0.115, height: 0.008),
            line("Example Reflection", y: 0.20, height: 0.008),
            line("Due 6 Sep at 23:59", y: 0.215, height: 0.008),
        ])
        let loose = parse([
            line("Example Quiz", y: 0.10, height: 0.02),
            line("Due 4 Aug at 16:00", y: 0.128, height: 0.02),
            line("Example Reflection", y: 0.24, height: 0.02),
            line("Due 6 Sep at 23:59", y: 0.268, height: 0.02),
        ])

        XCTAssertEqual(tight.count, 2)
        XCTAssertEqual(loose.count, 2)
        XCTAssertEqual(tight.map(\.title), loose.map(\.title))
    }

    // MARK: - Page furniture

    func testGlobalPageTextIsIgnored() {
        let candidates = parse([
            line("Upcoming Assignments", y: 0.02),
            line("Show By Date", y: 0.04),
            line("Search", y: 0.05),
            line("Example Quiz", y: 0.10),
            line("Due 4 Aug at 16:00", y: 0.125),
        ])

        XCTAssertEqual(candidates.count, 1)
        XCTAssertEqual(candidates.first?.title, "Example Quiz")
    }

    func testPointValuesAreNotTreatedAsDatesOrTitles() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10),
            line("10 pts", y: 0.122),
            line("Due 4 Aug at 16:00", y: 0.142),
        ])

        let candidate = try XCTUnwrap(candidates.first)
        XCTAssertEqual(candidate.title, "Example Quiz")
        XCTAssertNotNil(candidate.dueDate)
    }

    // MARK: - Warnings

    func testBlockWithoutADueLineIsKeptAndFlagged() throws {
        let candidates = parse([
            line("Example Ungraded Task", y: 0.10),
            line("Not available until 4 Aug at 14:40", y: 0.125),
        ])

        let candidate = try XCTUnwrap(candidates.first)
        XCTAssertNil(candidate.dueDate, "No deadline may be invented")
        XCTAssertTrue(candidate.warnings.contains(.missingDueDate))
        XCTAssertEqual(candidate.status, .invalid)
        XCTAssertFalse(candidate.canBeImported)
    }

    func testTwoDueLinesInOneBlockAreMarkedAmbiguous() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10),
            line("Due 4 Aug at 16:00", y: 0.122),
            line("Due 5 Aug at 16:00", y: 0.140),
        ])

        let candidate = try XCTUnwrap(candidates.first)
        XCTAssertTrue(candidate.warnings.contains(.multipleDueDates))
        XCTAssertEqual(candidate.status, .needsReview)
    }

    func testInferredYearIsCarriedOntoTheCandidate() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10),
            line("Due 4 Aug at 16:00", y: 0.125),
        ])

        let candidate = try XCTUnwrap(candidates.first)
        XCTAssertEqual(candidate.inferredYear, 2026)
        XCTAssertTrue(candidate.warnings.contains(.yearInferred(2026)))
        XCTAssertEqual(candidate.status, .needsReview)
    }

    func testLowConfidenceIsFlagged() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10, confidence: 0.3),
            line("Due 4 Aug at 16:00", y: 0.125, confidence: 0.35),
        ])

        XCTAssertTrue(
            try XCTUnwrap(candidates.first).warnings.contains(.lowConfidence)
        )
    }

    func testOriginalTextIsPreservedForReview() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10),
            line("Due 4 Aug al 16:00", y: 0.125),
        ])

        let candidate = try XCTUnwrap(candidates.first)
        XCTAssertEqual(
            candidate.originalDueText,
            "Due 4 Aug al 16:00",
            "The review screen shows exactly what was recognised"
        )
        XCTAssertNotNil(candidate.correctedOCRText)
    }

    func testBoundingRegionCoversTheWholeBlock() throws {
        let candidates = parse([
            line("Example Quiz", y: 0.10, height: 0.012),
            line("Due 4 Aug at 16:00", y: 0.130, height: 0.012),
        ])

        let region = try XCTUnwrap(candidates.first).boundingRegion
        XCTAssertLessThanOrEqual(region.minY, 0.10)
        XCTAssertGreaterThanOrEqual(region.maxY, 0.142)
    }

    func testEmptyInputProducesNoCandidates() {
        XCTAssertTrue(parse([]).isEmpty)
        XCTAssertTrue(parse([line("Upcoming Assignments", y: 0.02)]).isEmpty)
    }
}

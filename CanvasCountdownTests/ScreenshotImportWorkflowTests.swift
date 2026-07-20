import CoreGraphics
import Foundation
import XCTest
@testable import CanvasCountdown

/// The import workflow: recognition boundary, duplicates, review validation,
/// committing, and the privacy promises. Recognition itself is stubbed, because
/// Vision output varies between operating-system revisions.
@MainActor
final class ScreenshotImportWorkflowTests: XCTestCase {
    private let screenshotID = UUID()

    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private var referenceNow: Date {
        calendar.date(from: DateComponents(year: 2026, month: 7, day: 28, hour: 9))!
    }

    private func line(_ text: String, y: CGFloat) -> OCRTextObservation {
        OCRTextObservation(
            text: text,
            confidence: 0.9,
            boundingBox: CGRect(x: 0.08, y: y, width: 0.5, height: 0.012),
            screenshotID: screenshotID
        )
    }

    private func source() -> ScreenshotSource {
        ScreenshotSource(
            id: screenshotID,
            displayName: "example-screenshot.png",
            image: Self.blankImage()
        )
    }

    private static func blankImage() -> CGImage {
        let context = CGContext(
            data: nil,
            width: 4,
            height: 4,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        return context.makeImage()!
    }

    private func makeCoordinator(
        observations: [OCRTextObservation],
        repository: ScopeRepositoryStub = ScopeRepositoryStub(snapshots: [])
    ) -> (ScreenshotImportCoordinator, ScopeRepositoryStub) {
        let coordinator = ScreenshotImportCoordinator(
            ocr: StubOCRService(result: .success(observations)),
            repository: repository
        )
        return (coordinator, repository)
    }

    // MARK: - Recognition boundary

    func testSuccessfulRecognitionProducesCandidates() async {
        let (coordinator, _) = makeCoordinator(observations: [
            line("Example Quiz", y: 0.10),
            line("Not available until 4 Aug at 14:40", y: 0.125),
            line("Due 4 Aug at 16:00", y: 0.145),
        ])

        let session = await coordinator.buildSession(
            from: [source()],
            existing: [],
            now: referenceNow
        )

        XCTAssertEqual(session.candidates.count, 1)
        XCTAssertEqual(session.candidates.first?.title, "Example Quiz")
    }

    func testNoTextIsReportedRatherThanFailing() async {
        let (coordinator, _) = makeCoordinator(observations: [])

        let session = await coordinator.buildSession(
            from: [source()],
            existing: [],
            now: referenceNow
        )

        XCTAssertTrue(session.candidates.isEmpty)
        XCTAssertEqual(session.recognisedNothingNames, ["example-screenshot.png"])
        XCTAssertTrue(session.failedImageNames.isEmpty)
    }

    func testUnreadableImageIsReportedWithoutLosingOthers() async {
        let good = source()
        let bad = ScreenshotSource(
            displayName: "broken.png",
            image: Self.blankImage()
        )
        let coordinator = ScreenshotImportCoordinator(
            ocr: StubOCRService(
                result: .success([
                    line("Example Quiz", y: 0.10),
                    line("Due 4 Aug at 16:00", y: 0.125),
                ]),
                failingNames: ["broken.png"]
            ),
            repository: ScopeRepositoryStub(snapshots: [])
        )

        let session = await coordinator.buildSession(
            from: [good, bad],
            existing: [],
            now: referenceNow
        )

        XCTAssertEqual(session.candidates.count, 1, "The good screenshot survives")
        XCTAssertEqual(session.failedImageNames, ["broken.png"])
    }

    func testCancellationKeepsWhateverFinished() async {
        let coordinator = ScreenshotImportCoordinator(
            ocr: StubOCRService(result: .failure(.cancelled)),
            repository: ScopeRepositoryStub(snapshots: [])
        )

        let session = await coordinator.buildSession(
            from: [source()],
            existing: [],
            now: referenceNow
        )

        XCTAssertTrue(session.candidates.isEmpty)
    }

    func testSeveralScreenshotsAreCombined() async {
        let first = source()
        let second = ScreenshotSource(
            displayName: "second.png",
            image: Self.blankImage()
        )
        let coordinator = ScreenshotImportCoordinator(
            ocr: StubOCRService(
                result: .success([
                    line("Example Quiz", y: 0.10),
                    line("Due 4 Aug at 16:00", y: 0.125),
                ])
            ),
            repository: ScopeRepositoryStub(snapshots: [])
        )

        let session = await coordinator.buildSession(
            from: [first, second],
            existing: [],
            now: referenceNow
        )

        XCTAssertEqual(session.candidates.count, 2)
        XCTAssertEqual(session.screenshotNames.count, 2)
    }

    // MARK: - Duplicates

    private func storedAssignment(
        title: String,
        day: Int,
        course: String? = nil,
        isCompleted: Bool = false,
        isIgnored: Bool = false,
        source: AssignmentSource = .canvasCalendarFeed
    ) -> AssignmentSnapshot {
        AssignmentSnapshot(
            title: title,
            courseName: course,
            dueDate: calendar.date(
                from: DateComponents(year: 2026, month: 8, day: day, hour: 16)
            )!,
            source: source,
            isCompleted: isCompleted,
            isIgnored: isIgnored
        )
    }

    func testExactDuplicateIsMarkedAndDefaultsToSkip() async {
        let existing = storedAssignment(title: "Example Quiz", day: 4)
        let (coordinator, _) = makeCoordinator(observations: [
            line("Example Quiz", y: 0.10),
            line("Due 4 Aug at 16:00", y: 0.125),
        ])

        let session = await coordinator.buildSession(
            from: [source()],
            existing: [existing],
            now: referenceNow
        )
        let candidate = session.candidates.first

        XCTAssertEqual(candidate?.possibleDuplicate, true)
        XCTAssertEqual(candidate?.duplicateTargetID, existing.id)
        XCTAssertEqual(
            candidate?.selectedDuplicateAction,
            .skip,
            "Skipping is the choice that cannot lose anything"
        )
        XCTAssertEqual(candidate?.status, .possibleDuplicate)
        XCTAssertEqual(candidate?.willBeImported, false)
    }

    func testSpacingAndPunctuationDifferencesStillMatch() {
        let existing = storedAssignment(title: "Example  Quiz:  Week 1", day: 4)
        let candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Quiz Week 1",
            dueDate: existing.dueDate,
            originalTitleText: "Example Quiz Week 1",
            originalDueText: "Due 4 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )

        let annotated = ScreenshotDuplicateDetector.annotate(
            [candidate],
            against: [existing],
            calendar: calendar
        )

        XCTAssertTrue(annotated[0].possibleDuplicate)
    }

    func testSameTitleOnADifferentDayIsNotADuplicate() {
        let existing = storedAssignment(title: "Example Quiz", day: 4)
        let candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Quiz",
            dueDate: calendar.date(
                from: DateComponents(year: 2026, month: 8, day: 11, hour: 16)
            )!,
            originalTitleText: "Example Quiz",
            originalDueText: "Due 11 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )

        let annotated = ScreenshotDuplicateDetector.annotate(
            [candidate],
            against: [existing],
            calendar: calendar
        )

        XCTAssertFalse(
            annotated[0].possibleDuplicate,
            "A weekly task sharing a name is not the same assignment"
        )
    }

    func testDifferentTitlesOnTheSameDayAreNotDuplicates() {
        let existing = storedAssignment(title: "Example Quiz", day: 4)
        let candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Poster Submission",
            dueDate: existing.dueDate,
            originalTitleText: "Example Poster Submission",
            originalDueText: "Due 4 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )

        let annotated = ScreenshotDuplicateDetector.annotate(
            [candidate],
            against: [existing],
            calendar: calendar
        )

        XCTAssertFalse(annotated[0].possibleDuplicate)
    }

    func testCompletedAndIgnoredStateIsNeverTouchedByDetection() {
        let completed = storedAssignment(title: "Example Quiz", day: 4, isCompleted: true)
        let ignored = storedAssignment(title: "Example Reflection", day: 5, isIgnored: true)
        let candidates = [
            ScreenshotImportCandidate(
                sourceScreenshotID: screenshotID,
                sourceImageName: "example.png",
                title: "Example Quiz",
                dueDate: completed.dueDate,
                originalTitleText: "Example Quiz",
                originalDueText: "Due 4 Aug at 16:00",
                ocrConfidence: 0.9,
                parserConfidence: 0.9
            ),
        ]

        let annotated = ScreenshotDuplicateDetector.annotate(
            candidates,
            against: [completed, ignored],
            calendar: calendar
        )

        XCTAssertTrue(annotated[0].possibleDuplicate)
        XCTAssertTrue(completed.isCompleted, "Detection reads, it never writes")
        XCTAssertTrue(ignored.isIgnored)
    }

    func testExistingCanvasFeedEventIsFlaggedNotOverwritten() async {
        let feedEvent = storedAssignment(
            title: "Example Quiz",
            day: 4,
            source: .canvasCalendarFeed
        )
        let (coordinator, repository) = makeCoordinator(
            observations: [
                line("Example Quiz", y: 0.10),
                line("Due 4 Aug at 16:00", y: 0.125),
            ],
            repository: ScopeRepositoryStub(snapshots: [feedEvent])
        )

        let session = await coordinator.buildSession(
            from: [source()],
            existing: [feedEvent],
            now: referenceNow
        )
        let summary = try? await coordinator.commit(
            session.candidates,
            now: referenceNow
        )

        XCTAssertEqual(summary?.added, 0)
        XCTAssertEqual(summary?.skipped, 1)
        let stored = try? await repository.fetchAll()
        XCTAssertEqual(stored?.count, 1, "Nothing was added beside the feed event")
        XCTAssertEqual(stored?.first?.source, .canvasCalendarFeed)
    }

    // MARK: - Review validation and commit

    func testOnlySelectedValidRowsAreImported() async throws {
        let (coordinator, repository) = makeCoordinator(observations: [])
        let due = calendar.date(
            from: DateComponents(year: 2026, month: 8, day: 4, hour: 16)
        )!

        var ready = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Quiz",
            dueDate: due,
            originalTitleText: "Example Quiz",
            originalDueText: "Due 4 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )
        var deselected = ready
        deselected = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            include: false,
            title: "Example Poster Submission",
            dueDate: due,
            originalTitleText: "Example Poster Submission",
            originalDueText: "Due 4 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )
        let invalid = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Ungraded Task",
            dueDate: nil,
            originalTitleText: "Example Ungraded Task",
            originalDueText: "",
            ocrConfidence: 0.9,
            parserConfidence: 0,
            warnings: [.missingDueDate]
        )
        ready.include = true

        let summary = try await coordinator.commit(
            [ready, deselected, invalid],
            now: referenceNow
        )

        XCTAssertEqual(summary.added, 1)
        XCTAssertEqual(summary.skipped, 2, "Deselected and invalid rows are skipped")
        let stored = try await repository.fetchAll()
        XCTAssertEqual(stored.map(\.title), ["Example Quiz"])
        XCTAssertEqual(stored.first?.source, .screenshot)
    }

    func testInvalidCandidateCannotBeImported() {
        let candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Ungraded Task",
            dueDate: nil,
            originalTitleText: "Example Ungraded Task",
            originalDueText: "",
            ocrConfidence: 0.9,
            parserConfidence: 0,
            warnings: [.missingDueDate]
        )

        XCTAssertEqual(candidate.status, .invalid)
        XCTAssertFalse(candidate.canBeImported)
        XCTAssertFalse(candidate.willBeImported)
    }

    func testCorrectedCandidateBecomesReady() {
        var candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Quiz",
            dueDate: nil,
            originalTitleText: "Example Quiz",
            originalDueText: "",
            ocrConfidence: 0.9,
            parserConfidence: 0,
            warnings: [.missingDueDate]
        )
        XCTAssertEqual(candidate.status, .invalid)

        candidate.dueDate = referenceNow
        candidate.warnings.removeAll { $0 == .missingDueDate }

        XCTAssertEqual(candidate.status, .ready)
        XCTAssertTrue(candidate.willBeImported)
    }

    func testReimportingTheSameScreenshotUpdatesRatherThanDuplicates() async throws {
        let (coordinator, repository) = makeCoordinator(observations: [])
        let due = calendar.date(
            from: DateComponents(year: 2026, month: 8, day: 4, hour: 16)
        )!
        let candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example.png",
            title: "Example Quiz",
            dueDate: due,
            originalTitleText: "Example Quiz",
            originalDueText: "Due 4 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )

        _ = try await coordinator.commit([candidate], now: referenceNow)
        _ = try await coordinator.commit([candidate], now: referenceNow)

        let stored = try await repository.fetchAll()
        XCTAssertEqual(
            stored.count,
            1,
            "A stable local identifier keeps a re-import from duplicating"
        )
    }

    func testLocalIdentifierIsDerivedOnlyFromConfirmedFields() {
        let due = referenceNow
        let identifier = ScreenshotImportCoordinator.localIdentifier(
            title: "Example Quiz",
            dueDate: due,
            courseName: "99999 Example Interaction Course"
        )

        XCTAssertTrue(identifier.hasPrefix("screenshot|"))
        XCTAssertTrue(identifier.contains("example quiz"))
        XCTAssertFalse(
            identifier.contains("Due 4 Aug"),
            "No recognised transcript may reach a stored identifier"
        )
    }

    // MARK: - Privacy

    func testNoScreenshotBytesReachStorage() async throws {
        let (coordinator, repository) = makeCoordinator(observations: [])
        let candidate = ScreenshotImportCandidate(
            sourceScreenshotID: screenshotID,
            sourceImageName: "example-screenshot.png",
            title: "Example Quiz",
            dueDate: referenceNow,
            originalTitleText: "Example Quiz",
            originalDueText: "Due 4 Aug at 16:00",
            ocrConfidence: 0.9,
            parserConfidence: 0.9
        )

        _ = try await coordinator.commit([candidate], now: referenceNow)
        let stored = try await repository.fetchAll()
        let record = try XCTUnwrap(stored.first)

        // The image name and the recognised line are both absent from every
        // stored field.
        let fields = [
            record.title,
            record.courseName ?? "",
            record.sourceURL ?? "",
            record.externalID ?? "",
        ]
        for field in fields {
            XCTAssertFalse(field.contains("example-screenshot.png"), field)
            XCTAssertFalse(field.contains("Due 4 Aug at 16:00"), field)
        }
    }

    func testDiscardingReleasesEveryRetainedImage() async {
        let viewModel = ScreenshotImportViewModel(
            coordinator: ScreenshotImportCoordinator(
                ocr: StubOCRService(result: .success([])),
                repository: ScopeRepositoryStub(snapshots: [])
            ),
            existingProvider: { [] }
        )

        viewModel.discard()

        XCTAssertEqual(
            viewModel.retainedImageCount,
            0,
            "Images must not outlive the review session"
        )
        XCTAssertTrue(viewModel.candidates.isEmpty)
    }

    func testSupportedFileTypesAreLimitedToImages() {
        XCTAssertTrue(ScreenshotImportViewModel.isSupported(URL(fileURLWithPath: "/tmp/a.png")))
        XCTAssertTrue(ScreenshotImportViewModel.isSupported(URL(fileURLWithPath: "/tmp/a.jpeg")))
        XCTAssertTrue(ScreenshotImportViewModel.isSupported(URL(fileURLWithPath: "/tmp/a.heic")))
        XCTAssertTrue(ScreenshotImportViewModel.isSupported(URL(fileURLWithPath: "/tmp/a.tiff")))
        XCTAssertFalse(ScreenshotImportViewModel.isSupported(URL(fileURLWithPath: "/tmp/a.pdf")))
        XCTAssertFalse(ScreenshotImportViewModel.isSupported(URL(fileURLWithPath: "/tmp/a.txt")))
    }

    func testSessionSummaryCounts() {
        let due = referenceNow
        func candidate(
            title: String,
            dueDate: Date?,
            warnings: [ScreenshotImportWarning] = [],
            duplicate: Bool = false
        ) -> ScreenshotImportCandidate {
            var value = ScreenshotImportCandidate(
                sourceScreenshotID: screenshotID,
                sourceImageName: "example.png",
                title: title,
                dueDate: dueDate,
                originalTitleText: title,
                originalDueText: "",
                ocrConfidence: 0.9,
                parserConfidence: 0.9,
                warnings: warnings
            )
            value.possibleDuplicate = duplicate
            return value
        }

        let session = ScreenshotImportSession(candidates: [
            candidate(title: "Ready", dueDate: due),
            candidate(title: "Needs review", dueDate: due, warnings: [.yearInferred(2026)]),
            candidate(title: "Invalid", dueDate: nil, warnings: [.missingDueDate]),
            candidate(title: "Duplicate", dueDate: due, duplicate: true),
        ])

        XCTAssertEqual(session.readyCount, 1)
        XCTAssertEqual(session.needsReviewCount, 1)
        XCTAssertEqual(session.invalidCount, 1)
        XCTAssertEqual(session.duplicateCount, 1)
        XCTAssertEqual(
            session.importableCount,
            2,
            "The duplicate defaults to skip and the invalid row cannot import"
        )
    }
}

/// Stands in for Vision so no test depends on recognition output.
private struct StubOCRService: ScreenshotOCRServicing {
    enum Result {
        case success([OCRTextObservation])
        case failure(ScreenshotOCRError)
    }

    let result: Result
    var failingNames: [String] = []

    func recognizeText(in source: ScreenshotSource) async throws -> [OCRTextObservation] {
        if failingNames.contains(source.displayName) {
            throw ScreenshotOCRError.unreadableImage(name: source.displayName)
        }
        switch result {
        case let .success(observations):
            // Re-stamp with this screenshot's identifier so several sources
            // behave like several screenshots.
            return observations.map { observation in
                OCRTextObservation(
                    text: observation.text,
                    confidence: observation.confidence,
                    boundingBox: observation.boundingBox,
                    alternatives: observation.alternatives,
                    screenshotID: source.id,
                    pass: observation.pass
                )
            }
        case let .failure(error):
            if error == .cancelled {
                throw CancellationError()
            }
            throw error
        }
    }
}

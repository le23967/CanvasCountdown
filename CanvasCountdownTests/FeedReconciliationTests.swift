import Foundation
import SwiftData
import XCTest
@testable import CanvasCountdown

/// A single incomplete Canvas response must never destroy stored assignments or
/// the completed and ignored decisions attached to them.
@MainActor
final class FeedReconciliationTests: XCTestCase {
    private let parser = ICSParser()
    private let utc = TimeZone(secondsFromGMT: 0)!

    func testAbsentEventSurvivesOneIncompleteRefresh() async throws {
        let harness = try makeHarness(
            payloads: [fullFeed, partialFeed],
            archiveThreshold: 3
        )

        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)
        _ = try await harness.coordinator.refresh(now: harness.day(1), trigger: .automatic)

        let stored = try await harness.repository.fetchAll()
        XCTAssertEqual(stored.count, 2, "One thin feed must not remove anything")

        let absent = try XCTUnwrap(stored.first { $0.externalID == "beta@example.edu" })
        XCTAssertFalse(absent.isArchived)
        XCTAssertEqual(absent.missingRefreshCount, 1)
        XCTAssertTrue(absent.isMissingFromFeed)

        let present = try XCTUnwrap(stored.first { $0.externalID == "alpha@example.edu" })
        XCTAssertEqual(present.missingRefreshCount, 0)
        XCTAssertEqual(present.lastSeenInFeedAt, harness.day(1))
    }

    func testAbsentEventIsArchivedOnlyAfterThresholdRefreshes() async throws {
        let harness = try makeHarness(
            payloads: [fullFeed, partialFeed, partialFeed, partialFeed],
            archiveThreshold: 3
        )

        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)
        _ = try await harness.coordinator.refresh(now: harness.day(1), trigger: .automatic)
        _ = try await harness.coordinator.refresh(now: harness.day(2), trigger: .automatic)

        let beforeThreshold = try await harness.repository.fetchAll()
        XCTAssertEqual(
            beforeThreshold.count,
            2,
            "Two absences must still be below the archive threshold"
        )

        _ = try await harness.coordinator.refresh(now: harness.day(3), trigger: .automatic)

        let visible = try await harness.repository.fetchAll()
        XCTAssertEqual(visible.map(\.externalID), ["alpha@example.edu"])

        let retained = try await harness.repository.fetchAll(includingArchived: true)
        XCTAssertEqual(retained.count, 2, "Archiving must not delete the row")
        let archived = try XCTUnwrap(
            retained.first { $0.externalID == "beta@example.edu" }
        )
        XCTAssertTrue(archived.isArchived)
        XCTAssertEqual(archived.archivedAt, harness.day(3))
        XCTAssertEqual(archived.title, "Beta task")
    }

    func testArchivingPreservesCompletedAndIgnoredHistory() async throws {
        let harness = try makeHarness(
            payloads: [fullFeed, partialFeed, partialFeed, partialFeed],
            archiveThreshold: 3
        )
        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)

        let stored = try await harness.repository.fetchAll()
        let beta = try XCTUnwrap(stored.first { $0.externalID == "beta@example.edu" })
        try await harness.repository.updateStatus(
            id: beta.id,
            isCompleted: true,
            isIgnored: true,
            now: harness.day(0)
        )

        for day in 1...3 {
            _ = try await harness.coordinator.refresh(
                now: harness.day(day),
                trigger: .automatic
            )
        }

        let retained = try await harness.repository.fetchAll(includingArchived: true)
        let archived = try XCTUnwrap(
            retained.first { $0.externalID == "beta@example.edu" }
        )
        XCTAssertTrue(archived.isArchived)
        XCTAssertTrue(archived.isCompleted)
        XCTAssertTrue(archived.isIgnored)
    }

    func testReturningEventIsRestoredWithItsLocalState() async throws {
        let harness = try makeHarness(
            payloads: [fullFeed, partialFeed, partialFeed, partialFeed, fullFeed],
            archiveThreshold: 3
        )
        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)

        let stored = try await harness.repository.fetchAll()
        let beta = try XCTUnwrap(stored.first { $0.externalID == "beta@example.edu" })
        try await harness.repository.updateStatus(
            id: beta.id,
            isCompleted: true,
            isIgnored: nil,
            now: harness.day(0)
        )

        for day in 1...3 {
            _ = try await harness.coordinator.refresh(
                now: harness.day(day),
                trigger: .automatic
            )
        }
        let archivedVisible = try await harness.repository.fetchAll()
        XCTAssertEqual(archivedVisible.count, 1)

        _ = try await harness.coordinator.refresh(now: harness.day(4), trigger: .automatic)

        let visible = try await harness.repository.fetchAll()
        XCTAssertEqual(visible.count, 2)
        let restored = try XCTUnwrap(
            visible.first { $0.externalID == "beta@example.edu" }
        )
        XCTAssertFalse(restored.isArchived)
        XCTAssertEqual(restored.missingRefreshCount, 0)
        XCTAssertTrue(
            restored.isCompleted,
            "A returning assignment keeps the completion the user recorded"
        )
        XCTAssertEqual(restored.id, beta.id, "The original row is reused")
    }

    func testNetworkFailureNeverCountsAsAbsence() async throws {
        let repository = try makeRepository()
        let fetcher = ScriptedFetcher(
            steps: [
                .success(Data(fullFeed.utf8)),
                .failure(CanvasFeedFetchError.networkFailure(code: -1_009)),
                .failure(CanvasFeedFetchError.responseTooLarge(maximumBytes: 1)),
                .failure(CanvasFeedFetchError.insecureRedirect),
                .failure(CanvasFeedFetchError.HTTPStatus(503)),
            ]
        )
        let harness = try makeHarness(repository: repository, fetcher: fetcher)

        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)

        for day in 1...4 {
            do {
                _ = try await harness.coordinator.refresh(
                    now: harness.day(day),
                    trigger: .automatic
                )
                XCTFail("Expected the refresh to fail")
            } catch {
                // Expected: a failed refresh is not evidence of absence.
            }
        }

        let stored = try await harness.repository.fetchAll()
        XCTAssertEqual(stored.count, 2)
        for assignment in stored {
            XCTAssertEqual(
                assignment.missingRefreshCount,
                0,
                "A failed download says nothing about whether an event still exists"
            )
            XCTAssertFalse(assignment.isArchived)
        }
    }

    func testParserFailureNeverCountsAsAbsence() async throws {
        let harness = try makeHarness(
            payloads: [
                fullFeed,
                "not an ical document",
                "not an ical document",
                "not an ical document",
            ],
            archiveThreshold: 3
        )
        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)

        for day in 1...3 {
            do {
                _ = try await harness.coordinator.refresh(
                    now: harness.day(day),
                    trigger: .automatic
                )
                XCTFail("Expected the refresh to fail")
            } catch {
                // Expected: a failed refresh is not evidence of absence.
            }
        }

        let stored = try await harness.repository.fetchAll()
        XCTAssertEqual(stored.count, 2)
        XCTAssertTrue(stored.allSatisfy { $0.missingRefreshCount == 0 })
    }

    func testValidButEmptyFeedIsTreatedAsSuspiciousRatherThanAuthoritative() async throws {
        let emptyFeed = [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Canvas Countdown Reconciliation Tests//EN",
            "END:VCALENDAR",
        ].joined(separator: "\r\n")
        let harness = try makeHarness(
            payloads: [fullFeed, emptyFeed, emptyFeed, emptyFeed, emptyFeed],
            archiveThreshold: 3
        )

        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)
        for day in 1...4 {
            _ = try await harness.coordinator.refresh(
                now: harness.day(day),
                trigger: .automatic
            )
        }

        let stored = try await harness.repository.fetchAll()
        XCTAssertEqual(
            stored.count,
            2,
            "A feed that describes no events at all must not archive anything"
        )
        XCTAssertTrue(stored.allSatisfy { $0.missingRefreshCount == 0 })
    }

    func testCancellationTombstoneArchivesImmediatelyWithoutDeleting() async throws {
        let cancellationFeed = calendar(
            """
            BEGIN:VEVENT
            UID:alpha@example.edu
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Alpha task [COMP1000]
            END:VEVENT
            BEGIN:VEVENT
            UID:beta@example.edu
            SEQUENCE:2
            STATUS:CANCELLED
            END:VEVENT
            """
        )
        let harness = try makeHarness(
            payloads: [fullFeed, cancellationFeed],
            archiveThreshold: 3
        )

        _ = try await harness.coordinator.refresh(now: harness.day(0), trigger: .manual)
        _ = try await harness.coordinator.refresh(now: harness.day(1), trigger: .automatic)

        let visible = try await harness.repository.fetchAll()
        XCTAssertEqual(visible.map(\.externalID), ["alpha@example.edu"])

        let retained = try await harness.repository.fetchAll(includingArchived: true)
        XCTAssertEqual(
            retained.count,
            2,
            "An explicit cancellation still archives rather than deletes"
        )
        let cancelled = try XCTUnwrap(
            retained.first { $0.externalID == "beta@example.edu" }
        )
        XCTAssertTrue(cancelled.isArchived)
        XCTAssertEqual(cancelled.archivedAt, harness.day(1))
    }

    func testManualAssignmentsAreNeverAffectedByFeedAbsence() async throws {
        let harness = try makeHarness(
            payloads: [fullFeed, partialFeed, partialFeed, partialFeed],
            archiveThreshold: 3
        )
        _ = try await harness.repository.saveManual(
            ManualAssignmentDraft(
                title: "Hand written",
                dueDate: harness.day(9)
            ),
            now: harness.day(0)
        )

        for day in 0...3 {
            _ = try await harness.coordinator.refresh(
                now: harness.day(day),
                trigger: .automatic
            )
        }

        let stored = try await harness.repository.fetchAll()
        let manual = try XCTUnwrap(stored.first { $0.source == .manual })
        XCTAssertFalse(manual.isArchived)
        XCTAssertEqual(manual.missingRefreshCount, 0)
    }

    func testReconciliationOutcomeReportsWhatHappened() async throws {
        let repository = try makeRepository()
        _ = try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: "alpha@example.edu",
                    title: "Alpha task",
                    dueDate: day(0)
                ),
                AssignmentImportRecord(
                    externalID: "beta@example.edu",
                    title: "Beta task",
                    dueDate: day(1)
                ),
            ],
            importedAt: day(0)
        )

        let outcome = try await repository.reconcileCanvasFeed(
            FeedReconciliationRequest(
                activeExternalIDs: ["alpha@example.edu"],
                observedAt: day(1),
                archiveThreshold: 1
            )
        )

        XCTAssertEqual(outcome.confirmedCount, 1)
        XCTAssertEqual(outcome.newlyMissingCount, 1)
        XCTAssertEqual(outcome.archivedCount, 1)
        XCTAssertEqual(outcome.restoredCount, 0)
    }

    // MARK: - Fixtures

    private var fullFeed: String {
        calendar(
            """
            BEGIN:VEVENT
            UID:alpha@example.edu
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Alpha task [COMP1000]
            END:VEVENT
            BEGIN:VEVENT
            UID:beta@example.edu
            DTSTART:20260811T100000Z
            SUMMARY:Assignment: Beta task [COMP1000]
            END:VEVENT
            """
        )
    }

    private var partialFeed: String {
        calendar(
            """
            BEGIN:VEVENT
            UID:alpha@example.edu
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Alpha task [COMP1000]
            END:VEVENT
            """
        )
    }

    private func calendar(_ body: String) -> String {
        [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Canvas Countdown Reconciliation Tests//EN",
            body,
            "END:VCALENDAR",
        ].joined(separator: "\r\n")
    }

    private func makeRepository() throws -> SwiftDataAssignmentRepository {
        let schema = Schema([AssignmentEvent.self])
        let configuration = ModelConfiguration(
            schema: schema,
            isStoredInMemoryOnly: true
        )
        let container = try ModelContainer(
            for: schema,
            configurations: [configuration]
        )
        return SwiftDataAssignmentRepository(modelContainer: container)
    }

    private func makeHarness(
        payloads: [String],
        archiveThreshold: Int
    ) throws -> Harness {
        try makeHarness(
            repository: try makeRepository(),
            fetcher: ScriptedFetcher(
                steps: payloads.map { .success(Data($0.utf8)) }
            ),
            archiveThreshold: archiveThreshold
        )
    }

    private func makeHarness(
        repository: SwiftDataAssignmentRepository,
        fetcher: ScriptedFetcher,
        archiveThreshold: Int = FeedReconciliationRequest.defaultArchiveThreshold
    ) throws -> Harness {
        let feedURL = try XCTUnwrap(
            URL(string: "https://canvas.example.edu/feeds/calendar.ics?feed=private")
        )
        let coordinator = RefreshCoordinator(
            fetcher: fetcher,
            parser: parser,
            repository: repository,
            feedURLStore: ReconciliationFeedURLStore(value: feedURL),
            exclusionStore: ReconciliationExclusionStore(),
            defaultTimeZone: utc,
            missingRefreshArchiveThreshold: archiveThreshold
        )
        return Harness(repository: repository, coordinator: coordinator)
    }

    private func day(_ offset: Int) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = utc
        let base = calendar.date(
            from: DateComponents(year: 2026, month: 7, day: 28, hour: 9)
        ) ?? .now
        return calendar.date(byAdding: .day, value: offset, to: base) ?? base
    }

    private struct Harness {
        let repository: SwiftDataAssignmentRepository
        let coordinator: RefreshCoordinator

        func day(_ offset: Int) -> Date {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = TimeZone(secondsFromGMT: 0)!
            let base = calendar.date(
                from: DateComponents(year: 2026, month: 7, day: 28, hour: 9)
            ) ?? .now
            return calendar.date(byAdding: .day, value: offset, to: base) ?? base
        }
    }
}

private actor ScriptedFetcher: FeedFetching {
    enum Step {
        case success(Data)
        case failure(any Error)
    }

    private let steps: [Step]
    private var nextIndex = 0

    init(steps: [Step]) {
        self.steps = steps
    }

    func fetch(from url: URL) async throws -> Data {
        guard !steps.isEmpty else {
            throw CanvasFeedFetchError.emptyResponse
        }
        let step = steps[min(nextIndex, steps.count - 1)]
        nextIndex += 1
        switch step {
        case let .success(data):
            return data
        case let .failure(error):
            throw error
        }
    }
}

private actor ReconciliationFeedURLStore: FeedURLStoring {
    private var value: URL?

    init(value: URL? = nil) {
        self.value = value
    }

    func saveFeedURL(_ url: URL) async throws {
        value = url
    }

    func loadFeedURL() async throws -> URL? {
        value
    }

    func deleteFeedURL() async throws {
        value = nil
    }
}

private actor ReconciliationExclusionStore: FeedExclusionStoring {
    private var excluded: Set<String> = []

    func loadExcludedUIDs() -> Set<String> {
        excluded
    }

    func saveExcludedUIDs(_ UIDs: Set<String>) {
        excluded = UIDs
    }

    func clearExcludedUIDs() {
        excluded.removeAll()
    }
}

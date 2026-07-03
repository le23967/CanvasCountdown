import Foundation
import XCTest
@testable import CanvasCountdown

@MainActor
final class RefreshCoordinatorTests: XCTestCase {
    func testPreviewExcludesAvailabilityMarkersAndImportsSelection() async throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let assignment = calendarEvent(
            uid: "assignment-1",
            summary: "Assignment: Final report [COMP1000]",
            dueDate: now.addingTimeInterval(86_400)
        )
        let availability = calendarEvent(
            uid: "availability-1",
            summary: "Final report available from Monday",
            dueDate: now.addingTimeInterval(3_600)
        )
        let repository = RepositorySpy()
        let URLStore = FeedURLStoreSpy()
        let coordinator = RefreshCoordinator(
            fetcher: FetcherStub(data: Data("calendar".utf8)),
            parser: ParserStub(events: [availability, assignment]),
            repository: repository,
            feedURLStore: URLStore,
            defaultTimeZone: TimeZone(secondsFromGMT: 0)!
        )
        let feedURL = try XCTUnwrap(
            URL(string: "https://canvas.example.edu/feeds/calendar.ics?token=private")
        )

        let preview = try await coordinator.preview(
            feedURL: feedURL,
            now: now
        )
        XCTAssertEqual(preview.events.map(\.uid), ["assignment-1"])
        XCTAssertEqual(preview.excludedEventCount, 1)

        let result = try await coordinator.importSelected(
            preview.events,
            from: feedURL,
            at: now
        )
        let capturedRecords = await repository.capturedRecords()
        let savedURL = await URLStore.savedURL()

        XCTAssertEqual(result.importResult.insertedCount, 1)
        XCTAssertEqual(capturedRecords.count, 1)
        XCTAssertEqual(capturedRecords.first?.title, "Final report")
        XCTAssertEqual(capturedRecords.first?.courseName, "COMP1000")
        XCTAssertEqual(savedURL, feedURL)
    }

    func testImportDoesNotSaveFeedURLWhenRepositoryMergeFails() async throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let repository = RepositorySpy(shouldFailUpsert: true)
        let URLStore = FeedURLStoreSpy()
        let coordinator = RefreshCoordinator(
            fetcher: FetcherStub(data: Data()),
            parser: ParserStub(events: []),
            repository: repository,
            feedURLStore: URLStore
        )
        let feedURL = try XCTUnwrap(
            URL(string: "https://canvas.example.edu/calendar.ics?token=private")
        )

        do {
            _ = try await coordinator.importSelected(
                [
                    calendarEvent(
                        uid: "assignment-1",
                        summary: "Assignment",
                        dueDate: now.addingTimeInterval(86_400)
                    ),
                ],
                from: feedURL,
                at: now
            )
            XCTFail("Expected the repository failure to be surfaced.")
        } catch {
            XCTAssertEqual(
                error as? RefreshCoordinatorError,
                .refreshFailed
            )
        }

        let savedURL = await URLStore.savedURL()
        XCTAssertNil(savedURL)
    }

    private func calendarEvent(
        uid: String,
        summary: String,
        dueDate: Date
    ) -> ParsedCalendarEvent {
        ParsedCalendarEvent(
            uid: uid,
            summary: summary,
            startDate: dueDate,
            endDate: nil,
            description: nil,
            url: "https://canvas.example.edu/assignments/1",
            lastModified: nil,
            isAllDay: false,
            timeZoneIdentifier: "UTC"
        )
    }
}

private struct FetcherStub: FeedFetching {
    let data: Data

    func fetch(from url: URL) async throws -> Data {
        data
    }
}

private struct ParserStub: ICSParsing {
    let events: [ParsedCalendarEvent]

    func parse(
        _ data: Data,
        defaultTimeZone: TimeZone
    ) throws -> [ParsedCalendarEvent] {
        events
    }
}

private actor FeedURLStoreSpy: FeedURLStoring {
    private var value: URL?

    func saveFeedURL(_ url: URL) async throws {
        value = url
    }

    func loadFeedURL() async throws -> URL? {
        value
    }

    func deleteFeedURL() async throws {
        value = nil
    }

    func savedURL() -> URL? {
        value
    }
}

private actor RepositorySpy: AssignmentRepository {
    private enum Failure: Error {
        case expected
    }

    private let shouldFailUpsert: Bool
    private var records: [AssignmentImportRecord] = []

    init(shouldFailUpsert: Bool = false) {
        self.shouldFailUpsert = shouldFailUpsert
    }

    func fetchAll() async throws -> [AssignmentSnapshot] {
        []
    }

    func upsert(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) async throws -> ImportResult {
        if shouldFailUpsert {
            throw Failure.expected
        }
        self.records = records
        return ImportResult(
            insertedCount: records.count,
            updatedCount: 0,
            unchangedCount: 0
        )
    }

    func saveManual(
        _ draft: ManualAssignmentDraft,
        now: Date
    ) async throws -> AssignmentSnapshot {
        throw Failure.expected
    }

    func updateStatus(
        id: UUID,
        isCompleted: Bool?,
        isIgnored: Bool?,
        now: Date
    ) async throws {}

    func delete(id: UUID) async throws {}

    func deleteAll() async throws {}

    func capturedRecords() -> [AssignmentImportRecord] {
        records
    }
}

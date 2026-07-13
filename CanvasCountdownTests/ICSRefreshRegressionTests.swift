import Foundation
import SwiftData
import XCTest
@testable import CanvasCountdown

@MainActor
final class ICSRefreshRegressionTests: XCTestCase {
    private let parser = ICSParser()
    private let utc = TimeZone(secondsFromGMT: 0)!

    func testParserHandlesCRLFFoldingEscapedTextAndDateVariants() throws {
        let sydney = try XCTUnwrap(TimeZone(identifier: "Australia/Sydney"))
        let feed = [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Canvas Countdown Regression Tests//EN",
            "BEGIN:VEVENT",
            "UID:utc",
            "DTSTART:20260803T063000Z",
            "SUMMARY:Essay\\, phase one\\; final",
            "  and notes",
            "DESCRIPTION:First line\\nSecond line\\, bring notes\\; laptop",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:tzid",
            "DTSTART;TZID=Australia/Sydney:20261004T153000",
            "SUMMARY:TZID deadline",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:all-day",
            "DTSTART;VALUE=DATE:20260805",
            "SUMMARY:All-day deadline",
            "END:VEVENT",
            "END:VCALENDAR",
            "",
        ].joined(separator: "\r\n")

        let events = try parser.parse(feed, defaultTimeZone: sydney)
        let utcEvent = try XCTUnwrap(events.first { $0.uid == "utc" })
        let zonedEvent = try XCTUnwrap(events.first { $0.uid == "tzid" })
        let allDayEvent = try XCTUnwrap(events.first { $0.uid == "all-day" })

        XCTAssertEqual(utcEvent.summary, "Essay, phase one; final and notes")
        XCTAssertEqual(
            utcEvent.description,
            "First line\nSecond line, bring notes; laptop"
        )
        XCTAssertEqual(
            utcEvent.startDate,
            try date(
                year: 2026,
                month: 8,
                day: 3,
                hour: 6,
                minute: 30,
                timeZone: utc
            )
        )
        XCTAssertEqual(
            zonedEvent.startDate,
            try date(
                year: 2026,
                month: 10,
                day: 4,
                hour: 15,
                minute: 30,
                timeZone: sydney
            )
        )
        XCTAssertEqual(zonedEvent.timeZoneIdentifier, "Australia/Sydney")
        XCTAssertEqual(
            allDayEvent.startDate,
            try date(
                year: 2026,
                month: 8,
                day: 5,
                timeZone: sydney
            )
        )
        XCTAssertTrue(allDayEvent.isAllDay)
    }

    func testDuplicateUIDKeepsOnlyNewestRevisionRegardlessOfFeedOrder() throws {
        let feed = calendar(
            """
            BEGIN:VEVENT
            UID:duplicate@example.edu
            SEQUENCE:2
            LAST-MODIFIED:20260728T010000Z
            DTSTART:20260812T100000Z
            SUMMARY:Revised deadline
            END:VEVENT
            BEGIN:VEVENT
            UID:duplicate@example.edu
            SEQUENCE:1
            LAST-MODIFIED:20260720T010000Z
            DTSTART:20260810T100000Z
            SUMMARY:Stale deadline
            END:VEVENT
            """
        )

        let matches = try parser.parse(feed, defaultTimeZone: utc)
            .filter { $0.uid == "duplicate@example.edu" }

        XCTAssertEqual(matches.count, 1)
        XCTAssertEqual(matches.first?.summary, "Revised deadline")
        XCTAssertEqual(
            matches.first?.startDate,
            try date(
                year: 2026,
                month: 8,
                day: 12,
                hour: 10,
                timeZone: utc
            )
        )
    }

    func testCancellationTombstoneWinsOverActiveDuplicateUID() throws {
        let feed = calendar(
            """
            BEGIN:VEVENT
            UID:cancelled-duplicate@example.edu
            SEQUENCE:1
            DTSTART:20260810T100000Z
            SUMMARY:Active stale revision
            END:VEVENT
            BEGIN:VEVENT
            UID:cancelled-duplicate@example.edu
            SEQUENCE:2
            STATUS:CANCELLED
            END:VEVENT
            """
        )

        let events = try parser.parse(feed, defaultTimeZone: utc)

        XCTAssertFalse(
            events.contains { $0.uid == "cancelled-duplicate@example.edu" }
        )
    }

    func testRefreshRemovesPreviouslyImportedCancelledEvent() async throws {
        let feedURL = try secureFeedURL()
        let initialFeed = calendar(
            """
            BEGIN:VEVENT
            UID:cancel-me@example.edu
            SEQUENCE:1
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Withdrawn task [COMP1000]
            END:VEVENT
            """
        )
        let cancellationFeed = calendar(
            """
            BEGIN:VEVENT
            UID:cancel-me@example.edu
            SEQUENCE:2
            STATUS:CANCELLED
            END:VEVENT
            """
        )
        let repository = try makeRepository()
        let fetcher = ICSRegressionSequenceFetcher(
            payloads: [Data(initialFeed.utf8), Data(cancellationFeed.utf8)]
        )
        let coordinator = RefreshCoordinator(
            fetcher: fetcher,
            parser: parser,
            repository: repository,
            feedURLStore: ICSRegressionFeedURLStore(value: feedURL),
            defaultTimeZone: utc
        )
        let firstRefreshDate = try date(
            year: 2026,
            month: 7,
            day: 28,
            timeZone: utc
        )

        _ = try await coordinator.refresh(
            now: firstRefreshDate,
            trigger: .manual
        )
        let initiallyStored = try await repository.fetchAll()
        XCTAssertEqual(initiallyStored.count, 1)

        _ = try await coordinator.refresh(
            now: firstRefreshDate.addingTimeInterval(60),
            trigger: .manual
        )

        let storedAfterCancellation = try await repository.fetchAll()
        XCTAssertTrue(storedAfterCancellation.isEmpty)
    }

    func testOnboardingDeselectionDoesNotReturnOnNextRefresh() async throws {
        let feedURL = try secureFeedURL()
        let feed = calendar(
            """
            BEGIN:VEVENT
            UID:selected@example.edu
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Selected task [COMP1000]
            END:VEVENT
            BEGIN:VEVENT
            UID:deselected@example.edu
            DTSTART:20260811T100000Z
            SUMMARY:Assignment: Deselected task [COMP1000]
            END:VEVENT
            """
        )
        let repository = try makeRepository()
        let URLStore = ICSRegressionFeedURLStore()
        let coordinator = RefreshCoordinator(
            fetcher: ICSRegressionSequenceFetcher(
                payloads: [Data(feed.utf8)]
            ),
            parser: parser,
            repository: repository,
            feedURLStore: URLStore,
            defaultTimeZone: utc
        )
        let now = try date(
            year: 2026,
            month: 7,
            day: 28,
            timeZone: utc
        )
        let preview = try await coordinator.preview(
            feedURL: feedURL,
            now: now
        )
        let selected = try XCTUnwrap(
            preview.events.first { $0.uid == "selected@example.edu" }
        )

        _ = try await coordinator.importSelected(
            [selected],
            from: feedURL,
            at: now
        )
        _ = try await coordinator.refresh(
            now: now.addingTimeInterval(60),
            trigger: .manual
        )

        let stored = try await repository.fetchAll()
        XCTAssertEqual(stored.map(\.externalID), ["selected@example.edu"])
    }

    func testChangedDeadlinePreservesCompletedAndIgnoredState() async throws {
        let feedURL = try secureFeedURL()
        let firstFeed = calendar(
            """
            BEGIN:VEVENT
            UID:stateful@example.edu
            LAST-MODIFIED:20260720T010000Z
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Original task [COMP1000]
            END:VEVENT
            """
        )
        let changedFeed = calendar(
            """
            BEGIN:VEVENT
            UID:stateful@example.edu
            LAST-MODIFIED:20260728T010000Z
            DTSTART:20260812T120000Z
            SUMMARY:Assignment: Revised task [COMP1000]
            END:VEVENT
            """
        )
        let repository = try makeRepository()
        let coordinator = RefreshCoordinator(
            fetcher: ICSRegressionSequenceFetcher(
                payloads: [Data(firstFeed.utf8), Data(changedFeed.utf8)]
            ),
            parser: parser,
            repository: repository,
            feedURLStore: ICSRegressionFeedURLStore(value: feedURL),
            defaultTimeZone: utc
        )
        let firstRefreshDate = try date(
            year: 2026,
            month: 7,
            day: 28,
            timeZone: utc
        )
        _ = try await coordinator.refresh(
            now: firstRefreshDate,
            trigger: .manual
        )
        let originallyStored = try await repository.fetchAll()
        let original = try XCTUnwrap(originallyStored.first)
        try await repository.updateStatus(
            id: original.id,
            isCompleted: true,
            isIgnored: true,
            now: firstRefreshDate.addingTimeInterval(10)
        )

        _ = try await coordinator.refresh(
            now: firstRefreshDate.addingTimeInterval(60),
            trigger: .manual
        )

        let stored = try await repository.fetchAll()
        let revised = try XCTUnwrap(stored.first)
        XCTAssertEqual(stored.count, 1)
        XCTAssertEqual(revised.title, "Revised task")
        XCTAssertEqual(
            revised.dueDate,
            try date(
                year: 2026,
                month: 8,
                day: 12,
                hour: 12,
                timeZone: utc
            )
        )
        XCTAssertTrue(revised.isCompleted)
        XCTAssertTrue(revised.isIgnored)
    }

    func testMalformedRefreshDoesNotMutateExistingAssignments() async throws {
        let feedURL = try secureFeedURL()
        let validFeed = calendar(
            """
            BEGIN:VEVENT
            UID:safe@example.edu
            DTSTART:20260810T100000Z
            SUMMARY:Assignment: Keep me [COMP1000]
            END:VEVENT
            """
        )
        let repository = try makeRepository()
        let coordinator = RefreshCoordinator(
            fetcher: ICSRegressionSequenceFetcher(
                payloads: [
                    Data(validFeed.utf8),
                    Data("<html>Canvas sign-in</html>".utf8),
                ]
            ),
            parser: parser,
            repository: repository,
            feedURLStore: ICSRegressionFeedURLStore(value: feedURL),
            defaultTimeZone: utc
        )
        let now = try date(
            year: 2026,
            month: 7,
            day: 28,
            timeZone: utc
        )
        _ = try await coordinator.refresh(now: now, trigger: .manual)
        let before = try await repository.fetchAll()

        do {
            _ = try await coordinator.refresh(
                now: now.addingTimeInterval(60),
                trigger: .manual
            )
            XCTFail("Expected malformed non-calendar data to be rejected.")
        } catch {
            XCTAssertEqual(error as? ICSParserError, .notCalendarData)
        }

        let after = try await repository.fetchAll()
        XCTAssertEqual(after, before)
    }

    func testDistinctUIDsSharingSourceURLRemainDistinct() async throws {
        let repository = try makeRepository()
        let dueDate = try date(
            year: 2026,
            month: 8,
            day: 10,
            timeZone: utc
        )
        let sharedURL = "https://canvas.example.edu/courses/42"

        let result = try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: "first@example.edu",
                    title: "First assignment",
                    dueDate: dueDate,
                    sourceURL: sharedURL
                ),
                AssignmentImportRecord(
                    externalID: "second@example.edu",
                    title: "Second assignment",
                    dueDate: dueDate.addingTimeInterval(3_600),
                    sourceURL: sharedURL
                ),
            ],
            importedAt: dueDate.addingTimeInterval(-86_400)
        )

        let stored = try await repository.fetchAll()
        XCTAssertEqual(result.insertedCount, 2)
        XCTAssertEqual(result.updatedCount, 0)
        XCTAssertEqual(
            Set(stored.compactMap(\.externalID)),
            Set(["first@example.edu", "second@example.edu"])
        )
    }

    func testRejectedFeedURLAndDiagnosticNeverExposeSecret() async throws {
        let secret = "private-calendar-token"
        let insecureURL = try XCTUnwrap(
            URL(string: "http://canvas.example.edu/calendar.ics?\(secret)")
        )
        let coordinator = RefreshCoordinator(
            fetcher: ICSRegressionSequenceFetcher(
                payloads: [Data(calendar("").utf8)]
            ),
            parser: parser,
            repository: try makeRepository(),
            feedURLStore: ICSRegressionFeedURLStore(),
            defaultTimeZone: utc
        )

        do {
            _ = try await coordinator.preview(
                feedURL: insecureURL,
                now: Date(timeIntervalSince1970: 1_800_000_000)
            )
            XCTFail("Expected an insecure URL to be rejected.")
        } catch {
            XCTAssertEqual(
                error as? FeedURLValidationError,
                .HTTPSRequired
            )
            XCTAssertFalse(error.localizedDescription.contains(secret))
        }

        let diagnosticSnapshot = await coordinator.diagnosticSnapshot()
        let diagnostic = try XCTUnwrap(diagnosticSnapshot)
        XCTAssertFalse(diagnostic.exportText.contains(secret))
        XCTAssertEqual(diagnostic.errorCode, "url.https-required")
    }

    func testOversizedResponseStopsDownloadingBeforeEntireBody() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [
            ICSRegressionStreamingURLProtocol.self,
        ]
        let maximumBytes = 1_024
        let totalBytes = ICSRegressionStreamingURLProtocol.totalBytes
        ICSRegressionStreamingURLProtocol.probe.reset()
        let fetcher = URLSessionCanvasFeedFetcher(
            configuration: configuration,
            maximumResponseSize: maximumBytes
        )
        let url = try XCTUnwrap(
            URL(string: "https://stream.test/oversized-calendar.ics")
        )

        do {
            _ = try await fetcher.fetch(from: url)
            XCTFail("Expected the oversized response to be rejected.")
        } catch {
            XCTAssertEqual(
                error as? CanvasFeedFetchError,
                .responseTooLarge(maximumBytes: maximumBytes)
            )
        }

        XCTAssertLessThan(
            ICSRegressionStreamingURLProtocol.probe.deliveredByteCount,
            totalBytes,
            "The response-size limit must stop transport before the full body is buffered."
        )
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

    private func secureFeedURL() throws -> URL {
        try XCTUnwrap(
            URL(
                string:
                    "https://canvas.example.edu/feeds/calendar.ics?feed=private"
            )
        )
    }

    private func calendar(_ body: String) -> String {
        [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Canvas Countdown Regression Tests//EN",
            body,
            "END:VCALENDAR",
        ].joined(separator: "\r\n")
    }

    private func date(
        year: Int,
        month: Int,
        day: Int,
        hour: Int = 0,
        minute: Int = 0,
        timeZone: TimeZone
    ) throws -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return try XCTUnwrap(
            calendar.date(
                from: DateComponents(
                    year: year,
                    month: month,
                    day: day,
                    hour: hour,
                    minute: minute
                )
            )
        )
    }
}

private actor ICSRegressionSequenceFetcher: FeedFetching {
    enum StubError: Error {
        case noPayload
    }

    private let payloads: [Data]
    private var nextIndex = 0

    init(payloads: [Data]) {
        self.payloads = payloads
    }

    func fetch(from url: URL) async throws -> Data {
        guard !payloads.isEmpty else {
            throw StubError.noPayload
        }
        let index = min(nextIndex, payloads.count - 1)
        nextIndex += 1
        return payloads[index]
    }
}

private actor ICSRegressionFeedURLStore: FeedURLStoring {
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

private final class ICSRegressionStreamingURLProtocol:
    URLProtocol,
    @unchecked Sendable
{
    static let chunkSize = 512
    static let chunkCount = 128
    static let totalBytes = chunkSize * chunkCount
    static let probe = ICSRegressionDeliveryProbe()

    private let deliveryQueue = DispatchQueue(
        label: "CanvasCountdownTests.ICSRegressionStreamingURLProtocol"
    )
    private let stateLock = NSLock()
    private var stopped = false

    override class func canInit(with request: URLRequest) -> Bool {
        request.url?.host == "stream.test"
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    override func startLoading() {
        guard
            let url = request.url,
            let response = HTTPURLResponse(
                url: url,
                statusCode: 200,
                httpVersion: "HTTP/1.1",
                headerFields: [
                    "Content-Type": "text/calendar",
                    "Content-Length": "\(Self.totalBytes)",
                ]
            )
        else {
            client?.urlProtocol(
                self,
                didFailWithError: URLError(.badServerResponse)
            )
            return
        }

        client?.urlProtocol(
            self,
            didReceive: response,
            cacheStoragePolicy: .notAllowed
        )
        deliverChunk(at: 0)
    }

    override func stopLoading() {
        stateLock.lock()
        stopped = true
        stateLock.unlock()
    }

    private func deliverChunk(at index: Int) {
        deliveryQueue.asyncAfter(deadline: .now() + .milliseconds(1)) {
            guard !self.isStopped else {
                return
            }
            guard index < Self.chunkCount else {
                self.client?.urlProtocolDidFinishLoading(self)
                return
            }

            let data = Data(repeating: 0x41, count: Self.chunkSize)
            Self.probe.recordDelivery(byteCount: data.count)
            self.client?.urlProtocol(self, didLoad: data)
            self.deliverChunk(at: index + 1)
        }
    }

    private var isStopped: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return stopped
    }
}

private final class ICSRegressionDeliveryProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var bytes = 0

    var deliveredByteCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return bytes
    }

    func recordDelivery(byteCount: Int) {
        lock.lock()
        bytes += byteCount
        lock.unlock()
    }

    func reset() {
        lock.lock()
        bytes = 0
        lock.unlock()
    }
}

import Foundation
import XCTest
@testable import CanvasCountdown

/// Covers the download path: chunked accumulation, the safety limit, response
/// validation, redirect protection and cancellation.
final class CanvasFeedFetcherTests: XCTestCase {
    override func setUp() {
        super.setUp()
        FeedFetcherURLProtocol.reset()
    }

    override func tearDown() {
        FeedFetcherURLProtocol.reset()
        super.tearDown()
    }

    func testNormalFeedIsDownloadedIntact() async throws {
        let body = Data("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR".utf8)
        FeedFetcherURLProtocol.scenario = .success(chunks: [body])

        let fetched = try await makeFetcher().fetch(from: try feedURL())

        XCTAssertEqual(fetched, body)
    }

    func testBodyIsAccumulatedInDeliveredChunksNotPerByte() async throws {
        let chunkSize = 8_192
        let chunkCount = 32
        let chunks = (0..<chunkCount).map { index in
            Data(repeating: UInt8(65 + index % 26), count: chunkSize)
        }
        FeedFetcherURLProtocol.scenario = .success(chunks: chunks)

        let fetcher = makeFetcher(maximumResponseSize: 4 * 1_048_576)
        let fetched = try await fetcher.fetch(from: try feedURL())

        XCTAssertEqual(fetched.count, chunkSize * chunkCount)
        XCTAssertGreaterThan(fetcher.lastResponseChunkCount, 0)
        XCTAssertLessThanOrEqual(
            fetcher.lastResponseChunkCount,
            chunkCount,
            "The body must be appended in transport chunks, never one per byte"
        )
    }

    func testLargeFeedCompletesWellWithinAPerByteBudget() async throws {
        let chunk = Data(repeating: 0x41, count: 64 * 1_024)
        FeedFetcherURLProtocol.scenario = .success(
            chunks: Array(repeating: chunk, count: 32)
        )

        let fetcher = makeFetcher(maximumResponseSize: 8 * 1_048_576)
        let started = Date()
        let fetched = try await fetcher.fetch(from: try feedURL())
        let elapsed = Date().timeIntervalSince(started)

        XCTAssertEqual(fetched.count, 2 * 1_048_576)
        XCTAssertLessThan(
            elapsed,
            10,
            "Two megabytes must not take a per-byte amount of time"
        )
    }

    func testOversizedStreamIsRejectedAndStopsEarly() async throws {
        let chunk = Data(repeating: 0x41, count: 4_096)
        FeedFetcherURLProtocol.scenario = .success(
            chunks: Array(repeating: chunk, count: 64),
            declaresContentLength: false,
            // A real transport applies backpressure; the stub needs a beat so
            // the cancellation can take effect mid-stream.
            chunkDelay: .milliseconds(1)
        )
        let maximumBytes = 8_192

        do {
            _ = try await makeFetcher(maximumResponseSize: maximumBytes)
                .fetch(from: try feedURL())
            XCTFail("Expected the oversized stream to be rejected")
        } catch {
            XCTAssertEqual(
                error as? CanvasFeedFetchError,
                .responseTooLarge(maximumBytes: maximumBytes)
            )
        }

        XCTAssertLessThan(
            FeedFetcherURLProtocol.deliveredByteCount,
            4_096 * 64,
            "The transfer must stop before the whole body is delivered"
        )
    }

    func testDeclaredContentLengthOverTheLimitIsRejectedBeforeAnyBody() async throws {
        FeedFetcherURLProtocol.scenario = .success(
            chunks: [Data(repeating: 0x41, count: 200_000)]
        )
        let maximumBytes = 1_024

        do {
            _ = try await makeFetcher(maximumResponseSize: maximumBytes)
                .fetch(from: try feedURL())
            XCTFail("Expected the declared size to be rejected")
        } catch {
            XCTAssertEqual(
                error as? CanvasFeedFetchError,
                .responseTooLarge(maximumBytes: maximumBytes)
            )
        }
    }

    func testInvalidHTTPStatusIsReported() async throws {
        FeedFetcherURLProtocol.scenario = .status(503)

        do {
            _ = try await makeFetcher().fetch(from: try feedURL())
            XCTFail("Expected the error status to be reported")
        } catch {
            XCTAssertEqual(error as? CanvasFeedFetchError, .HTTPStatus(503))
        }
    }

    func testEmptyResponseIsReported() async throws {
        FeedFetcherURLProtocol.scenario = .success(chunks: [])

        do {
            _ = try await makeFetcher().fetch(from: try feedURL())
            XCTFail("Expected the empty body to be reported")
        } catch {
            XCTAssertEqual(error as? CanvasFeedFetchError, .emptyResponse)
        }
    }

    func testRedirectToPlainHTTPIsRejected() async throws {
        FeedFetcherURLProtocol.scenario = .redirect(
            to: "http://insecure.test/calendar.ics"
        )

        do {
            _ = try await makeFetcher().fetch(from: try feedURL())
            XCTFail("Expected the insecure redirect to be rejected")
        } catch {
            XCTAssertEqual(error as? CanvasFeedFetchError, .insecureRedirect)
        }
    }

    func testRedirectToAnotherHTTPSAddressIsFollowed() async throws {
        let body = Data("BEGIN:VCALENDAR\r\nEND:VCALENDAR".utf8)
        FeedFetcherURLProtocol.scenario = .redirect(
            to: "https://canvas.example.edu/moved.ics",
            then: body
        )

        let fetched = try await makeFetcher().fetch(from: try feedURL())

        XCTAssertEqual(fetched, body)
    }

    func testNonHTTPSRequestIsRejectedBeforeAnyTransport() async throws {
        let url = try XCTUnwrap(URL(string: "http://canvas.example.edu/feed.ics"))

        do {
            _ = try await makeFetcher().fetch(from: url)
            XCTFail("Expected the plain HTTP request to be rejected")
        } catch {
            XCTAssertEqual(
                error as? FeedURLValidationError,
                .HTTPSRequired
            )
        }
        XCTAssertEqual(FeedFetcherURLProtocol.deliveredByteCount, 0)
    }

    func testNetworkFailureIsReportedWithoutLeakingTheURL() async throws {
        FeedFetcherURLProtocol.scenario = .transportFailure

        do {
            _ = try await makeFetcher().fetch(from: try feedURL())
            XCTFail("Expected the transport failure to be reported")
        } catch let error as CanvasFeedFetchError {
            XCTAssertEqual(error.diagnosticCode, "fetch.url-error.-1001")
            XCTAssertFalse(
                error.localizedDescription.contains("canvas.example.edu")
            )
        }
    }

    func testCancellationStopsTheDownload() async throws {
        let chunk = Data(repeating: 0x41, count: 1_024)
        FeedFetcherURLProtocol.scenario = .success(
            chunks: Array(repeating: chunk, count: 400),
            chunkDelay: .milliseconds(5)
        )
        let fetcher = makeFetcher(maximumResponseSize: 8 * 1_048_576)
        let url = try feedURL()

        let task = Task { try await fetcher.fetch(from: url) }
        try await Task.sleep(for: .milliseconds(60))
        task.cancel()

        do {
            _ = try await task.value
            XCTFail("Expected the cancelled download to throw")
        } catch {
            XCTAssertTrue(
                error is CancellationError,
                "Cancellation must surface as CancellationError, got \(error)"
            )
        }
    }

    // MARK: - Helpers

    private func makeFetcher(
        maximumResponseSize: Int = URLSessionCanvasFeedFetcher.defaultMaximumResponseSize
    ) -> URLSessionCanvasFeedFetcher {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FeedFetcherURLProtocol.self]
        return URLSessionCanvasFeedFetcher(
            configuration: configuration,
            maximumResponseSize: maximumResponseSize
        )
    }

    private func feedURL() throws -> URL {
        try XCTUnwrap(URL(string: "https://canvas.example.edu/feeds/calendar.ics"))
    }
}

/// Scripted transport for the fetcher tests.
private final class FeedFetcherURLProtocol: URLProtocol, @unchecked Sendable {
    enum Scenario {
        case success(
            chunks: [Data],
            declaresContentLength: Bool = true,
            chunkDelay: Duration = .zero
        )
        case status(Int)
        case redirect(to: String, then: Data? = nil)
        case transportFailure
    }

    // Guarded by `stateLock` throughout.
    private static let stateLock = NSLock()
    nonisolated(unsafe) private static var storedScenario = Scenario.success(chunks: [])
    nonisolated(unsafe) private static var delivered = 0
    nonisolated(unsafe) private static var hasRedirected = false

    static var scenario: Scenario {
        get {
            stateLock.lock()
            defer { stateLock.unlock() }
            return storedScenario
        }
        set {
            stateLock.lock()
            storedScenario = newValue
            stateLock.unlock()
        }
    }

    static var deliveredByteCount: Int {
        stateLock.lock()
        defer { stateLock.unlock() }
        return delivered
    }

    static func reset() {
        stateLock.lock()
        storedScenario = .success(chunks: [])
        delivered = 0
        hasRedirected = false
        stateLock.unlock()
    }

    private static func recordDelivery(_ count: Int) {
        stateLock.lock()
        delivered += count
        stateLock.unlock()
    }

    private static func markRedirected() -> Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        guard !hasRedirected else {
            return false
        }
        hasRedirected = true
        return true
    }

    private let queue = DispatchQueue(
        label: "CanvasCountdownTests.FeedFetcherURLProtocol"
    )
    private let cancelLock = NSLock()
    private var isCancelled = false

    override class func canInit(with request: URLRequest) -> Bool {
        guard let host = request.url?.host else {
            return false
        }
        return host.hasSuffix("example.edu") || host.hasSuffix("insecure.test")
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    override func startLoading() {
        guard let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }

        switch Self.scenario {
        case let .success(chunks, declaresContentLength, chunkDelay):
            deliver(
                chunks: chunks,
                declaresContentLength: declaresContentLength,
                chunkDelay: chunkDelay,
                url: url
            )
        case let .status(code):
            respond(statusCode: code, url: url, headers: [:])
            client?.urlProtocolDidFinishLoading(self)
        case let .redirect(destination, body):
            performRedirect(to: destination, body: body, url: url)
        case .transportFailure:
            client?.urlProtocol(self, didFailWithError: URLError(.timedOut))
        }
    }

    override func stopLoading() {
        cancelLock.lock()
        isCancelled = true
        cancelLock.unlock()
    }

    private var cancelled: Bool {
        cancelLock.lock()
        defer { cancelLock.unlock() }
        return isCancelled
    }

    private func respond(
        statusCode: Int,
        url: URL,
        headers: [String: String]
    ) {
        guard let response = HTTPURLResponse(
            url: url,
            statusCode: statusCode,
            httpVersion: "HTTP/1.1",
            headerFields: headers
        ) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    }

    private func deliver(
        chunks: [Data],
        declaresContentLength: Bool,
        chunkDelay: Duration,
        url: URL
    ) {
        var headers = ["Content-Type": "text/calendar"]
        if declaresContentLength {
            headers["Content-Length"] = "\(chunks.reduce(0) { $0 + $1.count })"
        }
        respond(statusCode: 200, url: url, headers: headers)
        send(chunks: chunks, index: 0, delay: chunkDelay)
    }

    private func send(chunks: [Data], index: Int, delay: Duration) {
        let deadline = DispatchTime.now()
            + .nanoseconds(Int(delay.components.attoseconds / 1_000_000_000))
        queue.asyncAfter(deadline: deadline) { [weak self] in
            guard let self, !self.cancelled else {
                return
            }
            guard index < chunks.count else {
                self.client?.urlProtocolDidFinishLoading(self)
                return
            }
            Self.recordDelivery(chunks[index].count)
            self.client?.urlProtocol(self, didLoad: chunks[index])
            self.send(chunks: chunks, index: index + 1, delay: delay)
        }
    }

    private func performRedirect(to destination: String, body: Data?, url: URL) {
        guard
            Self.markRedirected(),
            let target = URL(string: destination),
            let response = HTTPURLResponse(
                url: url,
                statusCode: 302,
                httpVersion: "HTTP/1.1",
                headerFields: ["Location": destination]
            )
        else {
            // The redirected request itself: answer it normally.
            var headers = ["Content-Type": "text/calendar"]
            let payload = body ?? Data("BEGIN:VCALENDAR\r\nEND:VCALENDAR".utf8)
            headers["Content-Length"] = "\(payload.count)"
            respond(statusCode: 200, url: url, headers: headers)
            Self.recordDelivery(payload.count)
            client?.urlProtocol(self, didLoad: payload)
            client?.urlProtocolDidFinishLoading(self)
            return
        }

        client?.urlProtocol(
            self,
            wasRedirectedTo: URLRequest(url: target),
            redirectResponse: response
        )
        client?.urlProtocolDidFinishLoading(self)
    }
}

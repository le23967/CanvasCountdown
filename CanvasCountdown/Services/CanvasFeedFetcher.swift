import Foundation

protocol FeedFetching: Sendable {
    func fetch(from url: URL) async throws -> Data
}

enum CanvasFeedFetchError: LocalizedError, Equatable, Sendable {
    case nonHTTPResponse
    case insecureRedirect
    case HTTPStatus(Int)
    case emptyResponse
    case responseTooLarge(maximumBytes: Int)
    case networkFailure(code: Int?)

    var errorDescription: String? {
        switch self {
        case .nonHTTPResponse:
            "Canvas returned an unexpected response."
        case .insecureRedirect:
            "Canvas redirected the calendar request to an insecure address."
        case let .HTTPStatus(status):
            "Canvas returned an error (HTTP \(status))."
        case .emptyResponse:
            "Canvas returned an empty calendar feed."
        case let .responseTooLarge(maximumBytes):
            "The calendar feed exceeds the \(maximumBytes / 1_048_576) MB safety limit."
        case .networkFailure:
            "The Canvas calendar feed could not be downloaded."
        }
    }

    /// A redacted identifier suitable for diagnostics. The private request URL
    /// and the underlying URLSession error text are intentionally excluded.
    var diagnosticCode: String {
        switch self {
        case .nonHTTPResponse:
            "fetch.non-http-response"
        case .insecureRedirect:
            "fetch.insecure-redirect"
        case let .HTTPStatus(status):
            "fetch.http.\(status)"
        case .emptyResponse:
            "fetch.empty-response"
        case .responseTooLarge:
            "fetch.response-too-large"
        case let .networkFailure(code):
            code.map { "fetch.url-error.\($0)" } ?? "fetch.network-error"
        }
    }
}

/// Downloads a calendar feed over HTTPS, accumulating the body in the chunks
/// URLSession delivers rather than one asynchronous step per byte.
final class URLSessionCanvasFeedFetcher: FeedFetching {
    static let defaultMaximumResponseSize = 10 * 1_048_576

    static var defaultConfiguration: URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 60
        configuration.httpCookieAcceptPolicy = .never
        return configuration
    }

    private let session: URLSession
    private let collector: FeedResponseCollector
    private let maximumResponseSize: Int

    init(
        configuration: URLSessionConfiguration? = nil,
        maximumResponseSize: Int = defaultMaximumResponseSize
    ) {
        let limit = max(1, maximumResponseSize)
        let collector = FeedResponseCollector(maximumResponseSize: limit)

        self.maximumResponseSize = limit
        self.collector = collector
        self.session = URLSession(
            configuration: configuration ?? Self.defaultConfiguration,
            delegate: collector,
            delegateQueue: nil
        )
    }

    deinit {
        session.finishTasksAndInvalidate()
    }

    /// Number of body chunks the last completed download appended. A per-byte
    /// implementation would report one chunk per byte.
    var lastResponseChunkCount: Int {
        collector.lastChunkCount
    }

    func fetch(from url: URL) async throws -> Data {
        let secureURL = try FeedURLValidator.validated(url)
        var request = URLRequest(
            url: secureURL,
            cachePolicy: .reloadIgnoringLocalCacheData,
            timeoutInterval: 30
        )
        request.httpMethod = "GET"
        request.setValue(
            "text/calendar, text/plain;q=0.9, */*;q=0.1",
            forHTTPHeaderField: "Accept"
        )

        let task = session.dataTask(with: request)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                collector.start(task: task, continuation: continuation)
            }
        } onCancel: {
            task.cancel()
        }
    }
}

/// Session delegate that validates the response, appends delivered chunks, and
/// stops the transfer as soon as the safety limit would be exceeded.
private final class FeedResponseCollector:
    NSObject,
    URLSessionDataDelegate,
    @unchecked Sendable
{
    private struct Transfer {
        var data = Data()
        var chunkCount = 0
        var failure: CanvasFeedFetchError?
        var continuation: CheckedContinuation<Data, any Error>?
    }

    private let maximumResponseSize: Int
    private let lock = NSLock()
    private var transfers: [Int: Transfer] = [:]
    private var lastCompletedChunkCount = 0

    init(maximumResponseSize: Int) {
        self.maximumResponseSize = maximumResponseSize
    }

    var lastChunkCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return lastCompletedChunkCount
    }

    func start(
        task: URLSessionDataTask,
        continuation: CheckedContinuation<Data, any Error>
    ) {
        lock.lock()
        var transfer = Transfer()
        transfer.continuation = continuation
        transfers[task.taskIdentifier] = transfer
        lock.unlock()

        task.resume()
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse,
        completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void
    ) {
        guard let HTTPResponse = response as? HTTPURLResponse else {
            fail(dataTask, with: .nonHTTPResponse)
            completionHandler(.cancel)
            return
        }
        guard HTTPResponse.url?.scheme?.lowercased() == "https" else {
            fail(dataTask, with: .insecureRedirect)
            completionHandler(.cancel)
            return
        }
        guard (200...299).contains(HTTPResponse.statusCode) else {
            fail(dataTask, with: .HTTPStatus(HTTPResponse.statusCode))
            completionHandler(.cancel)
            return
        }
        if HTTPResponse.expectedContentLength > maximumResponseSize {
            fail(
                dataTask,
                with: .responseTooLarge(maximumBytes: maximumResponseSize)
            )
            completionHandler(.cancel)
            return
        }

        if HTTPResponse.expectedContentLength > 0 {
            reserveCapacity(
                for: dataTask,
                byteCount: min(
                    Int(HTTPResponse.expectedContentLength),
                    maximumResponseSize
                )
            )
        }
        completionHandler(.allow)
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive data: Data
    ) {
        lock.lock()
        guard var transfer = transfers[dataTask.taskIdentifier] else {
            lock.unlock()
            return
        }
        guard transfer.data.count + data.count <= maximumResponseSize else {
            transfer.failure = .responseTooLarge(
                maximumBytes: maximumResponseSize
            )
            transfers[dataTask.taskIdentifier] = transfer
            lock.unlock()
            dataTask.cancel()
            return
        }

        transfer.data.append(data)
        transfer.chunkCount += 1
        transfers[dataTask.taskIdentifier] = transfer
        lock.unlock()
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        guard request.url?.scheme?.lowercased() == "https" else {
            fail(task, with: .insecureRedirect)
            completionHandler(nil)
            return
        }
        completionHandler(request)
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: (any Error)?
    ) {
        lock.lock()
        guard let transfer = transfers.removeValue(forKey: task.taskIdentifier),
              let continuation = transfer.continuation else {
            lock.unlock()
            return
        }
        lastCompletedChunkCount = transfer.chunkCount
        lock.unlock()

        if let failure = transfer.failure {
            continuation.resume(throwing: failure)
            return
        }
        if let error {
            continuation.resume(throwing: Self.mapped(error))
            return
        }
        guard !transfer.data.isEmpty else {
            continuation.resume(throwing: CanvasFeedFetchError.emptyResponse)
            return
        }
        continuation.resume(returning: transfer.data)
    }

    private static func mapped(_ error: any Error) -> any Error {
        guard let URLError = error as? URLError else {
            return CanvasFeedFetchError.networkFailure(code: nil)
        }
        if URLError.code == .cancelled {
            return CancellationError()
        }
        return CanvasFeedFetchError.networkFailure(code: URLError.errorCode)
    }

    private func fail(_ task: URLSessionTask, with error: CanvasFeedFetchError) {
        lock.lock()
        transfers[task.taskIdentifier]?.failure = error
        lock.unlock()
    }

    private func reserveCapacity(for task: URLSessionTask, byteCount: Int) {
        lock.lock()
        transfers[task.taskIdentifier]?.data.reserveCapacity(byteCount)
        lock.unlock()
    }
}

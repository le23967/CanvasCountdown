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

final class URLSessionCanvasFeedFetcher: FeedFetching {
    static let defaultMaximumResponseSize = 10 * 1_048_576

    private let session: URLSession
    private let maximumResponseSize: Int

    init(
        session: URLSession? = nil,
        maximumResponseSize: Int = defaultMaximumResponseSize
    ) {
        if let session {
            self.session = session
        } else {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
            configuration.timeoutIntervalForRequest = 30
            configuration.timeoutIntervalForResource = 60
            configuration.httpCookieAcceptPolicy = .never
            self.session = URLSession(
                configuration: configuration,
                delegate: HTTPSOnlyRedirectDelegate(),
                delegateQueue: nil
            )
        }
        self.maximumResponseSize = max(1, maximumResponseSize)
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

        do {
            let (bytes, response) = try await session.bytes(for: request)

            guard let HTTPResponse = response as? HTTPURLResponse else {
                throw CanvasFeedFetchError.nonHTTPResponse
            }
            guard HTTPResponse.url?.scheme?.lowercased() == "https" else {
                throw CanvasFeedFetchError.insecureRedirect
            }
            guard (200...299).contains(HTTPResponse.statusCode) else {
                throw CanvasFeedFetchError.HTTPStatus(
                    HTTPResponse.statusCode
                )
            }
            if HTTPResponse.expectedContentLength > maximumResponseSize {
                throw CanvasFeedFetchError.responseTooLarge(
                    maximumBytes: maximumResponseSize
                )
            }

            var data = Data()
            if HTTPResponse.expectedContentLength > 0 {
                data.reserveCapacity(
                    min(
                        Int(HTTPResponse.expectedContentLength),
                        maximumResponseSize
                    )
                )
            }
            for try await byte in bytes {
                guard data.count < maximumResponseSize else {
                    throw CanvasFeedFetchError.responseTooLarge(
                        maximumBytes: maximumResponseSize
                    )
                }
                data.append(byte)
            }
            guard !data.isEmpty else {
                throw CanvasFeedFetchError.emptyResponse
            }
            return data
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as CanvasFeedFetchError {
            throw error
        } catch let error as URLError {
            throw CanvasFeedFetchError.networkFailure(
                code: error.errorCode
            )
        } catch {
            throw CanvasFeedFetchError.networkFailure(code: nil)
        }
    }
}

private final class HTTPSOnlyRedirectDelegate:
    NSObject,
    URLSessionTaskDelegate,
    @unchecked Sendable
{
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        guard request.url?.scheme?.lowercased() == "https" else {
            completionHandler(nil)
            return
        }
        completionHandler(request)
    }
}

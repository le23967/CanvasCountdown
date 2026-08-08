import Foundation

protocol ReleaseChecking: Sendable {
    /// The newest published release, or `nil` when the project has never
    /// published one.
    func latestRelease() async throws -> AppRelease?
}

enum ReleaseCheckError: LocalizedError, Equatable, Sendable {
    case nonHTTPResponse
    case httpStatus(Int)
    case rateLimited
    case unreadableResponse
    case networkFailure(code: Int?)

    var errorDescription: String? {
        switch self {
        case .nonHTTPResponse:
            "The update service returned an unexpected response."
        case let .httpStatus(status):
            "The update check failed (HTTP \(status))."
        case .rateLimited:
            "GitHub is rate limiting update checks. Try again later."
        case .unreadableResponse:
            "The newest release could not be read."
        case .networkFailure:
            "The update check could not reach GitHub."
        }
    }
}

/// Asks GitHub what the newest release is.
///
/// Deliberately unauthenticated: this reads a public list of releases and
/// nothing else, so sending anyone's credentials to do it would be collecting a
/// risk for no gain. Unauthenticated callers get 60 requests an hour, and this
/// asks for one a day.
struct GitHubReleaseChecker: ReleaseChecking {
    /// Where the app publishes. Held here rather than in preferences: pointing
    /// an update check somewhere else is not a setting, it is a compromise.
    static let repository = "le23967/CanvasCountdown"

    private let session: URLSession
    private let repository: String

    init(
        session: URLSession = .shared,
        repository: String = GitHubReleaseChecker.repository
    ) {
        self.session = session
        self.repository = repository
    }

    func latestRelease() async throws -> AppRelease? {
        var request = URLRequest(
            url: URL(
                string: "https://api.github.com/repos/\(repository)/releases/latest"
            )!
        )
        request.setValue(
            "application/vnd.github+json",
            forHTTPHeaderField: "Accept"
        )
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        request.timeoutInterval = 20

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError {
            throw ReleaseCheckError.networkFailure(code: error.errorCode)
        } catch {
            throw ReleaseCheckError.networkFailure(code: nil)
        }

        guard let http = response as? HTTPURLResponse else {
            throw ReleaseCheckError.nonHTTPResponse
        }
        switch http.statusCode {
        case 200:
            break
        case 403, 429:
            throw ReleaseCheckError.rateLimited
        case 404:
            // Nothing published yet. Not an error worth showing anyone.
            return nil
        default:
            throw ReleaseCheckError.httpStatus(http.statusCode)
        }

        return try Self.parse(data)
    }

    /// Reads the fields this app uses, and refuses anything it cannot make a
    /// version out of rather than presenting an update it cannot name.
    static func parse(_ data: Data) throws -> AppRelease? {
        guard let root = try? JSONSerialization.jsonObject(with: data),
              let object = root as? [String: Any] else {
            throw ReleaseCheckError.unreadableResponse
        }
        if object["draft"] as? Bool == true
            || object["prerelease"] as? Bool == true {
            return nil
        }
        guard let tag = object["tag_name"] as? String,
              let version = AppVersion(tag) else {
            throw ReleaseCheckError.unreadableResponse
        }

        let assets = (object["assets"] as? [[String: Any]]) ?? []
        let disk = assets.first { asset in
            ((asset["name"] as? String) ?? "").lowercased().hasSuffix(".dmg")
        }
        let downloadURL = (disk?["browser_download_url"] as? String)
            .flatMap(URL.init(string:))

        let pageURL = (object["html_url"] as? String).flatMap(URL.init(string:))
            ?? URL(string: "https://github.com/\(repository)/releases/latest")!

        return AppRelease(
            version: version,
            tag: tag,
            name: (object["name"] as? String) ?? tag,
            notes: (object["body"] as? String) ?? "",
            downloadURL: downloadURL,
            pageURL: pageURL
        )
    }
}

/// Never reaches the network. Used by automated runs, so no test depends on
/// what happens to be published today.
struct InertReleaseChecker: ReleaseChecking {
    func latestRelease() async throws -> AppRelease? {
        nil
    }
}

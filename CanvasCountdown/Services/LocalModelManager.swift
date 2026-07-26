import Foundation

/// A model already downloaded on this Mac.
struct LocalModel: Identifiable, Equatable, Sendable {
    let name: String
    let sizeBytes: Int64

    var id: String { name }

    var sizeDescription: String {
        ByteCountFormatter.string(fromByteCount: sizeBytes, countStyle: .file)
    }
}

/// A model the app offers to fetch, so nobody has to go hunting on a model
/// hosting site to get started.
struct SuggestedModel: Identifiable, Equatable, Sendable {
    let name: String
    /// Approximate download size. Stated as approximate because the real figure
    /// depends on the quantisation the registry serves.
    let approximateSize: String
    let note: String

    var id: String { name }

    /// Small, generally-available models that behave reasonably on the sort of
    /// short extraction this app asks for. Ordered smallest first, because a
    /// student on a laptop cares about the download.
    static let all: [SuggestedModel] = [
        SuggestedModel(
            name: "llama3.2:3b",
            approximateSize: "about 2 GB",
            note: "Smallest and quickest. Weakest at reading dates."
        ),
        SuggestedModel(
            name: "qwen2.5:7b",
            approximateSize: "about 4.7 GB",
            note: "Good at producing structured answers."
        ),
        SuggestedModel(
            name: "llama3.1:8b",
            approximateSize: "about 4.7 GB",
            note: "A balanced default."
        ),
        SuggestedModel(
            name: "mistral:7b",
            approximateSize: "about 4.1 GB",
            note: "Fast, competent at short instructions."
        ),
    ]
}

enum LocalModelError: LocalizedError, Equatable, Sendable {
    case serverUnreachable
    case requestFailed
    case cancelled

    var errorDescription: String? {
        switch self {
        case .serverUnreachable:
            "Ollama is not responding on this Mac."
        case .requestFailed:
            "That model operation did not complete."
        case .cancelled:
            "The download was cancelled."
        }
    }
}

/// Lists, downloads and removes local models.
///
/// This manages **models**, not Ollama itself. A sandboxed app cannot install or
/// launch another program, so the app detects whether Ollama is running and
/// otherwise shows the two commands to run; everything after that happens here
/// over its local HTTP API, without anyone visiting a model hosting site.
protocol LocalModelManaging: Sendable {
    func isReachable(baseURL: URL) async -> Bool
    func installedModels(baseURL: URL) async throws -> [LocalModel]
    func pull(
        _ name: String,
        baseURL: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws
    func delete(_ name: String, baseURL: URL) async throws
}

actor OllamaModelManager: LocalModelManaging {
    private let session: URLSession

    init(session: URLSession? = nil) {
        if let session {
            self.session = session
        } else {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 15
            // A multi-gigabyte pull needs a long resource budget.
            configuration.timeoutIntervalForResource = 3_600
            self.session = URLSession(configuration: configuration)
        }
    }

    /// The API sits beside the chat endpoint: `.../v1` becomes the root.
    static func apiRoot(from baseURL: URL) -> URL {
        var url = baseURL
        if url.lastPathComponent == "v1" {
            url.deleteLastPathComponent()
        }
        return url
    }

    func isReachable(baseURL: URL) async -> Bool {
        var request = URLRequest(
            url: Self.apiRoot(from: baseURL).appendingPathComponent("api/tags")
        )
        request.timeoutInterval = 3
        guard let (_, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse else {
            return false
        }
        return (200...299).contains(http.statusCode)
    }

    func installedModels(baseURL: URL) async throws -> [LocalModel] {
        let request = URLRequest(
            url: Self.apiRoot(from: baseURL).appendingPathComponent("api/tags")
        )
        guard let (data, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse,
              (200...299).contains(http.statusCode) else {
            throw LocalModelError.serverUnreachable
        }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let models = object["models"] as? [[String: Any]] else {
            throw LocalModelError.requestFailed
        }
        return models.compactMap { entry in
            guard let name = entry["name"] as? String else {
                return nil
            }
            let size = (entry["size"] as? NSNumber)?.int64Value ?? 0
            return LocalModel(name: name, sizeBytes: size)
        }
        .sorted { $0.name < $1.name }
    }

    /// Streams progress so a multi-gigabyte download is not a frozen button.
    func pull(
        _ name: String,
        baseURL: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        var request = URLRequest(
            url: Self.apiRoot(from: baseURL).appendingPathComponent("api/pull")
        )
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(
            withJSONObject: ["model": name, "stream": true]
        )

        do {
            let (bytes, response) = try await session.bytes(for: request)
            guard let http = response as? HTTPURLResponse,
                  (200...299).contains(http.statusCode) else {
                throw LocalModelError.serverUnreachable
            }
            // One JSON object per line, each reporting how far along it is.
            for try await line in bytes.lines {
                try Task.checkCancellation()
                guard let data = line.data(using: .utf8),
                      let object = try? JSONSerialization.jsonObject(with: data)
                        as? [String: Any] else {
                    continue
                }
                if let total = (object["total"] as? NSNumber)?.doubleValue,
                   let completed = (object["completed"] as? NSNumber)?.doubleValue,
                   total > 0 {
                    progress(min(1, completed / total))
                }
                if let error = object["error"] as? String, !error.isEmpty {
                    throw LocalModelError.requestFailed
                }
            }
            progress(1)
        } catch is CancellationError {
            throw LocalModelError.cancelled
        } catch let error as LocalModelError {
            throw error
        } catch {
            throw LocalModelError.serverUnreachable
        }
    }

    func delete(_ name: String, baseURL: URL) async throws {
        var request = URLRequest(
            url: Self.apiRoot(from: baseURL).appendingPathComponent("api/delete")
        )
        request.httpMethod = "DELETE"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(
            withJSONObject: ["model": name]
        )

        guard let (_, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse,
              (200...299).contains(http.statusCode) else {
            throw LocalModelError.requestFailed
        }
    }
}

/// What to run when Ollama is not installed or not started.
///
/// The app cannot do this itself: a sandboxed application may not install or
/// launch another program. Showing the exact commands, ready to copy, is the
/// honest substitute for pretending otherwise.
enum OllamaSetup {
    static let downloadPage = "https://ollama.com/download"
    static let homebrewCommand = "brew install ollama"
    static let serveCommand = "ollama serve"

    static func pullCommand(_ model: String) -> String {
        "ollama pull \(model)"
    }
}

import Foundation
import Security

/// A task the assistant proposed. Never written to storage directly: it goes
/// through the same review the screenshot importer uses.
struct AssistantDraftTask: Identifiable, Equatable, Sendable {
    let id: UUID
    var include: Bool
    var title: String
    var courseName: String?
    var dueDate: Date?
    let sourceText: String

    init(
        id: UUID = UUID(),
        include: Bool = true,
        title: String,
        courseName: String? = nil,
        dueDate: Date?,
        sourceText: String
    ) {
        self.id = id
        self.include = include
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
        self.sourceText = sourceText
    }

    var canBeSaved: Bool {
        dueDate != nil
            && !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

protocol AssistantServicing: Sendable {
    /// A short plain-language summary of the workload. Read-only.
    func summarise(
        _ digests: [AssistantAssignmentDigest],
        now: Date
    ) async throws -> String

    /// Turns a sentence into draft tasks for review. Never saves anything.
    func draftTasks(
        from text: String,
        now: Date
    ) async throws -> [AssistantDraftTask]
}

/// Talks to any chat completions endpoint of the common shape, which covers
/// both a local server such as Ollama and Groq.
actor ChatCompletionsAssistantService: AssistantServicing {
    private let settings: AssistantSettings
    private let apiKey: String?
    private let session: URLSession
    private let calendar: Calendar

    init(
        settings: AssistantSettings,
        apiKey: String?,
        session: URLSession? = nil,
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.settings = settings
        self.apiKey = apiKey
        self.calendar = calendar
        if let session {
            self.session = session
        } else {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 30
            configuration.httpCookieAcceptPolicy = .never
            self.session = URLSession(configuration: configuration)
        }
    }

    func summarise(
        _ digests: [AssistantAssignmentDigest],
        now: Date = .now
    ) async throws -> String {
        guard !digests.isEmpty else {
            return "Nothing is coming up."
        }
        // Only the three agreed fields are ever composed into a prompt.
        let lines = digests
            .sorted { $0.dueDate < $1.dueDate }
            .map { $0.line(now: now, calendar: calendar) }
            .joined(separator: "\n")

        return try await send(
            system: """
            You help a student plan coursework. Be brief and concrete. \
            Never invent deadlines that are not listed.
            """,
            user: """
            Here is what is due:
            \(lines)

            In at most four sentences, say what to start first and why.
            """
        )
    }

    func draftTasks(
        from text: String,
        now: Date = .now
    ) async throws -> [AssistantDraftTask] {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return []
        }

        let dayFormatter = DateFormatter()
        dayFormatter.calendar = calendar
        dayFormatter.timeZone = calendar.timeZone
        dayFormatter.locale = Locale(identifier: "en_US_POSIX")
        dayFormatter.dateFormat = "yyyy-MM-dd"
        let today = dayFormatter.string(from: now)
        let reply = try await send(
            system: """
            Extract tasks with deadlines. Reply with JSON only, as \
            {"tasks":[{"title":"...","course":null,"due":"YYYY-MM-DDTHH:MM"}]}. \
            Today is \(today). Use 23:59 when no time is given. \
            If no deadline is stated, use null for due.
            """,
            user: trimmed
        )
        return Self.parseDrafts(reply, sourceText: trimmed, calendar: calendar)
    }

    /// Reads the model's JSON without trusting it. A malformed reply yields no
    /// drafts rather than a wrong deadline, and a task with no date arrives with
    /// `dueDate` nil so review must supply one.
    static func parseDrafts(
        _ reply: String,
        sourceText: String,
        calendar: Calendar
    ) -> [AssistantDraftTask] {
        guard let start = reply.firstIndex(of: "{"),
              let end = reply.lastIndex(of: "}"),
              start < end else {
            return []
        }
        let json = String(reply[start...end])
        guard let data = json.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tasks = object["tasks"] as? [[String: Any]] else {
            return []
        }

        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm"

        return tasks.compactMap { entry in
            guard let title = (entry["title"] as? String)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
                  !title.isEmpty else {
                return nil
            }
            let course = (entry["course"] as? String)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let due = (entry["due"] as? String).flatMap(formatter.date(from:))
            return AssistantDraftTask(
                title: title,
                courseName: (course?.isEmpty ?? true) ? nil : course,
                dueDate: due,
                sourceText: sourceText
            )
        }
    }

    private func send(system: String, user: String) async throws -> String {
        guard settings.isEnabled else {
            throw AssistantError.notConfigured
        }
        guard let base = settings.resolvedURL else {
            throw AssistantError.invalidEndpoint
        }
        if settings.provider.requiresAPIKey, (apiKey ?? "").isEmpty {
            throw AssistantError.missingAPIKey
        }

        var request = URLRequest(
            url: base.appendingPathComponent("chat/completions")
        )
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let apiKey, !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "model": settings.model,
            "temperature": 0.2,
            "messages": [
                ["role": "system", "content": system],
                ["role": "user", "content": user],
            ],
        ])

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw AssistantError.badResponse
            }
            switch http.statusCode {
            case 200...299:
                break
            case 401, 403:
                throw AssistantError.unauthorised
            case 429:
                throw AssistantError.rateLimited
            default:
                throw AssistantError.badResponse
            }

            guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let choices = object["choices"] as? [[String: Any]],
                  let message = choices.first?["message"] as? [String: Any],
                  let content = message["content"] as? String else {
                throw AssistantError.badResponse
            }
            return content
        } catch let error as AssistantError {
            throw error
        } catch is CancellationError {
            throw AssistantError.cancelled
        } catch {
            throw AssistantError.unreachable
        }
    }
}

/// Stores the assistant API key beside the feed URL, under its own account, so
/// it is never written to preferences or diagnostics.
actor KeychainAssistantKeyStore: Sendable {
    private let service: String
    private let account: String

    init(
        service: String = "com.local.CanvasCountdown",
        account: String = "assistant-api-key"
    ) {
        self.service = service
        self.account = account
    }

    func save(_ key: String) throws {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            try delete()
            return
        }
        guard let data = trimmed.data(using: .utf8) else {
            throw KeychainFeedURLStoreError.textEncodingFailed
        }

        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String:
                kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let status = SecItemUpdate(baseQuery as CFDictionary, attributes as CFDictionary)
        switch status {
        case errSecSuccess:
            return
        case errSecItemNotFound:
            var item = baseQuery
            for (key, value) in attributes {
                item[key] = value
            }
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else {
                throw KeychainFeedURLStoreError.operationFailed(status: addStatus)
            }
        default:
            throw KeychainFeedURLStoreError.operationFailed(status: status)
        }
    }

    func load() throws -> String? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess else {
            throw KeychainFeedURLStoreError.operationFailed(status: status)
        }
        guard let data = result as? Data,
              let value = String(data: data, encoding: .utf8) else {
            throw KeychainFeedURLStoreError.storedValueIsInvalid
        }
        return value
    }

    func delete() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainFeedURLStoreError.operationFailed(status: status)
        }
    }

    private var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
    }
}

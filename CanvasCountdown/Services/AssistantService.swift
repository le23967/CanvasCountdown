import Foundation
import Security

/// A task the assistant proposed. Never written to storage directly: it goes
/// through the same review the screenshot importer uses.
struct AssistantDraftTask: Identifiable, Equatable, Sendable {
    let id: UUID
    var include: Bool
    var title: String
    /// The course, if the sentence named one. Editable during review, because
    /// the model guesses this from a sentence and the user knows.
    var courseName: String?
    /// The user's own label. Never guessed — the model is not given the label
    /// list and cannot set this — but choosing one here saves finding the event
    /// in the list afterwards just to mark it.
    var labelID: UUID?
    var dueDate: Date?
    let sourceText: String

    init(
        id: UUID = UUID(),
        include: Bool = true,
        title: String,
        courseName: String? = nil,
        labelID: UUID? = nil,
        dueDate: Date?,
        sourceText: String
    ) {
        self.id = id
        self.include = include
        self.title = title
        self.courseName = courseName
        self.labelID = labelID
        self.dueDate = dueDate
        self.sourceText = sourceText
    }

    var canBeSaved: Bool {
        dueDate != nil
            && !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

/// One turn of the conversation.
///
/// Codable and dated because the conversation is kept between launches: without
/// a record, there is no way to tell whether something has already been asked,
/// and no way to read the answer again once the window has closed.
struct AssistantMessage: Identifiable, Equatable, Sendable, Codable {
    enum Role: String, Equatable, Sendable, Codable {
        case user
        case assistant
    }

    let id: UUID
    let role: Role
    let text: String
    /// When it was said, so a conversation reopened next week still says when
    /// it happened.
    let date: Date

    init(
        id: UUID = UUID(),
        role: Role,
        text: String,
        date: Date = .now
    ) {
        self.id = id
        self.role = role
        self.text = text
        self.date = date
    }
}

/// Facts the app has worked out itself.
///
/// Counting and date arithmetic are given to the model rather than asked of it.
/// A small local model will happily miscount a list; the app cannot. The model
/// is left to do what it is actually good at, which is judgement and phrasing.
struct AssistantFacts: Equatable, Sendable {
    let total: Int
    let dueThisWeek: Int
    let dueThisMonth: Int
    let overdue: Int
    let nearestTitle: String?
    let nearestInDays: Int?

    init(
        digests: [AssistantAssignmentDigest],
        now: Date,
        calendar: Calendar
    ) {
        let startOfToday = calendar.startOfDay(for: now)
        func days(to date: Date) -> Int {
            calendar.dateComponents(
                [.day],
                from: startOfToday,
                to: calendar.startOfDay(for: date)
            ).day ?? 0
        }

        total = digests.count
        dueThisWeek = digests.filter { (0...7).contains(days(to: $0.dueDate)) }.count
        dueThisMonth = digests.filter { (0...31).contains(days(to: $0.dueDate)) }.count
        overdue = digests.filter { days(to: $0.dueDate) < 0 }.count

        let nearest = digests.min { $0.dueDate < $1.dueDate }
        nearestTitle = nearest?.title
        nearestInDays = nearest.map { days(to: $0.dueDate) }
    }

    var promptLines: String {
        var lines = [
            "Total upcoming: \(total)",
            "Due within 7 days: \(dueThisWeek)",
            "Due within 31 days: \(dueThisMonth)",
        ]
        if overdue > 0 {
            lines.append("Already past their date: \(overdue)")
        }
        if let nearestTitle, let nearestInDays {
            lines.append("Nearest: \(nearestTitle), in \(nearestInDays) days")
        }
        return lines.joined(separator: "\n")
    }
}

protocol AssistantServicing: Sendable {
    /// Answers a question about the workload. Read-only: it changes nothing.
    func answer(
        _ question: String,
        history: [AssistantMessage],
        digests: [AssistantAssignmentDigest],
        now: Date
    ) async throws -> String

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

    /// Changes drafts that already exist, rather than starting again.
    ///
    /// Without this, "put those under 41021" means retyping the sentence that
    /// produced them, and the reply is a fresh guess that loses whatever was
    /// already corrected by hand. The current drafts go with the request, so
    /// the model is editing a list rather than inventing one.
    func reviseDrafts(
        _ drafts: [AssistantDraftTask],
        instruction: String,
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

    func answer(
        _ question: String,
        history: [AssistantMessage],
        digests: [AssistantAssignmentDigest],
        now: Date = .now
    ) async throws -> String {
        let trimmed = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return ""
        }

        let facts = AssistantFacts(digests: digests, now: now, calendar: calendar)
        let lines = digests
            .sorted { $0.dueDate < $1.dueDate }
            .prefix(40)
            .map { $0.line(now: now, calendar: calendar) }
            .joined(separator: "\n")

        // Recent turns only: enough to follow up, not enough to send the whole
        // session every time.
        let recent = history.suffix(6).map { turn in
            [
                "role": turn.role == .user ? "user" : "assistant",
                "content": turn.text,
            ]
        }

        return try await send(
            system: Self.answerInstructions(
                figures: facts.promptLines,
                items: lines
            ),
            user: trimmed,
            history: recent
        )
    }

    /// What the model is told before a question.
    ///
    /// The second paragraph is there because of what the first one did on its
    /// own. Told only that it helps with coursework and must stick to the list,
    /// a model asked to "remind me to do the laundry at one" replied that it
    /// could only manage coursework deadlines — and said so again when told
    /// that it could. Neither was true of the app: the list holds anything
    /// with a date, and the same box adds to it. An answer cannot add
    /// anything, so the honest reply is where to go to do that.
    static func answerInstructions(figures: String, items: String) -> String {
        """
        You are the assistant inside Canvas Countdown, an app that keeps a \
        student's deadlines: coursework imported from Canvas, and anything \
        else they have added themselves, personal tasks included. Answer \
        briefly and concretely, in the language the user writes in. Use only \
        the figures and items given below; the counts are already correct, so \
        do not recount them. Never invent a deadline that is not listed. If \
        something is not in the list, say so.

        This reply cannot change the list. If the user wants something added, \
        scheduled or remembered, do not refuse and do not say the app only \
        handles coursework: tell them to choose "Add task" on the chip under \
        the message box and send the sentence again, and it will be drafted \
        for them to check before anything is saved.

        Figures:
        \(figures)

        Items:
        \(items.isEmpty ? "none" : items)
        """
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

        let reply = try await send(
            system: """
            Extract the tasks in what the user wrote, one for each separate \
            thing to do. They may be coursework or anything personal. Reply \
            with JSON only, as \
            {"tasks":[{"title":"...","course":null,"due":"YYYY-MM-DDTHH:MM"}]}. \
            Keep each title short, in the language the user wrote it in, and \
            leave the date out of it. \(Self.clock(at: now, calendar: calendar)) \
            Give due in that same local time, with no time zone. Use 23:59 \
            when a day is given with no time. When only a part of the day is \
            given, or the user leaves the time to you, choose a sensible \
            hour: 09:00 for morning, 12:00 for noon, 15:00 for afternoon, \
            20:00 for evening or night. If no day or time is stated at all, \
            use null for due.
            """,
            user: trimmed
        )
        return Self.parseDrafts(reply, sourceText: trimmed, calendar: calendar)
    }

    /// What day and time it is, written the way a model can use.
    ///
    /// The weekday is said outright rather than left to be worked out. A date
    /// alone means the model has to know that the nineteenth is a Thursday
    /// before it can find "Friday", and that is exactly the arithmetic these
    /// models get wrong. The time is there for "in two hours" and for "this
    /// evening" said at eleven at night.
    static func clock(at now: Date, calendar: Calendar) -> String {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "EEEE yyyy-MM-dd"
        let day = formatter.string(from: now)
        formatter.dateFormat = "HH:mm"
        return "Today is \(day) and the time is \(formatter.string(from: now))."
    }

    func reviseDrafts(
        _ drafts: [AssistantDraftTask],
        instruction: String,
        now: Date = .now
    ) async throws -> [AssistantDraftTask] {
        let trimmed = instruction
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !drafts.isEmpty else {
            return drafts
        }

        let reply = try await send(
            system: """
            You are editing a list of draft tasks that already exists. Here it \
            is as JSON:

            \(Self.draftsJSON(drafts, calendar: calendar))

            Apply the change the user asks for and reply with JSON only, in \
            that same shape: \
            {"tasks":[{"id":"...","title":"...","course":null,"due":"YYYY-MM-DDTHH:MM"}]}. \
            Return the whole list, not only what changed. Keep the id of every \
            task you are keeping, so edits line up with what is on screen; use \
            a new id only for a task you are adding, and leave a task out only \
            if the user asked for it to go. \(Self.clock(at: now, calendar: calendar)) \
            Give due in that same local time, with no time zone. Use 23:59 \
            when a day is given with no time.
            """,
            user: trimmed
        )
        return Self.parseRevisedDrafts(
            reply,
            revising: drafts,
            calendar: calendar
        )
    }

    /// The drafts as the model will be shown them. Only the three fields it is
    /// allowed to change are included; the label and the tick are the user's
    /// and are never sent or asked about.
    static func draftsJSON(
        _ drafts: [AssistantDraftTask],
        calendar: Calendar
    ) -> String {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm"

        let tasks: [[String: Any]] = drafts.map { draft in
            [
                "id": draft.id.uuidString,
                "title": draft.title,
                "course": draft.courseName ?? NSNull(),
                "due": draft.dueDate.map(formatter.string(from:)) ?? NSNull(),
            ]
        }
        guard let data = try? JSONSerialization.data(
            withJSONObject: ["tasks": tasks]
        ), let json = String(data: data, encoding: .utf8) else {
            return #"{"tasks":[]}"#
        }
        return json
    }

    /// Reads a revision without letting it undo the user's own work.
    ///
    /// A task the model kept keeps the tick and the label that were put on it
    /// here, because the model was never told about either and cannot have
    /// meant to change them. A reply that cannot be read at all returns the
    /// drafts untouched rather than emptying the review.
    static func parseRevisedDrafts(
        _ reply: String,
        revising drafts: [AssistantDraftTask],
        calendar: Calendar
    ) -> [AssistantDraftTask] {
        let sourceText = drafts.first?.sourceText ?? ""
        let parsed = parseDrafts(
            reply,
            sourceText: sourceText,
            calendar: calendar,
            preservingIDs: true
        )
        guard !parsed.isEmpty else {
            return drafts
        }
        let existing = Dictionary(
            drafts.map { ($0.id, $0) },
            uniquingKeysWith: { first, _ in first }
        )
        return parsed.map { task in
            guard let previous = existing[task.id] else {
                return task
            }
            var merged = task
            merged.include = previous.include
            merged.labelID = previous.labelID
            return merged
        }
    }

    /// Whether an `id` in the reply is honoured. Only a revision needs this: a
    /// first draft has nothing to line up with, and trusting an invented id
    /// there could collide with a task the user is already looking at.
    static func parseDrafts(
        _ reply: String,
        sourceText: String,
        calendar: Calendar,
        preservingIDs: Bool
    ) -> [AssistantDraftTask] {
        parseDrafts(
            reply,
            sourceText: sourceText,
            calendar: calendar,
            identifier: preservingIDs
                ? { ($0["id"] as? String).flatMap(UUID.init(uuidString:)) ?? UUID() }
                : { _ in UUID() }
        )
    }

    /// Reads the model's JSON without trusting it. A malformed reply yields no
    /// drafts rather than a wrong deadline, and a task with no date arrives with
    /// `dueDate` nil so review must supply one.
    static func parseDrafts(
        _ reply: String,
        sourceText: String,
        calendar: Calendar
    ) -> [AssistantDraftTask] {
        parseDrafts(
            reply,
            sourceText: sourceText,
            calendar: calendar,
            identifier: { _ in UUID() }
        )
    }

    private static func parseDrafts(
        _ reply: String,
        sourceText: String,
        calendar: Calendar,
        identifier: ([String: Any]) -> UUID
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

        return tasks.compactMap { entry in
            guard let title = (entry["title"] as? String)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
                  !title.isEmpty else {
                return nil
            }
            let course = (entry["course"] as? String)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let due = (entry["due"] as? String).flatMap {
                dueDate(from: $0, calendar: calendar)
            }
            return AssistantDraftTask(
                id: identifier(entry),
                title: title,
                courseName: (course?.isEmpty ?? true) ? nil : course,
                dueDate: due,
                sourceText: sourceText
            )
        }
    }

    /// Reads the date a model wrote, as the day and the clock time it names.
    ///
    /// It is asked for `2026-08-07T17:00` and often sends something close to
    /// it instead: seconds on the end, a `Z`, a space where the `T` should be,
    /// or the day with no time. An exact match threw every one of those away,
    /// and the draft arrived saying "No date was given" about a date that was
    /// sitting in the reply. So this reads the day, then the hour and minute
    /// if they are there, and stops.
    ///
    /// Anything after the minutes is ignored rather than honoured. The model
    /// is told the local date and time and nothing about the time zone, so an
    /// offset on its reply is decoration: treating it as real would move a
    /// one o'clock task by however far this Mac is from Greenwich.
    ///
    /// Still strict where it matters. A day that does not exist, or text that
    /// is not a date at all, yields nothing, and the draft says so.
    static func dueDate(from text: String, calendar: Calendar) -> Date? {
        let scanner = Scanner(
            string: text.trimmingCharacters(in: .whitespacesAndNewlines)
        )
        scanner.charactersToBeSkipped = nil

        func separator() -> Bool {
            scanner.scanString("-") != nil || scanner.scanString("/") != nil
        }
        func number(digits: ClosedRange<Int>) -> Int? {
            guard let run = scanner.scanCharacters(from: .decimalDigits),
                  digits.contains(run.count) else {
                return nil
            }
            return Int(run)
        }

        guard let year = number(digits: 4...4), separator(),
              let month = number(digits: 1...2), separator(),
              let day = number(digits: 1...2) else {
            return nil
        }

        // A day with no time is the end of that day, the same rule the model
        // is given.
        var hour = 23
        var minute = 59
        if !scanner.isAtEnd {
            guard scanner.scanString("T") != nil
                || scanner.scanString("t") != nil
                || scanner.scanCharacters(from: .whitespaces) != nil,
                  let statedHour = number(digits: 1...2),
                  scanner.scanString(":") != nil,
                  let statedMinute = number(digits: 2...2) else {
                return nil
            }
            // A clock written the American way, "5:00 PM", after any seconds.
            // Ignored, it read as five in the morning.
            let rest = scanner.string[scanner.currentIndex...].lowercased()
            if let meridiem = Self.meridiem(in: rest) {
                guard (1...12).contains(statedHour) else {
                    return nil
                }
                hour = statedHour % 12 + (meridiem == "p" ? 12 : 0)
                minute = statedMinute
            } else if statedHour == 24, statedMinute == 0 {
                // 24:00 is how the last moment of a day gets written.
                hour = 23
                minute = 59
            } else {
                hour = statedHour
                minute = statedMinute
            }
        }
        guard (0...23).contains(hour), (0...59).contains(minute) else {
            return nil
        }

        var components = DateComponents()
        components.calendar = calendar
        components.timeZone = calendar.timeZone
        components.year = year
        components.month = month
        components.day = day
        components.hour = hour
        components.minute = minute
        // `date(from:)` rolls the 31st of June over into July. A date that
        // rolled is one the model did not write.
        guard let date = calendar.date(from: components) else {
            return nil
        }
        let read = calendar.dateComponents([.year, .month, .day], from: date)
        guard read.year == year, read.month == month, read.day == day else {
            return nil
        }
        return date
    }

    /// "a" or "p" when what follows the minutes is a meridiem — after
    /// optional seconds and spaces, as "am", "pm", "a.m." or "p.m." — and nil
    /// otherwise.
    private static func meridiem(in rest: String) -> Character? {
        var text = Substring(rest)
        if text.first == ":" {
            text = text.dropFirst().drop(while: { $0.isNumber || $0 == "." })
        }
        text = text.drop(while: { $0 == " " })
        guard let letter = text.first, letter == "a" || letter == "p" else {
            return nil
        }
        let after = text.dropFirst().drop(while: { $0 == "." })
        guard after.first == "m" else {
            return nil
        }
        let end = after.dropFirst().first
        guard end == nil || !(end!.isLetter) else {
            return nil
        }
        return letter
    }

    private func send(
        system: String,
        user: String,
        history: [[String: String]] = []
    ) async throws -> String {
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
            "messages": [["role": "system", "content": system]]
                + history
                + [["role": "user", "content": user]],
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
            return AssistantReplyText.withoutReasoning(content)
        } catch let error as AssistantError {
            throw error
        } catch is CancellationError {
            throw AssistantError.cancelled
        } catch {
            throw AssistantError.unreachable
        }
    }
}

/// Where assistant API keys are kept.
///
/// A protocol so that a launch which must not touch the real Keychain — an
/// automated run, or the suite itself — can be handed somewhere else to put
/// them. `nil` addresses the single key earlier versions stored.
protocol AssistantKeyStoring: Sendable {
    func save(_ key: String, for profileID: UUID?) async throws
    func load(for profileID: UUID?) async throws -> String?
    func delete(for profileID: UUID?) async throws
    func adoptLegacyKey(as profileID: UUID) async throws
}

extension KeychainAssistantKeyStore: AssistantKeyStoring {}

/// Stores assistant API keys beside the feed URL, so none is ever written to
/// preferences or diagnostics.
///
/// One key per saved model, under an account derived from that model's id.
/// Keeping them apart is what lets someone hold an OpenAI key and a Groq key at
/// once and switch between them, rather than pasting one over the other. A nil
/// id addresses the single account earlier versions used, which is what makes
/// the move to named models invisible to anyone upgrading.
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

    func save(_ key: String, for profileID: UUID? = nil) throws {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            try delete(for: profileID)
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
        let query = baseQuery(for: profileID)
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        switch status {
        case errSecSuccess:
            return
        case errSecItemNotFound:
            var item = query
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

    func load(for profileID: UUID? = nil) throws -> String? {
        var query = baseQuery(for: profileID)
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

    func delete(for profileID: UUID? = nil) throws {
        let status = SecItemDelete(baseQuery(for: profileID) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainFeedURLStoreError.operationFailed(status: status)
        }
    }

    /// Moves the single key earlier versions stored onto a named model, then
    /// takes the old item away so there is one home for it rather than two.
    func adoptLegacyKey(as profileID: UUID) throws {
        guard let legacy = try load(for: nil), !legacy.isEmpty else {
            return
        }
        try save(legacy, for: profileID)
        try delete(for: nil)
    }

    private func account(for profileID: UUID?) -> String {
        guard let profileID else {
            return account
        }
        return "\(account).\(profileID.uuidString)"
    }

    private func baseQuery(for profileID: UUID?) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account(for: profileID),
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
    }
}

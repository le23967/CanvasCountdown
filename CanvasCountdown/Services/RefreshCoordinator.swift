import Foundation

struct FeedPreview: Equatable, Sendable {
    var events: [ParsedCalendarEvent]
    var fetchedAt: Date
    var receivedByteCount: Int
    var excludedEventCount: Int
    var activeExternalIDs: Set<String>
}

enum RefreshTrigger: String, Codable, Sendable {
    case launch
    case manual
    case automatic
    case onboarding
}

struct RefreshResult: Equatable, Sendable {
    var importResult: ImportResult
    var importedEventCount: Int
    var refreshedAt: Date
}

struct FeedRefreshDiagnostic: Equatable, Sendable {
    enum Outcome: String, Codable, Sendable {
        case success
        case failure
    }

    var attemptedAt: Date
    var trigger: RefreshTrigger
    var outcome: Outcome
    var receivedByteCount: Int?
    var parsedEventCount: Int?
    var eligibleEventCount: Int?
    var insertedCount: Int?
    var updatedCount: Int?
    var unchangedCount: Int?
    var errorCode: String?

    /// A line-based, deliberately secret-free diagnostic summary.
    var exportText: String {
        var lines = [
            "attemptedAt=\(attemptedAt.ISO8601Format())",
            "trigger=\(trigger.rawValue)",
            "outcome=\(outcome.rawValue)",
        ]
        if let receivedByteCount {
            lines.append("receivedBytes=\(receivedByteCount)")
        }
        if let parsedEventCount {
            lines.append("parsedEvents=\(parsedEventCount)")
        }
        if let eligibleEventCount {
            lines.append("eligibleEvents=\(eligibleEventCount)")
        }
        if let insertedCount {
            lines.append("inserted=\(insertedCount)")
        }
        if let updatedCount {
            lines.append("updated=\(updatedCount)")
        }
        if let unchangedCount {
            lines.append("unchanged=\(unchangedCount)")
        }
        if let errorCode {
            lines.append("error=\(errorCode)")
        }
        return lines.joined(separator: "\n")
    }
}

protocol FeedRefreshCoordinating: Sendable {
    func preview(feedURL: URL, now: Date) async throws -> FeedPreview
    func preview(now: Date) async throws -> FeedPreview

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date
    ) async throws -> RefreshResult

    func refresh(
        now: Date,
        trigger: RefreshTrigger
    ) async throws -> RefreshResult

    func diagnosticSnapshot() async -> FeedRefreshDiagnostic?
    func clearImportSelections() async
}

extension FeedRefreshCoordinating {
    func clearImportSelections() async {}
}

enum RefreshCoordinatorError: LocalizedError, Equatable, Sendable {
    case noSavedFeedURL
    case noEventsSelected
    case operationAlreadyInProgress
    case refreshFailed

    var errorDescription: String? {
        switch self {
        case .noSavedFeedURL:
            "Add your Canvas calendar feed in Settings first."
        case .noEventsSelected:
            "Select at least one assignment to import."
        case .operationAlreadyInProgress:
            "A calendar refresh is already in progress."
        case .refreshFailed:
            "The calendar refresh could not be completed."
        }
    }
}

actor RefreshCoordinator: FeedRefreshCoordinating {
    private let fetcher: any FeedFetching
    private let parser: any ICSParsing
    private let repository: any AssignmentRepository
    private let feedURLStore: any FeedURLStoring
    private let exclusionStore: any FeedExclusionStoring
    private let defaultTimeZone: TimeZone
    private let recentlyOverdueDayLimit: Int

    private var operationInProgress = false
    private var lastDiagnostic: FeedRefreshDiagnostic?
    private var lastPreviewEvents: [ParsedCalendarEvent] = []
    private var lastPreviewActiveExternalIDs: Set<String> = []

    init(
        fetcher: any FeedFetching,
        parser: any ICSParsing,
        repository: any AssignmentRepository,
        feedURLStore: any FeedURLStoring,
        exclusionStore: any FeedExclusionStoring =
            UserDefaultsFeedExclusionStore(),
        defaultTimeZone: TimeZone = .autoupdatingCurrent,
        recentlyOverdueDayLimit: Int = 30
    ) {
        self.fetcher = fetcher
        self.parser = parser
        self.repository = repository
        self.feedURLStore = feedURLStore
        self.exclusionStore = exclusionStore
        self.defaultTimeZone = defaultTimeZone
        self.recentlyOverdueDayLimit = max(0, recentlyOverdueDayLimit)
    }

    func preview(
        feedURL: URL,
        now: Date = .now
    ) async throws -> FeedPreview {
        try beginOperation()
        defer { operationInProgress = false }

        do {
            let preview = try await loadPreview(feedURL: feedURL, now: now)
            remember(preview)
            lastDiagnostic = Self.previewDiagnostic(
                preview,
                trigger: .onboarding
            )
            return preview
        } catch {
            lastDiagnostic = Self.failureDiagnostic(
                at: now,
                trigger: .onboarding,
                error: error
            )
            throw Self.presentationSafe(error)
        }
    }

    func preview(now: Date = .now) async throws -> FeedPreview {
        try beginOperation()
        defer { operationInProgress = false }

        do {
            guard let feedURL = try await feedURLStore.loadFeedURL() else {
                throw RefreshCoordinatorError.noSavedFeedURL
            }
            let preview = try await loadPreview(feedURL: feedURL, now: now)
            remember(preview)
            lastDiagnostic = Self.previewDiagnostic(
                preview,
                trigger: .manual
            )
            return preview
        } catch {
            lastDiagnostic = Self.failureDiagnostic(
                at: now,
                trigger: .manual,
                error: error
            )
            throw Self.presentationSafe(error)
        }
    }

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date = .now
    ) async throws -> RefreshResult {
        try beginOperation()
        defer { operationInProgress = false }

        do {
            guard !events.isEmpty else {
                throw RefreshCoordinatorError.noEventsSelected
            }
            let secureURL = try FeedURLValidator.validated(feedURL)
            let previouslySavedURL = try await feedURLStore.loadFeedURL()
            let existingExclusions: Set<String>
            if previouslySavedURL == secureURL {
                existingExclusions =
                    await exclusionStore.loadExcludedUIDs()
            } else {
                existingExclusions = []
            }
            let selectedUIDs = Self.externalIDs(in: events)
            let previewUIDs = Self.externalIDs(
                in: lastPreviewEvents.isEmpty ? events : lastPreviewEvents
            )
            let exclusions = existingExclusions
                .union(previewUIDs.subtracting(selectedUIDs))
                .subtracting(selectedUIDs)

            let importResult = try await repository.upsert(
                events.map(Self.importRecord),
                importedAt: date
            )
            let activeExternalIDs =
                lastPreviewActiveExternalIDs.isEmpty
                ? selectedUIDs
                : lastPreviewActiveExternalIDs
            try await repository.reconcileCanvasFeed(
                activeExternalIDs: activeExternalIDs,
                excludedExternalIDs: exclusions
            )

            // The onboarding URL is persisted only after the selected preview
            // entries have been imported successfully.
            try await feedURLStore.saveFeedURL(secureURL)
            await exclusionStore.saveExcludedUIDs(exclusions)

            let result = RefreshResult(
                importResult: importResult,
                importedEventCount: events.count,
                refreshedAt: date
            )
            lastDiagnostic = Self.importDiagnostic(
                result,
                trigger: .onboarding,
                receivedByteCount: nil,
                parsedEventCount: events.count
            )
            return result
        } catch {
            lastDiagnostic = Self.failureDiagnostic(
                at: date,
                trigger: .onboarding,
                error: error
            )
            throw Self.presentationSafe(error)
        }
    }

    func refresh(
        now: Date = .now,
        trigger: RefreshTrigger = .manual
    ) async throws -> RefreshResult {
        try beginOperation()
        defer { operationInProgress = false }

        do {
            guard let feedURL = try await feedURLStore.loadFeedURL() else {
                throw RefreshCoordinatorError.noSavedFeedURL
            }
            let preview = try await loadPreview(feedURL: feedURL, now: now)
            let exclusions = await exclusionStore.loadExcludedUIDs()
            let importableEvents = preview.events.filter { event in
                guard let uid = event.uid else {
                    return true
                }
                return !exclusions.contains(Self.normalizedUID(uid))
            }
            let importResult = try await repository.upsert(
                importableEvents.map(Self.importRecord),
                importedAt: now
            )
            try await repository.reconcileCanvasFeed(
                activeExternalIDs: preview.activeExternalIDs,
                excludedExternalIDs: exclusions
            )
            let result = RefreshResult(
                importResult: importResult,
                importedEventCount: importableEvents.count,
                refreshedAt: now
            )
            lastDiagnostic = Self.importDiagnostic(
                result,
                trigger: trigger,
                receivedByteCount: preview.receivedByteCount,
                parsedEventCount:
                    preview.events.count + preview.excludedEventCount
            )
            return result
        } catch {
            lastDiagnostic = Self.failureDiagnostic(
                at: now,
                trigger: trigger,
                error: error
            )
            throw Self.presentationSafe(error)
        }
    }

    func diagnosticSnapshot() async -> FeedRefreshDiagnostic? {
        lastDiagnostic
    }

    func clearImportSelections() async {
        await exclusionStore.clearExcludedUIDs()
    }

    private func beginOperation() throws {
        guard !operationInProgress else {
            throw RefreshCoordinatorError.operationAlreadyInProgress
        }
        operationInProgress = true
    }

    private func loadPreview(
        feedURL: URL,
        now: Date
    ) async throws -> FeedPreview {
        let secureURL = try FeedURLValidator.validated(feedURL)
        let data = try await fetcher.fetch(from: secureURL)
        let parseResult = try parser.parseResult(
            data,
            defaultTimeZone: defaultTimeZone
        )
        let parsedEvents = parseResult.events
        var cutoffCalendar = Calendar(identifier: .gregorian)
        cutoffCalendar.timeZone = defaultTimeZone
        let cutoff = cutoffCalendar.date(
            byAdding: .day,
            value: -recentlyOverdueDayLimit,
            to: now
        ) ?? now
        let activeEvents = parsedEvents.filter {
            !Self.isAvailabilityMarker($0)
        }
        let eligibleEvents = activeEvents
            .filter { $0.startDate >= cutoff }
            .sorted {
                if $0.startDate != $1.startDate {
                    return $0.startDate < $1.startDate
                }
                return $0.summary.localizedStandardCompare($1.summary)
                    == .orderedAscending
            }

        return FeedPreview(
            events: eligibleEvents,
            fetchedAt: now,
            receivedByteCount: data.count,
            excludedEventCount:
                parsedEvents.count - eligibleEvents.count,
            activeExternalIDs: Self.externalIDs(in: activeEvents)
        )
    }

    private func remember(_ preview: FeedPreview) {
        lastPreviewEvents = preview.events
        lastPreviewActiveExternalIDs = preview.activeExternalIDs
    }

    private static func externalIDs(
        in events: [ParsedCalendarEvent]
    ) -> Set<String> {
        Set(events.compactMap(\.uid).map(normalizedUID))
    }

    private static func normalizedUID(_ UID: String) -> String {
        UID.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
    }

    private static func importRecord(
        _ event: ParsedCalendarEvent
    ) -> AssignmentImportRecord {
        let metadata = assignmentMetadata(from: event.summary)
        return AssignmentImportRecord(
            externalID: event.uid,
            title: metadata.title,
            courseName: metadata.courseName,
            dueDate: event.startDate,
            source: .canvasCalendarFeed,
            sourceURL: event.url
        )
    }

    private static func assignmentMetadata(
        from summary: String
    ) -> (title: String, courseName: String?) {
        var title = summary.trimmingCharacters(in: .whitespacesAndNewlines)
        if title.lowercased().hasPrefix("assignment:") {
            title = String(title.dropFirst("assignment:".count))
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        guard
            title.hasSuffix("]"),
            let openingBracket = title.lastIndex(of: "["),
            openingBracket != title.startIndex
        else {
            return (title, nil)
        }

        let courseStart = title.index(after: openingBracket)
        let courseEnd = title.index(before: title.endIndex)
        let course = String(title[courseStart..<courseEnd])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !course.isEmpty, course.count <= 120 else {
            return (title, nil)
        }

        let titleWithoutCourse = String(title[..<openingBracket])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !titleWithoutCourse.isEmpty else {
            return (title, nil)
        }
        return (titleWithoutCourse, course)
    }

    private static func isAvailabilityMarker(
        _ event: ParsedCalendarEvent
    ) -> Bool {
        let summary = " \(event.summary.lowercased()) "
        let unmistakablePhrases = [
            " available from ",
            " available until ",
            " availability begins ",
            " availability ends ",
        ]
        return unmistakablePhrases.contains { summary.contains($0) }
    }

    private static func previewDiagnostic(
        _ preview: FeedPreview,
        trigger: RefreshTrigger
    ) -> FeedRefreshDiagnostic {
        FeedRefreshDiagnostic(
            attemptedAt: preview.fetchedAt,
            trigger: trigger,
            outcome: .success,
            receivedByteCount: preview.receivedByteCount,
            parsedEventCount:
                preview.events.count + preview.excludedEventCount,
            eligibleEventCount: preview.events.count,
            insertedCount: nil,
            updatedCount: nil,
            unchangedCount: nil,
            errorCode: nil
        )
    }

    private static func importDiagnostic(
        _ result: RefreshResult,
        trigger: RefreshTrigger,
        receivedByteCount: Int?,
        parsedEventCount: Int
    ) -> FeedRefreshDiagnostic {
        FeedRefreshDiagnostic(
            attemptedAt: result.refreshedAt,
            trigger: trigger,
            outcome: .success,
            receivedByteCount: receivedByteCount,
            parsedEventCount: parsedEventCount,
            eligibleEventCount: result.importedEventCount,
            insertedCount: result.importResult.insertedCount,
            updatedCount: result.importResult.updatedCount,
            unchangedCount: result.importResult.unchangedCount,
            errorCode: nil
        )
    }

    private static func failureDiagnostic(
        at date: Date,
        trigger: RefreshTrigger,
        error: any Error
    ) -> FeedRefreshDiagnostic {
        FeedRefreshDiagnostic(
            attemptedAt: date,
            trigger: trigger,
            outcome: .failure,
            receivedByteCount: nil,
            parsedEventCount: nil,
            eligibleEventCount: nil,
            insertedCount: nil,
            updatedCount: nil,
            unchangedCount: nil,
            errorCode: diagnosticCode(for: error)
        )
    }

    private static func diagnosticCode(for error: any Error) -> String {
        if let error = error as? CanvasFeedFetchError {
            return error.diagnosticCode
        }
        if let error = error as? KeychainFeedURLStoreError {
            return error.diagnosticCode
        }
        if let error = error as? FeedURLValidationError {
            switch error {
            case .HTTPSRequired:
                return "url.https-required"
            case .missingHost:
                return "url.missing-host"
            case .embeddedCredentialsNotAllowed:
                return "url.embedded-credentials"
            }
        }
        if let error = error as? ICSParserError {
            switch error {
            case .invalidTextEncoding:
                return "ics.invalid-text-encoding"
            case .notCalendarData:
                return "ics.not-calendar-data"
            }
        }
        if let error = error as? AssignmentRepositoryError {
            switch error {
            case .emptyTitle:
                return "repository.empty-title"
            case .assignmentNotFound:
                return "repository.not-found"
            case .cannotEditImportedAssignmentAsManual:
                return "repository.not-manual"
            }
        }
        if let error = error as? RefreshCoordinatorError {
            switch error {
            case .noSavedFeedURL:
                return "refresh.no-saved-url"
            case .noEventsSelected:
                return "refresh.no-events-selected"
            case .operationAlreadyInProgress:
                return "refresh.in-progress"
            case .refreshFailed:
                return "refresh.failed"
            }
        }
        return "refresh.unknown"
    }

    private static func presentationSafe(_ error: any Error) -> any Error {
        switch error {
        case is CanvasFeedFetchError,
             is KeychainFeedURLStoreError,
             is FeedURLValidationError,
             is ICSParserError,
             is AssignmentRepositoryError,
             is RefreshCoordinatorError,
             is CancellationError:
            error
        default:
            RefreshCoordinatorError.refreshFailed
        }
    }
}

import Foundation
import SwiftData

enum AssignmentSource: String, Codable, CaseIterable, Sendable {
    case canvasCalendarFeed
    case manual
    case screenshot
}

@Model
final class AssignmentEvent {
    @Attribute(.unique) var id: UUID
    var externalID: String?
    var title: String
    var courseName: String?
    var dueDate: Date
    var source: AssignmentSource
    var sourceURL: String?
    var isCompleted: Bool
    var isIgnored: Bool
    var createdAt: Date
    var updatedAt: Date

    /// Feed presence bookkeeping. A Canvas row is never deleted just because one
    /// refresh did not mention it; it is counted, and only archived once it has
    /// been absent from several consecutive authoritative feeds.
    var lastSeenInFeedAt: Date?
    var missingRefreshCount: Int = 0
    var isArchived: Bool = false
    var archivedAt: Date?

    init(
        id: UUID = UUID(),
        externalID: String? = nil,
        title: String,
        courseName: String? = nil,
        dueDate: Date,
        source: AssignmentSource,
        sourceURL: String? = nil,
        isCompleted: Bool = false,
        isIgnored: Bool = false,
        createdAt: Date = .now,
        updatedAt: Date = .now,
        lastSeenInFeedAt: Date? = nil,
        missingRefreshCount: Int = 0,
        isArchived: Bool = false,
        archivedAt: Date? = nil
    ) {
        self.id = id
        self.externalID = externalID
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
        self.source = source
        self.sourceURL = sourceURL
        self.isCompleted = isCompleted
        self.isIgnored = isIgnored
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.lastSeenInFeedAt = lastSeenInFeedAt
        self.missingRefreshCount = missingRefreshCount
        self.isArchived = isArchived
        self.archivedAt = archivedAt
    }
}

struct AssignmentSnapshot: Identifiable, Hashable, Sendable, CountdownEvent {
    var id: UUID
    var externalID: String?
    var title: String
    var courseName: String?
    var dueDate: Date
    var source: AssignmentSource
    var sourceURL: String?
    var isCompleted: Bool
    var isIgnored: Bool
    var createdAt: Date
    var updatedAt: Date
    var lastSeenInFeedAt: Date?
    var missingRefreshCount: Int
    var isArchived: Bool
    var archivedAt: Date?

    init(
        id: UUID = UUID(),
        externalID: String? = nil,
        title: String,
        courseName: String? = nil,
        dueDate: Date,
        source: AssignmentSource,
        sourceURL: String? = nil,
        isCompleted: Bool = false,
        isIgnored: Bool = false,
        createdAt: Date = .now,
        updatedAt: Date = .now,
        lastSeenInFeedAt: Date? = nil,
        missingRefreshCount: Int = 0,
        isArchived: Bool = false,
        archivedAt: Date? = nil
    ) {
        self.id = id
        self.externalID = externalID
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
        self.source = source
        self.sourceURL = sourceURL
        self.isCompleted = isCompleted
        self.isIgnored = isIgnored
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.lastSeenInFeedAt = lastSeenInFeedAt
        self.missingRefreshCount = missingRefreshCount
        self.isArchived = isArchived
        self.archivedAt = archivedAt
    }

    init(_ event: AssignmentEvent) {
        self.init(
            id: event.id,
            externalID: event.externalID,
            title: event.title,
            courseName: event.courseName,
            dueDate: event.dueDate,
            source: event.source,
            sourceURL: event.sourceURL,
            isCompleted: event.isCompleted,
            isIgnored: event.isIgnored,
            createdAt: event.createdAt,
            updatedAt: event.updatedAt,
            lastSeenInFeedAt: event.lastSeenInFeedAt,
            missingRefreshCount: event.missingRefreshCount,
            isArchived: event.isArchived,
            archivedAt: event.archivedAt
        )
    }

    /// True once a feed refresh has failed to mention this Canvas event, before
    /// it has been absent often enough to be archived.
    var isMissingFromFeed: Bool {
        missingRefreshCount > 0 && !isArchived
    }
}

struct AssignmentImportRecord: Hashable, Sendable {
    var externalID: String?
    var title: String
    var courseName: String?
    var dueDate: Date
    var source: AssignmentSource
    var sourceURL: String?

    init(
        externalID: String?,
        title: String,
        courseName: String? = nil,
        dueDate: Date,
        source: AssignmentSource = .canvasCalendarFeed,
        sourceURL: String? = nil
    ) {
        self.externalID = externalID
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
        self.source = source
        self.sourceURL = sourceURL
    }
}

struct ManualAssignmentDraft: Hashable, Sendable {
    var id: UUID?
    var title: String
    var courseName: String?
    var dueDate: Date
    var sourceURL: String?

    init(
        id: UUID? = nil,
        title: String,
        courseName: String? = nil,
        dueDate: Date,
        sourceURL: String? = nil
    ) {
        self.id = id
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
        self.sourceURL = sourceURL
    }
}

struct ImportResult: Equatable, Sendable {
    var insertedCount: Int
    var updatedCount: Int
    var unchangedCount: Int

    static let empty = ImportResult(
        insertedCount: 0,
        updatedCount: 0,
        unchangedCount: 0
    )
}

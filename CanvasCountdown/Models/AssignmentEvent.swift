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
        updatedAt: Date = .now
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
        updatedAt: Date = .now
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
            updatedAt: event.updatedAt
        )
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

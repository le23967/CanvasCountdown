import Foundation
import SwiftData

protocol AssignmentRepository: Sendable {
    func fetchAll() async throws -> [AssignmentSnapshot]

    func upsert(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) async throws -> ImportResult

    func saveManual(
        _ draft: ManualAssignmentDraft,
        now: Date
    ) async throws -> AssignmentSnapshot

    func updateStatus(
        id: UUID,
        isCompleted: Bool?,
        isIgnored: Bool?,
        now: Date
    ) async throws

    /// Removes Canvas rows that are no longer active in the authoritative
    /// calendar feed, including source cancellations and explicit preview
    /// exclusions. Manual events are never affected.
    func reconcileCanvasFeed(
        activeExternalIDs: Set<String>,
        excludedExternalIDs: Set<String>
    ) async throws

    func delete(id: UUID) async throws
    func deleteAll() async throws
}

extension AssignmentRepository {
    func upsert(
        _ records: [AssignmentImportRecord]
    ) async throws -> ImportResult {
        try await upsert(records, importedAt: .now)
    }

    func saveManual(
        _ draft: ManualAssignmentDraft
    ) async throws -> AssignmentSnapshot {
        try await saveManual(draft, now: .now)
    }

    func updateStatus(
        id: UUID,
        isCompleted: Bool? = nil,
        isIgnored: Bool? = nil
    ) async throws {
        try await updateStatus(
            id: id,
            isCompleted: isCompleted,
            isIgnored: isIgnored,
            now: .now
        )
    }

    func setCompleted(id: UUID, isCompleted: Bool) async throws {
        try await updateStatus(id: id, isCompleted: isCompleted)
    }

    func setIgnored(id: UUID, isIgnored: Bool) async throws {
        try await updateStatus(id: id, isIgnored: isIgnored)
    }

    func reconcileCanvasFeed(
        activeExternalIDs: Set<String>,
        excludedExternalIDs: Set<String>
    ) async throws {
        // Test doubles and alternate repositories may opt out. The production
        // SwiftData repository implements authoritative reconciliation.
    }
}

enum AssignmentRepositoryError: LocalizedError, Equatable {
    case emptyTitle
    case assignmentNotFound
    case cannotEditImportedAssignmentAsManual

    var errorDescription: String? {
        switch self {
        case .emptyTitle:
            "Enter an assignment name."
        case .assignmentNotFound:
            "The assignment no longer exists."
        case .cannotEditImportedAssignmentAsManual:
            "Only manually created assignments can be edited this way."
        }
    }
}

@ModelActor
actor SwiftDataAssignmentRepository: AssignmentRepository {
    func fetchAll() async throws -> [AssignmentSnapshot] {
        let events = try modelContext.fetch(FetchDescriptor<AssignmentEvent>())
        return events
            .map(AssignmentSnapshot.init)
            .sorted(by: AssignmentSnapshot.dueDateOrder)
    }

    func upsert(
        _ records: [AssignmentImportRecord],
        importedAt: Date
    ) async throws -> ImportResult {
        guard !records.isEmpty else {
            return .empty
        }

        let storedEvents = try modelContext.fetch(
            FetchDescriptor<AssignmentEvent>()
        )
        var externalIDIndex: [String: AssignmentEvent] = [:]
        var sourceURLIndex: [String: AssignmentEvent] = [:]
        var fallbackIndex: [String: AssignmentEvent] = [:]

        for event in storedEvents {
            if let key = Self.externalIDKey(
                event.externalID,
                source: event.source
            ) {
                externalIDIndex[key] = externalIDIndex[key] ?? event
            }
            if let key = Self.sourceURLKey(
                event.sourceURL,
                source: event.source
            ) {
                sourceURLIndex[key] = sourceURLIndex[key] ?? event
            }
            fallbackIndex[Self.fallbackKey(
                title: event.title,
                courseName: event.courseName,
                dueDate: event.dueDate,
                source: event.source
            )] = event
        }

        var result = ImportResult.empty

        for rawRecord in records {
            let record = try Self.sanitized(rawRecord)
            let externalKey = Self.externalIDKey(
                record.externalID,
                source: record.source
            )
            let URLKey = Self.sourceURLKey(
                record.sourceURL,
                source: record.source
            )
            let fallbackKey = Self.fallbackKey(
                title: record.title,
                courseName: record.courseName,
                dueDate: record.dueDate,
                source: record.source
            )

            let existing: AssignmentEvent?
            if let externalKey {
                existing = externalIDIndex[externalKey]
                    ?? URLKey
                    .flatMap { sourceURLIndex[$0] }
                    .flatMap { candidate in
                        // A URL may legitimately be shared by multiple Canvas
                        // VEVENTs. Only use it to upgrade a legacy row that did
                        // not yet have a stable UID.
                        candidate.externalID == nil ? candidate : nil
                    }
            } else {
                existing = URLKey.flatMap { sourceURLIndex[$0] }
                    ?? fallbackIndex[fallbackKey]
            }

            if let existing {
                let changed = Self.apply(record, to: existing)
                if changed {
                    existing.updatedAt = importedAt
                    result.updatedCount += 1
                } else {
                    result.unchangedCount += 1
                }

                if let externalKey {
                    externalIDIndex[externalKey] = existing
                }
                if let URLKey {
                    sourceURLIndex[URLKey] = existing
                }
                fallbackIndex[Self.fallbackKey(
                    title: existing.title,
                    courseName: existing.courseName,
                    dueDate: existing.dueDate,
                    source: existing.source
                )] = existing
                continue
            }

            let event = AssignmentEvent(
                externalID: record.externalID,
                title: record.title,
                courseName: record.courseName,
                dueDate: record.dueDate,
                source: record.source,
                sourceURL: record.sourceURL,
                createdAt: importedAt,
                updatedAt: importedAt
            )
            modelContext.insert(event)
            result.insertedCount += 1

            if let externalKey {
                externalIDIndex[externalKey] = event
            }
            if let URLKey {
                sourceURLIndex[URLKey] = event
            }
            fallbackIndex[fallbackKey] = event
        }

        if modelContext.hasChanges {
            try modelContext.save()
        }
        return result
    }

    func saveManual(
        _ draft: ManualAssignmentDraft,
        now: Date
    ) async throws -> AssignmentSnapshot {
        let title = try Self.requiredTitle(draft.title)
        let courseName = Self.optionalText(draft.courseName)
        let sourceURL = Self.optionalText(draft.sourceURL)

        if let id = draft.id {
            guard let event = try event(id: id) else {
                throw AssignmentRepositoryError.assignmentNotFound
            }
            guard event.source == .manual else {
                throw AssignmentRepositoryError
                    .cannotEditImportedAssignmentAsManual
            }

            event.title = title
            event.courseName = courseName
            event.dueDate = draft.dueDate
            event.sourceURL = sourceURL
            event.updatedAt = now
            try modelContext.save()
            return AssignmentSnapshot(event)
        }

        let event = AssignmentEvent(
            title: title,
            courseName: courseName,
            dueDate: draft.dueDate,
            source: .manual,
            sourceURL: sourceURL,
            createdAt: now,
            updatedAt: now
        )
        modelContext.insert(event)
        try modelContext.save()
        return AssignmentSnapshot(event)
    }

    func updateStatus(
        id: UUID,
        isCompleted: Bool?,
        isIgnored: Bool?,
        now: Date
    ) async throws {
        guard let event = try event(id: id) else {
            throw AssignmentRepositoryError.assignmentNotFound
        }

        var changed = false
        if let isCompleted, event.isCompleted != isCompleted {
            event.isCompleted = isCompleted
            changed = true
        }
        if let isIgnored, event.isIgnored != isIgnored {
            event.isIgnored = isIgnored
            changed = true
        }

        guard changed else {
            return
        }
        event.updatedAt = now
        try modelContext.save()
    }

    func reconcileCanvasFeed(
        activeExternalIDs: Set<String>,
        excludedExternalIDs: Set<String>
    ) async throws {
        let active = Set(activeExternalIDs.map(Self.normalizedExternalID))
        let excluded = Set(
            excludedExternalIDs.map(Self.normalizedExternalID)
        )
        let events = try modelContext.fetch(
            FetchDescriptor<AssignmentEvent>()
        )

        for event in events where event.source == .canvasCalendarFeed {
            guard let externalID = event.externalID else {
                continue
            }
            let normalizedID = Self.normalizedExternalID(externalID)
            if excluded.contains(normalizedID)
                || !active.contains(normalizedID) {
                modelContext.delete(event)
            }
        }
        if modelContext.hasChanges {
            try modelContext.save()
        }
    }

    func delete(id: UUID) async throws {
        guard let event = try event(id: id) else {
            throw AssignmentRepositoryError.assignmentNotFound
        }
        modelContext.delete(event)
        try modelContext.save()
    }

    func deleteAll() async throws {
        let events = try modelContext.fetch(FetchDescriptor<AssignmentEvent>())
        for event in events {
            modelContext.delete(event)
        }
        if modelContext.hasChanges {
            try modelContext.save()
        }
    }

    private func event(id: UUID) throws -> AssignmentEvent? {
        let requestedID = id
        var descriptor = FetchDescriptor<AssignmentEvent>(
            predicate: #Predicate { $0.id == requestedID }
        )
        descriptor.fetchLimit = 1
        return try modelContext.fetch(descriptor).first
    }

    private static func apply(
        _ record: AssignmentImportRecord,
        to event: AssignmentEvent
    ) -> Bool {
        var changed = false

        if event.externalID != record.externalID {
            event.externalID = record.externalID
            changed = true
        }
        if event.title != record.title {
            event.title = record.title
            changed = true
        }
        if event.courseName != record.courseName {
            event.courseName = record.courseName
            changed = true
        }
        if event.dueDate != record.dueDate {
            event.dueDate = record.dueDate
            changed = true
        }
        if event.sourceURL != record.sourceURL {
            event.sourceURL = record.sourceURL
            changed = true
        }

        // Intentionally never overwrite isCompleted or isIgnored. Those are
        // local decisions and must survive every Canvas refresh.
        return changed
    }

    private static func sanitized(
        _ record: AssignmentImportRecord
    ) throws -> AssignmentImportRecord {
        AssignmentImportRecord(
            externalID: optionalText(record.externalID),
            title: try requiredTitle(record.title),
            courseName: optionalText(record.courseName),
            dueDate: record.dueDate,
            source: record.source,
            sourceURL: optionalText(record.sourceURL)
        )
    }

    private static func requiredTitle(_ value: String) throws -> String {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else {
            throw AssignmentRepositoryError.emptyTitle
        }
        return value
    }

    private static func optionalText(_ value: String?) -> String? {
        guard let value else {
            return nil
        }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private static func externalIDKey(
        _ externalID: String?,
        source: AssignmentSource
    ) -> String? {
        optionalText(externalID).map {
            "\(source.rawValue)|\($0.lowercased(with: Locale(identifier: "en_US_POSIX")))"
        }
    }

    private static func normalizedExternalID(_ externalID: String) -> String {
        externalID.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
    }

    private static func sourceURLKey(
        _ sourceURL: String?,
        source: AssignmentSource
    ) -> String? {
        optionalText(sourceURL).map {
            "\(source.rawValue)|\($0)"
        }
    }

    private static func fallbackKey(
        title: String,
        courseName: String?,
        dueDate: Date,
        source: AssignmentSource
    ) -> String {
        let normalizedTitle = title
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
        let normalizedCourse = optionalText(courseName)?
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
            ?? ""
        let timestamp = Int64(dueDate.timeIntervalSinceReferenceDate.rounded())
        return "\(source.rawValue)|\(normalizedTitle)|\(normalizedCourse)|\(timestamp)"
    }
}

private extension AssignmentSnapshot {
    static func dueDateOrder(
        _ left: AssignmentSnapshot,
        _ right: AssignmentSnapshot
    ) -> Bool {
        if left.dueDate != right.dueDate {
            return left.dueDate < right.dueDate
        }
        return left.title.localizedStandardCompare(right.title) == .orderedAscending
    }
}

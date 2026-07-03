import SwiftData
import XCTest
@testable import CanvasCountdown

@MainActor
final class AssignmentRepositoryTests: XCTestCase {
    func testDuplicateCanvasUIDUpdatesInsteadOfInserting() async throws {
        let repository = try makeRepository()
        let originalDueDate = Date(timeIntervalSince1970: 1_800_000_000)
        let revisedDueDate = originalDueDate.addingTimeInterval(86_400)

        let firstResult = try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: "event-123@example.edu",
                    title: "Original title",
                    courseName: "COMP1000",
                    dueDate: originalDueDate
                ),
            ],
            importedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
        let secondResult = try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: " EVENT-123@example.edu ",
                    title: "Revised title",
                    courseName: "COMP1000",
                    dueDate: revisedDueDate
                ),
            ],
            importedAt: Date(timeIntervalSince1970: 1_700_000_100)
        )

        let assignments = try await repository.fetchAll()
        XCTAssertEqual(firstResult.insertedCount, 1)
        XCTAssertEqual(secondResult.updatedCount, 1)
        XCTAssertEqual(assignments.count, 1)
        XCTAssertEqual(assignments.first?.externalID, "EVENT-123@example.edu")
        XCTAssertEqual(assignments.first?.title, "Revised title")
        XCTAssertEqual(assignments.first?.dueDate, revisedDueDate)
    }

    func testCompletedStateSurvivesCanvasRefresh() async throws {
        let repository = try makeRepository()
        let dueDate = Date(timeIntervalSince1970: 1_800_000_000)
        try await importOne(
            into: repository,
            uid: "completed-state",
            title: "Before refresh",
            dueDate: dueDate
        )
        let imported = try await repository.fetchAll()
        let id = try XCTUnwrap(imported.first?.id)
        try await repository.updateStatus(
            id: id,
            isCompleted: true,
            isIgnored: nil,
            now: Date(timeIntervalSince1970: 1_700_000_010)
        )

        try await importOne(
            into: repository,
            uid: "completed-state",
            title: "After refresh",
            dueDate: dueDate.addingTimeInterval(3_600)
        )

        let assignments = try await repository.fetchAll()
        let refreshed = try XCTUnwrap(assignments.first)
        XCTAssertEqual(refreshed.title, "After refresh")
        XCTAssertTrue(refreshed.isCompleted)
        XCTAssertFalse(refreshed.isIgnored)
    }

    func testIgnoredStateSurvivesCanvasRefresh() async throws {
        let repository = try makeRepository()
        let dueDate = Date(timeIntervalSince1970: 1_800_000_000)
        try await importOne(
            into: repository,
            uid: "ignored-state",
            title: "Before refresh",
            dueDate: dueDate
        )
        let imported = try await repository.fetchAll()
        let id = try XCTUnwrap(imported.first?.id)
        try await repository.updateStatus(
            id: id,
            isCompleted: nil,
            isIgnored: true,
            now: Date(timeIntervalSince1970: 1_700_000_010)
        )

        try await importOne(
            into: repository,
            uid: "ignored-state",
            title: "After refresh",
            dueDate: dueDate.addingTimeInterval(7_200)
        )

        let assignments = try await repository.fetchAll()
        let refreshed = try XCTUnwrap(assignments.first)
        XCTAssertEqual(refreshed.title, "After refresh")
        XCTAssertFalse(refreshed.isCompleted)
        XCTAssertTrue(refreshed.isIgnored)
    }

    private func makeRepository() throws -> SwiftDataAssignmentRepository {
        let schema = Schema([AssignmentEvent.self])
        let configuration = ModelConfiguration(
            schema: schema,
            isStoredInMemoryOnly: true
        )
        let container = try ModelContainer(
            for: schema,
            configurations: [configuration]
        )
        return SwiftDataAssignmentRepository(modelContainer: container)
    }

    @discardableResult
    private func importOne(
        into repository: SwiftDataAssignmentRepository,
        uid: String,
        title: String,
        dueDate: Date
    ) async throws -> ImportResult {
        try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: uid,
                    title: title,
                    courseName: "COMP1000",
                    dueDate: dueDate
                ),
            ],
            importedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
    }
}

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

    /// Nothing ever refreshes an event read out of a screenshot, so the editor
    /// is the only place a misread title or date can be put right. It used to
    /// be refused as "imported".
    func testAnEventFromAScreenshotCanBeCorrected() async throws {
        let repository = try makeRepository()
        let misread = Date(timeIntervalSince1970: 1_800_000_000)
        _ = try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: "screenshot:example",
                    title: "Qviz 2",
                    courseName: "COMP1000",
                    dueDate: misread,
                    source: .screenshot
                ),
            ],
            importedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
        let stored = try await repository.fetchAll()
        let stale = try XCTUnwrap(stored.first)

        let corrected = try await repository.saveManual(
            ManualAssignmentDraft(
                id: stale.id,
                title: "Quiz 2",
                courseName: "COMP1000",
                dueDate: misread.addingTimeInterval(86_400)
            ),
            now: Date(timeIntervalSince1970: 1_700_000_100)
        )

        XCTAssertEqual(corrected.title, "Quiz 2")
        XCTAssertEqual(corrected.dueDate, misread.addingTimeInterval(86_400))
        XCTAssertEqual(
            corrected.source,
            .screenshot,
            "Correcting it does not change where it came from"
        )
        let afterwards = try await repository.fetchAll()
        XCTAssertEqual(afterwards.count, 1)

        // The same misreading imported again must not write over the
        // correction: it is added beside it, where duplicate detection in the
        // review will have flagged it.
        _ = try await repository.upsert(
            [
                AssignmentImportRecord(
                    externalID: "screenshot:example",
                    title: "Qviz 2",
                    courseName: "COMP1000",
                    dueDate: misread,
                    source: .screenshot
                ),
            ],
            importedAt: Date(timeIntervalSince1970: 1_700_000_200)
        )
        let reimported = try await repository.fetchAll()
        let corrected2 = try XCTUnwrap(reimported.first { $0.id == stale.id })
        XCTAssertEqual(corrected2.title, "Quiz 2", "The correction stands")
        XCTAssertEqual(corrected2.dueDate, misread.addingTimeInterval(86_400))
    }

    /// The refusal that matters is still there: the next refresh would undo an
    /// edit to anything the feed sent.
    func testAnEventFromTheFeedStillCannotBeEditedAsManual() async throws {
        let repository = try makeRepository()
        try await importOne(
            into: repository,
            uid: "event-9@example.edu",
            title: "From Canvas",
            dueDate: Date(timeIntervalSince1970: 1_800_000_000)
        )
        let stored = try await repository.fetchAll()
        let imported = try XCTUnwrap(stored.first)

        do {
            _ = try await repository.saveManual(
                ManualAssignmentDraft(
                    id: imported.id,
                    title: "Edited",
                    courseName: nil,
                    dueDate: imported.dueDate
                ),
                now: Date(timeIntervalSince1970: 1_700_000_100)
            )
            XCTFail("A feed event must not be editable")
        } catch let error as AssignmentRepositoryError {
            XCTAssertEqual(error, .cannotEditImportedAssignmentAsManual)
        }
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

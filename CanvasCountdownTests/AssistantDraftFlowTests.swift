import Foundation
import SwiftData
import XCTest
@testable import CanvasCountdown

/// Drafting a task is a conversation, not a single shot.
///
/// Correcting one wrong row used to mean retyping the whole sentence, and the
/// reply was a fresh guess that threw away whatever had already been fixed by
/// hand. These cover the flow that replaced that: what was asked stays on
/// screen, a follow-up edits the drafts that exist, and a follow-up that fails
/// leaves the review exactly as it was.
///
/// No test makes a real network request. All fixtures are invented.
@MainActor
final class AssistantDraftFlowTests: XCTestCase {
    // MARK: - The request stays, and reads as a conversation

    func testTheRequestStaysOnScreenAfterDrafting() async throws {
        let harness = try makeHarness()

        harness.viewModel.assistantDraftRequest = "gym monday wednesday friday"
        await harness.viewModel.draftTask(
            from: harness.viewModel.assistantDraftRequest
        )

        XCTAssertEqual(
            harness.viewModel.assistantDraftRequest,
            "gym monday wednesday friday",
            "Retyping the sentence to change one thing is what this fixed"
        )
        XCTAssertEqual(
            harness.viewModel.assistantDraftHistory,
            ["gym monday wednesday friday"]
        )
        XCTAssertEqual(harness.viewModel.assistantDrafts.count, 2)
    }

    /// The follow-up is sent with the drafts attached, so it edits the rows on
    /// screen instead of guessing at the original sentence again.
    func testAFollowUpEditsTheDraftsAlreadyOnScreen() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday friday")
        let before = harness.viewModel.assistantDrafts

        harness.service.revision = { drafts, _ in
            drafts.map { draft in
                var edited = draft
                edited.courseName = "41021 Interaction Design Studio"
                return edited
            }
        }
        await harness.viewModel.reviseAssistantDrafts(
            with: "put them all under 41021"
        )

        let sent = harness.service.lastRevisionRequest
        XCTAssertEqual(
            sent?.drafts.map(\.id),
            before.map(\.id),
            "The drafts on screen go with the follow-up"
        )
        XCTAssertEqual(sent?.instruction, "put them all under 41021")
        XCTAssertEqual(
            harness.viewModel.assistantDrafts.map(\.courseName),
            ["41021 Interaction Design Studio", "41021 Interaction Design Studio"]
        )
        XCTAssertEqual(
            harness.viewModel.assistantDraftHistory,
            ["gym monday wednesday friday", "put them all under 41021"],
            "Both turns are shown, so the drafts do not appear from nowhere"
        )
        XCTAssertTrue(
            harness.viewModel.assistantRevisionInput.isEmpty,
            "The follow-up field empties, ready for the next one"
        )
    }

    /// The tick and the label are never sent, so a follow-up cannot have meant
    /// to change them. This is the whole-flow version of that promise.
    func testAFollowUpKeepsCorrectionsMadeByHand() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday friday")

        var unticked = harness.viewModel.assistantDrafts[0]
        unticked.include = false
        harness.viewModel.updateAssistantDraft(unticked)

        // A service that answers the way the real one does: through the parser,
        // from JSON that carries no tick and no label.
        harness.service.revision = { drafts, _ in
            let reply = """
            {"tasks":[
              {"id":"\(drafts[0].id.uuidString)","title":"\(drafts[0].title)","course":"Gym","due":"2026-08-03T23:59"},
              {"id":"\(drafts[1].id.uuidString)","title":"\(drafts[1].title)","course":"Gym","due":"2026-08-05T23:59"}
            ]}
            """
            return ChatCompletionsAssistantService.parseRevisedDrafts(
                reply,
                revising: drafts,
                calendar: Self.sydneyCalendar
            )
        }
        await harness.viewModel.reviseAssistantDrafts(with: "call them Gym")

        XCTAssertFalse(
            harness.viewModel.assistantDrafts[0].include,
            "A row unticked by hand stays unticked through a follow-up"
        )
        XCTAssertTrue(harness.viewModel.assistantDrafts[1].include)
        XCTAssertEqual(
            harness.viewModel.assistantDrafts.map(\.courseName),
            ["Gym", "Gym"]
        )
    }

    // MARK: - When the follow-up does not work

    /// Losing the whole review because a follow-up failed would be worse than
    /// the follow-up doing nothing.
    func testAFailedFollowUpLeavesTheDraftsAlone() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday friday")
        let before = harness.viewModel.assistantDrafts

        harness.service.revision = { _, _ in
            throw AssistantError.unreachable
        }
        await harness.viewModel.reviseAssistantDrafts(with: "put them under 41021")

        XCTAssertEqual(harness.viewModel.assistantDrafts, before)
        XCTAssertNotNil(harness.viewModel.assistantErrorMessage)
        XCTAssertEqual(
            harness.viewModel.assistantDraftHistory,
            ["gym monday wednesday friday"],
            "A turn that failed is not shown as if it had happened"
        )
    }

    /// Silence would read as "it worked" and the drafts would look wrong for no
    /// reason anyone could see.
    func testAFollowUpThatChangesNothingSaysSo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday friday")

        harness.service.revision = { drafts, _ in drafts }
        await harness.viewModel.reviseAssistantDrafts(with: "make it better")

        XCTAssertNotNil(harness.viewModel.assistantErrorMessage)
        XCTAssertEqual(
            harness.viewModel.assistantDraftHistory,
            ["gym monday wednesday friday"]
        )
    }

    func testAFollowUpNeedsDraftsToChange() async throws {
        let harness = try makeHarness()

        XCTAssertFalse(harness.viewModel.canReviseAssistantDrafts)
        await harness.viewModel.reviseAssistantDrafts(with: "put them under 41021")
        let asked = harness.service.lastRevisionRequest

        XCTAssertNil(asked, "Nothing is sent when there is nothing to edit")

        await harness.viewModel.draftTask(from: "gym monday wednesday friday")
        XCTAssertTrue(harness.viewModel.canReviseAssistantDrafts)
    }

    // MARK: - Fixing a row without asking

    /// Dropping one row by hand is quicker than asking for it, and it cannot be
    /// misunderstood as meaning a different row.
    func testARowCanBeDroppedWithoutTheModel() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday friday")
        let dropped = harness.viewModel.assistantDrafts[0].id

        harness.viewModel.removeAssistantDraft(dropped)
        let asked = harness.service.lastRevisionRequest

        XCTAssertEqual(harness.viewModel.assistantDrafts.count, 1)
        XCTAssertFalse(harness.viewModel.assistantDrafts.contains { $0.id == dropped })
        XCTAssertNil(asked, "Removing a row does not go back to the model")
    }

    func testStartingOverClearsTheConversation() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday friday")
        harness.viewModel.assistantRevisionInput = "half typed"

        harness.viewModel.clearAssistantDrafts()

        XCTAssertTrue(harness.viewModel.assistantDrafts.isEmpty)
        XCTAssertTrue(harness.viewModel.assistantDraftHistory.isEmpty)
        XCTAssertTrue(harness.viewModel.assistantRevisionInput.isEmpty)
        XCTAssertNil(harness.viewModel.assistantErrorMessage)
    }

    // MARK: - Helpers

    nonisolated private static var sydneyCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private struct Harness {
        let viewModel: MainViewModel
        let service: DraftFlowAssistantStub
    }

    private func makeHarness() throws -> Harness {
        let suiteName = "AssistantDraftFlowTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)

        let container = try ModelContainer(
            for: AssignmentEvent.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
        let service = DraftFlowAssistantStub()
        let viewModel = MainViewModel(
            repository: SwiftDataAssignmentRepository(modelContainer: container),
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            calendar: Self.sydneyCalendar,
            automaticActivityEnabled: false,
            courseBlocklist: IsolatedCourseBlocklistStore(),
            assistantFactory: { _, _, _ in service }
        )
        return Harness(viewModel: viewModel, service: service)
    }
}

/// Stands in for the model. Answers with fixed drafts, and records what it was
/// asked so the test can check the drafts on screen were sent with a follow-up.
final class DraftFlowAssistantStub: AssistantServicing, @unchecked Sendable {
    struct RevisionRequest: Sendable {
        let drafts: [AssistantDraftTask]
        let instruction: String
    }

    private let lock = NSLock()
    private var recordedRequest: RevisionRequest?

    /// Set by the test before the follow-up it is about to make.
    nonisolated(unsafe) var revision:
        @Sendable ([AssistantDraftTask], String) throws -> [AssistantDraftTask] = { drafts, _ in
            drafts
        }

    var lastRevisionRequest: RevisionRequest? {
        lock.withLock { recordedRequest }
    }

    func answer(
        _ question: String,
        history: [AssistantMessage],
        digests: [AssistantAssignmentDigest],
        now: Date
    ) async throws -> String { "" }

    func summarise(
        _ digests: [AssistantAssignmentDigest],
        now: Date
    ) async throws -> String { "" }

    func draftTasks(
        from text: String,
        now: Date
    ) async throws -> [AssistantDraftTask] {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return [
            AssistantDraftTask(
                include: true,
                title: "Workout 1",
                courseName: nil,
                labelID: nil,
                dueDate: calendar.date(
                    from: DateComponents(year: 2026, month: 8, day: 3, hour: 23, minute: 59)
                )!,
                sourceText: text
            ),
            AssistantDraftTask(
                include: true,
                title: "Workout 2",
                courseName: nil,
                labelID: nil,
                dueDate: calendar.date(
                    from: DateComponents(year: 2026, month: 8, day: 5, hour: 23, minute: 59)
                )!,
                sourceText: text
            ),
        ]
    }

    func reviseDrafts(
        _ drafts: [AssistantDraftTask],
        instruction: String,
        now: Date
    ) async throws -> [AssistantDraftTask] {
        lock.withLock {
            recordedRequest = RevisionRequest(
                drafts: drafts,
                instruction: instruction
            )
        }
        return try revision(drafts, instruction)
    }
}

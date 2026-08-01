import Foundation
import SwiftData
import XCTest
@testable import CanvasCountdown

/// Saving drafts is the one step that leaves the panel and changes the list,
/// and it happens to several tasks at once. It asks first, and what it asks
/// says what is about to happen — including the sentence you typed and never
/// applied, which used to be thrown away silently.
///
/// No test makes a real network request. All fixtures are invented.
@MainActor
final class AssistantSaveConfirmationTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private func date(_ day: Int) -> Date {
        calendar.date(
            from: DateComponents(year: 2026, month: 8, day: day, hour: 23, minute: 59)
        )!
    }

    // MARK: - Nothing is written on the click

    func testSavingAsksBeforeItWritesAnything() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")

        harness.viewModel.requestSaveAssistantDrafts()

        XCTAssertTrue(harness.viewModel.isConfirmingAssistantSave)
        XCTAssertEqual(
            harness.viewModel.assistantDrafts.count,
            2,
            "The review is still there while the question is on screen"
        )
        let stored = try await harness.repository.fetchAll()
        XCTAssertTrue(stored.isEmpty, "Asking is not saving")
    }

    /// Cancel has to be a real way back, or the confirmation is just a step to
    /// click through.
    func testCancellingLeavesTheReviewExactlyAsItWas() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")
        let before = harness.viewModel.assistantDrafts
        harness.viewModel.assistantRevisionInput = "half typed"

        harness.viewModel.requestSaveAssistantDrafts()
        harness.viewModel.cancelSaveAssistantDrafts()

        XCTAssertFalse(harness.viewModel.isConfirmingAssistantSave)
        XCTAssertEqual(harness.viewModel.assistantDrafts, before)
        XCTAssertEqual(harness.viewModel.assistantRevisionInput, "half typed")
        let stored = try await harness.repository.fetchAll()
        XCTAssertTrue(stored.isEmpty)
    }

    func testThereIsNothingToConfirmWhenNothingWouldBeSaved() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")
        for draft in harness.viewModel.assistantDrafts {
            var untick = draft
            untick.include = false
            harness.viewModel.updateAssistantDraft(untick)
        }

        harness.viewModel.requestSaveAssistantDrafts()

        XCTAssertFalse(harness.viewModel.isConfirmingAssistantSave)
    }

    func testConfirmingSavesAndClosesTheQuestion() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")

        harness.viewModel.requestSaveAssistantDrafts()
        await harness.viewModel.saveAssistantDrafts(
            harness.viewModel.assistantDrafts
        )

        let stored = try await harness.repository.fetchAll()
        XCTAssertEqual(stored.count, 2)
        XCTAssertFalse(harness.viewModel.isConfirmingAssistantSave)
        XCTAssertTrue(harness.viewModel.assistantDrafts.isEmpty)
    }

    /// Undo is still offered afterwards. The confirmation is the chance to stop
    /// it happening; undo is the way back once it has.
    func testUndoIsStillOfferedAfterConfirming() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")

        harness.viewModel.requestSaveAssistantDrafts()
        await harness.viewModel.saveAssistantDrafts(
            harness.viewModel.assistantDrafts
        )

        XCTAssertTrue(harness.viewModel.canUndoAddition)
    }

    // MARK: - What the question counts

    func testTheCountIgnoresRowsThatWouldNotBeSaved() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")

        var untick = harness.viewModel.assistantDrafts[0]
        untick.include = false
        harness.viewModel.updateAssistantDraft(untick)

        let summary = harness.viewModel.assistantSaveSummary
        XCTAssertEqual(summary.savingCount, 1)
        XCTAssertEqual(summary.skippedCount, 1)
        XCTAssertEqual(summary.title, "Add 1 task?")
    }

    func testARowWithNoDateIsCountedAsLeftOut() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")

        var undated = harness.viewModel.assistantDrafts[0]
        undated.dueDate = nil
        harness.viewModel.updateAssistantDraft(undated)

        let summary = harness.viewModel.assistantSaveSummary
        XCTAssertEqual(summary.savingCount, 1)
        XCTAssertEqual(summary.skippedCount, 1)
    }

    /// The mistake this exists to catch: a follow-up typed but never applied,
    /// then Save clicked. Saving would drop the sentence without a word.
    func testATypedChangeThatWasNeverAppliedIsCalledOut() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")

        XCTAssertFalse(harness.viewModel.assistantSaveSummary.hasUnappliedChange)

        harness.viewModel.assistantRevisionInput = "put them under 41021"

        let summary = harness.viewModel.assistantSaveSummary
        XCTAssertTrue(summary.hasUnappliedChange)
        XCTAssertEqual(
            summary.detailLines(calendar: calendar).first,
            "You typed a change that has not been applied yet. Saving now leaves it out.",
            "It is said first, because it is the surprise"
        )
    }

    func testWhitespaceInTheFollowUpFieldIsNotAChange() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")
        harness.viewModel.assistantRevisionInput = "   \n "

        XCTAssertFalse(harness.viewModel.assistantSaveSummary.hasUnappliedChange)
    }

    func testTheCourseIsNamedSoItCanBeChecked() async throws {
        let harness = try makeHarness()
        await harness.viewModel.draftTask(from: "gym monday wednesday")
        for draft in harness.viewModel.assistantDrafts {
            var withCourse = draft
            withCourse.courseName = "41021 Interaction Design Studio"
            harness.viewModel.updateAssistantDraft(withCourse)
        }

        let lines = harness.viewModel.assistantSaveSummary
            .detailLines(calendar: calendar)

        XCTAssertTrue(
            lines.contains("Under 41021 Interaction Design Studio."),
            "Saw: \(lines)"
        )
    }

    // MARK: - The wording itself

    func testTheButtonRepeatsTheNumber() {
        XCTAssertEqual(summary(saving: 1).confirmTitle, "Add 1 Task")
        XCTAssertEqual(summary(saving: 6).confirmTitle, "Add 6 Tasks")
        XCTAssertEqual(summary(saving: 1).title, "Add 1 task?")
        XCTAssertEqual(summary(saving: 6).title, "Add 6 tasks?")
    }

    func testTheWayBackIsMentioned() {
        let lines = summary(saving: 3).detailLines(calendar: calendar)
        XCTAssertEqual(
            lines.last,
            "They are added as manual events, and can be undone."
        )
    }

    func testASingleDayIsNotShownAsARange() {
        let lines = summary(
            saving: 2,
            earliest: date(8),
            latest: date(8)
        ).detailLines(calendar: calendar)

        XCTAssertTrue(
            lines.contains { $0.hasPrefix("Due ") && !$0.contains(" to ") },
            "Saw: \(lines)"
        )
    }

    func testASpanOfDaysIsShownAsARange() {
        let lines = summary(
            saving: 2,
            earliest: date(8),
            latest: date(15)
        ).detailLines(calendar: calendar)

        XCTAssertTrue(
            lines.contains { $0.hasPrefix("Due ") && $0.contains(" to ") },
            "Saw: \(lines)"
        )
    }

    func testTasksWithoutACourseAreCounted() {
        let lines = summary(
            saving: 3,
            courses: ["41021"],
            withoutCourse: 2
        ).detailLines(calendar: calendar)

        XCTAssertTrue(
            lines.contains("Under 41021, and 2 without a course."),
            "Saw: \(lines)"
        )
    }

    func testNothingIsSaidAboutCoursesWhenThereAreNone() {
        let lines = summary(saving: 2).detailLines(calendar: calendar)
        XCTAssertFalse(lines.contains { $0.contains("Under ") })
    }

    func testAnEmptySelectionCannotBeSaved() {
        XCTAssertFalse(summary(saving: 0).canSave)
        XCTAssertTrue(summary(saving: 1).canSave)
    }

    // MARK: - Helpers

    private func summary(
        saving: Int,
        skipped: Int = 0,
        courses: [String] = [],
        withoutCourse: Int = 0,
        earliest: Date? = nil,
        latest: Date? = nil,
        unapplied: Bool = false
    ) -> AssistantSaveSummary {
        AssistantSaveSummary(
            savingCount: saving,
            skippedCount: skipped,
            courses: courses,
            withoutCourseCount: withoutCourse,
            earliest: earliest,
            latest: latest,
            hasUnappliedChange: unapplied
        )
    }

    private struct Harness {
        let viewModel: MainViewModel
        let repository: SwiftDataAssignmentRepository
    }

    private func makeHarness() throws -> Harness {
        let suiteName = "AssistantSaveConfirmationTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)

        let container = try ModelContainer(
            for: AssignmentEvent.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
        let repository = SwiftDataAssignmentRepository(modelContainer: container)
        let service = DraftFlowAssistantStub()
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            calendar: calendar,
            automaticActivityEnabled: false,
            courseBlocklist: IsolatedCourseBlocklistStore(),
            assistantFactory: { _, _, _ in service }
        )
        return Harness(viewModel: viewModel, repository: repository)
    }
}

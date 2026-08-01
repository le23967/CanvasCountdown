import Foundation
import SwiftData
import XCTest
@testable import CanvasCountdown

/// What was asked of the assistant is kept, so it is possible to tell whether
/// something has already been asked and to read the answer again tomorrow.
///
/// No test makes a real network request. All fixtures are invented.
@MainActor
final class AssistantConversationHistoryTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    // MARK: - It survives the window closing

    func testAConversationIsStillThereOnTheNextLaunch() async throws {
        let suiteName = "AssistantConversationHistoryTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let first = try makeViewModel(defaults: defaults)
        await first.ask("how many are due this week?")

        XCTAssertEqual(first.conversation.count, 2)

        // A second store over the same preferences is what the next launch is.
        let second = try makeViewModel(defaults: defaults)

        XCTAssertEqual(second.conversation.map(\.text), first.conversation.map(\.text))
        XCTAssertEqual(second.conversation.map(\.role), [.user, .assistant])
    }

    func testEachTurnRecordsWhenItHappened() async throws {
        let viewModel = try makeViewModel()
        let before = Date()
        await viewModel.ask("what should I start today?")

        for message in viewModel.conversation {
            XCTAssertGreaterThanOrEqual(message.date, before)
            XCTAssertLessThanOrEqual(message.date, Date())
        }
    }

    /// A question that failed still shows as asked. Losing it would make the
    /// record disagree with what happened.
    func testAQuestionIsKeptEvenWhenTheAnswerFails() async throws {
        let harness = try makeFailingViewModel()
        await harness.ask("what is most urgent?")

        XCTAssertEqual(harness.conversation.count, 1)
        XCTAssertEqual(harness.conversation.first?.role, .user)
        XCTAssertNotNil(harness.assistantErrorMessage)
    }

    // MARK: - Clearing it is a deletion

    func testClearingAsksFirst() async throws {
        let viewModel = try makeViewModel()
        await viewModel.ask("how many are due this week?")

        viewModel.requestClearConversation()

        XCTAssertTrue(viewModel.isConfirmingClearConversation)
        XCTAssertEqual(
            viewModel.conversation.count,
            2,
            "Asking to clear does not clear"
        )
    }

    func testClearingRemovesItEverywhere() async throws {
        let suiteName = "AssistantConversationHistoryTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let first = try makeViewModel(defaults: defaults)
        await first.ask("how many are due this week?")

        first.requestClearConversation()
        first.clearConversation()

        XCTAssertTrue(first.conversation.isEmpty)
        XCTAssertFalse(first.isConfirmingClearConversation)

        let second = try makeViewModel(defaults: defaults)
        XCTAssertTrue(
            second.conversation.isEmpty,
            "It stays cleared on the next launch"
        )
    }

    func testThereIsNothingToAskAboutWhenItIsAlreadyEmpty() throws {
        let viewModel = try makeViewModel()

        viewModel.requestClearConversation()

        XCTAssertFalse(viewModel.isConfirmingClearConversation)
    }

    // MARK: - A long record does not bury the panel

    /// The field to ask again, and the whole drafting section, sit below this.
    /// Weeks of scrollback in front of them would be worse than no record.
    func testOnlyTheRecentExchangesAreShownAtFirst() async throws {
        let viewModel = try makeViewModel()
        for index in 0..<8 {
            await viewModel.ask("question \(index)")
        }

        XCTAssertEqual(viewModel.conversation.count, 16)
        XCTAssertEqual(
            viewModel.recentConversation.count,
            MainViewModel.recentConversationLimit
        )
        XCTAssertEqual(
            viewModel.earlierConversationCount,
            16 - MainViewModel.recentConversationLimit
        )
    }

    /// Shown newest-last, so the last thing on screen is the last thing said.
    func testTheRecentOnesAreTheNewestOnes() async throws {
        let viewModel = try makeViewModel()
        for index in 0..<8 {
            await viewModel.ask("question \(index)")
        }

        XCTAssertEqual(viewModel.recentConversation.last?.text, viewModel.conversation.last?.text)
        XCTAssertEqual(viewModel.recentConversation.first?.text, "question 5")
    }

    func testAShortConversationIsShownWhole() async throws {
        let viewModel = try makeViewModel()
        await viewModel.ask("what is most urgent?")

        XCTAssertEqual(viewModel.earlierConversationCount, 0)
        XCTAssertEqual(viewModel.recentConversation.count, 2)
    }

    // MARK: - The log itself

    func testTheOldestGoWhenItGetsTooLong() {
        var log = AssistantConversationLog.empty
        let total = AssistantConversationLog.maximumMessages + 10
        for index in 0..<total {
            log.append(AssistantMessage(role: .user, text: "message \(index)"))
        }

        XCTAssertEqual(log.messages.count, AssistantConversationLog.maximumMessages)
        XCTAssertEqual(log.messages.first?.text, "message 10")
        XCTAssertEqual(log.messages.last?.text, "message \(total - 1)")
    }

    func testAnEmptyTurnIsNotKept() {
        var log = AssistantConversationLog.empty
        log.append(AssistantMessage(role: .assistant, text: "   \n "))

        XCTAssertTrue(log.isEmpty, "There is nothing to read back")
    }

    func testItSurvivesBeingWrittenAndReadAgain() throws {
        var log = AssistantConversationLog.empty
        log.append(AssistantMessage(role: .user, text: "what is most urgent?"))
        log.append(AssistantMessage(role: .assistant, text: "The prototype video."))

        let data = try JSONEncoder().encode(log)
        let decoded = try JSONDecoder().decode(
            AssistantConversationLog.self,
            from: data
        )

        XCTAssertEqual(decoded, log)
        XCTAssertEqual(decoded.messages.map(\.role), [.user, .assistant])
    }

    /// The same promise the labels make: something written by a newer build is
    /// left alone rather than rewritten over with what this build understands.
    func testALogFromANewerBuildReadsAsEmpty() throws {
        let json = """
        {"version": \(AssistantConversationLog.currentVersion + 1),
         "messages": [{"id":"\(UUID().uuidString)","role":"user",
                       "text":"from the future","date":0}]}
        """
        let decoded = try JSONDecoder().decode(
            AssistantConversationLog.self,
            from: Data(json.utf8)
        )

        XCTAssertTrue(decoded.isEmpty)
    }

    func testAnUnreadableLogDoesNotTakeTheRestOfSettingsWithIt() {
        let suiteName = "AssistantConversationHistoryTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }

        defaults.set(
            Data("not json".utf8),
            forKey: "CanvasCountdown.settings.assistantConversation"
        )
        defaults.set(true, forKey: "CanvasCountdown.settings.launchAtLogin")

        let store = SettingsStore(defaults: defaults)

        XCTAssertTrue(store.assistantConversation.isEmpty)
        XCTAssertTrue(store.launchAtLogin, "The rest of the settings still load")
    }

    /// Settings are reset to defaults; the record of what was asked is not a
    /// preference and is not swept up by that.
    func testResettingSettingsKeepsTheConversation() {
        let suiteName = "AssistantConversationHistoryTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let store = SettingsStore(defaults: defaults)
        store.assistantConversation.append(
            AssistantMessage(role: .user, text: "what is most urgent?")
        )

        store.reset()

        XCTAssertEqual(store.assistantConversation.messages.count, 1)
    }

    // MARK: - Helpers

    private func makeViewModel(
        defaults: UserDefaults? = nil,
        service: (any AssistantServicing)? = nil
    ) throws -> MainViewModel {
        let store: UserDefaults
        if let defaults {
            store = defaults
        } else {
            let suiteName = "AssistantConversationHistoryTests.\(UUID().uuidString)"
            store = UserDefaults(suiteName: suiteName)!
            store.removePersistentDomain(forName: suiteName)
        }

        let container = try ModelContainer(
            for: AssignmentEvent.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
        let assistant = service ?? ConversationAssistantStub()
        return MainViewModel(
            repository: SwiftDataAssignmentRepository(modelContainer: container),
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: SettingsStore(defaults: store),
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            calendar: calendar,
            automaticActivityEnabled: false,
            courseBlocklist: IsolatedCourseBlocklistStore(),
            assistantFactory: { _, _, _ in assistant }
        )
    }

    private func makeFailingViewModel() throws -> MainViewModel {
        try makeViewModel(service: ConversationAssistantStub(fails: true))
    }
}

/// Answers questions with a fixed sentence, or refuses to.
struct ConversationAssistantStub: AssistantServicing {
    var fails = false

    func answer(
        _ question: String,
        history: [AssistantMessage],
        digests: [AssistantAssignmentDigest],
        now: Date
    ) async throws -> String {
        if fails {
            throw AssistantError.unreachable
        }
        return "Nothing is due before the prototype video."
    }

    func summarise(
        _ digests: [AssistantAssignmentDigest],
        now: Date
    ) async throws -> String { "" }

    func draftTasks(
        from text: String,
        now: Date
    ) async throws -> [AssistantDraftTask] { [] }

    func reviseDrafts(
        _ drafts: [AssistantDraftTask],
        instruction: String,
        now: Date
    ) async throws -> [AssistantDraftTask] { drafts }
}

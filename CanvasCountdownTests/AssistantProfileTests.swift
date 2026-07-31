import AppKit
import Foundation
import SwiftData
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// Saved models, what the assistant is allowed to add, and taking that back.
///
/// No test makes a real network request or touches the real Keychain. Every
/// fixture is invented.
final class AssistantProfileTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    // MARK: - The library

    func testNamesAreUniqueWhateverTheirCase() {
        var library = AssistantProfileLibrary.empty
        library.add(AssistantProfile(service: .groq))

        XCTAssertEqual(library.validate(name: "groq"), .duplicate)
        XCTAssertNil(library.validate(name: "Groq at home"))
    }

    func testAddingTheSameServiceTwiceNumbersTheSecond() {
        var library = AssistantProfileLibrary.empty
        library.add(AssistantProfile(service: .groq))
        library.add(AssistantProfile(service: .groq))

        XCTAssertEqual(library.profiles.map(\.name), ["Groq", "Groq 2"])
    }

    func testDuplicatingCopiesTheSettingsButNotTheName() throws {
        var library = AssistantProfileLibrary.empty
        let id = try XCTUnwrap(library.add(AssistantProfile(service: .openAI)))

        let copyID = try XCTUnwrap(library.duplicate(id))
        let original = try XCTUnwrap(library.profile(withID: id))
        let copy = try XCTUnwrap(library.profile(withID: copyID))

        XCTAssertEqual(copy.baseURL, original.baseURL)
        XCTAssertEqual(copy.model, original.model)
        XCTAssertNotEqual(copy.name, original.name)
        XCTAssertNotEqual(copy.id, original.id)
    }

    func testRenamingToATakenNameIsRefusedAndChangesNothing() throws {
        var library = AssistantProfileLibrary.empty
        library.add(AssistantProfile(service: .groq))
        let secondID = try XCTUnwrap(
            library.add(AssistantProfile(service: .openAI))
        )

        XCTAssertEqual(library.rename(secondID, to: "Groq"), .duplicate)
        XCTAssertEqual(library.profile(withID: secondID)?.name, "OpenAI")
    }

    func testAProfileWithNoUsableAddressIsNotKept() {
        var library = AssistantProfileLibrary.empty

        XCTAssertNil(
            library.add(
                AssistantProfile(
                    name: "Broken",
                    provider: .cloud,
                    baseURL: "not a url at all",
                    model: "x"
                )
            )
        )
        XCTAssertTrue(library.profiles.isEmpty)
    }

    func testTheLibraryStopsAtItsLimit() {
        var library = AssistantProfileLibrary.empty
        for index in 0..<(AssistantProfileLibrary.maximumCount + 3) {
            library.add(
                AssistantProfile(
                    name: "Model \(index)",
                    provider: .cloud,
                    baseURL: "https://example.com/v1",
                    model: "m"
                )
            )
        }

        XCTAssertEqual(
            library.profiles.count,
            AssistantProfileLibrary.maximumCount
        )
        XCTAssertTrue(library.isFull)
    }

    /// The same rule the Dock presets and the labels follow: a library written
    /// by a newer build is shown as empty rather than half-read and rewritten.
    func testALibraryFromANewerBuildIsLeftAlone() throws {
        let stored = Data(#"{"version":99,"profiles":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","name":"Future","provider":"cloud","baseURL":"https://example.com/v1","model":"x"}]}"#.utf8)

        let decoded = try JSONDecoder().decode(
            AssistantProfileLibrary.self,
            from: stored
        )

        XCTAssertTrue(decoded.profiles.isEmpty)
    }

    func testPrivacyIsJudgedByTheAddressNotTheName() {
        let liar = AssistantProfile(
            name: "On this Mac",
            provider: .local,
            baseURL: "https://example.com/v1",
            model: "x"
        )

        XCTAssertFalse(
            liar.staysOnThisMac,
            "Calling it local does not keep the data here"
        )
        XCTAssertTrue(AssistantProfile(service: .ollama).staysOnThisMac)
    }

    // MARK: - Upgrading from the single configuration

    func testTheExistingConfigurationBecomesTheFirstSavedModel() {
        var settings = AssistantSettings.defaults
        settings.provider = .cloud
        settings.baseURL = AssistantService.groq.baseURL
        settings.model = "llama-3.3-70b-versatile"

        let library = AssistantProfileLibrary.migrated(from: settings)

        XCTAssertEqual(library.profiles.count, 1)
        XCTAssertEqual(library.profiles[0].name, "Groq")
        XCTAssertEqual(library.profiles[0].baseURL, settings.baseURL)
        XCTAssertEqual(library.profiles[0].model, settings.model)
        XCTAssertEqual(library.profiles[0].provider, .cloud)
    }

    func testAnUnrecognisedAddressStillMigratesUnderTheProviderName() {
        var settings = AssistantSettings.defaults
        settings.provider = .cloud
        settings.baseURL = "https://ai.example.edu/v1"
        settings.model = "campus-model"

        let library = AssistantProfileLibrary.migrated(from: settings)

        XCTAssertEqual(library.profiles.count, 1)
        XCTAssertEqual(library.profiles[0].name, "AI (Cloud)")
        XCTAssertEqual(library.profiles[0].baseURL, "https://ai.example.edu/v1")
    }

    @MainActor
    func testTheFirstLaunchNamesTheExistingSetupAndSelectsIt() async throws {
        let harness = try makeHarness()
        harness.settings.assistant = AssistantSettings.applying(
            .cloud,
            to: .defaults
        )

        await harness.viewModel.start()

        XCTAssertEqual(harness.viewModel.assistantProfiles.count, 1)
        XCTAssertEqual(
            harness.viewModel.activeAssistantProfile?.id,
            harness.viewModel.assistantProfiles.first?.id,
            "An upgrade must not leave the assistant pointing at nothing"
        )
    }

    @MainActor
    func testTheMigrationRunsOnceAndDoesNotKeepAddingModels() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        let firstID = harness.viewModel.assistantProfiles.first?.id

        let second = makeViewModel(
            settings: harness.settings,
            repository: harness.repository
        )
        await second.start()

        XCTAssertEqual(second.assistantProfiles.count, 1)
        XCTAssertEqual(second.assistantProfiles.first?.id, firstID)
    }

    // MARK: - Switching between them

    @MainActor
    func testSwitchingModelCarriesItsAddressAndModelName() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        let groqID = try XCTUnwrap(
            harness.viewModel.addAssistantProfile(
                AssistantProfile(service: .groq)
            )
        )
        let localID = try XCTUnwrap(
            harness.viewModel.assistantProfiles.first { $0.id != groqID }?.id
        )

        harness.viewModel.selectAssistantProfile(localID)

        XCTAssertEqual(
            harness.viewModel.assistantSettings.activeProfileID,
            localID
        )
        XCTAssertTrue(harness.viewModel.assistantStaysOnThisMac)

        harness.viewModel.selectAssistantProfile(groqID)

        XCTAssertEqual(
            harness.viewModel.assistantSettings.baseURL,
            AssistantService.groq.baseURL
        )
        XCTAssertFalse(harness.viewModel.assistantStaysOnThisMac)
    }

    /// There is no separate save step, so the fields and the saved model must
    /// never be allowed to disagree.
    @MainActor
    func testEditingTheAddressEditsTheModelItBelongsTo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        let activeID = try XCTUnwrap(
            harness.viewModel.activeAssistantProfile?.id
        )

        var form = harness.viewModel.settingsForm
        form.assistant.baseURL = "http://localhost:1234/v1"
        form.assistant.model = "something-else"
        harness.viewModel.applySettings(form)

        let stored = try XCTUnwrap(
            harness.viewModel.assistantProfiles.first { $0.id == activeID }
        )
        XCTAssertEqual(stored.baseURL, "http://localhost:1234/v1")
        XCTAssertEqual(stored.model, "something-else")
    }

    @MainActor
    func testDeletingTheModelInUseMovesToAnotherRatherThanNone() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        let addedID = try XCTUnwrap(
            harness.viewModel.addAssistantProfile(
                AssistantProfile(service: .groq)
            )
        )
        XCTAssertEqual(
            harness.viewModel.assistantSettings.activeProfileID,
            addedID
        )

        harness.viewModel.deleteAssistantProfile(addedID)

        XCTAssertEqual(harness.viewModel.assistantProfiles.count, 1)
        XCTAssertEqual(
            harness.viewModel.assistantSettings.activeProfileID,
            harness.viewModel.assistantProfiles.first?.id,
            "The assistant must never be left pointing at a deleted model"
        )
    }

    @MainActor
    func testTheToolbarOffersASwitcherOnlyOnceThereIsSomethingToSwitchTo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        XCTAssertFalse(
            harness.viewModel.showsAssistantModelSwitcher,
            "One model is just the model; a switcher would be noise"
        )
        XCTAssertEqual(harness.viewModel.toolbarItemCount, 7)

        harness.viewModel.addAssistantProfile(AssistantProfile(service: .groq))

        XCTAssertTrue(harness.viewModel.showsAssistantModelSwitcher)
        XCTAssertEqual(harness.viewModel.toolbarItemCount, 8)
    }

    @MainActor
    func testTurningTheAssistantOffTakesTheSwitcherWithIt() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.viewModel.addAssistantProfile(AssistantProfile(service: .groq))
        XCTAssertTrue(harness.viewModel.showsAssistantModelSwitcher)

        var form = harness.viewModel.settingsForm
        form.assistant.isEnabled = false
        harness.viewModel.applySettings(form)

        XCTAssertFalse(harness.viewModel.showsAssistantModelSwitcher)
        XCTAssertEqual(harness.viewModel.toolbarItemCount, 7)
    }

    /// Search is a panel now, so it no longer evicts the switcher — or
    /// anything else — from the toolbar.
    @MainActor
    func testOpeningSearchLeavesTheSwitcherInTheToolbar() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.viewModel.addAssistantProfile(AssistantProfile(service: .groq))
        XCTAssertEqual(harness.viewModel.toolbarItemCount, 8)

        harness.viewModel.presentSearch()

        XCTAssertEqual(harness.viewModel.toolbarItemCount, 8)
        XCTAssertTrue(harness.viewModel.showsAssistantModelSwitcher)
        XCTAssertTrue(harness.viewModel.showsOrdinaryToolbarActions)
    }

    // MARK: - What the assistant adds, and taking it back

    @MainActor
    func testADraftKeepsItsCourseAndLabelWhenItIsSaved() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        let label = try XCTUnwrap(harness.viewModel.eventLabels.first)

        await harness.viewModel.saveAssistantDrafts([
            AssistantDraftTask(
                title: "  Essay draft  ",
                courseName: "  41021 Interaction Design Studio  ",
                labelID: label.id,
                dueDate: date(2026, 8, 20),
                sourceText: "essay draft due next friday"
            ),
        ])

        let saved = try XCTUnwrap(harness.viewModel.assignments.first)
        XCTAssertEqual(saved.title, "Essay draft")
        XCTAssertEqual(
            saved.normalizedCourseName,
            "41021 Interaction Design Studio"
        )
        XCTAssertEqual(saved.labelID, label.id)
    }

    @MainActor
    func testDraftsTheUserUncheckedAreNeverSaved() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        await harness.viewModel.saveAssistantDrafts([
            AssistantDraftTask(
                include: false,
                title: "Not this one",
                dueDate: date(2026, 8, 20),
                sourceText: "x"
            ),
            AssistantDraftTask(
                title: "This one",
                dueDate: date(2026, 8, 21),
                sourceText: "x"
            ),
        ])

        XCTAssertEqual(
            harness.viewModel.assignments.map(\.title),
            ["This one"]
        )
    }

    @MainActor
    func testADraftWithNoDateIsNeverSaved() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        await harness.viewModel.saveAssistantDrafts([
            AssistantDraftTask(
                title: "No date given",
                dueDate: nil,
                sourceText: "x"
            ),
        ])

        XCTAssertTrue(harness.viewModel.assignments.isEmpty)
        XCTAssertNil(
            harness.viewModel.undoableAddition,
            "Nothing was added, so there is nothing to offer to undo"
        )
    }

    @MainActor
    func testSavingABatchOffersToTakeAllOfItBack() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        await harness.viewModel.saveAssistantDrafts([
            draft("One", day: 20),
            draft("Two", day: 21),
            draft("Three", day: 22),
        ])

        let offer = try XCTUnwrap(harness.viewModel.undoableAddition)
        XCTAssertEqual(offer.eventIDs.count, 3)
        XCTAssertEqual(offer.message, "3 tasks added")
    }

    @MainActor
    func testUndoRemovesTheWholeBatchInOneGo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        await harness.viewModel.saveAssistantDrafts([
            draft("One", day: 20),
            draft("Two", day: 21),
            draft("Three", day: 22),
        ])
        XCTAssertEqual(harness.viewModel.assignments.count, 3)

        await harness.viewModel.undoLastAddition()

        XCTAssertTrue(harness.viewModel.assignments.isEmpty)
        XCTAssertNil(harness.viewModel.undoableAddition)
    }

    /// Undo means the batch, not the library. Anything the user put there by
    /// hand has to survive it.
    @MainActor
    func testUndoLeavesEverythingItDidNotAddAlone() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(title: "Mine", dueDate: date(2026, 8, 1))
        )

        await harness.viewModel.saveAssistantDrafts([draft("Theirs", day: 20)])
        await harness.viewModel.undoLastAddition()

        XCTAssertEqual(harness.viewModel.assignments.map(\.title), ["Mine"])
    }

    /// Putting the toast away is not the same as giving up the undo. The toast
    /// is a notice; the Edit menu is the control, and it outlives the notice.
    @MainActor
    func testDismissingTheToastKeepsBothTheEventsAndTheWayBack() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        await harness.viewModel.saveAssistantDrafts([draft("Keep me", day: 20)])
        XCTAssertTrue(harness.viewModel.isUndoToastVisible)

        harness.viewModel.dismissUndoToast()

        XCTAssertFalse(harness.viewModel.isUndoToastVisible)
        XCTAssertEqual(harness.viewModel.assignments.map(\.title), ["Keep me"])
        XCTAssertTrue(
            harness.viewModel.canUndoAddition,
            "The menu must still be able to take it back"
        )
    }

    /// Deleting a row by hand and then pressing Undo must not put an error on
    /// screen for work that is already done.
    @MainActor
    func testUndoSurvivesSomethingHavingAlreadyBeenDeleted() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        await harness.viewModel.saveAssistantDrafts([
            draft("One", day: 20),
            draft("Two", day: 21),
        ])
        let first = try XCTUnwrap(harness.viewModel.assignments.first)
        harness.viewModel.deleteManualEvent(first)
        try await Task.sleep(for: .milliseconds(120))

        await harness.viewModel.undoLastAddition()

        XCTAssertTrue(harness.viewModel.assignments.isEmpty)
        XCTAssertNil(harness.viewModel.errorMessage)
    }

    @MainActor
    func testASecondBatchReplacesTheOfferRatherThanQueueingIt() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        await harness.viewModel.saveAssistantDrafts([draft("First", day: 20)])

        await harness.viewModel.saveAssistantDrafts([draft("Second", day: 21)])
        let offer = try XCTUnwrap(harness.viewModel.undoableAddition)
        await harness.viewModel.undoLastAddition()

        XCTAssertEqual(offer.message, "1 task added")
        XCTAssertEqual(
            harness.viewModel.assignments.map(\.title),
            ["First"],
            "Undo means the last thing that happened, not everything"
        )
    }

    // MARK: - Undo reaches an event added by hand, too

    /// The way back must not depend on which door the event came in through.
    @MainActor
    func testAddingAnEventByHandCanBeUndone() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(title: "Typed by hand", dueDate: date(2026, 8, 20))
        )

        let offer = try XCTUnwrap(harness.viewModel.undoableAddition)
        XCTAssertEqual(offer.eventIDs.count, 1)
        XCTAssertEqual(offer.message, "Added “Typed by hand”")
        XCTAssertTrue(harness.viewModel.isUndoToastVisible)

        await harness.viewModel.undoLastAddition()

        XCTAssertTrue(harness.viewModel.assignments.isEmpty)
        XCTAssertFalse(harness.viewModel.canUndoAddition)
    }

    /// Editing is not adding. Offering to "undo" an edit by deleting the event
    /// would destroy something that existed before the edit.
    @MainActor
    func testEditingAnExistingEventOffersNoUndo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(title: "Original", dueDate: date(2026, 8, 20))
        )
        let saved = try XCTUnwrap(harness.viewModel.assignments.first)
        harness.viewModel.dismissUndoToast()

        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(
                eventID: saved.id,
                title: "Renamed",
                dueDate: date(2026, 8, 20)
            )
        )

        XCTAssertEqual(harness.viewModel.statusMessage, "Event updated")
        XCTAssertFalse(
            harness.viewModel.isUndoToastVisible,
            "An edit is not an addition"
        )
        XCTAssertEqual(
            harness.viewModel.assignments.map(\.title),
            ["Renamed"]
        )
    }

    /// The menu is the part that does not assume anyone knows a shortcut, so
    /// it has to say what it would take back.
    @MainActor
    func testTheMenuNamesWhatItWouldTakeBack() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        XCTAssertFalse(harness.viewModel.canUndoAddition)
        XCTAssertEqual(harness.viewModel.undoAdditionMenuTitle, "Undo Adding")

        try await harness.viewModel.saveManualEvent(
            ManualEventDraft(title: "Reading week plan", dueDate: date(2026, 8, 20))
        )
        XCTAssertEqual(
            harness.viewModel.undoAdditionMenuTitle,
            "Undo Adding “Reading week plan”"
        )

        await harness.viewModel.saveAssistantDrafts([
            draft("One", day: 21),
            draft("Two", day: 22),
        ])
        XCTAssertEqual(
            harness.viewModel.undoAdditionMenuTitle,
            "Undo Adding 2 Tasks"
        )
    }

    /// The toast counts itself down and leaves. What it offered does not leave
    /// with it — that is what makes a ten-second toast safe to build on.
    @MainActor
    func testTheToastCountsDownAndTheUndoOutlivesIt() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        await harness.viewModel.saveAssistantDrafts([draft("Watch me", day: 20)])

        XCTAssertEqual(
            harness.viewModel.undoToastSecondsRemaining,
            MainViewModel.undoToastDuration
        )

        // Waiting out ten real seconds would only prove the clock works, so
        // this checks the state the countdown lands on instead.
        harness.viewModel.dismissUndoToast()

        XCTAssertNil(harness.viewModel.undoToastSecondsRemaining)
        XCTAssertTrue(harness.viewModel.canUndoAddition)

        await harness.viewModel.undoLastAddition()
        XCTAssertTrue(harness.viewModel.assignments.isEmpty)
    }

    // MARK: - The toolbar default

    @MainActor
    func testTheFirstLaunchChoosesIconAndText() {
        let defaults = makeDefaults()

        XCTAssertEqual(
            ToolbarDisplayModeConfigurator.startingMode(defaults: defaults),
            .iconAndLabel
        )
    }

    /// The toolbar has no autosaved configuration of its own, so a mode set
    /// once is gone by the next launch. Remembering it is the whole point.
    @MainActor
    func testARememberedChoiceIsPutBackOnEveryLaunch() {
        let defaults = makeDefaults()
        defaults.set(
            NSToolbar.DisplayMode.iconOnly.rawValue,
            forKey: ToolbarDisplayModeConfigurator.storedModeKey
        )

        XCTAssertEqual(
            ToolbarDisplayModeConfigurator.startingMode(defaults: defaults),
            .iconOnly,
            "Going back to icons only is the user's choice to keep"
        )
    }

    @MainActor
    func testAnUnreadableStoredModeFallsBackToTheDefault() {
        let defaults = makeDefaults()
        defaults.set(
            9_999,
            forKey: ToolbarDisplayModeConfigurator.storedModeKey
        )

        XCTAssertNil(
            ToolbarDisplayModeConfigurator.storedMode(defaults: defaults)
        )
        XCTAssertEqual(
            ToolbarDisplayModeConfigurator.startingMode(defaults: defaults),
            .iconAndLabel
        )
    }

    /// An isolated launch must not write into the real user's preferences.
    @MainActor
    func testAnIsolatedLaunchKeepsItsOwnPreferenceDomain() {
        XCTAssertNotNil(AppEnvironment.automatedTesting.preferencesSuiteName)
        XCTAssertNil(AppEnvironment.production.preferencesSuiteName)
        XCTAssertFalse(
            AppEnvironment.automatedTesting.preferences === UserDefaults.standard
        )
    }

    private func makeDefaults() -> UserDefaults {
        let suiteName = "ToolbarDefault.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        return defaults
    }

    // MARK: - Helpers

    private func date(_ year: Int, _ month: Int, _ day: Int) -> Date {
        calendar.date(
            from: DateComponents(year: year, month: month, day: day, hour: 17)
        )!
    }

    private func draft(_ title: String, day: Int) -> AssistantDraftTask {
        AssistantDraftTask(
            title: title,
            dueDate: date(2026, 8, day),
            sourceText: "invented"
        )
    }

    @MainActor
    private func makeHarness() throws -> Harness {
        let suiteName = "AssistantProfileTests.\(UUID().uuidString)"
        let store = UserDefaults(suiteName: suiteName)!
        store.removePersistentDomain(forName: suiteName)

        let container = try ModelContainer(
            for: AssignmentEvent.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
        let repository = SwiftDataAssignmentRepository(modelContainer: container)
        let settings = SettingsStore(defaults: store)
        return Harness(
            viewModel: makeViewModel(settings: settings, repository: repository),
            settings: settings,
            repository: repository
        )
    }

    @MainActor
    private func makeViewModel(
        settings: SettingsStore,
        repository: any AssignmentRepository
    ) -> MainViewModel {
        MainViewModel(
            repository: repository,
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: settings,
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            calendar: calendar,
            automaticActivityEnabled: false,
            // Never the real Keychain: a test must not read or write the key
            // an actual install depends on.
            assistantKeyStore: KeychainAssistantKeyStore(
                service: "com.local.CanvasCountdown.tests.\(UUID().uuidString)",
                account: "assistant-api-key"
            ),
            courseBlocklist: IsolatedCourseBlocklistStore()
        )
    }

    @MainActor
    private struct Harness {
        let viewModel: MainViewModel
        let settings: SettingsStore
        let repository: any AssignmentRepository
    }
}

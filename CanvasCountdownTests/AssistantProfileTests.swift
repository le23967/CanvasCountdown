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

    func testDuplicatingALongNameStillGivesAReadableName() throws {
        var library = AssistantProfileLibrary.empty
        let long = "Model name for a long and careful test"
        XCTAssertLessThanOrEqual(long.count, AssistantProfileLibrary.maximumNameLength)
        let id = try XCTUnwrap(
            library.add(
                AssistantProfile(
                    name: long,
                    provider: .cloud,
                    baseURL: "https://example.com/v1",
                    model: "m"
                )
            )
        )

        let copyID = try XCTUnwrap(library.duplicate(id))
        let copy = try XCTUnwrap(library.profile(withID: copyID))

        XCTAssertNil(UUID(uuidString: copy.name), "Not named after a UUID")
        XCTAssertTrue(copy.name.hasPrefix("Model name"))
        XCTAssertTrue(copy.name.hasSuffix(" Copy"), copy.name)
        XCTAssertLessThanOrEqual(copy.name.count, AssistantProfileLibrary.maximumNameLength)
        XCTAssertNotEqual(copy.name.lowercased(), long.lowercased())
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

    /// The address field writes through on every keystroke. A half-typed
    /// address used to be saved as it stood, and the next launch dropped the
    /// whole model for having no usable address.
    func testAHalfTypedAddressIsNotWrittenOverAGoodOne() throws {
        var library = AssistantProfileLibrary.empty
        let id = try XCTUnwrap(library.add(AssistantProfile(service: .deepSeek)))

        XCTAssertFalse(
            library.update(
                id,
                provider: .cloud,
                baseURL: "api.deepseek.com/v1",
                model: "deepseek-chat"
            ),
            "No scheme means no host, and a model with no host cannot be selected"
        )
        XCTAssertEqual(
            library.profile(withID: id)?.baseURL,
            AssistantService.deepSeek.baseURL
        )

        let reread = try JSONDecoder().decode(
            AssistantProfileLibrary.self,
            from: JSONEncoder().encode(library)
        )
        XCTAssertEqual(reread.profiles.map(\.id), [id], "Still there after a relaunch")
    }

    /// A library saved before keys were tied to a host has no such field, and
    /// has to read exactly as it did.
    func testALibrarySavedBeforeKeyHostsStillReads() throws {
        let stored = Data(#"{"version":1,"profiles":[{"id":"4C1D0E9A-0000-4000-A000-000000000002","name":"Example","provider":"cloud","baseURL":"https://example.com/v1","model":"x"}]}"#.utf8)

        let decoded = try JSONDecoder().decode(
            AssistantProfileLibrary.self,
            from: stored
        )

        XCTAssertEqual(decoded.profiles.map(\.name), ["Example"])
        XCTAssertNil(decoded.profiles.first?.keyHost)
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

    // MARK: - A key stays with the service it was saved for

    /// The suggested services under the address field rewrite the address of
    /// the model in use. Its key used to go with it: a key saved for one
    /// service was sent, with the very next question, to whichever service
    /// had been clicked.
    @MainActor
    func testAKeyIsNeverSentToAnAddressItWasNotSavedFor() async throws {
        let recorder = KeyRecorder()
        let viewModel = try makeViewModel(recorder: recorder)
        await viewModel.start()

        viewModel.addAssistantProfile(for: .groq)
        viewModel.saveAssistantKey("gsk-example-for-groq")
        try await waitUntil { viewModel.assistantAPIKey == "gsk-example-for-groq" }

        await viewModel.ask("what is due?")
        XCTAssertEqual(recorder.last?.host, "api.groq.com")
        XCTAssertEqual(recorder.last?.key, "gsk-example-for-groq")
        XCTAssertNil(viewModel.assistantKeyNote)

        viewModel.useAssistantService(.deepSeek)
        await viewModel.ask("what is due?")

        XCTAssertEqual(recorder.last?.host, "api.deepseek.com")
        XCTAssertNil(
            recorder.last?.key,
            "A key saved for one service must not be handed to another"
        )
        XCTAssertNil(viewModel.assistantKeyForCurrentAddress)
        XCTAssertEqual(
            viewModel.assistantKeyNote,
            "The key saved with this model is for api.groq.com, so it is not sent to api.deepseek.com. Save a key for this address to use it, or put the address back."
        )

        // Nothing was thrown away: the address goes back, and so does the key.
        viewModel.useAssistantService(.groq)
        await viewModel.ask("what is due?")
        XCTAssertEqual(recorder.last?.host, "api.groq.com")
        XCTAssertEqual(recorder.last?.key, "gsk-example-for-groq")
        XCTAssertNil(viewModel.assistantKeyNote)
    }

    @MainActor
    func testSavingAKeyForTheNewAddressMakesItThatAddressesKey() async throws {
        let recorder = KeyRecorder()
        let viewModel = try makeViewModel(recorder: recorder)
        await viewModel.start()
        viewModel.addAssistantProfile(for: .groq)
        viewModel.saveAssistantKey("gsk-example-for-groq")
        try await waitUntil { viewModel.assistantAPIKey == "gsk-example-for-groq" }

        viewModel.useAssistantService(.deepSeek)
        viewModel.saveAssistantKey("sk-example-for-deepseek")
        try await waitUntil { viewModel.assistantAPIKey == "sk-example-for-deepseek" }

        await viewModel.ask("what is due?")
        XCTAssertEqual(recorder.last?.host, "api.deepseek.com")
        XCTAssertEqual(recorder.last?.key, "sk-example-for-deepseek")
        XCTAssertEqual(viewModel.activeAssistantProfile?.keyHost, "api.deepseek.com")
    }

    /// A key saved by an earlier version has no host recorded. It is tied to
    /// the address it is sitting beside, which is the only one it can mean.
    @MainActor
    func testAKeySavedBeforeHostsWereRecordedIsTiedToItsAddress() async throws {
        let suiteName = "AssistantProfileTests.\(UUID().uuidString)"
        let store = UserDefaults(suiteName: suiteName)!
        store.removePersistentDomain(forName: suiteName)
        let settings = SettingsStore(defaults: store)
        var library = AssistantProfileLibrary.empty
        let id = try XCTUnwrap(library.add(AssistantProfile(service: .groq)))
        settings.assistantProfiles = library
        settings.assistant = AssistantSettings.applying(
            try XCTUnwrap(library.profile(withID: id)),
            to: settings.assistant
        )
        let keys = IsolatedAssistantKeyStore()
        await keys.save("gsk-example", for: id)

        let recorder = KeyRecorder()
        let viewModel = try makeViewModel(
            settings: settings,
            keys: keys,
            recorder: recorder
        )
        await viewModel.start()

        XCTAssertEqual(viewModel.activeAssistantProfile?.keyHost, "api.groq.com")
        await viewModel.ask("what is due?")
        XCTAssertEqual(recorder.last?.key, "gsk-example", "It works as it did")

        viewModel.useAssistantService(.openRouter)
        await viewModel.ask("what is due?")
        XCTAssertNil(recorder.last?.key)
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
    func testTheAssistantOffersAPickerOnlyOnceThereIsSomethingToSwitchTo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()

        XCTAssertFalse(
            harness.viewModel.showsAssistantModelPicker,
            "One model is just the model; a picker would be noise"
        )

        harness.viewModel.addAssistantProfile(AssistantProfile(service: .groq))

        XCTAssertTrue(harness.viewModel.showsAssistantModelPicker)
    }

    @MainActor
    func testTurningTheAssistantOffTakesThePickerWithIt() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        harness.viewModel.addAssistantProfile(AssistantProfile(service: .groq))
        XCTAssertTrue(harness.viewModel.showsAssistantModelPicker)

        var form = harness.viewModel.settingsForm
        form.assistant.isEnabled = false
        harness.viewModel.applySettings(form)

        XCTAssertFalse(harness.viewModel.showsAssistantModelPicker)
    }

    /// The picker moved into the assistant, beside the sentence it will answer,
    /// so saving a second model no longer changes the shape of the toolbar.
    @MainActor
    func testTheToolbarIsTheSameSixWhateverTheAssistantIsSetTo() async throws {
        let harness = try makeHarness()
        await harness.viewModel.start()
        XCTAssertEqual(harness.viewModel.toolbarItemCount, 6)

        harness.viewModel.addAssistantProfile(AssistantProfile(service: .groq))
        XCTAssertEqual(harness.viewModel.toolbarItemCount, 6)

        harness.viewModel.presentSearch()

        XCTAssertEqual(harness.viewModel.toolbarItemCount, 6)
        XCTAssertTrue(harness.viewModel.showsAssistantModelPicker)
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

    /// A view model whose keys live in memory and whose assistant only notes
    /// what it was handed.
    @MainActor
    private func makeViewModel(
        settings: SettingsStore? = nil,
        keys: IsolatedAssistantKeyStore = IsolatedAssistantKeyStore(),
        recorder: KeyRecorder
    ) throws -> MainViewModel {
        let store: SettingsStore
        if let settings {
            store = settings
        } else {
            let suiteName = "AssistantProfileTests.\(UUID().uuidString)"
            let defaults = UserDefaults(suiteName: suiteName)!
            defaults.removePersistentDomain(forName: suiteName)
            store = SettingsStore(defaults: defaults)
        }
        let container = try ModelContainer(
            for: AssignmentEvent.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
        return MainViewModel(
            repository: SwiftDataAssignmentRepository(modelContainer: container),
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: store,
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            calendar: calendar,
            automaticActivityEnabled: false,
            assistantKeyStore: keys,
            courseBlocklist: IsolatedCourseBlocklistStore(),
            assistantFactory: { settings, apiKey, _ in
                recorder.record(
                    host: settings.resolvedURL?.host,
                    key: apiKey
                )
                return ConversationAssistantStub()
            }
        )
    }

    @MainActor
    private func waitUntil(
        _ condition: @MainActor () -> Bool
    ) async throws {
        for _ in 0..<400 {
            if condition() {
                return
            }
            try await Task.sleep(for: .milliseconds(5))
        }
        XCTFail("Timed out waiting for the view model to update")
    }

    @MainActor
    private struct Harness {
        let viewModel: MainViewModel
        let settings: SettingsStore
        let repository: any AssignmentRepository
    }
}

/// Notes which host a request was built for and which key went with it.
@MainActor
private final class KeyRecorder {
    struct Request {
        let host: String?
        let key: String?
    }

    private(set) var last: Request?

    func record(host: String?, key: String?) {
        last = Request(host: host, key: key)
    }
}

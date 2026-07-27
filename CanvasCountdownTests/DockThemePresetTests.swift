import Foundation
import XCTest
@testable import CanvasCountdown

/// Saved Dock themes: naming rules, what survives a restart, and what the
/// theme row claims is in force. All fixtures are invented.
@MainActor
final class DockThemePresetTests: XCTestCase {
    private func colours(
        top: String = "#06381C",
        bottom: String = "#0E5C30",
        number: String = "#FFFFFF",
        label: String = "#EAFBF1"
    ) -> DockAppearance.Colours {
        DockAppearance.Colours(
            backgroundTop: top,
            backgroundBottom: bottom,
            number: number,
            label: label
        )
    }

    private func library(_ names: [String]) -> DockThemePresetLibrary {
        var library = DockThemePresetLibrary.empty
        for name in names {
            library.add(name: name, colours: colours())
        }
        return library
    }

    // MARK: - Naming

    func testANameIsTrimmedBeforeItIsStored() {
        var library = DockThemePresetLibrary.empty
        let id = library.add(name: "   Green Gradient  ", colours: colours())

        XCTAssertEqual(library.preset(withID: try! XCTUnwrap(id))?.name, "Green Gradient")
    }

    func testAnEmptyNameIsRefused() {
        let result = DockThemePresetLibrary.validate(name: "   ", in: .empty)

        XCTAssertEqual(result, .failure(.empty))
    }

    func testANameLongerThanFortyCharactersIsRefused() {
        let long = String(repeating: "a", count: 41)

        XCTAssertEqual(
            DockThemePresetLibrary.validate(name: long, in: .empty),
            .failure(.tooLong)
        )
        XCTAssertEqual(
            DockThemePresetLibrary.validate(
                name: String(repeating: "a", count: 40),
                in: .empty
            ),
            .success(String(repeating: "a", count: 40))
        )
    }

    func testANameAlreadyUsedByAnotherPresetIsRefused() {
        let existing = library(["Green Gradient"])

        XCTAssertEqual(
            DockThemePresetLibrary.validate(name: "green gradient", in: existing),
            .failure(.duplicate),
            "Case is not enough to tell two rows apart"
        )
    }

    func testANameAlreadyUsedByABuiltInThemeIsRefused() {
        for preset in DockThemePreset.selectable {
            XCTAssertEqual(
                DockThemePresetLibrary.validate(name: preset.title, in: .empty),
                .failure(.duplicate)
            )
        }
        XCTAssertEqual(
            DockThemePresetLibrary.validate(name: "Custom", in: .empty),
            .failure(.duplicate)
        )
    }

    func testRenamingIgnoresThePresetsOwnName() {
        var existing = library(["Green Gradient"])
        let id = existing.presets[0].id

        XCTAssertTrue(existing.rename(id, to: "Green Gradient"))
        XCTAssertTrue(existing.rename(id, to: "Forest"))
        XCTAssertEqual(existing.presets[0].name, "Forest")
    }

    func testRenamingOntoAnotherPresetsNameIsRefused() {
        var existing = library(["Green Gradient", "Forest"])
        let id = existing.presets[0].id

        XCTAssertFalse(existing.rename(id, to: "Forest"))
        XCTAssertEqual(existing.presets[0].name, "Green Gradient")
    }

    // MARK: - Duplicating

    func testDuplicatingAppendsCopy() throws {
        var existing = library(["Green Gradient"])
        let id = existing.presets[0].id

        let copy = try XCTUnwrap(existing.duplicate(id))

        XCTAssertEqual(existing.preset(withID: copy)?.name, "Green Gradient Copy")
        XCTAssertEqual(existing.preset(withID: copy)?.colours, colours())
        XCTAssertEqual(existing.presets.count, 2)
    }

    func testDuplicatingTwiceStillProducesAUsableName() throws {
        var existing = library(["Green Gradient"])
        let id = existing.presets[0].id

        existing.duplicate(id)
        let second = try XCTUnwrap(existing.duplicate(id))

        XCTAssertEqual(existing.preset(withID: second)?.name, "Green Gradient Copy 2")
        XCTAssertEqual(existing.presets.count, 3)
    }

    // MARK: - Deleting

    func testDeletingRemovesOnlyThatPreset() {
        var existing = library(["Green Gradient", "Forest"])
        let id = existing.presets[0].id

        existing.remove(id)

        XCTAssertEqual(existing.presets.map(\.name), ["Forest"])
    }

    // MARK: - What the theme row says

    func testABuiltInThemeIsNamedAsItself() {
        let appearance = DockAppearance.applying(.dark, to: .defaults)

        XCTAssertEqual(
            DockThemePresetLibrary.empty.status(for: appearance),
            .builtIn(.dark)
        )
        XCTAssertEqual(
            DockThemePresetLibrary.empty.status(for: appearance).title,
            "Dark"
        )
    }

    func testEditingABuiltInThemeIsUnsaved() {
        var appearance = DockAppearance.applying(.dark, to: .defaults)
        appearance.colours.backgroundTop = "#0A5C2E"
        appearance.preset = .custom

        let status = DockThemePresetLibrary.empty.status(for: appearance)

        XCTAssertEqual(status, .unsaved)
        XCTAssertEqual(status.title, "Custom — Unsaved")
        XCTAssertTrue(status.isSavable)
    }

    func testASavedPresetIsNamedAsItself() throws {
        let existing = library(["Green Gradient"])
        let preset = try XCTUnwrap(existing.presets.first)
        let appearance = DockAppearance.applying(preset, to: .defaults)

        XCTAssertEqual(existing.status(for: appearance), .saved(preset))
        XCTAssertEqual(existing.status(for: appearance).title, "Green Gradient")
        XCTAssertFalse(existing.status(for: appearance).isSavable)
    }

    func testEditingASavedPresetIsMarkedModified() throws {
        let existing = library(["Green Gradient"])
        let preset = try XCTUnwrap(existing.presets.first)
        var appearance = DockAppearance.applying(preset, to: .defaults)
        appearance.colours.number = "#101010"

        let status = existing.status(for: appearance)

        XCTAssertEqual(status, .modified(preset))
        XCTAssertEqual(status.title, "Green Gradient — Modified")
        XCTAssertEqual(status.editedPreset, preset)
    }

    func testAPresetDeletedElsewhereDoesNotLeaveAGhostName() throws {
        let existing = library(["Green Gradient"])
        let preset = try XCTUnwrap(existing.presets.first)
        let appearance = DockAppearance.applying(preset, to: .defaults)

        var emptied = existing
        emptied.remove(preset.id)

        XCTAssertEqual(
            emptied.status(for: appearance),
            .unsaved,
            "A name that no longer exists must not still be displayed"
        )
    }

    // MARK: - Persistence

    func testPresetsSurviveAReload() throws {
        let suite = "DockThemePresetTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.removePersistentDomain(forName: suite)

        let store = SettingsStore(defaults: defaults)
        store.dockThemePresets = library(["Green Gradient", "Forest"])

        let reloaded = SettingsStore(defaults: defaults)

        XCTAssertEqual(
            reloaded.dockThemePresets.presets.map(\.name),
            ["Green Gradient", "Forest"]
        )
        XCTAssertEqual(
            reloaded.dockThemePresets.presets.first?.colours,
            colours()
        )
    }

    func testACorruptPresetIsDroppedAndTheRestSurvive() throws {
        let good = UserDockThemePreset(name: "Green Gradient", colours: colours())
        let bad = UserDockThemePreset(
            name: "Broken",
            colours: colours(top: "not a colour")
        )
        let unnamed = UserDockThemePreset(name: "   ", colours: colours())
        let encoded = try JSONEncoder().encode(
            DockThemePresetLibrary(presets: [good, bad, unnamed])
        )

        let decoded = try JSONDecoder().decode(
            DockThemePresetLibrary.self,
            from: encoded
        )

        XCTAssertEqual(decoded.presets.map(\.name), ["Green Gradient"])
    }

    func testALibraryFromALaterVersionIsNotMisread() throws {
        let json = """
        {"version": 99, "presets": [{"id": "\(UUID().uuidString)", \
        "name": "From The Future", "colours": {"backgroundTop": "#000000", \
        "backgroundBottom": "#000000", "number": "#FFFFFF", "label": "#FFFFFF"}}]}
        """

        let decoded = try JSONDecoder().decode(
            DockThemePresetLibrary.self,
            from: try XCTUnwrap(json.data(using: .utf8))
        )

        XCTAssertTrue(decoded.presets.isEmpty)
    }

    func testGarbageInDefaultsFallsBackToAnEmptyLibrary() throws {
        let suite = "DockThemePresetTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.removePersistentDomain(forName: suite)
        defaults.set(
            Data("not json".utf8),
            forKey: "CanvasCountdown.settings.dockThemePresets"
        )

        let store = SettingsStore(defaults: defaults)

        XCTAssertTrue(store.dockThemePresets.presets.isEmpty)
    }

    // MARK: - Through the view model

    func testSavingThePresetInForceSelectsIt() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.settingsForm.dockAppearance.colours = colours()
        context.viewModel.settingsForm.dockAppearance.preset = .custom

        XCTAssertNil(context.viewModel.saveDockThemePreset(named: "Green Gradient"))

        XCTAssertEqual(context.viewModel.dockThemePresets.map(\.name), ["Green Gradient"])
        XCTAssertEqual(context.viewModel.dockThemeStatus.title, "Green Gradient")
    }

    func testSavingRefusesAnInvalidNameWithoutChangingAnything() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertEqual(context.viewModel.saveDockThemePreset(named: " "), .empty)
        XCTAssertEqual(
            context.viewModel.saveDockThemePreset(named: "Dark"),
            .duplicate
        )
        XCTAssertTrue(context.viewModel.dockThemePresets.isEmpty)
    }

    func testUpdatingWritesTheEditsBackToThePreset() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.settingsForm.dockAppearance.colours = colours()
        context.viewModel.settingsForm.dockAppearance.preset = .custom
        context.viewModel.saveDockThemePreset(named: "Green Gradient")

        context.viewModel.settingsForm.dockAppearance.colours.number = "#D7F7E4"
        XCTAssertEqual(
            context.viewModel.dockThemeStatus.title,
            "Green Gradient — Modified"
        )

        context.viewModel.updateSelectedDockThemePreset()

        XCTAssertEqual(context.viewModel.dockThemeStatus.title, "Green Gradient")
        XCTAssertEqual(
            context.viewModel.dockThemePresets.first?.colours.number,
            "#D7F7E4"
        )
    }

    func testRevertingRestoresTheSavedColours() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.settingsForm.dockAppearance.colours = colours()
        context.viewModel.settingsForm.dockAppearance.preset = .custom
        context.viewModel.saveDockThemePreset(named: "Green Gradient")

        context.viewModel.settingsForm.dockAppearance.colours.number = "#D7F7E4"
        context.viewModel.revertToSelectedDockThemePreset()

        XCTAssertEqual(
            context.viewModel.settingsForm.dockAppearance.colours,
            colours()
        )
        XCTAssertEqual(context.viewModel.dockThemeStatus.title, "Green Gradient")
    }

    func testResetToDefaultsKeepsSavedPresets() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.settingsForm.dockAppearance.colours = colours()
        context.viewModel.settingsForm.dockAppearance.preset = .custom
        context.viewModel.saveDockThemePreset(named: "Green Gradient")

        context.viewModel.resetDockAppearance()

        XCTAssertEqual(
            context.viewModel.dockThemePresets.map(\.name),
            ["Green Gradient"],
            "Resetting the appearance must not throw away the user's own themes"
        )
        XCTAssertEqual(context.viewModel.dockThemeStatus.title, "Default Blue")
    }

    func testDeletingThePresetInUseKeepsTheColoursOnScreen() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.settingsForm.dockAppearance.colours = colours()
        context.viewModel.settingsForm.dockAppearance.preset = .custom
        context.viewModel.saveDockThemePreset(named: "Green Gradient")
        let id = try XCTUnwrap(context.viewModel.dockThemePresets.first?.id)

        context.viewModel.deleteDockThemePreset(id)

        XCTAssertTrue(context.viewModel.dockThemePresets.isEmpty)
        XCTAssertEqual(
            context.viewModel.settingsForm.dockAppearance.colours,
            colours()
        )
        XCTAssertEqual(context.viewModel.dockThemeStatus.title, "Custom — Unsaved")
    }

    func testAPresetIsSavedInAReadableState() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        // White on white: unreadable, and corrected before it is stored.
        context.viewModel.settingsForm.dockAppearance.colours = colours(
            top: "#FFFFFF",
            bottom: "#FFFFFF",
            number: "#FEFEFE",
            label: "#FDFDFD"
        )
        context.viewModel.settingsForm.dockAppearance.preset = .custom

        context.viewModel.saveDockThemePreset(named: "Whiteout")

        let saved = try XCTUnwrap(context.viewModel.dockThemePresets.first)
        var check = DockAppearance.defaults
        check.colours = saved.colours
        XCTAssertTrue(
            check.hasSufficientContrast,
            "A preset that cannot be read would be saved unusable"
        )
    }

    func testChoosingABuiltInThemeLetsGoOfTheSavedPreset() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.settingsForm.dockAppearance.colours = colours()
        context.viewModel.settingsForm.dockAppearance.preset = .custom
        context.viewModel.saveDockThemePreset(named: "Green Gradient")

        context.viewModel.applyDockTheme(DockThemePreset.dark)

        XCTAssertNil(context.viewModel.settingsForm.dockAppearance.userPresetID)
        XCTAssertEqual(context.viewModel.dockThemeStatus.title, "Dark")
    }

    func testTheThemeChoiceMatchesWhateverTheRowIsShowing() throws {
        let existing = library(["Green Gradient"])
        let preset = try XCTUnwrap(existing.presets.first)

        XCTAssertEqual(DockThemeChoice(.builtIn(.dark)), .builtIn(.dark))
        XCTAssertEqual(DockThemeChoice(.saved(preset)), .saved(preset.id))
        XCTAssertEqual(
            DockThemeChoice(.modified(preset)),
            .saved(preset.id),
            "Edits still belong to the preset they came from"
        )
        XCTAssertEqual(DockThemeChoice(.unsaved), .unsaved)
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
    }

    private func makeContext() throws -> Context {
        let suite = "DockThemePresetTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.removePersistentDomain(forName: suite)

        let viewModel = MainViewModel(
            repository: ScopeRepositoryStub(snapshots: []),
            refreshCoordinator: PresetCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: AppearanceRecordingDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: Calendar(identifier: .gregorian),
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel, defaults: defaults)
    }
}

private actor PresetCoordinatorStub: FeedRefreshCoordinating {
    func preview(feedURL: URL, now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func preview(now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date
    ) throws -> RefreshResult {
        throw RefreshCoordinatorError.noEventsSelected
    }

    func refresh(now: Date, trigger: RefreshTrigger) throws -> RefreshResult {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func diagnosticSnapshot() -> FeedRefreshDiagnostic? { nil }
}

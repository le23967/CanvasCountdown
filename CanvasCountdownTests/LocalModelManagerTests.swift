import Foundation
import XCTest
@testable import CanvasCountdown

/// Local model management. No test reaches a real Ollama instance; a stub
/// protocol answers instead.
final class LocalModelManagerTests: XCTestCase {
    func testAPIRootIsDerivedFromTheChatAddress() {
        // The chat endpoint ends in /v1; the management API sits at the root.
        XCTAssertEqual(
            OllamaModelManager.apiRoot(
                from: URL(string: "http://localhost:11434/v1")!
            ).absoluteString,
            "http://localhost:11434/"
        )
        XCTAssertEqual(
            OllamaModelManager.apiRoot(
                from: URL(string: "http://localhost:11434")!
            ).absoluteString,
            "http://localhost:11434"
        )
    }

    func testSuggestedModelsAreNamedWithApproximateSizes() {
        XCTAssertFalse(SuggestedModel.all.isEmpty)
        for model in SuggestedModel.all {
            XCTAssertFalse(model.name.isEmpty)
            XCTAssertTrue(
                model.approximateSize.lowercased().contains("about"),
                "Sizes are approximate and must be described that way"
            )
            XCTAssertFalse(model.note.isEmpty)
        }
    }

    func testSuggestionsAreOrderedSmallestFirst() {
        // A student on a laptop cares about the download before the quality.
        XCTAssertEqual(SuggestedModel.all.first?.name, "llama3.2:3b")
    }

    func testSetupCommandsAreTheRealOnes() {
        XCTAssertEqual(OllamaSetup.serveCommand, "ollama serve")
        XCTAssertEqual(
            OllamaSetup.pullCommand("llama3.1:8b"),
            "ollama pull llama3.1:8b"
        )
        XCTAssertTrue(OllamaSetup.downloadPage.hasPrefix("https://"))
    }

    func testModelSizeIsShownInHumanUnits() {
        let model = LocalModel(name: "llama3.1:8b", sizeBytes: 4_700_000_000)

        XCTAssertTrue(
            model.sizeDescription.contains("GB"),
            "A multi-gigabyte download should not be printed in bytes"
        )
    }

    func testSuggestedModelListIsOfferedButNotEnforced() {
        XCTAssertFalse(AssistantService.groq.models.isEmpty)

        // A name outside the list is still accepted, so a new release does not
        // have to wait for an app update.
        var settings = AssistantSettings.applying(.cloud, to: .defaults)
        settings.model = "some-new-model-that-does-not-exist-yet"
        XCTAssertEqual(settings.model, "some-new-model-that-does-not-exist-yet")

        // The same for an address no suggestion mentions.
        settings.baseURL = "https://ai.example.edu/v1"
        XCTAssertNil(AssistantService.matching(baseURL: settings.baseURL))
        XCTAssertFalse(settings.staysOnThisMac)
    }

    // MARK: - View model behaviour against a stub

    @MainActor
    func testUnreachableServerReportsNoModels() async throws {
        let context = try makeContext(manager: StubModelManager(reachable: false))

        await context.viewModel.refreshLocalModels()

        XCTAssertFalse(context.viewModel.isOllamaReachable)
        XCTAssertTrue(context.viewModel.installedModels.isEmpty)
    }

    @MainActor
    func testInstalledModelsAreListed() async throws {
        let context = try makeContext(
            manager: StubModelManager(
                reachable: true,
                models: [
                    LocalModel(name: "llama3.1:8b", sizeBytes: 4_700_000_000),
                    LocalModel(name: "llama3.2:3b", sizeBytes: 2_000_000_000),
                ]
            )
        )

        await context.viewModel.refreshLocalModels()

        XCTAssertTrue(context.viewModel.isOllamaReachable)
        XCTAssertEqual(
            context.viewModel.installedModels.map(\.name),
            ["llama3.1:8b", "llama3.2:3b"]
        )
    }

    @MainActor
    func testChoosingAModelPersistsIt() async throws {
        let context = try makeContext(
            manager: StubModelManager(reachable: true, models: [])
        )
        await context.viewModel.start()

        context.viewModel.useModel("qwen2.5:7b")

        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).assistant.model,
            "qwen2.5:7b"
        )
    }

    @MainActor
    func testDeletingAModelRefreshesTheList() async throws {
        let manager = StubModelManager(
            reachable: true,
            models: [LocalModel(name: "llama3.2:3b", sizeBytes: 2_000_000_000)]
        )
        let context = try makeContext(manager: manager)
        await context.viewModel.refreshLocalModels()
        XCTAssertEqual(context.viewModel.installedModels.count, 1)

        context.viewModel.deleteModel("llama3.2:3b")
        try await Task.sleep(for: .milliseconds(300))

        XCTAssertTrue(context.viewModel.installedModels.isEmpty)
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
    }

    @MainActor
    private func makeContext(manager: any LocalModelManaging) throws -> Context {
        let suiteName = "LocalModelManagerTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let viewModel = MainViewModel(
            repository: ScopeRepositoryStub(snapshots: []),
            refreshCoordinator: ModelCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: AppearanceRecordingDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: .autoupdatingCurrent,
            automaticActivityEnabled: false,
            localModels: manager
        )
        return Context(viewModel: viewModel, defaults: defaults)
    }
}

private actor StubModelManager: LocalModelManaging {
    private let reachable: Bool
    private var models: [LocalModel]

    init(reachable: Bool, models: [LocalModel] = []) {
        self.reachable = reachable
        self.models = models
    }

    func isReachable(baseURL: URL) -> Bool { reachable }

    func installedModels(baseURL: URL) throws -> [LocalModel] {
        guard reachable else {
            throw LocalModelError.serverUnreachable
        }
        return models
    }

    func pull(
        _ name: String,
        baseURL: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) throws {
        progress(1)
        models.append(LocalModel(name: name, sizeBytes: 1_000_000_000))
    }

    func delete(_ name: String, baseURL: URL) throws {
        models.removeAll { $0.name == name }
    }
}

private actor ModelCoordinatorStub: FeedRefreshCoordinating {
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

import Foundation
import SwiftData

/// Single composition root for the application.
///
/// Keeping the wiring here means the production and isolated-testing graphs are
/// described side by side and can be asserted on directly, instead of being
/// buried in `CanvasCountdownApp.init`.
@MainActor
struct AppDependencies {
    let environment: AppEnvironment
    let modelContainer: ModelContainer
    let repository: any AssignmentRepository
    let feedURLStore: any FeedURLStoring
    let feedFetcher: any FeedFetching
    let exclusionStore: any FeedExclusionStoring
    let notificationScheduler: any NotificationScheduling
    let dockRenderer: any DockRendering
    let settingsStore: SettingsStore
    let viewModel: MainViewModel

    static func make(
        environment: AppEnvironment = .current
    ) throws -> AppDependencies {
        let modelContainer = try makeModelContainer(for: environment)
        let repository = SwiftDataAssignmentRepository(
            modelContainer: modelContainer
        )

        let feedURLStore: any FeedURLStoring
        let feedFetcher: any FeedFetching
        let exclusionStore: any FeedExclusionStoring
        let notificationScheduler: any NotificationScheduling
        let dockRenderer: any DockRendering

        switch environment {
        case .production:
            feedURLStore = KeychainFeedURLStore()
            feedFetcher = URLSessionCanvasFeedFetcher()
            exclusionStore = UserDefaultsFeedExclusionStore()
            notificationScheduler = NotificationService()
            dockRenderer = DockTileService()
        case .automatedTesting:
            feedURLStore = IsolatedFeedURLStore()
            feedFetcher = OfflineFeedFetcher()
            exclusionStore = IsolatedFeedExclusionStore()
            notificationScheduler = InertNotificationScheduler()
            dockRenderer = InertDockRenderer()
        }

        let refreshCoordinator = RefreshCoordinator(
            fetcher: feedFetcher,
            parser: ICSParser(),
            repository: repository,
            feedURLStore: feedURLStore,
            exclusionStore: exclusionStore
        )
        let settingsStore = SettingsStore(defaults: makeDefaults(for: environment))
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: refreshCoordinator,
            feedURLStore: feedURLStore,
            settingsStore: settingsStore,
            dockRenderer: dockRenderer,
            notificationScheduler: notificationScheduler,
            automaticActivityEnabled: !environment.isAutomatedTesting
        )

        return AppDependencies(
            environment: environment,
            modelContainer: modelContainer,
            repository: repository,
            feedURLStore: feedURLStore,
            feedFetcher: feedFetcher,
            exclusionStore: exclusionStore,
            notificationScheduler: notificationScheduler,
            dockRenderer: dockRenderer,
            settingsStore: settingsStore,
            viewModel: viewModel
        )
    }

    private static func makeModelContainer(
        for environment: AppEnvironment
    ) throws -> ModelContainer {
        switch environment {
        case .production:
            try ModelContainer(for: AssignmentEvent.self)
        case .automatedTesting:
            // Never opens the production store file on disk.
            try ModelContainer(
                for: AssignmentEvent.self,
                configurations: ModelConfiguration(isStoredInMemoryOnly: true)
            )
        }
    }

    private static func makeDefaults(
        for environment: AppEnvironment
    ) -> UserDefaults {
        guard let suiteName = environment.preferencesSuiteName else {
            return .standard
        }
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            return .standard
        }
        defaults.removePersistentDomain(forName: suiteName)
        return defaults
    }
}

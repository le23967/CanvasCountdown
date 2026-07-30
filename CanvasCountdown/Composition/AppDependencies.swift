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
    let courseBlocklist: any CourseBlocklisting
    let notificationScheduler: any NotificationScheduling
    let dockRenderer: any DockRendering
    let settingsStore: SettingsStore
    let viewModel: MainViewModel
    let screenshotImportCoordinator: any ScreenshotImportCoordinating

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
        let courseBlocklist: any CourseBlocklisting
        let notificationScheduler: any NotificationScheduling
        let dockRenderer: any DockRendering

        switch environment {
        case .production:
            feedURLStore = KeychainFeedURLStore()
            feedFetcher = URLSessionCanvasFeedFetcher()
            exclusionStore = UserDefaultsFeedExclusionStore()
            courseBlocklist = UserDefaultsCourseBlocklistStore()
            notificationScheduler = NotificationService()
            dockRenderer = DockTileService()
        case .automatedTesting:
            feedURLStore = IsolatedFeedURLStore()
            feedFetcher = OfflineFeedFetcher()
            exclusionStore = IsolatedFeedExclusionStore()
            courseBlocklist = IsolatedCourseBlocklistStore()
            notificationScheduler = InertNotificationScheduler()
            dockRenderer = InertDockRenderer()
        }

        // Recognition is local either way; an automated run uses a stub so no
        // test depends on Vision output, which varies between OS revisions.
        let ocrService: any ScreenshotOCRServicing
        switch environment {
        case .production:
            ocrService = VisionScreenshotOCRService()
        case .automatedTesting:
            ocrService = InertScreenshotOCRService()
        }
        let screenshotImportCoordinator = ScreenshotImportCoordinator(
            ocr: ocrService,
            repository: repository
        )

        let refreshCoordinator = RefreshCoordinator(
            fetcher: feedFetcher,
            parser: ICSParser(),
            repository: repository,
            feedURLStore: feedURLStore,
            exclusionStore: exclusionStore,
            courseBlocklist: courseBlocklist
        )
        let settingsStore = SettingsStore(defaults: makeDefaults(for: environment))
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: refreshCoordinator,
            feedURLStore: feedURLStore,
            settingsStore: settingsStore,
            dockRenderer: dockRenderer,
            notificationScheduler: notificationScheduler,
            automaticActivityEnabled: !environment.isAutomatedTesting,
            courseBlocklist: courseBlocklist
        )

        return AppDependencies(
            environment: environment,
            modelContainer: modelContainer,
            repository: repository,
            feedURLStore: feedURLStore,
            feedFetcher: feedFetcher,
            exclusionStore: exclusionStore,
            courseBlocklist: courseBlocklist,
            notificationScheduler: notificationScheduler,
            dockRenderer: dockRenderer,
            settingsStore: settingsStore,
            viewModel: viewModel,
            screenshotImportCoordinator: screenshotImportCoordinator
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

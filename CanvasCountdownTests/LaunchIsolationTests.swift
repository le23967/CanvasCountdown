import Foundation
import SwiftData
import XCTest
@testable import CanvasCountdown

/// Regression cover for the rule that an automated run must never reach the
/// user's production state: no Keychain feed URL, no Canvas request, no
/// production SwiftData store, no real notification, no real Dock tile, no
/// login-item change.
@MainActor
final class LaunchIsolationTests: XCTestCase {
    func testSuiteRunsInAutomatedTestingEnvironment() {
        XCTAssertEqual(AppEnvironment.current, .automatedTesting)
        XCTAssertTrue(AppEnvironment.current.isAutomatedTesting)
    }

    func testEnvironmentDetectionMatchesXCTestMarkers() {
        XCTAssertEqual(
            AppEnvironment.detected(in: ["XCTestConfigurationFilePath": "/tmp/x.plist"]),
            .automatedTesting
        )
        XCTAssertEqual(
            AppEnvironment.detected(in: ["XCTestBundlePath": "/tmp/Tests.xctest"]),
            .automatedTesting
        )
        XCTAssertEqual(
            AppEnvironment.detected(in: ["XCTestSessionIdentifier": "ABC"]),
            .automatedTesting
        )
        XCTAssertEqual(
            AppEnvironment.detected(in: ["CANVAS_COUNTDOWN_ISOLATED_LAUNCH": "1"]),
            .automatedTesting
        )
        XCTAssertEqual(AppEnvironment.detected(in: [:]), .production)
        XCTAssertEqual(
            AppEnvironment.detected(in: ["XCTestConfigurationFilePath": ""]),
            .production
        )
    }

    func testIsolatedLaunchNeverBuildsProductionDependencies() throws {
        let dependencies = try AppDependencies.make(environment: .automatedTesting)

        XCTAssertFalse(dependencies.feedURLStore is KeychainFeedURLStore)
        XCTAssertTrue(dependencies.feedURLStore is IsolatedFeedURLStore)

        XCTAssertFalse(dependencies.feedFetcher is URLSessionCanvasFeedFetcher)
        XCTAssertTrue(dependencies.feedFetcher is OfflineFeedFetcher)

        XCTAssertFalse(dependencies.exclusionStore is UserDefaultsFeedExclusionStore)
        XCTAssertTrue(dependencies.exclusionStore is IsolatedFeedExclusionStore)

        XCTAssertFalse(dependencies.notificationScheduler is NotificationService)
        XCTAssertTrue(dependencies.notificationScheduler is InertNotificationScheduler)

        XCTAssertFalse(dependencies.dockRenderer is DockTileService)
        XCTAssertTrue(dependencies.dockRenderer is InertDockRenderer)
    }

    func testIsolatedLaunchUsesInMemoryStoreInsteadOfProductionFile() throws {
        let dependencies = try AppDependencies.make(environment: .automatedTesting)
        let configurations = dependencies.modelContainer.configurations

        XCTAssertFalse(configurations.isEmpty)
        for configuration in configurations {
            XCTAssertTrue(
                configuration.isStoredInMemoryOnly,
                "An isolated launch must not open the production store file"
            )
        }
    }

    func testIsolatedFeedURLStoreNeverExposesAStoredFeed() async throws {
        let dependencies = try AppDependencies.make(environment: .automatedTesting)

        let loaded = try await dependencies.feedURLStore.loadFeedURL()
        XCTAssertNil(
            loaded,
            "An isolated launch must not read a feed URL from Keychain"
        )
    }

    func testIsolatedFeedFetcherRefusesToContactCanvas() async throws {
        let dependencies = try AppDependencies.make(environment: .automatedTesting)
        let url = try XCTUnwrap(URL(string: "https://canvas.example.edu/feed.ics"))

        do {
            _ = try await dependencies.feedFetcher.fetch(from: url)
            XCTFail("An isolated launch must not perform a network request")
        } catch let error as CanvasFeedFetchError {
            XCTAssertEqual(error, .networkFailure(code: nil))
        }
    }

    func testIsolatedStartDoesNotRefreshOrScheduleAnything() async throws {
        let dependencies = try AppDependencies.make(environment: .automatedTesting)
        let viewModel = dependencies.viewModel

        await viewModel.start()

        XCTAssertFalse(viewModel.hasConfiguredFeed)
        XCTAssertNil(viewModel.lastRefreshDate)
        XCTAssertFalse(viewModel.isRefreshing)
        XCTAssertFalse(
            viewModel.isShowingFeedImport,
            "An isolated launch must not open onboarding"
        )
        XCTAssertTrue(viewModel.assignments.isEmpty)

        let dock = try XCTUnwrap(dependencies.dockRenderer as? InertDockRenderer)
        XCTAssertNil(dock.lastRenderedDaysRemaining)
    }

    func testIsolatedExclusionStoreDoesNotUseSharedPreferences() async throws {
        let dependencies = try AppDependencies.make(environment: .automatedTesting)
        let key = "CanvasCountdown.feed.excludedUIDs"
        let before = UserDefaults.standard.stringArray(forKey: key)

        await dependencies.exclusionStore.saveExcludedUIDs(["isolated-uid"])
        let stored = await dependencies.exclusionStore.loadExcludedUIDs()

        XCTAssertEqual(stored, ["isolated-uid"])
        XCTAssertEqual(
            UserDefaults.standard.stringArray(forKey: key),
            before,
            "An isolated launch must not rewrite the real exclusion list"
        )
    }

    func testLaunchAtLoginIsInertDuringAutomatedRuns() throws {
        XCTAssertFalse(LaunchAtLoginController.isEnabled)
        XCTAssertNoThrow(try LaunchAtLoginController.setEnabled(true))
        XCTAssertFalse(
            LaunchAtLoginController.isEnabled,
            "An automated run must not register the real login item"
        )
    }

    func testProductionCompositionIsStillDescribedByTheSameRoot() {
        // The production graph is asserted structurally rather than built, so the
        // suite never instantiates Keychain, URLSession, or Dock dependencies.
        XCTAssertEqual(AppEnvironment.production.preferencesSuiteName, nil)
        XCTAssertEqual(
            AppEnvironment.automatedTesting.preferencesSuiteName,
            "com.local.CanvasCountdown.isolated-testing"
        )
        XCTAssertFalse(AppEnvironment.production.isAutomatedTesting)
    }
}

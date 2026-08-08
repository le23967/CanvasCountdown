import Foundation
import XCTest
@testable import CanvasCountdown

/// Knowing a new version exists, without becoming something people learn to
/// ignore.
///
/// No test makes a real network request, and none of them ask GitHub what is
/// published today: the suite must not pass or fail on somebody else's release.
/// All fixtures are invented.
@MainActor
final class UpdateCheckTests: XCTestCase {
    // MARK: - Version numbers are compared as numbers

    /// The failure this exists to prevent: as text, "1.10.0" sorts before
    /// "1.9.0", so everyone on 1.9.0 would be told they were current for the
    /// rest of the app's life, and no later release could ever correct it.
    func testTenIsNewerThanNine() throws {
        let ten = try XCTUnwrap(AppVersion("1.10.0"))
        let nine = try XCTUnwrap(AppVersion("1.9.0"))

        XCTAssertTrue(ten > nine)
        XCTAssertFalse(nine > ten)
    }

    func testTheTagMayCarryItsV() throws {
        XCTAssertEqual(AppVersion("v1.2.0"), AppVersion("1.2.0"))
        XCTAssertEqual(AppVersion("1.2"), AppVersion("1.2.0"))
        XCTAssertTrue(try XCTUnwrap(AppVersion("2.0")) > XCTUnwrap(AppVersion("1.99.99")))
    }

    /// A version it cannot read is refused rather than guessed at, because a
    /// guess here means telling somebody to install something.
    func testSomethingThatIsNotAVersionIsRefused() {
        XCTAssertNil(AppVersion(""))
        XCTAssertNil(AppVersion("v"))
        XCTAssertNil(AppVersion("nightly"))
        XCTAssertNil(AppVersion("1.2.x"))
        XCTAssertNil(AppVersion("1..2"))
    }

    // MARK: - Reading what GitHub sends back

    func testTheDiskImageIsPickedOutOfTheAssets() throws {
        let release = try XCTUnwrap(
            GitHubReleaseChecker.parse(Self.payload(tag: "v1.3.0"))
        )

        XCTAssertEqual(release.version, AppVersion("1.3.0"))
        XCTAssertEqual(release.tag, "v1.3.0")
        XCTAssertEqual(
            release.downloadURL?.lastPathComponent,
            "CanvasCountdown-1.3.0.dmg",
            "The zipped source is not what anybody wants to install"
        )
    }

    /// Nothing half-finished is offered to anyone.
    func testDraftsAndPrereleasesAreNotOffered() throws {
        XCTAssertNil(
            try GitHubReleaseChecker.parse(Self.payload(tag: "v9.9.9", draft: true))
        )
        XCTAssertNil(
            try GitHubReleaseChecker.parse(
                Self.payload(tag: "v9.9.9", prerelease: true)
            )
        )
    }

    func testAReleaseWithNoImageStillOffersItsPage() throws {
        let release = try XCTUnwrap(
            GitHubReleaseChecker.parse(Self.payload(tag: "v1.3.0", assets: false))
        )

        XCTAssertNil(release.downloadURL)
        XCTAssertEqual(
            release.pageURL.absoluteString,
            "https://github.com/le23967/CanvasCountdown/releases/tag/v1.3.0"
        )
    }

    func testNonsenseIsAnErrorRatherThanAnUpdate() {
        XCTAssertThrowsError(
            try GitHubReleaseChecker.parse(Data("not json".utf8))
        )
    }

    // MARK: - When something is said, and when nothing is

    func testANewerVersionIsOffered() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")

        await harness.viewModel.checkForUpdate()

        XCTAssertEqual(
            harness.viewModel.availableUpdate?.version,
            AppVersion("1.3.0")
        )
    }

    func testTheVersionYouAreOnIsNotAnUpdate() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.2.0")

        await harness.viewModel.checkForUpdate()

        XCTAssertNil(harness.viewModel.availableUpdate)
    }

    /// Somebody who built from source, or rolled back on purpose, is not
    /// nagged to install something older than what they are running.
    func testAnOlderPublishedVersionIsNotOffered() async throws {
        let harness = try makeHarness(running: "1.4.0", published: "1.3.0")

        await harness.viewModel.checkForUpdate()

        XCTAssertNil(harness.viewModel.availableUpdate)
    }

    /// Being offline is not news, and it is not the user's problem to solve.
    func testALaunchCheckThatFailsSaysNothing() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        harness.checker.failure = ReleaseCheckError.rateLimited

        await harness.viewModel.checkForUpdate()

        XCTAssertNil(harness.viewModel.availableUpdate)
        XCTAssertNil(
            harness.viewModel.updateCheckAnnouncement,
            "A check nobody asked for does not report its own failure"
        )
    }

    /// A button that says nothing back is a button people press twice.
    func testAManualCheckAlwaysAnswers() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.2.0")

        await harness.viewModel.checkForUpdate(force: true)

        XCTAssertEqual(
            harness.viewModel.updateCheckAnnouncement,
            "1.2.0 is the newest version"
        )

        harness.checker.failure = ReleaseCheckError.rateLimited
        await harness.viewModel.checkForUpdate(force: true)

        XCTAssertNotNil(harness.viewModel.updateCheckAnnouncement)
    }

    // MARK: - Asked once

    func testADismissedVersionIsNotOfferedAgain() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        let today = Date(timeIntervalSince1970: 1_800_000_000)
        await harness.viewModel.checkForUpdate(now: today)
        XCTAssertNotNil(harness.viewModel.availableUpdate)

        harness.viewModel.dismissUpdate()
        XCTAssertNil(harness.viewModel.availableUpdate)

        await harness.viewModel.checkForUpdate(now: Self.aWeekAfter(today))

        XCTAssertNil(
            harness.viewModel.availableUpdate,
            "Asking again about the version just waved away is how a notice becomes noise"
        )
    }

    /// Waving one version away is not a decision about every version after it.
    func testTheNextVersionIsStillOffered() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        let today = Date(timeIntervalSince1970: 1_800_000_000)
        await harness.viewModel.checkForUpdate(now: today)
        harness.viewModel.dismissUpdate()

        harness.checker.release = Self.release("1.4.0")
        await harness.viewModel.checkForUpdate(now: Self.aWeekAfter(today))

        XCTAssertEqual(
            harness.viewModel.availableUpdate?.version,
            AppVersion("1.4.0")
        )
    }

    private static func aWeekAfter(_ date: Date) -> Date {
        date.addingTimeInterval(86_400 * 7)
    }

    /// Pressing the button is a change of mind, and it is answered.
    func testAManualCheckReconsidersADismissedVersion() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        await harness.viewModel.checkForUpdate()
        harness.viewModel.dismissUpdate()

        await harness.viewModel.checkForUpdate(force: true)

        XCTAssertEqual(
            harness.viewModel.availableUpdate?.version,
            AppVersion("1.3.0")
        )
    }

    // MARK: - Once a day, not once a launch

    func testAsecondCheckWithinTheDayDoesNotAskAgain() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")

        await harness.viewModel.checkForUpdate()
        XCTAssertEqual(harness.checker.callCount, 1)

        await harness.viewModel.checkForUpdate()

        XCTAssertEqual(
            harness.checker.callCount,
            1,
            "Somebody who opens the app six times a day is not six checks"
        )
    }

    func testADayLaterItAsksAgain() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        await harness.viewModel.checkForUpdate(now: now)
        XCTAssertEqual(harness.checker.callCount, 1)

        await harness.viewModel.checkForUpdate(
            now: now.addingTimeInterval(MainViewModel.updateCheckInterval + 1)
        )

        XCTAssertEqual(harness.checker.callCount, 2)
    }

    /// A clock moved backwards must not lock the check out until it catches
    /// up. Somebody whose Mac had the wrong date would otherwise stop being
    /// told about updates for as long as the stamp stayed ahead.
    func testACheckStampedInTheFutureDoesNotBlockForever() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        let future = Date(timeIntervalSince1970: 1_800_000_000)
        await harness.viewModel.checkForUpdate(now: future)
        XCTAssertEqual(harness.checker.callCount, 1)

        // The clock is put back a month; the stamp is now in the future.
        let corrected = future.addingTimeInterval(-86_400 * 30)

        XCTAssertTrue(harness.viewModel.shouldCheckForUpdate(now: corrected))
        await harness.viewModel.checkForUpdate(now: corrected)
        XCTAssertEqual(harness.checker.callCount, 2)
    }

    /// The button means now, whatever the clock says about the last one.
    func testTheButtonIgnoresTheDailyGate() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        await harness.viewModel.checkForUpdate()

        await harness.viewModel.checkForUpdate(force: true)

        XCTAssertEqual(harness.checker.callCount, 2)
    }

    // MARK: - Downloading is as far as it goes

    func testDownloadingLeavesTheImageWhereItCanBeFound() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        await harness.viewModel.checkForUpdate()

        await harness.viewModel.downloadUpdate()

        XCTAssertEqual(
            harness.downloader.revealed?.lastPathComponent,
            "CanvasCountdown-1.3.0.dmg",
            "The image is opened, so the volume with the app in it appears"
        )
        XCTAssertEqual(
            harness.viewModel.updateMessage,
            "1.3.0 is in your Downloads. Drag it to Applications to finish."
        )
    }

    /// A failed download says so and keeps the offer up, rather than clearing
    /// the banner as though it had worked.
    func testAFailedDownloadKeepsTheOfferAndSaysWhy() async throws {
        let harness = try makeHarness(running: "1.2.0", published: "1.3.0")
        await harness.viewModel.checkForUpdate()
        harness.downloader.failure = UpdateDownloadError.networkFailure(code: -1009)

        await harness.viewModel.downloadUpdate()

        XCTAssertNotNil(harness.viewModel.availableUpdate)
        XCTAssertNil(harness.downloader.revealed)
        XCTAssertEqual(
            harness.viewModel.updateMessage,
            UpdateDownloadError.networkFailure(code: -1009).errorDescription
        )
    }

    /// A release published without an image cannot be installed from here, so
    /// the button takes them where they can see what happened.
    func testAReleaseWithNoImageOpensThePageInstead() async throws {
        let harness = try makeHarness(
            running: "1.2.0",
            published: "1.3.0",
            withDiskImage: false
        )
        await harness.viewModel.checkForUpdate()

        await harness.viewModel.downloadUpdate()

        XCTAssertNil(harness.downloader.revealed)
        XCTAssertEqual(
            harness.opened?.absoluteString,
            "https://github.com/le23967/CanvasCountdown/releases/tag/v1.3.0"
        )
    }

    // MARK: - Fixtures

    private static func release(
        _ version: String,
        withDiskImage: Bool = true
    ) -> AppRelease {
        AppRelease(
            version: AppVersion(version)!,
            tag: "v\(version)",
            name: "Canvas Countdown \(version)",
            notes: "Copyright © 2026 le23967. MIT Licence.\n\n**A made-up change** for this test.",
            downloadURL: withDiskImage
                ? URL(string: "https://example.invalid/CanvasCountdown-\(version).dmg")
                : nil,
            pageURL: URL(
                string: "https://github.com/le23967/CanvasCountdown/releases/tag/v\(version)"
            )!
        )
    }

    private static func payload(
        tag: String,
        draft: Bool = false,
        prerelease: Bool = false,
        assets: Bool = true
    ) -> Data {
        let assetList = assets
            ? """
              {"name":"CanvasCountdown-1.3.0.dmg",
               "browser_download_url":"https://example.invalid/CanvasCountdown-1.3.0.dmg"}
            """
            : ""
        return Data("""
        {
          "tag_name": "\(tag)",
          "name": "Canvas Countdown",
          "body": "Notes go here.",
          "draft": \(draft),
          "prerelease": \(prerelease),
          "html_url": "https://github.com/le23967/CanvasCountdown/releases/tag/\(tag)",
          "assets": [\(assetList)]
        }
        """.utf8)
    }

    private struct Harness {
        let viewModel: MainViewModel
        let checker: ReleaseCheckerStub
        let downloader: UpdateDownloaderSpy
        var opened: URL? { openedBox.value }
        let openedBox: URLBox
    }

    /// Carries the opened link out of the `@MainActor` closure the view model
    /// holds, without the harness having to be a class.
    final class URLBox: @unchecked Sendable {
        var value: URL?
    }

    private func makeHarness(
        running: String,
        published: String,
        withDiskImage: Bool = true
    ) throws -> Harness {
        let suiteName = "UpdateCheckTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let checker = ReleaseCheckerStub()
        checker.release = Self.release(published, withDiskImage: withDiskImage)
        let downloader = UpdateDownloaderSpy()
        let box = URLBox()

        let viewModel = MainViewModel(
            repository: ScopeRepositoryStub(snapshots: []),
            refreshCoordinator: DueTimeCoordinatorStub(),
            feedURLStore: DueTimeFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: DueTimeDockStub(),
            notificationScheduler: DueTimeNotificationSpy(),
            automaticActivityEnabled: false,
            releaseChecker: checker,
            updateDownloader: downloader,
            // Never the main bundle: under xctest that is the test runner,
            // whose version has nothing to do with the app's.
            currentVersion: { AppVersion(running) },
            openURL: { url in box.value = url }
        )
        return Harness(
            viewModel: viewModel,
            checker: checker,
            downloader: downloader,
            openedBox: box
        )
    }
}

final class ReleaseCheckerStub: ReleaseChecking, @unchecked Sendable {
    private let lock = NSLock()
    private var calls = 0

    nonisolated(unsafe) var release: AppRelease?
    nonisolated(unsafe) var failure: (any Error)?

    var callCount: Int {
        lock.withLock { calls }
    }

    func latestRelease() async throws -> AppRelease? {
        lock.withLock { calls += 1 }
        if let failure {
            throw failure
        }
        return release
    }
}

final class UpdateDownloaderSpy: UpdateDownloading, @unchecked Sendable {
    private let lock = NSLock()
    private var revealedFile: URL?

    nonisolated(unsafe) var failure: (any Error)?

    var revealed: URL? {
        lock.withLock { revealedFile }
    }

    func download(_ release: AppRelease) async throws -> URL {
        if let failure {
            throw failure
        }
        guard let source = release.downloadURL else {
            throw UpdateDownloadError.noDiskImage
        }
        // Where a real one would put it, without writing anything.
        return URL(fileURLWithPath: "/tmp/Downloads")
            .appendingPathComponent(source.lastPathComponent)
    }

    @MainActor
    func reveal(_ file: URL) {
        lock.withLock { revealedFile = file }
    }
}

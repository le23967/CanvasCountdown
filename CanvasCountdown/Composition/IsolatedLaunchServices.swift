import AppKit
import Foundation
import UserNotifications

/// Replacements used when `AppEnvironment.current` is `.automatedTesting`.
///
/// Each type deliberately refuses to reach production state: no Keychain item,
/// no network request, no notification centre, no Dock repaint.

/// Holds a feed URL for the lifetime of the process only. Nothing is written to
/// Keychain, so an automated run can never read or overwrite the real feed URL.
actor IsolatedFeedURLStore: FeedURLStoring {
    private var storedURL: URL?

    init(initialURL: URL? = nil) {
        storedURL = initialURL
    }

    func saveFeedURL(_ url: URL) async throws {
        storedURL = try FeedURLValidator.validated(url)
    }

    func loadFeedURL() async throws -> URL? {
        storedURL
    }

    func deleteFeedURL() async throws {
        storedURL = nil
    }
}

/// Fails every download attempt. An isolated launch has no feed URL to begin
/// with, and this guarantees that no code path can reach Canvas even if one
/// were supplied.
struct OfflineFeedFetcher: FeedFetching {
    func fetch(from url: URL) async throws -> Data {
        throw CanvasFeedFetchError.networkFailure(code: nil)
    }
}

/// Accepts scheduling calls and drops them. `UNUserNotificationCenter` is never
/// touched, so no reminder is registered with macOS.
struct InertNotificationScheduler: NotificationScheduling {
    func authorizationStatus() async -> UNAuthorizationStatus {
        .denied
    }

    func requestAuthorization() async throws -> Bool {
        false
    }

    func reschedule(
        candidates: [NotificationCandidate],
        schedule: ReminderSchedule,
        now: Date,
        calendar: Calendar
    ) async throws {}

    func cancelAll() async {}
}

/// Records what would have been drawn without installing a content view on the
/// real `NSApplication.shared.dockTile`.
@MainActor
final class InertDockRenderer: DockRendering {
    private(set) var lastRenderedDaysRemaining: Int?
    private(set) var lastRenderedLabel: String?

    func render(daysRemaining: Int?, label: String) {
        lastRenderedDaysRemaining = daysRemaining
        lastRenderedLabel = label
    }
}

/// Keeps deselected UIDs in memory so an automated run cannot rewrite the
/// user's real exclusion list in `UserDefaults`.
actor IsolatedFeedExclusionStore: FeedExclusionStoring {
    private var excludedUIDs: Set<String> = []

    func loadExcludedUIDs() -> Set<String> {
        excludedUIDs
    }

    func saveExcludedUIDs(_ UIDs: Set<String>) {
        excludedUIDs = UIDs
    }

    func clearExcludedUIDs() {
        excludedUIDs.removeAll()
    }
}

/// Recognition stub for automated runs.
///
/// Vision output changes between operating-system revisions, so no test is
/// allowed to depend on it. Tests supply their own observation fixtures.
struct InertScreenshotOCRService: ScreenshotOCRServicing {
    func recognizeText(in source: ScreenshotSource) async throws -> [OCRTextObservation] {
        []
    }
}

import Foundation

/// Describes how the app composes its dependencies at launch.
///
/// The unit-test bundle is hosted by the real application, so the app process
/// starts for every `xcodebuild test` run. Without this distinction that host
/// process would open the production SwiftData store, read the private feed URL
/// out of Keychain, contact Canvas, schedule real notifications and repaint the
/// real Dock tile. Automated runs therefore compose an isolated, offline set of
/// dependencies instead.
enum AppEnvironment: Equatable, Sendable {
    case production
    case automatedTesting

    static var current: AppEnvironment {
        detected(in: ProcessInfo.processInfo.environment)
    }

    static func detected(in environment: [String: String]) -> AppEnvironment {
        let testingKeys = [
            "XCTestConfigurationFilePath",
            "XCTestBundlePath",
            "XCTestSessionIdentifier",
        ]
        if testingKeys.contains(where: { environment[$0]?.isEmpty == false }) {
            return .automatedTesting
        }
        if environment["CANVAS_COUNTDOWN_ISOLATED_LAUNCH"] == "1" {
            return .automatedTesting
        }
        return .production
    }

    var isAutomatedTesting: Bool {
        self == .automatedTesting
    }

    /// Preference domain for the launch. Automated runs get a throwaway suite so
    /// a test can never rewrite the real user's settings or exclusion list.
    var preferencesSuiteName: String? {
        switch self {
        case .production:
            nil
        case .automatedTesting:
            "com.local.CanvasCountdown.isolated-testing"
        }
    }
}

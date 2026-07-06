import Foundation
import ServiceManagement

@MainActor
enum LaunchAtLoginController {
    static var isEnabled: Bool {
        guard !AppEnvironment.current.isAutomatedTesting else {
            return false
        }
        return SMAppService.mainApp.status == .enabled
    }

    static func setEnabled(_ enabled: Bool) throws {
        // An automated run must never register or unregister the real login item.
        guard !AppEnvironment.current.isAutomatedTesting else {
            return
        }
        if enabled {
            guard SMAppService.mainApp.status != .enabled else {
                return
            }
            try SMAppService.mainApp.register()
        } else {
            guard SMAppService.mainApp.status == .enabled else {
                return
            }
            try SMAppService.mainApp.unregister()
        }
    }
}

import Foundation

/// What the settings screen needs to show and drive model management, without
/// reaching into the view model or the network layer itself.
struct LocalModelPresentation {
    let isReachable: Bool
    let installed: [LocalModel]
    let downloading: String?
    let progress: Double
    let message: String?
    let onDownload: @MainActor (String) -> Void
    let onDelete: @MainActor (String) -> Void
    let onUse: @MainActor (String) -> Void
    let onCancelDownload: @MainActor () -> Void
    let onRefresh: @MainActor () -> Void
}

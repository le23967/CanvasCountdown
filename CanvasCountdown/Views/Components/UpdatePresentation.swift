import Foundation

/// What Settings needs to say about versions, gathered so the view's parameter
/// list does not grow another five entries.
struct UpdatePresentation {
    let runningVersion: String
    let isChecking: Bool
    /// The newest release, when it is newer than the one running.
    let available: AppRelease?
    /// What the last deliberate check came back with. Only a check somebody
    /// asked for sets this: a silent one that found nothing has nothing to say.
    let announcement: String?
    let onCheck: @MainActor () -> Void
}

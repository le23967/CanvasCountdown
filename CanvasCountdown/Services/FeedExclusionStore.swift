import Foundation

/// Persists Canvas UIDs explicitly deselected in the import preview.
///
/// UIDs are not credentials and the private feed URL is never part of this
/// store. New, previously unseen UIDs remain eligible for automatic import.
protocol FeedExclusionStoring: Sendable {
    func loadExcludedUIDs() async -> Set<String>
    func saveExcludedUIDs(_ UIDs: Set<String>) async
    func clearExcludedUIDs() async
}

actor UserDefaultsFeedExclusionStore: FeedExclusionStoring {
    private let defaults: UserDefaults
    private let key: String

    init(
        defaults: UserDefaults = .standard,
        key: String = "CanvasCountdown.feed.excludedUIDs"
    ) {
        self.defaults = defaults
        self.key = key
    }

    func loadExcludedUIDs() -> Set<String> {
        Set(
            (defaults.stringArray(forKey: key) ?? [])
                .map(Self.normalized)
                .filter { !$0.isEmpty }
        )
    }

    func saveExcludedUIDs(_ UIDs: Set<String>) {
        defaults.set(
            UIDs.map(Self.normalized)
                .filter { !$0.isEmpty }
                .sorted(),
            forKey: key
        )
    }

    func clearExcludedUIDs() {
        defaults.removeObject(forKey: key)
    }

    private static func normalized(_ UID: String) -> String {
        UID.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
    }
}

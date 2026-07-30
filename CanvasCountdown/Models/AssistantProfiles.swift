import Foundation

/// A named assistant configuration the user kept.
///
/// The app used to hold exactly two: one local, one hosted, each overwritten
/// whenever the other was chosen. That made "try the fast one for a quick
/// question, use the careful one for drafting" mean retyping an address, a
/// model name and a key every time. A profile is that configuration with a name
/// on it, so switching is one click.
///
/// The API key is deliberately absent. It lives in Keychain under this
/// profile's id, so it is never written to preferences or diagnostics.
struct AssistantProfile: Identifiable, Equatable, Codable, Sendable {
    let id: UUID
    var name: String
    var provider: AssistantProvider
    var baseURL: String
    var model: String

    init(
        id: UUID = UUID(),
        name: String,
        provider: AssistantProvider,
        baseURL: String,
        model: String
    ) {
        self.id = id
        self.name = name
        self.provider = provider
        self.baseURL = baseURL
        self.model = model
    }

    /// A profile pointed at one of the known services, ready to name.
    init(service: AssistantService, name: String? = nil) {
        self.init(
            name: name ?? service.name,
            provider: service.baseURL.hasPrefix("http://localhost")
                ? .local
                : .cloud,
            baseURL: service.baseURL,
            model: service.models.first ?? ""
        )
    }

    /// True when this profile's endpoint is on this machine, whatever it is
    /// named. Reading the address rather than the label is what stops the
    /// interface claiming privacy the configuration does not provide.
    var staysOnThisMac: Bool {
        AssistantEndpoint.isLocal(baseURL)
    }

    /// What the switcher shows beside the name: the model, which is the thing
    /// being switched between.
    var subtitle: String {
        let trimmed = model.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "No model set" : trimmed
    }
}

/// Whether an address is served from this machine.
enum AssistantEndpoint {
    static func isLocal(_ baseURL: String) -> Bool {
        guard let host = URL(
            string: baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        )?.host?.lowercased() else {
            return false
        }
        return host == "localhost" || host == "127.0.0.1" || host == "::1"
    }
}

/// Why a profile name was refused, in the words the row shows.
enum AssistantProfileNameError: Error, Equatable, Sendable {
    case empty
    case tooLong
    case duplicate

    var message: String {
        switch self {
        case .empty:
            "Give the model a name."
        case .tooLong:
            "Names can be up to \(AssistantProfileLibrary.maximumNameLength) characters."
        case .duplicate:
            "There is already a model with that name."
        }
    }
}

/// The saved profiles, and every rule about changing them.
///
/// Versioned for the same reason the Dock presets and the labels are: this
/// outlives any one build, and a library written by a newer version is left
/// alone rather than half-read and rewritten.
struct AssistantProfileLibrary: Equatable, Codable, Sendable {
    static let currentVersion = 1
    static let maximumNameLength = 40
    static let maximumCount = 12
    static let empty = AssistantProfileLibrary(profiles: [])

    var version: Int
    private(set) var profiles: [AssistantProfile]

    init(
        version: Int = AssistantProfileLibrary.currentVersion,
        profiles: [AssistantProfile]
    ) {
        self.version = version
        self.profiles = profiles
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version)
            ?? Self.currentVersion
        guard version <= Self.currentVersion else {
            // Written by a newer build. Showing none is honest; rewriting what
            // we cannot read would destroy it.
            profiles = []
            return
        }
        let decoded = try container.decodeIfPresent(
            [AssistantProfile].self,
            forKey: .profiles
        ) ?? []
        profiles = decoded.filter(Self.isUsable)
    }

    /// A profile with no name or no address cannot be selected, so it is not
    /// kept. The model may be blank: a local server can be asked for one later.
    private static func isUsable(_ profile: AssistantProfile) -> Bool {
        !profile.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && URL(
                string: profile.baseURL
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            )?.host != nil
    }

    var isFull: Bool {
        profiles.count >= Self.maximumCount
    }

    func profile(withID id: UUID?) -> AssistantProfile? {
        guard let id else {
            return nil
        }
        return profiles.first { $0.id == id }
    }

    // MARK: - Naming

    func validate(
        name: String,
        excluding excludedID: UUID? = nil
    ) -> AssistantProfileNameError? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return .empty
        }
        if trimmed.count > Self.maximumNameLength {
            return .tooLong
        }
        let clash = profiles.contains {
            $0.id != excludedID
                && $0.name.compare(trimmed, options: [.caseInsensitive])
                    == .orderedSame
        }
        return clash ? .duplicate : nil
    }

    /// "Groq" becomes "Groq 2", then "Groq 3", so adding the same service twice
    /// does not fail on the name.
    func availableName(startingFrom base: String) -> String {
        let trimmed = base.trimmingCharacters(in: .whitespacesAndNewlines)
        let seed = trimmed.isEmpty ? "Model" : trimmed
        if validate(name: seed) == nil {
            return seed
        }
        for suffix in 2...99 {
            let candidate = "\(seed) \(suffix)"
            if validate(name: candidate) == nil {
                return candidate
            }
        }
        return UUID().uuidString
    }

    // MARK: - Changing the library

    /// Returns the new profile's id so the caller can select what it just added.
    @discardableResult
    mutating func add(_ profile: AssistantProfile) -> UUID? {
        guard !isFull, Self.isUsable(profile) else {
            return nil
        }
        var stored = profile
        stored.name = availableName(startingFrom: profile.name)
        profiles.append(stored)
        return stored.id
    }

    @discardableResult
    mutating func rename(
        _ id: UUID,
        to name: String
    ) -> AssistantProfileNameError? {
        if let error = validate(name: name, excluding: id) {
            return error
        }
        guard let index = profiles.firstIndex(where: { $0.id == id }) else {
            return nil
        }
        profiles[index].name = name
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return nil
    }

    /// Writes the live address and model back over a saved profile.
    @discardableResult
    mutating func update(
        _ id: UUID,
        provider: AssistantProvider,
        baseURL: String,
        model: String
    ) -> Bool {
        guard let index = profiles.firstIndex(where: { $0.id == id }) else {
            return false
        }
        profiles[index].provider = provider
        profiles[index].baseURL = baseURL
        profiles[index].model = model
        return true
    }

    @discardableResult
    mutating func duplicate(_ id: UUID) -> UUID? {
        guard let existing = profile(withID: id) else {
            return nil
        }
        return add(
            AssistantProfile(
                name: availableName(startingFrom: "\(existing.name) Copy"),
                provider: existing.provider,
                baseURL: existing.baseURL,
                model: existing.model
            )
        )
    }

    mutating func remove(_ id: UUID) {
        profiles.removeAll { $0.id == id }
    }

    private enum CodingKeys: String, CodingKey {
        case version
        case profiles
    }
}

extension AssistantProfileLibrary {
    /// The starting set, built from whatever the assistant was already
    /// configured with.
    ///
    /// An existing install has one working configuration and, if it is a hosted
    /// one, a key in Keychain under the old single-key account. Making that
    /// configuration the first profile is what carries both across without the
    /// user noticing anything happened.
    static func migrated(from settings: AssistantSettings) -> AssistantProfileLibrary {
        let name = AssistantService.matching(baseURL: settings.baseURL)?.name
            ?? settings.provider.title
        return AssistantProfileLibrary(profiles: [
            AssistantProfile(
                name: name,
                provider: settings.provider,
                baseURL: settings.baseURL,
                model: settings.model
            ),
        ])
    }
}

import Foundation

/// A colour scheme the user named and kept.
///
/// Only the colours are saved. Size and weight stay with the Dock settings
/// rather than the theme, because those are about legibility on this particular
/// Dock, not about how the tile is meant to look.
struct UserDockThemePreset: Identifiable, Equatable, Codable, Sendable {
    let id: UUID
    var name: String
    var colours: DockAppearance.Colours

    init(id: UUID = UUID(), name: String, colours: DockAppearance.Colours) {
        self.id = id
        self.name = name
        self.colours = colours
    }
}

/// Why a name was refused, in the words the sheet shows.
enum DockPresetNameError: Error, Equatable, Sendable {
    case empty
    case tooLong
    case duplicate

    var message: String {
        switch self {
        case .empty:
            "Give the preset a name."
        case .tooLong:
            "Names can be up to \(DockThemePresetLibrary.maximumNameLength) characters."
        case .duplicate:
            "There is already a theme with that name."
        }
    }
}

/// The saved presets, and every rule about changing them.
///
/// Versioned because these outlive any one build: a library written by a later
/// version is left alone rather than half-read, and anything unreadable is
/// dropped instead of taking the rest of the settings down with it.
struct DockThemePresetLibrary: Equatable, Codable, Sendable {
    static let currentVersion = 1
    static let maximumNameLength = 40
    static let empty = DockThemePresetLibrary(presets: [])

    var version: Int
    private(set) var presets: [UserDockThemePreset]

    init(version: Int = DockThemePresetLibrary.currentVersion, presets: [UserDockThemePreset]) {
        self.version = version
        self.presets = presets
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version)
            ?? Self.currentVersion
        guard version <= Self.currentVersion else {
            // Written by a newer build. Showing none is honest; rewriting what
            // we cannot read would destroy it.
            presets = []
            return
        }
        let decoded = try container.decodeIfPresent(
            [UserDockThemePreset].self,
            forKey: .presets
        ) ?? []
        presets = decoded.filter(Self.isUsable)
    }

    /// A preset with an unreadable colour cannot be applied, so it is not kept.
    private static func isUsable(_ preset: UserDockThemePreset) -> Bool {
        guard !preset.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }
        return [
            preset.colours.backgroundTop,
            preset.colours.backgroundBottom,
            preset.colours.number,
            preset.colours.label,
        ].allSatisfy { NSColorHex.isValid($0) }
    }

    func preset(withID id: UUID) -> UserDockThemePreset? {
        presets.first { $0.id == id }
    }

    // MARK: - Naming

    /// Trims the name and checks it against both the built-in themes and the
    /// saved ones, ignoring case, so the picker can never show two identical
    /// rows that mean different things.
    static func validate(
        name: String,
        in library: DockThemePresetLibrary,
        excluding excludedID: UUID? = nil
    ) -> Result<String, DockPresetNameError> {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return .failure(.empty)
        }
        guard trimmed.count <= maximumNameLength else {
            return .failure(.tooLong)
        }
        let taken = DockThemePreset.selectable.map(\.title)
            + [DockThemePreset.custom.title]
            + library.presets
                .filter { $0.id != excludedID }
                .map(\.name)
        guard !taken.contains(where: { $0.caseInsensitiveCompare(trimmed) == .orderedSame }) else {
            return .failure(.duplicate)
        }
        return .success(trimmed)
    }

    /// "Green Gradient" becomes "Green Gradient Copy", then "Green Gradient
    /// Copy 2", so duplicating twice does not fail on the name.
    func availableCopyName(for preset: UserDockThemePreset) -> String {
        let base = "\(preset.name) Copy"
        if case .success(let name) = Self.validate(name: base, in: self) {
            return name
        }
        for suffix in 2...99 {
            let candidate = "\(base) \(suffix)"
            if case .success(let name) = Self.validate(name: candidate, in: self) {
                return name
            }
        }
        return UUID().uuidString
    }

    // MARK: - Changing the library

    /// Returns the new preset's id so the caller can select what it just saved.
    @discardableResult
    mutating func add(name: String, colours: DockAppearance.Colours) -> UUID? {
        guard case .success(let validated) = Self.validate(name: name, in: self) else {
            return nil
        }
        let preset = UserDockThemePreset(name: validated, colours: colours)
        presets.append(preset)
        return preset.id
    }

    @discardableResult
    mutating func rename(_ id: UUID, to name: String) -> Bool {
        guard case .success(let validated) = Self.validate(
            name: name,
            in: self,
            excluding: id
        ), let index = presets.firstIndex(where: { $0.id == id }) else {
            return false
        }
        presets[index].name = validated
        return true
    }

    @discardableResult
    mutating func update(_ id: UUID, colours: DockAppearance.Colours) -> Bool {
        guard let index = presets.firstIndex(where: { $0.id == id }) else {
            return false
        }
        presets[index].colours = colours
        return true
    }

    @discardableResult
    mutating func duplicate(_ id: UUID) -> UUID? {
        guard let existing = preset(withID: id) else {
            return nil
        }
        return add(name: availableCopyName(for: existing), colours: existing.colours)
    }

    mutating func remove(_ id: UUID) {
        presets.removeAll { $0.id == id }
    }

    private enum CodingKeys: String, CodingKey {
        case version
        case presets
    }
}

/// What the theme row is showing, which is not always a preset.
enum DockThemeStatus: Equatable, Sendable {
    case builtIn(DockThemePreset)
    case saved(UserDockThemePreset)
    /// A saved preset with unsaved edits on top of it.
    case modified(UserDockThemePreset)
    /// Colours that match nothing, and have never been named.
    case unsaved

    var title: String {
        switch self {
        case .builtIn(let preset):
            preset.title
        case .saved(let preset):
            preset.name
        case .modified(let preset):
            "\(preset.name) — Modified"
        case .unsaved:
            "Custom — Unsaved"
        }
    }

    /// Whether the sheet and the update/revert buttons have anything to act on.
    var editedPreset: UserDockThemePreset? {
        if case .modified(let preset) = self {
            return preset
        }
        return nil
    }

    var isSavable: Bool {
        switch self {
        case .builtIn, .saved:
            false
        case .modified, .unsaved:
            true
        }
    }
}

extension DockThemePresetLibrary {
    /// Works out what the current colours amount to: a built-in theme, a saved
    /// preset, a saved preset with edits, or something not yet named.
    ///
    /// Derived rather than stored, so the label cannot drift away from the
    /// colours actually in force.
    func status(for appearance: DockAppearance) -> DockThemeStatus {
        if let id = appearance.userPresetID, let preset = preset(withID: id) {
            return preset.colours == appearance.colours
                ? .saved(preset)
                : .modified(preset)
        }
        if let colours = appearance.preset.colours, colours == appearance.colours {
            return .builtIn(appearance.preset)
        }
        return .unsaved
    }
}

/// Hex validation shared by the library and the colour wells.
enum NSColorHex {
    static func isValid(_ value: String) -> Bool {
        let trimmed = value
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "#", with: "")
        return trimmed.count == 6 && UInt32(trimmed, radix: 16) != nil
    }
}

/// A row in the theme picker.
///
/// The picker offers built-ins and saved presets together, so its selection has
/// to be able to name either. `unsaved` is the tag for the row that only exists
/// to show the current state; picking it does nothing.
enum DockThemeChoice: Hashable, Identifiable, Sendable {
    case builtIn(DockThemePreset)
    case saved(UUID)
    case unsaved

    var id: Self { self }

    init(_ status: DockThemeStatus) {
        switch status {
        case .builtIn(let preset):
            self = .builtIn(preset)
        case .saved(let preset), .modified(let preset):
            self = .saved(preset.id)
        case .unsaved:
            self = .unsaved
        }
    }
}

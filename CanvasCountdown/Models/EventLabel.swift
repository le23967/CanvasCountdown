import AppKit
import Foundation

/// A coloured mark the user puts on an event.
///
/// Deliberately one concept rather than two. "How urgent is this" and "what
/// kind of thing is this" are the same operation on the same field — a name and
/// a colour — and keeping them apart would mean two managers, two filters and
/// two colours competing for the same row. A label is whatever the user says it
/// is: Important, Personal, Society, Reading.
struct EventLabel: Identifiable, Equatable, Codable, Sendable {
    let id: UUID
    var name: String
    /// "#RRGGBB". Stored as text so a label survives a colour space change and
    /// can be read in preferences.
    var colorHex: String

    init(id: UUID = UUID(), name: String, colorHex: String) {
        self.id = id
        self.name = name
        self.colorHex = colorHex
    }

    var color: NSColor {
        NSColor(hex: colorHex) ?? .systemGray
    }
}

/// Why a label name was refused, in the words the sheet shows.
enum EventLabelNameError: Error, Equatable, Sendable {
    case empty
    case tooLong
    case duplicate

    var message: String {
        switch self {
        case .empty:
            "Give the label a name."
        case .tooLong:
            "Names can be up to \(EventLabelLibrary.maximumNameLength) characters."
        case .duplicate:
            "There is already a label with that name."
        }
    }
}

/// The labels themselves, and every rule about changing them.
///
/// Versioned and self-repairing for the same reason the Dock theme presets are:
/// this outlives any one build, and a library it cannot read is dropped rather
/// than taking the rest of the settings with it.
struct EventLabelLibrary: Equatable, Codable, Sendable {
    static let currentVersion = 1
    static let maximumNameLength = 24
    static let maximumCount = 24

    /// A starting set covering both of the things labels are for, so the
    /// feature is usable before anyone opens Settings. All of them can be
    /// renamed, recoloured or deleted.
    static let defaults = EventLabelLibrary(labels: [
        EventLabel(name: "Important", colorHex: "#E5484D"),
        EventLabel(name: "In Progress", colorHex: "#F5A623"),
        EventLabel(name: "Personal", colorHex: "#30A46C"),
        EventLabel(name: "Activity", colorHex: "#3E63DD"),
    ])

    static let empty = EventLabelLibrary(labels: [])

    var version: Int
    private(set) var labels: [EventLabel]

    init(version: Int = EventLabelLibrary.currentVersion, labels: [EventLabel]) {
        self.version = version
        self.labels = labels
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version)
            ?? Self.currentVersion
        guard version <= Self.currentVersion else {
            // Written by a newer build. Showing none is honest; rewriting what
            // we cannot read would destroy it.
            labels = []
            return
        }
        let decoded = try container.decodeIfPresent([EventLabel].self, forKey: .labels) ?? []
        labels = decoded.filter(Self.isUsable)
    }

    private static func isUsable(_ label: EventLabel) -> Bool {
        !label.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && NSColor(hex: label.colorHex) != nil
    }

    func label(id: UUID?) -> EventLabel? {
        guard let id else {
            return nil
        }
        return labels.first { $0.id == id }
    }

    var isFull: Bool {
        labels.count >= Self.maximumCount
    }

    // MARK: - Changing them

    /// Names are unique so a colour can be talked about. Case and surrounding
    /// space do not make two labels different.
    func validate(
        name: String,
        excluding id: UUID? = nil
    ) -> EventLabelNameError? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return .empty
        }
        if trimmed.count > Self.maximumNameLength {
            return .tooLong
        }
        let clash = labels.contains {
            $0.id != id
                && $0.name.compare(trimmed, options: [.caseInsensitive]) == .orderedSame
        }
        return clash ? .duplicate : nil
    }

    mutating func add(name: String, colorHex: String) throws -> EventLabel {
        if let error = validate(name: name) {
            throw error
        }
        let label = EventLabel(
            name: name.trimmingCharacters(in: .whitespacesAndNewlines),
            colorHex: colorHex
        )
        labels.append(label)
        return label
    }

    mutating func rename(_ id: UUID, to name: String) throws {
        if let error = validate(name: name, excluding: id) {
            throw error
        }
        guard let index = labels.firstIndex(where: { $0.id == id }) else {
            return
        }
        labels[index].name = name.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    mutating func recolor(_ id: UUID, to colorHex: String) {
        guard let index = labels.firstIndex(where: { $0.id == id }),
              NSColor(hex: colorHex) != nil else {
            return
        }
        labels[index].colorHex = colorHex
    }

    mutating func remove(_ id: UUID) {
        labels.removeAll { $0.id == id }
    }

    private enum CodingKeys: String, CodingKey {
        case version
        case labels
    }
}

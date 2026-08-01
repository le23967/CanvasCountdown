import Foundation

/// Everything asked of the assistant, kept between launches.
///
/// A conversation that only lived while the panel was open meant there was no
/// way to tell whether something had already been asked, or to read an answer
/// again the next morning. It is the user's own record, so it is stored beside
/// their labels and models rather than in `AppSettings`, and `reset()` leaves
/// it alone.
///
/// Versioned and self-repairing for the same reason the labels are: a log
/// written by a newer build reads as empty rather than being rewritten over.
struct AssistantConversationLog: Equatable, Codable, Sendable {
    static let currentVersion = 1

    /// Enough to look back over weeks of asking, bounded so an install that
    /// runs for a year does not grow this without end. The oldest go first,
    /// which is also the order in which they stop being worth keeping.
    static let maximumMessages = 200

    static let empty = AssistantConversationLog(messages: [])

    var version: Int
    private(set) var messages: [AssistantMessage]

    init(
        version: Int = AssistantConversationLog.currentVersion,
        messages: [AssistantMessage]
    ) {
        self.version = version
        self.messages = Self.usable(messages)
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version)
            ?? Self.currentVersion
        guard version <= Self.currentVersion else {
            // Written by a newer build. Showing none is honest; rewriting what
            // we cannot read would destroy it.
            messages = []
            return
        }
        messages = Self.usable(
            try container.decodeIfPresent([AssistantMessage].self, forKey: .messages) ?? []
        )
    }

    var isEmpty: Bool {
        messages.isEmpty
    }

    mutating func append(_ message: AssistantMessage) {
        messages = Self.usable(messages + [message])
    }

    mutating func removeAll() {
        messages = []
    }

    /// An empty turn says nothing and cannot be read back, so it is not kept.
    private static func usable(
        _ messages: [AssistantMessage]
    ) -> [AssistantMessage] {
        let kept = messages.filter {
            !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
        guard kept.count > maximumMessages else {
            return kept
        }
        return Array(kept.suffix(maximumMessages))
    }

    private enum CodingKeys: String, CodingKey {
        case version
        case messages
    }
}

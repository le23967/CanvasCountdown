import CoreGraphics
import Foundation

/// Where the user asked the assistant to appear.
///
/// There is no automatic mode. Choosing the container from the window width
/// meant a tiled or split-screen window silently lost the sidebar, which is the
/// one case where the panel is most wanted and least replaceable. The width now
/// decides how wide the panel is, never whether it exists.
enum AssistantPresentationPreference: String, Codable, CaseIterable, Identifiable, Sendable {
    case sidebar
    case popover

    var id: String { rawValue }

    var title: String {
        switch self {
        case .sidebar:
            "Sidebar"
        case .popover:
            "Popover"
        }
    }

    /// Anything unrecognised — notably the "automatic" that used to be stored —
    /// reads as a sidebar, so an old preferences file opens on the new default
    /// rather than throwing the whole assistant blob back to its defaults.
    init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = AssistantPresentationPreference(rawValue: raw) ?? .sidebar
    }
}

/// What is actually on screen. Exactly one at a time.
///
/// A single value rather than a handful of booleans: with `isSidebarOpen`,
/// `isPopoverOpen` and `isWindowOpen` it is possible to be in two places at
/// once, and every close path has to remember to clear all three.
enum ActiveAssistantPresentation: String, Equatable, Sendable {
    case closed
    case sidebar
    case popover
    case separateWindow

    var isOpen: Bool {
        self != .closed
    }
}

/// What the one box in the assistant is for.
///
/// There used to be three fields — ask, describe a task, change these drafts —
/// stacked one above another, each with its own button. The third read as the
/// save field, which meant the one place a typed sentence could be thrown away
/// by pressing the wrong thing. One box that says what it is doing replaces all
/// three.
///
/// `automatic` is the default so that the box costs nothing to use: most
/// sentences say plainly enough which of the two they are, and the panel shows
/// what it read before anything is sent. The other two are there for when that
/// guess is not wanted — choosing `ask` means a sentence is never read as a
/// task, however much it sounds like one.
enum AssistantComposerMode: String, Codable, CaseIterable, Identifiable, Sendable {
    case automatic
    case ask
    case addTask

    var id: String { rawValue }

    /// Kept to two words: this is the label on a chip under the field, not a
    /// sentence.
    var title: String {
        switch self {
        case .automatic:
            "Automatic"
        case .ask:
            "Ask"
        case .addTask:
            "Add task"
        }
    }

    var systemImage: String {
        switch self {
        case .automatic:
            "wand.and.stars"
        case .ask:
            "bubble.left.and.text.bubble.right"
        case .addTask:
            "calendar.badge.plus"
        }
    }
}

/// What the box will actually do when Return is pressed right now.
///
/// Not the same as the mode: drafts on screen take the box over, because a
/// sentence typed while a review is open is about that review, and on automatic
/// the sentence itself decides.
enum AssistantComposerRole: Equatable, Sendable {
    case ask
    case addTask
    case reviseDrafts
}

/// Reads a sentence and says which of the two it is.
///
/// Deliberately local and deliberately plain. Sending the sentence to the model
/// to be classified would cost a round trip before anything happened, and would
/// make a box that is meant to feel free to type in the slowest part of the
/// panel. Neither answer is expensive to be wrong about: a question produces an
/// answer, and a task produces a review that writes nothing until it is
/// confirmed. The panel says which way it read the sentence while it is still
/// being typed, so a wrong guess is corrected before it is sent, not after.
enum AssistantRequestReader {
    /// Words that open a question about the list rather than name a thing to
    /// put on it. "Should I start the essay today" has no question mark and is
    /// still plainly a question.
    private static let questionOpeners: Set<String> = [
        "am", "are", "can", "could", "did", "do", "does", "explain", "has",
        "have", "how", "is", "list", "must", "shall", "should", "show",
        "summarise", "summarize", "tell", "was", "were", "what", "whats",
        "when", "where", "which", "who", "why", "will", "would",
    ]

    /// Verbs that ask for something to be put on the list.
    private static let addingOpeners: Set<String> = [
        "add", "book", "create", "make", "new", "note", "plan", "put",
        "remind", "schedule", "set",
    ]

    /// Words that only appear when a sentence is naming a time.
    private static let timeWords: Set<String> = [
        "today", "tonight", "tomorrow", "monday", "tuesday", "wednesday",
        "thursday", "friday", "saturday", "sunday", "mon", "tue", "tues",
        "wed", "thu", "thur", "thurs", "fri", "sat", "sun", "january",
        // "may" is left out on purpose: it is a modal verb far more often than
        // it is a month, and a date in May still carries its day number.
        "february", "march", "april", "june", "july", "august",
        "september", "october", "november", "december", "jan", "feb", "mar",
        "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "am", "pm", "noon", "midnight", "morning", "afternoon", "evening",
        "weekly", "fortnightly", "daily",
    ]

    static func readsAsQuestion(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            // An empty box has not said anything yet, and asking is what most
            // people open the panel to do.
            return true
        }
        if trimmed.hasSuffix("?") || trimmed.hasSuffix("？") {
            return true
        }

        let words = trimmed.lowercased().split { !$0.isLetter && !$0.isNumber }
            .map(String.init)
        guard let first = words.first else {
            return true
        }
        if questionOpeners.contains(first) {
            return true
        }
        if addingOpeners.contains(first) {
            return false
        }
        // Nothing said when. Whatever this is, it cannot become a task with a
        // due date, so it goes to the side that can answer it.
        return !namesATime(trimmed.lowercased(), words: words)
    }

    private static func namesATime(_ text: String, words: [String]) -> Bool {
        if words.contains(where: timeWords.contains) {
            return true
        }
        // A clock time written as one word: 5pm, 11am.
        if words.contains(where: { word in
            (word.hasSuffix("am") || word.hasSuffix("pm"))
                && word.dropLast(2).allSatisfy(\.isNumber)
                && !word.dropLast(2).isEmpty
        }) {
            return true
        }
        // A bare number, but only one small enough to be a day or an hour. A
        // course code is a run of digits too, and "anything for 41021" is a
        // question about the list, not a task due in the year 41021.
        if words.contains(where: { word in
            word.allSatisfy(\.isNumber) && (1...2).contains(word.count)
        }) {
            return true
        }
        // 26/8, 17:00, 26.8 — the separator is what makes these a date or a
        // time rather than two unrelated numbers.
        return text.indices.contains { index in
            guard text[index] == ":" || text[index] == "/" else {
                return false
            }
            let after = text.index(after: index)
            guard index > text.startIndex, after < text.endIndex else {
                return false
            }
            return text[text.index(before: index)].isNumber
                && text[after].isNumber
        }
    }
}

/// How much room the assistant panel takes beside the assignments.
enum AssistantLayout {
    static let sidebarMinimum: CGFloat = 300
    static let sidebarIdeal: CGFloat = 380
    static let sidebarMaximum: CGFloat = 440

    /// What the assignment list keeps when the panel is open. Below this the
    /// titles are unreadable, so the panel gives width back instead of taking
    /// more.
    static let mainContentFloor: CGFloat = 300

    /// The room the list and the panel need to sit beside each other. Below it
    /// something has to give, and the navigation sidebar is the cheapest thing
    /// to fold away: it is one click to bring back, and unlike the window's own
    /// width it is the app's to decide.
    static var minimumWidthForBoth: CGFloat {
        mainContentFloor + sidebarMinimum
    }

    /// The presentation to use, given what the user asked for and what is
    /// already on screen.
    ///
    /// The window's width is not a parameter: it cannot take a container away.
    static func presentation(
        preference: AssistantPresentationPreference,
        current: ActiveAssistantPresentation
    ) -> ActiveAssistantPresentation {
        // A detached window is only ever chosen explicitly, so nothing here
        // takes it away.
        guard current != .separateWindow else {
            return .separateWindow
        }

        switch preference {
        case .sidebar:
            return .sidebar
        case .popover:
            return .popover
        }
    }

    /// The width to actually draw the panel at, which is the preferred width
    /// trimmed to what the window already has.
    ///
    /// This is what keeps a split-screen window the size the user left it: the
    /// panel is only ever as wide as the space beside the list, so opening it
    /// never asks macOS for a wider window. On a window too narrow to satisfy
    /// both, the two halve it rather than one of them disappearing.
    static func fittedSidebarWidth(
        preferred: CGFloat?,
        availableWidth: CGFloat
    ) -> CGFloat {
        let wanted = clampedSidebarWidth(preferred)
        guard availableWidth.isFinite, availableWidth > 0 else {
            return wanted
        }
        let roomBesideTheList = availableWidth - mainContentFloor
        guard roomBesideTheList >= sidebarMinimum else {
            return min(wanted, availableWidth / 2)
        }
        return min(wanted, roomBesideTheList)
    }

    /// Keeps a stored width inside the usable range, so a bad value from
    /// preferences cannot produce an unusable panel.
    static func clampedSidebarWidth(_ width: CGFloat?) -> CGFloat {
        guard let width, width.isFinite, width > 0 else {
            return sidebarIdeal
        }
        return min(max(width, sidebarMinimum), sidebarMaximum)
    }

    static func isValidSidebarWidth(_ width: CGFloat) -> Bool {
        width.isFinite && width >= sidebarMinimum && width <= sidebarMaximum
    }
}

/// The assignment an answer is about, kept short enough for a chip.
struct AssistantContext: Equatable, Sendable, Identifiable {
    let id: UUID
    let title: String
    let courseName: String?

    init(_ item: AssignmentListItem) {
        id = item.id
        title = item.title
        courseName = item.normalizedCourseName
    }

    /// Truncated for display. A full Canvas course title would stretch the chip
    /// across the panel.
    var chipTitle: String {
        title.count <= 32 ? title : title.prefix(31).trimmingCharacters(
            in: .whitespacesAndNewlines
        ) + "…"
    }
}

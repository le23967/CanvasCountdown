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

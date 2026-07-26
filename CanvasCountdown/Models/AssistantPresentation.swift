import CoreGraphics
import Foundation

/// What the user asked for.
enum AssistantPresentationPreference: String, Codable, CaseIterable, Identifiable, Sendable {
    case automatic
    case sidebar
    case popover

    var id: String { rawValue }

    var title: String {
        switch self {
        case .automatic:
            "Automatic"
        case .sidebar:
            "Sidebar"
        case .popover:
            "Popover"
        }
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

/// Chooses between a sidebar and a popover from the width actually available.
enum AssistantLayout {
    /// Below this the assignment list stops being readable: titles and course
    /// names start wrapping into slivers.
    static let mainContentMinimum: CGFloat = 680
    static let sidebarMinimum: CGFloat = 340
    static let sidebarIdeal: CGFloat = 380
    static let sidebarMaximum: CGFloat = 440

    /// The navigation sidebar is not available to either of them.
    static let navigationSidebarAllowance: CGFloat = 220

    /// Extra width required to go back to the sidebar, so a window resting near
    /// the threshold does not flip modes on every pixel of a drag.
    static let hysteresis: CGFloat = 60

    /// Width the two panes have to share, given the whole window.
    static func availableWidth(forWindowWidth width: CGFloat) -> CGFloat {
        max(0, width - navigationSidebarAllowance)
    }

    static var minimumWidthForSidebar: CGFloat {
        mainContentMinimum + sidebarMinimum
    }

    /// The presentation to use, given what the user asked for, how much room
    /// there is, and what is already on screen.
    ///
    /// The current presentation is part of the decision because of the
    /// hysteresis: leaving the sidebar is cheaper than re-entering it.
    static func presentation(
        preference: AssistantPresentationPreference,
        availableWidth: CGFloat,
        current: ActiveAssistantPresentation
    ) -> ActiveAssistantPresentation {
        // A detached window is only ever chosen explicitly, so nothing here
        // takes it away.
        guard current != .separateWindow else {
            return .separateWindow
        }

        switch preference {
        case .popover:
            return .popover
        case .sidebar:
            // An explicit choice is honoured while it is workable. When it is
            // not, falling back beats corrupting the layout.
            return fitsSidebar(availableWidth: availableWidth, current: current)
                ? .sidebar
                : .popover
        case .automatic:
            return fitsSidebar(availableWidth: availableWidth, current: current)
                ? .sidebar
                : .popover
        }
    }

    static func fitsSidebar(
        availableWidth: CGFloat,
        current: ActiveAssistantPresentation
    ) -> Bool {
        let threshold = current == .sidebar
            ? minimumWidthForSidebar
            : minimumWidthForSidebar + hysteresis
        return availableWidth >= threshold
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

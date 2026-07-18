import CoreGraphics

/// Where the search field is laid out, and how wide it may become.
///
/// The field began life as a toolbar item. A toolbar cannot give a text field
/// enough room beside the other controls in an ordinary window, so macOS moved
/// it into the overflow menu and clicking search looked like it did nothing.
/// The decision is recorded here rather than only in the view, so a test can
/// hold it in place.
enum SearchFieldPlacement {
    enum Container: Equatable {
        case toolbar
        case contentArea
    }

    static let container: Container = .contentArea

    static var isToolbarItem: Bool {
        container == .toolbar
    }

    /// Bounded so the field does not run the whole width of a wide window.
    static let maximumWidth: CGFloat = 520
}

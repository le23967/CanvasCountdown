import AppKit
import SwiftUI

/// Remembers how the toolbar should be shown, and puts it back each launch.
///
/// An earlier version pinned the mode to icon-only and reverted anything the
/// user chose. That left "Icon and Text" sitting in the context menu doing
/// nothing when clicked, which is worse than the layout problem it was meant to
/// solve: a control that looks available but ignores you.
///
/// Icons alone turned out to hide the toolbar. People handed the app to use it
/// did not find the course filter, because a funnel glyph does not say
/// "filter" to anyone who is not already looking for one. Every control carries
/// a short title, so showing those titles costs a little width and makes the
/// row legible without a hover.
///
/// The mode has to be stored and re-applied rather than set once, because this
/// toolbar has no autosaved configuration of its own: left alone it comes back
/// as icon-only on the next launch, whatever it was set to before. So the rule
/// is: choose Icon and Text the first time, apply the remembered choice every
/// time after that, and adopt whatever the user switches to.
struct ToolbarDisplayModeConfigurator: NSViewRepresentable {
    /// The remembered mode, as `NSToolbar.DisplayMode`'s raw value. Absent on a
    /// first run, which is the one time a default is chosen.
    static let storedModeKey = "CanvasCountdown.toolbar.displayMode"

    private let defaults: UserDefaults

    /// Defaults to the launch's own preference domain, so an isolated run
    /// cannot write into the real user's settings.
    init(defaults: UserDefaults? = nil) {
        self.defaults = defaults ?? AppEnvironment.current.preferences
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        // The window is not attached during make, so configure on the next turn
        // of the run loop.
        DispatchQueue.main.async {
            configure(view.window, coordinator: context.coordinator)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        configure(nsView.window, coordinator: context.coordinator)
    }

    private func configure(_ window: NSWindow?, coordinator: Coordinator) {
        guard let toolbar = window?.toolbar else {
            return
        }

        guard coordinator.hasApplied else {
            toolbar.displayMode = Self.startingMode(defaults: defaults)
            defaults.set(
                toolbar.displayMode.rawValue,
                forKey: Self.storedModeKey
            )
            coordinator.hasApplied = true
            // Items keep their order and cannot be dragged out; the display
            // mode stays available.
            toolbar.allowsUserCustomization = false
            return
        }

        // Applied already, so anything different now came from the user via the
        // toolbar's own context menu. Remember it instead of arguing with it.
        if toolbar.displayMode != Self.storedMode(defaults: defaults) {
            defaults.set(
                toolbar.displayMode.rawValue,
                forKey: Self.storedModeKey
            )
        }
    }

    /// What to show this launch: whatever was remembered, or Icon and Text the
    /// first time. Pure, so the rule can be checked without a window.
    static func startingMode(defaults: UserDefaults) -> NSToolbar.DisplayMode {
        storedMode(defaults: defaults) ?? .iconAndLabel
    }

    /// The three modes the toolbar's own context menu offers. `NSToolbar`'s
    /// display mode accepts any raw value it is handed, so a stored number has
    /// to be checked against this list rather than trusted to initialise.
    /// `.default` is deliberately absent: it is not something anyone can pick,
    /// and it draws as icons only, which is the state being corrected.
    private static let selectableModes: [NSToolbar.DisplayMode] = [
        .iconAndLabel, .iconOnly, .labelOnly,
    ]

    static func storedMode(defaults: UserDefaults) -> NSToolbar.DisplayMode? {
        guard let raw = defaults.object(forKey: storedModeKey) as? Int else {
            return nil
        }
        return selectableModes.first { $0.rawValue == UInt(clamping: raw) }
    }

    /// Tracks whether this window has already been set up, so a later update is
    /// read as the user's own change rather than a fresh launch.
    @MainActor
    final class Coordinator {
        var hasApplied = false
    }
}

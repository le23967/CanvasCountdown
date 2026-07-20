import AppKit
import SwiftUI

/// Chooses the toolbar's starting display mode, then leaves it alone.
///
/// An earlier version pinned the mode to icon-only and reverted anything the
/// user chose. That left "Icon and Text" sitting in the context menu doing
/// nothing when clicked, which is worse than the layout problem it was meant to
/// solve: a control that looks available but ignores you.
///
/// Every toolbar control now carries a short title, so "Icon and Text" renders
/// properly and the choice is the user's to make. This only picks the default
/// on first run, when macOS has not yet recorded a preference.
struct ToolbarDisplayModeConfigurator: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        // The window is not attached during make, so configure on the next turn
        // of the run loop.
        DispatchQueue.main.async {
            configure(view.window)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        configure(nsView.window)
    }

    private func configure(_ window: NSWindow?) {
        guard let toolbar = window?.toolbar else {
            return
        }
        // Only when macOS has no preference of its own.
        if toolbar.displayMode == .default {
            toolbar.displayMode = .iconOnly
        }
        // Items keep their order and cannot be dragged out; the display mode
        // stays available.
        toolbar.allowsUserCustomization = false
    }
}

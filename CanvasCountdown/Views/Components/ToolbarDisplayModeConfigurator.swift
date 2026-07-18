import AppKit
import SwiftUI

/// Pins the window's toolbar to icon-only and takes the display mode out of the
/// user's hands.
///
/// macOS otherwise offers "Icon and Text" and "Text Only" from the toolbar's
/// context menu. Those modes reserve room for a title under every control, and
/// the widest one, the course filter, then stretched the whole row and left
/// uneven gaps. The layout should not depend on a setting buried in a context
/// menu, so it is fixed here instead of asking the user to choose Icon Only.
struct ToolbarDisplayModeConfigurator: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        // The window is not attached yet during make, so configure on the next
        // turn of the run loop.
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
        if toolbar.displayMode != .iconOnly {
            toolbar.displayMode = .iconOnly
        }
        toolbar.allowsUserCustomization = false
        toolbar.autosavesConfiguration = false
    }
}

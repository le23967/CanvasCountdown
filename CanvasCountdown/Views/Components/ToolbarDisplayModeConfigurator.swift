import AppKit
import SwiftUI

/// Holds the window's toolbar at icon-only and disables customisation.
///
/// Every control in this toolbar is an icon with a tooltip and an accessibility
/// label; none carries a visible title. "Icon and Text" and "Text Only" would
/// therefore reserve label space that stays empty, stretch the row and push
/// items into the overflow menu.
///
/// AppKit exposes no public API to remove those entries from the toolbar's
/// context menu, so instead the mode is enforced: if the menu is used, the
/// toolbar is put straight back to icon-only and the choice has no visible
/// effect. The alternative, giving every control a visible title, is the "fake
/// support" this app deliberately avoids.
struct ToolbarDisplayModeConfigurator: NSViewRepresentable {
    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        // The window is not attached during make, so configure on the next turn
        // of the run loop.
        DispatchQueue.main.async {
            context.coordinator.attach(to: view.window)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.attach(to: nsView.window)
    }

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        coordinator.detach()
    }

    @MainActor
    final class Coordinator {
        private var observation: NSKeyValueObservation?
        private weak var toolbar: NSToolbar?
        private var isEnforcing = false

        func attach(to window: NSWindow?) {
            guard let toolbar = window?.toolbar else {
                return
            }
            enforce(on: toolbar)

            guard toolbar !== self.toolbar else {
                return
            }
            self.toolbar = toolbar
            observation?.invalidate()
            observation = toolbar.observe(
                \.displayMode,
                options: [.new]
            ) { [weak self] observed, _ in
                MainActor.assumeIsolated {
                    self?.enforce(on: observed)
                }
            }
        }

        func detach() {
            observation?.invalidate()
            observation = nil
            toolbar = nil
        }

        private func enforce(on toolbar: NSToolbar) {
            // The observer fires again when the mode is corrected, so the guard
            // keeps that from recursing.
            guard !isEnforcing else {
                return
            }
            isEnforcing = true
            defer { isEnforcing = false }

            if toolbar.displayMode != .iconOnly {
                toolbar.displayMode = .iconOnly
            }
            toolbar.allowsUserCustomization = false
            toolbar.autosavesConfiguration = false
        }
    }
}

import AppKit
import SwiftUI

/// Puts the assistant in its own floating panel.
///
/// It began as an inspector attached to the window. An inspector is part of the
/// window, so showing one either widens the window or squeezes the content;
/// macOS chose to widen, and the window jumped every time the assistant was
/// opened or closed. No amount of width tuning fixes that, because the panel and
/// the assignments were competing for the same rectangle.
///
/// A separate panel does not touch the main window at all. It floats above it,
/// can be dragged anywhere, and remembers where it was left.
@MainActor
final class AssistantWindowController: NSObject, NSWindowDelegate {
    private var panel: NSPanel?
    private var onClose: (() -> Void)?

    var isVisible: Bool {
        panel?.isVisible ?? false
    }

    func toggle<Content: View>(
        onClose: @escaping () -> Void,
        @ViewBuilder content: () -> Content
    ) {
        if isVisible {
            close()
        } else {
            show(onClose: onClose, content: content)
        }
    }

    func show<Content: View>(
        onClose: @escaping () -> Void,
        @ViewBuilder content: () -> Content
    ) {
        self.onClose = onClose

        if let panel {
            panel.makeKeyAndOrderFront(nil)
            return
        }

        let hosting = NSHostingController(rootView: content())
        let panel = NSPanel(contentViewController: hosting)
        panel.title = "Assistant"
        panel.styleMask = [.titled, .closable, .resizable, .utilityWindow]
        // Floating, so it stays with the work rather than behind it, but not
        // non-activating: people type in this panel.
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.delegate = self
        panel.setContentSize(NSSize(width: 340, height: 460))
        panel.minSize = NSSize(width: 280, height: 320)
        // Remembers where it was left, so it does not reappear in the middle of
        // whatever the user is reading.
        panel.setFrameAutosaveName("CanvasCountdown.assistantPanel")
        panel.center()
        panel.makeKeyAndOrderFront(nil)

        self.panel = panel
    }

    func close() {
        panel?.close()
    }

    /// Closing from the panel's own button must leave the app's idea of the
    /// state in step, or the toolbar button stops matching what is on screen.
    func windowWillClose(_ notification: Notification) {
        onClose?()
    }
}

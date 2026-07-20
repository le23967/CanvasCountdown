import AppKit
import SwiftUI

/// Leaves search mode when a click lands anywhere in the content area.
///
/// A SwiftUI tap gesture on the container was not enough: the list, its rows and
/// the empty state each handle their own clicks, so a click on "blank" content
/// often never reached the container and search stayed open.
///
/// A local mouse monitor sees the click wherever it lands. It only observes:
/// the event is always returned unchanged, so the row, menu or button under the
/// pointer still receives it. The toolbar is excluded by testing the window's
/// content area, which starts below the title bar, so clicking the field itself
/// or any toolbar control does not dismiss.
struct SearchDismissMonitor: ViewModifier {
    let isActive: Bool
    let onClickOutside: () -> Void

    @State private var monitor: Any?

    func body(content: Content) -> some View {
        content
            .onAppear(perform: install)
            .onDisappear(perform: remove)
            .onChange(of: isActive) { _, active in
                if active {
                    install()
                } else {
                    remove()
                }
            }
    }

    private func install() {
        guard isActive, monitor == nil else {
            return
        }
        monitor = NSEvent.addLocalMonitorForEvents(
            matching: [.leftMouseDown, .rightMouseDown]
        ) { event in
            if let window = event.window,
               window.contentLayoutRect.contains(event.locationInWindow) {
                onClickOutside()
            }
            // Never swallowed.
            return event
        }
    }

    private func remove() {
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }
        monitor = nil
    }
}

extension View {
    /// Dismisses search when the content area is clicked, without intercepting
    /// the click itself.
    func dismissesSearchOnContentClick(
        isActive: Bool,
        perform onClickOutside: @escaping () -> Void
    ) -> some View {
        modifier(
            SearchDismissMonitor(isActive: isActive, onClickOutside: onClickOutside)
        )
    }
}

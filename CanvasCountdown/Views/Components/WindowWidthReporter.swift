import AppKit
import SwiftUI

/// Reports the window's width, when the view joins a window and on every resize.
///
/// SwiftUI's geometry reporting did not deliver a width here in time to be
/// useful: the assistant asked how much room it had before any layout pass had
/// answered, read zero, and chose the popover on a window wide enough for the
/// sidebar. Asking AppKit is unambiguous.
///
/// The attach point is `viewDidMoveToWindow` rather than anything scheduled from
/// `makeNSView`: at that moment the view has no window yet, so a deferred read
/// is a race, and it lost about half the time.
struct WindowWidthReporter: NSViewRepresentable {
    let onChange: (CGFloat) -> Void

    func makeNSView(context: Context) -> WidthReportingView {
        let view = WidthReportingView()
        view.onChange = onChange
        return view
    }

    func updateNSView(_ nsView: WidthReportingView, context: Context) {
        nsView.onChange = onChange
    }

    static func dismantleNSView(_ nsView: WidthReportingView, coordinator: ()) {
        nsView.stopObserving()
    }

    final class WidthReportingView: NSView {
        var onChange: ((CGFloat) -> Void)?
        private var observer: (any NSObjectProtocol)?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            stopObserving()
            guard let window else {
                return
            }
            report(window.frame.width)
            observer = NotificationCenter.default.addObserver(
                forName: NSWindow.didResizeNotification,
                object: window,
                queue: .main
            ) { [weak self] notification in
                guard let resized = notification.object as? NSWindow else {
                    return
                }
                MainActor.assumeIsolated {
                    self?.report(resized.frame.width)
                }
            }
        }

        func stopObserving() {
            if let observer {
                NotificationCenter.default.removeObserver(observer)
            }
            observer = nil
        }

        private func report(_ width: CGFloat) {
            guard width > 0 else {
                return
            }
            onChange?(width)
        }
    }
}

import AppKit

/// A narrow abstraction around the AppKit Dock API so countdown coordination can
/// be tested without constructing an `NSApplication`.
@MainActor
protocol DockRendering: AnyObject {
    /// Renders the current countdown. Pass `nil` when there is no eligible event.
    func render(daysRemaining: Int?, label: String)
}

/// The subset of `NSDockTile` the countdown needs.
///
/// `NSDockTile` cannot be instantiated directly, so tests would otherwise be
/// forced to mutate `NSApplication.shared.dockTile` — the real Dock icon of
/// whoever is running the suite. This protocol lets them supply a double.
@MainActor
protocol DockTileSurface: AnyObject {
    var contentView: NSView? { get set }
    var badgeLabel: String? { get set }
    func display()
}

extension NSDockTile: DockTileSurface {}

@MainActor
final class DockTileService: DockRendering {
    private let dockTile: any DockTileSurface
    private let tileView: CountdownDockTileView

    init(dockTile: any DockTileSurface = NSApplication.shared.dockTile) {
        self.dockTile = dockTile
        self.tileView = CountdownDockTileView(
            frame: NSRect(x: 0, y: 0, width: 128, height: 128)
        )

        dockTile.contentView = tileView
    }

    func render(daysRemaining: Int?, label: String) {
        tileView.update(
            daysRemaining: daysRemaining,
            label: label.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? "DAYS"
                : label
        )
        dockTile.display()
    }
}

@MainActor
private final class CountdownDockTileView: NSView {
    private var daysRemaining: Int?
    private var label = "DAYS"

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        return nil
    }

    func update(daysRemaining: Int?, label: String) {
        self.daysRemaining = daysRemaining
        self.label = label
        needsDisplay = true
    }

    override var isFlipped: Bool {
        true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }

        let layout = DockTileLayout.make(
            in: bounds,
            countdownText: DockTileLayout.text(forDaysRemaining: daysRemaining)
        )
        let clipPath = NSBezierPath(
            roundedRect: layout.tileRect,
            xRadius: layout.cornerRadius,
            yRadius: layout.cornerRadius
        )
        clipPath.addClip()

        drawBackground(in: layout.tileRect)

        NSColor.black.withAlphaComponent(0.20).setFill()
        layout.tileRect.fill()

        drawLabel(with: layout)
        drawCountdown(with: layout)
    }

    private func drawBackground(in rect: NSRect) {
        let gradient = NSGradient(
            starting: NSColor(
                calibratedRed: 0.13,
                green: 0.27,
                blue: 0.55,
                alpha: 1
            ),
            ending: NSColor(
                calibratedRed: 0.24,
                green: 0.48,
                blue: 0.82,
                alpha: 1
            )
        )
        gradient?.draw(in: rect, angle: -90)
    }

    private func drawLabel(with layout: DockTileLayout) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: DockTileLayout.labelFont(ofSize: layout.labelFontSize),
            .foregroundColor: NSColor.white.withAlphaComponent(0.92),
            .kern: layout.labelFontSize * 0.05,
        ]

        let text = NSAttributedString(string: label, attributes: attributes)
        // Drawn at a point rather than into a rectangle: `draw(in:)` clips, and
        // the tile must never lose part of a glyph.
        text.draw(at: layout.labelOrigin(for: label, measuredSize: text.size()))
    }

    private func drawCountdown(with layout: DockTileLayout) {
        let value = DockTileLayout.text(forDaysRemaining: daysRemaining)
        let attributes: [NSAttributedString.Key: Any] = [
            .font: DockTileLayout.countdownFont(ofSize: layout.countdownFontSize),
            .foregroundColor: NSColor.white,
            .shadow: textShadow,
        ]

        NSAttributedString(string: value, attributes: attributes)
            .draw(at: layout.countdownOrigin(for: value))
    }

    private var textShadow: NSShadow {
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 2
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        return shadow
    }
}

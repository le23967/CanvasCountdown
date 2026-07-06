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

        let scaleX = bounds.width / 128
        let scaleY = bounds.height / 128
        let tileRect = NSRect(
            x: 5 * scaleX,
            y: 5 * scaleY,
            width: 118 * scaleX,
            height: 118 * scaleY
        )
        let cornerRadius = 25 * min(scaleX, scaleY)
        let clipPath = NSBezierPath(roundedRect: tileRect, xRadius: cornerRadius, yRadius: cornerRadius)
        clipPath.addClip()

        drawBackground(in: tileRect)

        NSColor.black.withAlphaComponent(0.20).setFill()
        tileRect.fill()

        drawLabel(in: tileRect)
        drawCountdown(in: tileRect)
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

    private func drawLabel(in rect: NSRect) {
        let paragraphStyle = NSMutableParagraphStyle()
        paragraphStyle.alignment = .center

        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(
                ofSize: max(6, rect.height * 0.105),
                weight: .semibold
            ),
            .foregroundColor: NSColor.white.withAlphaComponent(0.92),
            .paragraphStyle: paragraphStyle,
            .kern: 0.8,
        ]

        let text = NSAttributedString(string: label, attributes: attributes)
        let textRect = NSRect(
            x: rect.minX + 8,
            y: rect.minY + rect.height * 0.17,
            width: rect.width - 16,
            height: rect.height * 0.18
        )
        text.draw(in: textRect)
    }

    private func drawCountdown(in rect: NSRect) {
        let value = daysRemaining.map(String.init) ?? "–"
        let digitCount = value.count
        let fontScale: CGFloat
        switch digitCount {
        case 0...2:
            fontScale = 0.50
        case 3:
            fontScale = 0.41
        default:
            fontScale = 0.32
        }

        let paragraphStyle = NSMutableParagraphStyle()
        paragraphStyle.alignment = .center

        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(
                ofSize: max(12, rect.height * fontScale),
                weight: .bold
            ),
            .foregroundColor: NSColor.white,
            .paragraphStyle: paragraphStyle,
            .kern: -1.2,
            .shadow: textShadow,
        ]

        let text = NSAttributedString(string: value, attributes: attributes)
        let textRect = NSRect(
            x: rect.minX + 5,
            y: rect.minY + rect.height * 0.36,
            width: rect.width - 10,
            height: rect.height * 0.55
        )
        text.draw(in: textRect)
    }

    private var textShadow: NSShadow {
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 2
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        return shadow
    }
}

import AppKit

/// A narrow abstraction around the AppKit Dock API so countdown coordination can
/// be tested without constructing an `NSApplication`.
@MainActor
protocol DockRendering: AnyObject {
    /// Renders the current countdown. Pass `nil` when there is no eligible event.
    func render(daysRemaining: Int?, label: String)
    func apply(appearance: DockAppearance)
}

extension DockRendering {
    func apply(appearance: DockAppearance) {}
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

    init(
        dockTile: any DockTileSurface = NSApplication.shared.dockTile,
        appearance: DockAppearance = .defaults
    ) {
        self.dockTile = dockTile
        self.tileView = CountdownDockTileView(
            frame: NSRect(x: 0, y: 0, width: 128, height: 128),
            appearance: appearance
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

    func apply(appearance: DockAppearance) {
        tileView.apply(appearance: appearance)
        dockTile.display()
    }
}

@MainActor
private final class CountdownDockTileView: NSView {
    private var daysRemaining: Int?
    private var label = "DAYS"
    /// Named to avoid colliding with `NSView.appearance`.
    private var tileAppearance: DockAppearance

    init(frame frameRect: NSRect, appearance: DockAppearance) {
        self.tileAppearance = appearance
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

    func apply(appearance: DockAppearance) {
        tileAppearance = appearance
        needsDisplay = true
    }

    override var isFlipped: Bool {
        true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        DockTileRenderer.draw(
            in: bounds,
            daysRemaining: daysRemaining,
            label: label,
            appearance: tileAppearance
        )
    }
}

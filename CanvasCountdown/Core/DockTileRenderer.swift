import AppKit

/// Draws the countdown tile.
///
/// One implementation serves the live `NSDockTile` content view, the settings
/// preview, and the static application icon, so the three can never drift apart.
enum DockTileRenderer {
    static func draw(
        in bounds: CGRect,
        daysRemaining: Int?,
        label: String,
        appearance: DockAppearance
    ) {
        let text = DockTileLayout.text(forDaysRemaining: daysRemaining)
        let layout = DockTileLayout.make(
            in: bounds,
            countdownText: text,
            numberSize: appearance.numberSize,
            numberWeight: appearance.numberWeight
        )

        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }

        let clipPath = NSBezierPath(
            roundedRect: layout.tileRect,
            xRadius: layout.cornerRadius,
            yRadius: layout.cornerRadius
        )
        clipPath.addClip()

        NSGradient(
            starting: appearance.backgroundTopColor,
            ending: appearance.backgroundBottomColor
        )?.draw(in: layout.tileRect, angle: -90)

        drawLabel(label, layout: layout, appearance: appearance)
        drawCountdown(text, layout: layout, appearance: appearance)
    }

    /// A rendered image of the tile, used by the settings preview and by the
    /// icon generator.
    static func image(
        size: CGFloat,
        daysRemaining: Int?,
        label: String,
        appearance: DockAppearance
    ) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size))
        image.lockFocusFlipped(true)
        draw(
            in: CGRect(x: 0, y: 0, width: size, height: size),
            daysRemaining: daysRemaining,
            label: label,
            appearance: appearance
        )
        image.unlockFocus()
        return image
    }

    private static func drawLabel(
        _ label: String,
        layout: DockTileLayout,
        appearance: DockAppearance
    ) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: DockTileLayout.labelFont(ofSize: layout.labelFontSize),
            .foregroundColor: appearance.labelColor,
            .kern: layout.labelFontSize * 0.05,
        ]
        let text = NSAttributedString(string: label, attributes: attributes)
        // Drawn at a point rather than into a rectangle: `draw(in:)` clips, and
        // the tile must never lose part of a glyph.
        text.draw(at: layout.labelOrigin(for: label, measuredSize: text.size()))
    }

    private static func drawCountdown(
        _ value: String,
        layout: DockTileLayout,
        appearance: DockAppearance
    ) {
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.28)
        shadow.shadowBlurRadius = max(1, layout.tileRect.height * 0.015)
        shadow.shadowOffset = NSSize(
            width: 0,
            height: -max(0.5, layout.tileRect.height * 0.008)
        )

        let attributes: [NSAttributedString.Key: Any] = [
            .font: DockTileLayout.countdownFont(
                ofSize: layout.countdownFontSize,
                weight: layout.numberWeight
            ),
            .foregroundColor: appearance.numberColor,
            .shadow: shadow,
        ]
        NSAttributedString(string: value, attributes: attributes)
            .draw(at: layout.countdownOrigin(for: value))
    }
}

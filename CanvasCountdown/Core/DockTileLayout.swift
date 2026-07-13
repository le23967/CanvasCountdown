import AppKit

/// Geometry and type sizing for the Dock countdown tile.
///
/// Kept separate from the drawing code so the proportions can be asserted
/// directly: a small header band across the top, and a countdown numeral that
/// fills the middle of the tile at every Dock size.
///
/// Sizing works from the numeral's cap height rather than its line box. A line
/// box carries ascender and descender space that digits never use, and sizing
/// against it leaves the number looking far smaller than the tile allows.
struct DockTileLayout: Equatable {
    /// Fractions of the tile height.
    enum Proportion {
        static let inset: CGFloat = 0.031
        static let cornerRadius: CGFloat = 0.18
        static let labelTop: CGFloat = 0.05
        static let labelHeight: CGFloat = 0.17
        static let countdownTop: CGFloat = 0.24
        static let countdownHeight: CGFloat = 0.66
        static let labelFont: CGFloat = 0.135
        static let horizontalPadding: CGFloat = 0.04
        /// The header never competes with the number for attention.
        static let labelToCountdownCeiling: CGFloat = 0.38
        /// Share of the number band the digits' cap height should fill.
        static let digitCapFill: CGFloat = 0.94
        static let placeholderCapFill: CGFloat = 0.46
    }

    let tileRect: CGRect
    let cornerRadius: CGFloat
    let labelRect: CGRect
    let labelFontSize: CGFloat
    let countdownRect: CGRect
    let countdownFontSize: CGFloat

    /// The em dash shown when nothing is counting down.
    static let placeholder = "–"

    static func text(forDaysRemaining daysRemaining: Int?) -> String {
        daysRemaining.map(String.init) ?? placeholder
    }

    static func make(in bounds: CGRect, countdownText: String) -> DockTileLayout {
        let inset = min(bounds.width, bounds.height) * Proportion.inset
        let tileRect = bounds.insetBy(dx: inset, dy: inset)
        let height = tileRect.height
        let horizontalPadding = tileRect.width * Proportion.horizontalPadding

        let labelRect = CGRect(
            x: tileRect.minX + horizontalPadding,
            y: tileRect.minY + height * Proportion.labelTop,
            width: tileRect.width - horizontalPadding * 2,
            height: height * Proportion.labelHeight
        )
        let countdownRect = CGRect(
            x: tileRect.minX + horizontalPadding,
            y: tileRect.minY + height * Proportion.countdownTop,
            width: tileRect.width - horizontalPadding * 2,
            height: height * Proportion.countdownHeight
        )
        let countdownFontSize = countdownFontSize(
            for: countdownText,
            in: countdownRect
        )

        return DockTileLayout(
            tileRect: tileRect,
            cornerRadius: min(tileRect.width, height) * Proportion.cornerRadius,
            labelRect: labelRect,
            labelFontSize: min(
                max(5, height * Proportion.labelFont),
                countdownFontSize * Proportion.labelToCountdownCeiling
            ),
            countdownRect: countdownRect,
            countdownFontSize: countdownFontSize
        )
    }

    private init(
        tileRect: CGRect,
        cornerRadius: CGFloat,
        labelRect: CGRect,
        labelFontSize: CGFloat,
        countdownRect: CGRect,
        countdownFontSize: CGFloat
    ) {
        self.tileRect = tileRect
        self.cornerRadius = cornerRadius
        self.labelRect = labelRect
        self.labelFontSize = labelFontSize
        self.countdownRect = countdownRect
        self.countdownFontSize = countdownFontSize
    }

    /// A heavy rounded numeral, matching the large-number calendar countdown look.
    static func countdownFont(ofSize size: CGFloat) -> NSFont {
        rounded(NSFont.monospacedDigitSystemFont(ofSize: size, weight: .heavy), size: size)
    }

    static func labelFont(ofSize size: CGFloat) -> NSFont {
        rounded(NSFont.systemFont(ofSize: size, weight: .bold), size: size)
    }

    private static func rounded(_ font: NSFont, size: CGFloat) -> NSFont {
        guard let descriptor = font.fontDescriptor.withDesign(.rounded),
              let rounded = NSFont(descriptor: descriptor, size: size) else {
            return font
        }
        return rounded
    }

    /// The space the glyphs actually occupy: advance width by cap height.
    static func countdownInkSize(_ text: String, fontSize: CGFloat) -> CGSize {
        let font = countdownFont(ofSize: fontSize)
        let width = NSAttributedString(
            string: text,
            attributes: [.font: font]
        ).size().width
        return CGSize(width: width, height: font.capHeight)
    }

    /// Top-left point for drawing the countdown in a flipped view, chosen so the
    /// digits are optically centred on their cap height inside the number band.
    func countdownOrigin(for text: String) -> CGPoint {
        let font = Self.countdownFont(ofSize: countdownFontSize)
        let ink = Self.countdownInkSize(text, fontSize: countdownFontSize)
        return CGPoint(
            x: countdownRect.midX - ink.width / 2,
            y: countdownRect.midY + font.capHeight / 2 - font.ascender
        )
    }

    func labelOrigin(for text: String, measuredSize: CGSize) -> CGPoint {
        CGPoint(
            x: labelRect.midX - measuredSize.width / 2,
            y: labelRect.midY - measuredSize.height / 2
        )
    }

    /// Starts from the size that fills the band vertically, then shrinks until
    /// the glyphs also fit horizontally. Nothing is ever clipped, whatever the
    /// Dock size or the value.
    private static func countdownFontSize(
        for text: String,
        in band: CGRect
    ) -> CGFloat {
        guard band.height > 0, band.width > 0 else {
            return 1
        }

        let probe = countdownFont(ofSize: 100)
        let capRatio = max(0.4, probe.capHeight / 100)
        let fill = text == placeholder
            ? Proportion.placeholderCapFill
            : Proportion.digitCapFill

        var size = max(4, band.height * fill / capRatio)
        let minimumSize: CGFloat = 4
        while size > minimumSize {
            let ink = countdownInkSize(text, fontSize: size)
            if ink.width <= band.width, ink.height <= band.height {
                break
            }
            size *= 0.96
        }
        return max(minimumSize, size)
    }
}

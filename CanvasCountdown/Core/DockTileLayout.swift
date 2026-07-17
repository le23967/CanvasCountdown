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
    let numberWeight: DockNumberWeight

    /// The em dash shown when nothing is counting down.
    static let placeholder = "–"

    static func text(forDaysRemaining daysRemaining: Int?) -> String {
        daysRemaining.map(String.init) ?? placeholder
    }

    static func make(
        in bounds: CGRect,
        countdownText: String,
        numberSize: DockNumberSize = .medium,
        numberWeight: DockNumberWeight = .heavy
    ) -> DockTileLayout {
        let inset = min(bounds.width, bounds.height) * Proportion.inset
        let tileRect = bounds.insetBy(dx: inset, dy: inset)
        let height = tileRect.height
        let horizontalPadding = tileRect.width * Proportion.horizontalPadding

        // The bands are fixed, so the header always keeps its room and the
        // number always sits in the same region whatever size is chosen.
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

        // Fit first, then scale. Fitting alone would push every size preference
        // back to the same visual result, which is what made Small look no
        // different from Large.
        let safeFontSize = maximumSafeFontSize(
            for: countdownText,
            in: countdownRect,
            weight: numberWeight
        )
        let countdownFontSize = max(
            Self.minimumFontSize,
            safeFontSize * numberSize.fontScale
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
            countdownFontSize: countdownFontSize,
            numberWeight: numberWeight
        )
    }

    private init(
        tileRect: CGRect,
        cornerRadius: CGFloat,
        labelRect: CGRect,
        labelFontSize: CGFloat,
        countdownRect: CGRect,
        countdownFontSize: CGFloat,
        numberWeight: DockNumberWeight
    ) {
        self.tileRect = tileRect
        self.cornerRadius = cornerRadius
        self.labelRect = labelRect
        self.labelFontSize = labelFontSize
        self.countdownRect = countdownRect
        self.countdownFontSize = countdownFontSize
        self.numberWeight = numberWeight
    }

    /// A rounded numeral, matching the large-number calendar countdown look.
    static func countdownFont(
        ofSize size: CGFloat,
        weight: DockNumberWeight = .heavy
    ) -> NSFont {
        rounded(
            NSFont.monospacedDigitSystemFont(
                ofSize: size,
                weight: weight.fontWeight
            ),
            size: size
        )
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
    static func countdownInkSize(
        _ text: String,
        fontSize: CGFloat,
        weight: DockNumberWeight = .heavy
    ) -> CGSize {
        let font = countdownFont(ofSize: fontSize, weight: weight)
        let width = NSAttributedString(
            string: text,
            attributes: [.font: font]
        ).size().width
        return CGSize(width: width, height: font.capHeight)
    }

    /// Top-left point for drawing the countdown in a flipped view, chosen so the
    /// digits are optically centred on their cap height inside the number band.
    func countdownOrigin(for text: String) -> CGPoint {
        let font = Self.countdownFont(
            ofSize: countdownFontSize,
            weight: numberWeight
        )
        let ink = Self.countdownInkSize(
            text,
            fontSize: countdownFontSize,
            weight: numberWeight
        )
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

    static let minimumFontSize: CGFloat = 4

    /// The largest size at which this value still fits the band in both
    /// directions, before the user's size preference is applied.
    ///
    /// This is where digit-count adaptation happens: "1024" runs out of width
    /// long before "7" does, so it yields a smaller safe size.
    static func maximumSafeFontSize(
        for text: String,
        in band: CGRect,
        weight: DockNumberWeight = .heavy
    ) -> CGFloat {
        guard band.height > 0, band.width > 0 else {
            return 1
        }

        let probe = countdownFont(ofSize: 100, weight: weight)
        let capRatio = max(0.4, probe.capHeight / 100)
        let fill = text == placeholder
            ? Proportion.placeholderCapFill
            : Proportion.digitCapFill

        var size = max(minimumFontSize, band.height * fill / capRatio)
        while size > minimumFontSize {
            let ink = countdownInkSize(text, fontSize: size, weight: weight)
            if ink.width <= band.width, ink.height <= band.height {
                break
            }
            size *= 0.96
        }
        return max(minimumFontSize, size)
    }
}

import AppKit

enum DockNumberSize: String, Codable, CaseIterable, Identifiable, Sendable {
    case small
    case medium
    case large
    case extraLarge

    var id: String { rawValue }

    var title: String {
        switch self {
        case .small:
            "Small"
        case .medium:
            "Medium"
        case .large:
            "Large"
        case .extraLarge:
            "Extra Large"
        }
    }

    /// Fraction of the largest size that still fits the tile for the value being
    /// drawn. Applied after fitting, so digit-count adaptation is preserved and
    /// nothing can clip, while the choice stays visually obvious.
    var fontScale: CGFloat {
        switch self {
        case .small:
            0.58
        case .medium:
            0.72
        case .large:
            0.88
        case .extraLarge:
            1.0
        }
    }

    /// Short description used by the preview tiles and accessibility labels.
    var accessibilityDescription: String {
        "Select \(title.lowercased()) Dock countdown"
    }
}

enum DockNumberWeight: String, Codable, CaseIterable, Identifiable, Sendable {
    case bold
    case heavy

    var id: String { rawValue }

    var title: String {
        switch self {
        case .bold:
            "Bold"
        case .heavy:
            "Heavy"
        }
    }

    var fontWeight: NSFont.Weight {
        switch self {
        case .bold:
            .bold
        case .heavy:
            .heavy
        }
    }
}

enum DockThemePreset: String, Codable, CaseIterable, Identifiable, Sendable {
    case defaultBlue
    case daysMatter
    case dark
    case highContrast
    case custom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .defaultBlue:
            "Default Blue"
        case .daysMatter:
            "Days Matter Inspired"
        case .dark:
            "Dark"
        case .highContrast:
            "High Contrast"
        case .custom:
            "Custom"
        }
    }

    /// Presets the picker offers. `custom` only appears once the user has
    /// changed a colour themselves.
    static var selectable: [DockThemePreset] {
        [.defaultBlue, .daysMatter, .dark, .highContrast]
    }

    var colours: DockAppearance.Colours? {
        switch self {
        case .defaultBlue:
            DockAppearance.Colours(
                backgroundTop: "#21448C",
                backgroundBottom: "#3D7AD1",
                number: "#FFFFFF",
                label: "#EAF1FF"
            )
        case .daysMatter:
            DockAppearance.Colours(
                backgroundTop: "#F5F5F7",
                backgroundBottom: "#E3E4E8",
                number: "#D83A2E",
                label: "#5A5A5F"
            )
        case .dark:
            DockAppearance.Colours(
                backgroundTop: "#2B2B2F",
                backgroundBottom: "#131316",
                number: "#F2F2F7",
                label: "#A1A1AA"
            )
        case .highContrast:
            DockAppearance.Colours(
                backgroundTop: "#000000",
                backgroundBottom: "#000000",
                number: "#FFFFFF",
                label: "#FFFF00"
            )
        case .custom:
            nil
        }
    }
}

/// Everything the user can choose about how the Dock tile looks.
struct DockAppearance: Equatable, Codable, Sendable {
    struct Colours: Equatable, Codable, Sendable {
        var backgroundTop: String
        var backgroundBottom: String
        var number: String
        var label: String
    }

    /// WCAG contrast floors. The countdown is very large text, which the
    /// guidelines allow at 3:1; the small header is held to the 4.5:1 body-text
    /// figure.
    static let minimumNumberContrastRatio: Double = 3.0
    static let minimumLabelContrastRatio: Double = 4.5

    var preset: DockThemePreset
    var numberSize: DockNumberSize
    var numberWeight: DockNumberWeight
    var colours: Colours

    static let defaults = DockAppearance(
        preset: .defaultBlue,
        numberSize: .medium,
        numberWeight: .heavy,
        colours: DockThemePreset.defaultBlue.colours
            ?? Colours(
                backgroundTop: "#21448C",
                backgroundBottom: "#3D7AD1",
                number: "#FFFFFF",
                label: "#EAF1FF"
            )
    )

    static func applying(_ preset: DockThemePreset, to appearance: DockAppearance) -> DockAppearance {
        guard let colours = preset.colours else {
            return appearance
        }
        var updated = appearance
        updated.preset = preset
        updated.colours = colours
        return updated
    }

    var backgroundTopColor: NSColor {
        NSColor(hex: colours.backgroundTop) ?? .systemBlue
    }

    var backgroundBottomColor: NSColor {
        NSColor(hex: colours.backgroundBottom) ?? .systemBlue
    }

    var numberColor: NSColor {
        NSColor(hex: colours.number) ?? .white
    }

    var labelColor: NSColor {
        NSColor(hex: colours.label) ?? .white
    }

    /// Contrast of the number against the middle of the background gradient,
    /// which is the worst case a gradient can present.
    var numberContrastRatio: Double {
        NSColor.contrastRatio(
            between: numberColor,
            and: averageBackgroundColor
        )
    }

    var labelContrastRatio: Double {
        NSColor.contrastRatio(
            between: labelColor,
            and: averageBackgroundColor
        )
    }

    var hasSufficientContrast: Bool {
        numberContrastRatio >= Self.minimumNumberContrastRatio
            && labelContrastRatio >= Self.minimumLabelContrastRatio
    }

    var averageBackgroundColor: NSColor {
        NSColor.blend(backgroundTopColor, backgroundBottomColor)
    }

    /// Replaces any colour that cannot be read on the chosen background with
    /// black or white, whichever contrasts more.
    func correctedForContrast() -> DockAppearance {
        var corrected = self
        let background = averageBackgroundColor

        if numberContrastRatio < Self.minimumNumberContrastRatio {
            corrected.colours.number = NSColor
                .mostReadable(on: background)
                .hexString
        }
        if labelContrastRatio < Self.minimumLabelContrastRatio {
            corrected.colours.label = NSColor
                .mostReadable(on: background)
                .hexString
        }
        if corrected != self {
            corrected.preset = .custom
        }
        return corrected
    }
}

extension NSColor {
    convenience init?(hex: String) {
        var value = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        value = value.replacingOccurrences(of: "#", with: "")
        value = value.replacingOccurrences(of: " ", with: "")
        guard value.count == 6, let number = UInt32(value, radix: 16) else {
            return nil
        }
        self.init(
            srgbRed: CGFloat((number & 0xFF0000) >> 16) / 255,
            green: CGFloat((number & 0x00FF00) >> 8) / 255,
            blue: CGFloat(number & 0x0000FF) / 255,
            alpha: 1
        )
    }

    var hexString: String {
        guard let converted = usingColorSpace(.sRGB) else {
            return "#FFFFFF"
        }
        let red = Int(round(converted.redComponent * 255))
        let green = Int(round(converted.greenComponent * 255))
        let blue = Int(round(converted.blueComponent * 255))
        return String(format: "#%02X%02X%02X", red, green, blue)
    }

    /// WCAG relative luminance.
    var relativeLuminance: Double {
        guard let converted = usingColorSpace(.sRGB) else {
            return 0
        }
        func channel(_ value: CGFloat) -> Double {
            let component = Double(value)
            return component <= 0.03928
                ? component / 12.92
                : pow((component + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * channel(converted.redComponent)
            + 0.7152 * channel(converted.greenComponent)
            + 0.0722 * channel(converted.blueComponent)
    }

    static func contrastRatio(between first: NSColor, and second: NSColor) -> Double {
        let lighter = max(first.relativeLuminance, second.relativeLuminance)
        let darker = min(first.relativeLuminance, second.relativeLuminance)
        return (lighter + 0.05) / (darker + 0.05)
    }

    static func mostReadable(on background: NSColor) -> NSColor {
        let white = NSColor.white
        let black = NSColor.black
        return contrastRatio(between: white, and: background)
            >= contrastRatio(between: black, and: background)
            ? white
            : black
    }

    static func blend(_ first: NSColor, _ second: NSColor) -> NSColor {
        guard let a = first.usingColorSpace(.sRGB),
              let b = second.usingColorSpace(.sRGB) else {
            return first
        }
        return NSColor(
            srgbRed: (a.redComponent + b.redComponent) / 2,
            green: (a.greenComponent + b.greenComponent) / 2,
            blue: (a.blueComponent + b.blueComponent) / 2,
            alpha: 1
        )
    }
}

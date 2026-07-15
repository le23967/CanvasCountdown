import AppKit
import XCTest
@testable import CanvasCountdown

/// Dock appearance preferences: bounded sizing that cannot clip, readable
/// colours, working presets, and immediate application to the live tile.
final class DockAppearanceTests: XCTestCase {
    private let tileSizes: [CGFloat] = [32, 48, 64, 128, 256, 1_024]
    private let values = ["0", "7", "21", "157", "1024", DockTileLayout.placeholder]

    // MARK: - Sizing

    func testEverySizeAndWeightCombinationFitsEveryValue() {
        for size in tileSizes {
            for numberSize in DockNumberSize.allCases {
                for weight in DockNumberWeight.allCases {
                    for value in values {
                        let layout = DockTileLayout.make(
                            in: CGRect(x: 0, y: 0, width: size, height: size),
                            countdownText: value,
                            numberSize: numberSize,
                            numberWeight: weight
                        )
                        let ink = DockTileLayout.countdownInkSize(
                            value,
                            fontSize: layout.countdownFontSize,
                            weight: weight
                        )

                        XCTAssertLessThanOrEqual(
                            ink.width,
                            layout.countdownRect.width + 0.5,
                            "\(value) at \(numberSize.title)/\(weight.title)/\(size) overflows"
                        )
                        XCTAssertLessThanOrEqual(
                            ink.height,
                            layout.countdownRect.height + 0.5,
                            "\(value) at \(numberSize.title)/\(weight.title)/\(size) overflows"
                        )
                        XCTAssertLessThanOrEqual(
                            layout.countdownRect.maxY,
                            layout.tileRect.maxY + 0.5,
                            "The number band must stay inside the tile"
                        )
                    }
                }
            }
        }
    }

    func testLargerSizePreferencesProduceLargerNumbers() {
        var previous: CGFloat = 0
        for numberSize in DockNumberSize.allCases {
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 128, height: 128),
                countdownText: "7",
                numberSize: numberSize
            )
            XCTAssertGreaterThan(
                layout.countdownFontSize,
                previous,
                "\(numberSize.title) should not be smaller than the size below it"
            )
            previous = layout.countdownFontSize
        }
    }

    func testMediumKeepsTheDocumentedCentralProportion() {
        let layout = DockTileLayout.make(
            in: CGRect(x: 0, y: 0, width: 128, height: 128),
            countdownText: "7",
            numberSize: .medium
        )
        let fraction = layout.countdownRect.height / layout.tileRect.height

        XCTAssertGreaterThanOrEqual(fraction, 0.60)
        XCTAssertLessThanOrEqual(fraction, 0.70)
    }

    func testAdaptiveScalingBetweenDigitLengthsSurvivesEverySizePreference() {
        for numberSize in DockNumberSize.allCases {
            let one = fontSize(for: "7", numberSize: numberSize)
            let two = fontSize(for: "21", numberSize: numberSize)
            let three = fontSize(for: "157", numberSize: numberSize)
            let four = fontSize(for: "1024", numberSize: numberSize)

            XCTAssertGreaterThan(one, two, numberSize.title)
            XCTAssertGreaterThan(two, three, numberSize.title)
            XCTAssertGreaterThan(three, four, numberSize.title)
        }
    }

    // MARK: - Colour and contrast

    func testEveryPresetIsReadable() {
        for preset in DockThemePreset.selectable {
            let appearance = DockAppearance.applying(preset, to: .defaults)

            XCTAssertTrue(
                appearance.hasSufficientContrast,
                "\(preset.title) does not meet the contrast floor"
            )
            XCTAssertEqual(appearance.correctedForContrast(), appearance)
        }
    }

    func testUnreadableColoursAreCorrectedAutomatically() {
        var appearance = DockAppearance.defaults
        appearance.colours.backgroundTop = "#FFFFFF"
        appearance.colours.backgroundBottom = "#FFFFFF"
        appearance.colours.number = "#FFFFF0"
        appearance.colours.label = "#FEFEFE"

        XCTAssertFalse(appearance.hasSufficientContrast)

        let corrected = appearance.correctedForContrast()
        XCTAssertTrue(corrected.hasSufficientContrast)
        XCTAssertEqual(corrected.colours.number, "#000000")
        XCTAssertEqual(corrected.preset, .custom)
        XCTAssertEqual(
            corrected.colours.backgroundTop,
            "#FFFFFF",
            "Only the unreadable text colour is changed"
        )
    }

    func testContrastRatioMatchesKnownValues() {
        let white = NSColor(hex: "#FFFFFF")!
        let black = NSColor(hex: "#000000")!

        XCTAssertEqual(
            NSColor.contrastRatio(between: white, and: black),
            21,
            accuracy: 0.01
        )
        XCTAssertEqual(
            NSColor.contrastRatio(between: white, and: white),
            1,
            accuracy: 0.01
        )
    }

    func testHexRoundTrip() {
        let colour = NSColor(hex: "#21448C")
        XCTAssertNotNil(colour)
        XCTAssertEqual(colour?.hexString, "#21448C")
        XCTAssertNil(NSColor(hex: "not a colour"))
    }

    func testPresetsChangeColoursButKeepSizePreferences() {
        var appearance = DockAppearance.defaults
        appearance.numberSize = .extraLarge
        appearance.numberWeight = .bold

        let themed = DockAppearance.applying(.dark, to: appearance)

        XCTAssertEqual(themed.numberSize, .extraLarge)
        XCTAssertEqual(themed.numberWeight, .bold)
        XCTAssertEqual(themed.preset, .dark)
        XCTAssertEqual(themed.colours, DockThemePreset.dark.colours)
    }

    // MARK: - Rendering

    func testRenderedTileUsesTheChosenNumberColour() throws {
        var appearance = DockAppearance.defaults
        appearance.colours.backgroundTop = "#000000"
        appearance.colours.backgroundBottom = "#000000"
        appearance.colours.number = "#FF0000"

        let image = DockTileRenderer.image(
            size: 128,
            daysRemaining: 21,
            label: "DAYS",
            appearance: appearance
        )
        let rep = try XCTUnwrap(
            NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))
        )

        var foundRed = false
        for y in stride(from: 0, to: rep.pixelsHigh, by: 3) {
            for x in stride(from: 0, to: rep.pixelsWide, by: 3) {
                guard let colour = rep.colorAt(x: x, y: y)?
                    .usingColorSpace(.sRGB) else {
                    continue
                }
                if colour.redComponent > 0.6,
                   colour.greenComponent < 0.3,
                   colour.blueComponent < 0.3 {
                    foundRed = true
                    break
                }
            }
        }
        XCTAssertTrue(foundRed, "The chosen number colour must reach the tile")
    }

    // MARK: - Persistence and live application

    @MainActor
    func testAppearanceIsPersistedAndAppliedToTheTileImmediately() throws {
        let suiteName = "DockAppearanceTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let store = SettingsStore(defaults: defaults)
        var appearance = DockAppearance.defaults
        appearance.numberSize = .extraLarge
        appearance.preset = .dark
        appearance.colours = try XCTUnwrap(DockThemePreset.dark.colours)
        store.dockAppearance = appearance

        let reloaded = SettingsStore(defaults: defaults)
        XCTAssertEqual(reloaded.dockAppearance.numberSize, .extraLarge)
        XCTAssertEqual(reloaded.dockAppearance.preset, .dark)
        XCTAssertEqual(
            reloaded.dockAppearance.colours.number,
            DockThemePreset.dark.colours?.number
        )
    }

    @MainActor
    func testDockServiceRepaintsWhenAppearanceChanges() {
        let surface = AppearanceDockSurfaceSpy()
        let service = DockTileService(dockTile: surface, appearance: .defaults)
        service.render(daysRemaining: 3, label: "DAYS")
        let rendersAfterFirstDraw = surface.displayCount

        service.apply(appearance: DockAppearance.applying(.dark, to: .defaults))

        XCTAssertGreaterThan(surface.displayCount, rendersAfterFirstDraw)
        XCTAssertNil(surface.badgeLabel, "The badge is still never used")
        XCTAssertNotNil(surface.contentView)
    }

    // MARK: - Helpers

    private func fontSize(
        for text: String,
        numberSize: DockNumberSize
    ) -> CGFloat {
        DockTileLayout.make(
            in: CGRect(x: 0, y: 0, width: 128, height: 128),
            countdownText: text,
            numberSize: numberSize
        ).countdownFontSize
    }
}

@MainActor
private final class AppearanceDockSurfaceSpy: DockTileSurface {
    var contentView: NSView?
    var badgeLabel: String?
    private(set) var displayCount = 0

    func display() {
        displayCount += 1
    }
}

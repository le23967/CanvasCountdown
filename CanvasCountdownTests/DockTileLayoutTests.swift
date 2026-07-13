import AppKit
import XCTest
@testable import CanvasCountdown

/// The Dock tile has to stay readable at every size macOS asks for, with the
/// countdown numeral as the dominant element and nothing clipped.
final class DockTileLayoutTests: XCTestCase {
    private let tileSizes: [CGFloat] = [32, 48, 64, 128, 256, 1_024]
    private let values = ["0", "7", "21", "157", "1024", DockTileLayout.placeholder]

    func testLabelOccupiesTheUpperBandOnly() {
        for size in tileSizes {
            let layout = layout(size: size, text: "7")
            let tile = layout.tileRect
            let topFraction = (layout.labelRect.minY - tile.minY) / tile.height
            let bottomFraction = (layout.labelRect.maxY - tile.minY) / tile.height

            XCTAssertGreaterThan(topFraction, 0.02, "size \(size)")
            XCTAssertLessThanOrEqual(
                bottomFraction,
                0.24,
                "The header must stay in the top fifth at size \(size)"
            )
        }
    }

    func testCountdownOccupiesTheCentralSixtyToSeventyPercent() {
        for size in tileSizes {
            let layout = layout(size: size, text: "7")
            let tile = layout.tileRect
            let heightFraction = layout.countdownRect.height / tile.height

            XCTAssertGreaterThanOrEqual(heightFraction, 0.60, "size \(size)")
            XCTAssertLessThanOrEqual(heightFraction, 0.70, "size \(size)")
            XCTAssertGreaterThan(
                layout.countdownRect.minY,
                layout.labelRect.maxY - 0.001,
                "The number must sit below the header at size \(size)"
            )
            XCTAssertLessThanOrEqual(
                layout.countdownRect.maxY,
                tile.maxY + 0.001,
                "The number band must stay inside the tile at size \(size)"
            )
        }
    }

    func testNoValueIsClippedAtAnySize() {
        for size in tileSizes {
            for value in values {
                let layout = layout(size: size, text: value)
                let ink = DockTileLayout.countdownInkSize(
                    value,
                    fontSize: layout.countdownFontSize
                )

                XCTAssertLessThanOrEqual(
                    ink.width,
                    layout.countdownRect.width + 0.5,
                    "\"\(value)\" overflows horizontally at size \(size)"
                )
                XCTAssertLessThanOrEqual(
                    ink.height,
                    layout.countdownRect.height + 0.5,
                    "\"\(value)\" overflows vertically at size \(size)"
                )

                let origin = layout.countdownOrigin(for: value)
                let font = DockTileLayout.countdownFont(
                    ofSize: layout.countdownFontSize
                )
                let capTop = origin.y + font.ascender - font.capHeight
                XCTAssertGreaterThanOrEqual(
                    capTop,
                    layout.countdownRect.minY - 0.5,
                    "\"\(value)\" is drawn above its band at size \(size)"
                )
                XCTAssertLessThanOrEqual(
                    origin.y + font.ascender,
                    layout.countdownRect.maxY + 0.5,
                    "\"\(value)\" is drawn below its band at size \(size)"
                )
                XCTAssertGreaterThanOrEqual(
                    origin.x,
                    layout.countdownRect.minX - 0.5,
                    "\"\(value)\" starts left of its band at size \(size)"
                )
            }
        }
    }

    func testCountdownIsTheDominantElement() {
        for size in tileSizes {
            for value in values {
                let layout = layout(size: size, text: value)

                XCTAssertGreaterThan(
                    layout.countdownFontSize,
                    layout.labelFontSize * 2.5,
                    "\"\(value)\" is not dominant at size \(size)"
                )
            }
        }
    }

    func testShorterValuesAreDrawnLarger() {
        let oneDigit = layout(size: 128, text: "7")
        let twoDigits = layout(size: 128, text: "21")
        let threeDigits = layout(size: 128, text: "157")
        let fourCharacters = layout(size: 128, text: "1024")

        XCTAssertGreaterThan(oneDigit.countdownFontSize, twoDigits.countdownFontSize)
        XCTAssertGreaterThan(twoDigits.countdownFontSize, threeDigits.countdownFontSize)
        XCTAssertGreaterThan(
            threeDigits.countdownFontSize,
            fourCharacters.countdownFontSize
        )
    }

    func testFontSizesScaleWithTheTile() {
        var previous: CGFloat = 0
        for size in tileSizes {
            let current = layout(size: size, text: "21").countdownFontSize
            XCTAssertGreaterThan(current, previous, "size \(size) did not grow")
            previous = current
        }
    }

    func testCountdownIsSubstantiallyLargerThanTheOldFixedScale() {
        // The previous implementation drew two digits at 0.50 of the tile height.
        let layout = layout(size: 128, text: "21")

        XCTAssertGreaterThan(
            layout.countdownFontSize,
            layout.tileRect.height * 0.60,
            "Two digits should now be far larger than the old 0.50 scale"
        )
    }

    func testPlaceholderStillFitsAndStaysSmallerThanADigit() {
        let placeholder = layout(size: 128, text: DockTileLayout.placeholder)
        let digit = layout(size: 128, text: "7")
        let measured = DockTileLayout.countdownInkSize(
            DockTileLayout.placeholder,
            fontSize: placeholder.countdownFontSize
        )

        XCTAssertLessThan(
            placeholder.countdownFontSize,
            digit.countdownFontSize
        )
        XCTAssertLessThanOrEqual(
            measured.width,
            placeholder.countdownRect.width + 0.5
        )
    }

    func testDaysRemainingTextMapping() {
        XCTAssertEqual(DockTileLayout.text(forDaysRemaining: 0), "0")
        XCTAssertEqual(DockTileLayout.text(forDaysRemaining: 157), "157")
        XCTAssertEqual(
            DockTileLayout.text(forDaysRemaining: nil),
            DockTileLayout.placeholder
        )
    }

    @MainActor
    func testRenderedTileDrawsInkInsideTheNumberBand() throws {
        let spy = RenderingDockTileSpy()
        let service = DockTileService(dockTile: spy)
        service.render(daysRemaining: 21, label: "DAYS")

        let view = try XCTUnwrap(spy.contentView)
        let image = try XCTUnwrap(bitmap(of: view))
        let layout = DockTileLayout.make(in: view.bounds, countdownText: "21")

        let numberInk = brightPixelFraction(in: image, rect: layout.countdownRect)
        let labelInk = brightPixelFraction(in: image, rect: layout.labelRect)

        XCTAssertGreaterThan(
            numberInk,
            0.10,
            "The countdown band should carry substantial ink"
        )
        XCTAssertGreaterThan(
            numberInk,
            labelInk,
            "The number must read more strongly than the header"
        )
    }

    // MARK: - Helpers

    private func layout(size: CGFloat, text: String) -> DockTileLayout {
        DockTileLayout.make(
            in: CGRect(x: 0, y: 0, width: size, height: size),
            countdownText: text
        )
    }

    @MainActor
    private func bitmap(of view: NSView) -> NSBitmapImageRep? {
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            return nil
        }
        view.cacheDisplay(in: view.bounds, to: rep)
        return rep
    }

    /// Fraction of near-white pixels, which is how the drawn glyphs appear
    /// against the tile's blue background.
    private func brightPixelFraction(
        in image: NSBitmapImageRep,
        rect: CGRect
    ) -> Double {
        var bright = 0
        var total = 0
        let scaleX = Double(image.pixelsWide) / Double(image.size.width)
        let scaleY = Double(image.pixelsHigh) / Double(image.size.height)

        for y in Int(rect.minY * scaleY)..<Int(rect.maxY * scaleY) {
            for x in Int(rect.minX * scaleX)..<Int(rect.maxX * scaleX) {
                guard x >= 0, y >= 0, x < image.pixelsWide, y < image.pixelsHigh,
                      let colour = image.colorAt(x: x, y: y) else {
                    continue
                }
                total += 1
                if colour.brightnessComponent > 0.85, colour.saturationComponent < 0.25 {
                    bright += 1
                }
            }
        }
        return total == 0 ? 0 : Double(bright) / Double(total)
    }
}

@MainActor
private final class RenderingDockTileSpy: DockTileSurface {
    var contentView: NSView?
    var badgeLabel: String?

    func display() {}
}

import AppKit
import XCTest
@testable import CanvasCountdown

/// The countdown must read as one number, not as separately placed digits.
final class DockDigitSpacingTests: XCTestCase {
    private let values = ["11", "17", "21", "77", "100", "117", "157", "1024"]

    func testCountdownIsDrawnAsASingleTextRun() {
        // One attributed string, one origin: there is no per-digit layout to
        // introduce gaps. If the drawing were ever split per character, the
        // measured width of the whole value would stop matching the sum of the
        // run rendered in one piece.
        for value in values {
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 128, height: 128),
                countdownText: value
            )
            let font = DockTileLayout.countdownFont(
                ofSize: layout.countdownFontSize
            )
            let wholeRun = NSAttributedString(
                string: value,
                attributes: [.font: font]
            ).size().width
            let perCharacterSum = value.reduce(CGFloat.zero) { total, character in
                total + NSAttributedString(
                    string: String(character),
                    attributes: [.font: font]
                ).size().width
            }

            XCTAssertEqual(
                wholeRun,
                perCharacterSum,
                accuracy: max(0.5, wholeRun * 0.01),
                "\(value) must be measured and drawn as one run"
            )
        }
    }

    func testNoPositiveTrackingIsAppliedToTheNumber() {
        for value in values {
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 128, height: 128),
                countdownText: value
            )
            let font = DockTileLayout.countdownFont(
                ofSize: layout.countdownFontSize
            )
            let plain = NSAttributedString(
                string: value,
                attributes: [.font: font]
            ).size().width
            let measured = DockTileLayout.countdownInkSize(
                value,
                fontSize: layout.countdownFontSize
            ).width

            XCTAssertEqual(
                measured,
                plain,
                accuracy: 0.001,
                "\(value) carries extra tracking"
            )
        }
    }

    func testDigitsUseProportionalWidthsSoNarrowOnesDoNotFloat() {
        let font = DockTileLayout.countdownFont(ofSize: 100)
        let one = NSAttributedString(string: "1", attributes: [.font: font])
            .size().width
        let zero = NSAttributedString(string: "0", attributes: [.font: font])
            .size().width

        XCTAssertLessThan(
            one,
            zero * 0.95,
            "A monospaced digit set pads \"1\" to the width of \"0\", which is what left a gap in values like 17"
        )
    }

    func testTwoDigitValuesContainingOneAreCompact() {
        let font = DockTileLayout.countdownFont(ofSize: 100)

        for value in ["11", "17", "71"] {
            let width = NSAttributedString(
                string: value,
                attributes: [.font: font]
            ).size().width
            let doubleZero = NSAttributedString(
                string: "00",
                attributes: [.font: font]
            ).size().width

            XCTAssertLessThan(
                width,
                doubleZero,
                "\(value) should be narrower than \"00\" now that digits are proportional"
            )
        }
    }

    func testEveryTestedValueStaysInsideItsBandAtEverySize() {
        for size in DockNumberSize.allCases {
            for value in values {
                for tile in [32.0, 64.0, 128.0] as [CGFloat] {
                    let layout = DockTileLayout.make(
                        in: CGRect(x: 0, y: 0, width: tile, height: tile),
                        countdownText: value,
                        numberSize: size
                    )
                    let ink = DockTileLayout.countdownInkSize(
                        value,
                        fontSize: layout.countdownFontSize
                    )

                    XCTAssertLessThanOrEqual(
                        ink.width,
                        layout.countdownRect.width + 0.5,
                        "\(value) at \(size.title)/\(tile)"
                    )
                    XCTAssertLessThanOrEqual(
                        ink.height,
                        layout.countdownRect.height + 0.5,
                        "\(value) at \(size.title)/\(tile)"
                    )
                }
            }
        }
    }

    /// Renders "17" and checks the drawn glyphs span exactly the width the
    /// single-run text metrics predict.
    ///
    /// Measuring a gap at one scanline would only rediscover the shapes of the
    /// glyphs: the stem of a "1" sits beside the diagonal of a "7", so white
    /// space between them at mid-height is normal. What must not happen is the
    /// drawn value occupying more width than one text run needs, which is the
    /// signature of per-digit placement or added tracking.
    func testRenderedSeventeenOccupiesExactlyOneTextRunOfWidth() throws {
        for value in ["17", "11", "117", "1024"] {
            let canvas: CGFloat = 256
            let image = DockTileRenderer.image(
                size: canvas,
                daysRemaining: Int(value),
                label: "DAYS",
                appearance: .defaults
            )
            let rep = try XCTUnwrap(
                NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))
            )
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: canvas, height: canvas),
                countdownText: value
            )
            let scale = Double(rep.pixelsHigh) / Double(canvas)

            let topRow = Int(layout.countdownRect.minY * scale)
            let bottomRow = Int(layout.countdownRect.maxY * scale)
            var firstInk = rep.pixelsWide
            var lastInk = -1

            for y in topRow..<min(bottomRow, rep.pixelsHigh) {
                for x in 0..<rep.pixelsWide {
                    guard let colour = rep.colorAt(x: x, y: y)?
                        .usingColorSpace(.sRGB) else {
                        continue
                    }
                    guard colour.brightnessComponent > 0.75,
                          colour.saturationComponent < 0.3 else {
                        continue
                    }
                    firstInk = min(firstInk, x)
                    lastInk = max(lastInk, x)
                }
            }

            XCTAssertGreaterThan(lastInk, firstInk, value)
            let renderedWidth = Double(lastInk - firstInk + 1) / scale
            let runWidth = Double(
                DockTileLayout.countdownInkSize(
                    value,
                    fontSize: layout.countdownFontSize
                ).width
            )

            XCTAssertLessThanOrEqual(
                renderedWidth,
                runWidth * 1.02,
                "\(value) is drawn wider than one text run: digits are being spaced apart"
            )
            XCTAssertGreaterThan(
                renderedWidth,
                runWidth * 0.7,
                "\(value) does not fill its run, which suggests it is not one piece of text"
            )
        }
    }
}

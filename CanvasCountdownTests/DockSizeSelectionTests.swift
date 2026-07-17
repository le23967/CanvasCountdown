import AppKit
import XCTest
@testable import CanvasCountdown

/// The size preference has to stay visually meaningful, the preview has to be a
/// real control backed by the same renderer as the Dock, and the sample value
/// must never touch the real countdown.
@MainActor
final class DockSizeSelectionTests: XCTestCase {
    private let tileSizes: [CGFloat] = [32, 48, 64, 128, 256, 1_024]
    private let values = ["0", "7", "21", "157", "1024", DockTileLayout.placeholder]

    // MARK: - Sizes are visibly different

    func testEachSizeIsMeaningfullySmallerThanTheNextAtEveryTileSize() {
        for tile in tileSizes {
            for value in values {
                let small = fontSize(tile: tile, value: value, size: .small)
                let medium = fontSize(tile: tile, value: value, size: .medium)
                let large = fontSize(tile: tile, value: value, size: .large)
                let extraLarge = fontSize(tile: tile, value: value, size: .extraLarge)

                XCTAssertLessThan(small, medium, "\(value) at \(tile)")
                XCTAssertLessThan(medium, large, "\(value) at \(tile)")
                XCTAssertLessThan(large, extraLarge, "\(value) at \(tile)")
            }
        }
    }

    func testTheGapBetweenAdjacentSizesIsObvious() {
        // At least a tenth larger each step, so the choice reads at a glance.
        for value in values {
            let small = fontSize(tile: 128, value: value, size: .small)
            let medium = fontSize(tile: 128, value: value, size: .medium)
            let large = fontSize(tile: 128, value: value, size: .large)
            let extraLarge = fontSize(tile: 128, value: value, size: .extraLarge)

            XCTAssertGreaterThan(medium / small, 1.1, value)
            XCTAssertGreaterThan(large / medium, 1.1, value)
            XCTAssertGreaterThan(extraLarge / large, 1.1, value)
        }
    }

    func testSizeScaleIsAppliedOnTopOfTheSafeFittingSize() {
        for size in DockNumberSize.allCases {
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 128, height: 128),
                countdownText: "157",
                numberSize: size
            )
            let safe = DockTileLayout.maximumSafeFontSize(
                for: "157",
                in: layout.countdownRect
            )

            XCTAssertEqual(
                layout.countdownFontSize,
                safe * size.fontScale,
                accuracy: 0.001,
                "\(size.title) must be the safe size scaled by its preference"
            )
        }
    }

    func testFittingStillAdaptsToDigitCountWithinASingleSize() {
        for size in DockNumberSize.allCases {
            let one = fontSize(tile: 128, value: "7", size: size)
            let two = fontSize(tile: 128, value: "21", size: size)
            let three = fontSize(tile: 128, value: "157", size: size)
            let four = fontSize(tile: 128, value: "1024", size: size)

            XCTAssertGreaterThan(one, two, size.title)
            XCTAssertGreaterThan(two, three, size.title)
            XCTAssertGreaterThan(three, four, size.title)
        }
    }

    func testNothingClipsAtAnySizeOrValue() {
        for tile in tileSizes {
            for size in DockNumberSize.allCases {
                for weight in DockNumberWeight.allCases {
                    for value in values {
                        let layout = DockTileLayout.make(
                            in: CGRect(x: 0, y: 0, width: tile, height: tile),
                            countdownText: value,
                            numberSize: size,
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
                            "\(value)/\(size.title)/\(weight.title)/\(tile)"
                        )
                        XCTAssertLessThanOrEqual(
                            ink.height,
                            layout.countdownRect.height + 0.5,
                            "\(value)/\(size.title)/\(weight.title)/\(tile)"
                        )
                    }
                }
            }
        }
    }

    func testTheHeaderKeepsItsRoomAndBandsDoNotMoveWithSize() {
        var labelRects: Set<String> = []
        var countdownRects: Set<String> = []

        for size in DockNumberSize.allCases {
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 128, height: 128),
                countdownText: "21",
                numberSize: size
            )
            labelRects.insert("\(layout.labelRect)")
            countdownRects.insert("\(layout.countdownRect)")

            XCTAssertGreaterThan(layout.labelRect.height, 0)
            XCTAssertGreaterThanOrEqual(
                layout.countdownRect.minY,
                layout.labelRect.maxY - 0.001
            )
        }

        XCTAssertEqual(labelRects.count, 1, "The header band must not move")
        XCTAssertEqual(countdownRects.count, 1, "The number band must not move")
    }

    func testTheNumberStaysCentredInItsBandAtEverySize() {
        for size in DockNumberSize.allCases {
            let layout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 128, height: 128),
                countdownText: "21",
                numberSize: size
            )
            let font = DockTileLayout.countdownFont(
                ofSize: layout.countdownFontSize,
                weight: layout.numberWeight
            )
            let origin = layout.countdownOrigin(for: "21")
            let capTop = origin.y + font.ascender - font.capHeight
            let capCentre = capTop + font.capHeight / 2

            XCTAssertEqual(
                capCentre,
                layout.countdownRect.midY,
                accuracy: 0.5,
                "\(size.title) is not vertically centred"
            )

            let ink = DockTileLayout.countdownInkSize(
                "21",
                fontSize: layout.countdownFontSize
            )
            XCTAssertEqual(
                origin.x + ink.width / 2,
                layout.countdownRect.midX,
                accuracy: 0.5,
                "\(size.title) is not horizontally centred"
            )
        }
    }

    // MARK: - Preview is the same renderer

    func testPreviewAndDockUseTheSameLayoutCalculation() {
        // The preview passes the same appearance through the same entry point,
        // differing only in canvas size.
        for size in DockNumberSize.allCases {
            var appearance = DockAppearance.defaults
            appearance.numberSize = size

            let previewLayout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 76, height: 76),
                countdownText: "21",
                numberSize: appearance.numberSize,
                numberWeight: appearance.numberWeight
            )
            let dockLayout = DockTileLayout.make(
                in: CGRect(x: 0, y: 0, width: 76, height: 76),
                countdownText: "21",
                numberSize: appearance.numberSize,
                numberWeight: appearance.numberWeight
            )

            XCTAssertEqual(previewLayout, dockLayout)
        }
    }

    func testPreviewImageMatchesTheTileViewPixelForPixel() throws {
        var appearance = DockAppearance.defaults
        appearance.numberSize = .small

        let surface = SizeSelectionDockSurfaceSpy()
        let service = DockTileService(dockTile: surface, appearance: appearance)
        service.render(daysRemaining: 17, label: "DAYS")
        let view = try XCTUnwrap(surface.contentView)
        view.frame = NSRect(x: 0, y: 0, width: 128, height: 128)
        view.needsDisplay = true

        let rep = try XCTUnwrap(
            view.bitmapImageRepForCachingDisplay(in: view.bounds)
        )
        view.cacheDisplay(in: view.bounds, to: rep)
        let fromView = try XCTUnwrap(rep.representation(using: .png, properties: [:]))

        let previewImage = DockTileRenderer.image(
            size: 128,
            daysRemaining: 17,
            label: "DAYS",
            appearance: appearance
        )
        let previewRep = try XCTUnwrap(
            NSBitmapImageRep(data: try XCTUnwrap(previewImage.tiffRepresentation))
        )
        let fromRenderer = try XCTUnwrap(
            previewRep.representation(using: .png, properties: [:])
        )

        XCTAssertFalse(fromView.isEmpty)
        XCTAssertFalse(fromRenderer.isEmpty)

        // Compared as ink masks rather than raw colour. Both drawings come from
        // the same renderer, but the two capture paths (cacheDisplay versus
        // lockFocusFlipped) apply different colour management, which would make
        // a raw pixel comparison test the capture API rather than the tile.
        // Antialiased glyph edges keep this a shade under 1.0.
        XCTAssertGreaterThan(
            inkAgreement(between: rep, and: previewRep),
            0.95,
            "The settings preview must lay out exactly what the Dock tile draws"
        )

        // Control: a different size must score far lower, proving the measure
        // would actually catch a divergence.
        var otherSize = appearance
        otherSize.numberSize = .extraLarge
        let differentImage = DockTileRenderer.image(
            size: 128,
            daysRemaining: 17,
            label: "DAYS",
            appearance: otherSize
        )
        let differentRep = try XCTUnwrap(
            NSBitmapImageRep(
                data: try XCTUnwrap(differentImage.tiffRepresentation)
            )
        )
        XCTAssertLessThan(
            inkAgreement(between: rep, and: differentRep),
            0.95,
            "A different size preference must be visibly different"
        )
    }

    // MARK: - Selection behaviour

    func testSelectingEachSizePersistsAndReachesTheDockRenderer() async throws {
        for size in DockNumberSize.allCases {
            let context = try makeContext()
            await context.viewModel.start()

            var form = context.viewModel.settingsForm
            form.dockAppearance.numberSize = size
            context.viewModel.applySettings(form)

            XCTAssertEqual(
                SettingsStore(defaults: context.defaults)
                    .dockAppearance.numberSize,
                size,
                "\(size.title) must persist without a save step"
            )
            XCTAssertEqual(
                context.dock.appliedAppearances.last?.numberSize,
                size,
                "\(size.title) must reach the live Dock renderer"
            )
        }
    }

    func testSelectingASizeLeavesOtherAppearanceSettingsAlone() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.applyDockTheme(.dark)

        var form = context.viewModel.settingsForm
        form.dockAppearance.numberSize = .large
        context.viewModel.applySettings(form)

        let stored = SettingsStore(defaults: context.defaults).dockAppearance
        XCTAssertEqual(stored.numberSize, .large)
        XCTAssertEqual(stored.preset, .dark)
        XCTAssertEqual(stored.colours, DockThemePreset.dark.colours)
    }

    func testPreviewValueIsNotPartOfPersistedSettings() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let rendersBefore = context.dock.renders.count

        // The sample value lives only in the settings view's local state, so
        // there is nowhere for it to reach the model or the Dock.
        let mirror = Mirror(reflecting: context.viewModel.settingsForm)
        let names = mirror.children.compactMap(\.label)
        XCTAssertFalse(names.contains("previewValue"))
        XCTAssertFalse(names.contains("previewDays"))

        let appearanceMirror = Mirror(
            reflecting: context.viewModel.settingsForm.dockAppearance
        )
        XCTAssertFalse(
            appearanceMirror.children.compactMap(\.label).contains("previewValue")
        )
        XCTAssertEqual(
            context.dock.renders.count,
            rendersBefore,
            "Inspecting the preview must not repaint the Dock"
        )
    }

    func testTheRealDockKeepsShowingTheNearestDeadline() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        XCTAssertEqual(
            context.dock.renders.last?.daysRemaining,
            2,
            "The Dock shows the real countdown, never a preview sample"
        )

        var form = context.viewModel.settingsForm
        form.dockAppearance.numberSize = .small
        context.viewModel.applySettings(form)

        XCTAssertEqual(
            context.dock.renders.last?.daysRemaining,
            2,
            "Changing the size must not change the value the Dock shows"
        )
    }

    func testNoObsoleteSizeStateRemains() {
        // The size lives in one place only: the appearance model.
        let mirror = Mirror(reflecting: SettingsFormState())
        let names = mirror.children.compactMap(\.label)

        XCTAssertFalse(names.contains("numberSize"))
        XCTAssertFalse(names.contains("dockNumberSize"))
        XCTAssertTrue(names.contains("dockAppearance"))
        XCTAssertEqual(
            DockNumberSize.allCases.count,
            4,
            "Four sizes are offered, so four preview tiles are shown"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
        let dock: AppearanceRecordingDockSpy
    }

    private func makeContext() throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let start = calendar.startOfDay(for: .now)
        let due = calendar.date(byAdding: .day, value: 2, to: start) ?? start

        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Physics lab",
                    courseName: "PHYS200",
                    dueDate: calendar.date(
                        bySettingHour: 12,
                        minute: 0,
                        second: 0,
                        of: due
                    ) ?? due,
                    source: .canvasCalendarFeed
                ),
            ]
        )

        let suiteName = "DockSizeSelectionTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let dock = AppearanceRecordingDockSpy()
        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: SizeSelectionCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: dock,
            notificationScheduler: InertNotificationScheduler(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel, defaults: defaults, dock: dock)
    }

    private func fontSize(
        tile: CGFloat,
        value: String,
        size: DockNumberSize
    ) -> CGFloat {
        DockTileLayout.make(
            in: CGRect(x: 0, y: 0, width: tile, height: tile),
            countdownText: value,
            numberSize: size
        ).countdownFontSize
    }

    /// Fraction of sampled points where both renderings agree on whether the
    /// point is glyph ink or background.
    ///
    /// Sampled by fraction rather than pixel index so the two reps may have
    /// different backing scales, and thresholded against each image's own mean
    /// luminance so a colour-management difference between capture paths does
    /// not register as a layout difference.
    private func inkAgreement(
        between first: NSBitmapImageRep,
        and second: NSBitmapImageRep
    ) -> Double {
        let samples = 96
        let firstMean = meanLuminance(of: first, samples: samples)
        let secondMean = meanLuminance(of: second, samples: samples)
        var agreeing = 0
        var total = 0

        for row in 0..<samples {
            for column in 0..<samples {
                let u = (Double(column) + 0.5) / Double(samples)
                let v = (Double(row) + 0.5) / Double(samples)

                guard
                    let a = sample(first, u: u, v: v),
                    let b = sample(second, u: u, v: v)
                else {
                    continue
                }
                total += 1
                let aIsInk = a.brightnessComponent > firstMean + 0.2
                let bIsInk = b.brightnessComponent > secondMean + 0.2
                if aIsInk == bIsInk {
                    agreeing += 1
                }
            }
        }
        return total == 0 ? 0 : Double(agreeing) / Double(total)
    }

    private func meanLuminance(
        of rep: NSBitmapImageRep,
        samples: Int
    ) -> CGFloat {
        var total: CGFloat = 0
        var count = 0
        for row in 0..<samples {
            for column in 0..<samples {
                let u = (Double(column) + 0.5) / Double(samples)
                let v = (Double(row) + 0.5) / Double(samples)
                guard let colour = sample(rep, u: u, v: v) else {
                    continue
                }
                total += colour.brightnessComponent
                count += 1
            }
        }
        return count == 0 ? 0 : total / CGFloat(count)
    }

    private func sample(
        _ rep: NSBitmapImageRep,
        u: Double,
        v: Double
    ) -> NSColor? {
        rep.colorAt(
            x: min(rep.pixelsWide - 1, Int(u * Double(rep.pixelsWide))),
            y: min(rep.pixelsHigh - 1, Int(v * Double(rep.pixelsHigh)))
        )?.usingColorSpace(.sRGB)
    }
}

@MainActor
final class AppearanceRecordingDockSpy: DockRendering {
    struct Render: Equatable {
        let daysRemaining: Int?
        let label: String
    }

    private(set) var renders: [Render] = []
    private(set) var appliedAppearances: [DockAppearance] = []

    func render(daysRemaining: Int?, label: String) {
        renders.append(Render(daysRemaining: daysRemaining, label: label))
    }

    func apply(appearance: DockAppearance) {
        appliedAppearances.append(appearance)
    }
}

@MainActor
private final class SizeSelectionDockSurfaceSpy: DockTileSurface {
    var contentView: NSView?
    var badgeLabel: String?
    func display() {}
}

private actor SizeSelectionCoordinatorStub: FeedRefreshCoordinating {
    func preview(feedURL: URL, now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func preview(now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date
    ) throws -> RefreshResult {
        throw RefreshCoordinatorError.noEventsSelected
    }

    func refresh(now: Date, trigger: RefreshTrigger) throws -> RefreshResult {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func diagnosticSnapshot() -> FeedRefreshDiagnostic? { nil }
}

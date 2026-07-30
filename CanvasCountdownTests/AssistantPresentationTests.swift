import CoreGraphics
import Foundation
import XCTest
@testable import CanvasCountdown

/// How the assistant decides where to appear, and what must survive that
/// decision changing. All fixtures are invented.
@MainActor
final class AssistantPresentationTests: XCTestCase {
    /// A detail column with room to spare.
    private let wideWindow: CGFloat = 1_400
    /// About what a half-screen window leaves: the case that used to lose the
    /// sidebar altogether.
    private let narrowWindow: CGFloat = 620

    // MARK: - Choosing a presentation

    func testTheSidebarIsShownWhateverTheWindowIsDoing() {
        XCTAssertEqual(
            AssistantLayout.presentation(preference: .sidebar, current: .closed),
            .sidebar,
            "Split screen and full screen must not undo what the user chose"
        )
    }

    func testAPopoverChoiceIsAlsoHonoured() {
        XCTAssertEqual(
            AssistantLayout.presentation(preference: .popover, current: .closed),
            .popover
        )
    }

    func testThereIsNoAutomaticPreference() {
        XCTAssertEqual(
            AssistantPresentationPreference.allCases,
            [.sidebar, .popover],
            "The width decides how wide the panel is, never whether it exists"
        )
    }

    func testAStoredAutomaticPreferenceReadsAsSidebar() throws {
        let decoded = try JSONDecoder().decode(
            AssistantPresentationPreference.self,
            from: Data(#""automatic""#.utf8)
        )

        XCTAssertEqual(decoded, .sidebar)
    }

    func testASeparateWindowIsNeverTakenAwayByResizing() {
        let presentation = AssistantLayout.presentation(
            preference: .sidebar,
            current: .separateWindow
        )

        XCTAssertEqual(presentation, .separateWindow)
    }

    // MARK: - Width bounds

    func testAWideWindowGetsThePreferredWidth() {
        XCTAssertEqual(
            AssistantLayout.fittedSidebarWidth(
                preferred: 400,
                availableWidth: wideWindow
            ),
            400
        )
    }

    func testTheSidebarNeverAsksForMoreThanTheColumnHas() {
        // The whole point: on a split-screen window the panel is trimmed rather
        // than the window being widened to fit it.
        for available in stride(from: CGFloat(200), through: 1_600, by: 20) {
            let width = AssistantLayout.fittedSidebarWidth(
                preferred: AssistantLayout.sidebarMaximum,
                availableWidth: available
            )

            XCTAssertLessThanOrEqual(
                width,
                available,
                "A panel wider than the column would force macOS to resize the window"
            )
            XCTAssertGreaterThan(width, 0)
        }
    }

    func testAHalfScreenColumnGetsANarrowerPanelRatherThanNone() {
        let width = AssistantLayout.fittedSidebarWidth(
            preferred: AssistantLayout.sidebarIdeal,
            availableWidth: narrowWindow
        )

        XCTAssertLessThan(width, AssistantLayout.sidebarIdeal)
        XCTAssertLessThanOrEqual(width, narrowWindow - AssistantLayout.mainContentFloor)
        XCTAssertGreaterThanOrEqual(width, AssistantLayout.sidebarMinimum)
    }

    func testTheListKeepsItsFloorWhileThereIsRoomForBoth() {
        let available = AssistantLayout.mainContentFloor
            + AssistantLayout.sidebarMinimum
            + 20
        let width = AssistantLayout.fittedSidebarWidth(
            preferred: AssistantLayout.sidebarMaximum,
            availableWidth: available
        )

        XCTAssertEqual(width, available - AssistantLayout.mainContentFloor)
        XCTAssertGreaterThanOrEqual(width, AssistantLayout.sidebarMinimum)
    }

    func testATinyWindowSplitsTheDifferenceInsteadOfHidingThePanel() {
        let available: CGFloat = 400
        let width = AssistantLayout.fittedSidebarWidth(
            preferred: AssistantLayout.sidebarIdeal,
            availableWidth: available
        )

        XCTAssertEqual(width, available / 2)
    }

    func testAnUnmeasuredWindowFallsBackToThePreferredWidth() {
        XCTAssertEqual(
            AssistantLayout.fittedSidebarWidth(preferred: 400, availableWidth: 0),
            400
        )
    }

    func testSidebarWidthStaysWithinItsBounds() {
        XCTAssertEqual(
            AssistantLayout.clampedSidebarWidth(10),
            AssistantLayout.sidebarMinimum
        )
        XCTAssertEqual(
            AssistantLayout.clampedSidebarWidth(9_999),
            AssistantLayout.sidebarMaximum
        )
        XCTAssertEqual(AssistantLayout.clampedSidebarWidth(400), 400)
        XCTAssertEqual(
            AssistantLayout.clampedSidebarWidth(nil),
            AssistantLayout.sidebarIdeal
        )
        XCTAssertEqual(
            AssistantLayout.clampedSidebarWidth(.nan),
            AssistantLayout.sidebarIdeal
        )
    }

    func testDeclaredBoundsMatchTheIntendedRange() {
        XCTAssertEqual(AssistantLayout.sidebarMinimum, 300)
        XCTAssertEqual(AssistantLayout.sidebarIdeal, 380)
        XCTAssertEqual(AssistantLayout.sidebarMaximum, 440)
    }

    func testOnlyAValidWidthIsRemembered() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.rememberSidebarWidth(400)
        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).assistant.sidebarWidth,
            400
        )

        context.viewModel.rememberSidebarWidth(2_000)
        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).assistant.sidebarWidth,
            400,
            "An out-of-range width must not replace a good one"
        )
    }

    // MARK: - One presentation at a time

    func testOnlyOnePresentationIsEverActive() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.openAssistant()
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)

        context.viewModel.showAssistantAsPopover()
        XCTAssertEqual(context.viewModel.assistantPresentation, .popover)

        context.viewModel.openAssistantInSeparateWindow()
        XCTAssertEqual(context.viewModel.assistantPresentation, .separateWindow)

        context.viewModel.closeAssistant()
        XCTAssertEqual(context.viewModel.assistantPresentation, .closed)
        XCTAssertFalse(context.viewModel.isAssistantOpen)
    }

    func testASeparateWindowIsNeverTheDefault() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.toggleAssistant()

        XCTAssertNotEqual(context.viewModel.assistantPresentation, .separateWindow)
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
    }

    func testClosingReturnsTheWindowToTheAssignments() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.openAssistant()

        context.viewModel.toggleAssistant()

        XCTAssertEqual(context.viewModel.assistantPresentation, .closed)
    }

    // MARK: - Nothing is lost when the container changes

    func testMovingBetweenContainersKeepsTheConversationDraftAndContext() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        let item = try XCTUnwrap(context.viewModel.assignments.first)
        context.viewModel.openAssistant(about: item)
        context.viewModel.assistantDraftInput = "half a question"
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)

        // Out to a popover and back: nothing typed or said is lost.
        context.viewModel.showAssistantAsPopover()
        XCTAssertEqual(context.viewModel.assistantPresentation, .popover)
        XCTAssertEqual(context.viewModel.assistantDraftInput, "half a question")
        XCTAssertEqual(context.viewModel.assistantContext?.id, item.id)

        context.viewModel.showAssistantAsSidebar()
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
        XCTAssertEqual(context.viewModel.assistantDraftInput, "half a question")
        XCTAssertEqual(context.viewModel.assistantContext?.id, item.id)
    }

    /// The reported bug: a split-screen window could not open the assistant at
    /// all, because the panel was chosen away on width. Nothing about the
    /// window reaches this decision any more.
    func testTheSidebarOpensWhateverTheWindowIsDoing() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.openAssistant()

        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
        XCTAssertGreaterThan(context.viewModel.preferredSidebarWidth, 0)
    }

    func testAPinnedSidebarSurvivesResizing() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.setAssistantPreference(.sidebar)
        context.viewModel.openAssistant()


        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
    }

    func testAPinnedPopoverSurvivesResizing() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.setAssistantPreference(.popover)
        context.viewModel.openAssistant()


        XCTAssertEqual(context.viewModel.assistantPresentation, .popover)
    }

    func testResizingWhileClosedDoesNotOpenAnything() async throws {
        let context = try makeContext()
        await context.viewModel.start()


        XCTAssertEqual(context.viewModel.assistantPresentation, .closed)
    }

    // MARK: - Context

    func testContextNarrowsWhatIsSentRatherThanAddingToIt() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let all = context.viewModel.assistantDigests().count
        XCTAssertGreaterThan(all, 1)

        let item = try XCTUnwrap(context.viewModel.assignments.first)
        context.viewModel.openAssistant(about: item)

        XCTAssertEqual(
            context.viewModel.assistantDigests().count,
            1,
            "Asking about one deadline must not send the whole term"
        )

        context.viewModel.clearAssistantContext()
        XCTAssertEqual(context.viewModel.assistantDigests().count, all)
    }

    func testTheContextChipTruncatesALongTitle() {
        let long = AssistantContext(
            AssignmentListItem(
                id: UUID(),
                title: String(repeating: "A", count: 90),
                courseName: "99999 Example Course",
                dueDate: .now,
                isCompleted: false,
                isIgnored: false,
                isManual: false
            )
        )

        XCTAssertLessThanOrEqual(long.chipTitle.count, 32)
        XCTAssertTrue(long.chipTitle.hasSuffix("…"))
    }

    func testOpeningFromTheToolbarStartsWithGeneralContext() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.openAssistant()

        XCTAssertNil(context.viewModel.assistantContext)
    }

    // MARK: - Preference and privacy

    func testThePreferencePersists() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.setAssistantPreference(.popover)

        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).assistant.presentation,
            .popover
        )
    }

    func testTheSidebarIsTheDefaultPreference() {
        XCTAssertEqual(AssistantSettings.defaults.presentation, .sidebar)
    }

    func testThePrivacyLabelMatchesTheConfiguredService() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        XCTAssertEqual(context.viewModel.assistantPrivacyLabel, "On this Mac")
        XCTAssertTrue(context.viewModel.assistantStaysOnThisMac)

        var form = context.viewModel.settingsForm
        form.assistant = AssistantSettings.applying(.cloud, to: form.assistant)
        context.viewModel.applySettings(form)

        XCTAssertTrue(
            context.viewModel.assistantPrivacyLabel.hasPrefix("Cloud"),
            "A cloud service must never be labelled as staying on this Mac"
        )
        XCTAssertFalse(context.viewModel.assistantStaysOnThisMac)
    }

    func testLocalPointedAtARemoteHostIsNotLabelledPrivate() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.assistant.baseURL = "https://example.com/v1"
        context.viewModel.applySettings(form)

        XCTAssertFalse(context.viewModel.assistantStaysOnThisMac)
        XCTAssertTrue(context.viewModel.assistantPrivacyLabel.hasPrefix("Cloud"))
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
    }

    private func makeContext() throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = calendar.startOfDay(for: .now)
            let day = calendar.date(byAdding: .day, value: days, to: start) ?? start
            return calendar.date(bySettingHour: 12, minute: 0, second: 0, of: day) ?? day
        }

        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Example Quiz",
                    courseName: "99999 Example Interaction Course",
                    dueDate: due(inDays: 2),
                    source: .canvasCalendarFeed
                ),
                AssignmentSnapshot(
                    title: "Example Poster Submission",
                    courseName: "99999 Example Interaction Course",
                    dueDate: due(inDays: 9),
                    source: .canvasCalendarFeed
                ),
            ]
        )

        let suiteName = "AssistantPresentationTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: PresentationCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: AppearanceRecordingDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel, defaults: defaults)
    }
}

private actor PresentationCoordinatorStub: FeedRefreshCoordinating {
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

import CoreGraphics
import Foundation
import XCTest
@testable import CanvasCountdown

/// How the assistant decides where to appear, and what must survive that
/// decision changing. All fixtures are invented.
@MainActor
final class AssistantPresentationTests: XCTestCase {
    /// Comfortably above the sidebar threshold plus its hysteresis.
    private let wideWindow: CGFloat = 1_400
    /// Below the threshold: the list would be squeezed under its readable width.
    private let narrowWindow: CGFloat = 900

    // MARK: - Choosing a presentation

    func testWideWindowChoosesTheSidebar() {
        let presentation = AssistantLayout.presentation(
            preference: .automatic,
            availableWidth: AssistantLayout.availableWidth(forWindowWidth: wideWindow),
            current: .closed
        )

        XCTAssertEqual(presentation, .sidebar)
    }

    func testNarrowWindowChoosesThePopover() {
        let presentation = AssistantLayout.presentation(
            preference: .automatic,
            availableWidth: AssistantLayout.availableWidth(forWindowWidth: narrowWindow),
            current: .closed
        )

        XCTAssertEqual(
            presentation,
            .popover,
            "Squeezing the list below its readable width is not an option"
        )
    }

    func testTheMainContentKeepsItsMinimumWidth() {
        // The threshold is exactly the two minimums; nothing is taken from the
        // list to make the panel fit.
        XCTAssertEqual(
            AssistantLayout.minimumWidthForSidebar,
            AssistantLayout.mainContentMinimum + AssistantLayout.sidebarMinimum
        )
        XCTAssertGreaterThanOrEqual(AssistantLayout.mainContentMinimum, 650)
    }

    func testHysteresisStopsFlickeringAtTheThreshold() {
        let threshold = AssistantLayout.minimumWidthForSidebar

        // Sitting just above the bare minimum: a sidebar already open stays.
        XCTAssertTrue(
            AssistantLayout.fitsSidebar(availableWidth: threshold + 1, current: .sidebar)
        )
        // The same width does not pull a popover back into a sidebar, so a
        // drag resting near the line does not flip modes repeatedly.
        XCTAssertFalse(
            AssistantLayout.fitsSidebar(availableWidth: threshold + 1, current: .popover)
        )
        XCTAssertTrue(
            AssistantLayout.fitsSidebar(
                availableWidth: threshold + AssistantLayout.hysteresis,
                current: .popover
            )
        )
    }

    func testAnExplicitSidebarChoiceFallsBackRatherThanBreakingTheLayout() {
        let presentation = AssistantLayout.presentation(
            preference: .sidebar,
            availableWidth: AssistantLayout.availableWidth(forWindowWidth: 700),
            current: .closed
        )

        XCTAssertEqual(
            presentation,
            .popover,
            "A forced sidebar that cannot fit would corrupt the layout"
        )
    }

    func testAnExplicitPopoverChoiceIsHonouredOnAWideWindow() {
        let presentation = AssistantLayout.presentation(
            preference: .popover,
            availableWidth: AssistantLayout.availableWidth(forWindowWidth: wideWindow),
            current: .closed
        )

        XCTAssertEqual(presentation, .popover)
    }

    func testASeparateWindowIsNeverTakenAwayByResizing() {
        let presentation = AssistantLayout.presentation(
            preference: .automatic,
            availableWidth: 100,
            current: .separateWindow
        )

        XCTAssertEqual(presentation, .separateWindow)
    }

    // MARK: - Width bounds

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
        XCTAssertEqual(AssistantLayout.sidebarMinimum, 340)
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

        context.viewModel.updateAvailableWidth(wideWindow)
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
        context.viewModel.updateAvailableWidth(wideWindow)

        context.viewModel.toggleAssistant()

        XCTAssertNotEqual(context.viewModel.assistantPresentation, .separateWindow)
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
    }

    func testClosingReturnsTheWindowToTheAssignments() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.updateAvailableWidth(wideWindow)
        context.viewModel.openAssistant()

        context.viewModel.toggleAssistant()

        XCTAssertEqual(context.viewModel.assistantPresentation, .closed)
    }

    // MARK: - Nothing is lost when the container changes

    func testResizingKeepsTheConversationDraftAndContext() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        context.viewModel.updateAvailableWidth(wideWindow)

        let item = try XCTUnwrap(context.viewModel.assignments.first)
        context.viewModel.openAssistant(about: item)
        context.viewModel.assistantDraftInput = "half a question"
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)

        // Drag the window narrow: the presentation changes, nothing else does.
        context.viewModel.updateAvailableWidth(narrowWindow)
        XCTAssertEqual(context.viewModel.assistantPresentation, .popover)
        XCTAssertEqual(context.viewModel.assistantDraftInput, "half a question")
        XCTAssertEqual(context.viewModel.assistantContext?.id, item.id)

        // And back again.
        context.viewModel.updateAvailableWidth(wideWindow)
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
        XCTAssertEqual(context.viewModel.assistantDraftInput, "half a question")
        XCTAssertEqual(context.viewModel.assistantContext?.id, item.id)
    }

    func testResizingWhileClosedDoesNotOpenAnything() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.updateAvailableWidth(wideWindow)
        context.viewModel.updateAvailableWidth(narrowWindow)

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
        context.viewModel.updateAvailableWidth(wideWindow)

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

    func testAutomaticIsTheDefaultPreference() {
        XCTAssertEqual(AssistantSettings.defaults.presentation, .automatic)
    }

    func testThePrivacyLabelMatchesTheConfiguredService() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        XCTAssertEqual(context.viewModel.assistantPrivacyLabel, "On this Mac")
        XCTAssertTrue(context.viewModel.assistantStaysOnThisMac)

        var form = context.viewModel.settingsForm
        form.assistant = AssistantSettings.applying(.groq, to: form.assistant)
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

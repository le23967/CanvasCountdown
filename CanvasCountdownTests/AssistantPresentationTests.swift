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

    func testAColumnALittleShortOfBothFloorsIsHalved() {
        let available: CGFloat = 580
        let width = AssistantLayout.fittedSidebarWidth(
            preferred: AssistantLayout.sidebarIdeal,
            availableWidth: available
        )

        XCTAssertEqual(width, available / 2)
        XCTAssertGreaterThanOrEqual(width, AssistantLayout.paneMinimum)
    }

    /// Halving a column this narrow would hand each pane less than it can be
    /// laid out in. That is what put the assistant on top of the list: its row
    /// of chips cannot shrink, so it was drawn at its own width, centred on
    /// room that was too small for it.
    func testAColumnTooNarrowForTwoPanesGoesToThePanelWhole() {
        for available in [CGFloat(280), 362, 400, 559] {
            XCTAssertEqual(
                AssistantLayout.fittedSidebarWidth(
                    preferred: AssistantLayout.sidebarIdeal,
                    availableWidth: available
                ),
                available,
                "The panel is not hidden and not squeezed at \(available)"
            )
        }
    }

    // MARK: - Where the two panes go

    func testAWideColumnPutsThePanelBesideTheList() {
        let panes = AssistantLayout.panes(
            isPanelShown: true,
            preferred: 380,
            columnWidth: 1_000
        )

        XCTAssertFalse(panes.panelCoversList)
        XCTAssertEqual(panes.panelWidth, 381, "The panel and its divider")
        XCTAssertEqual(panes.listWidth, 619)
    }

    func testEveryWidthLeavesBothPanesWideEnoughToRead() {
        for column in stride(from: CGFloat(0), through: 1_800, by: 1) {
            let panes = AssistantLayout.panes(
                isPanelShown: true,
                preferred: AssistantLayout.sidebarMaximum,
                columnWidth: column
            )

            if panes.panelCoversList {
                XCTAssertEqual(panes.panelWidth, column)
                XCTAssertEqual(
                    panes.listWidth,
                    column,
                    "Underneath, the list keeps the whole column"
                )
                XCTAssertLessThan(column, AssistantLayout.minimumWidthToShare)
            } else {
                XCTAssertEqual(
                    panes.listWidth + panes.panelWidth,
                    column,
                    accuracy: 0.001,
                    "Nothing is asked of the window at \(column)"
                )
                // The divider is a point of the panel's share.
                XCTAssertGreaterThanOrEqual(
                    panes.listWidth,
                    AssistantLayout.paneMinimum - 1
                )
                XCTAssertGreaterThanOrEqual(
                    panes.panelWidth,
                    AssistantLayout.paneMinimum
                )
            }
        }
    }

    /// The three windows this was reported from, by the width of the column
    /// the two panes had to share in each.
    func testTheReportedWindowsAreLaidOutWithoutOverlap() {
        // 1041 wide with the navigation sidebar showing.
        let wide = AssistantLayout.panes(
            isPanelShown: true,
            preferred: nil,
            columnWidth: 824
        )
        XCTAssertFalse(wide.panelCoversList)
        XCTAssertGreaterThanOrEqual(wide.listWidth, AssistantLayout.mainContentFloor)

        // 583 wide with the navigation sidebar folded: a little short of both
        // floors, so halved.
        let folded = AssistantLayout.panes(
            isPanelShown: true,
            preferred: nil,
            columnWidth: 583
        )
        XCTAssertFalse(folded.panelCoversList)
        XCTAssertEqual(folded.listWidth, 583 - 292.5, accuracy: 0.001)

        // 579 wide with the navigation sidebar brought back by hand, which
        // left 362 for both. Each half got 181 and neither fitted in it.
        let squeezed = AssistantLayout.panes(
            isPanelShown: true,
            preferred: nil,
            columnWidth: 362
        )
        XCTAssertTrue(squeezed.panelCoversList)
        XCTAssertEqual(squeezed.panelWidth, 362)
    }

    func testAClosedPanelTakesNothing() {
        let panes = AssistantLayout.panes(
            isPanelShown: false,
            preferred: 380,
            columnWidth: 400
        )

        XCTAssertEqual(panes.panelWidth, 0)
        XCTAssertEqual(panes.listWidth, 400)
        XCTAssertFalse(panes.panelCoversList)
    }

    func testAnUnmeasuredColumnDoesNotProduceNonsense() {
        for width in [CGFloat(0), -40, .infinity, .nan] {
            let panes = AssistantLayout.panes(
                isPanelShown: true,
                preferred: 380,
                columnWidth: width
            )
            XCTAssertEqual(panes.panelWidth, 0)
            XCTAssertEqual(panes.listWidth, 0)
        }
    }

    /// The detached window may be made this narrow, so the panel has to be
    /// usable at it.
    func testThePaneFloorMatchesTheDetachedWindow() {
        XCTAssertEqual(AssistantLayout.paneMinimum, 280)
        XCTAssertEqual(
            AssistantLayout.minimumWidthToShare,
            560
        )
        XCTAssertLessThan(
            AssistantLayout.minimumWidthToShare,
            AssistantLayout.minimumWidthForBoth,
            "The navigation sidebar is folded before the list is ever covered"
        )
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
        context.viewModel.assistantComposerInput = "half a question"
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)

        // Out to a popover and back: nothing typed or said is lost.
        context.viewModel.showAssistantAsPopover()
        XCTAssertEqual(context.viewModel.assistantPresentation, .popover)
        XCTAssertEqual(context.viewModel.assistantComposerInput, "half a question")
        XCTAssertEqual(context.viewModel.assistantContext?.id, item.id)

        context.viewModel.showAssistantAsSidebar()
        XCTAssertEqual(context.viewModel.assistantPresentation, .sidebar)
        XCTAssertEqual(context.viewModel.assistantComposerInput, "half a question")
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

    /// Asked about something outside the list on screen — here, a course
    /// the filter is hiding — the request used to carry every upcoming item
    /// and leave out the one thing it was about.
    func testAskingAboutSomethingOutsideTheFilterSendsOnlyThat() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let item = try XCTUnwrap(context.viewModel.assignments.first)
        context.viewModel.selectCourse("A course with nothing in it")
        XCTAssertTrue(context.viewModel.visibleUpcomingAssignments.isEmpty)

        context.viewModel.openAssistant(about: item)

        XCTAssertEqual(
            context.viewModel.assistantDigests().map(\.title),
            [item.title],
            "Only the assignment that was asked about leaves this Mac"
        )
    }

    /// Whether something is done is never sent to the model, so asked about
    /// finished work all it could say is that it is late. Finished work is not
    /// offered as something to ask about, and a question already about it
    /// stops being about it once it is marked done.
    func testFinishedWorkIsNotWhatAQuestionIsAbout() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let item = try XCTUnwrap(context.viewModel.assignments.first)

        context.viewModel.openAssistant(about: item)
        XCTAssertEqual(context.viewModel.assistantContext?.id, item.id)

        context.viewModel.toggleCompleted(item)
        for _ in 0..<200 where context.viewModel.assistantContext != nil {
            try await Task.sleep(for: .milliseconds(5))
        }
        XCTAssertNil(context.viewModel.assistantContext)
        XCTAssertFalse(
            context.viewModel.assistantDigests().contains { $0.title == item.title },
            "Finished work is not sent as though it were still due"
        )

        let finished = try XCTUnwrap(
            context.viewModel.assignments.first { $0.id == item.id }
        )
        context.viewModel.openAssistant(about: finished)
        XCTAssertNil(context.viewModel.assistantContext)
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

    /// It used to add the provider's own title, which read "Cloud · AI (Cloud)"
    /// for every hosted service there is.
    func testThePrivacyLabelNamesTheServiceWhenItKnowsIt() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        var form = context.viewModel.settingsForm
        form.assistant.provider = .cloud
        form.assistant.baseURL = AssistantService.groq.baseURL
        context.viewModel.applySettings(form)
        XCTAssertEqual(context.viewModel.assistantPrivacyLabel, "Cloud · Groq")
        XCTAssertEqual(
            context.viewModel.assistantPrivacyDescription,
            "What you type here, and the titles, courses and due dates of your assignments, are sent to api.groq.com."
        )

        form.assistant.baseURL = "https://models.example.com/v1"
        context.viewModel.applySettings(form)
        XCTAssertEqual(context.viewModel.assistantPrivacyLabel, "Cloud")
        XCTAssertFalse(
            context.viewModel.assistantPrivacyLabel.contains("AI (Cloud)")
        )

        form.assistant = AssistantSettings.applying(.local, to: form.assistant)
        context.viewModel.applySettings(form)
        XCTAssertEqual(
            context.viewModel.assistantPrivacyDescription,
            "The assistant runs on this Mac. Nothing is uploaded."
        )
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

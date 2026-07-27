import Foundation
import Observation

@MainActor
@Observable
final class MainViewModel {
    var sidebarSelection: SidebarDestination? = .upcoming {
        didSet {
            guard sidebarSelection != oldValue else {
                return
            }
            // Moving to another section leaves search behind rather than
            // carrying a stale query and a focused field into it.
            dismissSearch()
        }
    }
    var assignments: [AssignmentListItem] = []
    var searchText = ""
    /// The one switch the toolbar reads. Search mode replaces the ordinary
    /// actions with a field and a Cancel button; it is never inferred from
    /// whether the query happens to be empty.
    var isSearchModeActive = false
    /// Mirrors the field's `FocusState`, so focus is testable and can be
    /// released from anywhere without the view owning the decision.
    var isSearchFieldFocused = false
    var selectedCourse: String?
    var showCompletedAndIgnored = false
    /// The month the calendar is showing.
    var calendarMonth = Date.now
    var selectedCalendarDay: Date?
    var currentDate = Date.now
    var settingsForm: SettingsFormState
    var notificationPermission: NotificationPermissionState = .notDetermined

    var isLoading = false
    var isRefreshing = false
    var isShowingFeedImport = false
    var feedImportIsOnboarding = false
    var feedImportInitialURL = ""
    var isShowingManualEditor = false
    var isShowingScreenshotImport = false
    var isShowingImportedDetails = false
    var importedEventDetails: AssignmentListItem?
    var manualEventDraft = ManualEventDraft()
    /// Row the list has focus on, used for keyboard actions.
    var selectedEventID: UUID?
    var errorMessage: String?
    var statusMessage: String?
    var lastRefreshDate: Date?

    @ObservationIgnored
    private let repository: any AssignmentRepository
    @ObservationIgnored
    private let refreshCoordinator: any FeedRefreshCoordinating
    @ObservationIgnored
    private let feedURLStore: any FeedURLStoring
    @ObservationIgnored
    private let settingsStore: SettingsStore
    @ObservationIgnored
    private let dockRenderer: any DockRendering
    @ObservationIgnored
    private let notificationScheduler: any NotificationScheduling
    @ObservationIgnored
    private let calendar: Calendar
    /// Disabled for isolated (automated) launches so a test host never starts a
    /// refresh loop, a countdown timer, or a launch-time feed download.
    @ObservationIgnored
    private let automaticActivityEnabled: Bool

    @ObservationIgnored
    private var parsedPreviewEvents: [String: ParsedCalendarEvent] = [:]
    @ObservationIgnored
    private var storedFeedURLString = ""
    @ObservationIgnored
    private var automaticRefreshTask: Task<Void, Never>?
    @ObservationIgnored
    private var countdownTransitionTask: Task<Void, Never>?
    @ObservationIgnored
    private var notificationDebounceTask: Task<Void, Never>?
    @ObservationIgnored
    private var transientStatusTask: Task<Void, Never>?
    @ObservationIgnored
    private let assistantKeyStore: KeychainAssistantKeyStore
    @ObservationIgnored
    private let localModels: any LocalModelManaging
    @ObservationIgnored
    private var hasStarted = false

    init(
        repository: any AssignmentRepository,
        refreshCoordinator: any FeedRefreshCoordinating,
        feedURLStore: any FeedURLStoring,
        settingsStore: SettingsStore,
        dockRenderer: any DockRendering,
        notificationScheduler: any NotificationScheduling,
        calendar: Calendar = .autoupdatingCurrent,
        automaticActivityEnabled: Bool = true,
        assistantKeyStore: KeychainAssistantKeyStore = KeychainAssistantKeyStore(),
        localModels: any LocalModelManaging = OllamaModelManager()
    ) {
        self.localModels = localModels
        self.assistantKeyStore = assistantKeyStore
        self.automaticActivityEnabled = automaticActivityEnabled
        self.repository = repository
        self.refreshCoordinator = refreshCoordinator
        self.feedURLStore = feedURLStore
        self.settingsStore = settingsStore
        self.dockRenderer = dockRenderer
        self.notificationScheduler = notificationScheduler
        self.calendar = calendar
        self.settingsForm = SettingsFormState(
            settings: settingsStore.snapshot,
            feedURL: nil
        )
    }

    deinit {
        automaticRefreshTask?.cancel()
        countdownTransitionTask?.cancel()
        notificationDebounceTask?.cancel()
        transientStatusTask?.cancel()
    }

    var hasConfiguredFeed: Bool {
        !storedFeedURLString.isEmpty
    }

    var availableCourses: [String] {
        Array(Set(assignments.compactMap(\.normalizedCourseName)))
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    var upcomingCount: Int {
        visibleUpcomingAssignments.filter { item in
            item.dueDate >= currentDate
                && !item.isCompleted
                && !item.isIgnored
        }.count
    }

    var nearestAssignment: AssignmentListItem? {
        CountdownCalculator.nearestUpcoming(
            from: visibleUpcomingAssignments,
            now: currentDate,
            calendar: calendar
        )?.event
    }

    /// What Upcoming is actually showing: the persistent course scope from
    /// Settings, narrowed further by the toolbar filter.
    ///
    /// The Dock deliberately does not follow the toolbar filter. That filter is
    /// a transient way to look through the list and is not persisted, so having
    /// the Dock jump around while browsing, then revert on the next launch,
    /// would be surprising. The Dock follows the saved Settings scope.
    var visibleUpcomingAssignments: [AssignmentListItem] {
        courseScopedAssignments.filter(matchesSelectedCourse)
    }

    private func matchesSelectedCourse(_ item: AssignmentListItem) -> Bool {
        guard let selectedCourse else {
            return true
        }
        return item.normalizedCourseName == selectedCourse
    }

    /// Nil selects every course.
    func selectCourse(_ course: String?) {
        selectedCourse = course
    }

    // MARK: - Assistant

    /// One value, so the assistant cannot be in two places at once and every
    /// close path clears the same thing.
    private(set) var assistantPresentation: ActiveAssistantPresentation = .closed
    /// The assignment a question is about, if any.
    private(set) var assistantContext: AssistantContext?
    /// Kept here rather than in the panel, so moving between a sidebar, a
    /// popover and a window never resets what was typed or said.
    var assistantDraftInput = ""
    @ObservationIgnored
    private var availableAssistantWidth: CGFloat = 0
    private(set) var assistantSummary: String?
    private(set) var assistantDrafts: [AssistantDraftTask] = []
    private(set) var isAssistantBusy = false
    private(set) var assistantErrorMessage: String?

    /// Read back so the field can show what is stored without the key ever
    /// touching preferences or diagnostics.
    private(set) var assistantAPIKey = ""

    private(set) var isOllamaReachable = false
    private(set) var installedModels: [LocalModel] = []
    private(set) var pullingModel: String?
    private(set) var pullProgress: Double = 0
    private(set) var modelMessage: String?

    @ObservationIgnored
    private var pullTask: Task<Void, Never>?

    /// Refreshes what is installed, and whether Ollama is answering at all.
    func refreshLocalModels() async {
        guard let url = settingsStore.assistant.resolvedURL else {
            isOllamaReachable = false
            return
        }
        isOllamaReachable = await localModels.isReachable(baseURL: url)
        guard isOllamaReachable else {
            installedModels = []
            return
        }
        installedModels = (try? await localModels.installedModels(baseURL: url)) ?? []
    }

    func downloadModel(_ name: String) {
        guard let url = settingsStore.assistant.resolvedURL else {
            return
        }
        pullTask?.cancel()
        pullingModel = name
        pullProgress = 0
        modelMessage = nil

        let manager = localModels
        // Captured once, outside the task, so the progress callback does not
        // reach back into a captured optional from concurrent code.
        let onProgress: @Sendable (Double) -> Void = { [weak self] fraction in
            Task { @MainActor in
                self?.pullProgress = fraction
            }
        }

        pullTask = Task { [weak self] in
            do {
                try await manager.pull(name, baseURL: url, progress: onProgress)
                await self?.finishDownload(message: "\(name) is ready to use.")
            } catch {
                await self?.finishDownload(message: error.localizedDescription)
            }
        }
    }

    private func finishDownload(message: String) async {
        pullingModel = nil
        await refreshLocalModels()
        modelMessage = message
    }

    func cancelModelDownload() {
        pullTask?.cancel()
        pullTask = nil
        pullingModel = nil
        modelMessage = "Download cancelled."
    }

    func deleteModel(_ name: String) {
        guard let url = settingsStore.assistant.resolvedURL else {
            return
        }
        Task {
            do {
                try await localModels.delete(name, baseURL: url)
                await refreshLocalModels()
                modelMessage = "\(name) was removed."
            } catch {
                modelMessage = error.localizedDescription
            }
        }
    }

    func useModel(_ name: String) {
        settingsForm.assistant.model = name
        applySettings(settingsForm)
    }

    var hasSaveableDrafts: Bool {
        assistantDrafts.contains { $0.include && $0.canBeSaved }
    }

    private func makeAssistant() -> any AssistantServicing {
        ChatCompletionsAssistantService(
            settings: settingsStore.assistant,
            apiKey: assistantAPIKey.isEmpty ? nil : assistantAPIKey,
            calendar: calendar
        )
    }

    private(set) var conversation: [AssistantMessage] = []

    var assistantPreference: AssistantPresentationPreference {
        settingsStore.assistant.presentation
    }

    var isAssistantOpen: Bool {
        assistantPresentation.isOpen
    }

    var sidebarWidth: CGFloat {
        AssistantLayout.clampedSidebarWidth(settingsStore.assistant.sidebarWidth)
    }

    /// Accurate by construction: it reads the endpoint rather than the provider
    /// name, so pointing "local" at a remote host cannot produce a false claim.
    var assistantPrivacyLabel: String {
        settingsStore.assistant.staysOnThisMac
            ? "On this Mac"
            : "Cloud · \(settingsStore.assistant.provider.title)"
    }

    var assistantStaysOnThisMac: Bool {
        settingsStore.assistant.staysOnThisMac
    }

    /// Called as the window resizes. Only the choice between sidebar and
    /// popover changes; the conversation is untouched.
    func updateAvailableWidth(_ windowWidth: CGFloat) {
        availableAssistantWidth = AssistantLayout.availableWidth(
            forWindowWidth: windowWidth
        )
        guard assistantPresentation.isOpen else {
            return
        }
        let next = AssistantLayout.presentation(
            preference: assistantPreference,
            availableWidth: availableAssistantWidth,
            current: assistantPresentation
        )
        guard next != assistantPresentation else {
            return
        }
        move(to: next)
    }

    /// The single writer for the presentation.
    ///
    /// The sidebar and the popover are two presentation modifiers attached at
    /// the same time, each bound to this one value, so the value being a single
    /// case is what keeps them from both appearing. Every path that changes
    /// where the assistant lives goes through here rather than assigning
    /// directly.
    private func move(to presentation: ActiveAssistantPresentation) {
        guard presentation != assistantPresentation else {
            return
        }
        assistantPresentation = presentation
    }

    func toggleAssistant() {
        if assistantPresentation.isOpen {
            closeAssistant()
        } else {
            openAssistant()
        }
    }

    func openAssistant(about item: AssignmentListItem? = nil) {
        if let item {
            assistantContext = AssistantContext(item)
        }
        move(
            to: AssistantLayout.presentation(
                preference: assistantPreference,
                availableWidth: availableAssistantWidth,
                current: assistantPresentation
            )
        )
    }

    func closeAssistant() {
        assistantPresentation = .closed
    }

    /// Explicit only: a detached window is never chosen for the user.
    func openAssistantInSeparateWindow() {
        move(to: .separateWindow)
    }

    func showAssistantAsPopover() {
        move(to: .popover)
    }

    func showAssistantAsSidebar() {
        move(to: .sidebar)
    }

    func setAssistantPreference(_ preference: AssistantPresentationPreference) {
        settingsForm.assistant.presentation = preference
        applySettings(settingsForm)
        if assistantPresentation.isOpen, assistantPresentation != .separateWindow {
            move(
                to: AssistantLayout.presentation(
                    preference: preference,
                    availableWidth: availableAssistantWidth,
                    current: assistantPresentation
                )
            )
        }
    }

    /// Stored only when usable, so a bad drag cannot leave an unopenable panel.
    func rememberSidebarWidth(_ width: CGFloat) {
        guard AssistantLayout.isValidSidebarWidth(width) else {
            return
        }
        settingsForm.assistant.sidebarWidth = width
        applySettings(settingsForm)
    }

    func clearAssistantContext() {
        assistantContext = nil
    }

    /// Common openers, so a blank box is not the first thing anyone meets.
    static let assistantSuggestions = [
        "What is most urgent?",
        "How many are due this week?",
        "What should I start today?",
    ]

    func ask(_ question: String) async {
        let trimmed = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return
        }
        let history = conversation
        conversation.append(AssistantMessage(role: .user, text: trimmed))
        isAssistantBusy = true
        assistantErrorMessage = nil
        defer { isAssistantBusy = false }

        do {
            let reply = try await makeAssistant().answer(
                trimmed,
                history: history,
                digests: assistantDigests(),
                now: .now
            )
            conversation.append(
                AssistantMessage(
                    role: .assistant,
                    text: reply.trimmingCharacters(in: .whitespacesAndNewlines)
                )
            )
        } catch {
            assistantErrorMessage = error.localizedDescription
        }
    }

    func clearConversation() {
        conversation = []
        assistantErrorMessage = nil
    }

    func summariseWorkload() async {
        isAssistantBusy = true
        assistantErrorMessage = nil
        defer { isAssistantBusy = false }
        do {
            assistantSummary = try await makeAssistant().summarise(
                assistantDigests(),
                now: .now
            )
        } catch {
            assistantSummary = nil
            assistantErrorMessage = error.localizedDescription
        }
    }

    func draftTask(from text: String) async {
        isAssistantBusy = true
        assistantErrorMessage = nil
        defer { isAssistantBusy = false }
        do {
            let drafts = try await makeAssistant().draftTasks(from: text, now: .now)
            if drafts.isEmpty {
                assistantErrorMessage =
                    "Nothing could be read from that. Try naming the task and when it is due."
            }
            assistantDrafts = drafts
        } catch {
            assistantErrorMessage = error.localizedDescription
        }
    }

    func updateAssistantDraft(_ draft: AssistantDraftTask) {
        guard let index = assistantDrafts.firstIndex(where: { $0.id == draft.id }) else {
            return
        }
        assistantDrafts[index] = draft
    }

    func clearAssistantDrafts() {
        assistantDrafts = []
    }

    /// Saves only the drafts the user kept, through the ordinary manual-event
    /// path, so nothing the model produced bypasses validation.
    func saveAssistantDrafts(_ drafts: [AssistantDraftTask]) async {
        var saved = 0
        for draft in drafts where draft.include && draft.canBeSaved {
            guard let dueDate = draft.dueDate else {
                continue
            }
            do {
                try await saveManualEvent(
                    ManualEventDraft(
                        title: draft.title,
                        courseName: draft.courseName ?? "",
                        dueDate: dueDate
                    )
                )
                saved += 1
            } catch {
                present(error)
            }
        }
        assistantDrafts = []
        if saved > 0 {
            showTransientStatus(saved == 1 ? "1 task added" : "\(saved) tasks added")
        }
    }

    func loadAssistantKey() async {
        assistantAPIKey = (try? await assistantKeyStore.load()) ?? ""
    }

    func saveAssistantKey(_ key: String) {
        Task {
            do {
                try await assistantKeyStore.save(key)
                assistantAPIKey = key.trimmingCharacters(in: .whitespacesAndNewlines)
                showTransientStatus("Assistant key saved")
            } catch {
                present(error)
            }
        }
    }

    /// Only the three agreed fields, and only for what is currently in scope.
    ///
    /// With an assignment in context the list narrows to that one item: asking
    /// about a single deadline is not a reason to send the whole term.
    func assistantDigests() -> [AssistantAssignmentDigest] {
        let scoped = visibleUpcomingAssignments
            .filter { !$0.isCompleted && !$0.isIgnored && $0.dueDate >= currentDate }
        if let assistantContext,
           let focused = scoped.first(where: { $0.id == assistantContext.id }) {
            return [AssistantAssignmentDigest(focused)]
        }
        return scoped.map(AssistantAssignmentDigest.init)
    }

    var isAssistantEnabled: Bool {
        settingsStore.assistant.isEnabled
    }

    var assistantSettings: AssistantSettings {
        settingsStore.assistant
    }

    // MARK: - Calendar

    /// The month grid, built from exactly the events the list would show.
    var calendarDays: [CalendarDay] {
        AssignmentCalendar.month(
            containing: calendarMonth,
            events: filteredAssignments,
            calendar: calendar,
            now: currentDate
        )
    }

    var calendarMonthTitle: String {
        AssignmentCalendar.monthTitle(for: calendarMonth, calendar: calendar)
    }

    /// Events on the selected day, or an empty list when no day is chosen.
    var selectedDayItems: [AssignmentListItem] {
        guard let selectedCalendarDay else {
            return []
        }
        return filteredAssignments
            .filter { calendar.isDate($0.dueDate, inSameDayAs: selectedCalendarDay) }
            .sorted { $0.dueDate < $1.dueDate }
    }

    func showCalendarMonth(offsetBy months: Int) {
        calendarMonth = AssignmentCalendar.month(
            byAdding: months,
            to: calendarMonth,
            calendar: calendar
        )
        selectedCalendarDay = nil
    }

    func showCurrentMonth() {
        calendarMonth = .now
        selectedCalendarDay = nil
    }

    func selectCalendarDay(_ day: Date?) {
        selectedCalendarDay = day
    }

    // MARK: - Search

    func presentSearch() {
        isSearchModeActive = true
        isSearchFieldFocused = true
    }

    func toggleSearch() {
        if isSearchModeActive {
            dismissSearch()
        } else {
            presentSearch()
        }
    }

    /// Releases keyboard focus but leaves the field and its query in place.
    /// Used when a click lands outside the field, or another toolbar control is
    /// pressed, so the click still reaches its target.
    func dismissSearchFocus() {
        isSearchFieldFocused = false
    }

    /// Leaves search mode, releases focus and clears the query so the full list
    /// comes back. Escape, Cancel, a click on empty content and a section change
    /// all land here, so leaving search always means the same thing.
    func dismissSearch() {
        isSearchFieldFocused = false
        isSearchModeActive = false
        if !searchText.isEmpty {
            searchText = ""
        }
    }

    func handleSearchEscape() {
        dismissSearch()
    }

    /// What the toolbar builds. The two are mutually exclusive: the ordinary
    /// actions are not constructed at all in search mode, so none of them is
    /// left invisible but still able to take a click.
    var showsOrdinaryToolbarActions: Bool {
        !isSearchModeActive
    }

    var showsToolbarSearchField: Bool {
        isSearchModeActive
    }

    /// Six compact actions normally, or the field plus Cancel while searching.
    /// Keeping the search layout down to two items is what stops the field
    /// being pushed into the toolbar's overflow menu.
    var toolbarItemCount: Int {
        isSearchModeActive ? 2 : 7
    }

    func clearSearchQuery() {
        searchText = ""
    }

    /// Called before a toolbar action runs, so the field gives up focus and the
    /// action still happens on the same click.
    func prepareForToolbarAction() {
        dismissSearchFocus()
    }

    /// Kept short so a long Canvas course title cannot stretch the toolbar.
    var courseFilterButtonTitle: String {
        guard let selectedCourse else {
            return "All Courses"
        }
        return Self.menuTitle(for: selectedCourse, maximumLength: 18)
    }

    var courseFilterDescription: String {
        guard let selectedCourse else {
            return "Filter by course, showing all courses"
        }
        return "Filter by course, showing \(selectedCourse)"
    }

    /// Truncates a long course title for display. The full title stays
    /// available through the tooltip and accessibility label.
    static func menuTitle(
        for course: String?,
        maximumLength: Int = 44
    ) -> String {
        guard let course else {
            return "All Courses"
        }
        guard course.count > maximumLength else {
            return course
        }
        return course.prefix(maximumLength - 1)
            .trimmingCharacters(in: .whitespacesAndNewlines) + "…"
    }

    /// Assignments in scope for Upcoming, the nearest-assignment card and the
    /// Dock countdown. All Events and Completed deliberately ignore this: the
    /// course selection is a focus filter, never a deletion.
    var courseScopedAssignments: [AssignmentListItem] {
        assignments.filter(isInSelectedCourseScope)
    }

    func isInSelectedCourseScope(_ item: AssignmentListItem) -> Bool {
        guard settingsStore.dockCountMode == .selectedCourses else {
            return true
        }
        guard let courseName = item.normalizedCourseName else {
            // Manual entries and imported events without a course are never
            // silently hidden by a course selection.
            return true
        }
        return settingsStore.selectedCourses.contains(courseName)
    }

    var filteredAssignments: [AssignmentListItem] {
        let destination = sidebarSelection ?? .upcoming
        guard destination != .settings else {
            return []
        }

        let now = currentDate
        return assignments
            .filter { item in
                switch destination {
                case .upcoming:
                    guard item.dueDate >= now,
                          isInSelectedCourseScope(item) else {
                        return false
                    }
                    return showCompletedAndIgnored
                        || (!item.isCompleted && !item.isIgnored)
                case .allEvents, .calendar:
                    // The calendar shows the whole library. Restricting it to
                    // what is still ahead would leave every past week blank,
                    // which is not what a month grid is for.
                    return showCompletedAndIgnored
                        || (!item.isCompleted && !item.isIgnored)
                case .completed:
                    return item.isCompleted
                case .settings:
                    return false
                }
            }
            .filter { item in
                guard let selectedCourse else {
                    return true
                }
                return item.normalizedCourseName == selectedCourse
            }
            .filter { item in
                let query = searchText.trimmingCharacters(
                    in: .whitespacesAndNewlines
                )
                guard !query.isEmpty else {
                    return true
                }
                return item.title.localizedCaseInsensitiveContains(query)
                    || (item.normalizedCourseName?
                        .localizedCaseInsensitiveContains(query) ?? false)
            }
            .sorted {
                if $0.dueDate != $1.dueDate {
                    return $0.dueDate < $1.dueDate
                }
                return $0.title.localizedStandardCompare($1.title)
                    == .orderedAscending
            }
    }

    func start() async {
        guard !hasStarted else {
            return
        }
        hasStarted = true
        isLoading = true
        errorMessage = nil

        do {
            async let loadedAssignments = repository.fetchAll()
            async let loadedFeedURL = feedURLStore.loadFeedURL()
            async let loadedPermission = notificationScheduler
                .authorizationStatus()

            let (snapshots, feedURL, permission) = try await (
                loadedAssignments,
                loadedFeedURL,
                loadedPermission
            )
            apply(snapshots)
            storedFeedURLString = feedURL?.absoluteString ?? ""
            settingsForm = SettingsFormState(
                settings: settingsStore.snapshot,
                feedURL: feedURL
            )
            notificationPermission = NotificationPermissionState(permission)
            dockRenderer.apply(appearance: settingsStore.dockAppearance)
            await loadAssistantKey()
            synchronizeDock()

            if automaticActivityEnabled, feedURL == nil, assignments.isEmpty {
                presentFeedImport(onboarding: true)
            }
        } catch {
            present(error)
        }
        isLoading = false

        guard automaticActivityEnabled else {
            return
        }

        startAutomaticRefreshLoop()
        startCountdownTransitionLoop()

        if hasConfiguredFeed {
            await refresh(trigger: .launch, announcesResult: false)
        } else {
            await synchronizeNotifications()
        }
    }

    func refreshManually() {
        Task {
            await refresh(trigger: .manual, announcesResult: true)
        }
    }

    func presentFeedImport(
        initialURL: String? = nil,
        onboarding: Bool = false
    ) {
        feedImportInitialURL = initialURL ?? settingsForm.feedURL
        feedImportIsOnboarding = onboarding
        parsedPreviewEvents.removeAll()
        isShowingFeedImport = true
    }

    /// Opens the screenshot import sheet. A secondary method: the Canvas
    /// Calendar Feed stays the recommended way to keep deadlines current.
    func presentScreenshotImport() {
        prepareForToolbarAction()
        isShowingScreenshotImport = true
    }

    /// Called once a screenshot import commits, so the list, the card, the
    /// sidebar count, the Dock and reminders all catch up at once.
    func screenshotImportDidFinish() async {
        do {
            try await reloadAssignmentsAndSynchronize()
            statusMessage = "Screenshot import complete"
        } catch {
            present(error)
        }
    }

    /// Snapshot used for duplicate checking during screenshot review.
    func currentSnapshots() async throws -> [AssignmentSnapshot] {
        try await repository.fetchAll()
    }

    func presentNewManualEvent() {
        manualEventDraft = ManualEventDraft()
        isShowingManualEditor = true
    }

    /// Opens the right surface for a row: full editing for a manual event, and
    /// a details sheet limited to local state for anything Canvas owns.
    func presentEditor(for item: AssignmentListItem) {
        if item.isManual {
            manualEventDraft = ManualEventDraft(item: item)
            isShowingManualEditor = true
        } else {
            importedEventDetails = item
            isShowingImportedDetails = true
        }
    }

    /// The imported event currently open in the details sheet, refreshed from
    /// the latest snapshot so its toggles reflect reality.
    var currentImportedEventDetails: AssignmentListItem? {
        guard let importedEventDetails else {
            return nil
        }
        return assignments.first { $0.id == importedEventDetails.id }
            ?? importedEventDetails
    }

    func saveManualEvent(_ draft: ManualEventDraft) async throws {
        _ = try await repository.saveManual(
            ManualAssignmentDraft(presentation: draft)
        )
        try await reloadAssignmentsAndSynchronize()
        statusMessage = draft.eventID == nil ? "Event added" : "Event updated"
    }

    func toggleCompleted(_ item: AssignmentListItem) {
        Task {
            do {
                try await repository.updateStatus(
                    id: item.id,
                    isCompleted: !item.isCompleted,
                    isIgnored: nil
                )
                try await reloadAssignmentsAndSynchronize()
            } catch {
                present(error)
            }
        }
    }

    func toggleIgnored(_ item: AssignmentListItem) {
        Task {
            do {
                try await repository.updateStatus(
                    id: item.id,
                    isCompleted: nil,
                    isIgnored: !item.isIgnored
                )
                try await reloadAssignmentsAndSynchronize()
            } catch {
                present(error)
            }
        }
    }

    /// Keyboard Return on the focused row.
    func openSelectedEvent() {
        guard let item = selectedItem else {
            return
        }
        presentEditor(for: item)
    }

    /// Keyboard Delete on the focused row. Only manual events can be deleted;
    /// imported ones are Canvas's to remove.
    func deletionCandidate() -> AssignmentListItem? {
        guard let item = selectedItem, item.isManual else {
            return nil
        }
        return item
    }

    var selectedItem: AssignmentListItem? {
        guard let selectedEventID else {
            return nil
        }
        return assignments.first { $0.id == selectedEventID }
    }

    func deleteManualEvent(_ item: AssignmentListItem) {
        guard item.isManual else {
            return
        }
        Task {
            do {
                try await repository.delete(id: item.id)
                try await reloadAssignmentsAndSynchronize()
                statusMessage = "Manual event deleted"
            } catch {
                present(error)
            }
        }
    }

    func previewFeed(_ feedURL: URL) async throws -> [ImportPreviewItem] {
        let preview = try await refreshCoordinator.preview(
            feedURL: feedURL,
            now: .now
        )
        parsedPreviewEvents.removeAll(keepingCapacity: true)

        return preview.events.enumerated().map { index, event in
            let id = Self.previewIdentifier(event: event, index: index)
            parsedPreviewEvents[id] = event
            let metadata = Self.assignmentMetadata(from: event.summary)
            return ImportPreviewItem(
                id: id,
                title: metadata.title,
                courseName: metadata.courseName,
                dueDate: event.startDate,
                details: event.description
            )
        }
    }

    func importFeed(
        _ feedURL: URL,
        selectedIDs: Set<String>
    ) async throws -> ImportSummary {
        let selectedEvents = selectedIDs.compactMap { parsedPreviewEvents[$0] }
        let result = try await refreshCoordinator.importSelected(
            selectedEvents,
            from: feedURL,
            at: .now
        )

        storedFeedURLString = feedURL.absoluteString
        settingsForm.feedURL = feedURL.absoluteString
        try await reloadAssignmentsAndSynchronize()
        startAutomaticRefreshLoop()
        statusMessage = ImportSummary(result.importResult).message
        return ImportSummary(result.importResult)
    }

    /// Applies a settings change the moment the user makes it.
    ///
    /// Persistence and anything cheap happen immediately. Notification
    /// rescheduling is debounced, because dragging through a picker or editing a
    /// reminder would otherwise rebuild every pending request on each keystroke.
    func applySettings(_ form: SettingsFormState) {
        // The feed URL is Keychain-managed and only ever changes through the
        // preview and import flow, so the form's copy is never written back.
        if form.launchAtLogin != LaunchAtLoginController.isEnabled {
            do {
                try LaunchAtLoginController.setEnabled(form.launchAtLogin)
            } catch {
                present(error)
                settingsForm.launchAtLogin = LaunchAtLoginController.isEnabled
                return
            }
        }

        settingsStore.refreshInterval = form.refreshInterval.modelValue
        settingsStore.dockDisplayLanguage = form.dockLabel.modelValue
        settingsStore.dockCountMode = form.dockCourseScope.modelValue
        settingsStore.selectedCourses = form.selectedCourses
        settingsStore.launchAtLogin = LaunchAtLoginController.isEnabled

        let remindersChanged =
            settingsStore.reminderSchedule != form.reminderSchedule
        settingsStore.reminderSchedule = form.reminderSchedule

        settingsStore.assistant = form.assistant

        // Colours the user cannot read are corrected rather than stored.
        let appearance = form.dockAppearance.correctedForContrast()
        if settingsStore.dockAppearance != appearance {
            settingsStore.dockAppearance = appearance
            settingsForm.dockAppearance = appearance
            dockRenderer.apply(appearance: appearance)
        }

        synchronizeDock()
        startAutomaticRefreshLoop()
        startCountdownTransitionLoop()

        if remindersChanged {
            scheduleDebouncedNotificationUpdate()
        } else {
            showTransientStatus("Settings updated")
        }
    }

    // MARK: - Dock appearance

    func applyDockTheme(_ preset: DockThemePreset) {
        settingsForm.dockAppearance = DockAppearance.applying(
            preset,
            to: settingsForm.dockAppearance
        )
        applySettings(settingsForm)
    }

    /// Reverts the appearance only. Saved presets are the user's own work and
    /// survive this, so "Reset to Defaults" is never a way to lose them.
    func resetDockAppearance() {
        settingsForm.dockAppearance = .defaults
        applySettings(settingsForm)
    }

    // MARK: - Saved Dock themes

    var dockThemePresets: [UserDockThemePreset] {
        settingsStore.dockThemePresets.presets
    }

    var dockThemeStatus: DockThemeStatus {
        settingsStore.dockThemePresets.status(for: settingsForm.dockAppearance)
    }

    func applyDockTheme(_ preset: UserDockThemePreset) {
        settingsForm.dockAppearance = DockAppearance.applying(
            preset,
            to: settingsForm.dockAppearance
        )
        applySettings(settingsForm)
    }

    func validateDockPresetName(
        _ name: String,
        excluding excludedID: UUID? = nil
    ) -> DockPresetNameError? {
        switch DockThemePresetLibrary.validate(
            name: name,
            in: settingsStore.dockThemePresets,
            excluding: excludedID
        ) {
        case .success:
            nil
        case .failure(let error):
            error
        }
    }

    /// Saves the colours currently in force under a new name and selects it.
    ///
    /// The contrast correction runs first, so a preset can never be saved in a
    /// state the app would refuse to draw.
    @discardableResult
    func saveDockThemePreset(named name: String) -> DockPresetNameError? {
        if let error = validateDockPresetName(name) {
            return error
        }
        let corrected = settingsForm.dockAppearance.correctedForContrast()
        var library = settingsStore.dockThemePresets
        guard let id = library.add(name: name, colours: corrected.colours) else {
            return .duplicate
        }
        settingsStore.dockThemePresets = library
        guard let saved = library.preset(withID: id) else {
            return nil
        }
        settingsForm.dockAppearance = DockAppearance.applying(
            saved,
            to: corrected
        )
        applySettings(settingsForm)
        showTransientStatus("Saved “\(saved.name)”")
        return nil
    }

    /// Writes the current colours back over the preset they were edited from.
    func updateSelectedDockThemePreset() {
        guard let preset = dockThemeStatus.editedPreset else {
            return
        }
        let corrected = settingsForm.dockAppearance.correctedForContrast()
        var library = settingsStore.dockThemePresets
        guard library.update(preset.id, colours: corrected.colours) else {
            return
        }
        settingsStore.dockThemePresets = library
        settingsForm.dockAppearance = corrected
        settingsForm.dockAppearance.userPresetID = preset.id
        applySettings(settingsForm)
        showTransientStatus("Updated “\(preset.name)”")
    }

    /// Throws away edits and goes back to the preset as saved.
    func revertToSelectedDockThemePreset() {
        guard let preset = dockThemeStatus.editedPreset else {
            return
        }
        applyDockTheme(preset)
    }

    func renameDockThemePreset(_ id: UUID, to name: String) -> DockPresetNameError? {
        if let error = validateDockPresetName(name, excluding: id) {
            return error
        }
        var library = settingsStore.dockThemePresets
        guard library.rename(id, to: name) else {
            return .duplicate
        }
        settingsStore.dockThemePresets = library
        return nil
    }

    func duplicateDockThemePreset(_ id: UUID) {
        var library = settingsStore.dockThemePresets
        guard let copy = library.duplicate(id) else {
            return
        }
        settingsStore.dockThemePresets = library
        showTransientStatus(
            "Duplicated as “\(library.preset(withID: copy)?.name ?? "")”"
        )
    }

    /// Deleting the preset in use leaves its colours alone; only the name goes.
    func deleteDockThemePreset(_ id: UUID) {
        var library = settingsStore.dockThemePresets
        let name = library.preset(withID: id)?.name
        library.remove(id)
        settingsStore.dockThemePresets = library
        if settingsForm.dockAppearance.userPresetID == id {
            settingsForm.dockAppearance.userPresetID = nil
            settingsForm.dockAppearance.preset = .custom
            applySettings(settingsForm)
        }
        if let name {
            showTransientStatus("Deleted “\(name)”")
        }
    }

    /// True when the chosen colours would be hard to read and were corrected.
    var dockAppearanceHadContrastCorrection: Bool {
        !settingsForm.dockAppearance.hasSufficientContrast
    }

    // MARK: - Reminder schedule

    var canAddReminder: Bool {
        settingsForm.reminderSchedule.canAddRule
    }

    func addReminder(amount: Int, unit: ReminderUnit) throws {
        var schedule = settingsForm.reminderSchedule
        try schedule.add(ReminderRule(amount: amount, unit: unit))
        applyReminderSchedule(schedule)
    }

    func updateReminder(_ rule: ReminderRule) throws {
        var schedule = settingsForm.reminderSchedule
        try schedule.update(rule)
        applyReminderSchedule(schedule)
    }

    func removeReminder(id: UUID) {
        var schedule = settingsForm.reminderSchedule
        schedule.remove(id: id)
        applyReminderSchedule(schedule)
    }

    func setReminderEnabled(_ isEnabled: Bool, for id: UUID) {
        var schedule = settingsForm.reminderSchedule
        schedule.setEnabled(isEnabled, for: id)
        applyReminderSchedule(schedule)
    }

    func moveReminders(fromOffsets source: IndexSet, toOffset destination: Int) {
        var schedule = settingsForm.reminderSchedule
        schedule.move(fromOffsets: source, toOffset: destination)
        applyReminderSchedule(schedule)
    }

    func resetRemindersToDefaults() {
        var schedule = settingsForm.reminderSchedule
        schedule.resetToDefaults()
        applyReminderSchedule(schedule)
    }

    private func applyReminderSchedule(_ schedule: ReminderSchedule) {
        settingsForm.reminderSchedule = schedule
        applySettings(settingsForm)
    }

    /// True only while macOS would actually show a permission prompt. Once the
    /// user has denied notifications, asking again does nothing, so the UI sends
    /// them to System Settings instead.
    var canRequestNotificationPermission: Bool {
        notificationPermission == .notDetermined
    }

    /// Briefly confirms a change without leaving a banner sitting on screen.
    func showTransientStatus(_ message: String) {
        statusMessage = message
        transientStatusTask?.cancel()
        transientStatusTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else {
                return
            }
            guard let self, self.statusMessage == message else {
                return
            }
            self.statusMessage = nil
        }
    }

    private func scheduleDebouncedNotificationUpdate() {
        notificationDebounceTask?.cancel()
        notificationDebounceTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(600))
            guard !Task.isCancelled, let self else {
                return
            }
            await self.synchronizeNotifications()
            guard !Task.isCancelled else {
                return
            }
            self.showTransientStatus("Notifications rescheduled")
        }
    }

    func removeFeed() async throws {
        try await feedURLStore.deleteFeedURL()
        await refreshCoordinator.clearImportSelections()
        storedFeedURLString = ""
        settingsForm.feedURL = ""
        startAutomaticRefreshLoop()
        statusMessage = "Canvas feed removed"
    }

    func requestNotificationPermission() async throws -> Bool {
        // Never ask again after a denial: macOS shows nothing, and repeating the
        // request would look like the button is broken.
        guard canRequestNotificationPermission else {
            return notificationPermission == .authorized
        }
        let granted = try await notificationScheduler.requestAuthorization()
        notificationPermission = NotificationPermissionState(
            await notificationScheduler.authorizationStatus()
        )
        if granted {
            await synchronizeNotifications()
        }
        return granted
    }

    func resetLocalData() async throws {
        try await repository.deleteAll()
        try await feedURLStore.deleteFeedURL()
        await refreshCoordinator.clearImportSelections()
        await notificationScheduler.cancelAll()

        if LaunchAtLoginController.isEnabled {
            try LaunchAtLoginController.setEnabled(false)
        }
        settingsStore.reset()

        storedFeedURLString = ""
        settingsForm = SettingsFormState(
            settings: settingsStore.snapshot,
            feedURL: nil
        )
        assignments = []
        selectedCourse = nil
        synchronizeDock()
        startAutomaticRefreshLoop()
        startCountdownTransitionLoop()
        statusMessage = "Local data reset"
    }

    func exportDiagnostics() async throws -> String {
        let diagnostic = await refreshCoordinator.diagnosticSnapshot()
        let completedCount = assignments.filter(\.isCompleted).count
        let ignoredCount = assignments.filter(\.isIgnored).count
        let manualCount = assignments.filter(\.isManual).count

        var lines = [
            "Canvas Countdown Diagnostics",
            "generatedAt=\(Date.now.ISO8601Format())",
            "system=\(ProcessInfo.processInfo.operatingSystemVersionString)",
            "feedConfigured=\(hasConfiguredFeed)",
            "assignmentCount=\(assignments.count)",
            "completedCount=\(completedCount)",
            "ignoredCount=\(ignoredCount)",
            "manualCount=\(manualCount)",
            "refreshInterval=\(settingsStore.refreshInterval.rawValue)",
            "reminderRuleCount=\(settingsStore.reminderSchedule.rules.count)",
            "enabledReminderOffsetsMinutes="
                + "\(settingsStore.reminderSchedule.enabledRules.map(\.offsetMinutes).sorted())",
            "dockLanguage=\(settingsStore.dockDisplayLanguage.rawValue)",
            "dockNumberSize=\(settingsStore.dockAppearance.numberSize.rawValue)",
            "dockTheme=\(settingsStore.dockAppearance.preset.rawValue)",
            "assistantEnabled=\(settingsStore.assistant.isEnabled)",
            "assistantProvider=\(settingsStore.assistant.provider.rawValue)",
            "assistantStaysLocal=\(settingsStore.assistant.staysOnThisMac)",
            "dockCountMode=\(settingsStore.dockCountMode.rawValue)",
            "selectedCourseCount=\(settingsStore.selectedCourses.count)",
            "launchAtLogin=\(LaunchAtLoginController.isEnabled)",
            "notificationPermission=\(notificationPermission)",
        ]
        if let diagnostic {
            lines.append("")
            lines.append("Last Feed Operation")
            lines.append(diagnostic.exportText)
        }
        return DiagnosticRedactor.redact(lines.joined(separator: "\n"))
    }

    func clearError() {
        errorMessage = nil
    }

    func clearStatus() {
        statusMessage = nil
    }

    private func refresh(
        trigger: RefreshTrigger,
        announcesResult: Bool
    ) async {
        guard !isRefreshing, hasConfiguredFeed else {
            if announcesResult, !hasConfiguredFeed {
                errorMessage = RefreshCoordinatorError.noSavedFeedURL
                    .localizedDescription
            }
            return
        }

        isRefreshing = true
        errorMessage = nil
        defer { isRefreshing = false }

        do {
            let result = try await refreshCoordinator.refresh(
                now: .now,
                trigger: trigger
            )
            lastRefreshDate = result.refreshedAt
            try await reloadAssignmentsAndSynchronize()
            if announcesResult {
                statusMessage = ImportSummary(result.importResult).message
            }
        } catch {
            present(error)
        }
    }

    private func reloadAssignmentsAndSynchronize() async throws {
        let snapshots = try await repository.fetchAll()
        apply(snapshots)
        synchronizeDock()
        startCountdownTransitionLoop()
        await scheduleNotificationsBestEffort(from: snapshots)
    }

    private func apply(_ snapshots: [AssignmentSnapshot]) {
        assignments = snapshots
            .map(AssignmentListItem.init(snapshot:))
            .sorted {
                if $0.dueDate != $1.dueDate {
                    return $0.dueDate < $1.dueDate
                }
                return $0.title.localizedStandardCompare($1.title)
                    == .orderedAscending
            }

        if let selectedCourse,
           !availableCourses.contains(selectedCourse) {
            self.selectedCourse = nil
        }
    }

    private func synchronizeDock() {
        currentDate = .now

        let selection = CountdownCalculator.nearestUpcoming(
            from: dockEligibleAssignments,
            now: currentDate,
            calendar: calendar
        )
        dockRenderer.render(
            daysRemaining: selection?.daysRemaining,
            label: settingsStore.dockLabel
        )
    }

    private func synchronizeNotifications() async {
        do {
            let snapshots = try await repository.fetchAll()
            try await scheduleNotifications(from: snapshots)
        } catch {
            // Notification failures should not roll back saved assignments or
            // settings, but they remain visible and actionable to the user.
            present(error)
        }
    }

    private func scheduleNotifications(
        from snapshots: [AssignmentSnapshot]
    ) async throws {
        try await notificationScheduler.reschedule(
            candidates: snapshots.map(NotificationCandidate.init(snapshot:)),
            schedule: settingsStore.reminderSchedule,
            now: .now,
            calendar: calendar
        )
    }

    private func scheduleNotificationsBestEffort(
        from snapshots: [AssignmentSnapshot]
    ) async {
        do {
            try await scheduleNotifications(from: snapshots)
        } catch {
            // The assignment mutation has already committed. Surface the
            // reminder failure without making the editor retry and create a
            // duplicate manual event.
            present(error)
        }
    }

    private func startAutomaticRefreshLoop() {
        automaticRefreshTask?.cancel()
        guard automaticActivityEnabled, hasConfiguredFeed else {
            automaticRefreshTask = nil
            return
        }

        automaticRefreshTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else {
                    return
                }
                let interval = self.settingsStore.refreshInterval.duration
                do {
                    try await Task.sleep(for: interval)
                } catch {
                    return
                }
                guard !Task.isCancelled else {
                    return
                }
                await self.refresh(
                    trigger: .automatic,
                    announcesResult: false
                )
            }
        }
    }

    private var dockEligibleAssignments: [AssignmentListItem] {
        courseScopedAssignments
    }

    private func startCountdownTransitionLoop() {
        countdownTransitionTask?.cancel()
        guard automaticActivityEnabled else {
            countdownTransitionTask = nil
            return
        }
        countdownTransitionTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else {
                    return
                }
                let now = Date.now
                guard let transitionDate =
                    CountdownCalculator.nextTransitionDate(
                        from: dockEligibleAssignments,
                        now: now,
                        calendar: calendar
                    )
                else {
                    return
                }
                let milliseconds = max(
                    250,
                    Int64(
                        transitionDate.timeIntervalSince(now) * 1_000
                    ) + 250
                )
                do {
                    try await Task.sleep(
                        for: .milliseconds(milliseconds)
                    )
                } catch {
                    return
                }
                guard !Task.isCancelled else {
                    return
                }
                self.synchronizeDock()
            }
        }
    }

    private func present(_ error: any Error) {
        guard !(error is CancellationError) else {
            return
        }
        errorMessage = error.localizedDescription
    }

    private static func previewIdentifier(
        event: ParsedCalendarEvent,
        index: Int
    ) -> String {
        let stableComponent = event.uid
            ?? event.url
            ?? "\(event.summary)|\(event.startDate.timeIntervalSinceReferenceDate)"
        return "\(index)|\(stableComponent)"
    }

    private static func assignmentMetadata(
        from summary: String
    ) -> (title: String, courseName: String?) {
        var title = summary.trimmingCharacters(in: .whitespacesAndNewlines)
        if title.lowercased().hasPrefix("assignment:") {
            title = String(title.dropFirst("assignment:".count))
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        guard title.hasSuffix("]"),
              let openingBracket = title.lastIndex(of: "["),
              openingBracket != title.startIndex else {
            return (title, nil)
        }

        let courseStart = title.index(after: openingBracket)
        let courseEnd = title.index(before: title.endIndex)
        let course = String(title[courseStart..<courseEnd])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let assignmentTitle = String(title[..<openingBracket])
            .trimmingCharacters(in: .whitespacesAndNewlines)

        guard !course.isEmpty,
              course.count <= 120,
              !assignmentTitle.isEmpty else {
            return (title, nil)
        }
        return (assignmentTitle, course)
    }
}


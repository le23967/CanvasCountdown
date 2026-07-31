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
    /// The day the calendar is centred on. Which grid is drawn around it is
    /// `calendarScale`; the two together are the whole of "where am I".
    var calendarAnchor = Date.now
    var selectedCalendarDay: Date?
    /// What the user typed into Go to Date, kept here so the field survives the
    /// popover being dismissed and reopened.
    var calendarDateEntry = ""
    /// Opened from the calendar bar and from the View menu, so it lives here
    /// rather than inside the bar.
    var isShowingCalendarDateEntry = false
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
    private let courseBlocklist: any CourseBlocklisting
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
        localModels: any LocalModelManaging = OllamaModelManager(),
        courseBlocklist: any CourseBlocklisting =
            UserDefaultsCourseBlocklistStore()
    ) {
        self.courseBlocklist = courseBlocklist
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
        undoToastTask?.cancel()
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

    // MARK: - Managing courses

    /// Courses the user removed, read back so Settings can offer to undo it.
    /// Cached here because the store lives off the main actor.
    private(set) var blockedCourses: [String] = []

    /// Every course with something stored against it, plus how much, so
    /// removing one says what it is about to take with it.
    var managedCourses: [ManagedCourse] {
        var counts: [String: (name: String, count: Int)] = [:]
        for item in assignments {
            guard let name = item.normalizedCourseName,
                  let key = CourseName.normalized(name) else {
                continue
            }
            counts[key] = (name, (counts[key]?.count ?? 0) + 1)
        }
        return counts
            .map { ManagedCourse(name: $0.value.name, eventCount: $0.value.count) }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    func refreshBlockedCourses() async {
        blockedCourses = await courseBlocklist.loadBlockedCourses().sorted()
    }

    /// Removes a course entirely: its events go, and the name is blocked so the
    /// next Canvas refresh does not import them all over again.
    func removeCourse(_ courseName: String) {
        Task {
            do {
                await courseBlocklist.block(courseName)
                let removed = try await repository.deleteEvents(
                    inCourse: courseName
                )
                if settingsForm.selectedCourses.contains(courseName) {
                    settingsForm.selectedCourses.remove(courseName)
                    settingsStore.selectedCourses = settingsForm.selectedCourses
                }
                if selectedCourse == courseName {
                    selectedCourse = nil
                }
                await refreshBlockedCourses()
                try await reloadAssignmentsAndSynchronize()
                showTransientStatus(
                    removed == 1
                        ? "Removed “\(courseName)” and 1 event"
                        : "Removed “\(courseName)” and \(removed) events"
                )
            } catch {
                present(error)
            }
        }
    }

    /// Lets a course back in. Nothing reappears until the next refresh, which
    /// the status message says outright rather than leaving it to be guessed.
    func allowCourse(_ courseName: String) {
        Task {
            await courseBlocklist.unblock(courseName)
            await refreshBlockedCourses()
            showTransientStatus(
                hasConfiguredFeed
                    ? "“\(courseName)” will return on the next refresh"
                    : "“\(courseName)” is no longer blocked"
            )
        }
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

    /// The width the user left the panel at. What it is actually drawn at is
    /// this trimmed to the room the window has, which only the layout knows.
    var preferredSidebarWidth: CGFloat {
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
    ///
    /// Every id created here is remembered, because a model asked for several
    /// tasks at once can produce several wrong ones at once, and taking those
    /// back one row at a time is exactly the kind of tidying nobody should have
    /// to do to recover from a bad guess.
    func saveAssistantDrafts(_ drafts: [AssistantDraftTask]) async {
        var savedIDs: [UUID] = []
        for draft in drafts where draft.include && draft.canBeSaved {
            guard let dueDate = draft.dueDate else {
                continue
            }
            do {
                let snapshot = try await repository.saveManual(
                    ManualAssignmentDraft(
                        title: draft.title.trimmingCharacters(
                            in: .whitespacesAndNewlines
                        ),
                        courseName: draft.courseName?.trimmingCharacters(
                            in: .whitespacesAndNewlines
                        ),
                        dueDate: dueDate
                    )
                )
                if let labelID = draft.labelID {
                    try await repository.setLabel(
                        id: snapshot.id,
                        labelID: labelID,
                        now: .now
                    )
                }
                savedIDs.append(snapshot.id)
            } catch {
                present(error)
            }
        }

        assistantDrafts = []
        guard !savedIDs.isEmpty else {
            return
        }

        do {
            try await reloadAssignmentsAndSynchronize()
        } catch {
            present(error)
        }
        offerUndo(
            for: savedIDs,
            message: savedIDs.count == 1
                ? "1 task added"
                : "\(savedIDs.count) tasks added",
            menuTitle: savedIDs.count == 1
                ? "Undo Adding 1 Task"
                : "Undo Adding \(savedIDs.count) Tasks"
        )
    }

    // MARK: - Undoing an addition

    /// Something just added, and what it would take to take it back.
    ///
    /// Covers both the assistant and the ordinary editor. Adding an event by
    /// hand can be a mistake in exactly the same way, and there is no reason
    /// the way back should depend on which door it came in through.
    struct UndoableAddition: Equatable, Sendable {
        var eventIDs: [UUID]
        /// What the toast says: "3 tasks added".
        var message: String
        /// What the menu says: "Undo Adding 3 Tasks".
        var menuTitle: String
    }

    /// How long the toast stays up. Long enough to notice and read, short
    /// enough not to sit in the way — and it is not the only way back, so it
    /// can afford to leave.
    static let undoToastDuration = 10

    /// The last addition, kept until it is undone or another one replaces it.
    /// Outlives the toast on purpose: the menu is what makes this reachable by
    /// someone who never learned a keyboard shortcut.
    private(set) var undoableAddition: UndoableAddition?

    /// Seconds left on the toast, or nil once it has gone. The addition itself
    /// stays undoable either way.
    private(set) var undoToastSecondsRemaining: Int?

    @ObservationIgnored
    private var undoToastTask: Task<Void, Never>?

    var isUndoToastVisible: Bool {
        undoToastSecondsRemaining != nil
    }

    var canUndoAddition: Bool {
        undoableAddition != nil
    }

    /// The Edit menu entry. Names what it would take back, so choosing it is
    /// not a guess.
    var undoAdditionMenuTitle: String {
        guard let addition = undoableAddition else {
            return "Undo Adding"
        }
        return addition.menuTitle
    }

    private func offerUndo(
        for eventIDs: [UUID],
        message: String,
        menuTitle: String
    ) {
        // A pending offer is replaced rather than queued: undo means the last
        // thing that happened, and two of them at once would be ambiguous.
        undoableAddition = UndoableAddition(
            eventIDs: eventIDs,
            message: message,
            menuTitle: menuTitle
        )
        statusMessage = nil
        transientStatusTask?.cancel()
        startUndoToastCountdown()
    }

    private func startUndoToastCountdown() {
        undoToastTask?.cancel()
        undoToastSecondsRemaining = Self.undoToastDuration
        undoToastTask = Task { [weak self] in
            while true {
                try? await Task.sleep(for: .seconds(1))
                guard !Task.isCancelled, let self else {
                    return
                }
                guard let remaining = self.undoToastSecondsRemaining else {
                    return
                }
                if remaining <= 1 {
                    // The toast goes; the way back stays, in the Edit menu.
                    self.undoToastSecondsRemaining = nil
                    return
                }
                self.undoToastSecondsRemaining = remaining - 1
            }
        }
    }

    /// Puts the toast away without touching what was added, and without taking
    /// the undo itself away.
    func dismissUndoToast() {
        undoToastTask?.cancel()
        undoToastTask = nil
        undoToastSecondsRemaining = nil
    }

    /// Deletes exactly what that addition created, and nothing else.
    func undoLastAddition() async {
        guard let addition = undoableAddition else {
            return
        }
        undoableAddition = nil
        dismissUndoToast()

        var removed = 0
        for id in addition.eventIDs {
            do {
                try await repository.delete(id: id)
                removed += 1
            } catch AssignmentRepositoryError.assignmentNotFound {
                // Already gone, by hand or by a reset. Nothing to undo, and
                // nothing worth interrupting anyone about.
                continue
            } catch {
                present(error)
            }
        }

        do {
            try await reloadAssignmentsAndSynchronize()
        } catch {
            present(error)
        }
        showTransientStatus(
            removed == 1 ? "1 event removed" : "\(removed) events removed"
        )
    }

    func loadAssistantKey() async {
        assistantAPIKey = (try? await assistantKeyStore.load(
            for: settingsStore.assistant.activeProfileID
        )) ?? ""
    }

    func saveAssistantKey(_ key: String) {
        let profileID = settingsStore.assistant.activeProfileID
        Task {
            do {
                try await assistantKeyStore.save(key, for: profileID)
                assistantAPIKey = key.trimmingCharacters(in: .whitespacesAndNewlines)
                showTransientStatus("Assistant key saved")
            } catch {
                present(error)
            }
        }
    }

    // MARK: - Saved models

    var assistantProfiles: [AssistantProfile] {
        settingsStore.assistantProfiles.profiles
    }

    var activeAssistantProfile: AssistantProfile? {
        settingsStore.assistantProfiles.profile(
            withID: settingsStore.assistant.activeProfileID
        )
    }

    var canAddAssistantProfile: Bool {
        !settingsStore.assistantProfiles.isFull
    }

    /// Whether the toolbar is worth a switcher. One model is just the model;
    /// the control only earns its place once there is something to switch to.
    var showsAssistantModelSwitcher: Bool {
        settingsStore.assistant.isEnabled && assistantProfiles.count > 1
    }

    var assistantModelDescription: String {
        guard let active = activeAssistantProfile else {
            return "Choose which AI model to use"
        }
        return "AI model, using \(active.name)"
    }

    /// Builds the starting library from whatever the assistant was already set
    /// to, and carries the existing API key onto it.
    ///
    /// Runs once, on the first launch after saved models arrived. Anyone
    /// upgrading finds their configuration already named and working rather
    /// than an empty list and an assistant that stopped answering.
    private func migrateAssistantProfilesIfNeeded() async {
        guard settingsStore.assistantProfiles.profiles.isEmpty else {
            return
        }
        let library = AssistantProfileLibrary.migrated(
            from: settingsStore.assistant
        )
        guard let first = library.profiles.first else {
            return
        }
        settingsStore.assistantProfiles = library
        settingsStore.assistant.activeProfileID = first.id
        settingsForm.assistant.activeProfileID = first.id
        if first.provider.requiresAPIKey {
            try? await assistantKeyStore.adoptLegacyKey(as: first.id)
        }
    }

    /// Switches the assistant to a saved model. Its address, its model name and
    /// its own API key all come with it.
    func selectAssistantProfile(_ id: UUID) {
        guard let profile = settingsStore.assistantProfiles.profile(withID: id),
              profile.id != settingsStore.assistant.activeProfileID else {
            return
        }
        settingsForm.assistant = AssistantSettings.applying(
            profile,
            to: settingsForm.assistant
        )
        applySettings(settingsForm)
        Task {
            await loadAssistantKey()
            showTransientStatus("Using “\(profile.name)”")
        }
    }

    /// Adds a model and selects it, so the fields below are already editing the
    /// thing that was just added.
    @discardableResult
    func addAssistantProfile(_ profile: AssistantProfile) -> UUID? {
        var library = settingsStore.assistantProfiles
        guard let id = library.add(profile) else {
            return nil
        }
        settingsStore.assistantProfiles = library
        guard let saved = library.profile(withID: id) else {
            return nil
        }
        settingsForm.assistant = AssistantSettings.applying(
            saved,
            to: settingsForm.assistant
        )
        applySettings(settingsForm)
        Task {
            await loadAssistantKey()
        }
        showTransientStatus("Added “\(saved.name)”")
        return id
    }

    func addAssistantProfile(for service: AssistantService) {
        addAssistantProfile(AssistantProfile(service: service))
    }

    /// A model to fill in by hand, for a service that is not on the list.
    /// Starts local, because that is the setting that cannot upload anything
    /// while it is half-configured.
    func addBlankAssistantProfile() {
        addAssistantProfile(
            AssistantProfile(
                name: "New Model",
                provider: .local,
                baseURL: AssistantProvider.local.defaultBaseURL,
                model: AssistantProvider.local.defaultModel
            )
        )
    }

    @discardableResult
    func renameAssistantProfile(
        _ id: UUID,
        to name: String
    ) -> AssistantProfileNameError? {
        var library = settingsStore.assistantProfiles
        if let error = library.rename(id, to: name) {
            return error
        }
        settingsStore.assistantProfiles = library
        return nil
    }

    func duplicateAssistantProfile(_ id: UUID) {
        var library = settingsStore.assistantProfiles
        guard let copy = library.duplicate(id) else {
            return
        }
        settingsStore.assistantProfiles = library
        if let name = library.profile(withID: copy)?.name {
            showTransientStatus("Duplicated as “\(name)”")
        }
    }

    /// Deleting a model takes its API key with it, and moves to another so the
    /// assistant is never left pointing at nothing.
    func deleteAssistantProfile(_ id: UUID) {
        var library = settingsStore.assistantProfiles
        let name = library.profile(withID: id)?.name
        library.remove(id)
        settingsStore.assistantProfiles = library

        if settingsStore.assistant.activeProfileID == id {
            if let next = library.profiles.first {
                settingsForm.assistant = AssistantSettings.applying(
                    next,
                    to: settingsForm.assistant
                )
            } else {
                settingsForm.assistant.activeProfileID = nil
            }
            applySettings(settingsForm)
        }

        Task {
            try? await assistantKeyStore.delete(for: id)
            await loadAssistantKey()
            if let name {
                showTransientStatus("Deleted “\(name)”")
            }
        }
    }

    func validateAssistantProfileName(
        _ name: String,
        excluding excludedID: UUID? = nil
    ) -> AssistantProfileNameError? {
        settingsStore.assistantProfiles.validate(
            name: name,
            excluding: excludedID
        )
    }

    /// Points the live configuration at one of the known services. The name is
    /// left alone: it is the user's, and a service is only a starting point.
    func useAssistantService(_ service: AssistantService) {
        settingsForm.assistant.provider = AssistantEndpoint
            .isLocal(service.baseURL) ? .local : .cloud
        settingsForm.assistant.baseURL = service.baseURL
        if let model = service.models.first {
            settingsForm.assistant.model = model
        }
        applySettings(settingsForm)
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

    /// Day, week, month or year. Persisted, because a person who works in the
    /// week view expects to come back to the week view.
    var calendarScale: CalendarScale {
        settingsStore.calendarScale
    }

    /// The month grid, built from exactly the events the list would show.
    var calendarDays: [CalendarDay] {
        AssignmentCalendar.month(
            containing: calendarAnchor,
            events: filteredAssignments,
            calendar: calendar,
            now: currentDate
        )
    }

    var calendarWeekDays: [CalendarDay] {
        AssignmentCalendar.week(
            containing: calendarAnchor,
            events: filteredAssignments,
            calendar: calendar,
            now: currentDate
        )
    }

    var calendarDay: CalendarDay {
        AssignmentCalendar.day(
            containing: calendarAnchor,
            events: filteredAssignments,
            calendar: calendar,
            now: currentDate
        )
    }

    var calendarYearMonths: [CalendarMonthSummary] {
        AssignmentCalendar.year(
            containing: calendarAnchor,
            events: filteredAssignments,
            calendar: calendar,
            now: currentDate
        )
    }

    var calendarTitle: String {
        AssignmentCalendar.title(
            for: calendarScale,
            date: calendarAnchor,
            calendar: calendar
        )
    }

    /// Still used by the month grid's popover, and by anything that wants the
    /// day the user last clicked.
    var selectedDayItems: [AssignmentListItem] {
        guard let selectedCalendarDay else {
            return []
        }
        return items(on: selectedCalendarDay)
    }

    func items(on day: Date) -> [AssignmentListItem] {
        filteredAssignments
            .filter { calendar.isDate($0.dueDate, inSameDayAs: day) }
            .sorted { $0.dueDate < $1.dueDate }
    }

    func showCalendarScale(_ scale: CalendarScale) {
        guard scale != calendarScale else {
            return
        }
        settingsForm.calendarScale = scale
        applySettings(settingsForm)
        selectedCalendarDay = nil
    }

    /// One step of whatever is on screen: a day in Day, a week in Week, and so
    /// on, so the arrows always mean what the switcher says.
    func showCalendar(offsetBy steps: Int) {
        // Anchored to the start of the period first, so paging through the
        // months from the 31st does not slide a day earlier each time.
        let anchor = AssignmentCalendar.normalized(
            calendarAnchor,
            for: calendarScale,
            calendar: calendar
        )
        calendarAnchor = AssignmentCalendar.date(
            byAdding: calendarScale,
            count: steps,
            to: anchor,
            calendar: calendar
        )
        selectedCalendarDay = nil
    }

    func showToday() {
        calendarAnchor = currentDate
        selectedCalendarDay = nil
    }

    /// Moves the calendar to a day, optionally changing scale with it — how the
    /// year view drills into a month and the month view into a day.
    func showCalendarDate(_ date: Date, scale: CalendarScale? = nil) {
        calendarAnchor = date
        selectedCalendarDay = nil
        if let scale {
            showCalendarScale(scale)
        }
    }

    var isShowingToday: Bool {
        switch calendarScale {
        case .day:
            calendar.isDate(calendarAnchor, inSameDayAs: currentDate)
        case .week:
            calendar.isDate(calendarAnchor, equalTo: currentDate, toGranularity: .weekOfYear)
        case .month:
            calendar.isDate(calendarAnchor, equalTo: currentDate, toGranularity: .month)
        case .year:
            calendar.isDate(calendarAnchor, equalTo: currentDate, toGranularity: .year)
        }
    }

    /// What Go to Date would land on, so the field can show the day before it
    /// is committed and disable itself when the text means nothing.
    var calendarDateEntryResult: Date? {
        CalendarDateEntry.date(
            from: calendarDateEntry,
            reference: calendarAnchor,
            calendar: calendar,
            now: currentDate
        )
    }

    /// Returns whether it went anywhere, so the field can stay open on a typo.
    @discardableResult
    func commitCalendarDateEntry() -> Bool {
        guard let target = calendarDateEntryResult else {
            return false
        }
        showCalendarDate(target)
        calendarDateEntry = ""
        return true
    }

    func selectCalendarDay(_ day: Date?) {
        selectedCalendarDay = day
    }

    /// What the View menu calls: it moves to the calendar first, so ⌘2 from the
    /// list means "show me the week" rather than quietly changing a setting for
    /// a screen that is not open.
    func showCalendarSection(_ scale: CalendarScale? = nil) {
        sidebarSelection = .calendar
        if let scale {
            showCalendarScale(scale)
        } else {
            showToday()
        }
    }

    func presentCalendarDateEntry() {
        sidebarSelection = .calendar
        isShowingCalendarDateEntry = true
    }

    // MARK: - Search

    /// Search is a panel over the window, not a field wedged into the toolbar.
    ///
    /// The field used to replace the whole toolbar with itself and a Cancel
    /// button, which meant the six things people came for disappeared the
    /// moment they went looking for something. A panel takes nothing away: the
    /// toolbar stays exactly as it was, the list stays exactly as it was, and
    /// the results are in the panel rather than hidden behind it.
    func presentSearch() {
        isSearchModeActive = true
        isSearchFieldFocused = true
        highlightedSearchResultID = searchResults.first?.id
    }

    func toggleSearch() {
        if isSearchModeActive {
            dismissSearch()
        } else {
            presentSearch()
        }
    }

    /// Releases keyboard focus but leaves the panel and its query in place.
    func dismissSearchFocus() {
        isSearchFieldFocused = false
    }

    /// Closes the panel and clears the query. Escape, a click on the dimmed
    /// backdrop, opening a result and a section change all land here, so
    /// leaving search always means the same thing.
    func dismissSearch() {
        isSearchFieldFocused = false
        isSearchModeActive = false
        highlightedSearchResultID = nil
        if !searchText.isEmpty {
            searchText = ""
        }
    }

    func handleSearchEscape() {
        dismissSearch()
    }

    /// The toolbar no longer changes shape for search, so every action stays
    /// where it was put.
    var showsOrdinaryToolbarActions: Bool {
        true
    }

    /// Seven compact actions, or eight once there is more than one saved model
    /// to switch between. Search no longer takes the row over.
    var toolbarItemCount: Int {
        showsAssistantModelSwitcher ? 8 : 7
    }

    func clearSearchQuery() {
        searchText = ""
        highlightedSearchResultID = nil
    }

    /// Called before a toolbar action runs, so the field gives up focus and the
    /// action still happens on the same click.
    func prepareForToolbarAction() {
        dismissSearchFocus()
    }

    // MARK: - Search results

    /// The row the keyboard is on, so Return has something to open.
    var highlightedSearchResultID: UUID?

    /// How many results the panel will show before it stops. A panel that grows
    /// past the window is not a panel any more.
    static let maximumSearchResults = 8

    /// Everything stored, not just the section on screen.
    ///
    /// Searching only what is already visible would mean the answer depends on
    /// where you happened to be standing, which is the opposite of what a
    /// search is for. A completed assignment is still a thing you can look for.
    var searchResults: [AssignmentListItem] {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else {
            return []
        }
        return assignments
            .filter { item in
                item.title.localizedCaseInsensitiveContains(query)
                    || (item.normalizedCourseName?
                        .localizedCaseInsensitiveContains(query) ?? false)
            }
            .sorted { left, right in
                // What is still ahead comes first, nearest deadline at the top;
                // anything already past follows, most recent first.
                let leftPast = left.dueDate < currentDate
                let rightPast = right.dueDate < currentDate
                if leftPast != rightPast {
                    return !leftPast
                }
                if leftPast {
                    return left.dueDate > right.dueDate
                }
                return left.dueDate < right.dueDate
            }
            .prefix(Self.maximumSearchResults)
            .map { $0 }
    }

    var hasSearchQuery: Bool {
        !searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var highlightedSearchResult: AssignmentListItem? {
        guard let highlightedSearchResultID else {
            return searchResults.first
        }
        return searchResults.first { $0.id == highlightedSearchResultID }
            ?? searchResults.first
    }

    /// Moves the keyboard highlight, wrapping at both ends so holding an arrow
    /// key never dead-ends.
    func moveSearchHighlight(by offset: Int) {
        let results = searchResults
        guard !results.isEmpty else {
            highlightedSearchResultID = nil
            return
        }
        guard let current = highlightedSearchResultID,
              let index = results.firstIndex(where: { $0.id == current }) else {
            highlightedSearchResultID = offset >= 0
                ? results.first?.id
                : results.last?.id
            return
        }
        let next = (index + offset + results.count) % results.count
        highlightedSearchResultID = results[next].id
    }

    /// Keeps the highlight on something that still exists as the query changes.
    func searchQueryDidChange() {
        let results = searchResults
        guard let current = highlightedSearchResultID,
              results.contains(where: { $0.id == current }) else {
            highlightedSearchResultID = results.first?.id
            return
        }
    }

    /// Opens a result the way clicking its row would, and closes the panel:
    /// the search is over once it has been answered.
    func openSearchResult(_ item: AssignmentListItem) {
        selectedEventID = item.id
        dismissSearch()
        presentEditor(for: item)
    }

    @discardableResult
    func openHighlightedSearchResult() -> Bool {
        guard let item = highlightedSearchResult else {
            return false
        }
        openSearchResult(item)
        return true
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
            // Deliberately not narrowed by the search query. The panel shows
            // what it found; rearranging the list underneath it as well would
            // mean closing the search left you somewhere you did not choose to
            // be.
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
            await migrateAssistantProfilesIfNeeded()
            await loadAssistantKey()
            await refreshBlockedCourses()
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
        let isNew = draft.eventID == nil
        let saved = try await repository.saveManual(
            ManualAssignmentDraft(presentation: draft)
        )
        try await reloadAssignmentsAndSynchronize()

        // An event typed by hand can be a mistake in the same way one the
        // assistant proposed can, so it gets the same way back rather than a
        // status message that only says it happened.
        guard isNew else {
            statusMessage = "Event updated"
            return
        }
        offerUndo(
            for: [saved.id],
            message: "Added “\(saved.title)”",
            menuTitle: "Undo Adding “\(saved.title)”"
        )
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

    // MARK: - Labels

    var eventLabels: [EventLabel] {
        settingsStore.eventLabels.labels
    }

    var canAddEventLabel: Bool {
        !settingsStore.eventLabels.isFull
    }

    func label(for item: AssignmentListItem) -> EventLabel? {
        settingsStore.eventLabels.label(id: item.labelID)
    }

    /// Nil takes the label off. The same call does both, so a row's menu is one
    /// list with a "None" at the top rather than an assign and a clear.
    func setLabel(_ labelID: UUID?, for item: AssignmentListItem) {
        Task {
            do {
                try await repository.setLabel(
                    id: item.id,
                    labelID: labelID,
                    now: .now
                )
                try await reloadAssignmentsAndSynchronize()
            } catch {
                present(error)
            }
        }
    }

    func validateLabelName(
        _ name: String,
        excluding excludedID: UUID? = nil
    ) -> EventLabelNameError? {
        settingsStore.eventLabels.validate(name: name, excluding: excludedID)
    }

    func labelUsageCount(_ id: UUID) -> Int {
        assignments.filter { $0.labelID == id }.count
    }

    /// Adds a label the user can then rename in place, rather than making them
    /// name it in a sheet before they can see it.
    func addEventLabel() {
        let palette = [
            "#E5484D", "#F5A623", "#30A46C", "#3E63DD",
            "#8E4EC6", "#0091FF", "#D6409F", "#12A594",
        ]
        let used = Set(eventLabels.map { $0.colorHex.uppercased() })
        let colorHex = palette.first { !used.contains($0) } ?? palette[
            eventLabels.count % palette.count
        ]

        var name = "New Label"
        var suffix = 2
        while validateLabelName(name) == .duplicate {
            name = "New Label \(suffix)"
            suffix += 1
        }
        addEventLabel(name: name, colorHex: colorHex)
    }

    @discardableResult
    func addEventLabel(
        name: String,
        colorHex: String
    ) -> EventLabelNameError? {
        var library = settingsStore.eventLabels
        do {
            let label = try library.add(name: name, colorHex: colorHex)
            settingsStore.eventLabels = library
            showTransientStatus("Added “\(label.name)”")
            return nil
        } catch let error as EventLabelNameError {
            return error
        } catch {
            present(error)
            return nil
        }
    }

    @discardableResult
    func renameEventLabel(_ id: UUID, to name: String) -> EventLabelNameError? {
        var library = settingsStore.eventLabels
        do {
            try library.rename(id, to: name)
            settingsStore.eventLabels = library
            return nil
        } catch let error as EventLabelNameError {
            return error
        } catch {
            present(error)
            return nil
        }
    }

    func recolorEventLabel(_ id: UUID, to colorHex: String) {
        var library = settingsStore.eventLabels
        library.recolor(id, to: colorHex)
        settingsStore.eventLabels = library
    }

    /// Deleting a label takes it off everything carrying it, so no event is
    /// left pointing at a colour that no longer exists.
    func deleteEventLabel(_ id: UUID) {
        let name = settingsStore.eventLabels.label(id: id)?.name
        var library = settingsStore.eventLabels
        library.remove(id)
        settingsStore.eventLabels = library

        Task {
            do {
                let cleared = try await repository.clearLabel(id, now: .now)
                try await reloadAssignmentsAndSynchronize()
                if let name {
                    showTransientStatus(
                        cleared == 0
                            ? "Deleted “\(name)”"
                            : "Deleted “\(name)” and removed it from \(cleared) event\(cleared == 1 ? "" : "s")"
                    )
                }
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

        let endOfDay = settingsStore.treatsMidnightAsEndOfDay
        return preview.events.enumerated().map { index, event in
            let id = Self.previewIdentifier(event: event, index: index)
            parsedPreviewEvents[id] = event
            let metadata = Self.assignmentMetadata(from: event.summary)
            return ImportPreviewItem(
                id: id,
                title: metadata.title,
                courseName: metadata.courseName,
                // Shown the way it will be listed once imported, so the preview
                // is not the one place still saying midnight.
                dueDate: DueTimePolicy.effectiveImportedDueDate(
                    event.startDate,
                    treatsMidnightAsEndOfDay: endOfDay,
                    calendar: calendar
                ),
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
        settingsStore.calendarScale = form.calendarScale

        // Every deadline on screen is re-read through the policy, so the change
        // has to reach the list rather than waiting for the next refresh.
        let dueTimesChanged = settingsStore.treatsMidnightAsEndOfDay
            != form.treatsMidnightAsEndOfDay
        settingsStore.treatsMidnightAsEndOfDay = form.treatsMidnightAsEndOfDay

        let remindersChanged =
            settingsStore.reminderSchedule != form.reminderSchedule
        settingsStore.reminderSchedule = form.reminderSchedule

        settingsStore.assistant = form.assistant
        // Editing the address or the model edits the model it belongs to.
        // There is no separate save step: a saved model that quietly disagreed
        // with the fields above it would be worse than no saving at all.
        syncActiveAssistantProfile(with: form.assistant)

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

        if dueTimesChanged {
            Task { [weak self] in
                guard let self else {
                    return
                }
                do {
                    try await self.reloadAssignmentsAndSynchronize()
                } catch {
                    self.present(error)
                }
            }
        }

        if remindersChanged {
            scheduleDebouncedNotificationUpdate()
        } else {
            showTransientStatus("Settings updated")
        }
    }

    private func syncActiveAssistantProfile(with settings: AssistantSettings) {
        guard let id = settings.activeProfileID else {
            return
        }
        guard let existing = settingsStore.assistantProfiles.profile(withID: id),
              existing.provider != settings.provider
                || existing.baseURL != settings.baseURL
                || existing.model != settings.model else {
            return
        }
        var library = settingsStore.assistantProfiles
        guard library.update(
            id,
            provider: settings.provider,
            baseURL: settings.baseURL,
            model: settings.model
        ) else {
            return
        }
        settingsStore.assistantProfiles = library
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
        await courseBlocklist.clearBlockedCourses()
        await refreshBlockedCourses()
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
        // Nothing survives the reset, so there is nothing left to undo.
        undoableAddition = nil
        dismissUndoToast()
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
        let endOfDay = settingsStore.treatsMidnightAsEndOfDay
        assignments = snapshots
            .map {
                AssignmentListItem(
                    snapshot: $0,
                    treatsMidnightAsEndOfDay: endOfDay,
                    calendar: calendar
                )
            }
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
        let endOfDay = settingsStore.treatsMidnightAsEndOfDay
        try await notificationScheduler.reschedule(
            candidates: snapshots.map {
                NotificationCandidate(
                    snapshot: $0,
                    treatsMidnightAsEndOfDay: endOfDay,
                    calendar: calendar
                )
            },
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


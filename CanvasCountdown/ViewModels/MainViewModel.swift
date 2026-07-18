import Foundation
import Observation

@MainActor
@Observable
final class MainViewModel {
    var sidebarSelection: SidebarDestination? = .upcoming
    var assignments: [AssignmentListItem] = []
    var searchText = ""
    var selectedCourse: String?
    var showCompletedAndIgnored = false
    var currentDate = Date.now
    var settingsForm: SettingsFormState
    var notificationPermission: NotificationPermissionState = .notDetermined

    var isLoading = false
    var isRefreshing = false
    var isShowingFeedImport = false
    var feedImportIsOnboarding = false
    var feedImportInitialURL = ""
    var isShowingManualEditor = false
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
    private var hasStarted = false

    init(
        repository: any AssignmentRepository,
        refreshCoordinator: any FeedRefreshCoordinating,
        feedURLStore: any FeedURLStoring,
        settingsStore: SettingsStore,
        dockRenderer: any DockRendering,
        notificationScheduler: any NotificationScheduling,
        calendar: Calendar = .autoupdatingCurrent,
        automaticActivityEnabled: Bool = true
    ) {
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
                case .allEvents:
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

    func resetDockAppearance() {
        settingsForm.dockAppearance = .defaults
        applySettings(settingsForm)
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


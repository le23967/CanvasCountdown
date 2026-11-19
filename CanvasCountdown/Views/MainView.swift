import AppKit
import SwiftUI

struct MainView: View {
    @Bindable var viewModel: MainViewModel
    let screenshotImportViewModel: ScreenshotImportViewModel

    @State private var eventPendingDeletion: AssignmentListItem?
    @State private var hoveredEventID: UUID?
    @State private var assistantWindow = AssistantWindowController()
    @State private var draggedSidebarWidth: CGFloat?
    @State private var sidebarWidthAtDragStart: CGFloat?
    @State private var isResizeCursorPushed = false
    @State private var navigationColumnVisibility: NavigationSplitViewVisibility = .automatic
    /// Only what this view folded away is unfolded again, so a sidebar the user
    /// closed themselves stays closed.
    @State private var didFoldNavigationForAssistant = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        NavigationSplitView(columnVisibility: $navigationColumnVisibility) {
            SidebarView(
                selection: $viewModel.sidebarSelection,
                upcomingCount: viewModel.upcomingCount
            )
            .navigationSplitViewColumnWidth(min: 185, ideal: 220, max: 280)
        } detail: {
            detailWithAssistant
                // The same floor whether or not the assistant is open, so
                // opening it never changes what the window is allowed to be
                // and macOS is never given a reason to move the window.
                .frame(minWidth: AssistantLayout.paneMinimum, minHeight: 420)
                // Across the content column, not the whole window: an inset on
                // the split view itself lays the strip over the sidebar's first
                // row, which then reads as a rendering fault rather than a
                // notice.
                .safeAreaInset(edge: .top) {
                    updateBanner
                }
                // Here for the same reason, at the other end. Set on the split
                // view, the strip was drawn over the last 36 points of every
                // column instead of making room for itself: the bottom row of
                // the list sat behind it, and so did the assistant's Send
                // button, for as long as a message that does not time out —
                // "Event updated", an import summary — was left showing.
                .safeAreaInset(edge: .bottom) {
                    if let statusMessage = viewModel.statusMessage {
                        statusBanner(statusMessage)
                    }
                }
                .animation(
                    reduceMotion ? nil : .snappy(duration: 0.22),
                    value: viewModel.availableUpdate
                )
        }
        .task {
            await viewModel.start()
        }
        .onReceive(
            NotificationCenter.default.publisher(
                for: NSApplication.didBecomeActiveNotification
            )
        ) { _ in
            Task { await viewModel.appDidBecomeActive() }
        }
        .background(ToolbarDisplayModeConfigurator())
        .onChange(of: viewModel.assistantPresentation) { _, presentation in
            syncAssistantWindow(presentation == .separateWindow)
        }
        .sheet(isPresented: $viewModel.isShowingManualEditor) {
            ManualEventEditorView(draft: viewModel.manualEventDraft) { draft in
                try await viewModel.saveManualEvent(draft)
            }
        }
        .sheet(isPresented: $viewModel.isShowingScreenshotImport) {
            ScreenshotImportView(
                viewModel: screenshotImportViewModel,
                existingCourseNames: viewModel.availableCourses
            ) {
                await viewModel.screenshotImportDidFinish()
            }
        }
        .sheet(isPresented: $viewModel.isShowingImportedDetails) {
            if let item = viewModel.currentImportedEventDetails {
                ImportedEventDetailsView(
                    item: item,
                    now: viewModel.currentDate,
                    onToggleCompleted: { viewModel.toggleCompleted($0) },
                    onToggleIgnored: { viewModel.toggleIgnored($0) }
                )
            }
        }
        .sheet(isPresented: $viewModel.isShowingFeedImport) {
            FeedImportView(
                initialURL: viewModel.feedImportInitialURL,
                isOnboarding: viewModel.feedImportIsOnboarding,
                onPreview: { url in
                    try await viewModel.previewFeed(url)
                },
                onImport: { url, selectedIDs in
                    try await viewModel.importFeed(
                        url,
                        selectedIDs: selectedIDs
                    )
                }
            )
        }
        .alert(
            "Canvas Countdown couldn’t complete that action",
            isPresented: Binding(
                get: { viewModel.errorMessage != nil },
                set: { visible in
                    if !visible {
                        viewModel.clearError()
                    }
                }
            )
        ) {
            Button("OK") {
                viewModel.clearError()
            }
        } message: {
            Text(viewModel.errorMessage ?? "")
        }
        .alert(
            "Delete this event?",
            isPresented: Binding(
                get: { eventPendingDeletion != nil },
                set: { visible in
                    if !visible {
                        eventPendingDeletion = nil
                    }
                }
            ),
            presenting: eventPendingDeletion
        ) { item in
            Button("Cancel", role: .cancel) {
                eventPendingDeletion = nil
            }
            Button("Delete", role: .destructive) {
                viewModel.deleteManualEvent(item)
                eventPendingDeletion = nil
            }
        } message: { item in
            Text("“\(item.title)” will be permanently removed.")
        }
        // Floating, so it covers nothing and moves nothing: the list underneath
        // stays exactly where it was while the offer is up.
        .overlay(alignment: .bottom) {
            if let addition = viewModel.undoableAddition,
               let seconds = viewModel.undoToastSecondsRemaining {
                UndoToastView(
                    message: addition.message,
                    secondsRemaining: seconds,
                    totalSeconds: MainViewModel.undoToastDuration,
                    onUndo: {
                        Task { await viewModel.undoLastAddition() }
                    },
                    onDismiss: {
                        viewModel.dismissUndoToast()
                    }
                )
                .padding(.bottom, 22)
                .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(
            reduceMotion ? nil : .snappy(duration: 0.22),
            value: viewModel.isUndoToastVisible
        )
        // Over everything, including the toolbar, the way a search panel should
        // be: nothing behind it is taken away to make room.
        .overlay {
            if viewModel.isSearchModeActive {
                SearchOverlayView(viewModel: viewModel)
            }
        }
        .animation(
            reduceMotion ? nil : .snappy(duration: 0.18),
            value: viewModel.isSearchModeActive
        )
    }

    /// The assignments and the assistant, side by side inside the window.
    ///
    /// Deliberately not `.inspector`: an inspector is a column macOS sizes by
    /// widening the window, which a tiled or split-screen window cannot do, so
    /// there the panel never appeared at all.
    ///
    /// Both panes are given an explicit width cut from the room this column
    /// actually has, and the geometry reader between them and the window stops
    /// either pane's own minimum width from travelling any further out. That is
    /// what keeps the window still: with nothing asking for more room, macOS has
    /// no reason to widen the window, so a tiled or split-screen window opens
    /// the assistant at the size the user left it.
    ///
    /// A width cut from the column is only half of it, though. A pane handed
    /// less than its contents can be laid out in does not shrink to fit: it is
    /// centred on the space it was given and hangs out of both sides. That put
    /// the assistant on top of the list on one side and off the edge of the
    /// window on the other. So neither pane is ever laid out narrower than
    /// `AssistantLayout.paneMinimum`, both are clipped to their own room, and a
    /// column too narrow for two of them gives the panel all of it.
    private var detailWithAssistant: some View {
        GeometryReader { proxy in
            let panes = AssistantLayout.panes(
                isPanelShown: isAssistantSidebarShown,
                preferred: draggedSidebarWidth ?? viewModel.preferredSidebarWidth,
                columnWidth: proxy.size.width,
                dividerWidth: assistantDividerWidth
            )
            let dividerWidth = panes.panelCoversList ? 0 : assistantDividerWidth
            let panelContentWidth = max(0, panes.panelWidth - dividerWidth)

            ZStack(alignment: .topTrailing) {
                detail
                    // Leading, so what is cut off in the very narrowest window
                    // is the trailing edge rather than both edges at once.
                    .frame(width: panes.listWidth, alignment: .leading)
                    .clipped()
                    .frame(maxWidth: .infinity, alignment: .leading)
                    // Covered rather than removed: taking it out of the window
                    // would throw away where it was scrolled to and anything
                    // half-typed in Settings. Covered also means not there to
                    // be clicked or read out.
                    .opacity(panes.panelCoversList ? 0 : 1)
                    .allowsHitTesting(!panes.panelCoversList)
                    .accessibilityHidden(panes.panelCoversList)
                    // Nor there to be typed at: a row that kept keyboard
                    // focus could still be opened with Return, or offered for
                    // deletion with Delete, without being seen.
                    .disabled(panes.panelCoversList)

                if isAssistantSidebarShown {
                    HStack(spacing: 0) {
                        if !panes.panelCoversList {
                            assistantResizeDivider
                        }
                        VStack(spacing: 0) {
                            if panes.panelCoversList {
                                backToListBar
                            }
                            assistantPanel
                        }
                        .frame(
                            width: max(
                                panelContentWidth,
                                AssistantLayout.paneMinimum
                            ),
                            alignment: .leading
                        )
                        .frame(width: panelContentWidth, alignment: .leading)
                        .clipped()
                    }
                    .frame(width: panes.panelWidth, alignment: .leading)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
            .clipped()
            .animation(
                reduceMotion ? nil : .snappy(duration: 0.22),
                value: isAssistantSidebarShown
            )
            // Folding the navigation sidebar buys the two panes another ~200pt
            // without touching the window, which is the only other place the
            // room can come from. It happens once per opening and never
            // reverses on width: folding is itself what makes the column wide
            // enough again, so reacting to that would just fight itself. Bring
            // the sidebar back by hand and it stays back.
            .onChange(of: needsMoreRoom(inColumnOf: proxy.size.width)) { _, needed in
                guard needed, !didFoldNavigationForAssistant else {
                    return
                }
                navigationColumnVisibility = .detailOnly
                didFoldNavigationForAssistant = true
            }
        }
        .onChange(of: isAssistantSidebarShown) { _, shown in
            guard !shown, didFoldNavigationForAssistant else {
                return
            }
            navigationColumnVisibility = .automatic
            didFoldNavigationForAssistant = false
        }
    }

    private func needsMoreRoom(inColumnOf columnWidth: CGFloat) -> Bool {
        isAssistantSidebarShown
            && columnWidth > 0
            && columnWidth < AssistantLayout.minimumWidthForBoth
    }

    private var isAssistantSidebarShown: Bool {
        viewModel.assistantPresentation == .sidebar
    }

    private var assistantDividerWidth: CGFloat { 1 }

    /// The way back, written out, for when the panel has the column to itself.
    ///
    /// With the list covered, the only other things that close the panel are a
    /// small cross and a toolbar button that a window this narrow has usually
    /// pushed into the overflow menu. Neither says that the list is still
    /// there. This names the page underneath and goes back to it.
    private var backToListBar: some View {
        let section = (viewModel.sidebarSelection ?? .upcoming).title
        return HStack(spacing: 0) {
            Button {
                viewModel.closeAssistant()
            } label: {
                Label(section, systemImage: "chevron.left")
                    .font(.callout)
                    .lineLimit(1)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.tint)
            .help("Close the assistant and go back to \(section). The window is too narrow to show both side by side.")
            .accessibilityLabel("Back to \(section)")

            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 7)
        .background(.bar)
        .overlay(alignment: .bottom) {
            Divider()
        }
    }

    /// Replaces the drag handle the inspector used to provide. The width is only
    /// written back when the drag ends, so a drag in progress cannot leave a
    /// half-way value in preferences.
    private var assistantResizeDivider: some View {
        Divider()
            .overlay(alignment: .center) {
                Color.clear
                    .frame(width: 10)
                    .contentShape(Rectangle())
                    .onHover { isInside in
                        // Tracked, because the handle can go away while the
                        // pointer is still on it — the panel is closed from
                        // the keyboard, or the window narrows until the panel
                        // covers the list — and a cursor pushed without its
                        // pop stays a resize arrow over the whole window.
                        if isInside, !isResizeCursorPushed {
                            NSCursor.resizeLeftRight.push()
                            isResizeCursorPushed = true
                        } else if !isInside, isResizeCursorPushed {
                            NSCursor.pop()
                            isResizeCursorPushed = false
                        }
                    }
                    .onDisappear {
                        if isResizeCursorPushed {
                            NSCursor.pop()
                            isResizeCursorPushed = false
                        }
                    }
                    .gesture(
                        DragGesture(minimumDistance: 1)
                            .onChanged { value in
                                let base = sidebarWidthAtDragStart
                                    ?? viewModel.preferredSidebarWidth
                                sidebarWidthAtDragStart = base
                                draggedSidebarWidth = min(
                                    max(
                                        base - value.translation.width,
                                        AssistantLayout.sidebarMinimum
                                    ),
                                    AssistantLayout.sidebarMaximum
                                )
                            }
                            .onEnded { _ in
                                if let width = draggedSidebarWidth {
                                    viewModel.rememberSidebarWidth(width)
                                }
                                draggedSidebarWidth = nil
                                sidebarWidthAtDragStart = nil
                            }
                    )
                    .accessibilityHidden(true)
            }
    }

    @ViewBuilder
    private var detail: some View {
        if (viewModel.sidebarSelection ?? .upcoming) == .settings {
            SettingsView(
                settings: $viewModel.settingsForm,
                availableCourses: viewModel.availableCourses,
                notificationPermission: viewModel.notificationPermission,
                hasConfiguredFeed: viewModel.hasConfiguredFeed,
                onConnectFeed: { value in
                    viewModel.presentFeedImport(initialURL: value)
                },
                onRemoveFeed: {
                    try await viewModel.removeFeed()
                },
                onRequestNotificationPermission: {
                    try await viewModel.requestNotificationPermission()
                },
                onSettingsChanged: { form in
                    viewModel.applySettings(form)
                },
                onResetData: {
                    try await viewModel.resetLocalData()
                },
                onExportDiagnostics: {
                    try await viewModel.exportDiagnostics()
                },
                canAddReminder: viewModel.canAddReminder,
                canRequestNotificationPermission:
                    viewModel.canRequestNotificationPermission,
                onAddReminder: { amount, unit in
                    reminderFailure {
                        try viewModel.addReminder(amount: amount, unit: unit)
                    }
                },
                onUpdateReminder: { rule in
                    reminderFailure {
                        try viewModel.updateReminder(rule)
                    }
                },
                onRemoveReminder: { id in
                    viewModel.removeReminder(id: id)
                },
                onSetReminderEnabled: { isEnabled, id in
                    viewModel.setReminderEnabled(isEnabled, for: id)
                },
                onResetReminders: {
                    viewModel.resetRemindersToDefaults()
                },
                dockThemes: DockThemePresetActions(
                    presets: viewModel.dockThemePresets,
                    status: viewModel.dockThemeStatus,
                    applyBuiltIn: { viewModel.applyDockTheme($0) },
                    applySaved: { viewModel.applyDockTheme($0) },
                    save: { viewModel.saveDockThemePreset(named: $0) },
                    updateSelected: { viewModel.updateSelectedDockThemePreset() },
                    revertSelected: { viewModel.revertToSelectedDockThemePreset() },
                    rename: { viewModel.renameDockThemePreset($0, to: $1) },
                    duplicate: { viewModel.duplicateDockThemePreset($0) },
                    delete: { viewModel.deleteDockThemePreset($0) },
                    validateName: { viewModel.validateDockPresetName($0, excluding: $1) }
                ),
                onResetDockAppearance: {
                    viewModel.resetDockAppearance()
                },
                labels: EventLabelActions(
                    labels: viewModel.eventLabels,
                    canAdd: viewModel.canAddEventLabel,
                    add: { viewModel.addEventLabel() },
                    rename: { viewModel.renameEventLabel($0, to: $1) },
                    recolor: { viewModel.recolorEventLabel($0, to: $1) },
                    delete: { viewModel.deleteEventLabel($0) },
                    usageCount: { viewModel.labelUsageCount($0) }
                ),
                courses: CourseManagementActions(
                    courses: viewModel.managedCourses,
                    blocked: viewModel.blockedCourses,
                    remove: { viewModel.removeCourse($0) },
                    allow: { viewModel.allowCourse($0) }
                ),
                assistant: AssistantProfileActions(
                    profiles: viewModel.assistantProfiles,
                    activeID: viewModel.assistantSettings.activeProfileID,
                    canAdd: viewModel.canAddAssistantProfile,
                    select: { viewModel.selectAssistantProfile($0) },
                    add: { viewModel.addAssistantProfile(for: $0) },
                    addBlank: { viewModel.addBlankAssistantProfile() },
                    rename: { viewModel.renameAssistantProfile($0, to: $1) },
                    duplicate: { viewModel.duplicateAssistantProfile($0) },
                    delete: { viewModel.deleteAssistantProfile($0) },
                    useService: { viewModel.useAssistantService($0) }
                ),
                assistantAPIKey: viewModel.assistantKeyForCurrentAddress ?? "",
                assistantKeyNote: viewModel.assistantKeyNote,
                onSaveAssistantKey: { key in
                    viewModel.saveAssistantKey(key)
                },
                models: LocalModelPresentation(
                    isReachable: viewModel.isOllamaReachable,
                    installed: viewModel.installedModels,
                    downloading: viewModel.pullingModel,
                    progress: viewModel.pullProgress,
                    message: viewModel.modelMessage,
                    onDownload: { viewModel.downloadModel($0) },
                    onDelete: { viewModel.deleteModel($0) },
                    onUse: { viewModel.useModel($0) },
                    onCancelDownload: { viewModel.cancelModelDownload() },
                    onRefresh: {
                        Task { await viewModel.refreshLocalModels() }
                    }
                ),
                updates: UpdatePresentation(
                    runningVersion: viewModel.runningVersionDescription,
                    isChecking: viewModel.isCheckingForUpdate,
                    available: viewModel.availableUpdate,
                    announcement: viewModel.updateCheckAnnouncement,
                    onCheck: { viewModel.checkForUpdateManually() }
                )
            )
        } else {
            assignmentDashboard
        }
    }


    /// Turns a thrown schedule-validation error into a message the reminder
    /// editor can show inline, rather than a modal alert for a typo.
    private func reminderFailure(_ work: () throws -> Void) -> String? {
        do {
            try work()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    private var assignmentDashboard: some View {
        VStack(alignment: .leading, spacing: 0) {
            dashboardHeader

            if viewModel.isLoading {
                ProgressView("Loading assignments…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                if [.upcoming, .calendar].contains(viewModel.sidebarSelection ?? .upcoming),
                   let nearest = viewModel.nearestAssignment {
                    // The full card is worth its height above a list. Above a
                    // month grid it repeats what the grid already shows and
                    // pushes the weeks off the window, so it shrinks to a line.
                    if (viewModel.sidebarSelection ?? .upcoming) == .calendar {
                        compactNextDeadline(nearest)
                    } else {
                        // The card needs room for its count, its title and a
                        // date on one line. In a column narrower than that it
                        // folded its text a few letters to a line and grew
                        // tall enough to push the list off the window, so
                        // there it becomes the one-line version instead.
                        ViewThatFits(in: .horizontal) {
                            NearestAssignmentCard(
                                item: nearest,
                                now: viewModel.currentDate
                            )
                                .frame(
                                    minWidth: 420,
                                    idealWidth: 420,
                                    maxWidth: .infinity
                                )
                                .padding(.horizontal, 22)
                                .padding(.bottom, 14)

                            compactNextDeadline(nearest)
                        }
                    }
                }

                if (viewModel.sidebarSelection ?? .upcoming) == .calendar {
                    AssignmentCalendarView(viewModel: viewModel) { item in
                        viewModel.presentEditor(for: item)
                    }
                } else {
                    assignmentList
                }
            }
        }
        .navigationTitle("Canvas Countdown")
        .toolbar {
            // One layout, always. Search used to replace the whole row with a
            // field and a Cancel button, so the six things people came for
            // disappeared the moment they went looking for something; it is a
            // panel over the window now and takes nothing away.
            Group {
                ToolbarItem(id: "action.courseFilter", placement: .primaryAction) {
                    courseFilter
                }
                ToolbarItem(id: "action.refresh", placement: .primaryAction) {
                    toolbarButton(
                        systemImage: "arrow.clockwise",
                        title: "Refresh",
                        description: "Refresh Canvas feed"
                    ) {
                        viewModel.refreshManually()
                    }
                    .disabled(
                        viewModel.isRefreshing || !viewModel.hasConfiguredFeed
                    )
                }
                ToolbarItem(id: "action.add", placement: .primaryAction) {
                    addMenu
                }
                ToolbarItem(id: "action.settings", placement: .primaryAction) {
                    toolbarButton(
                        systemImage: "gearshape",
                        title: "Settings",
                        description: "Settings"
                    ) {
                        viewModel.sidebarSelection = .settings
                    }
                }
                ToolbarItem(id: "action.assistant", placement: .primaryAction) {
                    toolbarButton(
                        systemImage: "sparkles",
                        title: "Assistant",
                        description: viewModel.isAssistantOpen
                            ? "Hide the assistant"
                            : "Show the assistant"
                    ) {
                        viewModel.toggleAssistant()
                    }
                    .keyboardShortcut("a", modifiers: [.command, .option])
                    // Anchored to the button it came from, rather than
                    // appearing as an unrelated window in the middle.
                    .popover(
                        isPresented: Binding(
                            get: { viewModel.assistantPresentation == .popover },
                            set: { shown in
                                if !shown, viewModel.assistantPresentation == .popover {
                                    viewModel.closeAssistant()
                                }
                            }
                        ),
                        arrowEdge: .bottom
                    ) {
                        assistantPanel
                            .frame(width: 400)
                            .frame(minHeight: 320, maxHeight: 560)
                    }
                }
                ToolbarItem(id: "action.search", placement: .primaryAction) {
                    toolbarButton(
                        systemImage: "magnifyingglass",
                        title: "Search",
                        description: "Search assignments or courses"
                    ) {
                        enterSearchMode()
                    }
                }
            }
        }
    }

    /// The three ways to get an event in, gathered under the one control a
    /// person actually reaches for.
    ///
    /// Screenshot import used to live only in the File menu, where nobody found
    /// it. A menu here costs no toolbar width, so it cannot bring back the
    /// crowding that pushed items into the overflow.
    private var addMenu: some View {
        Menu {
            Button {
                viewModel.prepareForToolbarAction()
                viewModel.presentNewManualEvent()
            } label: {
                Label("Add Manual Event…", systemImage: "calendar.badge.plus")
            }
            .keyboardShortcut("n", modifiers: .command)

            Divider()

            Button {
                viewModel.prepareForToolbarAction()
                viewModel.presentFeedImport()
            } label: {
                Label("Import Canvas Feed…", systemImage: "link")
            }

            Button {
                viewModel.presentScreenshotImport()
            } label: {
                Label("Import from Screenshot…", systemImage: "text.viewfinder")
            }
            .keyboardShortcut("i", modifiers: [.command, .shift])
        } label: {
            Label("Add", systemImage: "plus")
        }
        .menuIndicator(.hidden)
        .help("Add an event, or import from Canvas")
        .accessibilityLabel("Add or import events")
    }

    @ViewBuilder
    private var updateBanner: some View {
        if let release = viewModel.availableUpdate {
            UpdateBannerView(
                release: release,
                isDownloading: viewModel.isDownloadingUpdate,
                message: viewModel.updateMessage,
                onDownload: {
                    Task { await viewModel.downloadUpdate() }
                },
                onReadMore: {
                    viewModel.openReleasePage()
                },
                onDismiss: {
                    viewModel.dismissUpdate()
                }
            )
            .transition(.move(edge: .top).combined(with: .opacity))
        }
    }

    private var assistantPanel: some View {
        AssistantPanelView(viewModel: viewModel) { drafts in
            await viewModel.saveAssistantDrafts(drafts)
        }
    }

    /// Opens or closes the detached window to match the state, leaving the main
    /// window exactly where it is either way.
    private func syncAssistantWindow(_ shown: Bool) {
        if shown {
            assistantWindow.show(
                onClose: { viewModel.closeAssistant() }
            ) {
                AssistantPanelView(viewModel: viewModel) { drafts in
                    await viewModel.saveAssistantDrafts(drafts)
                }
            }
        } else {
            assistantWindow.close()
        }
    }

    private func enterSearchMode() {
        viewModel.presentSearch()
    }

    private func exitSearchMode() {
        viewModel.dismissSearch()
    }

    /// A toolbar control with a short visible title and a fuller description.
    ///
    /// The title is what "Icon and Text" shows, so it is kept to one word: long
    /// titles are what made that mode stretch the row. The description stays the
    /// tooltip and the VoiceOver label.
    private func toolbarButton(
        systemImage: String,
        title: String,
        description: String,
        action: @escaping () -> Void
    ) -> some View {
        Button {
            // Give up search focus first, then act, so one click does both.
            viewModel.prepareForToolbarAction()
            action()
        } label: {
            Label(title, systemImage: systemImage)
        }
        .help(description)
        .accessibilityLabel(description)
    }

    private func compactNextDeadline(_ item: AssignmentListItem) -> some View {
        let days = item.remainingDays(relativeTo: viewModel.currentDate)
        return HStack(spacing: 8) {
            Text("\(days)")
                .font(.title3.weight(.bold))
                .monospacedDigit()
            Text(days == 1 ? "day left" : "days left")
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .fixedSize()
            Text("·")
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text(item.title)
                .lineLimit(1)
            Spacer(minLength: 0)
        }
        .font(.callout)
        .padding(.horizontal, 22)
        .padding(.bottom, 12)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(
            days == 1
                ? "Next deadline, \(item.title), 1 day left"
                : "Next deadline, \(item.title), \(days) days left"
        )
    }

    /// The title with when the list was last refreshed beside it, or under it
    /// when the column is too narrow to hold both on a line.
    ///
    /// They used to share one row whatever the width. Squeezed, the row gave
    /// the title whatever was left over and the title wrapped to fit, down to
    /// "Can-", "vas", "Coun", "tdow", "n". The title stays on one line now and
    /// gets smaller before it is cut short.
    private var dashboardHeader: some View {
        ViewThatFits(in: .horizontal) {
            HStack(alignment: .firstTextBaseline) {
                dashboardTitle
                Spacer(minLength: 12)
                refreshStatus
            }

            VStack(alignment: .leading, spacing: 4) {
                dashboardTitle
                refreshStatus
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 22)
        .padding(.top, 18)
        .padding(.bottom, 16)
    }

    private var dashboardTitle: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("Canvas Countdown")
                .font(.largeTitle.weight(.semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text((viewModel.sidebarSelection ?? .upcoming).title)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    @ViewBuilder
    private var refreshStatus: some View {
        if viewModel.isRefreshing {
            ProgressView()
                .controlSize(.small)
                .accessibilityLabel("Refreshing Canvas calendar")
        } else if let failure = viewModel.refreshFailureDescription {
            // A refresh the app ran by itself no longer interrupts with an
            // alert, so it says here that the list may be out of date, and
            // why on hover.
            HStack(spacing: 6) {
                Label("Couldn’t refresh", systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .lineLimit(1)
                    .help(failure)
                Button("Try Again") {
                    viewModel.refreshManually()
                }
                .buttonStyle(.link)
                .font(.caption)
                .fixedSize()
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Couldn’t refresh the Canvas calendar. \(failure)")
        } else if let lastRefreshDate = viewModel.lastRefreshDate {
            // Worked out again every minute. Written once, "Updated 2 minutes
            // ago" went on saying so for as long as the window stayed open.
            TimelineView(.everyMinute) { _ in
                Text("Updated \(lastRefreshDate, format: .relative(presentation: .named))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .accessibilityLabel(
                        "Last updated \(lastRefreshDate.formatted(date: .abbreviated, time: .shortened))"
                    )
            }
        }
    }

    @ViewBuilder
    private var assignmentList: some View {
        if viewModel.filteredAssignments.isEmpty {
            // Measured here because the empty state cannot measure itself: it
            // offers its buttons a fixed reading width whatever the column is,
            // so nothing inside it can tell a wide column from a narrow one.
            GeometryReader { proxy in
                emptyState(
                    stackingActions: proxy.size.width
                        < Self.widthForEmptyStateActionsInARow
                )
                .frame(width: proxy.size.width, height: proxy.size.height)
            }
        } else {
            List(
                viewModel.filteredAssignments,
                selection: $viewModel.selectedEventID
            ) { item in
                HStack(spacing: 8) {
                    AssignmentRowView(
                        item: item,
                        now: viewModel.currentDate,
                        label: viewModel.label(for: item)
                    )

                    Spacer(minLength: 0)

                    // The whole row opens the editor, so the row's own tap
                    // handler stops here: the menu must not also edit.
                    Menu {
                        eventActions(for: item)
                    } label: {
                        Image(systemName: "ellipsis.circle")
                            .accessibilityLabel("Actions for \(item.title)")
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .help("Quick actions")
                }
                .contentShape(Rectangle())
                .padding(.vertical, 2)
                .background {
                    if hoveredEventID == item.id {
                        RoundedRectangle(cornerRadius: 6)
                            .fill(Color.primary.opacity(0.06))
                    }
                }
                .onHover { isInside in
                    hoveredEventID = isInside ? item.id : nil
                }
                // A single click on the row opens it. `onTapGesture` does not
                // fire while a scroll is in progress, so scrolling cannot open
                // an editor by accident.
                .onTapGesture {
                    viewModel.selectedEventID = item.id
                    viewModel.presentEditor(for: item)
                }
                .contextMenu {
                    eventActions(for: item)
                }
                .accessibilityHint(
                    item.isUserOwned
                        ? "Opens the event editor"
                        : "Opens details for this Canvas assignment"
                )
                .tag(item.id)
            }
            .listStyle(.inset)
            .accessibilityLabel(
                (viewModel.sidebarSelection ?? .upcoming).title
            )
            .onKeyPress(.return) {
                guard viewModel.selectedItem != nil else {
                    return .ignored
                }
                viewModel.openSelectedEvent()
                return .handled
            }
            .onDeleteCommand {
                eventPendingDeletion = viewModel.deletionCandidate()
            }
        }
    }

    /// What a column has to be before three buttons fit across it.
    ///
    /// The empty state gives each of its buttons the width of the longest
    /// title and sets them side by side, 525 points in all, without asking
    /// whether there is room. In a narrower column the row ran off both sides
    /// and the first and last buttons were cut in half.
    private static let widthForEmptyStateActionsInARow: CGFloat = 560

    @ViewBuilder
    private func emptyState(stackingActions: Bool) -> some View {
        if viewModel.selectedCourse != nil {
            ContentUnavailableView(
                "No events in this course",
                systemImage: "line.3.horizontal.decrease.circle",
                description: Text("Choose another course from Filter, or All Courses to see everything.")
            )
        } else {
            switch viewModel.sidebarSelection ?? .upcoming {
            case .upcoming:
                ContentUnavailableView {
                    Label(
                        "No upcoming assignments",
                        systemImage: "calendar.badge.checkmark"
                    )
                } description: {
                    Text(
                        viewModel.hasConfiguredFeed
                            ? "You’re caught up. Refresh to check Canvas for changes."
                            : "Connect a Canvas calendar feed or add a manual event."
                    )
                } actions: {
                    // In a row while they fit, and one above the other when
                    // they do not.
                    if stackingActions {
                        VStack(spacing: 8) {
                            emptyStateActions
                        }
                    } else {
                        emptyStateActions
                    }
                }
            case .allEvents, .calendar:
                ContentUnavailableView(
                    "No events",
                    systemImage: "calendar",
                    description: Text("Imported and manual events appear here.")
                )
            case .completed:
                ContentUnavailableView(
                    "No completed assignments",
                    systemImage: "checkmark.circle",
                    description: Text("Mark an assignment complete from its actions menu.")
                )
            case .settings:
                EmptyView()
            }
        }
    }

    @ViewBuilder
    private var emptyStateActions: some View {
        if viewModel.hasConfiguredFeed {
            Button("Refresh Now") {
                viewModel.refreshManually()
            }
        } else {
            Button("Connect Canvas…") {
                viewModel.presentFeedImport(onboarding: true)
            }
        }
        Button("Add Manual Event") {
            viewModel.presentNewManualEvent()
        }
        Button("Import from Screenshot…") {
            viewModel.presentScreenshotImport()
        }
    }

    /// One flat native menu. A `Picker` here would render as a labelled
    /// submenu, which pushed the course list out sideways.
    ///
    /// Completed and ignored events sit at the top of it. They used to be an eye
    /// in the toolbar whose title flipped between "Hide" and "Show" — two words
    /// that named neither the state it was in nor the thing it acted on. Written
    /// out here, beside the course rules, it reads as what it always was: a
    /// filter.
    private var courseFilter: some View {
        Menu {
            Toggle(
                "Show completed and ignored",
                isOn: $viewModel.showCompletedAndIgnored
            )
            .help("Keep finished and ignored events in the list")

            Divider()

            courseFilterItem(for: nil)

            if !viewModel.availableCourses.isEmpty {
                Divider()

                ForEach(viewModel.availableCourses, id: \.self) { course in
                    courseFilterItem(for: course)
                }
            }
        } label: {
            // A one-word title, never the selected course: a long Canvas course
            // name here would set this control's width and push the rest apart.
            // The fill is the only thing left saying a filter is on, now that
            // both rules live behind this one label.
            Label(
                "Filter",
                systemImage: viewModel.isFilterActive
                    ? "line.3.horizontal.decrease.circle.fill"
                    : "line.3.horizontal.decrease.circle"
            )
        }
        .menuIndicator(.hidden)
        .help(viewModel.courseFilterDescription)
        .accessibilityLabel(viewModel.courseFilterDescription)
    }

    /// A `Toggle` gives the native checkmark, keyboard handling and VoiceOver
    /// state for free; a plain button would need all three rebuilt by hand.
    private func courseFilterItem(for course: String?) -> some View {
        Toggle(
            isOn: Binding(
                get: { viewModel.selectedCourse == course },
                set: { isSelected in
                    if isSelected {
                        viewModel.selectCourse(course)
                    }
                }
            )
        ) {
            Text(MainViewModel.menuTitle(for: course))
        }
        .help(course ?? "Show assignments from every course")
        .accessibilityLabel(course ?? "All Courses")
    }

    @ViewBuilder
    private func eventActions(for item: AssignmentListItem) -> some View {
        Button {
            viewModel.toggleCompleted(item)
        } label: {
            Label(
                item.isCompleted ? "Mark Incomplete" : "Mark Complete",
                systemImage: item.isCompleted
                    ? "arrow.uturn.backward.circle"
                    : "checkmark.circle"
            )
        }

        // Not for finished or ignored work. Whether something is done is
        // never sent to the model, so asked about it, all it could say is
        // that it was late.
        if !item.isCompleted, !item.isIgnored {
            Button {
                viewModel.openAssistant(about: item)
            } label: {
                Label("Ask the Assistant…", systemImage: "sparkles")
            }
        }

        Button {
            viewModel.toggleIgnored(item)
        } label: {
            Label(
                item.isIgnored ? "Stop Ignoring" : "Ignore",
                systemImage: item.isIgnored ? "eye" : "eye.slash"
            )
        }

        labelMenu(for: item)

        Divider()

        Button {
            viewModel.presentEditor(for: item)
        } label: {
            Label(item.isUserOwned ? "Edit…" : "Details…", systemImage: "pencil")
        }

        if item.isUserOwned {

            Button(role: .destructive) {
                eventPendingDeletion = item
            } label: {
                Label("Delete…", systemImage: "trash")
            }
        }
    }

    /// One list with None at the top: assigning and clearing are the same
    /// action, so they are not two menu items.
    private func labelMenu(for item: AssignmentListItem) -> some View {
        Menu {
            Button {
                viewModel.setLabel(nil, for: item)
            } label: {
                if item.labelID == nil {
                    Label("None", systemImage: "checkmark")
                } else {
                    Text("None")
                }
            }

            Divider()

            ForEach(viewModel.eventLabels) { label in
                Button {
                    viewModel.setLabel(label.id, for: item)
                } label: {
                    if item.labelID == label.id {
                        Label(label.name, systemImage: "checkmark")
                    } else {
                        Text(label.name)
                    }
                }
            }
        } label: {
            Label("Label", systemImage: "tag")
        }
        .disabled(viewModel.eventLabels.isEmpty)
    }

    /// What the assistant just added, with a way to take all of it back.
    ///
    /// Deliberately does not time out. A model asked for several tasks can get
    /// several of them wrong at once, and an undo that vanishes after two
    /// seconds leaves the same tidying job it was meant to prevent.
    private func statusBanner(_ message: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .accessibilityHidden(true)
            Text(message)
                .lineLimit(2)
            Spacer()
            Button {
                viewModel.clearStatus()
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .accessibilityLabel("Dismiss")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.bar)
        .overlay(alignment: .top) {
            Divider()
        }
        .accessibilityElement(children: .combine)
    }
}

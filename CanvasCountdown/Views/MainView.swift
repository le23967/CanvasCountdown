import AppKit
import SwiftUI

struct MainView: View {
    @Bindable var viewModel: MainViewModel
    let screenshotImportViewModel: ScreenshotImportViewModel

    @State private var eventPendingDeletion: AssignmentListItem?
    @State private var hoveredEventID: UUID?
    @FocusState private var isSearchFieldFocused: Bool
    @State private var assistantWindow = AssistantWindowController()
    @State private var draggedSidebarWidth: CGFloat?
    @State private var sidebarWidthAtDragStart: CGFloat?
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
                .frame(minHeight: 420)
        }
        .task {
            await viewModel.start()
        }
        .background(ToolbarDisplayModeConfigurator())
        .onChange(of: viewModel.assistantPresentation) { _, presentation in
            syncAssistantWindow(presentation == .separateWindow)
        }
        // The field's focus and the view model's copy of it are kept in step so
        // any part of the app can release focus without reaching into the view.
        .onChange(of: isSearchFieldFocused) { _, isFocused in
            viewModel.isSearchFieldFocused = isFocused
        }
        .onChange(of: viewModel.isSearchFieldFocused) { _, isFocused in
            if isSearchFieldFocused != isFocused {
                isSearchFieldFocused = isFocused
            }
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
            "Delete manual event?",
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
        .safeAreaInset(edge: .bottom) {
            // The undo offer takes precedence: it is the one banner that is
            // worth reading, so it must not be pushed aside by a routine
            // "Settings updated".
            if let undoable = viewModel.undoableAssistantImport {
                undoBanner(undoable)
            } else if let statusMessage = viewModel.statusMessage {
                statusBanner(statusMessage)
            }
        }
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
    private var detailWithAssistant: some View {
        GeometryReader { proxy in
            let panel = panelWidth(inColumnOf: proxy.size.width)

            HStack(spacing: 0) {
                detail
                    // Leading, so what is cut off in the very narrowest window
                    // is the trailing edge rather than both edges at once.
                    .frame(
                        width: max(0, proxy.size.width - panel),
                        alignment: .leading
                    )
                    .clipped()

                if isAssistantSidebarShown {
                    assistantResizeDivider
                    assistantPanel
                        .frame(width: max(0, panel - assistantDividerWidth))
                        .transition(.move(edge: .trailing).combined(with: .opacity))
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
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

    /// What the panel and its divider take from the column, which is never more
    /// than the column has. Nothing is asked of the window.
    private func panelWidth(inColumnOf columnWidth: CGFloat) -> CGFloat {
        guard isAssistantSidebarShown else {
            return 0
        }
        let fitted = AssistantLayout.fittedSidebarWidth(
            preferred: draggedSidebarWidth ?? viewModel.preferredSidebarWidth,
            availableWidth: columnWidth
        )
        return min(columnWidth, fitted + assistantDividerWidth)
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
                        if isInside {
                            NSCursor.resizeLeftRight.push()
                        } else {
                            NSCursor.pop()
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
                assistantAPIKey: viewModel.assistantAPIKey,
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
                        NearestAssignmentCard(
                            item: nearest,
                            now: viewModel.currentDate
                        )
                            .padding(.horizontal, 22)
                            .padding(.bottom, 14)
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
        // A click anywhere in the content releases search focus. Simultaneous
        // rather than exclusive, so the click still reaches the row, menu or
        // button underneath it.
        .dismissesSearchOnContentClick(
            isActive: viewModel.isSearchModeActive
        ) {
            exitSearchMode()
        }
        .toolbar {
            // Two complete layouts chosen by one switch. Each item carries a
            // stable id: without them SwiftUI could not tell which item became
            // which when the mode flipped, and it removed the old items without
            // installing the new ones.
            if viewModel.isSearchModeActive {
                ToolbarItem(id: "search.field", placement: .primaryAction) {
                    toolbarSearchField
                }
                ToolbarItem(id: "search.cancel", placement: .primaryAction) {
                    cancelSearchButton
                }
            } else {
                ToolbarItem(id: "action.courseFilter", placement: .primaryAction) {
                    courseFilter
                }
                ToolbarItem(id: "action.visibility", placement: .primaryAction) {
                    toolbarButton(
                        systemImage: viewModel.showCompletedAndIgnored
                            ? "eye"
                            : "eye.slash",
                        title: viewModel.showCompletedAndIgnored ? "Hide" : "Show",
                        description: viewModel.showCompletedAndIgnored
                            ? "Hide completed and ignored"
                            : "Show completed and ignored"
                    ) {
                        viewModel.showCompletedAndIgnored.toggle()
                    }
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
                if viewModel.showsAssistantModelSwitcher {
                    ToolbarItem(id: "action.model", placement: .primaryAction) {
                        modelSwitcher
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

    /// Switches which saved model the assistant uses, without a trip to
    /// Settings. Only appears once there is more than one to switch between.
    private var modelSwitcher: some View {
        Menu {
            ForEach(viewModel.assistantProfiles) { profile in
                Button {
                    viewModel.prepareForToolbarAction()
                    viewModel.selectAssistantProfile(profile.id)
                } label: {
                    if profile.id == viewModel.assistantSettings.activeProfileID {
                        Label(profile.name, systemImage: "checkmark")
                    } else {
                        Text(profile.name)
                    }
                }
                .help(profile.subtitle)
            }

            Divider()

            Button("Manage Models…") {
                viewModel.prepareForToolbarAction()
                viewModel.sidebarSelection = .settings
            }
        } label: {
            Label("Model", systemImage: "cpu")
        }
        .menuIndicator(.hidden)
        .help(viewModel.assistantModelDescription)
        .accessibilityLabel(viewModel.assistantModelDescription)
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

    /// The search field, shown only in search mode.
    ///
    /// It shares the toolbar with nothing but Cancel, so unlike the earlier
    /// attempt to sit it beside the five ordinary actions, there is room for it
    /// and macOS has no reason to move it into the overflow menu.
    private var toolbarSearchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)

            TextField(
                "Search assignments or courses",
                text: $viewModel.searchText
            )
            .textFieldStyle(.plain)
            .focused($isSearchFieldFocused)
            .onSubmit {
                // Return must not trap focus in the field.
                isSearchFieldFocused = false
            }
            .accessibilityLabel("Search assignments or courses")

            if !viewModel.searchText.isEmpty {
                Button {
                    viewModel.clearSearchQuery()
                    isSearchFieldFocused = true
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Clear search")
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 7)
        .padding(.vertical, 3)
        .background(
            .quaternary.opacity(0.5),
            in: RoundedRectangle(cornerRadius: 6)
        )
        // A small minimum so the field gives up width first. Cancel is the last
        // item, and the last item is what macOS pushes into the overflow menu,
        // so the field must be the one that shrinks in a narrow window.
        .frame(minWidth: 90, idealWidth: 280, maxWidth: 340)
        .onExitCommand {
            exitSearchMode()
        }
    }

    private var cancelSearchButton: some View {
        Button("Cancel") {
            exitSearchMode()
        }
        .help("Close search")
        .accessibilityLabel("Close search")
    }

    /// Short and flat, and skipped entirely under Reduce Motion.
    private var searchAnimation: Animation? {
        reduceMotion ? nil : .easeInOut(duration: 0.18)
    }

    private func enterSearchMode() {
        withAnimation(searchAnimation) {
            viewModel.presentSearch()
        }
    }

    private func exitSearchMode() {
        withAnimation(searchAnimation) {
            viewModel.dismissSearch()
        }
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
        HStack(spacing: 8) {
            Text("\(item.remainingDays(relativeTo: viewModel.currentDate))")
                .font(.title3.weight(.bold))
                .monospacedDigit()
            Text("days left")
                .foregroundStyle(.secondary)
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
            "Next deadline, \(item.title), \(item.remainingDays(relativeTo: viewModel.currentDate)) days left"
        )
    }

    private var dashboardHeader: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Canvas Countdown")
                    .font(.largeTitle.weight(.semibold))
                Text((viewModel.sidebarSelection ?? .upcoming).title)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            if viewModel.isRefreshing {
                ProgressView()
                    .controlSize(.small)
                    .accessibilityLabel("Refreshing Canvas calendar")
            } else if let lastRefreshDate = viewModel.lastRefreshDate {
                Text("Updated \(lastRefreshDate, format: .relative(presentation: .named))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel(
                        "Last updated \(lastRefreshDate.formatted(date: .abbreviated, time: .shortened))"
                    )
            }
        }
        .padding(.horizontal, 22)
        .padding(.top, 18)
        .padding(.bottom, 16)
    }

    @ViewBuilder
    private var assignmentList: some View {
        if viewModel.filteredAssignments.isEmpty {
            emptyState
                .frame(maxWidth: .infinity, maxHeight: .infinity)
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
                    item.isManual
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

    @ViewBuilder
    private var emptyState: some View {
        if !viewModel.searchText.isEmpty || viewModel.selectedCourse != nil {
            ContentUnavailableView.search(text: viewModel.searchText)
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

    /// One flat native menu. A `Picker` here would render as a labelled
    /// submenu, which pushed the course list out sideways.
    private var courseFilter: some View {
        Menu {
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
            Label(
                "Filter",
                systemImage: viewModel.selectedCourse == nil
                    ? "line.3.horizontal.decrease.circle"
                    : "line.3.horizontal.decrease.circle.fill"
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

        Button {
            viewModel.openAssistant(about: item)
        } label: {
            Label("Ask the Assistant…", systemImage: "sparkles")
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
            Label(item.isManual ? "Edit…" : "Details…", systemImage: "pencil")
        }

        if item.isManual {

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
    private func undoBanner(
        _ batch: MainViewModel.UndoableAssistantImport
    ) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "sparkles")
                .foregroundStyle(AssistantStyle.accent)
                .accessibilityHidden(true)
            Text(batch.message)
                .lineLimit(1)
            Spacer()
            Button("Undo") {
                Task { await viewModel.undoAssistantImport() }
            }
            .keyboardShortcut("z", modifiers: .command)
            .help("Remove everything the assistant just added")
            Button {
                viewModel.dismissUndoableAssistantImport()
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .accessibilityLabel("Keep them")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.bar)
        .overlay(alignment: .top) {
            Divider()
        }
    }

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

import SwiftUI

struct MainView: View {
    @Bindable var viewModel: MainViewModel

    @State private var eventPendingDeletion: AssignmentListItem?
    @State private var hoveredEventID: UUID?
    @FocusState private var isSearchFieldFocused: Bool

    var body: some View {
        NavigationSplitView {
            SidebarView(
                selection: $viewModel.sidebarSelection,
                upcomingCount: viewModel.upcomingCount
            )
            .navigationSplitViewColumnWidth(min: 185, ideal: 220, max: 280)
        } detail: {
            detail
                .frame(minWidth: 600, minHeight: 500)
        }
        .task {
            await viewModel.start()
        }
        .background(ToolbarDisplayModeConfigurator())
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
            if let statusMessage = viewModel.statusMessage {
                statusBanner(statusMessage)
            }
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
                onApplyDockTheme: { preset in
                    viewModel.applyDockTheme(preset)
                },
                onResetDockAppearance: {
                    viewModel.resetDockAppearance()
                }
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
                if (viewModel.sidebarSelection ?? .upcoming) == .upcoming,
                   let nearest = viewModel.nearestAssignment {
                    NearestAssignmentCard(
                        item: nearest,
                        now: viewModel.currentDate
                    )
                        .padding(.horizontal, 22)
                        .padding(.bottom, 14)
                }

                assignmentList
            }
        }
        .navigationTitle("Canvas Countdown")
        // A click anywhere in the content releases search focus. Simultaneous
        // rather than exclusive, so the click still reaches the row, menu or
        // button underneath it.
        .simultaneousGesture(
            TapGesture().onEnded {
                viewModel.dismissSearchFocus()
            }
        )
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                courseFilter

                toolbarButton(
                    systemImage: viewModel.showCompletedAndIgnored
                        ? "eye"
                        : "eye.slash",
                    title: viewModel.showCompletedAndIgnored
                        ? "Hide completed and ignored"
                        : "Show completed and ignored"
                ) {
                    viewModel.showCompletedAndIgnored.toggle()
                }

                toolbarButton(
                    systemImage: "arrow.clockwise",
                    title: "Refresh Canvas feed"
                ) {
                    viewModel.refreshManually()
                }
                .disabled(
                    viewModel.isRefreshing || !viewModel.hasConfiguredFeed
                )

                toolbarButton(
                    systemImage: "plus",
                    title: "Add manual event"
                ) {
                    viewModel.presentNewManualEvent()
                }
                .keyboardShortcut("n", modifiers: .command)

                toolbarButton(
                    systemImage: "gearshape",
                    title: "Settings"
                ) {
                    viewModel.sidebarSelection = .settings
                }

                searchControl
            }
        }
    }

    /// Icon-only by construction: the label is an image, so no toolbar display
    /// mode can introduce a text label and reflow the row.
    private func toolbarButton(
        systemImage: String,
        title: String,
        action: @escaping () -> Void
    ) -> some View {
        Button {
            // Give up search focus first, then act, so one click does both.
            viewModel.prepareForToolbarAction()
            action()
        } label: {
            Image(systemName: systemImage)
        }
        .help(title)
        .accessibilityLabel(title)
    }

    @ViewBuilder
    private var searchControl: some View {
        if viewModel.isSearchPresented {
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
            .padding(.vertical, 4)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
            .frame(minWidth: 220, idealWidth: 300, maxWidth: 360)
            .onExitCommand {
                viewModel.handleSearchEscape()
            }
        } else {
            toolbarButton(
                systemImage: "magnifyingglass",
                title: "Search assignments or courses"
            ) {
                viewModel.presentSearch()
            }
        }
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
                        now: viewModel.currentDate
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
                }
            case .allEvents:
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
            // Icon only: a selected Canvas course title would otherwise set the
            // width of this control and push the rest of the toolbar apart.
            Image(systemName: viewModel.selectedCourse == nil
                ? "line.3.horizontal.decrease.circle"
                : "line.3.horizontal.decrease.circle.fill")
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
            viewModel.toggleIgnored(item)
        } label: {
            Label(
                item.isIgnored ? "Stop Ignoring" : "Ignore",
                systemImage: item.isIgnored ? "eye" : "eye.slash"
            )
        }

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

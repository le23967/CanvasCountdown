import SwiftUI

struct MainView: View {
    @Bindable var viewModel: MainViewModel

    @State private var eventPendingDeletion: AssignmentListItem?

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
        .sheet(isPresented: $viewModel.isShowingManualEditor) {
            ManualEventEditorView(draft: viewModel.manualEventDraft) { draft in
                try await viewModel.saveManualEvent(draft)
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
        .searchable(
            text: $viewModel.searchText,
            placement: .toolbar,
            prompt: "Search assignments or courses"
        )
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                courseFilter

                Toggle(isOn: $viewModel.showCompletedAndIgnored) {
                    Label(
                        "Show completed and ignored",
                        systemImage: viewModel.showCompletedAndIgnored
                            ? "eye"
                            : "eye.slash"
                    )
                }
                .toggleStyle(.button)
                .help(
                    viewModel.showCompletedAndIgnored
                        ? "Hide completed and ignored events"
                        : "Show completed and ignored events"
                )
                .accessibilityLabel(
                    viewModel.showCompletedAndIgnored
                        ? "Hide completed and ignored events"
                        : "Show completed and ignored events"
                )

                Button {
                    viewModel.refreshManually()
                } label: {
                    Label("Refresh Canvas feed", systemImage: "arrow.clockwise")
                }
                .disabled(
                    viewModel.isRefreshing || !viewModel.hasConfiguredFeed
                )
                .help("Refresh Canvas feed")

                Button {
                    viewModel.presentNewManualEvent()
                } label: {
                    Label("Add manual event", systemImage: "plus")
                }
                .keyboardShortcut("n", modifiers: .command)
                .help("Add manual event")

                Button {
                    viewModel.sidebarSelection = .settings
                } label: {
                    Label("Settings", systemImage: "gearshape")
                }
                .help("Settings")
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
            List(viewModel.filteredAssignments) { item in
                HStack(spacing: 8) {
                    AssignmentRowView(
                        item: item,
                        now: viewModel.currentDate
                    )

                    Spacer(minLength: 0)

                    Menu {
                        eventActions(for: item)
                    } label: {
                        Image(systemName: "ellipsis.circle")
                            .accessibilityLabel("Actions for \(item.title)")
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                }
                .contextMenu {
                    eventActions(for: item)
                }
            }
            .listStyle(.inset)
            .accessibilityLabel(
                (viewModel.sidebarSelection ?? .upcoming).title
            )
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

    private var courseFilter: some View {
        Menu {
            Picker("Course", selection: $viewModel.selectedCourse) {
                Text("All Courses").tag(nil as String?)
                ForEach(viewModel.availableCourses, id: \.self) { course in
                    Text(course).tag(course as String?)
                }
            }
        } label: {
            Label(
                viewModel.selectedCourse ?? "All Courses",
                systemImage: "line.3.horizontal.decrease.circle"
            )
        }
        .help("Filter by course")
        .accessibilityLabel(
            "Course filter, \(viewModel.selectedCourse ?? "all courses")"
        )
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

        if item.isManual {
            Divider()

            Button {
                viewModel.presentEditor(for: item)
            } label: {
                Label("Edit…", systemImage: "pencil")
            }

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

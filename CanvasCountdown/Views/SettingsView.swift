import AppKit
import SwiftUI
import UserNotifications

enum NotificationPermissionState: Equatable, Sendable {
    case notDetermined
    case denied
    case authorized

    init(_ status: UNAuthorizationStatus) {
        switch status {
        case .authorized, .provisional, .ephemeral:
            self = .authorized
        case .denied:
            self = .denied
        case .notDetermined:
            self = .notDetermined
        @unknown default:
            self = .notDetermined
        }
    }
}

struct SettingsView: View {
    @Binding var settings: SettingsFormState

    let availableCourses: [String]
    let notificationPermission: NotificationPermissionState
    let onConnectFeed: (String) -> Void
    let onRemoveFeed: @MainActor () async throws -> Void
    let onRequestNotificationPermission: @MainActor () async throws -> Bool
    let onSettingsChanged: @MainActor (SettingsFormState) -> Void
    let onResetData: @MainActor () async throws -> Void
    let onExportDiagnostics: @MainActor () async throws -> String

    @State private var revealFeedURL = false
    @State private var isWorking = false
    @State private var errorMessage: String?
    @State private var showNotificationExplanation = false
    @State private var showRemoveFeedConfirmation = false
    @State private var showResetConfirmation = false
    @State private var diagnosticsDocument: DiagnosticsDocument?
    @State private var showDiagnosticsExporter = false

    var body: some View {
        Form {
            canvasSection
            refreshSection
            notificationSection
            courseFilteringSection
            dockSection
            systemSection
            dataSection
        }
        .formStyle(.grouped)
        .navigationTitle("Settings")
        // Every control writes straight through: there is no save step.
        .onChange(of: settings) { _, updated in
            onSettingsChanged(updated)
        }
        .safeAreaInset(edge: .bottom) {
            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .padding(.horizontal)
                    .padding(.vertical, 10)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.bar)
            }
        }
        .alert("Allow assignment reminders?", isPresented: $showNotificationExplanation) {
            Button("Not Now", role: .cancel) {}
            Button("Continue") {
                requestNotificationPermission()
            }
        } message: {
            Text("Canvas Countdown can alert you before selected due dates. Notification content contains only the assignment and course name, and reminders are scheduled locally on this Mac.")
        }
        .alert("Remove Canvas feed?", isPresented: $showRemoveFeedConfirmation) {
            Button("Cancel", role: .cancel) {}
            Button("Remove Feed", role: .destructive) {
                removeFeed()
            }
        } message: {
            Text("Automatic refresh will stop. Existing imported assignments remain on this Mac.")
        }
        .alert("Reset all local data?", isPresented: $showResetConfirmation) {
            Button("Cancel", role: .cancel) {}
            Button("Reset", role: .destructive) {
                resetData()
            }
        } message: {
            Text("This permanently deletes all imported and manual assignments, local settings, and the saved feed URL.")
        }
        .fileExporter(
            isPresented: $showDiagnosticsExporter,
            document: diagnosticsDocument,
            contentType: .plainText,
            defaultFilename: "Canvas-Countdown-Diagnostics.txt"
        ) { result in
            if case let .failure(error) = result {
                errorMessage = error.localizedDescription
            }
            diagnosticsDocument = nil
        }
        .overlay {
            if isWorking {
                ProgressView()
                    .controlSize(.large)
                    .padding(18)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
                    .accessibilityLabel("Saving settings")
            }
        }
    }

    private var canvasSection: some View {
        Section {
            HStack {
                Group {
                    if revealFeedURL {
                        TextField("Canvas calendar feed URL", text: $settings.feedURL)
                    } else {
                        SecureField("Canvas calendar feed URL", text: $settings.feedURL)
                    }
                }
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("Canvas calendar feed URL")

                Button {
                    revealFeedURL.toggle()
                } label: {
                    Image(systemName: revealFeedURL ? "eye.slash" : "eye")
                }
                .buttonStyle(.borderless)
                .help(revealFeedURL ? "Hide feed URL" : "Reveal feed URL")
                .accessibilityLabel(revealFeedURL ? "Hide feed URL" : "Reveal feed URL")
            }

            HStack {
                Button(settings.feedURL.isEmpty ? "Connect Feed…" : "Preview & Update…") {
                    onConnectFeed(settings.feedURL)
                }
                .disabled(settings.feedURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                if !settings.feedURL.isEmpty {
                    Button("Remove Feed", role: .destructive) {
                        showRemoveFeedConfirmation = true
                    }
                }
            }

            Label(
                "Stored in the macOS Keychain. The URL is never written to app preferences or diagnostics.",
                systemImage: "lock.fill"
            )
            .font(.caption)
            .foregroundStyle(.secondary)
        } header: {
            Text("Canvas Calendar Feed")
        }
    }

    private var refreshSection: some View {
        Section("Automatic Refresh") {
            Picker("Automatic refresh", selection: $settings.refreshInterval) {
                ForEach(RefreshIntervalOption.allCases) { interval in
                    Text(interval.title).tag(interval)
                }
            }
            .pickerStyle(.menu)

            Text("Automatic refresh occurs while Canvas Countdown is running. You can refresh manually at any time.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var notificationSection: some View {
        Section {
            permissionRow

            ForEach([7, 3, 1, 0], id: \.self) { offset in
                Toggle(notificationLabel(offset), isOn: Binding(
                    get: { settings.notificationOffsets.contains(offset) },
                    set: { enabled in
                        if enabled {
                            settings.notificationOffsets.insert(offset)
                        } else {
                            settings.notificationOffsets.remove(offset)
                        }
                    }
                ))
            }
            .disabled(notificationPermission != .authorized)
        } header: {
            Text("Notifications")
        } footer: {
            Text("Reminders are scheduled locally and replaced whenever an imported due date changes, preventing duplicates.")
        }
    }

    @ViewBuilder
    private var permissionRow: some View {
        switch notificationPermission {
        case .authorized:
            Label("Notifications allowed", systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
        case .notDetermined:
            HStack {
                Label("Notifications are optional", systemImage: "bell")
                Spacer()
                Button("Learn & Allow…") {
                    showNotificationExplanation = true
                }
            }
        case .denied:
            HStack {
                Label("Notifications are disabled in System Settings", systemImage: "bell.slash")
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Open System Settings") {
                    openNotificationSettings()
                }
            }
        }
    }

    private var dockSection: some View {
        Section("Dock Appearance") {
            Picker("Header label", selection: $settings.dockLabel) {
                ForEach(DockLabelOption.allCases) { option in
                    Text("\(option.title) — \(option.renderedLabel)").tag(option)
                }
            }
            .pickerStyle(.radioGroup)
        }
    }

    private var courseFilteringSection: some View {
        Section {
            Picker("Include", selection: $settings.dockCourseScope) {
                ForEach(DockCourseScopeOption.allCases) { option in
                    Text(option.title).tag(option)
                }
            }
            .pickerStyle(.radioGroup)

            if settings.dockCourseScope == .selectedCourses {
                if availableCourses.isEmpty {
                    Text("Courses appear here after events are imported.")
                        .foregroundStyle(.secondary)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Included courses")
                            .font(.subheadline.weight(.medium))
                        ForEach(availableCourses, id: \.self) { course in
                            Toggle(course, isOn: Binding(
                                get: { settings.selectedCourses.contains(course) },
                                set: { enabled in
                                    if enabled {
                                        settings.selectedCourses.insert(course)
                                    } else {
                                        settings.selectedCourses.remove(course)
                                    }
                                }
                            ))
                        }
                    }
                }
            }
        } header: {
            Text("Course Filtering")
        } footer: {
            Text("Applies to Upcoming, the nearest deadline card and the Dock countdown. All Events and Completed always show everything, and events without a course are always included.")
        }
    }

    private var systemSection: some View {
        Section("System") {
            Toggle("Launch Canvas Countdown at login", isOn: $settings.launchAtLogin)
            Text("macOS may ask you to approve this under Login Items.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var dataSection: some View {
        Section {
            Button("Export Redacted Diagnostics…") {
                exportDiagnostics()
            }
            Button("Reset Local Data…", role: .destructive) {
                showResetConfirmation = true
            }
        } header: {
            Text("Data & Privacy")
        } footer: {
            Text("Diagnostics include app state and counts, never the private Canvas feed URL or event descriptions.")
        }
    }

    private func notificationLabel(_ offset: Int) -> String {
        switch offset {
        case 0:
            "On the due day"
        case 1:
            "1 day before"
        default:
            "\(offset) days before"
        }
    }

    private func requestNotificationPermission() {
        guard !isWorking else {
            return
        }
        isWorking = true
        errorMessage = nil
        Task {
            do {
                _ = try await onRequestNotificationPermission()
            } catch {
                errorMessage = error.localizedDescription
            }
            isWorking = false
        }
    }

    private func removeFeed() {
        guard !isWorking else {
            return
        }
        isWorking = true
        errorMessage = nil
        Task {
            do {
                try await onRemoveFeed()
                settings.feedURL = ""
            } catch {
                errorMessage = error.localizedDescription
            }
            isWorking = false
        }
    }

    private func resetData() {
        guard !isWorking else {
            return
        }
        isWorking = true
        errorMessage = nil
        Task {
            do {
                try await onResetData()
                settings.feedURL = ""
            } catch {
                errorMessage = error.localizedDescription
            }
            isWorking = false
        }
    }

    private func exportDiagnostics() {
        guard !isWorking else {
            return
        }
        isWorking = true
        errorMessage = nil
        Task {
            do {
                let content = try await onExportDiagnostics()
                diagnosticsDocument = DiagnosticsDocument(content: content)
                showDiagnosticsExporter = true
            } catch {
                errorMessage = error.localizedDescription
            }
            isWorking = false
        }
    }

    private func openNotificationSettings() {
        guard let url = URL(
            string: "x-apple.systempreferences:com.apple.preference.notifications"
        ) else {
            return
        }
        NSWorkspace.shared.open(url)
    }
}

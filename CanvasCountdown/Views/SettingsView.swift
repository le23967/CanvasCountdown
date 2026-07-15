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

    let canAddReminder: Bool
    let canRequestNotificationPermission: Bool
    let onAddReminder: @MainActor (Int, ReminderUnit) -> String?
    let onUpdateReminder: @MainActor (ReminderRule) -> String?
    let onRemoveReminder: @MainActor (UUID) -> Void
    let onSetReminderEnabled: @MainActor (Bool, UUID) -> Void
    let onResetReminders: @MainActor () -> Void
    let onApplyDockTheme: @MainActor (DockThemePreset) -> Void
    let onResetDockAppearance: @MainActor () -> Void

    @State private var previewDays = 7
    @State private var editingRule: ReminderRule?
    @State private var draftAmount = 1
    @State private var draftUnit: ReminderUnit = .days
    @State private var reminderError: String?
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

            ForEach(settings.reminderSchedule.rules) { rule in
                HStack {
                    Toggle(rule.title, isOn: Binding(
                        get: { rule.isEnabled },
                        set: { onSetReminderEnabled($0, rule.id) }
                    ))

                    Spacer()

                    Button {
                        editingRule = rule
                        draftAmount = rule.amount
                        draftUnit = rule.unit
                    } label: {
                        Image(systemName: "pencil")
                    }
                    .buttonStyle(.borderless)
                    .help("Edit this reminder")
                    .accessibilityLabel("Edit \(rule.title)")

                    Button(role: .destructive) {
                        onRemoveReminder(rule.id)
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .help("Delete this reminder")
                    .accessibilityLabel("Delete \(rule.title)")
                }
            }
            .disabled(notificationPermission == .denied)

            reminderEditor

            HStack {
                Button("Reset Reminders") {
                    onResetReminders()
                }
                Spacer()
                Text("\(settings.reminderSchedule.rules.count) of \(ReminderRule.maximumRuleCount)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let reminderError {
                Label(reminderError, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        } header: {
            Text("Notifications")
        } footer: {
            Text("Reminders are scheduled locally and rebuilt whenever a due date or the schedule changes, so no duplicate arrives.")
        }
    }

    @ViewBuilder
    private var reminderEditor: some View {
        HStack {
            if let editingRule {
                Text("Editing \(editingRule.title)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Stepper(value: $draftAmount, in: 0...maximumDraftAmount) {
                Text("\(draftAmount)")
                    .monospacedDigit()
                    .frame(minWidth: 34, alignment: .trailing)
            }
            .accessibilityLabel("Reminder amount")

            Picker("", selection: $draftUnit) {
                ForEach(ReminderUnit.allCases) { unit in
                    Text(unit.title).tag(unit)
                }
            }
            .labelsHidden()
            .frame(width: 96)
            .accessibilityLabel("Reminder unit")

            Text("before")
                .foregroundStyle(.secondary)

            Spacer()

            if editingRule != nil {
                Button("Cancel") {
                    clearReminderDraft()
                }
            }

            Button(editingRule == nil ? "Add" : "Save") {
                commitReminderDraft()
            }
            .disabled(editingRule == nil && !canAddReminder)
        }
        .onChange(of: draftUnit) { _, _ in
            draftAmount = min(draftAmount, maximumDraftAmount)
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
                .disabled(!canRequestNotificationPermission)
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
        Section {
            dockPreview

            Picker("Header label", selection: $settings.dockLabel) {
                ForEach(DockLabelOption.allCases) { option in
                    Text("\(option.title) — \(option.renderedLabel)").tag(option)
                }
            }

            Picker("Number size", selection: $settings.dockAppearance.numberSize) {
                ForEach(DockNumberSize.allCases) { size in
                    Text(size.title).tag(size)
                }
            }

            Picker("Number weight", selection: $settings.dockAppearance.numberWeight) {
                ForEach(DockNumberWeight.allCases) { weight in
                    Text(weight.title).tag(weight)
                }
            }
            .pickerStyle(.segmented)

            Picker("Theme", selection: Binding(
                get: { settings.dockAppearance.preset },
                set: { onApplyDockTheme($0) }
            )) {
                ForEach(DockThemePreset.selectable) { preset in
                    Text(preset.title).tag(preset)
                }
                if settings.dockAppearance.preset == .custom {
                    Text(DockThemePreset.custom.title)
                        .tag(DockThemePreset.custom)
                }
            }

            ColorPicker(
                "Background top",
                selection: hexBinding(\.backgroundTop),
                supportsOpacity: false
            )
            ColorPicker(
                "Background bottom",
                selection: hexBinding(\.backgroundBottom),
                supportsOpacity: false
            )
            ColorPicker(
                "Number",
                selection: hexBinding(\.number),
                supportsOpacity: false
            )
            ColorPicker(
                "Header label",
                selection: hexBinding(\.label),
                supportsOpacity: false
            )

            if !settings.dockAppearance.hasSufficientContrast {
                Label(
                    "Those colours are hard to read on this background, so the text colour is corrected automatically.",
                    systemImage: "exclamationmark.triangle.fill"
                )
                .font(.caption)
                .foregroundStyle(.orange)
            }

            Button("Reset to Defaults") {
                onResetDockAppearance()
            }
        } header: {
            Text("Dock Appearance")
        } footer: {
            Text("The countdown is drawn live while Canvas Countdown is running.")
        }
    }

    /// Shown at roughly the sizes macOS asks the Dock for.
    private var dockPreview: some View {
        HStack(alignment: .bottom, spacing: 14) {
            ForEach([96.0, 64.0, 40.0], id: \.self) { size in
                VStack(spacing: 4) {
                    Image(
                        nsImage: DockTileRenderer.image(
                            size: size,
                            daysRemaining: previewDays,
                            label: settings.dockLabel.renderedLabel,
                            appearance: settings.dockAppearance
                        )
                    )
                    .accessibilityLabel(
                        "Dock preview at \(Int(size)) points showing \(previewDays) days"
                    )
                    Text("\(Int(size))pt")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Picker("Preview value", selection: $previewDays) {
                ForEach([0, 7, 21, 157, 1_024], id: \.self) { value in
                    Text("\(value)").tag(value)
                }
            }
            .labelsHidden()
            .frame(width: 90)
        }
        .padding(.vertical, 4)
    }

    private func hexBinding(
        _ keyPath: WritableKeyPath<DockAppearance.Colours, String>
    ) -> Binding<Color> {
        Binding(
            get: {
                Color(nsColor: NSColor(hex: settings.dockAppearance.colours[keyPath: keyPath]) ?? .white)
            },
            set: { newValue in
                settings.dockAppearance.colours[keyPath: keyPath] =
                    NSColor(newValue).hexString
                settings.dockAppearance.preset = .custom
            }
        )
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

    private var maximumDraftAmount: Int {
        draftUnit == .days ? ReminderRule.maximumDays : ReminderRule.maximumHours
    }

    private func commitReminderDraft() {
        if let editingRule {
            var updated = editingRule
            updated.amount = draftAmount
            updated.unit = draftUnit
            reminderError = onUpdateReminder(updated)
        } else {
            reminderError = onAddReminder(draftAmount, draftUnit)
        }
        if reminderError == nil {
            clearReminderDraft()
        }
    }

    private func clearReminderDraft() {
        editingRule = nil
        draftAmount = 1
        draftUnit = .days
        reminderError = nil
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

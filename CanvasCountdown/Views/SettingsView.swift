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
    let dockThemes: DockThemePresetActions
    let onResetDockAppearance: @MainActor () -> Void
    let labels: EventLabelActions
    let courses: CourseManagementActions
    let assistant: AssistantProfileActions
    let assistantAPIKey: String
    let onSaveAssistantKey: @MainActor (String) -> Void
    let models: LocalModelPresentation
    let updates: UpdatePresentation

    @State private var coursePendingRemoval: ManagedCourse?
    @State private var apiKeyDraft = ""
    @State private var revealAPIKey = false
    @State private var previewValue: Int? = 7
    @State private var hoveredSize: DockNumberSize?
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
            dueTimeSection
            notificationSection
            courseFilteringSection
            courseManagementSection
            labelsSection
            dockSection
            assistantSection
            systemSection
            updatesSection
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

    /// Canvas exports an assignment due "on Friday" as an all-day entry, which
    /// arrives as 00:00 — the first moment of Friday rather than the last. This
    /// is the one switch that decides how that is read.
    private var dueTimeSection: some View {
        Section {
            Toggle(
                "Show midnight deadlines as 11:59 PM",
                isOn: $settings.treatsMidnightAsEndOfDay
            )

            Text("Only the time changes. The day, the days-left count and the Dock number stay exactly as they are, and nothing Canvas sent is altered — turning this off brings the original times straight back.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        } header: {
            Text("Due Times")
        } footer: {
            Text("Most Canvas deadlines are 11:59 PM, but an all-day entry reaches this Mac as 00:00, which reads as the start of the day instead of the end. Events you added yourself are never adjusted.")
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

            Picker("Number weight", selection: $settings.dockAppearance.numberWeight) {
                ForEach(DockNumberWeight.allCases) { weight in
                    Text(weight.title).tag(weight)
                }
            }
            .pickerStyle(.segmented)

            DockThemeRow(actions: dockThemes)

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

    /// Each tile is the real renderer at the size it would draw, and choosing
    /// one selects that size. There is no separate size picker.
    private var dockPreview: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text("Preview value")
                Picker("Preview value", selection: $previewValue) {
                    ForEach(Self.previewValues, id: \.self) { value in
                        Text(Self.previewValueTitle(value)).tag(value)
                    }
                }
                .labelsHidden()
                .frame(width: 96)
                .accessibilityLabel("Preview value")

                Spacer(minLength: 0)
            }

            // Wraps instead of scrolling when the window is narrow.
            LazyVGrid(
                columns: [
                    GridItem(
                        .adaptive(minimum: 92, maximum: 132),
                        spacing: 10,
                        alignment: .top
                    ),
                ],
                alignment: .leading,
                spacing: 10
            ) {
                ForEach(DockNumberSize.allCases) { size in
                    sizeTile(size)
                }
            }

            Text("Preview uses the same renderer as the live Dock icon.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
    }

    private func sizeTile(_ size: DockNumberSize) -> some View {
        let isSelected = settings.dockAppearance.numberSize == size

        return Button {
            settings.dockAppearance.numberSize = size
        } label: {
            VStack(spacing: 6) {
                Image(
                    nsImage: DockTileRenderer.image(
                        size: 76,
                        daysRemaining: previewValue,
                        label: settings.dockLabel.renderedLabel,
                        appearance: previewAppearance(for: size)
                    )
                )
                .frame(width: 76, height: 76)

                HStack(spacing: 4) {
                    if isSelected {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundStyle(.tint)
                            .accessibilityHidden(true)
                    }
                    Text(size.title)
                        .font(.caption)
                        .fontWeight(isSelected ? .semibold : .regular)
                }
            }
            .padding(6)
            .frame(maxWidth: .infinity)
            .background {
                RoundedRectangle(cornerRadius: 10)
                    .fill(
                        isSelected
                            ? Color.accentColor.opacity(0.14)
                            : (hoveredSize == size
                                ? Color.primary.opacity(0.06)
                                : Color.clear)
                    )
            }
            .overlay {
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(
                        isSelected ? Color.accentColor : Color.clear,
                        lineWidth: 2
                    )
            }
            .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .focusable()
        .onHover { isInside in
            hoveredSize = isInside ? size : nil
        }
        .help("\(size.title) Dock countdown")
        .accessibilityLabel(size.accessibilityDescription)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }

    /// The tile draws with the real appearance, differing only in the size being
    /// offered, so what is shown is what the Dock will draw.
    private func previewAppearance(for size: DockNumberSize) -> DockAppearance {
        var appearance = settings.dockAppearance
        appearance.numberSize = size
        return appearance
    }

    /// Sample values for the preview only. Nil renders the em dash placeholder.
    private static let previewValues: [Int?] = [0, 7, 21, 157, 1_024, nil]

    private static func previewValueTitle(_ value: Int?) -> String {
        guard let value else {
            return "\(DockTileLayout.placeholder) (none)"
        }
        return "\(value)"
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

    /// Removing a course, as opposed to filtering it out.
    ///
    /// A Canvas Calendar Feed carries every course the account has ever been
    /// enrolled in, so a subject finished last year keeps arriving. Filtering
    /// hides it; this takes it out and keeps it out, and says plainly that it is
    /// a deletion rather than another way to hide something.
    private var courseManagementSection: some View {
        Section {
            if courses.courses.isEmpty {
                Text("Courses appear here once events are imported.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(courses.courses) { course in
                    HStack {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(course.name)
                                .lineLimit(2)
                            Text(course.eventCountDescription)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }

                        Spacer(minLength: 8)

                        Button(role: .destructive) {
                            coursePendingRemoval = course
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.borderless)
                        .help("Remove this course and everything in it")
                        .accessibilityLabel("Remove \(course.name)")
                    }
                }
            }

            if !courses.blocked.isEmpty {
                Text("Removed")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(.secondary)

                ForEach(courses.blocked, id: \.self) { name in
                    HStack {
                        Label(name, systemImage: "nosign")
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                        Spacer(minLength: 8)
                        Button("Allow Again") {
                            courses.allow(name)
                        }
                        .accessibilityLabel("Allow \(name) again")
                    }
                }
            }
        } header: {
            Text("Courses")
        } footer: {
            Text("Removing a course permanently deletes its events on this Mac and stops the Canvas feed from importing it again. Manual events are never touched. Allowing a course again brings it back on the next refresh.")
        }
        .alert(
            "Remove this course?",
            isPresented: Binding(
                get: { coursePendingRemoval != nil },
                set: { visible in
                    if !visible {
                        coursePendingRemoval = nil
                    }
                }
            ),
            presenting: coursePendingRemoval
        ) { course in
            Button("Cancel", role: .cancel) {
                coursePendingRemoval = nil
            }
            Button("Remove Course", role: .destructive) {
                courses.remove(course.name)
                coursePendingRemoval = nil
            }
        } message: { course in
            Text("“\(course.name)” and its \(course.eventCountDescription) will be deleted from this Mac, and the Canvas feed will stop importing it. You can allow it again later.")
        }
    }

    /// One list of labels, because "how urgent" and "what kind of thing" are
    /// the same mark on the same event. Two systems would mean two colours
    /// competing for one row.
    private var labelsSection: some View {
        Section {
            if labels.labels.isEmpty {
                Text("No labels yet. Add one to colour events in the list and the calendar.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(labels.labels) { label in
                    EventLabelRow(
                        label: label,
                        usageCount: labels.usageCount(label.id),
                        onRename: { labels.rename(label.id, $0) },
                        onRecolor: { labels.recolor(label.id, $0) },
                        onDelete: { labels.delete(label.id) }
                    )
                }
            }

            Button("Add Label", action: labels.add)
                .disabled(!labels.canAdd)
        } header: {
            Text("Labels")
        } footer: {
            Text("A label is a name and a colour — Important, Personal, Society, whatever you need. Put one on an event from the ⋯ menu beside it, and it colours that event in the list and on the calendar. Nothing is labelled automatically.")
        }
    }

    private var assistantSection: some View {
        Section {
            Toggle("Use an AI assistant", isOn: $settings.assistant.isEnabled)

            if settings.assistant.isEnabled {
                savedModelRows
                addModelMenu

                Picker("Runs", selection: Binding(
                    get: { settings.assistant.provider },
                    set: { settings.assistant = AssistantSettings.applying($0, to: settings.assistant) }
                )) {
                    ForEach(AssistantProvider.allCases) { provider in
                        Text(provider.title).tag(provider)
                    }
                }
                .pickerStyle(.radioGroup)

                // The whole decision, stated where it is made. Read off the
                // address rather than the label, so it cannot claim privacy the
                // configuration does not provide.
                Label(
                    settings.assistant.staysOnThisMac
                        ? AssistantProvider.local.privacySummary
                        : AssistantProvider.cloud.privacySummary,
                    systemImage: settings.assistant.staysOnThisMac ? "lock.fill" : "arrow.up.right.circle.fill"
                )
                .font(.callout)
                .foregroundStyle(
                    settings.assistant.staysOnThisMac
                        ? AnyShapeStyle(.secondary)
                        : AnyShapeStyle(Color.orange)
                )

                Text(settings.assistant.provider.qualityNote)
                    .font(.caption)
                    .foregroundStyle(.secondary)

                TextField("Address", text: $settings.assistant.baseURL)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Assistant address")

                TextField("Model", text: $settings.assistant.model)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Assistant model")

                serviceSuggestions

                if settings.assistant.staysOnThisMac {
                    localModelControls
                }

                if settings.assistant.provider.requiresAPIKey {
                    HStack {
                        Group {
                            if revealAPIKey {
                                TextField("API key", text: $apiKeyDraft)
                            } else {
                                SecureField("API key", text: $apiKeyDraft)
                            }
                        }
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Assistant API key")

                        Button {
                            revealAPIKey.toggle()
                        } label: {
                            Image(systemName: revealAPIKey ? "eye.slash" : "eye")
                        }
                        .buttonStyle(.borderless)
                        .accessibilityLabel(revealAPIKey ? "Hide API key" : "Reveal API key")

                        Button("Save Key") {
                            onSaveAssistantKey(apiKeyDraft)
                        }
                        .disabled(apiKeyDraft == assistantAPIKey)
                    }

                    Label(
                        "Each model keeps its own key in the macOS Keychain, never in preferences or diagnostics.",
                        systemImage: "lock.fill"
                    )
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
            }
        } header: {
            Text("AI Assistant (optional)")
        } footer: {
            Text("Off by default. It can summarise your workload and turn a sentence into a task, and anything it proposes is reviewed by you before it is saved. It never changes an event on its own.")
        }
        .onAppear {
            apiKeyDraft = assistantAPIKey
            // Ask the local server what it has, so the list is not stale.
            models.onRefresh()
        }
        // The key belongs to the selected model, so switching model has to swap
        // what this field is showing.
        .onChange(of: assistantAPIKey) { _, key in
            apiKeyDraft = key
        }
    }

    /// The saved models, one of which is in use. Keeping several means an
    /// OpenAI key and a Groq key can both be held, and switching is one click
    /// rather than retyping an address, a model name and a key.
    @ViewBuilder
    private var savedModelRows: some View {
        if assistant.profiles.isEmpty {
            Text("No models saved yet. Add one below, or just fill in the address and model.")
                .foregroundStyle(.secondary)
        } else {
            ForEach(assistant.profiles) { profile in
                AssistantProfileRow(
                    profile: profile,
                    isSelected: profile.id == assistant.activeID,
                    canDelete: assistant.profiles.count > 1,
                    onSelect: { assistant.select(profile.id) },
                    onRename: { assistant.rename(profile.id, $0) },
                    onDuplicate: { assistant.duplicate(profile.id) },
                    onDelete: { assistant.delete(profile.id) }
                )
            }
        }
    }

    private var addModelMenu: some View {
        Menu("Add a Model…") {
            ForEach(AssistantService.allServices) { service in
                Button {
                    assistant.add(service)
                } label: {
                    Text("\(service.name) — \(service.note)")
                }
            }

            Divider()

            Button("Blank…") {
                assistant.addBlank()
            }
        }
        .disabled(!assistant.canAdd)
        .help(assistant.canAdd
            ? "Start from a known service, or from nothing"
            : "That is as many models as this keeps")
    }

    /// Suggestions, in blue, because they are links rather than settings.
    ///
    /// The address and the model above stay ordinary text fields: a service
    /// that ships a new model tomorrow works today, and one missing from this
    /// list works by typing its address. Nothing here is a restriction.
    private var serviceSuggestions: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Works with any service that speaks the OpenAI format:")
                .font(.caption)
                .foregroundStyle(.secondary)

            // Wraps rather than running off the edge on a narrow window.
            LazyVGrid(
                columns: [
                    GridItem(
                        .adaptive(minimum: 108, maximum: 200),
                        spacing: 8,
                        alignment: .leading
                    ),
                ],
                alignment: .leading,
                spacing: 4
            ) {
                ForEach(AssistantService.allServices) { service in
                    Button(service.name) {
                        assistant.useService(service)
                    }
                    .buttonStyle(.link)
                    .font(.caption)
                    .help("\(service.note) \(service.baseURL)")
                    .accessibilityHint("Fills in the address and a model for \(service.name)")
                }
            }

            if let keysURL = currentServiceKeysURL {
                Link("Where to get a key", destination: keysURL)
                    .font(.caption)
            }
        }
        .padding(.vertical, 2)
    }

    private var currentServiceKeysURL: URL? {
        guard settings.assistant.provider.requiresAPIKey,
              let keys = AssistantService
                .matching(baseURL: settings.assistant.baseURL)?.keysURL else {
            return nil
        }
        return URL(string: keys)
    }

    /// Model management for the local server.
    ///
    /// The app can list, fetch and remove models, but it cannot install or start
    /// Ollama itself: a sandboxed application may not launch another program. So
    /// when nothing answers, the commands are shown ready to copy rather than
    /// pretending the app can do it.
    @ViewBuilder
    private var localModelControls: some View {
        if models.isReachable {
            if !models.installed.isEmpty {
                ForEach(models.installed) { model in
                    HStack {
                        Image(systemName: model.name == settings.assistant.model
                            ? "largecircle.fill.circle"
                            : "circle")
                            .foregroundStyle(.tint)
                            .accessibilityHidden(true)

                        VStack(alignment: .leading, spacing: 1) {
                            Text(model.name)
                            Text(model.sizeDescription)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }

                        Spacer()

                        if model.name != settings.assistant.model {
                            Button("Use") {
                                models.onUse(model.name)
                            }
                        }
                        Button(role: .destructive) {
                            models.onDelete(model.name)
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.borderless)
                        .accessibilityLabel("Delete \(model.name)")
                    }
                }
            } else {
                Text("No models are installed yet. Download one below.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            if let downloading = models.downloading {
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text("Downloading \(downloading)")
                            .font(.callout)
                        Spacer()
                        Button("Cancel") {
                            models.onCancelDownload()
                        }
                    }
                    ProgressView(value: models.progress)
                }
            } else {
                Menu("Download a Model…") {
                    ForEach(SuggestedModel.all) { suggestion in
                        Button {
                            models.onDownload(suggestion.name)
                        } label: {
                            Text("\(suggestion.name) — \(suggestion.approximateSize). \(suggestion.note)")
                        }
                        .disabled(models.installed.contains { $0.name == suggestion.name })
                    }
                }
            }
        } else {
            VStack(alignment: .leading, spacing: 8) {
                Label(
                    "Ollama is not responding on this Mac.",
                    systemImage: "exclamationmark.triangle.fill"
                )
                .font(.callout)
                .foregroundStyle(.orange)

                Text("Canvas Countdown can manage models for you, but it cannot install or start Ollama itself. Run these once:")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                copyableCommand(OllamaSetup.homebrewCommand)
                copyableCommand(OllamaSetup.serveCommand)

                HStack {
                    Link("Download Ollama", destination: URL(string: OllamaSetup.downloadPage)!)
                    Spacer()
                    Button("Check Again") {
                        models.onRefresh()
                    }
                }
            }
        }

        if let message = models.message {
            Text(message)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func copyableCommand(_ command: String) -> some View {
        HStack {
            Text(command)
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
            Spacer()
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(command, forType: .string)
            } label: {
                Image(systemName: "doc.on.doc")
            }
            .buttonStyle(.borderless)
            .help("Copy")
            .accessibilityLabel("Copy \(command)")
        }
        .padding(6)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 5))
    }

    private var systemSection: some View {
        Section("System") {
            Toggle("Launch Canvas Countdown at login", isOn: $settings.launchAtLogin)
            Text("macOS may ask you to approve this under Login Items.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    /// Says which version is running and offers to look for a newer one.
    ///
    /// The app checks by itself once a day, so this button is for the moment
    /// somebody wants an answer now — and for saying plainly that they are
    /// already on the newest version, which the silent check never bothers to
    /// mention.
    private var updatesSection: some View {
        Section {
            LabeledContent("Version", value: updates.runningVersion)

            HStack(spacing: 10) {
                Button("Check for Updates") {
                    updates.onCheck()
                }
                .disabled(updates.isChecking)

                if updates.isChecking {
                    ProgressView().controlSize(.small)
                }

                if let release = updates.available {
                    Text("\(release.version.description) is available")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                } else if let announcement = updates.announcement {
                    Text(announcement)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: 0)
            }
        } header: {
            Text("Updates")
        } footer: {
            // Said here rather than discovered halfway through: the app cannot
            // install over itself, and somebody about to update should know
            // where it will stop before they start.
            Text("Canvas Countdown checks once a day and tells you in the window. Downloading opens the disk image — the last step is dragging the app to Applications yourself, because a sandboxed app cannot replace itself.")
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

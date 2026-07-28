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
    let assistantAPIKey: String
    let onSaveAssistantKey: @MainActor (String) -> Void
    let models: LocalModelPresentation

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
            notificationSection
            courseFilteringSection
            labelsSection
            dockSection
            assistantSection
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
                Picker("Runs", selection: Binding(
                    get: { settings.assistant.provider },
                    set: { settings.assistant = AssistantSettings.applying($0, to: settings.assistant) }
                )) {
                    ForEach(AssistantProvider.allCases) { provider in
                        Text(provider.title).tag(provider)
                    }
                }
                .pickerStyle(.radioGroup)

                // The whole decision, stated where it is made.
                Label(
                    settings.assistant.staysOnThisMac
                        ? AssistantProvider.local.privacySummary
                        : AssistantProvider.groq.privacySummary,
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

                HStack {
                    TextField("Model", text: $settings.assistant.model)
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Assistant model")

                    if settings.assistant.provider == .groq {
                        Menu("Choose") {
                            ForEach(AssistantProvider.groqModels, id: \.self) { name in
                                Button(name) {
                                    settings.assistant.model = name
                                }
                            }
                        }
                        .frame(width: 90)
                        .help("Common Groq models. Any other name can be typed.")
                    }
                }

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
                        "Stored in the macOS Keychain, never in preferences or diagnostics.",
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

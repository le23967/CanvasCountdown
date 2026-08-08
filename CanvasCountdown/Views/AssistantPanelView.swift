import SwiftUI

/// The assistant, as a panel beside the assignments rather than a modal sheet.
///
/// Kept next to the list on purpose: asking "what should I start" while the
/// deadlines are still visible is the whole point, and a sheet would cover them.
/// The assistant's accent: a blue-violet that reads as distinct from the app's
/// own tint without turning the panel into a purple slab. Used for the identity
/// mark, the user's own messages and the send control, never for body text or
/// large surfaces.
enum AssistantStyle {
    static let accent = Color(red: 0.42, green: 0.36, blue: 0.86)
    static let secondaryAccent = Color(red: 0.58, green: 0.44, blue: 0.90)

    /// The identity mark, and the send control. One gradient at one size, so
    /// the panel reads as a place rather than as another form.
    static var mark: LinearGradient {
        LinearGradient(
            colors: [secondaryAccent, accent],
            startPoint: .topLeading,
            endPoint: .bottomTrailing
        )
    }

    /// Tinted fills stay faint so system label colours keep their contrast in
    /// both appearances.
    static func messageFill(isUser: Bool) -> Color {
        isUser ? accent.opacity(0.14) : Color.secondary.opacity(0.10)
    }

    /// The hairline the suggestion cards, the draft cards and the composer all
    /// share, so they read as one set of surfaces rather than three treatments.
    /// Derived from the label colour, so it follows the appearance.
    static let cardStroke = Color.primary.opacity(0.10)
    static let cardFill = Color.primary.opacity(0.04)
    static let cardCornerRadius: CGFloat = 12
}

struct AssistantPanelView: View {
    @Bindable var viewModel: MainViewModel
    let onSaveDrafts: @MainActor ([AssistantDraftTask]) async -> Void

    /// One field, so one focus binding. There used to be three of each.
    @FocusState private var isComposerFocused: Bool

    /// Starts off, so opening the panel lands on the newest exchange rather
    /// than the oldest one kept.
    @State private var showsFullHistory = false
    @State private var hoveredSuggestion: String?

    private var visibleConversation: [AssistantMessage] {
        showsFullHistory ? viewModel.conversation : viewModel.recentConversation
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    contextChip

                    if !viewModel.isAssistantEnabled {
                        disabledNotice
                    } else {
                        conversationSection

                        if !viewModel.assistantDrafts.isEmpty {
                            Divider()
                            draftSection
                        }
                    }

                    if let message = viewModel.assistantErrorMessage {
                        Label(message, systemImage: "exclamationmark.triangle.fill")
                            .font(.callout)
                            .foregroundStyle(.orange)
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            // Outside the scroll view on purpose. The one place to type is also
            // the one thing that must never be scrolled off the panel.
            if viewModel.isAssistantEnabled {
                composer
            }
        }
        .frame(minWidth: 220)
    }

    private var header: some View {
        HStack(spacing: 9) {
            Image(systemName: "sparkles")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background(AssistantStyle.mark, in: Circle())
                .accessibilityHidden(true)

            Text("Assistant")
                .font(.headline)

            Text("Beta")
                .font(.caption2.weight(.semibold))
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(
                    AssistantStyle.accent.opacity(0.16),
                    in: Capsule()
                )

            Spacer(minLength: 4)

            // Reads the endpoint, not the provider name, so this cannot claim
            // privacy the configuration does not provide.
            Label(
                viewModel.assistantPrivacyLabel,
                systemImage: viewModel.assistantStaysOnThisMac
                    ? "lock.fill"
                    : "arrow.up.right.circle.fill"
            )
            .font(.caption)
            .foregroundStyle(.secondary)
            .lineLimit(1)

            // Which model is in use is chosen on the composer now, beside the
            // sentence it will answer, rather than in here as well.
            Menu {
                Picker("Show as", selection: Binding(
                    get: { viewModel.assistantPreference },
                    set: { viewModel.setAssistantPreference($0) }
                )) {
                    ForEach(AssistantPresentationPreference.allCases) { option in
                        Text(option.title).tag(option)
                    }
                }

                Divider()

                Button("Open in Separate Window") {
                    viewModel.openAssistantInSeparateWindow()
                }
                Button("Clear Conversation…") {
                    viewModel.requestClearConversation()
                }
                .disabled(viewModel.conversation.isEmpty)
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .accessibilityLabel("Assistant options")

            Button {
                viewModel.closeAssistant()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Close assistant")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    /// Shown when a question is about one assignment, so the panel says what it
    /// is answering about and the request stays narrow.
    @ViewBuilder
    private var contextChip: some View {
        if let context = viewModel.assistantContext {
            HStack(spacing: 6) {
                Text("Asking about:")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack(spacing: 4) {
                    Text(context.chipTitle)
                        .font(.caption)
                        .lineLimit(1)
                    Button {
                        viewModel.clearAssistantContext()
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.caption2)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Remove assignment context")
                }
                .padding(.horizontal, 7)
                .padding(.vertical, 3)
                .background(
                    AssistantStyle.accent.opacity(0.12),
                    in: Capsule()
                )

                Spacer(minLength: 0)
            }
            .help(context.title)
        }
    }

    private var disabledNotice: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("The assistant is off")
                .font(.subheadline.weight(.semibold))
            Text("Turn it on in Settings. It can run on this Mac, in which case nothing is uploaded.")
                .font(.callout)
                .foregroundStyle(.secondary)
            Button("Open Settings") {
                viewModel.sidebarSelection = .settings
            }
        }
    }

    // MARK: - The conversation

    @ViewBuilder
    private var conversationSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            if viewModel.conversation.isEmpty {
                emptyState
            } else {
                // The newest exchange is the one being read, and the field to
                // ask again sits below all of this, so only the recent ones are
                // shown until the rest is asked for.
                if viewModel.earlierConversationCount > 0 {
                    Button(showsFullHistory
                        ? "Show recent only"
                        : "Show \(viewModel.earlierConversationCount) earlier") {
                        showsFullHistory.toggle()
                    }
                    .buttonStyle(.link)
                    .font(.caption)
                }

                // Kept between launches, so this is a record rather than a
                // scrollback: it says which day each exchange happened on.
                ForEach(visibleConversation) { message in
                    if let heading = dayHeading(before: message) {
                        Text(heading)
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .center)
                            .padding(.top, 4)
                    }
                    messageBubble(message)
                }

                Button("Clear conversation") {
                    viewModel.requestClearConversation()
                }
                .buttonStyle(.link)
                .font(.caption)
            }

            if viewModel.isAssistantBusy {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("Thinking…").foregroundStyle(.secondary)
                }
                .font(.callout)
            }
        }
        // Kept between launches now, so clearing throws away a record rather
        // than closing a window.
        .confirmationDialog(
            "Clear this conversation?",
            isPresented: $viewModel.isConfirmingClearConversation,
            titleVisibility: .visible
        ) {
            Button("Clear Conversation", role: .destructive) {
                viewModel.clearConversation()
            }
            Button("Keep", role: .cancel) {
                viewModel.isConfirmingClearConversation = false
            }
        } message: {
            Text("Everything asked and answered here is deleted. Your assignments are not touched.")
        }
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Ask about your deadlines")
                .font(.title3.weight(.semibold))

            // Counts and dates are worked out by the app and handed to the
            // model, so an answer about "how many" is not a guess.
            Text("Counts and dates come from your own list, not from the model.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.bottom, 2)

            ForEach(MainViewModel.assistantSuggestions, id: \.self) { suggestion in
                suggestionCard(suggestion, systemImage: "text.bubble") {
                    Task { await viewModel.ask(suggestion) }
                }
                .disabled(viewModel.isAssistantBusy)
            }

            // The one thing that says the box below has a second job. Without
            // it the mode chip is a control nobody has a reason to open, and
            // writing a task in a sentence goes back to being undiscoverable.
            suggestionCard(
                "Add a task in your own words",
                systemImage: AssistantComposerMode.addTask.systemImage
            ) {
                viewModel.assistantComposerMode = .addTask
                isComposerFocused = true
            }
        }
    }

    /// A whole row that can be pressed, rather than a line of blue underlined
    /// text. The cards are the empty state; there is nothing else to read.
    private func suggestionCard(
        _ title: String,
        systemImage: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: systemImage)
                    .font(.caption)
                    .foregroundStyle(AssistantStyle.accent)
                    .frame(width: 24, height: 24)
                    .background(
                        AssistantStyle.accent.opacity(0.12),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                Text(title)
                    .font(.callout)
                    .foregroundStyle(.primary)
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)

                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 10)
                    .fill(hoveredSuggestion == title
                        ? AssistantStyle.cardFill
                        : Color.clear)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(AssistantStyle.cardStroke)
            )
            .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .onHover { isInside in
            hoveredSuggestion = isInside ? title : nil
        }
    }

    /// The date to put above this message, or nothing if it belongs to the same
    /// day as the one before it.
    ///
    /// Read against what is on screen rather than the whole record, so the
    /// first message shown always says which day it belongs to.
    private func dayHeading(before message: AssistantMessage) -> String? {
        let messages = visibleConversation
        guard let index = messages.firstIndex(where: { $0.id == message.id }) else {
            return nil
        }
        let calendar = Calendar.autoupdatingCurrent
        if index > 0,
           calendar.isDate(
               messages[index - 1].date,
               inSameDayAs: message.date
           ) {
            return nil
        }
        if calendar.isDateInToday(message.date) {
            return "Today"
        }
        if calendar.isDateInYesterday(message.date) {
            return "Yesterday"
        }
        return message.date.formatted(
            .dateTime.weekday(.abbreviated).day().month(.abbreviated)
        )
    }

    /// Your own words sit on the right, the answer on the left, both short of
    /// the full width so the two sides are told apart at a glance. The name and
    /// the time stay: this is a record that survives a relaunch, not a
    /// scrollback.
    private func messageBubble(_ message: AssistantMessage) -> some View {
        let isUser = message.role == .user
        return HStack(spacing: 0) {
            if isUser {
                Spacer(minLength: 28)
            }

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(isUser ? "You" : "Assistant")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.secondary)
                    Text(message.date.formatted(.dateTime.hour().minute()))
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                    Spacer(minLength: 0)
                }
                Text(message.text)
                    .font(.callout)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(10)
            .background(
                AssistantStyle.messageFill(isUser: isUser),
                in: RoundedRectangle(cornerRadius: 14)
            )

            if !isUser {
                Spacer(minLength: 28)
            }
        }
    }

    // MARK: - Drafts waiting to be checked

    private var draftSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            requestHistory

            Text("Check these before saving")
                .font(.caption)
                .foregroundStyle(.secondary)

            ForEach(viewModel.assistantDrafts) { task in
                draftRow(task)
            }

            HStack {
                Button("Discard") {
                    viewModel.clearAssistantDrafts()
                }
                .help("Throw these away and start from a new sentence")
                Spacer()
                // The ellipsis is the promise that this is not the last
                // step: it opens the confirmation rather than writing.
                Button("Save Selected…") {
                    viewModel.requestSaveAssistantDrafts()
                }
                // Return belongs to whatever is being typed. While there is a
                // sentence in the box, it changes the drafts rather than saving
                // them.
                .keyboardShortcut(
                    hasTypedText ? nil : KeyboardShortcut.defaultAction
                )
                .disabled(!viewModel.hasSaveableDrafts)
                .help("Check what will be added, then add it")
            }
        }
        .confirmationDialog(
            viewModel.assistantSaveSummary.title,
            isPresented: $viewModel.isConfirmingAssistantSave,
            titleVisibility: .visible
        ) {
            Button(viewModel.assistantSaveSummary.confirmTitle) {
                let drafts = viewModel.assistantDrafts
                Task { await onSaveDrafts(drafts) }
            }
            Button("Cancel", role: .cancel) {
                viewModel.cancelSaveAssistantDrafts()
            }
        } message: {
            Text(
                viewModel.assistantSaveSummary
                    .detailLines()
                    .joined(separator: "\n")
            )
        }
    }

    /// What has been asked so far, so the drafts read as the result of a
    /// conversation rather than appearing from nowhere.
    ///
    /// This is also where the sentence that produced them goes once the box has
    /// been emptied, so nothing typed is lost by clearing it.
    @ViewBuilder
    private var requestHistory: some View {
        if !viewModel.assistantDraftHistory.isEmpty {
            VStack(alignment: .leading, spacing: 3) {
                ForEach(
                    Array(viewModel.assistantDraftHistory.enumerated()),
                    id: \.offset
                ) { index, entry in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Image(systemName: index == 0
                            ? "text.bubble"
                            : "arrow.turn.down.right")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                        Text(entry)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 0)
                    }
                }
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                AssistantStyle.accent.opacity(0.07),
                in: RoundedRectangle(cornerRadius: 6)
            )
            .accessibilityElement(children: .combine)
            .accessibilityLabel(
                "Asked so far: " +
                    viewModel.assistantDraftHistory.joined(separator: ", then ")
            )
        }
    }

    private func draftRow(_ task: AssistantDraftTask) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Toggle(isOn: Binding(
                get: { task.include },
                set: { included in
                    var updated = task
                    updated.include = included
                    viewModel.updateAssistantDraft(updated)
                }
            )) {
                TextField("Task name", text: Binding(
                    get: { task.title },
                    set: { title in
                        var updated = task
                        updated.title = title
                        viewModel.updateAssistantDraft(updated)
                    }
                ))
                .textFieldStyle(.roundedBorder)
            }
            .disabled(!task.canBeSaved)

            // What kind of thing this is. The model can guess a course out of
            // the sentence and often does not; the label it is never asked for
            // at all. Both belong here, while the task is still a draft, rather
            // than as two more trips through the list afterwards.
            HStack(spacing: 6) {
                TextField("Course (optional)", text: Binding(
                    get: { task.courseName ?? "" },
                    set: { course in
                        var updated = task
                        let trimmed = course.trimmingCharacters(
                            in: .whitespacesAndNewlines
                        )
                        updated.courseName = trimmed.isEmpty ? nil : course
                        viewModel.updateAssistantDraft(updated)
                    }
                ))
                .textFieldStyle(.roundedBorder)
                .font(.caption)
                .accessibilityLabel("Course for \(task.title)")

                labelPicker(for: task)

                Button {
                    viewModel.removeAssistantDraft(task.id)
                } label: {
                    Image(systemName: "minus.circle")
                        .font(.caption)
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help("Remove this draft")
                .accessibilityLabel("Remove \(task.title)")
            }

            if let due = task.dueDate {
                DatePicker(
                    "Due",
                    selection: Binding(
                        get: { due },
                        set: { date in
                            var updated = task
                            updated.dueDate = date
                            viewModel.updateAssistantDraft(updated)
                        }
                    ),
                    displayedComponents: [.date, .hourAndMinute]
                )
                .font(.caption)
            } else {
                // The model gave no date. The app will not invent one.
                HStack(spacing: 6) {
                    Label("No date was given", systemImage: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(.orange)
                    Button("Set a date") {
                        var updated = task
                        updated.dueDate = Date().addingTimeInterval(86_400)
                        viewModel.updateAssistantDraft(updated)
                    }
                    .font(.caption)
                }
            }
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: AssistantStyle.cardCornerRadius)
                .fill(AssistantStyle.cardFill)
        )
        .overlay(
            RoundedRectangle(cornerRadius: AssistantStyle.cardCornerRadius)
                .strokeBorder(AssistantStyle.cardStroke)
        )
    }

    /// One list with None at the top, the same shape the row menu in the list
    /// uses, so putting a label on something means one thing everywhere.
    private func labelPicker(for task: AssistantDraftTask) -> some View {
        Menu {
            Button {
                var updated = task
                updated.labelID = nil
                viewModel.updateAssistantDraft(updated)
            } label: {
                if task.labelID == nil {
                    Label("None", systemImage: "checkmark")
                } else {
                    Text("None")
                }
            }

            Divider()

            ForEach(viewModel.eventLabels) { label in
                Button {
                    var updated = task
                    updated.labelID = label.id
                    viewModel.updateAssistantDraft(updated)
                } label: {
                    if task.labelID == label.id {
                        Label(label.name, systemImage: "checkmark")
                    } else {
                        Text(label.name)
                    }
                }
            }
        } label: {
            if let label = viewModel.eventLabels.first(where: {
                $0.id == task.labelID
            }) {
                Label {
                    Text(label.name).lineLimit(1)
                } icon: {
                    Circle()
                        .fill(Color(nsColor: label.color))
                        .frame(width: 8, height: 8)
                }
            } else {
                Label("Label", systemImage: "tag")
            }
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .font(.caption)
        .disabled(viewModel.eventLabels.isEmpty)
        .help(viewModel.eventLabels.isEmpty
            ? "Add a label in Settings first"
            : "Put one of your labels on this task")
        .accessibilityLabel("Label for \(task.title)")
    }

    // MARK: - The one box

    /// Everything typed into the assistant goes here.
    ///
    /// There used to be three fields stacked one above another — ask, describe
    /// a task, change these drafts — each with its own button. The third looked
    /// like the save field, which made it the one place where pressing the
    /// obvious thing threw away a sentence and wrote the drafts unchanged. One
    /// box, saying underneath it what it is about to do, cannot do that.
    private var composer: some View {
        VStack(alignment: .leading, spacing: 8) {
            TextField(
                composerPlaceholder,
                text: $viewModel.assistantComposerInput,
                axis: .vertical
            )
            .textFieldStyle(.plain)
            .font(.callout)
            .lineLimit(1...5)
            .focused($isComposerFocused)
            .onSubmit(send)
            .accessibilityLabel(composerDescription)

            HStack(spacing: 6) {
                if viewModel.assistantComposerRole == .reviseDrafts {
                    revisingNotice
                } else {
                    modeChip
                    automaticReading
                    if viewModel.showsAssistantModelPicker {
                        modelChip
                    }
                }

                Spacer(minLength: 0)

                sendControl
            }
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: AssistantStyle.cardCornerRadius)
                .fill(Color(nsColor: .textBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: AssistantStyle.cardCornerRadius)
                .strokeBorder(isComposerFocused
                    ? AssistantStyle.accent.opacity(0.55)
                    : AssistantStyle.cardStroke)
        )
        .padding(12)
        .background(.bar)
        .overlay(alignment: .top) {
            Divider()
        }
        // A click anywhere on the surround puts the caret in the field, so the
        // whole composer behaves as the one target it looks like.
        .contentShape(Rectangle())
        .onTapGesture {
            isComposerFocused = true
        }
    }

    /// Says what the box is for, in the box.
    ///
    /// Read off the mode rather than the role, so that on automatic it names
    /// both jobs instead of naming whichever one an empty box happens to
    /// resolve to.
    private var composerPlaceholder: String {
        guard viewModel.assistantComposerRole != .reviseDrafts else {
            return "Change these — “put them under 41021”"
        }
        switch viewModel.assistantComposerMode {
        case .automatic:
            return "Ask about your deadlines, or describe a task"
        case .ask:
            return "Ask about your deadlines"
        case .addTask:
            return "Describe a task — essay draft due next Friday at 5pm"
        }
    }

    /// What automatic has made of the sentence so far.
    ///
    /// The whole safety of guessing rests on this line: it appears as soon as
    /// there is something to read, so a wrong reading is caught and the chip
    /// changed before Return is ever pressed.
    @ViewBuilder
    private var automaticReading: some View {
        if viewModel.assistantComposerMode == .automatic, hasTypedText {
            Text(viewModel.assistantComposerRole == .addTask
                ? "will add a task"
                : "will answer")
                .font(.caption2)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .accessibilityLabel(viewModel.assistantComposerRole == .addTask
                    ? "Read as a task to add"
                    : "Read as a question to answer")
        }
    }

    private var composerDescription: String {
        switch viewModel.assistantComposerRole {
        case .ask:
            "Ask the assistant"
        case .addTask:
            "Describe a task"
        case .reviseDrafts:
            "Change the drafts above"
        }
    }

    /// Which of the two jobs the box has, named rather than drawn. A glyph here
    /// would be the eye button all over again.
    private var modeChip: some View {
        Menu {
            ForEach(AssistantComposerMode.allCases) { mode in
                Button {
                    viewModel.assistantComposerMode = mode
                    isComposerFocused = true
                } label: {
                    if mode == viewModel.assistantComposerMode {
                        Label(mode.title, systemImage: "checkmark")
                    } else {
                        Text(mode.title)
                    }
                }
            }
        } label: {
            Label(
                viewModel.assistantComposerMode.title,
                systemImage: viewModel.assistantComposerMode.systemImage
            )
            .font(.caption)
        }
        .menuStyle(.button)
        .buttonStyle(.bordered)
        .buttonBorderShape(.capsule)
        .controlSize(.small)
        .fixedSize()
        .help("Whether this box asks a question or writes a task")
        .accessibilityLabel(
            "Composer mode, \(viewModel.assistantComposerMode.title)"
        )
    }

    /// The toolbar's old Model button, rehoused beside the sentence it will
    /// answer. Only appears once there is more than one saved model.
    private var modelChip: some View {
        Menu {
            ForEach(viewModel.assistantProfiles) { profile in
                Button {
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
                viewModel.sidebarSelection = .settings
            }
        } label: {
            Text(viewModel.activeAssistantProfile?.name ?? "Model")
                .font(.caption)
                .lineLimit(1)
        }
        .menuStyle(.button)
        .buttonStyle(.bordered)
        .buttonBorderShape(.capsule)
        .controlSize(.small)
        .fixedSize()
        .help(viewModel.assistantModelDescription)
        .accessibilityLabel(viewModel.assistantModelDescription)
    }

    /// Replaces the mode chip while drafts are on screen, because the box is no
    /// longer the user's to point somewhere: a sentence typed now is about the
    /// review above it. This line is also where the reassurance about ticks and
    /// labels lives, next to the thing it reassures about.
    private var revisingNotice: some View {
        Label(
            viewModel.assistantDrafts.count == 1
                ? "Changing the draft above · ticks and labels kept"
                : "Changing the \(viewModel.assistantDrafts.count) drafts above · ticks and labels kept",
            systemImage: "arrow.turn.down.right"
        )
        .font(.caption)
        .foregroundStyle(.secondary)
        .lineLimit(2)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var sendControl: some View {
        Button(action: send) {
            if viewModel.isAssistantBusy {
                ProgressView()
                    .controlSize(.small)
                    .frame(width: 26, height: 26)
            } else {
                Image(systemName: "arrow.up")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 26, height: 26)
                    .background {
                        if viewModel.canSendComposer {
                            Circle().fill(AssistantStyle.mark)
                        } else {
                            Circle().fill(Color.secondary.opacity(0.35))
                        }
                    }
            }
        }
        .buttonStyle(.plain)
        .disabled(!viewModel.canSendComposer)
        .help(sendDescription)
        .accessibilityLabel(sendDescription)
    }

    private var sendDescription: String {
        switch viewModel.assistantComposerRole {
        case .ask:
            "Send this question"
        case .addTask:
            "Read tasks out of this sentence"
        case .reviseDrafts:
            "Rewrite the drafts above using this sentence"
        }
    }

    /// Something is typed and has not been sent. While this is true, Return
    /// belongs to the box rather than to Save Selected.
    private var hasTypedText: Bool {
        !viewModel.assistantComposerInput
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .isEmpty
    }

    private func send() {
        guard viewModel.canSendComposer else {
            return
        }
        Task { await viewModel.sendComposer() }
    }
}

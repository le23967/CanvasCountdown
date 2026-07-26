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

    /// Tinted fills stay faint so system label colours keep their contrast in
    /// both appearances.
    static func messageFill(isUser: Bool) -> Color {
        isUser ? accent.opacity(0.14) : Color.secondary.opacity(0.10)
    }
}

struct AssistantPanelView: View {
    @Bindable var viewModel: MainViewModel
    let onSaveDrafts: @MainActor ([AssistantDraftTask]) async -> Void

    @State private var request = ""

    @FocusState private var isRequestFocused: Bool
    @FocusState private var isQuestionFocused: Bool

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
                        Divider()
                        draftSection
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
            }
        }
        .frame(minWidth: 220)
    }

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: "sparkles")
                .foregroundStyle(AssistantStyle.accent)
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
                Button("Clear Conversation") {
                    viewModel.clearConversation()
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

    private var conversationSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            if viewModel.conversation.isEmpty {
                Text("Ask about your deadlines")
                    .font(.subheadline.weight(.semibold))

                // Counts and dates are worked out by the app and handed to the
                // model, so an answer about "how many" is not a guess.
                Text("Counts and dates come from your own list, not from the model.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                ForEach(MainViewModel.assistantSuggestions, id: \.self) { suggestion in
                    Button(suggestion) {
                        Task { await viewModel.ask(suggestion) }
                    }
                    .buttonStyle(.link)
                    .disabled(viewModel.isAssistantBusy)
                }
            } else {
                ForEach(viewModel.conversation) { message in
                    messageBubble(message)
                }

                Button("Clear conversation") {
                    viewModel.clearConversation()
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

            HStack(spacing: 6) {
                TextField(
                    "Ask anything about your deadlines",
                    text: $viewModel.assistantDraftInput,
                    axis: .vertical
                )
                    .textFieldStyle(.roundedBorder)
                    .lineLimit(1...4)
                    .focused($isQuestionFocused)
                    .onSubmit(sendQuestion)
                    .accessibilityLabel("Ask the assistant")

                Button {
                    sendQuestion()
                } label: {
                    Image(systemName: "arrow.up.circle.fill")
                        .foregroundStyle(
                            viewModel.assistantDraftInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                ? AnyShapeStyle(.secondary)
                                : AnyShapeStyle(AssistantStyle.accent)
                        )
                }
                .buttonStyle(.borderless)
                .disabled(
                    viewModel.isAssistantBusy
                        || viewModel.assistantDraftInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                )
                .accessibilityLabel("Send")
            }
        }
    }

    private func messageBubble(_ message: AssistantMessage) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(message.role == .user ? "You" : "Assistant")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary)
            Text(message.text)
                .font(.callout)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(10)
        .background(
            AssistantStyle.messageFill(isUser: message.role == .user),
            in: RoundedRectangle(cornerRadius: 8)
        )
    }

    private func sendQuestion() {
        let text = viewModel.assistantDraftInput
        viewModel.assistantDraftInput = ""
        Task { await viewModel.ask(text) }
    }

    private var draftSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Add a task in your own words")
                .font(.subheadline.weight(.semibold))

            TextField("Essay draft due next Friday at 5pm", text: $request, axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .lineLimit(2...4)
                .focused($isRequestFocused)
                .onSubmit(draft)
                .accessibilityLabel("Describe a task")

            HStack {
                Button("Draft Task") {
                    draft()
                }
                .disabled(
                    viewModel.isAssistantBusy
                        || request.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                )
                Spacer()
            }

            if !viewModel.assistantDrafts.isEmpty {
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
                    Spacer()
                    Button("Save Selected") {
                        Task {
                            await onSaveDrafts(viewModel.assistantDrafts)
                        }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!viewModel.hasSaveableDrafts)
                }
            }
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
        .background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 8))
    }

    private func draft() {
        let text = request
        Task {
            await viewModel.draftTask(from: text)
            request = ""
        }
    }
}

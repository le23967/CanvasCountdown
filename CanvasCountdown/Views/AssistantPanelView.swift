import SwiftUI

/// The assistant, as a panel beside the assignments rather than a modal sheet.
///
/// Kept next to the list on purpose: asking "what should I start" while the
/// deadlines are still visible is the whole point, and a sheet would cover them.
struct AssistantPanelView: View {
    @Bindable var viewModel: MainViewModel
    let onSaveDrafts: @MainActor ([AssistantDraftTask]) async -> Void

    @State private var request = ""
    @State private var question = ""
    @FocusState private var isRequestFocused: Bool
    @FocusState private var isQuestionFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
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
                .foregroundStyle(.tint)
                .accessibilityHidden(true)
            Text("Assistant")
                .font(.headline)
            Spacer()
            // Where the data goes, stated where the questions are asked.
            Label(
                viewModel.assistantSettings.staysOnThisMac ? "On this Mac" : "Cloud",
                systemImage: viewModel.assistantSettings.staysOnThisMac
                    ? "lock.fill"
                    : "arrow.up.right.circle.fill"
            )
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
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
                TextField("Ask anything about your deadlines", text: $question, axis: .vertical)
                    .textFieldStyle(.roundedBorder)
                    .lineLimit(1...4)
                    .focused($isQuestionFocused)
                    .onSubmit(sendQuestion)
                    .accessibilityLabel("Ask the assistant")

                Button {
                    sendQuestion()
                } label: {
                    Image(systemName: "arrow.up.circle.fill")
                }
                .buttonStyle(.borderless)
                .disabled(
                    viewModel.isAssistantBusy
                        || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
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
            message.role == .user
                ? AnyShapeStyle(.quaternary.opacity(0.35))
                : AnyShapeStyle(Color.accentColor.opacity(0.12)),
            in: RoundedRectangle(cornerRadius: 8)
        )
    }

    private func sendQuestion() {
        let text = question
        question = ""
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

import SwiftUI

struct ManualEventEditorView: View {
    @Environment(\.dismiss) private var dismiss

    @State private var draft: ManualEventDraft
    @State private var isSaving = false
    @State private var errorMessage: String?

    let onSave: @MainActor (ManualEventDraft) async throws -> Void

    init(
        draft: ManualEventDraft,
        onSave: @escaping @MainActor (ManualEventDraft) async throws -> Void
    ) {
        _draft = State(initialValue: draft)
        self.onSave = onSave
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("Assignment name", text: $draft.title)
                        .accessibilityLabel("Assignment name")

                    TextField("Course (optional)", text: $draft.courseName)
                        .accessibilityLabel("Course name, optional")

                    DatePicker(
                        "Due date",
                        selection: $draft.dueDate,
                        displayedComponents: [.date, .hourAndMinute]
                    )
                }

                if let errorMessage {
                    Section {
                        Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.red)
                            .textSelection(.enabled)
                    }
                }
            }
            .formStyle(.grouped)

            Divider()

            HStack {
                Button("Cancel", role: .cancel) {
                    dismiss()
                }
                .keyboardShortcut(.cancelAction)

                Spacer()

                Button(draft.eventID == nil ? "Add Event" : "Save Changes") {
                    save()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!draft.canSave || isSaving)
            }
            .padding()
        }
        .frame(width: 480, height: 300)
        .navigationTitle(draft.eventID == nil ? "Add Manual Event" : "Edit Event")
        .interactiveDismissDisabled(isSaving)
        .overlay {
            if isSaving {
                ProgressView("Saving…")
                    .padding(18)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            }
        }
    }

    private func save() {
        guard draft.canSave, !isSaving else {
            return
        }
        isSaving = true
        errorMessage = nil

        Task {
            do {
                try await onSave(draft)
                dismiss()
            } catch {
                errorMessage = error.localizedDescription
                isSaving = false
            }
        }
    }
}

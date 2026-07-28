import AppKit
import SwiftUI

/// Everything Settings can do to the labels, handed in rather than reached for.
struct EventLabelActions {
    var labels: [EventLabel]
    var canAdd: Bool
    var add: () -> Void
    var rename: (UUID, String) -> EventLabelNameError?
    var recolor: (UUID, String) -> Void
    var delete: (UUID) -> Void
    /// How many events currently carry the label, so deleting one can say what
    /// it is about to take off.
    var usageCount: (UUID) -> Int
}

/// One label in Settings: its colour, its name, and how to get rid of it.
struct EventLabelRow: View {
    let label: EventLabel
    let usageCount: Int
    let onRename: (String) -> EventLabelNameError?
    let onRecolor: (String) -> Void
    let onDelete: () -> Void

    @State private var draftName = ""
    @State private var nameError: String?
    @State private var isConfirmingDelete = false
    @FocusState private var isNameFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 10) {
                ColorPicker(
                    "Colour for \(label.name)",
                    selection: Binding(
                        get: { Color(nsColor: label.color) },
                        set: { onRecolor(NSColor($0).hexString) }
                    ),
                    supportsOpacity: false
                )
                .labelsHidden()

                // Hidden label and a bounded width: inside a Form a titled
                // field lays itself out as a full-width row, which pushed the
                // count and the delete button off the edge of the window.
                TextField("Name", text: $draftName)
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 240)
                    .focused($isNameFocused)
                    .onSubmit(commitName)
                    .onChange(of: isNameFocused) { _, focused in
                        // Committing on the way out as well as on Return, so a
                        // rename is not lost by clicking elsewhere.
                        if !focused {
                            commitName()
                        }
                    }

                Spacer(minLength: 8)

                Text(usageCount == 1 ? "1 event" : "\(usageCount) events")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()

                Button(role: .destructive) {
                    if usageCount == 0 {
                        onDelete()
                    } else {
                        isConfirmingDelete = true
                    }
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .help("Delete this label")
                .accessibilityLabel("Delete \(label.name)")
            }

            if let nameError {
                Text(nameError)
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
        .onAppear {
            draftName = label.name
        }
        .onChange(of: label.name) { _, newValue in
            if !isNameFocused {
                draftName = newValue
            }
        }
        .confirmationDialog(
            "Delete “\(label.name)”?",
            isPresented: $isConfirmingDelete,
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive, action: onDelete)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                usageCount == 1
                    ? "It will be removed from 1 event. The event itself stays."
                    : "It will be removed from \(usageCount) events. The events themselves stay."
            )
        }
    }

    private func commitName() {
        let trimmed = draftName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed != label.name else {
            nameError = nil
            return
        }
        if let error = onRename(trimmed) {
            nameError = error.message
            draftName = label.name
        } else {
            nameError = nil
        }
    }
}

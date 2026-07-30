import SwiftUI

/// What Settings needs to manage the saved models, without reaching into the
/// view model or the Keychain.
struct AssistantProfileActions {
    var profiles: [AssistantProfile]
    var activeID: UUID?
    var canAdd: Bool
    var select: (UUID) -> Void
    var add: (AssistantService) -> Void
    var addBlank: () -> Void
    var rename: (UUID, String) -> AssistantProfileNameError?
    var duplicate: (UUID) -> Void
    var delete: (UUID) -> Void
    var useService: (AssistantService) -> Void
}

/// One saved model in Settings: pick it, rename it, or get rid of it.
struct AssistantProfileRow: View {
    let profile: AssistantProfile
    let isSelected: Bool
    let canDelete: Bool
    let onSelect: () -> Void
    let onRename: (String) -> AssistantProfileNameError?
    let onDuplicate: () -> Void
    let onDelete: () -> Void

    @State private var draftName = ""
    @State private var nameError: String?
    @State private var isConfirmingDelete = false
    @FocusState private var isNameFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 10) {
                Button {
                    onSelect()
                } label: {
                    Image(systemName: isSelected
                        ? "largecircle.fill.circle"
                        : "circle")
                        .foregroundStyle(.tint)
                }
                .buttonStyle(.plain)
                .help(isSelected ? "In use" : "Use this model")
                .accessibilityLabel(
                    isSelected
                        ? "\(profile.name), in use"
                        : "Use \(profile.name)"
                )
                .accessibilityAddTraits(
                    isSelected ? [.isButton, .isSelected] : .isButton
                )

                // Hidden label and a bounded width: inside a Form a titled
                // field lays itself out as a full-width row, which would push
                // everything after it off the edge of the window.
                TextField("Name", text: $draftName)
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 190)
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

                // Reads the address rather than the label, so a model named
                // "local" but pointed somewhere else cannot claim privacy.
                Image(systemName: profile.staysOnThisMac
                    ? "lock.fill"
                    : "arrow.up.right.circle.fill")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help(profile.staysOnThisMac
                        ? "Runs on this Mac"
                        : "Sends titles, courses and dates to this service")
                    .accessibilityLabel(profile.staysOnThisMac
                        ? "Runs on this Mac"
                        : "Cloud model")

                Text(profile.subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 150, alignment: .trailing)
                    .help(profile.baseURL)

                Button {
                    onDuplicate()
                } label: {
                    Image(systemName: "plus.square.on.square")
                }
                .buttonStyle(.borderless)
                .help("Duplicate this model")
                .accessibilityLabel("Duplicate \(profile.name)")

                Button(role: .destructive) {
                    isConfirmingDelete = true
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .disabled(!canDelete)
                .help(canDelete
                    ? "Delete this model"
                    : "The last model cannot be deleted")
                .accessibilityLabel("Delete \(profile.name)")
            }

            if let nameError {
                Text(nameError)
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
        .onAppear {
            draftName = profile.name
        }
        .onChange(of: profile.name) { _, newValue in
            if !isNameFocused {
                draftName = newValue
            }
        }
        .confirmationDialog(
            "Delete “\(profile.name)”?",
            isPresented: $isConfirmingDelete,
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                onDelete()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Its address, model name and saved API key are removed from this Mac.")
        }
    }

    private func commitName() {
        let trimmed = draftName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed != profile.name else {
            nameError = nil
            return
        }
        if let error = onRename(trimmed) {
            nameError = error.message
            draftName = profile.name
        } else {
            nameError = nil
            draftName = trimmed
        }
    }
}

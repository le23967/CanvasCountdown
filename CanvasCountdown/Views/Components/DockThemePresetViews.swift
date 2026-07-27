import SwiftUI

/// Everything the Dock Appearance section can do to saved themes, in one value
/// rather than nine separate closures on `SettingsView`.
@MainActor
struct DockThemePresetActions {
    var presets: [UserDockThemePreset]
    var status: DockThemeStatus
    var applyBuiltIn: (DockThemePreset) -> Void
    var applySaved: (UserDockThemePreset) -> Void
    var save: (String) -> DockPresetNameError?
    var updateSelected: () -> Void
    var revertSelected: () -> Void
    var rename: (UUID, String) -> DockPresetNameError?
    var duplicate: (UUID) -> Void
    var delete: (UUID) -> Void
    var validateName: (String, UUID?) -> DockPresetNameError?
}

/// The theme row: what is in force, how to change it, and what to do about
/// edits that are not saved anywhere yet.
struct DockThemeRow: View {
    let actions: DockThemePresetActions

    @State private var isNaming = false
    @State private var isManaging = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("Theme", selection: selection) {
                Section("Built-in") {
                    ForEach(DockThemePreset.selectable) { preset in
                        Text(preset.title).tag(DockThemeChoice.builtIn(preset))
                    }
                }

                if !actions.presets.isEmpty {
                    Section("My Presets") {
                        ForEach(actions.presets) { preset in
                            Text(preset.name).tag(DockThemeChoice.saved(preset.id))
                        }
                    }
                }

                // Only present while it is the answer, and picking it is a
                // no-op: there is nothing to switch to.
                if case .unsaved = actions.status {
                    Text(DockThemeStatus.unsaved.title)
                        .tag(DockThemeChoice.unsaved)
                }
            }

            if actions.status.editedPreset != nil {
                Text(actions.status.title)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            // One line of buttons, so the section does not grow a block of
            // controls that are usually irrelevant.
            HStack(spacing: 8) {
                if actions.status.editedPreset != nil {
                    Button("Update Preset") { actions.updateSelected() }
                    Button("Save as New Preset…") { isNaming = true }
                    Button("Revert Changes") { actions.revertSelected() }
                } else if actions.status.isSavable {
                    Button("Save as Preset…") { isNaming = true }
                }

                Spacer(minLength: 0)

                Button("Manage Presets…") { isManaging = true }
                    .disabled(actions.presets.isEmpty)
            }
            .buttonStyle(.link)
            .font(.callout)
        }
        .sheet(isPresented: $isNaming) {
            DockPresetNameSheet(
                title: "Save Preset",
                initialName: suggestedName,
                confirmTitle: "Save",
                validate: { actions.validateName($0, nil) },
                onConfirm: { actions.save($0) }
            )
        }
        .sheet(isPresented: $isManaging) {
            DockPresetManagerSheet(actions: actions)
        }
    }

    private var selection: Binding<DockThemeChoice> {
        Binding(
            get: { DockThemeChoice(actions.status) },
            set: { choice in
                switch choice {
                case .builtIn(let preset):
                    actions.applyBuiltIn(preset)
                case .saved(let id):
                    if let preset = actions.presets.first(where: { $0.id == id }) {
                        actions.applySaved(preset)
                    }
                case .unsaved:
                    break
                }
            }
        )
    }

    /// Offers the edited preset's name as a starting point, which is what
    /// "Save as New Preset…" is usually a small variation on.
    private var suggestedName: String {
        actions.status.editedPreset.map { "\($0.name) Copy" } ?? ""
    }
}

/// Names a preset, and refuses a name the library would not accept.
struct DockPresetNameSheet: View {
    let title: String
    let initialName: String
    let confirmTitle: String
    let validate: (String) -> DockPresetNameError?
    /// Returns an error to keep the sheet open, or nil once it is done.
    let onConfirm: (String) -> DockPresetNameError?

    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var error: DockPresetNameError?
    @FocusState private var isFieldFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(.headline)

            TextField("Preset name", text: $name)
                .textFieldStyle(.roundedBorder)
                .focused($isFieldFocused)
                .onSubmit(confirm)
                .onChange(of: name) { _, _ in error = nil }
                .accessibilityLabel("Preset name")

            // Reserved height, so typing an invalid name does not make the
            // sheet jump.
            Text(error?.message ?? " ")
                .font(.caption)
                .foregroundStyle(error == nil ? Color.secondary : Color.red)
                .accessibilityHidden(error == nil)

            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(confirmTitle, action: confirm)
                    .keyboardShortcut(.defaultAction)
                    .disabled(validate(name) != nil)
            }
        }
        .padding(20)
        .frame(width: 340)
        .onAppear {
            name = initialName
            isFieldFocused = true
        }
    }

    private func confirm() {
        if let failure = onConfirm(name) {
            error = failure
            return
        }
        dismiss()
    }
}

/// Rename, duplicate and delete, away from the settings row so that row stays
/// one line.
struct DockPresetManagerSheet: View {
    let actions: DockThemePresetActions

    @Environment(\.dismiss) private var dismiss
    @State private var renaming: UserDockThemePreset?
    @State private var pendingDeletion: UserDockThemePreset?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Manage Presets")
                .font(.headline)

            if actions.presets.isEmpty {
                Text("No saved presets.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.vertical, 24)
            } else {
                List {
                    ForEach(actions.presets) { preset in
                        HStack(spacing: 10) {
                            DockThemeSwatch(colours: preset.colours)
                            Text(preset.name)
                            Spacer(minLength: 8)
                            Button("Rename") { renaming = preset }
                            Button("Duplicate") { actions.duplicate(preset.id) }
                            Button("Delete", role: .destructive) {
                                pendingDeletion = preset
                            }
                        }
                        .buttonStyle(.link)
                        .font(.callout)
                    }
                }
                .frame(height: 220)
            }

            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
        .sheet(item: $renaming) { preset in
            DockPresetNameSheet(
                title: "Rename Preset",
                initialName: preset.name,
                confirmTitle: "Rename",
                validate: { actions.validateName($0, preset.id) },
                onConfirm: { actions.rename(preset.id, $0) }
            )
        }
        // Deleting is the one action here that cannot be undone.
        .confirmationDialog(
            "Delete “\(pendingDeletion?.name ?? "")”?",
            isPresented: Binding(
                get: { pendingDeletion != nil },
                set: { if !$0 { pendingDeletion = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                if let preset = pendingDeletion {
                    actions.delete(preset.id)
                }
                pendingDeletion = nil
            }
            Button("Cancel", role: .cancel) { pendingDeletion = nil }
        } message: {
            Text("The colours stay in place. Only the saved preset is removed.")
        }
    }
}

/// A small sample of a preset's gradient, so presets are distinguishable
/// without reading their names.
struct DockThemeSwatch: View {
    let colours: DockAppearance.Colours

    var body: some View {
        RoundedRectangle(cornerRadius: 5, style: .continuous)
            .fill(
                LinearGradient(
                    colors: [
                        Color(nsColor: NSColor(hex: colours.backgroundTop) ?? .systemBlue),
                        Color(nsColor: NSColor(hex: colours.backgroundBottom) ?? .systemBlue),
                    ],
                    startPoint: .top,
                    endPoint: .bottom
                )
            )
            .overlay(
                RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .strokeBorder(.separator)
            )
            .frame(width: 22, height: 22)
            .accessibilityHidden(true)
    }
}

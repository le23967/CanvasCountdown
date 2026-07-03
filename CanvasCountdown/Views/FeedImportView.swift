import SwiftUI

struct FeedImportView: View {
    @Environment(\.dismiss) private var dismiss

    @State private var feedURLText: String
    @State private var previewItems: [ImportPreviewItem] = []
    @State private var selectedIDs: Set<String> = []
    @State private var errorMessage: String?
    @State private var isLoadingPreview = false
    @State private var isImporting = false
    @State private var revealFeedURL =
        FeedURLPresentationPolicy.isRevealedByDefault

    let isOnboarding: Bool
    let onPreview: @MainActor (URL) async throws -> [ImportPreviewItem]
    let onImport: @MainActor (URL, Set<String>) async throws -> ImportSummary

    init(
        initialURL: String = "",
        isOnboarding: Bool,
        onPreview: @escaping @MainActor (URL) async throws -> [ImportPreviewItem],
        onImport: @escaping @MainActor (URL, Set<String>) async throws -> ImportSummary
    ) {
        _feedURLText = State(initialValue: initialURL)
        self.isOnboarding = isOnboarding
        self.onPreview = onPreview
        self.onImport = onImport
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()

            if previewItems.isEmpty {
                connectionForm
            } else {
                previewList
            }

            Divider()
            actionBar
        }
        .frame(minWidth: 660, idealWidth: 720, minHeight: 500, idealHeight: 560)
        .interactiveDismissDisabled(isImporting)
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "calendar.badge.plus")
                .font(.system(size: 28))
                .foregroundStyle(.tint)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 4) {
                Text(isOnboarding ? "Connect your Canvas calendar" : "Import Canvas calendar")
                    .font(.title2.weight(.semibold))
                Text("Preview your deadlines before anything is saved.")
                    .foregroundStyle(.secondary)
            }

            Spacer()
        }
        .padding(20)
    }

    private var connectionForm: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Calendar feed URL")
                        .font(.headline)

                    HStack {
                        Group {
                            if revealFeedURL {
                                TextField(
                                    "https://…/calendar.ics",
                                    text: $feedURLText
                                )
                            } else {
                                SecureField(
                                    "https://…/calendar.ics",
                                    text: $feedURLText
                                )
                            }
                        }
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Canvas calendar feed URL")
                        .onSubmit(loadPreview)

                        Button {
                            revealFeedURL.toggle()
                        } label: {
                            Image(
                                systemName: revealFeedURL
                                    ? "eye.slash"
                                    : "eye"
                            )
                        }
                        .buttonStyle(.borderless)
                        .help(
                            revealFeedURL
                                ? "Hide feed URL"
                                : "Reveal feed URL"
                        )
                        .accessibilityLabel(
                            revealFeedURL
                                ? "Hide feed URL"
                                : "Reveal feed URL"
                        )
                    }

                    Label(
                        "Your private feed URL is stored in the macOS Keychain and is never included in diagnostics.",
                        systemImage: "lock.fill"
                    )
                    .font(.callout)
                    .foregroundStyle(.secondary)
                }

                VStack(alignment: .leading, spacing: 12) {
                    Text("Find it in Canvas")
                        .font(.headline)
                    instruction(number: 1, text: "Open Calendar in Canvas.")
                    instruction(number: 2, text: "Choose Calendar Feed at the bottom of the sidebar.")
                    instruction(number: 3, text: "Copy the iCal feed link and paste it above.")
                }

                if let errorMessage {
                    errorView(errorMessage)
                }
            }
            .frame(maxWidth: 620, alignment: .leading)
            .padding(24)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
    }

    private var previewList: some View {
        VStack(spacing: 0) {
            HStack {
                Text("\(selectedIDs.count) of \(previewItems.count) selected")
                    .font(.headline)
                Spacer()
                Button("Select All") {
                    selectedIDs = Set(previewItems.map(\.id))
                }
                .disabled(selectedIDs.count == previewItems.count)
                Button("Select None") {
                    selectedIDs.removeAll()
                }
                .disabled(selectedIDs.isEmpty)
            }
            .padding()

            List(previewItems, selection: Binding(
                get: { selectedIDs },
                set: { selectedIDs = $0 }
            )) { item in
                Toggle(isOn: Binding(
                    get: { selectedIDs.contains(item.id) },
                    set: { isSelected in
                        if isSelected {
                            selectedIDs.insert(item.id)
                        } else {
                            selectedIDs.remove(item.id)
                        }
                    }
                )) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(item.title)
                            .font(.body.weight(.medium))
                            .lineLimit(2)
                        HStack(spacing: 6) {
                            if let courseName = item.courseName, !courseName.isEmpty {
                                Text(courseName)
                                Text("•")
                                    .accessibilityHidden(true)
                            }
                            Text(item.dueDate, format: .dateTime.weekday(.abbreviated).month(.abbreviated).day().hour().minute())
                        }
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.checkbox)
                .padding(.vertical, 5)
                .tag(item.id)
            }
            .accessibilityLabel("Events available to import")

            if let errorMessage {
                errorView(errorMessage)
                    .padding()
            }
        }
    }

    private var actionBar: some View {
        HStack {
            Button("Cancel", role: .cancel) {
                dismiss()
            }
            .keyboardShortcut(.cancelAction)

            if !previewItems.isEmpty {
                Button("Change URL") {
                    previewItems = []
                    selectedIDs = []
                    errorMessage = nil
                }
            }

            Spacer()

            if previewItems.isEmpty {
                Button("Preview") {
                    loadPreview()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(feedURLText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isLoadingPreview)
            } else {
                Button("Import \(selectedIDs.count) Event\(selectedIDs.count == 1 ? "" : "s")") {
                    importSelection()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selectedIDs.isEmpty || isImporting)
            }
        }
        .padding()
        .overlay {
            if isLoadingPreview || isImporting {
                ProgressView(isLoadingPreview ? "Loading calendar…" : "Importing…")
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .background(.regularMaterial, in: Capsule())
            }
        }
    }

    private func instruction(number: Int, text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text("\(number)")
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background(.tint, in: Circle())
                .accessibilityHidden(true)
            Text(text)
        }
    }

    private func errorView(_ message: String) -> some View {
        Label {
            Text(message)
                .textSelection(.enabled)
        } icon: {
            Image(systemName: "exclamationmark.triangle.fill")
        }
        .foregroundStyle(.red)
        .accessibilityLabel("Import error: \(message)")
    }

    private func loadPreview() {
        guard !isLoadingPreview else {
            return
        }

        do {
            let url = try normalizedFeedURL()
            isLoadingPreview = true
            errorMessage = nil
            Task {
                do {
                    let items = try await onPreview(url)
                        .sorted { $0.dueDate < $1.dueDate }
                    previewItems = items
                    selectedIDs = Set(items.map(\.id))
                    if items.isEmpty {
                        errorMessage = "No importable dated events were found in this feed."
                    }
                } catch {
                    errorMessage = error.localizedDescription
                }
                isLoadingPreview = false
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func importSelection() {
        guard !isImporting else {
            return
        }

        do {
            let url = try normalizedFeedURL()
            isImporting = true
            errorMessage = nil
            Task {
                do {
                    _ = try await onImport(url, selectedIDs)
                    dismiss()
                } catch {
                    errorMessage = error.localizedDescription
                    isImporting = false
                }
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func normalizedFeedURL() throws -> URL {
        let value = feedURLText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var components = URLComponents(string: value) else {
            throw FeedURLInputError.invalid
        }
        if components.scheme?.lowercased() == "webcal" {
            components.scheme = "https"
        }
        guard components.scheme?.lowercased() == "https",
              let host = components.host,
              !host.isEmpty,
              let url = components.url else {
            throw FeedURLInputError.requiresHTTPS
        }
        return url
    }
}

private enum FeedURLInputError: LocalizedError {
    case invalid
    case requiresHTTPS

    var errorDescription: String? {
        switch self {
        case .invalid:
            "Enter a valid Canvas calendar feed URL."
        case .requiresHTTPS:
            "For your security, the calendar feed URL must use HTTPS."
        }
    }
}

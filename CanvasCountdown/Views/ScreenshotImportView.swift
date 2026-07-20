import SwiftUI
import UniformTypeIdentifiers

/// The screenshot import sheet: choose images, then review every row before
/// anything is stored. There is no path from recognition straight to import.
struct ScreenshotImportView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    @Bindable var viewModel: ScreenshotImportViewModel
    let existingCourseNames: [String]
    let onFinished: @MainActor () async -> Void

    @State private var isShowingFilePicker = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()

            switch viewModel.stage {
            case .choosing:
                chooser
            case .recognising:
                recognising
            case .reviewing:
                reviewTable
            case let .finished(summary):
                finished(summary)
            }

            Divider()
            actionBar
        }
        .frame(minWidth: 760, idealWidth: 900, minHeight: 520, idealHeight: 640)
        .fileImporter(
            isPresented: $isShowingFilePicker,
            allowedContentTypes: ScreenshotImportViewModel.supportedTypes,
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case let .success(urls):
                Task { await viewModel.load(urls: urls) }
            case .failure:
                viewModel.errorMessage = "Those screenshots could not be opened."
            }
        }
        .onDisappear {
            // Images and recognised text never outlive the sheet.
            viewModel.discard()
        }
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "text.viewfinder")
                .font(.system(size: 26))
                .foregroundStyle(.tint)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 4) {
                Text("Import from Canvas Screenshot")
                    .font(.title2.weight(.semibold))
                Text("Text recognition runs locally on this Mac. Images are not uploaded.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            Spacer()
        }
        .padding(20)
    }

    // MARK: - Choosing

    private var chooser: some View {
        VStack(spacing: 18) {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(
                    viewModel.isTargetedForDrop ? Color.accentColor : Color.secondary.opacity(0.5),
                    style: StrokeStyle(lineWidth: viewModel.isTargetedForDrop ? 2 : 1, dash: [6])
                )
                .background(
                    RoundedRectangle(cornerRadius: 12)
                        .fill(viewModel.isTargetedForDrop
                            ? Color.accentColor.opacity(0.08)
                            : Color.clear)
                )
                .frame(height: 180)
                .overlay {
                    VStack(spacing: 8) {
                        Image(systemName: "photo.on.rectangle.angled")
                            .font(.system(size: 30))
                            .foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                        Text("Drop screenshots here")
                            .font(.headline)
                        Text("PNG, JPEG, HEIC or TIFF")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .accessibilityLabel("Screenshot drop area. Drop PNG, JPEG, HEIC or TIFF files here.")
                .onDrop(
                    of: [.fileURL],
                    isTargeted: $viewModel.isTargetedForDrop
                ) { providers in
                    handleDrop(providers)
                }

            HStack(spacing: 12) {
                Button("Choose Screenshots…") {
                    isShowingFilePicker = true
                }
                .keyboardShortcut("o", modifiers: .command)

                Button("Paste Screenshot") {
                    Task { await viewModel.pasteFromClipboard() }
                }
                .keyboardShortcut("v", modifiers: .command)
            }

            if let notice = viewModel.noticeMessage {
                messageLabel(notice, systemImage: "info.circle", tint: .secondary)
            }
            if let error = viewModel.errorMessage {
                messageLabel(error, systemImage: "exclamationmark.triangle.fill", tint: .red)
            }

            Spacer()
        }
        .padding(24)
    }

    private var recognising: some View {
        VStack(spacing: 14) {
            Spacer()
            ProgressView()
                .controlSize(.large)
            Text(viewModel.progressText ?? "Reading screenshots…")
                .foregroundStyle(.secondary)
            Button("Cancel Reading") {
                viewModel.cancelRecognition()
            }
            Spacer()
        }
        .frame(maxWidth: .infinity)
        .padding(24)
    }

    // MARK: - Review

    private var reviewTable: some View {
        VStack(alignment: .leading, spacing: 10) {
            summaryRow

            List(selection: $viewModel.selectedCandidateID) {
                ForEach(viewModel.candidates) { candidate in
                    candidateRow(candidate)
                        .tag(candidate.id)
                }
            }
            .listStyle(.inset)
            .accessibilityLabel("Detected assignments awaiting review")

            if let notice = viewModel.noticeMessage {
                messageLabel(notice, systemImage: "info.circle", tint: .secondary)
            }
            if let error = viewModel.errorMessage {
                messageLabel(error, systemImage: "exclamationmark.triangle.fill", tint: .red)
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
    }

    private var summaryRow: some View {
        HStack(spacing: 14) {
            summaryChip("Ready", count: viewModel.readyCount, systemImage: "checkmark.circle.fill", tint: .green)
            summaryChip("Needs review", count: viewModel.needsReviewCount, systemImage: "exclamationmark.triangle.fill", tint: .orange)
            summaryChip("Invalid", count: viewModel.invalidCount, systemImage: "xmark.octagon.fill", tint: .red)
            summaryChip("Duplicates", count: viewModel.duplicateCount, systemImage: "doc.on.doc.fill", tint: .blue)

            Spacer()

            Button("Select All Ready") {
                viewModel.selectAllReady()
            }
            Button("Deselect All") {
                viewModel.deselectAll()
            }
        }
    }

    private func summaryChip(
        _ title: String,
        count: Int,
        systemImage: String,
        tint: Color
    ) -> some View {
        Label("\(title): \(count)", systemImage: systemImage)
            .font(.caption)
            .foregroundStyle(count == 0 ? AnyShapeStyle(.secondary) : AnyShapeStyle(tint))
            .accessibilityLabel("\(title), \(count)")
    }

    @ViewBuilder
    private func candidateRow(_ candidate: ScreenshotImportCandidate) -> some View {
        let binding = Binding<ScreenshotImportCandidate>(
            get: { viewModel.binding(for: candidate.id) ?? candidate },
            set: { viewModel.update($0) }
        )

        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Toggle("", isOn: binding.include)
                    .labelsHidden()
                    .disabled(!candidate.canBeImported)
                    .accessibilityLabel("Include \(candidate.title)")

                // Status is never colour alone: icon plus words.
                Label(candidate.status.title, systemImage: candidate.status.systemImage)
                    .font(.caption)
                    .foregroundStyle(statusTint(candidate.status))
                    .fixedSize()

                TextField("Assignment name", text: binding.title)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Assignment name")

                courseField(binding, candidate: candidate)
            }

            HStack(alignment: .firstTextBaseline, spacing: 10) {
                if let dueDate = candidate.dueDate {
                    DatePicker(
                        "Due",
                        selection: Binding(
                            get: { dueDate },
                            set: { newValue in
                                var updated = candidate
                                updated.dueDate = newValue
                                viewModel.update(updated)
                            }
                        ),
                        displayedComponents: [.date, .hourAndMinute]
                    )
                    .accessibilityLabel("Due date and time")
                } else {
                    Button("Set a due date…") {
                        var updated = candidate
                        updated.dueDate = Date().addingTimeInterval(86_400)
                        viewModel.update(updated)
                    }
                }

                Spacer()

                Text("Confidence \(Int(candidate.ocrConfidence * 100))%")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }

            if !candidate.originalDueText.isEmpty {
                Text("Recognised: \(candidate.originalDueText)")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }

            ForEach(candidate.warnings) { warning in
                Label(warning.message, systemImage: "info.circle")
                    .font(.caption2)
                    .foregroundStyle(warning.isBlocking ? .red : .orange)
            }

            if candidate.possibleDuplicate {
                Picker(
                    "Already stored",
                    selection: binding.selectedDuplicateAction
                ) {
                    ForEach(DuplicateResolution.allCases) { action in
                        Text(action.title).tag(action)
                    }
                }
                .pickerStyle(.segmented)
                .accessibilityLabel("Action for possible duplicate")
            }
        }
        .padding(.vertical, 6)
    }

    @ViewBuilder
    private func courseField(
        _ binding: Binding<ScreenshotImportCandidate>,
        candidate: ScreenshotImportCandidate
    ) -> some View {
        Menu {
            Button("No Course") {
                var updated = candidate
                updated.courseName = nil
                viewModel.update(updated)
            }
            if !existingCourseNames.isEmpty {
                Divider()
                ForEach(existingCourseNames, id: \.self) { course in
                    Button(course) {
                        var updated = candidate
                        updated.courseName = course
                        viewModel.update(updated)
                    }
                }
            }
            Divider()
            Button("Apply This Course To All Rows From This Screenshot") {
                viewModel.applyCourse(
                    candidate.courseName,
                    toScreenshot: candidate.sourceScreenshotID
                )
            }
            .disabled(candidate.courseName == nil)
        } label: {
            Text(candidate.courseName ?? "No Course")
                .lineLimit(1)
        }
        .frame(width: 160)
        .accessibilityLabel("Course, \(candidate.courseName ?? "none")")
    }

    private func statusTint(_ status: ScreenshotCandidateStatus) -> Color {
        switch status {
        case .ready:
            .green
        case .needsReview:
            .orange
        case .invalid:
            .red
        case .possibleDuplicate:
            .blue
        }
    }

    private func finished(_ summary: ScreenshotImportSummary) -> some View {
        VStack(spacing: 12) {
            Spacer()
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 34))
                .foregroundStyle(.green)
                .accessibilityHidden(true)
            Text("Import complete")
                .font(.title3.weight(.semibold))
            Text(summary.message)
                .foregroundStyle(.secondary)
            Spacer()
        }
        .frame(maxWidth: .infinity)
        .padding(24)
    }

    // MARK: - Actions

    private var actionBar: some View {
        HStack {
            Button("Cancel", role: .cancel) {
                dismiss()
            }
            .keyboardShortcut(.cancelAction)

            if viewModel.stage == .reviewing {
                Button("Back") {
                    viewModel.discard()
                }
            }

            Spacer()

            switch viewModel.stage {
            case .reviewing:
                Button("Import \(viewModel.importableCount) Selected") {
                    Task {
                        await viewModel.importSelected()
                        await onFinished()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!viewModel.canImport)
            case .finished:
                Button("Done") {
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            default:
                EmptyView()
            }
        }
        .padding()
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        Task {
            var urls: [URL] = []
            for provider in providers {
                if let url = try? await provider.loadItem(
                    forTypeIdentifier: UTType.fileURL.identifier
                ) as? Data,
                   let resolved = URL(
                       dataRepresentation: url,
                       relativeTo: nil
                   ) {
                    urls.append(resolved)
                }
            }
            guard !urls.isEmpty else {
                viewModel.errorMessage = "Those items are not image files."
                return
            }
            await viewModel.load(urls: urls)
        }
        return true
    }

    private func messageLabel(
        _ text: String,
        systemImage: String,
        tint: Color
    ) -> some View {
        Label(text, systemImage: systemImage)
            .font(.callout)
            .foregroundStyle(tint)
            .textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

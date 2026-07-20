import AppKit
import Foundation
import Observation
import UniformTypeIdentifiers

/// Drives the screenshot import sheet.
///
/// Holds the images for the length of the review only. Nothing here is written
/// to disk, and `discard()` drops every image and every recognised string the
/// moment the sheet closes.
@MainActor
@Observable
final class ScreenshotImportViewModel {
    enum Stage: Equatable {
        case choosing
        case recognising
        case reviewing
        case finished(ScreenshotImportSummary)
    }

    static let supportedTypes: [UTType] = [.png, .jpeg, .heic, .tiff]
    /// A generous ceiling: a full-screen Retina capture is far below this.
    static let maximumFileSizeBytes = 60 * 1_024 * 1_024

    var stage: Stage = .choosing
    var candidates: [ScreenshotImportCandidate] = []
    var errorMessage: String?
    var noticeMessage: String?
    var selectedCandidateID: UUID?
    var isTargetedForDrop = false

    private(set) var screenshotNames: [UUID: String] = [:]
    private(set) var progressText: String?

    @ObservationIgnored
    private var sources: [ScreenshotSource] = []
    @ObservationIgnored
    private let coordinator: any ScreenshotImportCoordinating
    @ObservationIgnored
    private let existingProvider: @Sendable () async throws -> [AssignmentSnapshot]
    @ObservationIgnored
    private var recognitionTask: Task<Void, Never>?

    init(
        coordinator: any ScreenshotImportCoordinating,
        existingProvider: @escaping @Sendable () async throws -> [AssignmentSnapshot]
    ) {
        self.coordinator = coordinator
        self.existingProvider = existingProvider
    }

    // MARK: - Summary

    var readyCount: Int { candidates.filter { $0.status == .ready }.count }
    var needsReviewCount: Int { candidates.filter { $0.status == .needsReview }.count }
    var invalidCount: Int { candidates.filter { $0.status == .invalid }.count }
    var duplicateCount: Int { candidates.filter { $0.status == .possibleDuplicate }.count }
    var importableCount: Int { candidates.filter(\.willBeImported).count }

    var canImport: Bool {
        importableCount > 0
    }

    var availableCourseNames: [String] {
        Array(Set(candidates.compactMap(\.courseName)))
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    // MARK: - Input

    /// Accepts file URLs from the picker or a drop. Unsupported or unreadable
    /// files are reported rather than silently dropped.
    func load(urls: [URL]) async {
        var accepted: [ScreenshotSource] = []
        var rejected: [String] = []

        for url in urls {
            let name = url.lastPathComponent
            // Security-scoped access is released as soon as the bytes are read;
            // the app never holds onto the user's folder.
            let scoped = url.startAccessingSecurityScopedResource()
            defer {
                if scoped {
                    url.stopAccessingSecurityScopedResource()
                }
            }

            guard Self.isSupported(url) else {
                rejected.append(name)
                continue
            }
            guard let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize,
                  size <= Self.maximumFileSizeBytes else {
                rejected.append(name)
                continue
            }
            guard let image = Self.loadImage(at: url) else {
                rejected.append(name)
                continue
            }
            accepted.append(ScreenshotSource(displayName: name, image: image))
        }

        if !rejected.isEmpty {
            errorMessage = "These files could not be used: "
                + rejected.joined(separator: ", ")
                + ". Use PNG, JPEG, HEIC or TIFF screenshots."
        }
        guard !accepted.isEmpty else {
            return
        }
        await recognise(accepted)
    }

    /// Accepts an image already on the pasteboard.
    func pasteFromClipboard() async {
        guard let image = Self.imageFromPasteboard() else {
            errorMessage = "There is no image on the clipboard to paste."
            return
        }
        await recognise([
            ScreenshotSource(displayName: "Pasted screenshot", image: image),
        ])
    }

    private func recognise(_ newSources: [ScreenshotSource]) async {
        sources.append(contentsOf: newSources)
        stage = .recognising
        progressText = newSources.count == 1
            ? "Reading 1 screenshot…"
            : "Reading \(newSources.count) screenshots…"

        let existing = (try? await existingProvider()) ?? []
        let session = await coordinator.buildSession(
            from: sources,
            existing: existing,
            now: .now
        )

        progressText = nil
        screenshotNames = session.screenshotNames
        candidates = session.candidates
        stage = session.candidates.isEmpty ? .choosing : .reviewing

        var notices: [String] = []
        if !session.failedImageNames.isEmpty {
            notices.append(
                "Could not read: " + session.failedImageNames.joined(separator: ", ")
            )
        }
        if !session.recognisedNothingNames.isEmpty {
            notices.append(
                "No Canvas deadlines were detected in "
                + session.recognisedNothingNames.joined(separator: ", ")
                + ". Make sure the screenshot includes the assignment title and the line beginning with “Due”."
            )
        }
        noticeMessage = notices.isEmpty ? nil : notices.joined(separator: "\n")
    }

    func cancelRecognition() {
        recognitionTask?.cancel()
        recognitionTask = nil
        progressText = nil
        stage = candidates.isEmpty ? .choosing : .reviewing
    }

    // MARK: - Review editing

    func binding(for id: UUID) -> ScreenshotImportCandidate? {
        candidates.first { $0.id == id }
    }

    func update(_ candidate: ScreenshotImportCandidate) {
        guard let index = candidates.firstIndex(where: { $0.id == candidate.id }) else {
            return
        }
        var updated = candidate
        // A hand-typed correction answers the warnings that prompted it.
        if updated.dueDate != nil {
            updated.warnings.removeAll { $0 == .missingDueDate }
        }
        candidates[index] = updated
    }

    func selectAllReady() {
        for index in candidates.indices where candidates[index].status == .ready {
            candidates[index].include = true
        }
    }

    func deselectAll() {
        for index in candidates.indices {
            candidates[index].include = false
        }
    }

    /// Applies one course to every row from the same screenshot, which is the
    /// common case: one screenshot is usually one subject.
    func applyCourse(_ course: String?, toScreenshot screenshotID: UUID) {
        for index in candidates.indices
        where candidates[index].sourceScreenshotID == screenshotID {
            candidates[index].courseName = course
        }
    }

    // MARK: - Commit

    func importSelected() async {
        guard canImport else {
            return
        }
        do {
            let summary = try await coordinator.commit(candidates, now: .now)
            stage = .finished(summary)
            // The images and recognised text have done their job.
            releaseImages()
        } catch {
            errorMessage = "The selected events could not be imported."
        }
    }

    /// Called when the sheet closes, however it closes.
    func discard() {
        recognitionTask?.cancel()
        recognitionTask = nil
        candidates = []
        screenshotNames = [:]
        releaseImages()
        stage = .choosing
    }

    private func releaseImages() {
        sources = []
    }

    /// Only for tests and the preview overlay: the images never leave the
    /// session.
    var retainedImageCount: Int {
        sources.count
    }

    func image(for screenshotID: UUID) -> CGImage? {
        sources.first { $0.id == screenshotID }?.image
    }

    // MARK: - File helpers

    static func isSupported(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension.lowercased()) else {
            return false
        }
        return supportedTypes.contains { type.conforms(to: $0) }
    }

    static func loadImage(at url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else {
            return nil
        }
        // Orientation is applied here so the parser never sees a rotated page.
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: false,
            kCGImageSourceShouldCache: false,
        ]
        return CGImageSourceCreateImageAtIndex(source, 0, options as CFDictionary)
    }

    static func imageFromPasteboard() -> CGImage? {
        let pasteboard = NSPasteboard.general
        guard let image = NSImage(pasteboard: pasteboard) else {
            return nil
        }
        var rect = CGRect(origin: .zero, size: image.size)
        return image.cgImage(forProposedRect: &rect, context: nil, hints: nil)
    }
}

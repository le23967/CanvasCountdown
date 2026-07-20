import Foundation

/// Flags rows that look like something already stored.
///
/// Marks only; never merges or deletes on its own. A screenshot is a lossy
/// picture of Canvas, so it is never allowed to overwrite authoritative feed
/// data without the user saying so.
enum ScreenshotDuplicateDetector {
    /// Same title on the same calendar day is treated as the same assignment.
    /// Requiring the exact minute would miss a screenshot read as 23:59 against
    /// a feed time of 23:59:59, while ignoring the day entirely would collapse
    /// weekly tasks that share a name.
    static func annotate(
        _ candidates: [ScreenshotImportCandidate],
        against existing: [AssignmentSnapshot],
        calendar: Calendar = .autoupdatingCurrent
    ) -> [ScreenshotImportCandidate] {
        candidates.map { candidate in
            var updated = candidate
            guard let dueDate = candidate.dueDate else {
                return updated
            }
            let title = normalisedTitle(candidate.title)
            guard !title.isEmpty else {
                return updated
            }

            let match = existing.first { stored in
                normalisedTitle(stored.title) == title
                    && calendar.isDate(
                        stored.dueDate,
                        inSameDayAs: dueDate
                    )
                    && coursesAgree(candidate.courseName, stored.courseName)
            }

            if let match {
                updated.possibleDuplicate = true
                updated.duplicateTargetID = match.id
                // A strong match defaults to skipping, which is the choice that
                // cannot lose anything.
                updated.selectedDuplicateAction = .skip
            }
            return updated
        }
    }

    /// Only a stated disagreement counts. A screenshot usually has no course at
    /// all, and that absence should not stop a duplicate being spotted.
    static func coursesAgree(_ lhs: String?, _ rhs: String?) -> Bool {
        guard let lhs = normalisedOptional(lhs), let rhs = normalisedOptional(rhs) else {
            return true
        }
        return lhs == rhs
    }

    /// Folds away the differences OCR introduces without merging genuinely
    /// different names.
    static func normalisedTitle(_ title: String) -> String {
        let lowered = title.lowercased()
            .replacingOccurrences(
                of: "[\u{2018}\u{2019}\u{201C}\u{201D}]",
                with: "'",
                options: .regularExpression
            )
            .replacingOccurrences(
                of: "[\\p{Punctuation}]",
                with: " ",
                options: .regularExpression
            )
            .replacingOccurrences(
                of: "\\s+",
                with: " ",
                options: .regularExpression
            )
        return lowered.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func normalisedOptional(_ value: String?) -> String? {
        guard let value else {
            return nil
        }
        let normalised = normalisedTitle(value)
        return normalised.isEmpty ? nil : normalised
    }
}

protocol ScreenshotImportCoordinating: Sendable {
    /// Recognises, parses and duplicate-checks, returning a session for review.
    /// Nothing is written to storage by this call.
    func buildSession(
        from sources: [ScreenshotSource],
        existing: [AssignmentSnapshot],
        now: Date
    ) async -> ScreenshotImportSession

    /// Writes only the rows the user confirmed.
    func commit(
        _ candidates: [ScreenshotImportCandidate],
        now: Date
    ) async throws -> ScreenshotImportSummary
}

actor ScreenshotImportCoordinator: ScreenshotImportCoordinating {
    private let ocr: any ScreenshotOCRServicing
    private let parser: any ScreenshotAssignmentParsing
    private let repository: any AssignmentRepository

    init(
        ocr: any ScreenshotOCRServicing,
        parser: any ScreenshotAssignmentParsing = CanvasScreenshotParser(),
        repository: any AssignmentRepository
    ) {
        self.ocr = ocr
        self.parser = parser
        self.repository = repository
    }

    func buildSession(
        from sources: [ScreenshotSource],
        existing: [AssignmentSnapshot],
        now: Date = .now
    ) async -> ScreenshotImportSession {
        var session = ScreenshotImportSession()

        for source in sources {
            session.screenshotNames[source.id] = source.displayName
            do {
                let observations = try await ocr.recognizeText(in: source)
                guard !observations.isEmpty else {
                    session.recognisedNothingNames.append(source.displayName)
                    continue
                }
                let candidates = parser.candidates(
                    from: observations,
                    imageName: source.displayName,
                    now: now
                )
                if candidates.isEmpty {
                    session.recognisedNothingNames.append(source.displayName)
                }
                session.candidates.append(contentsOf: candidates)
            } catch is CancellationError {
                // A cancelled run keeps whatever finished, and adds nothing.
                return session
            } catch {
                // One unreadable screenshot must not lose the others.
                session.failedImageNames.append(source.displayName)
            }
        }

        session.candidates = ScreenshotDuplicateDetector.annotate(
            session.candidates,
            against: existing
        )
        return session
    }

    func commit(
        _ candidates: [ScreenshotImportCandidate],
        now: Date = .now
    ) async throws -> ScreenshotImportSummary {
        var summary = ScreenshotImportSummary.empty
        var records: [AssignmentImportRecord] = []

        for candidate in candidates {
            guard candidate.willBeImported, let dueDate = candidate.dueDate else {
                summary.skipped += 1
                continue
            }
            let title = candidate.trimmedTitle
            guard !title.isEmpty else {
                summary.failed += 1
                continue
            }

            records.append(
                AssignmentImportRecord(
                    // A stable local identifier, so re-importing the same
                    // screenshot updates its row instead of duplicating it.
                    externalID: Self.localIdentifier(
                        title: title,
                        dueDate: dueDate,
                        courseName: candidate.courseName
                    ),
                    title: title,
                    courseName: candidate.courseName,
                    dueDate: dueDate,
                    source: .screenshot
                )
            )
        }

        guard !records.isEmpty else {
            return summary
        }

        // One write for the whole batch: a failure part-way leaves nothing
        // half-applied.
        let result = try await repository.upsert(records, importedAt: now)
        summary.added = result.insertedCount
        summary.updated = result.updatedCount
        summary.skipped += result.unchangedCount
        return summary
    }

    /// Derived from the confirmed fields only. No image bytes, no OCR
    /// transcript, nothing that could carry recognised text into storage.
    static func localIdentifier(
        title: String,
        dueDate: Date,
        courseName: String?
    ) -> String {
        let normalisedTitle = ScreenshotDuplicateDetector.normalisedTitle(title)
        let course = ScreenshotDuplicateDetector.normalisedTitle(courseName ?? "")
        let day = Int(dueDate.timeIntervalSinceReferenceDate / 86_400)
        return "screenshot|\(normalisedTitle)|\(course)|\(day)"
    }
}

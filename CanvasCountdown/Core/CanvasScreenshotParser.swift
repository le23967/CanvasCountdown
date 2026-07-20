import CoreGraphics
import Foundation

protocol ScreenshotAssignmentParsing: Sendable {
    func candidates(
        from observations: [OCRTextObservation],
        imageName: String,
        now: Date
    ) -> [ScreenshotImportCandidate]
}

/// Turns recognised text into reviewable assignment rows.
///
/// Deterministic and layout-driven: observations are grouped by vertical
/// position into blocks, then each block is read for a title and a "Due" line.
/// Nothing here guesses a deadline that was not on screen, and an availability
/// line is never promoted into one.
struct CanvasScreenshotParser: ScreenshotAssignmentParsing {
    /// Page furniture that appears in Canvas screenshots but is never an
    /// assignment.
    static let ignoredPhrases = [
        "upcoming assignments",
        "show by date",
        "show by type",
        "search",
        "assignments",
        "dashboard",
        "courses",
        "calendar",
        "inbox",
        "history",
        "help",
        "account",
        "past assignments",
        "undated assignments",
    ]

    /// Fraction of image height treated as the same block. Generous enough for
    /// a wrapped title plus its metadata, tight enough not to swallow the next
    /// assignment. Expressed as a fraction so browser zoom and Retina scaling
    /// do not change the result.
    private let blockGap: CGFloat

    init(blockGap: CGFloat = 0.035) {
        self.blockGap = blockGap
    }

    func candidates(
        from observations: [OCRTextObservation],
        imageName: String,
        now: Date = .now
    ) -> [ScreenshotImportCandidate] {
        let usable = observations
            .filter { !Self.isIgnoredChrome($0.text) }
            .sorted { lhs, rhs in
                if abs(lhs.boundingBox.minY - rhs.boundingBox.minY) > 0.004 {
                    return lhs.boundingBox.minY < rhs.boundingBox.minY
                }
                return lhs.boundingBox.minX < rhs.boundingBox.minX
            }

        return blocks(from: usable).compactMap { block in
            candidate(from: block, imageName: imageName, now: now)
        }
    }

    /// Groups observations that sit close together vertically. A block breaks
    /// when the next line is far below the previous one, or when a due line has
    /// already been seen and a new non-metadata line starts.
    func blocks(
        from observations: [OCRTextObservation]
    ) -> [[OCRTextObservation]] {
        var result: [[OCRTextObservation]] = []
        var current: [OCRTextObservation] = []
        var previousBottom: CGFloat?
        var currentHasDue = false

        for observation in observations {
            let isMetadata = CanvasDateTextParser.isDueLine(observation.text)
                || CanvasDateTextParser.isAvailabilityLine(observation.text)

            let farBelow = previousBottom.map {
                observation.boundingBox.minY - $0 > blockGap
            } ?? false
            let startsNewAssignment = currentHasDue && !isMetadata

            if !current.isEmpty, farBelow || startsNewAssignment {
                result.append(current)
                current = []
                currentHasDue = false
            }

            current.append(observation)
            if CanvasDateTextParser.isDueLine(observation.text) {
                currentHasDue = true
            }
            previousBottom = observation.boundingBox.maxY
        }

        if !current.isEmpty {
            result.append(current)
        }
        return result
    }

    private func candidate(
        from block: [OCRTextObservation],
        imageName: String,
        now: Date
    ) -> ScreenshotImportCandidate? {
        let dueLines = block.filter { CanvasDateTextParser.isDueLine($0.text) }
        let titleLines = block.filter { observation in
            !CanvasDateTextParser.isDueLine(observation.text)
                && !CanvasDateTextParser.isAvailabilityLine(observation.text)
                && !Self.isPointsLine(observation.text)
        }

        let title = titleLines
            .map(\.text)
            .joined(separator: " ")
            .replacingOccurrences(
                of: "\\s+",
                with: " ",
                options: .regularExpression
            )
            .trimmingCharacters(in: .whitespacesAndNewlines)

        // A block with neither a name nor a deadline is page furniture.
        guard !title.isEmpty || !dueLines.isEmpty else {
            return nil
        }
        guard !title.isEmpty else {
            return nil
        }

        var warnings: [ScreenshotImportWarning] = []
        var parsed: ParsedScreenshotDate?

        if dueLines.isEmpty {
            warnings.append(.missingDueDate)
        } else {
            // Multiple "Due" phrases in one block cannot be resolved safely, so
            // the first is offered and the ambiguity is declared.
            if dueLines.count > 1 {
                warnings.append(.multipleDueDates)
            }
            parsed = dueLines
                .lazy
                .compactMap { CanvasDateTextParser.parse($0.text, now: now) }
                .first
            if parsed == nil {
                warnings.append(.missingDueDate)
            }
        }

        if let parsed {
            warnings.append(contentsOf: parsed.warnings)
        }

        let ocrConfidence = block.map(\.confidence).reduce(0, +)
            / Double(max(1, block.count))
        if ocrConfidence < 0.5 {
            warnings.append(.lowConfidence)
        }

        let dueText = dueLines.map(\.text).joined(separator: " · ")

        return ScreenshotImportCandidate(
            sourceScreenshotID: block[0].screenshotID,
            sourceImageName: imageName,
            title: title,
            dueDate: parsed?.date,
            originalTitleText: titleLines.map(\.text).joined(separator: " "),
            originalDueText: dueText,
            ocrConfidence: ocrConfidence,
            parserConfidence: Self.parserConfidence(
                hasDue: parsed != nil,
                warnings: warnings
            ),
            warnings: Array(Set(warnings)).sorted { $0.message < $1.message },
            inferredYear: parsed?.inferredYear,
            correctedOCRText: parsed?.correctedText,
            boundingRegion: Self.union(of: block)
        )
    }

    static func isIgnoredChrome(_ text: String) -> Bool {
        let normalised = CanvasDateTextParser.normalisedForMatching(text)
        guard !normalised.isEmpty else {
            return true
        }
        return ignoredPhrases.contains(normalised)
    }

    /// "10 pts" is a score, never a date.
    static func isPointsLine(_ text: String) -> Bool {
        let normalised = CanvasDateTextParser.normalisedForMatching(text)
        return normalised.range(
            of: "^\\d+(\\.\\d+)?\\s*(pts|points|point)$",
            options: .regularExpression
        ) != nil
    }

    private static func parserConfidence(
        hasDue: Bool,
        warnings: [ScreenshotImportWarning]
    ) -> Double {
        guard hasDue else {
            return 0
        }
        let penalty = Double(warnings.count) * 0.15
        return max(0.1, min(1, 1 - penalty))
    }

    private static func union(of block: [OCRTextObservation]) -> CGRect {
        block.dropFirst().reduce(block.first?.boundingBox ?? .zero) {
            $0.union($1.boundingBox)
        }
    }
}

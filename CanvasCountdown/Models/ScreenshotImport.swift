import CoreGraphics
import Foundation

/// One piece of text Vision recognised, with everything the parser needs to
/// reason about where it sat on screen.
///
/// Coordinates are normalised to 0...1 with the origin at the **top left**,
/// which is the opposite of Vision's own convention. Converting once here keeps
/// the flip out of the parser, where an inverted comparison would silently sort
/// rows upside down.
struct OCRTextObservation: Equatable, Sendable {
    let text: String
    let confidence: Double
    let boundingBox: CGRect
    let alternatives: [String]
    let screenshotID: UUID
    let pass: RecognitionPass

    init(
        text: String,
        confidence: Double,
        boundingBox: CGRect,
        alternatives: [String] = [],
        screenshotID: UUID = UUID(),
        pass: RecognitionPass = .original
    ) {
        self.text = text
        self.confidence = confidence
        self.boundingBox = boundingBox
        self.alternatives = alternatives
        self.screenshotID = screenshotID
        self.pass = pass
    }

    var verticalCentre: CGFloat {
        boundingBox.midY
    }
}

/// Which recognition attempt produced an observation. Recorded so a candidate
/// can say where it came from rather than quietly mixing passes.
enum RecognitionPass: String, Equatable, Sendable {
    case original
    case enhanced
}

/// Something the app changed, guessed, or could not resolve. Every one of these
/// is shown in review; none of them is applied silently.
enum ScreenshotImportWarning: Equatable, Hashable, Sendable, Identifiable {
    case yearInferred(Int)
    case timeCorrected(from: String, to: String)
    case multipleDueDates
    case missingDueDate
    case missingTime
    case dateInPast
    case lowConfidence
    case titleUncertain

    var id: String { message }

    var message: String {
        switch self {
        case let .yearInferred(year):
            "Year was not visible and was inferred as \(year)."
        case let .timeCorrected(from, to):
            "The recognised time “\(from)” was interpreted as \(to)."
        case .multipleDueDates:
            "More than one possible due date was detected."
        case .missingDueDate:
            "No due date was found. Add one before importing."
        case .missingTime:
            "No due time was found."
        case .dateInPast:
            "The detected date is already in the past."
        case .lowConfidence:
            "Text recognition was uncertain about this row."
        case .titleUncertain:
            "The assignment name may be incomplete."
        }
    }

    /// Whether the row cannot be imported until the user fixes it.
    var isBlocking: Bool {
        self == .missingDueDate
    }
}

enum ScreenshotCandidateStatus: String, Equatable, Sendable {
    case ready
    case needsReview
    case invalid
    case possibleDuplicate

    var title: String {
        switch self {
        case .ready:
            "Ready"
        case .needsReview:
            "Needs review"
        case .invalid:
            "Invalid"
        case .possibleDuplicate:
            "Possible duplicate"
        }
    }

    /// Paired with the colour everywhere it is shown, so status never depends
    /// on colour alone.
    var systemImage: String {
        switch self {
        case .ready:
            "checkmark.circle.fill"
        case .needsReview:
            "exclamationmark.triangle.fill"
        case .invalid:
            "xmark.octagon.fill"
        case .possibleDuplicate:
            "doc.on.doc.fill"
        }
    }
}

/// What to do about a row that looks like something already stored.
enum DuplicateResolution: String, Equatable, Sendable, CaseIterable, Identifiable {
    case skip
    case updateExisting
    case importSeparately

    var id: String { rawValue }

    var title: String {
        switch self {
        case .skip:
            "Skip"
        case .updateExisting:
            "Update existing"
        case .importSeparately:
            "Import separately"
        }
    }
}

/// One row awaiting the user's decision. Everything here is editable in review;
/// nothing reaches storage without an explicit confirmation.
struct ScreenshotImportCandidate: Identifiable, Equatable, Sendable {
    let id: UUID
    let sourceScreenshotID: UUID
    let sourceImageName: String

    var include: Bool
    var title: String
    var courseName: String?
    var dueDate: Date?

    let originalTitleText: String
    let originalDueText: String
    let ocrConfidence: Double
    let parserConfidence: Double
    var warnings: [ScreenshotImportWarning]
    var inferredYear: Int?
    var correctedOCRText: String?
    var possibleDuplicate: Bool
    var duplicateTargetID: UUID?
    var selectedDuplicateAction: DuplicateResolution
    let boundingRegion: CGRect

    init(
        id: UUID = UUID(),
        sourceScreenshotID: UUID,
        sourceImageName: String,
        include: Bool = true,
        title: String,
        courseName: String? = nil,
        dueDate: Date?,
        originalTitleText: String,
        originalDueText: String,
        ocrConfidence: Double,
        parserConfidence: Double,
        warnings: [ScreenshotImportWarning] = [],
        inferredYear: Int? = nil,
        correctedOCRText: String? = nil,
        possibleDuplicate: Bool = false,
        duplicateTargetID: UUID? = nil,
        selectedDuplicateAction: DuplicateResolution = .skip,
        boundingRegion: CGRect = .zero
    ) {
        self.id = id
        self.sourceScreenshotID = sourceScreenshotID
        self.sourceImageName = sourceImageName
        self.include = include
        self.title = title
        self.courseName = courseName
        self.dueDate = dueDate
        self.originalTitleText = originalTitleText
        self.originalDueText = originalDueText
        self.ocrConfidence = ocrConfidence
        self.parserConfidence = parserConfidence
        self.warnings = warnings
        self.inferredYear = inferredYear
        self.correctedOCRText = correctedOCRText
        self.possibleDuplicate = possibleDuplicate
        self.duplicateTargetID = duplicateTargetID
        self.selectedDuplicateAction = selectedDuplicateAction
        self.boundingRegion = boundingRegion
    }

    var trimmedTitle: String {
        title.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Confidence is only ever used to sort human attention. It never decides
    /// that a row is correct.
    var status: ScreenshotCandidateStatus {
        if dueDate == nil || trimmedTitle.isEmpty {
            return .invalid
        }
        if possibleDuplicate {
            return .possibleDuplicate
        }
        return warnings.isEmpty ? .ready : .needsReview
    }

    var canBeImported: Bool {
        status != .invalid
    }

    /// True when this row would actually write something on confirmation.
    var willBeImported: Bool {
        guard include, canBeImported else {
            return false
        }
        if possibleDuplicate, selectedDuplicateAction == .skip {
            return false
        }
        return true
    }
}

/// One review session. Held in memory only: the images live here and nowhere
/// else, and are dropped when the session ends.
struct ScreenshotImportSession: Equatable, Sendable {
    var candidates: [ScreenshotImportCandidate]
    var screenshotNames: [UUID: String]
    var failedImageNames: [String]
    var recognisedNothingNames: [String]

    init(
        candidates: [ScreenshotImportCandidate] = [],
        screenshotNames: [UUID: String] = [:],
        failedImageNames: [String] = [],
        recognisedNothingNames: [String] = []
    ) {
        self.candidates = candidates
        self.screenshotNames = screenshotNames
        self.failedImageNames = failedImageNames
        self.recognisedNothingNames = recognisedNothingNames
    }

    var readyCount: Int {
        candidates.filter { $0.status == .ready }.count
    }

    var needsReviewCount: Int {
        candidates.filter { $0.status == .needsReview }.count
    }

    var invalidCount: Int {
        candidates.filter { $0.status == .invalid }.count
    }

    var duplicateCount: Int {
        candidates.filter { $0.status == .possibleDuplicate }.count
    }

    var importableCount: Int {
        candidates.filter(\.willBeImported).count
    }

    var isEmpty: Bool {
        candidates.isEmpty
    }
}

/// Outcome shown after the user confirms.
struct ScreenshotImportSummary: Equatable, Sendable {
    var added: Int
    var updated: Int
    var skipped: Int
    var failed: Int

    static let empty = ScreenshotImportSummary(
        added: 0,
        updated: 0,
        skipped: 0,
        failed: 0
    )

    var message: String {
        var parts = ["\(added) added", "\(updated) updated"]
        if skipped > 0 {
            parts.append("\(skipped) skipped")
        }
        if failed > 0 {
            parts.append("\(failed) failed")
        }
        return parts.joined(separator: ", ")
    }
}

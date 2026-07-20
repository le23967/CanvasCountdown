import CoreGraphics
import CoreImage
import Foundation
import ImageIO
import Vision

/// One screenshot being processed. Held only for the length of a review
/// session; nothing here is written to disk or to SwiftData.
struct ScreenshotSource: @unchecked Sendable, Identifiable {
    let id: UUID
    let displayName: String
    /// `CGImage` is immutable once created, so passing it between actors is
    /// safe even though it is not formally `Sendable`.
    let image: CGImage

    init(id: UUID = UUID(), displayName: String, image: CGImage) {
        self.id = id
        self.displayName = displayName
        self.image = image
    }
}

enum ScreenshotOCRError: LocalizedError, Equatable, Sendable {
    case unreadableImage(name: String)
    case unsupportedFileType(name: String)
    case imageTooLarge(name: String)
    case recognitionFailed
    case cancelled

    var errorDescription: String? {
        switch self {
        case let .unreadableImage(name):
            "“\(name)” could not be opened as an image."
        case let .unsupportedFileType(name):
            "“\(name)” is not a supported image type. Use PNG, JPEG, HEIC or TIFF."
        case let .imageTooLarge(name):
            "“\(name)” is too large to process. Crop it to the assignment list and try again."
        case .recognitionFailed:
            "Text could not be recognised in that screenshot."
        case .cancelled:
            "Text recognition was cancelled."
        }
    }

    /// Safe for redacted diagnostics: an identifier only, never recognised text.
    var diagnosticCode: String {
        switch self {
        case .unreadableImage:
            "ocr.unreadable-image"
        case .unsupportedFileType:
            "ocr.unsupported-type"
        case .imageTooLarge:
            "ocr.image-too-large"
        case .recognitionFailed:
            "ocr.recognition-failed"
        case .cancelled:
            "ocr.cancelled"
        }
    }
}

/// Recognises text. Deliberately knows nothing about Canvas: every assumption
/// about rows, deadlines and availability lives in the parser, so this layer
/// can be swapped for a stub in tests without losing that logic.
protocol ScreenshotOCRServicing: Sendable {
    func recognizeText(
        in source: ScreenshotSource
    ) async throws -> [OCRTextObservation]
}

actor VisionScreenshotOCRService: ScreenshotOCRServicing {
    /// Beyond this the image is scaled down. Canvas text stays legible well
    /// below it, and Vision slows sharply on very large screenshots.
    static let maximumPixelDimension = 4_000
    /// A screenshot larger than this is rejected rather than chewed on.
    static let absolutePixelLimit = 12_000

    /// Below this mean confidence, a second pass on a contrast-boosted copy is
    /// tried. The passes are never merged: whichever reads better is used
    /// whole, so two contradictory readings cannot be stitched together.
    private let lowConfidenceThreshold: Double
    private let recognitionLanguages: [String]

    init(
        lowConfidenceThreshold: Double = 0.45,
        recognitionLanguages: [String] = ["en-US", "zh-Hans"]
    ) {
        self.lowConfidenceThreshold = lowConfidenceThreshold
        self.recognitionLanguages = recognitionLanguages
    }

    func recognizeText(
        in source: ScreenshotSource
    ) async throws -> [OCRTextObservation] {
        guard source.image.width <= Self.absolutePixelLimit,
              source.image.height <= Self.absolutePixelLimit else {
            throw ScreenshotOCRError.imageTooLarge(name: source.displayName)
        }

        let prepared = Self.downscaledIfNeeded(source.image)
        try Task.checkCancellation()

        let first = try recognise(
            prepared,
            screenshotID: source.id,
            pass: .original
        )
        try Task.checkCancellation()

        let confidence = Self.meanConfidence(first)
        guard first.isEmpty || confidence < lowConfidenceThreshold else {
            return first
        }

        // Conservative retry: grayscale with a modest contrast lift. Canvas
        // metadata is light grey, so anything harsher erases the "Due" line the
        // parser depends on.
        guard let enhanced = Self.contrastEnhanced(prepared) else {
            return first
        }
        let second = try recognise(
            enhanced,
            screenshotID: source.id,
            pass: .enhanced
        )
        return Self.meanConfidence(second) > confidence ? second : first
    }

    private func recognise(
        _ image: CGImage,
        screenshotID: UUID,
        pass: RecognitionPass
    ) throws -> [OCRTextObservation] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.recognitionLanguages = recognitionLanguages

        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        do {
            try handler.perform([request])
        } catch {
            throw ScreenshotOCRError.recognitionFailed
        }

        let results = request.results ?? []
        return results.compactMap { observation in
            let candidates = observation.topCandidates(3)
            guard let best = candidates.first else {
                return nil
            }
            return OCRTextObservation(
                text: best.string,
                confidence: Double(best.confidence),
                boundingBox: Self.topLeftBox(observation.boundingBox),
                alternatives: candidates.dropFirst().map(\.string),
                screenshotID: screenshotID,
                pass: pass
            )
        }
    }

    /// Vision reports a bottom-left origin; everything downstream assumes top
    /// left, so the flip happens once, here.
    static func topLeftBox(_ box: CGRect) -> CGRect {
        CGRect(
            x: box.minX,
            y: 1 - box.maxY,
            width: box.width,
            height: box.height
        )
    }

    static func meanConfidence(_ observations: [OCRTextObservation]) -> Double {
        guard !observations.isEmpty else {
            return 0
        }
        return observations.map(\.confidence).reduce(0, +)
            / Double(observations.count)
    }

    private static func downscaledIfNeeded(_ image: CGImage) -> CGImage {
        let longest = max(image.width, image.height)
        guard longest > maximumPixelDimension else {
            // Never upscaled: a crisp screenshot is already ideal input.
            return image
        }
        let scale = CGFloat(maximumPixelDimension) / CGFloat(longest)
        let width = Int((CGFloat(image.width) * scale).rounded())
        let height = Int((CGFloat(image.height) * scale).rounded())
        guard width > 0, height > 0,
              let context = CGContext(
                  data: nil,
                  width: width,
                  height: height,
                  bitsPerComponent: 8,
                  bytesPerRow: 0,
                  space: CGColorSpaceCreateDeviceRGB(),
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else {
            return image
        }
        context.interpolationQuality = .high
        context.draw(
            image,
            in: CGRect(x: 0, y: 0, width: width, height: height)
        )
        return context.makeImage() ?? image
    }

    private static func contrastEnhanced(_ image: CGImage) -> CGImage? {
        let input = CIImage(cgImage: image)
        guard let filter = CIFilter(name: "CIColorControls") else {
            return nil
        }
        filter.setValue(input, forKey: kCIInputImageKey)
        filter.setValue(0.0, forKey: kCIInputSaturationKey)
        filter.setValue(1.25, forKey: kCIInputContrastKey)
        guard let output = filter.outputImage else {
            return nil
        }
        return CIContext().createCGImage(output, from: output.extent)
    }
}

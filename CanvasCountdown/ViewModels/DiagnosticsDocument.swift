import Foundation
import SwiftUI
import UniformTypeIdentifiers

struct DiagnosticsDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.plainText]

    private let content: String

    init(content: String) {
        self.content = DiagnosticRedactor.redact(content)
    }

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents,
              let content = String(data: data, encoding: .utf8) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        self.content = DiagnosticRedactor.redact(content)
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: Data(content.utf8))
    }
}

enum DiagnosticRedactor {
    static func redact(_ value: String) -> String {
        var result = value

        // Feed URLs contain bearer-like secrets in their paths/query values.
        // Diagnostics do not need any URL, so redact all of them defensively.
        result = replacing(
            pattern: #"(?i)\b(?:https?|webcal)://[^\s"'<>]+"#,
            in: result,
            with: "[REDACTED URL]"
        )

        // Also cover non-URL serialized values attached to common secret keys.
        result = replacing(
            pattern: #"(?i)(feed[\s_-]*url|calendar[\s_-]*feed)(\s*[:=]\s*)[^,\n}\]]+"#,
            in: result,
            with: "$1$2[REDACTED]"
        )

        return result
    }

    private static func replacing(
        pattern: String,
        in value: String,
        with replacement: String
    ) -> String {
        guard let expression = try? NSRegularExpression(pattern: pattern) else {
            return "[Diagnostics unavailable: redaction failed]"
        }
        let range = NSRange(value.startIndex..<value.endIndex, in: value)
        return expression.stringByReplacingMatches(
            in: value,
            range: range,
            withTemplate: replacement
        )
    }
}

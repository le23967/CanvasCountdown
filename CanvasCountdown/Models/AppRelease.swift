import Foundation

/// A version number, compared the way people expect rather than the way strings
/// sort.
///
/// "1.10.0" is newer than "1.9.0"; as text it is not. Getting that backwards
/// would leave everyone on 1.9.0 being told they are up to date for the rest of
/// the app's life, which is the one failure an update check cannot recover from
/// on its own.
struct AppVersion: Equatable, Comparable, Sendable, CustomStringConvertible {
    /// Zero-padded to three, so "1.2" and "1.2.0" are the same version.
    private let components: [Int]
    let description: String

    /// Reads "1.2.0", "v1.2.0" or "1.2". Anything that is not a run of numbers
    /// separated by dots is not a version this app produces, and is refused
    /// rather than guessed at.
    init?(_ text: String) {
        var trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("v") || trimmed.hasPrefix("V") {
            trimmed.removeFirst()
        }
        guard !trimmed.isEmpty else {
            return nil
        }
        let parts = trimmed.split(separator: ".", omittingEmptySubsequences: false)
        guard !parts.isEmpty, parts.count <= 4 else {
            return nil
        }
        var numbers: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.allSatisfy(\.isNumber),
                  let value = Int(part) else {
                return nil
            }
            numbers.append(value)
        }
        while numbers.count < 3 {
            numbers.append(0)
        }
        components = numbers
        description = trimmed
    }

    /// The version of the running app, read from its own bundle.
    static func current(
        bundle: Bundle = .main
    ) -> AppVersion? {
        guard let text = bundle.object(
            forInfoDictionaryKey: "CFBundleShortVersionString"
        ) as? String else {
            return nil
        }
        return AppVersion(text)
    }

    static func < (lhs: AppVersion, rhs: AppVersion) -> Bool {
        for index in 0..<max(lhs.components.count, rhs.components.count) {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right {
                return left < right
            }
        }
        return false
    }

    static func == (lhs: AppVersion, rhs: AppVersion) -> Bool {
        !(lhs < rhs) && !(rhs < lhs)
    }
}

/// A published release, as much of it as this app has any use for.
struct AppRelease: Equatable, Sendable {
    let version: AppVersion
    /// The tag, kept as published so a link built from it cannot be wrong.
    let tag: String
    let name: String
    /// The release notes, as written. Shown in full only when asked for.
    let notes: String
    /// Where the disk image is. Absent when a release was published without
    /// one, which is a release nobody can install.
    let downloadURL: URL?
    /// The page to open when there is nothing to download, or when someone
    /// would rather read first.
    let pageURL: URL

    /// The first line or two of the notes, for a banner that must not become a
    /// wall of text.
    var summary: String {
        let firstParagraph = notes
            .components(separatedBy: "\n\n")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .first { line in
                !line.isEmpty && !line.hasPrefix("Copyright")
            }
        guard let firstParagraph else {
            return ""
        }
        // Markdown marks read as noise in one line of a banner: nothing here
        // renders them, so they arrive as literal asterisks and backticks.
        let flattened = firstParagraph
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "**", with: "")
            .replacingOccurrences(of: "`", with: "")
            .replacingOccurrences(of: "#", with: "")
            .trimmingCharacters(in: .whitespaces)
        guard flattened.count > 150 else {
            return flattened
        }
        return flattened.prefix(149).trimmingCharacters(
            in: .whitespacesAndNewlines
        ) + "…"
    }
}

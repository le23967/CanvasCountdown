import Foundation

/// The result of reading one date phrase, with everything the review screen
/// needs to explain what happened.
struct ParsedScreenshotDate: Equatable, Sendable {
    var date: Date
    var warnings: [ScreenshotImportWarning]
    var inferredYear: Int?
    var correctedText: String?
    var hasExplicitTime: Bool
}

/// Reads Canvas date phrases out of recognised text.
///
/// Written for OCR output rather than clean strings, but the corrections are
/// deliberately narrow: only substitutions that are unambiguous in a date are
/// applied, and each one is reported. Broad fuzzy matching would quietly turn a
/// misread into a confident wrong deadline, which is worse than asking.
enum CanvasDateTextParser {
    /// English month names are always accepted, whatever the system language,
    /// because Canvas renders them in English for most institutions.
    private static let months: [String: Int] = [
        "jan": 1, "january": 1,
        "feb": 2, "february": 2,
        "mar": 3, "march": 3,
        "apr": 4, "april": 4,
        "may": 5,
        "jun": 6, "june": 6,
        "jul": 7, "july": 7,
        "aug": 8, "august": 8,
        "sep": 9, "sept": 9, "september": 9,
        "oct": 10, "october": 10,
        "nov": 11, "november": 11,
        "dec": 12, "december": 12,
    ]

    /// Phrases that introduce a real deadline.
    static let dueMarkers = ["due"]

    /// Phrases that introduce an availability window. These are never deadlines.
    static let availabilityMarkers = [
        "not available until",
        "available until",
        "available from",
        "available after",
        "availability",
        "opens",
    ]

    static func isAvailabilityLine(_ text: String) -> Bool {
        let lowered = normalisedForMatching(text)
        return availabilityMarkers.contains { lowered.contains($0) }
    }

    static func isDueLine(_ text: String) -> Bool {
        guard !isAvailabilityLine(text) else {
            return false
        }
        let lowered = normalisedForMatching(text)
        return dueMarkers.contains { marker in
            lowered.range(of: "\\b\(marker)\\b", options: .regularExpression) != nil
        }
    }

    /// Collapses OCR spacing so phrase matching is not defeated by "Due  4  Aug".
    static func normalisedForMatching(_ text: String) -> String {
        text.lowercased()
            .replacingOccurrences(
                of: "\\s+",
                with: " ",
                options: .regularExpression
            )
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Parses one phrase such as "Due 4 Aug at 16:00".
    ///
    /// Returns nil when nothing date-like is present; an availability phrase is
    /// refused outright so it can never become a deadline.
    static func parse(
        _ text: String,
        now: Date = .now,
        calendar: Calendar = .autoupdatingCurrent
    ) -> ParsedScreenshotDate? {
        guard !isAvailabilityLine(text) else {
            return nil
        }

        var warnings: [ScreenshotImportWarning] = []
        var correctedText: String?

        let cleaned = correctOCRNoise(text)
        if cleaned != normalisedForMatching(text) {
            correctedText = cleaned
        }

        let time: ParsedTime?
        switch parseTime(in: cleaned) {
        case .none:
            time = nil
            warnings.append(.missingTime)
        case .invalid:
            // A time was written but cannot exist. Falling back to end of day
            // would invent a deadline the screenshot never showed.
            return nil
        case let .parsed(value):
            time = value
            // Report the substitution using what the screenshot actually said.
            if let original = originalTimeText(in: text),
               original.caseInsensitiveCompare(value.rendered) != .orderedSame {
                warnings.append(
                    .timeCorrected(from: original, to: value.rendered)
                )
            }
        }

        var inferredYear: Int?
        var components: DateComponents?

        if let relative = parseRelativeDay(in: cleaned, now: now, calendar: calendar) {
            components = calendar.dateComponents(
                [.year, .month, .day],
                from: relative
            )
        } else if let absolute = parseDayAndMonth(in: cleaned) {
            var parts = DateComponents()
            parts.day = absolute.day
            parts.month = absolute.month
            if let year = absolute.year {
                parts.year = year
            } else {
                let year = inferYear(
                    day: absolute.day,
                    month: absolute.month,
                    now: now,
                    calendar: calendar
                )
                parts.year = year
                inferredYear = year
                warnings.append(.yearInferred(year))
            }
            components = parts
        }

        guard var parts = components else {
            return nil
        }

        parts.hour = time?.hour ?? 23
        parts.minute = time?.minute ?? 59
        parts.second = 0

        // Rejects 31 February and friends: a rolled-over date is a wrong date.
        guard let day = parts.day, let month = parts.month, let year = parts.year,
              isRealDate(day: day, month: month, year: year, calendar: calendar),
              let date = calendar.date(from: parts) else {
            return nil
        }

        if date < now {
            warnings.append(.dateInPast)
        }

        return ParsedScreenshotDate(
            date: date,
            warnings: warnings,
            inferredYear: inferredYear,
            correctedText: correctedText,
            hasExplicitTime: time != nil
        )
    }

    // MARK: - Pieces

    private struct ParsedTime {
        let hour: Int
        let minute: Int
        let rendered: String
    }

    /// Distinguishes "no time was written" from "a time was written but is
    /// impossible". The two must not be treated alike: the first falls back to
    /// end of day, the second rejects the whole phrase.
    private enum TimeParseResult {
        case none
        case invalid
        case parsed(ParsedTime)
    }

    /// The time exactly as it appeared before any correction, for the warning.
    static func originalTimeText(in text: String) -> String? {
        let normalised = normalisedForMatching(text)
        guard let expression = try? NSRegularExpression(
            pattern: "\\d{1,2}\\s*[:.]\\s*[0-9oOlI]{2}\\s*(am|pm)?",
            options: [.caseInsensitive]
        ) else {
            return nil
        }
        let range = NSRange(normalised.startIndex..<normalised.endIndex, in: normalised)
        guard let match = expression.firstMatch(in: normalised, range: range),
              let matched = Range(match.range, in: normalised) else {
            return nil
        }
        return String(normalised[matched]).trimmingCharacters(in: .whitespaces)
    }

    /// Only substitutions that cannot mean anything else inside a date.
    static func correctOCRNoise(_ text: String) -> String {
        var value = normalisedForMatching(text)
        // "al" and "af" are common misreads of "at" between a date and a time.
        value = value.replacingOccurrences(
            of: "\\b(al|af|ai)\\b(?=\\s*\\d)",
            with: "at",
            options: .regularExpression
        )
        // A letter O or lowercase l inside an otherwise numeric time.
        value = value.replacingOccurrences(
            of: "(?<=\\d)[o](?=\\d)",
            with: "0",
            options: [.regularExpression, .caseInsensitive]
        )
        value = value.replacingOccurrences(
            of: "(?<=\\d[:.])[o](?=\\d)",
            with: "0",
            options: [.regularExpression, .caseInsensitive]
        )
        value = value.replacingOccurrences(
            of: "(?<=\\d)[o](?=[:.]\\d)",
            with: "0",
            options: [.regularExpression, .caseInsensitive]
        )
        // A full stop between hour and minute.
        value = value.replacingOccurrences(
            of: "(?<=\\d)\\.(?=\\d\\d\\b)",
            with: ":",
            options: .regularExpression
        )
        return value
    }

    private static func parseTime(in text: String) -> TimeParseResult {
        // 16:00, 4:00pm, 4 pm
        let colonPattern = "(\\d{1,2})\\s*[:]\\s*(\\d{2})\\s*(am|pm)?"
        if let match = firstMatch(colonPattern, in: text) {
            let hour = Int(match[1]) ?? 0
            let minute = Int(match[2]) ?? 0
            let meridiem = match.count > 3 ? match[3] : ""
            guard let resolved = resolveHour(hour, meridiem: meridiem),
                  (0...59).contains(minute) else {
                return .invalid
            }
            let rendered = String(format: "%02d:%02d", resolved, minute)
            return .parsed(
                ParsedTime(hour: resolved, minute: minute, rendered: rendered)
            )
        }

        // A bare four-digit time such as "at 1600".
        if let match = firstMatch("\\bat\\s+(\\d{4})\\b", in: text) {
            let digits = match[1]
            let hour = Int(digits.prefix(2)) ?? 0
            let minute = Int(digits.suffix(2)) ?? 0
            guard (0...23).contains(hour), (0...59).contains(minute) else {
                return .invalid
            }
            return .parsed(
                ParsedTime(
                    hour: hour,
                    minute: minute,
                    rendered: String(format: "%02d:%02d", hour, minute)
                )
            )
        }

        // "4pm" with no minutes.
        if let match = firstMatch("(\\d{1,2})\\s*(am|pm)\\b", in: text) {
            let hour = Int(match[1]) ?? 0
            guard let resolved = resolveHour(hour, meridiem: match[2]) else {
                return .invalid
            }
            return .parsed(
                ParsedTime(
                    hour: resolved,
                    minute: 0,
                    rendered: String(format: "%02d:00", resolved)
                )
            )
        }
        return .none
    }

    private static func resolveHour(_ hour: Int, meridiem: String) -> Int? {
        switch meridiem {
        case "am":
            guard (1...12).contains(hour) else { return nil }
            return hour == 12 ? 0 : hour
        case "pm":
            guard (1...12).contains(hour) else { return nil }
            return hour == 12 ? 12 : hour + 12
        default:
            guard (0...23).contains(hour) else { return nil }
            return hour
        }
    }

    private static func parseRelativeDay(
        in text: String,
        now: Date,
        calendar: Calendar
    ) -> Date? {
        if text.contains("today") {
            return calendar.startOfDay(for: now)
        }
        if text.contains("tomorrow") {
            return calendar.date(
                byAdding: .day,
                value: 1,
                to: calendar.startOfDay(for: now)
            )
        }
        return nil
    }

    private static func parseDayAndMonth(
        in text: String
    ) -> (day: Int, month: Int, year: Int?)? {
        let monthNames = months.keys.sorted { $0.count > $1.count }.joined(separator: "|")

        // "4 Aug 2026" or "4 Aug"
        if let match = firstMatch(
            "\\b(\\d{1,2})\\s+(\(monthNames))\\b(?:\\s+(\\d{4}))?",
            in: text
        ) {
            guard let day = Int(match[1]), let month = months[match[2]] else {
                return nil
            }
            let year = match.count > 3 ? Int(match[3]) : nil
            return (day, month, year)
        }

        // "Aug 4 2026" or "Aug 4"
        if let match = firstMatch(
            "\\b(\(monthNames))\\s+(\\d{1,2})\\b(?:,?\\s+(\\d{4}))?",
            in: text
        ) {
            guard let month = months[match[1]], let day = Int(match[2]) else {
                return nil
            }
            let year = match.count > 3 ? Int(match[3]) : nil
            return (day, month, year)
        }
        return nil
    }

    /// Chooses the nearest plausible year, preferring the next twelve months.
    /// The choice is always surfaced as a warning rather than assumed correct.
    static func inferYear(
        day: Int,
        month: Int,
        now: Date,
        calendar: Calendar
    ) -> Int {
        let currentYear = calendar.component(.year, from: now)
        var parts = DateComponents()
        parts.day = day
        parts.month = month
        parts.year = currentYear
        parts.hour = 23
        parts.minute = 59

        guard let thisYear = calendar.date(from: parts) else {
            return currentYear
        }
        // Anything more than a month behind is far likelier to be next year's
        // deadline than one already missed.
        let monthAgo = calendar.date(byAdding: .day, value: -31, to: now) ?? now
        return thisYear < monthAgo ? currentYear + 1 : currentYear
    }

    static func isRealDate(
        day: Int,
        month: Int,
        year: Int,
        calendar: Calendar
    ) -> Bool {
        guard (1...12).contains(month), (1...31).contains(day) else {
            return false
        }
        var parts = DateComponents()
        parts.year = year
        parts.month = month
        parts.day = day
        guard let date = calendar.date(from: parts) else {
            return false
        }
        // Rejects a rolled-over date such as 31 February becoming 3 March.
        return calendar.component(.day, from: date) == day
            && calendar.component(.month, from: date) == month
    }

    private static func firstMatch(
        _ pattern: String,
        in text: String
    ) -> [String]? {
        guard let expression = try? NSRegularExpression(
            pattern: pattern,
            options: [.caseInsensitive]
        ) else {
            return nil
        }
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        guard let match = expression.firstMatch(in: text, range: range) else {
            return nil
        }
        return (0..<match.numberOfRanges).map { index in
            guard let range = Range(match.range(at: index), in: text) else {
                return ""
            }
            return String(text[range])
        }
    }
}

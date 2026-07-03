import Foundation

/// The subset of an iCalendar event that Canvas Countdown imports.
public struct ParsedCalendarEvent: Equatable, Sendable {
    public let uid: String?
    public let summary: String
    public let startDate: Date
    public let endDate: Date?
    public let description: String?
    public let url: String?
    public let lastModified: Date?
    public let isAllDay: Bool
    public let timeZoneIdentifier: String?
    public let sequence: Int?

    public init(
        uid: String?,
        summary: String,
        startDate: Date,
        endDate: Date?,
        description: String?,
        url: String?,
        lastModified: Date?,
        isAllDay: Bool,
        timeZoneIdentifier: String?,
        sequence: Int? = nil
    ) {
        self.uid = uid
        self.summary = summary
        self.startDate = startDate
        self.endDate = endDate
        self.description = description
        self.url = url
        self.lastModified = lastModified
        self.isAllDay = isAllDay
        self.timeZoneIdentifier = timeZoneIdentifier
        self.sequence = sequence
    }
}

/// A recoverable problem with one entry in a feed. Valid entries are still returned.
public struct ICSParseWarning: Equatable, Sendable {
    public let eventUID: String?
    public let line: Int?
    public let message: String

    public init(eventUID: String?, line: Int?, message: String) {
        self.eventUID = eventUID
        self.line = line
        self.message = message
    }
}

public struct ICSParseResult: Equatable, Sendable {
    public let events: [ParsedCalendarEvent]
    public let warnings: [ICSParseWarning]
    public let cancelledEventUIDs: Set<String>

    public init(
        events: [ParsedCalendarEvent],
        warnings: [ICSParseWarning],
        cancelledEventUIDs: Set<String> = []
    ) {
        self.events = events
        self.warnings = warnings
        self.cancelledEventUIDs = cancelledEventUIDs
    }
}

public enum ICSParserError: Error, Equatable, Sendable {
    case invalidTextEncoding
    case notCalendarData
}

extension ICSParserError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .invalidTextEncoding:
            "The calendar feed is not valid UTF-8 text."
        case .notCalendarData:
            "The downloaded content is not an iCalendar feed."
        }
    }
}

public protocol ICSParsing: Sendable {
    func parse(_ data: Data, defaultTimeZone: TimeZone) throws -> [ParsedCalendarEvent]
    func parseResult(
        _ data: Data,
        defaultTimeZone: TimeZone
    ) throws -> ICSParseResult
}

public extension ICSParsing {
    func parseResult(
        _ data: Data,
        defaultTimeZone: TimeZone
    ) throws -> ICSParseResult {
        ICSParseResult(
            events: try parse(data, defaultTimeZone: defaultTimeZone),
            warnings: []
        )
    }
}

/// A dependency-free RFC 5545 parser for the fields used by Canvas Countdown.
///
/// Parsing is deliberately tolerant at the event level: a malformed VEVENT produces a
/// warning and does not prevent other valid deadlines from being imported.
public struct ICSParser: ICSParsing, Sendable {
    public init() {}

    public func parse(
        _ data: Data,
        defaultTimeZone: TimeZone = .current
    ) throws -> [ParsedCalendarEvent] {
        try parseResult(data, defaultTimeZone: defaultTimeZone).events
    }

    public func parseResult(
        _ data: Data,
        defaultTimeZone: TimeZone = .current
    ) throws -> ICSParseResult {
        guard var text = String(data: data, encoding: .utf8) else {
            throw ICSParserError.invalidTextEncoding
        }
        if text.first == "\u{feff}" {
            text.removeFirst()
        }
        return try parseResult(text, defaultTimeZone: defaultTimeZone)
    }

    public func parse(
        _ text: String,
        defaultTimeZone: TimeZone = .current
    ) throws -> [ParsedCalendarEvent] {
        try parseResult(text, defaultTimeZone: defaultTimeZone).events
    }

    public func parseResult(
        _ text: String,
        defaultTimeZone: TimeZone = .current
    ) throws -> ICSParseResult {
        let logicalLines = Self.unfold(text)
        let contentLines = logicalLines.compactMap { sourceLine -> ContentLine? in
            Self.parseContentLine(sourceLine.text, sourceLine: sourceLine.number)
        }

        let isCalendar = contentLines.contains {
            $0.name == "BEGIN" && $0.value.uppercased() == "VCALENDAR"
        }
        guard isCalendar else {
            throw ICSParserError.notCalendarData
        }

        let timeZoneAliases = Self.timeZoneAliases(in: contentLines)
        let calendarTimeZone = Self.calendarTimeZone(
            in: contentLines,
            aliases: timeZoneAliases
        )
        let floatingTimeZone = calendarTimeZone ?? defaultTimeZone
        let calendarMethodIsCancellation = contentLines.contains {
            $0.name == "METHOD" && $0.value.trimmingCharacters(in: .whitespacesAndNewlines)
                .caseInsensitiveCompare("CANCEL") == .orderedSame
        }

        var events: [ParsedCalendarEvent] = []
        var warnings: [ICSParseWarning] = []
        var cancelledEventUIDs: Set<String> = []
        var eventLines: [ContentLine]?
        var nestedComponentDepth = 0

        for line in contentLines {
            let componentName = line.value.trimmingCharacters(in: .whitespacesAndNewlines)
                .uppercased()

            if eventLines == nil {
                if line.name == "BEGIN", componentName == "VEVENT" {
                    eventLines = []
                    nestedComponentDepth = 0
                }
                continue
            }

            if line.name == "BEGIN" {
                nestedComponentDepth += 1
                continue
            }

            if line.name == "END" {
                if nestedComponentDepth > 0 {
                    nestedComponentDepth -= 1
                    continue
                }

                if componentName == "VEVENT", let completedLines = eventLines {
                    let eventResult = Self.makeEvent(
                        from: completedLines,
                        defaultTimeZone: floatingTimeZone,
                        timeZoneAliases: timeZoneAliases,
                        calendarMethodIsCancellation: calendarMethodIsCancellation
                    )
                    if let event = eventResult.event {
                        events.append(event)
                    }
                    if let cancelledUID = eventResult.cancelledUID {
                        cancelledEventUIDs.insert(
                            Self.normalizedUID(cancelledUID)
                        )
                    }
                    warnings.append(contentsOf: eventResult.warnings)
                    eventLines = nil
                }
                continue
            }

            if nestedComponentDepth == 0 {
                eventLines?.append(line)
            }
        }

        if let unfinishedLines = eventLines {
            let uid = Self.firstValue(named: "UID", in: unfinishedLines)
            warnings.append(
                ICSParseWarning(
                    eventUID: uid,
                    line: unfinishedLines.first?.sourceLine,
                    message: "An unfinished VEVENT was ignored."
                )
            )
        }

        let activeEvents = Self.deduplicated(
            events.filter { event in
                guard let uid = event.uid else {
                    return true
                }
                return !cancelledEventUIDs.contains(
                    Self.normalizedUID(uid)
                )
            }
        )
        return ICSParseResult(
            events: activeEvents,
            warnings: warnings,
            cancelledEventUIDs: cancelledEventUIDs
        )
    }
}

private extension ICSParser {
    struct SourceLine {
        var text: String
        let number: Int
    }

    struct ContentLine {
        let name: String
        let parameters: [String: String]
        let value: String
        let sourceLine: Int
    }

    struct ParsedDate {
        let date: Date
        let isAllDay: Bool
        let timeZoneIdentifier: String?
    }

    struct EventConstructionResult {
        let event: ParsedCalendarEvent?
        let warnings: [ICSParseWarning]
        let cancelledUID: String?
    }

    static func normalizedUID(_ uid: String) -> String {
        uid.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
    }

    static func deduplicated(
        _ events: [ParsedCalendarEvent]
    ) -> [ParsedCalendarEvent] {
        var result: [ParsedCalendarEvent] = []
        var indexByUID: [String: Int] = [:]

        for event in events {
            guard let uid = event.uid else {
                result.append(event)
                continue
            }
            let key = normalizedUID(uid)
            guard let existingIndex = indexByUID[key] else {
                indexByUID[key] = result.count
                result.append(event)
                continue
            }

            let existing = result[existingIndex]
            let shouldReplace: Bool
            if existing.sequence != event.sequence,
               existing.sequence != nil || event.sequence != nil {
                shouldReplace =
                    (event.sequence ?? Int.min)
                    > (existing.sequence ?? Int.min)
            } else {
                switch (existing.lastModified, event.lastModified) {
                case let (existingDate?, incomingDate?):
                    shouldReplace = incomingDate >= existingDate
                case (nil, _?):
                    shouldReplace = true
                case (_?, nil):
                    shouldReplace = false
                case (nil, nil):
                    // With no revision metadata, RFC 5545 producers
                    // conventionally emit the later revision last.
                    shouldReplace = true
                }
            }
            if shouldReplace {
                result[existingIndex] = event
            }
        }
        return result
    }

    static func unfold(_ text: String) -> [SourceLine] {
        let normalized = text
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        let physicalLines = normalized.split(
            separator: "\n",
            omittingEmptySubsequences: false
        )

        var logicalLines: [SourceLine] = []
        logicalLines.reserveCapacity(physicalLines.count)

        for (offset, physicalLine) in physicalLines.enumerated() {
            let line = String(physicalLine)
            if (line.first == " " || line.first == "\t"), !logicalLines.isEmpty {
                logicalLines[logicalLines.count - 1].text.append(contentsOf: line.dropFirst())
            } else {
                logicalLines.append(SourceLine(text: line, number: offset + 1))
            }
        }
        return logicalLines
    }

    static func parseContentLine(_ line: String, sourceLine: Int) -> ContentLine? {
        guard !line.isEmpty, let colonIndex = firstUnquotedColon(in: line) else {
            return nil
        }

        let header = String(line[..<colonIndex])
        let valueStart = line.index(after: colonIndex)
        let value = String(line[valueStart...])
        let headerParts = splitUnquoted(header, separator: ";")
        guard let rawName = headerParts.first else {
            return nil
        }

        let name = rawName.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard !name.isEmpty else {
            return nil
        }

        var parameters: [String: String] = [:]
        for part in headerParts.dropFirst() {
            guard let equalsIndex = part.firstIndex(of: "=") else {
                continue
            }
            let key = part[..<equalsIndex].trimmingCharacters(in: .whitespacesAndNewlines)
                .uppercased()
            var parameterValue = String(part[part.index(after: equalsIndex)...])
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if parameterValue.count >= 2,
               parameterValue.first == "\"",
               parameterValue.last == "\"" {
                parameterValue.removeFirst()
                parameterValue.removeLast()
            }
            parameters[key] = decodeParameterText(parameterValue)
        }

        return ContentLine(
            name: name,
            parameters: parameters,
            value: value,
            sourceLine: sourceLine
        )
    }

    static func firstUnquotedColon(in value: String) -> String.Index? {
        var isQuoted = false
        var isEscaped = false

        for index in value.indices {
            let character = value[index]
            if isEscaped {
                isEscaped = false
            } else if character == "\\" {
                isEscaped = true
            } else if character == "\"" {
                isQuoted.toggle()
            } else if character == ":", !isQuoted {
                return index
            }
        }
        return nil
    }

    static func splitUnquoted(_ value: String, separator: Character) -> [String] {
        var result: [String] = []
        var current = ""
        var isQuoted = false
        var isEscaped = false

        for character in value {
            if isEscaped {
                current.append(character)
                isEscaped = false
            } else if character == "\\" {
                current.append(character)
                isEscaped = true
            } else if character == "\"" {
                current.append(character)
                isQuoted.toggle()
            } else if character == separator, !isQuoted {
                result.append(current)
                current = ""
            } else {
                current.append(character)
            }
        }
        result.append(current)
        return result
    }

    static func decodeParameterText(_ value: String) -> String {
        var decoded = ""
        var index = value.startIndex
        while index < value.endIndex {
            guard value[index] == "^" else {
                decoded.append(value[index])
                index = value.index(after: index)
                continue
            }

            let nextIndex = value.index(after: index)
            guard nextIndex < value.endIndex else {
                decoded.append("^")
                break
            }
            switch value[nextIndex] {
            case "^":
                decoded.append("^")
            case "n", "N":
                decoded.append("\n")
            case "'":
                decoded.append("\"")
            default:
                decoded.append("^")
                decoded.append(value[nextIndex])
            }
            index = value.index(after: nextIndex)
        }
        return decoded
    }

    static func decodeText(_ value: String) -> String {
        var decoded = ""
        var iterator = value.makeIterator()

        while let character = iterator.next() {
            guard character == "\\" else {
                decoded.append(character)
                continue
            }
            guard let escaped = iterator.next() else {
                decoded.append("\\")
                break
            }
            switch escaped {
            case "n", "N":
                decoded.append("\n")
            case ",", ";", "\\", ":":
                decoded.append(escaped)
            default:
                // Be liberal with feeds produced by older calendar exporters.
                decoded.append(escaped)
            }
        }
        return decoded
    }

    static func firstLine(named name: String, in lines: [ContentLine]) -> ContentLine? {
        lines.first { $0.name == name }
    }

    static func firstValue(named name: String, in lines: [ContentLine]) -> String? {
        guard let rawValue = firstLine(named: name, in: lines)?.value else {
            return nil
        }
        let decoded = decodeText(rawValue).trimmingCharacters(in: .whitespacesAndNewlines)
        return decoded.isEmpty ? nil : decoded
    }

    static func makeEvent(
        from lines: [ContentLine],
        defaultTimeZone: TimeZone,
        timeZoneAliases: [String: TimeZone],
        calendarMethodIsCancellation: Bool
    ) -> EventConstructionResult {
        let uid = firstValue(named: "UID", in: lines)
        let status = firstValue(named: "STATUS", in: lines)
        if calendarMethodIsCancellation ||
            status?.caseInsensitiveCompare("CANCELLED") == .orderedSame {
            return EventConstructionResult(
                event: nil,
                warnings: [],
                cancelledUID: uid
            )
        }

        guard let startLine = firstLine(named: "DTSTART", in: lines) else {
            return EventConstructionResult(
                event: nil,
                warnings: [
                    ICSParseWarning(
                        eventUID: uid,
                        line: lines.first?.sourceLine,
                        message: "An event without DTSTART was ignored."
                    )
                ],
                cancelledUID: nil
            )
        }

        let parsedStart: ParsedDate
        do {
            parsedStart = try parseDate(
                startLine,
                defaultTimeZone: defaultTimeZone,
                aliases: timeZoneAliases
            )
        } catch {
            return EventConstructionResult(
                event: nil,
                warnings: [
                    ICSParseWarning(
                        eventUID: uid,
                        line: startLine.sourceLine,
                        message: "DTSTART could not be interpreted: \(error.localizedDescription)"
                    )
                ],
                cancelledUID: nil
            )
        }

        var warnings: [ICSParseWarning] = []
        var endDate: Date?
        if let endLine = firstLine(named: "DTEND", in: lines) {
            do {
                endDate = try parseDate(
                    endLine,
                    defaultTimeZone: defaultTimeZone,
                    aliases: timeZoneAliases
                ).date
            } catch {
                warnings.append(
                    ICSParseWarning(
                        eventUID: uid,
                        line: endLine.sourceLine,
                        message: "DTEND was ignored because it could not be interpreted."
                    )
                )
            }
        }

        var lastModified: Date?
        if let modifiedLine = firstLine(named: "LAST-MODIFIED", in: lines) {
            do {
                lastModified = try parseDate(
                    modifiedLine,
                    defaultTimeZone: defaultTimeZone,
                    aliases: timeZoneAliases
                ).date
            } catch {
                warnings.append(
                    ICSParseWarning(
                        eventUID: uid,
                        line: modifiedLine.sourceLine,
                        message: "LAST-MODIFIED was ignored because it could not be interpreted."
                    )
                )
            }
        }

        let title = firstValue(named: "SUMMARY", in: lines) ?? "Untitled event"
        let event = ParsedCalendarEvent(
            uid: uid,
            summary: title,
            startDate: parsedStart.date,
            endDate: endDate,
            description: firstValue(named: "DESCRIPTION", in: lines),
            url: firstValue(named: "URL", in: lines),
            lastModified: lastModified,
            isAllDay: parsedStart.isAllDay,
            timeZoneIdentifier: parsedStart.timeZoneIdentifier,
            sequence: firstValue(named: "SEQUENCE", in: lines)
                .flatMap(Int.init)
        )
        return EventConstructionResult(
            event: event,
            warnings: warnings,
            cancelledUID: nil
        )
    }

    enum DateParsingError: LocalizedError {
        case invalidValue(String)
        case unknownTimeZone(String)

        var errorDescription: String? {
            switch self {
            case .invalidValue(let value):
                "“\(value)” is not a valid iCalendar date."
            case .unknownTimeZone(let identifier):
                "The time zone “\(identifier)” is not recognized."
            }
        }
    }

    static func parseDate(
        _ line: ContentLine,
        defaultTimeZone: TimeZone,
        aliases: [String: TimeZone]
    ) throws -> ParsedDate {
        let rawValue = line.value.trimmingCharacters(in: .whitespacesAndNewlines)
        let valueKind = line.parameters["VALUE"]?.uppercased()
        let isDateOnly = valueKind == "DATE" ||
            (rawValue.count == 8 && rawValue.allSatisfy(\.isNumber))

        if isDateOnly {
            guard let date = makeDateOnly(rawValue, timeZone: defaultTimeZone) else {
                throw DateParsingError.invalidValue(rawValue)
            }
            return ParsedDate(date: date, isAllDay: true, timeZoneIdentifier: nil)
        }

        let explicitTimeZoneID = line.parameters["TZID"]
        let timeZone: TimeZone
        let exposedTimeZoneIdentifier: String?
        var dateValue = rawValue

        if rawValue.uppercased().hasSuffix("Z") {
            dateValue.removeLast()
            timeZone = TimeZone(secondsFromGMT: 0) ?? defaultTimeZone
            exposedTimeZoneIdentifier = "UTC"
        } else if let explicitTimeZoneID {
            guard let resolved = resolveTimeZone(explicitTimeZoneID, aliases: aliases) else {
                throw DateParsingError.unknownTimeZone(explicitTimeZoneID)
            }
            timeZone = resolved
            exposedTimeZoneIdentifier = resolved.identifier
        } else if hasNumericUTCOffset(rawValue) {
            guard let parsed = parseDateWithOffset(rawValue) else {
                throw DateParsingError.invalidValue(rawValue)
            }
            return ParsedDate(
                date: parsed,
                isAllDay: false,
                timeZoneIdentifier: nil
            )
        } else {
            timeZone = defaultTimeZone
            exposedTimeZoneIdentifier = nil
        }

        guard let date = parseLocalDateTime(dateValue, timeZone: timeZone) else {
            throw DateParsingError.invalidValue(rawValue)
        }
        return ParsedDate(
            date: date,
            isAllDay: false,
            timeZoneIdentifier: exposedTimeZoneIdentifier
        )
    }

    static func makeDateOnly(_ value: String, timeZone: TimeZone) -> Date? {
        guard value.count == 8,
              let year = Int(value.prefix(4)),
              let month = Int(value.dropFirst(4).prefix(2)),
              let day = Int(value.dropFirst(6).prefix(2)) else {
            return nil
        }
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_US_POSIX")
        calendar.timeZone = timeZone
        let components = DateComponents(
            calendar: calendar,
            timeZone: timeZone,
            year: year,
            month: month,
            day: day
        )
        guard let date = calendar.date(from: components),
              calendar.dateComponents([.year, .month, .day], from: date) ==
                DateComponents(year: year, month: month, day: day) else {
            return nil
        }
        return calendar.startOfDay(for: date)
    }

    static func parseLocalDateTime(_ value: String, timeZone: TimeZone) -> Date? {
        let formats = [
            "yyyyMMdd'T'HHmmss.SSS",
            "yyyyMMdd'T'HHmmss",
            "yyyyMMdd'T'HHmm"
        ]
        for format in formats {
            let formatter = DateFormatter()
            formatter.calendar = Calendar(identifier: .gregorian)
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = timeZone
            formatter.dateFormat = format
            formatter.isLenient = false
            if let date = formatter.date(from: value) {
                return date
            }
        }
        return nil
    }

    static func hasNumericUTCOffset(_ value: String) -> Bool {
        let suffix = value.suffix(5)
        guard suffix.count == 5,
              suffix.first == "+" || suffix.first == "-" else {
            return false
        }
        return suffix.dropFirst().allSatisfy(\.isNumber)
    }

    static func parseDateWithOffset(_ value: String) -> Date? {
        let formats = [
            "yyyyMMdd'T'HHmmssZ",
            "yyyyMMdd'T'HHmmZ"
        ]
        for format in formats {
            let formatter = DateFormatter()
            formatter.calendar = Calendar(identifier: .gregorian)
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(secondsFromGMT: 0)
            formatter.dateFormat = format
            formatter.isLenient = false
            if let date = formatter.date(from: value) {
                return date
            }
        }
        return nil
    }

    static func calendarTimeZone(
        in lines: [ContentLine],
        aliases: [String: TimeZone]
    ) -> TimeZone? {
        guard let identifier = lines.first(where: { $0.name == "X-WR-TIMEZONE" })?.value else {
            return nil
        }
        return resolveTimeZone(
            identifier.trimmingCharacters(in: .whitespacesAndNewlines),
            aliases: aliases
        )
    }

    static func timeZoneAliases(in lines: [ContentLine]) -> [String: TimeZone] {
        var result: [String: TimeZone] = [:]
        var currentTZID: String?
        var location: String?
        var depth = 0

        func saveCurrentAlias() {
            guard let alias = currentTZID else {
                return
            }
            if let location, let zone = resolveKnownTimeZone(location) {
                result[alias] = zone
                result[alias.lowercased()] = zone
            } else if let zone = resolveKnownTimeZone(alias) {
                result[alias] = zone
                result[alias.lowercased()] = zone
            }
        }

        for line in lines {
            let component = line.value.trimmingCharacters(in: .whitespacesAndNewlines)
                .uppercased()
            if line.name == "BEGIN", component == "VTIMEZONE" {
                currentTZID = nil
                location = nil
                depth = 1
                continue
            }
            guard depth > 0 else {
                continue
            }
            if line.name == "BEGIN" {
                depth += 1
            } else if line.name == "END" {
                depth -= 1
                if depth == 0 {
                    saveCurrentAlias()
                    currentTZID = nil
                    location = nil
                }
            } else if depth == 1, line.name == "TZID" {
                currentTZID = decodeText(line.value)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            } else if depth == 1, line.name == "X-LIC-LOCATION" {
                location = decodeText(line.value)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
        return result
    }

    static func resolveTimeZone(
        _ identifier: String,
        aliases: [String: TimeZone]
    ) -> TimeZone? {
        aliases[identifier] ??
            aliases[identifier.lowercased()] ??
            resolveKnownTimeZone(identifier)
    }

    static func resolveKnownTimeZone(_ rawIdentifier: String) -> TimeZone? {
        let identifier = rawIdentifier.trimmingCharacters(in: .whitespacesAndNewlines)
        if let exact = TimeZone(identifier: identifier) {
            return exact
        }
        if let abbreviation = TimeZone(abbreviation: identifier) {
            return abbreviation
        }

        let windowsMappings = [
            "eastern standard time": "America/New_York",
            "central standard time": "America/Chicago",
            "mountain standard time": "America/Denver",
            "pacific standard time": "America/Los_Angeles",
            "aus eastern standard time": "Australia/Sydney",
            "new zealand standard time": "Pacific/Auckland"
        ]
        if let mappedIdentifier = windowsMappings[identifier.lowercased()] {
            return TimeZone(identifier: mappedIdentifier)
        }

        if let caseInsensitiveMatch = TimeZone.knownTimeZoneIdentifiers.first(where: {
            $0.caseInsensitiveCompare(identifier) == .orderedSame
        }) {
            return TimeZone(identifier: caseInsensitiveMatch)
        }

        if let suffixMatch = TimeZone.knownTimeZoneIdentifiers
            .sorted(by: { $0.count > $1.count })
            .first(where: { identifier.hasSuffix($0) }) {
            return TimeZone(identifier: suffixMatch)
        }
        return nil
    }
}

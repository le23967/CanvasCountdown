import Foundation

/// Reads a date the user typed into the calendar's date field.
///
/// Deliberately narrow rather than clever. Everything it accepts resolves to
/// exactly one day, and anything it cannot resolve is refused, because taking a
/// wrong guess here silently moves the calendar somewhere the user did not ask
/// for. Missing pieces come from the day already on screen, never from "the
/// next one that sounds right".
enum CalendarDateEntry {
    /// Full month names and the abbreviations Canvas itself uses. English is
    /// always accepted whatever the system language, and the locale's own month
    /// names are accepted on top of it.
    private static let englishMonths: [String: Int] = [
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

    /// The day the entry lands on, or nil when the text says nothing definite.
    ///
    /// - Parameter reference: the day the calendar is showing, used for the
    ///   parts the text leaves out.
    static func date(
        from text: String,
        reference: Date,
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent,
        now: Date = .now
    ) -> Date? {
        let cleaned = text
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        guard !cleaned.isEmpty else {
            return nil
        }

        // Order matters. The system's own parser is tried last and never
        // leniently: asked to read "2026-13-01" it happily returns a day in
        // 2007, and a confident wrong answer is the one failure mode this is
        // written to avoid.
        let readings = [
            relativeDate(cleaned, calendar: calendar, now: now),
            numericDate(cleaned, reference: reference, calendar: calendar, locale: locale),
            namedMonthDate(cleaned, reference: reference, calendar: calendar, locale: locale),
            localeFormatted(cleaned, calendar: calendar, locale: locale),
        ]
        guard let match = readings.compactMap({ $0 }).first else {
            return nil
        }
        return calendar.startOfDay(for: match)
    }

    private static func relativeDate(
        _ text: String,
        calendar: Calendar,
        now: Date
    ) -> Date? {
        switch text {
        case "today", "now":
            now
        case "tomorrow":
            calendar.date(byAdding: .day, value: 1, to: now)
        case "yesterday":
            calendar.date(byAdding: .day, value: -1, to: now)
        default:
            nil
        }
    }

    /// What the user would get from typing a date the way their own system
    /// writes one.
    private static func localeFormatted(
        _ text: String,
        calendar: Calendar,
        locale: Locale
    ) -> Date? {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = locale
        formatter.timeZone = calendar.timeZone
        formatter.dateStyle = .short
        formatter.timeStyle = .none
        formatter.isLenient = false
        return formatter.date(from: text)
    }

    /// Digits and separators: 2026-08-14, 2026/8/14, 14.8.2026, 14/8, or a bare
    /// day of the month.
    private static func numericDate(
        _ text: String,
        reference: Date,
        calendar: Calendar,
        locale: Locale
    ) -> Date? {
        let parts = text
            .split(whereSeparator: { "-/. ".contains($0) })
            .compactMap { Int($0) }
        guard !parts.isEmpty, parts.count == text.split(
            whereSeparator: { "-/. ".contains($0) }
        ).count else {
            return nil
        }

        let referenceParts = calendar.dateComponents([.year, .month], from: reference)
        switch parts.count {
        case 1:
            return day(
                parts[0],
                month: referenceParts.month,
                year: referenceParts.year,
                calendar: calendar
            )
        case 2:
            // Two numbers are a day and a month, in whatever order this locale
            // writes them, unless the first is plainly a year.
            if parts[0] > 31 {
                return day(1, month: parts[1], year: parts[0], calendar: calendar)
            }
            let (dayValue, monthValue) = dayIsFirst(in: locale)
                ? (parts[0], parts[1])
                : (parts[1], parts[0])
            return day(
                dayValue,
                month: monthValue,
                year: referenceParts.year,
                calendar: calendar
            )
        case 3:
            if parts[0] > 31 {
                return day(parts[2], month: parts[1], year: parts[0], calendar: calendar)
            }
            let (dayValue, monthValue) = dayIsFirst(in: locale)
                ? (parts[0], parts[1])
                : (parts[1], parts[0])
            return day(
                dayValue,
                month: monthValue,
                year: fullYear(parts[2], calendar: calendar),
                calendar: calendar
            )
        default:
            return nil
        }
    }

    /// "14 aug", "aug 14", "august 14 2026", "aug 2026".
    private static func namedMonthDate(
        _ text: String,
        reference: Date,
        calendar: Calendar,
        locale: Locale
    ) -> Date? {
        let words = text.split(whereSeparator: { !$0.isLetter && !$0.isNumber })
        guard !words.isEmpty else {
            return nil
        }

        var month: Int?
        var numbers: [Int] = []
        for word in words {
            if let value = Int(word) {
                numbers.append(value)
            } else if let named = monthNumber(String(word), locale: locale, calendar: calendar) {
                month = named
            } else {
                return nil
            }
        }

        guard let month else {
            return nil
        }
        let referenceYear = calendar.component(.year, from: reference)
        let dayValue = numbers.first { $0 <= 31 } ?? 1
        let yearValue = numbers.first { $0 > 31 }.map { fullYear($0, calendar: calendar) }
            ?? referenceYear
        return day(dayValue, month: month, year: yearValue, calendar: calendar)
    }

    private static func monthNumber(
        _ word: String,
        locale: Locale,
        calendar: Calendar
    ) -> Int? {
        if let english = englishMonths[word] {
            return english
        }
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = locale
        let names = (formatter.monthSymbols ?? [])
            + (formatter.shortMonthSymbols ?? [])
            + (formatter.standaloneMonthSymbols ?? [])
        if let index = names.firstIndex(where: {
            $0.lowercased() == word || $0.lowercased().hasPrefix(word)
        }) {
            return index % 12 + 1
        }
        return nil
    }

    /// Two digits mean this century, which is the only reading that is ever
    /// useful for a deadline.
    private static func fullYear(_ value: Int, calendar: Calendar) -> Int {
        guard value < 100 else {
            return value
        }
        let century = calendar.component(.year, from: .now) / 100 * 100
        return century + value
    }

    private static func dayIsFirst(in locale: Locale) -> Bool {
        let order = DateFormatter.dateFormat(
            fromTemplate: "yMd",
            options: 0,
            locale: locale
        ) ?? "d/M/y"
        guard let day = order.firstIndex(of: "d"),
              let month = order.firstIndex(of: "M") else {
            return true
        }
        return day < month
    }

    /// Rejects the days that do not exist, so "31 february" is refused rather
    /// than rolled into March.
    private static func day(
        _ day: Int,
        month: Int?,
        year: Int?,
        calendar: Calendar
    ) -> Date? {
        guard let month, let year, (1...12).contains(month), day >= 1 else {
            return nil
        }
        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day
        guard let date = calendar.date(from: components),
              calendar.component(.day, from: date) == day,
              calendar.component(.month, from: date) == month else {
            return nil
        }
        return date
    }
}

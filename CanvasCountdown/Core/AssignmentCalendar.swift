import Foundation

/// How much of the calendar is on screen at once.
///
/// The same four the system calendar offers, in the same order, because the
/// keyboard shortcuts and the muscle memory come with them.
enum CalendarScale: String, CaseIterable, Codable, Identifiable, Sendable {
    case day
    case week
    case month
    case year

    var id: String { rawValue }

    var title: String {
        switch self {
        case .day:
            "Day"
        case .week:
            "Week"
        case .month:
            "Month"
        case .year:
            "Year"
        }
    }

    /// What one press of the back or forward control moves by.
    var step: Calendar.Component {
        switch self {
        case .day:
            .day
        case .week:
            .weekOfYear
        case .month:
            .month
        case .year:
            .year
        }
    }

    /// ⌘1 … ⌘4, as in the system calendar.
    var shortcut: Character {
        switch self {
        case .day:
            "1"
        case .week:
            "2"
        case .month:
            "3"
        case .year:
            "4"
        }
    }
}

/// One square in a grid, whatever grid it is in.
struct CalendarDay: Identifiable, Equatable, Sendable {
    let date: Date
    let isInDisplayedMonth: Bool
    let isToday: Bool
    let items: [AssignmentListItem]

    var id: Date { date }

    var dayNumber: Int {
        Calendar.autoupdatingCurrent.component(.day, from: date)
    }
}

/// One of the twelve small grids in the year view.
struct CalendarMonthSummary: Identifiable, Equatable, Sendable {
    let start: Date
    let title: String
    let days: [CalendarDay]

    var id: Date { start }
}

/// Builds every grid the calendar shows.
///
/// Pure and calendar-driven: no arithmetic on 86,400-second days, so months of
/// any length and daylight-saving transitions come out right. The events it is
/// given are already filtered, so the calendar always shows exactly what the
/// list would show.
enum AssignmentCalendar {
    /// Six weeks of squares in the month grid, always. A fixed height stops the
    /// grid jumping as the user pages between months.
    static let weeksShown = 6

    // MARK: - Grids

    static func month(
        containing date: Date,
        events: [AssignmentListItem],
        calendar: Calendar = .autoupdatingCurrent,
        now: Date = .now
    ) -> [CalendarDay] {
        guard let monthStart = startOfMonth(for: date, calendar: calendar),
              let gridStart = startOfGrid(for: monthStart, calendar: calendar) else {
            return []
        }

        let displayedMonth = calendar.component(.month, from: monthStart)
        let eventsByDay = groupedByDay(events, calendar: calendar)

        return (0..<(weeksShown * 7)).compactMap { offset in
            guard let day = calendar.date(
                byAdding: .day,
                value: offset,
                to: gridStart
            ) else {
                return nil
            }
            let start = calendar.startOfDay(for: day)
            return CalendarDay(
                date: start,
                isInDisplayedMonth: calendar.component(.month, from: start) == displayedMonth,
                isToday: calendar.isDate(start, inSameDayAs: now),
                items: eventsByDay[start] ?? []
            )
        }
    }

    static func week(
        containing date: Date,
        events: [AssignmentListItem],
        calendar: Calendar = .autoupdatingCurrent,
        now: Date = .now
    ) -> [CalendarDay] {
        guard let weekStart = startOfWeek(for: date, calendar: calendar) else {
            return []
        }
        let eventsByDay = groupedByDay(events, calendar: calendar)

        return (0..<7).compactMap { offset in
            guard let day = calendar.date(
                byAdding: .day,
                value: offset,
                to: weekStart
            ) else {
                return nil
            }
            let start = calendar.startOfDay(for: day)
            return CalendarDay(
                date: start,
                isInDisplayedMonth: true,
                isToday: calendar.isDate(start, inSameDayAs: now),
                items: eventsByDay[start] ?? []
            )
        }
    }

    static func day(
        containing date: Date,
        events: [AssignmentListItem],
        calendar: Calendar = .autoupdatingCurrent,
        now: Date = .now
    ) -> CalendarDay {
        let start = calendar.startOfDay(for: date)
        return CalendarDay(
            date: start,
            isInDisplayedMonth: true,
            isToday: calendar.isDate(start, inSameDayAs: now),
            items: groupedByDay(events, calendar: calendar)[start] ?? []
        )
    }

    /// Twelve month grids for the year containing `date`.
    static func year(
        containing date: Date,
        events: [AssignmentListItem],
        calendar: Calendar = .autoupdatingCurrent,
        now: Date = .now,
        locale: Locale = .autoupdatingCurrent
    ) -> [CalendarMonthSummary] {
        let year = calendar.component(.year, from: date)
        guard let yearStart = calendar.date(
            from: DateComponents(year: year, month: 1, day: 1)
        ) else {
            return []
        }

        return (0..<12).compactMap { offset in
            guard let monthStart = calendar.date(
                byAdding: .month,
                value: offset,
                to: yearStart
            ) else {
                return nil
            }
            return CalendarMonthSummary(
                start: monthStart,
                title: standaloneMonthTitle(
                    for: monthStart,
                    calendar: calendar,
                    locale: locale
                ),
                days: month(
                    containing: monthStart,
                    events: events,
                    calendar: calendar,
                    now: now
                )
            )
        }
    }

    // MARK: - Moving around

    /// One step of whatever the calendar is currently showing.
    static func date(
        byAdding scale: CalendarScale,
        count: Int,
        to date: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date {
        calendar.date(byAdding: scale.step, value: count, to: date) ?? date
    }

    /// The first instant of the period a date belongs to.
    ///
    /// Stepping works on this rather than on the day itself, because adding a
    /// month to the 31st lands on the 30th and the calendar would walk backwards
    /// through the months a day at a time. Which day the anchor holds is
    /// invisible in every scale above `day`, so nothing is lost by pinning it.
    static func normalized(
        _ date: Date,
        for scale: CalendarScale,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date {
        switch scale {
        case .day:
            return calendar.startOfDay(for: date)
        case .week:
            return startOfWeek(for: date, calendar: calendar)
                ?? calendar.startOfDay(for: date)
        case .month:
            return startOfMonth(for: date, calendar: calendar)
                ?? calendar.startOfDay(for: date)
        case .year:
            let year = calendar.component(.year, from: date)
            return calendar.date(from: DateComponents(year: year, month: 1, day: 1))
                ?? calendar.startOfDay(for: date)
        }
    }

    static func startOfMonth(
        for date: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date? {
        calendar.date(
            from: calendar.dateComponents([.year, .month], from: date)
        )
    }

    /// Honours the user's first weekday rather than assuming Sunday or Monday.
    static func startOfWeek(
        for date: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date? {
        let start = calendar.startOfDay(for: date)
        let weekday = calendar.component(.weekday, from: start)
        let offset = (weekday - calendar.firstWeekday + 7) % 7
        return calendar.date(byAdding: .day, value: -offset, to: start)
    }

    /// Backs up to the first day of the week containing the first of the month.
    static func startOfGrid(
        for monthStart: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date? {
        startOfWeek(for: monthStart, calendar: calendar)
    }

    static func month(
        byAdding months: Int,
        to date: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date {
        calendar.date(byAdding: .month, value: months, to: date) ?? date
    }

    // MARK: - Titles

    /// Column headings in the user's own week order.
    static func weekdaySymbols(calendar: Calendar = .autoupdatingCurrent) -> [String] {
        rotatedToFirstWeekday(calendar.shortWeekdaySymbols, calendar: calendar)
    }

    /// Single letters, for the twelve small grids where a three-letter heading
    /// would not fit.
    static func narrowWeekdaySymbols(
        calendar: Calendar = .autoupdatingCurrent
    ) -> [String] {
        rotatedToFirstWeekday(calendar.veryShortWeekdaySymbols, calendar: calendar)
    }

    /// What the header says, which is different for each scale: a week has no
    /// single month, and a year has no month at all.
    static func title(
        for scale: CalendarScale,
        date: Date,
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent
    ) -> String {
        switch scale {
        case .day:
            return formatted(date, template: "EEEEdMMMMy", calendar: calendar, locale: locale)
        case .week:
            return weekTitle(for: date, calendar: calendar, locale: locale)
        case .month:
            return monthTitle(for: date, calendar: calendar, locale: locale)
        case .year:
            return formatted(date, template: "y", calendar: calendar, locale: locale)
        }
    }

    static func monthTitle(
        for date: Date,
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent
    ) -> String {
        formatted(date, template: "yMMMM", calendar: calendar, locale: locale)
    }

    /// A range, because a week rarely sits inside one month and never inside
    /// one when it matters most — at the turn of a month or a year.
    static func weekTitle(
        for date: Date,
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent
    ) -> String {
        guard let start = startOfWeek(for: date, calendar: calendar),
              let end = calendar.date(byAdding: .day, value: 6, to: start) else {
            return monthTitle(for: date, calendar: calendar, locale: locale)
        }

        let sameYear = calendar.component(.year, from: start)
            == calendar.component(.year, from: end)
        let sameMonth = sameYear && calendar.component(.month, from: start)
            == calendar.component(.month, from: end)

        let startTemplate = sameYear ? (sameMonth ? "dMMM" : "dMMM") : "dMMMy"
        let leading = formatted(start, template: startTemplate, calendar: calendar, locale: locale)
        let trailing = formatted(end, template: "dMMMy", calendar: calendar, locale: locale)
        return "\(leading) – \(trailing)"
    }

    /// "January" rather than "January 2026": the year is already in the header
    /// above the twelve grids.
    static func standaloneMonthTitle(
        for date: Date,
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent
    ) -> String {
        formatted(date, template: "MMMM", calendar: calendar, locale: locale)
    }

    // MARK: - Helpers

    private static func groupedByDay(
        _ events: [AssignmentListItem],
        calendar: Calendar
    ) -> [Date: [AssignmentListItem]] {
        Dictionary(grouping: events) {
            calendar.startOfDay(for: $0.dueDate)
        }
        .mapValues { $0.sorted { $0.dueDate < $1.dueDate } }
    }

    private static func rotatedToFirstWeekday(
        _ symbols: [String],
        calendar: Calendar
    ) -> [String] {
        guard symbols.count == 7 else {
            return symbols
        }
        let first = (calendar.firstWeekday - 1 + 7) % 7
        return Array(symbols[first...] + symbols[..<first])
    }

    private static func formatted(
        _ date: Date,
        template: String,
        calendar: Calendar,
        locale: Locale
    ) -> String {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = locale
        formatter.timeZone = calendar.timeZone
        formatter.setLocalizedDateFormatFromTemplate(template)
        return formatter.string(from: date)
    }
}

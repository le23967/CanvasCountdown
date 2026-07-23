import Foundation

/// One square in the month grid.
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

/// Builds the month grid.
///
/// Pure and calendar-driven: no arithmetic on 86,400-second days, so months of
/// any length and daylight-saving transitions come out right. The events it is
/// given are already filtered, so the calendar always shows exactly what the
/// list would show.
enum AssignmentCalendar {
    /// Six weeks of squares, always. A fixed height stops the grid jumping as
    /// the user pages between months.
    static let weeksShown = 6

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
        let eventsByDay = Dictionary(grouping: events) {
            calendar.startOfDay(for: $0.dueDate)
        }

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
                items: (eventsByDay[start] ?? []).sorted { $0.dueDate < $1.dueDate }
            )
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

    /// Backs up to the first day of the week containing the first of the month,
    /// honouring the user's first weekday.
    static func startOfGrid(
        for monthStart: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date? {
        let weekday = calendar.component(.weekday, from: monthStart)
        let offset = (weekday - calendar.firstWeekday + 7) % 7
        return calendar.date(byAdding: .day, value: -offset, to: monthStart)
    }

    static func month(
        byAdding months: Int,
        to date: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date {
        calendar.date(byAdding: .month, value: months, to: date) ?? date
    }

    /// Column headings in the user's own week order.
    static func weekdaySymbols(calendar: Calendar = .autoupdatingCurrent) -> [String] {
        let symbols = calendar.shortWeekdaySymbols
        let first = calendar.firstWeekday - 1
        return Array(symbols[first...] + symbols[..<first])
    }

    static func monthTitle(
        for date: Date,
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent
    ) -> String {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = locale
        formatter.setLocalizedDateFormatFromTemplate("yMMMM")
        return formatter.string(from: date)
    }
}

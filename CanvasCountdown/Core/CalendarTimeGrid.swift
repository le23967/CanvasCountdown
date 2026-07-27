import Foundation

/// One deadline placed on a day's timeline.
///
/// `column` and `columnCount` are how two deadlines an hour apart stop drawing
/// on top of each other: everything that overlaps shares the width of the day.
struct TimedCalendarEntry: Identifiable, Equatable, Sendable {
    let item: AssignmentListItem
    let minuteOfDay: Int
    let column: Int
    let columnCount: Int

    var id: UUID { item.id }
}

/// Turns a day's deadlines into positions on an hour grid.
///
/// A deadline is an instant, not a meeting: nothing here reads a duration,
/// because there is none to read. `entryMinutes` is the height a chip is drawn
/// at, and the only reason two deadlines are ever considered to overlap.
enum CalendarTimeGrid {
    static let hoursShown = 24
    static let entryMinutes = 50

    /// Midnight means "this day" rather than "00:00", which is how Canvas
    /// writes a deadline with no time on it. Those belong in the strip above
    /// the grid, not squeezed into the first row.
    static func isAllDay(
        _ item: AssignmentListItem,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Bool {
        minuteOfDay(item.dueDate, calendar: calendar) == 0
    }

    static func minuteOfDay(
        _ date: Date,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Int {
        let parts = calendar.dateComponents([.hour, .minute], from: date)
        return (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
    }

    static func allDayItems(
        in items: [AssignmentListItem],
        calendar: Calendar = .autoupdatingCurrent
    ) -> [AssignmentListItem] {
        items.filter { isAllDay($0, calendar: calendar) }
    }

    /// Everything with a time on it, laid out into columns.
    static func timedEntries(
        in items: [AssignmentListItem],
        calendar: Calendar = .autoupdatingCurrent
    ) -> [TimedCalendarEntry] {
        let placed: [(item: AssignmentListItem, minute: Int)] = items
            .filter { !isAllDay($0, calendar: calendar) }
            .map { (item: $0, minute: minuteOfDay($0.dueDate, calendar: calendar)) }
        let timed = placed.sorted { left, right in
            guard left.minute == right.minute else {
                return left.minute < right.minute
            }
            let order = left.item.title.localizedStandardCompare(right.item.title)
            return order == .orderedAscending
        }

        var entries: [TimedCalendarEntry] = []
        // A cluster is a run of deadlines that all touch each other, directly or
        // through a neighbour. Its width is decided once, when it ends, so every
        // chip in it lines up.
        var cluster: [(item: AssignmentListItem, minute: Int, column: Int)] = []
        var columnEnds: [Int] = []

        func closeCluster() {
            let width = max(columnEnds.count, 1)
            entries.append(
                contentsOf: cluster.map {
                    TimedCalendarEntry(
                        item: $0.item,
                        minuteOfDay: $0.minute,
                        column: $0.column,
                        columnCount: width
                    )
                }
            )
            cluster.removeAll()
            columnEnds.removeAll()
        }

        for (item, minute) in timed {
            if columnEnds.allSatisfy({ $0 <= minute }) {
                closeCluster()
            }
            let column = columnEnds.firstIndex { $0 <= minute } ?? columnEnds.count
            if column < columnEnds.count {
                columnEnds[column] = minute + entryMinutes
            } else {
                columnEnds.append(minute + entryMinutes)
            }
            cluster.append((item, minute, column))
        }
        closeCluster()

        return entries
    }

    /// The row labels down the left, in the user's own clock.
    static func hourLabels(
        calendar: Calendar = .autoupdatingCurrent,
        locale: Locale = .autoupdatingCurrent
    ) -> [String] {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = locale
        formatter.timeZone = calendar.timeZone
        formatter.setLocalizedDateFormatFromTemplate("j")

        let reference = calendar.startOfDay(for: .now)
        return (0..<hoursShown).map { hour in
            guard let date = calendar.date(
                byAdding: .hour,
                value: hour,
                to: reference
            ) else {
                return ""
            }
            return formatter.string(from: date)
        }
    }
}

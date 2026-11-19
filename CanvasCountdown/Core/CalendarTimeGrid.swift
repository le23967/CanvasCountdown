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
    ///
    /// Read off the item as well as the clock. With midnight deadlines shown
    /// as 11:59 PM, which is the default, a Canvas all-day deadline no longer
    /// sits on midnight by the time it gets here, so judged by the clock alone
    /// none of them ever reached the strip: they were drawn at the very
    /// bottom of the grid instead, below the last hour and out of sight.
    static func isAllDay(
        _ item: AssignmentListItem,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Bool {
        item.isAllDayDeadline || minuteOfDay(item.dueDate, calendar: calendar) == 0
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
        // Counted out on a clock that never changes for daylight saving. On
        // the day the clocks change, adding hours to local midnight skips an
        // hour or repeats one, and every label after it named the wrong row.
        var steady = calendar
        steady.timeZone = TimeZone(secondsFromGMT: 0) ?? calendar.timeZone

        let formatter = DateFormatter()
        formatter.calendar = steady
        formatter.locale = locale
        formatter.timeZone = steady.timeZone
        formatter.setLocalizedDateFormatFromTemplate("j")

        let reference = steady.startOfDay(for: .now)
        return (0..<hoursShown).map { hour in
            guard let date = steady.date(
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

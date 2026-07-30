import Foundation

/// What a due time of exactly midnight means.
///
/// Canvas exports an assignment due "on Friday" as an all-day entry, which
/// lands at 00:00 on that Friday. Read literally that is the *first* instant of
/// the day, so the app showed "Fri, Aug 14 at 00:00" for work that is really
/// due at the end of Friday. Nearly every Canvas deadline is 23:59, so the
/// literal reading is the confusing one.
///
/// The correction only moves the clock, never the day: 00:00 on the 14th
/// becomes 23:59 on the 14th. The countdown counts whole days between
/// `startOfDay` values, so no number in the list or the Dock changes — only the
/// time shown, and when a reminder fires.
enum DueTimePolicy {
    /// The time an adjusted deadline lands on. One minute before midnight,
    /// matching what Canvas itself displays.
    static let endOfDayHour = 23
    static let endOfDayMinute = 59

    /// True when a stored date sits exactly on midnight, which is how an
    /// all-day Canvas entry arrives. A deadline genuinely set to 00:00:00.000
    /// is indistinguishable from one, and is treated the same way.
    static func isMidnight(_ date: Date, calendar: Calendar) -> Bool {
        let parts = calendar.dateComponents(
            [.hour, .minute, .second, .nanosecond],
            from: date
        )
        return parts.hour == 0
            && parts.minute == 0
            && parts.second == 0
            && (parts.nanosecond ?? 0) == 0
    }

    /// The due date to show and to schedule against.
    ///
    /// Manual events are left alone. A time the user typed themselves is not a
    /// Canvas export artefact, and silently moving it would be the app
    /// overruling them.
    static func effectiveDueDate(
        _ dueDate: Date,
        isManual: Bool,
        treatsMidnightAsEndOfDay: Bool,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date {
        guard treatsMidnightAsEndOfDay,
              !isManual,
              isMidnight(dueDate, calendar: calendar) else {
            return dueDate
        }
        return calendar.date(
            bySettingHour: endOfDayHour,
            minute: endOfDayMinute,
            second: 0,
            of: dueDate
        ) ?? dueDate
    }

    /// The same correction for a date that has no stored event behind it yet,
    /// used by the import preview so what is shown before importing matches
    /// what is saved.
    static func effectiveImportedDueDate(
        _ dueDate: Date,
        treatsMidnightAsEndOfDay: Bool,
        calendar: Calendar = .autoupdatingCurrent
    ) -> Date {
        effectiveDueDate(
            dueDate,
            isManual: false,
            treatsMidnightAsEndOfDay: treatsMidnightAsEndOfDay,
            calendar: calendar
        )
    }
}

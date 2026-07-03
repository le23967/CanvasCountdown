import Foundation

/// The minimal view of an assignment needed by the countdown engine.
public protocol CountdownEvent {
    var dueDate: Date { get }
    var isCompleted: Bool { get }
    var isIgnored: Bool { get }
}

public struct CountdownSelection<Event> {
    public let event: Event
    public let daysRemaining: Int

    public init(event: Event, daysRemaining: Int) {
        self.event = event
        self.daysRemaining = daysRemaining
    }
}

extension CountdownSelection: Equatable where Event: Equatable {}
extension CountdownSelection: Sendable where Event: Sendable {}

public enum CountdownCalculator {
    /// Returns the difference between the two local calendar dates.
    ///
    /// This intentionally does not divide elapsed seconds by 86,400, which would be
    /// incorrect around daylight-saving transitions and near midnight.
    public static func calendarDayDifference(
        from start: Date,
        to end: Date,
        calendar: Calendar = .current
    ) -> Int {
        let startOfFirstDay = calendar.startOfDay(for: start)
        let startOfSecondDay = calendar.startOfDay(for: end)
        return calendar.dateComponents(
            [.day],
            from: startOfFirstDay,
            to: startOfSecondDay
        ).day ?? 0
    }

    public static func daysRemaining(
        until dueDate: Date,
        now: Date = .now,
        calendar: Calendar = .current
    ) -> Int {
        calendarDayDifference(from: now, to: dueDate, calendar: calendar)
    }

    /// Selects the first chronologically nearest event that has not passed.
    ///
    /// Completed and ignored events never participate. Equal due dates preserve input
    /// order, which keeps the UI stable across repeated recalculations.
    public static func nearestUpcoming<Event: CountdownEvent>(
        from events: [Event],
        now: Date = .now,
        calendar: Calendar = .current
    ) -> CountdownSelection<Event>? {
        var nearest: Event?

        for event in events {
            guard !event.isCompleted,
                  !event.isIgnored,
                  event.dueDate >= now else {
                continue
            }
            if let currentNearest = nearest {
                if event.dueDate < currentNearest.dueDate {
                    nearest = event
                }
            } else {
                nearest = event
            }
        }

        guard let nearest else {
            return nil
        }
        return CountdownSelection(
            event: nearest,
            daysRemaining: daysRemaining(
                until: nearest.dueDate,
                now: now,
                calendar: calendar
            )
        )
    }

    /// Returns the next instant at which the Dock selection or displayed
    /// calendar-day value can change.
    ///
    /// A countdown changes at local midnight, while the selected assignment
    /// can change at its exact due time. Scheduling the earlier transition
    /// avoids leaving an overdue assignment in the Dock until the next refresh.
    public static func nextTransitionDate<Event: CountdownEvent>(
        from events: [Event],
        now: Date = .now,
        calendar: Calendar = .current
    ) -> Date? {
        let nextMidnight = calendar.date(
            byAdding: .day,
            value: 1,
            to: calendar.startOfDay(for: now)
        )
        let nextDueDate = events
            .lazy
            .filter { !$0.isCompleted && !$0.isIgnored && $0.dueDate > now }
            .map(\.dueDate)
            .min()

        switch (nextMidnight, nextDueDate) {
        case let (midnight?, dueDate?):
            return min(midnight, dueDate)
        case let (midnight?, nil):
            return midnight
        case let (nil, dueDate?):
            return dueDate
        case (nil, nil):
            return nil
        }
    }
}

import Foundation

/// What "Save Selected" is about to do, in the words the confirmation shows.
///
/// The review lists rows; this says what saving them means as one sentence —
/// how many, under which course, over what dates — because several tasks are
/// written at once and the count is the thing worth checking. Kept apart from
/// the view so the numbers can be tested without a window, and worked out from
/// the same rule the save itself uses.
struct AssistantSaveSummary: Equatable, Sendable {
    let savingCount: Int
    /// Rows that will not be written: unticked, or still missing a date.
    let skippedCount: Int
    let courses: [String]
    let withoutCourseCount: Int
    let earliest: Date?
    let latest: Date?
    /// Something was typed into the follow-up field and never applied. Saving
    /// now would throw that sentence away, which is the mistake this catches.
    let hasUnappliedChange: Bool

    var canSave: Bool {
        savingCount > 0
    }

    var title: String {
        savingCount == 1 ? "Add 1 task?" : "Add \(savingCount) tasks?"
    }

    /// The confirming button. Says the number again, so the last thing read
    /// before the click is what is about to happen.
    var confirmTitle: String {
        savingCount == 1 ? "Add 1 Task" : "Add \(savingCount) Tasks"
    }

    /// The lines under the title, in the order they matter: what is being left
    /// out first, because that is the surprise.
    func detailLines(calendar: Calendar = .autoupdatingCurrent) -> [String] {
        var lines: [String] = []

        if hasUnappliedChange {
            lines.append(
                "You typed a change that has not been applied yet. Saving now leaves it out."
            )
        }

        if skippedCount > 0 {
            lines.append(
                skippedCount == 1
                    ? "1 draft is not ticked or has no date, and will not be added."
                    : "\(skippedCount) drafts are not ticked or have no date, and will not be added."
            )
        }

        if let courseLine = courseLine {
            lines.append(courseLine)
        }

        if let dateLine = dateLine(calendar: calendar) {
            lines.append(dateLine)
        }

        lines.append("They are added as manual events, and can be undone.")
        return lines
    }

    private var courseLine: String? {
        switch (courses.count, withoutCourseCount) {
        case (0, 0):
            return nil
        case (0, _):
            return withoutCourseCount == 1
                ? "Without a course."
                : "All \(withoutCourseCount) without a course."
        case (1, 0):
            return "Under \(courses[0])."
        case (1, _):
            return "Under \(courses[0]), and \(withoutCourseCount) without a course."
        default:
            let named = courses.joined(separator: ", ")
            return withoutCourseCount == 0
                ? "Under \(named)."
                : "Under \(named), and \(withoutCourseCount) without a course."
        }
    }

    private func dateLine(calendar: Calendar) -> String? {
        guard let earliest, let latest else {
            return nil
        }
        let format = Date.FormatStyle.dateTime.day().month(.abbreviated)
        if calendar.isDate(earliest, inSameDayAs: latest) {
            return "Due \(earliest.formatted(format))."
        }
        return "Due \(earliest.formatted(format)) to \(latest.formatted(format))."
    }
}

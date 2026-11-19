import SwiftUI

struct AssignmentRowView: View {
    let item: AssignmentListItem
    let now: Date
    /// The user's own mark on this event, if it has one. Passed in rather than
    /// looked up, so the row stays a pure view of what it is given.
    var label: EventLabel?

    var body: some View {
        HStack(spacing: 14) {
            labelBar
            dayCount

            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(item.title)
                        .font(.body.weight(.medium))
                        .foregroundStyle(item.isIgnored ? .secondary : .primary)
                        .lineLimit(2)

                    if item.isCompleted {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                            .accessibilityLabel("Completed")
                    } else if item.isIgnored {
                        Image(systemName: "eye.slash")
                            .foregroundStyle(.secondary)
                            .accessibilityLabel("Ignored")
                    }
                }

                HStack(spacing: 6) {
                    if let label {
                        Text(label.name)
                            .foregroundStyle(Color(nsColor: label.color))
                            .lineLimit(1)
                        Text("•")
                            .accessibilityHidden(true)
                    }
                    if let courseName = item.normalizedCourseName {
                        Text(courseName)
                            .lineLimit(1)
                        Text("•")
                            .accessibilityHidden(true)
                    }
                    // Kept on one line and ahead of the course name: the date
                    // is the part of this line that matters, and it used to be
                    // the part that wrapped while the course was cut short.
                    Text(item.dueDate, format: .dateTime.weekday(.abbreviated).month(.abbreviated).day().hour().minute())
                        .lineLimit(1)
                        .layoutPriority(1)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            Spacer(minLength: 8)
        }
        .padding(.vertical, 7)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityLabel)
    }

    /// A stripe rather than a dot: it reads at a glance down a long list, and
    /// it takes no width from the title when there is no label.
    @ViewBuilder
    private var labelBar: some View {
        if let label {
            // A fixed height rather than a flexible one: inside a row, a view
            // free to grow vertically takes the height it is offered rather
            // than the height of its neighbours.
            Capsule()
                .fill(Color(nsColor: label.color))
                .frame(width: 3, height: 34)
                .accessibilityHidden(true)
        }
    }

    /// Past work that is finished or set aside is not late, so it is not shown
    /// as a negative count in the column that means "how long is left".
    private var isSettled: Bool {
        item.isCompleted || item.isIgnored
    }

    private var dayCount: some View {
        let days = item.remainingDays(relativeTo: now)
        return VStack(spacing: 0) {
            Text(days >= 0 || isSettled ? "\(abs(days))" : "−\(abs(days))")
                .font(.title3.monospacedDigit().weight(.semibold))
                .foregroundStyle(dayCountColor(days))
            Text(dayCaption(days))
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .frame(width: 54)
        .accessibilityHidden(true)
    }

    private func dayCountColor(_ days: Int) -> Color {
        guard !item.isCompleted && !item.isIgnored else {
            return .secondary
        }
        if days < 0 {
            return .red
        }
        if days == 0 {
            return .orange
        }
        return .primary
    }

    /// Work that is done, or ignored, and whose day has passed used to read
    /// "−3 late" here, and "3 days overdue" to VoiceOver, on the Completed
    /// page of all places. It says how long ago instead.
    private func dayCaption(_ days: Int) -> String {
        switch days {
        case ..<0 where isSettled:
            days == -1 ? "day ago" : "days ago"
        case ..<0:
            "late"
        case 0:
            "today"
        case 1:
            "day"
        default:
            "days"
        }
    }

    /// Also says whether it is done or ignored. The row's label replaces the
    /// labels of everything inside it, including the two symbols that would
    /// otherwise have said so.
    private var accessibilityLabel: String {
        let days = item.remainingDays(relativeTo: now)
        let timing: String
        switch days {
        case ..<0 where isSettled:
            timing = days == -1 ? "was due 1 day ago" : "was due \(abs(days)) days ago"
        case ..<0:
            timing = days == -1 ? "1 day overdue" : "\(abs(days)) days overdue"
        case 0:
            timing = "due today"
        case 1:
            timing = "due in 1 day"
        default:
            timing = "due in \(days) days"
        }

        let status = item.isCompleted
            ? ", completed"
            : item.isIgnored ? ", ignored" : ""
        let course = item.normalizedCourseName.map { ", \($0)" } ?? ""
        let labelName = label.map { ", labelled \($0.name)" } ?? ""
        return "\(item.title)\(course)\(labelName)\(status), \(timing)"
    }
}

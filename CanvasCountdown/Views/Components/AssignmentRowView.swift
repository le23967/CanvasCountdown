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
                    Text(item.dueDate, format: .dateTime.weekday(.abbreviated).month(.abbreviated).day().hour().minute())
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

    private var dayCount: some View {
        let days = item.remainingDays(relativeTo: now)
        return VStack(spacing: 0) {
            Text(days >= 0 ? "\(days)" : "−\(abs(days))")
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

    private func dayCaption(_ days: Int) -> String {
        switch days {
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

    private var accessibilityLabel: String {
        let days = item.remainingDays(relativeTo: now)
        let timing: String
        switch days {
        case ..<0:
            timing = "\(abs(days)) days overdue"
        case 0:
            timing = "due today"
        case 1:
            timing = "due in 1 day"
        default:
            timing = "due in \(days) days"
        }

        let course = item.normalizedCourseName.map { ", \($0)" } ?? ""
        let labelName = label.map { ", labelled \($0.name)" } ?? ""
        return "\(item.title)\(course)\(labelName), \(timing)"
    }
}

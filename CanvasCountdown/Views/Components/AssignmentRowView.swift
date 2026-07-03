import SwiftUI

struct AssignmentRowView: View {
    let item: AssignmentListItem
    let now: Date

    var body: some View {
        HStack(spacing: 14) {
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
        return "\(item.title)\(course), \(timing)"
    }
}

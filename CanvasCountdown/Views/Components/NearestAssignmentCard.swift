import SwiftUI

struct NearestAssignmentCard: View {
    let item: AssignmentListItem
    let now: Date

    var body: some View {
        HStack(alignment: .center, spacing: 24) {
            VStack(spacing: 1) {
                Text("\(item.remainingDays(relativeTo: now))")
                    .font(.system(size: 54, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .contentTransition(.numericText())
                Text(dayLabel)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .textCase(.uppercase)
                    .tracking(0.7)
            }
            .frame(minWidth: 92)
            .accessibilityElement(children: .combine)
            .accessibilityLabel(countdownAccessibilityLabel)

            Divider()
                .frame(height: 72)

            VStack(alignment: .leading, spacing: 7) {
                Text("Next deadline")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .textCase(.uppercase)
                    .tracking(0.8)

                Text(item.title)
                    .font(.title2.weight(.semibold))
                    .lineLimit(2)

                HStack(spacing: 8) {
                    if let courseName = item.normalizedCourseName {
                        Label(courseName, systemImage: "book.closed")
                            .lineLimit(1)
                    }
                    Label {
                        Text(item.dueDate, format: .dateTime.weekday(.wide).month(.wide).day().hour().minute())
                    } icon: {
                        Image(systemName: "clock")
                    }
                }
                .font(.callout)
                .foregroundStyle(.secondary)
            }

            Spacer(minLength: 0)
        }
        .padding(22)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .stroke(.separator.opacity(0.55), lineWidth: 1)
        }
        .accessibilityElement(children: .contain)
    }

    private var dayLabel: String {
        item.remainingDays(relativeTo: now) == 1 ? "day left" : "days left"
    }

    private var countdownAccessibilityLabel: String {
        let days = item.remainingDays(relativeTo: now)
        return switch days {
        case 0:
            "Due today"
        case 1:
            "1 day remaining"
        default:
            "\(days) days remaining"
        }
    }
}

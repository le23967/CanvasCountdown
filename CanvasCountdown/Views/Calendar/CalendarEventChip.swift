import SwiftUI

/// One deadline as it appears inside a grid: a tinted block with a colour bar.
///
/// The same chip in the month, week and day grids, so a deadline looks like
/// itself wherever it is seen.
struct CalendarEventChip: View {
    let item: AssignmentListItem
    /// The colour of the user's label, when it has one.
    var labelColor: Color?
    var showsTime = false
    var isCompact = false

    var body: some View {
        HStack(spacing: 5) {
            Capsule()
                .fill(tint)
                .frame(width: 2.5)

            VStack(alignment: .leading, spacing: 1) {
                Text(item.title)
                    .font(isCompact ? .caption2 : .caption)
                    .fontWeight(.medium)
                    .lineLimit(isCompact ? 1 : 2)
                    .strikethrough(item.isCompleted)

                if showsTime, !isCompact {
                    Text(item.dueDate, format: .dateTime.hour().minute())
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .foregroundStyle(item.isIgnored || item.isCompleted ? .secondary : .primary)
        .padding(.leading, 3)
        .padding(.trailing, 4)
        .padding(.vertical, isCompact ? 1 : 3)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(tint.opacity(0.16), in: RoundedRectangle(cornerRadius: 4))
        .contentShape(Rectangle())
        .help(helpText)
    }

    /// The one place a deadline's colour is decided, so the grids do not each
    /// grow their own opinion of it. A label wins over the app's accent, which
    /// is the whole point of putting one on.
    private var tint: Color {
        if item.isCompleted || item.isIgnored {
            return .secondary
        }
        return labelColor ?? .accentColor
    }

    private var helpText: String {
        let course = item.normalizedCourseName.map { "\($0)\n" } ?? ""
        let due = item.dueDate.formatted(date: .abbreviated, time: .shortened)
        return "\(item.title)\n\(course)\(due)"
    }
}

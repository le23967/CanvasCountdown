import SwiftUI

/// Details for a Canvas-imported event.
///
/// The title, course and deadline belong to Canvas and are replaced by the next
/// refresh, so they are shown read-only and labelled as such. Only the local
/// decisions the app owns can be changed here.
struct ImportedEventDetailsView: View {
    @Environment(\.dismiss) private var dismiss

    let item: AssignmentListItem
    let now: Date
    let onToggleCompleted: @MainActor (AssignmentListItem) -> Void
    let onToggleIgnored: @MainActor (AssignmentListItem) -> Void

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    LabeledContent("Assignment", value: item.title)
                    if let courseName = item.normalizedCourseName {
                        LabeledContent("Course", value: courseName)
                    }
                    LabeledContent("Due") {
                        Text(
                            item.dueDate,
                            format: .dateTime.weekday(.wide).day().month(.wide)
                                .hour().minute()
                        )
                    }
                    LabeledContent("Countdown") {
                        Text(countdownDescription)
                    }
                } header: {
                    Text("From Canvas")
                } footer: {
                    Label(
                        "These fields come from your Canvas calendar and are replaced by the next refresh.",
                        systemImage: "arrow.triangle.2.circlepath"
                    )
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }

                Section {
                    Toggle("Completed", isOn: Binding(
                        get: { item.isCompleted },
                        set: { _ in onToggleCompleted(item) }
                    ))
                    Toggle("Ignored", isOn: Binding(
                        get: { item.isIgnored },
                        set: { _ in onToggleIgnored(item) }
                    ))
                } header: {
                    Text("Kept On This Mac")
                } footer: {
                    Text("Completed and ignored are yours. They survive every Canvas refresh, and an ignored assignment never reaches the Dock or a reminder.")
                }
            }
            .formStyle(.grouped)

            Divider()

            HStack {
                Spacer()
                Button("Done") {
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(width: 480, height: 420)
        .navigationTitle(item.title)
    }

    private var countdownDescription: String {
        let days = item.remainingDays(relativeTo: now)
        switch days {
        case 0:
            return "Due today"
        case 1:
            return "Tomorrow"
        case ..<0:
            return abs(days) == 1 ? "1 day overdue" : "\(abs(days)) days overdue"
        default:
            return "\(days) days remaining"
        }
    }
}

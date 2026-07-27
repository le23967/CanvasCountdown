import SwiftUI

/// The month grid: six weeks of squares, whatever the month.
struct CalendarMonthGrid: View {
    let days: [CalendarDay]
    let selectedDay: Date?
    let now: Date
    let onSelect: (Date?) -> Void
    let onOpenDay: (Date) -> Void
    let onOpen: (AssignmentListItem) -> Void

    /// A small minimum so seven columns still fit a narrow window; the cells
    /// share whatever width there is rather than pushing past the edge.
    private let columns = Array(
        repeating: GridItem(.flexible(minimum: 34), spacing: 1),
        count: 7
    )

    var body: some View {
        VStack(spacing: 0) {
            weekdayHeader

            ScrollView {
                LazyVGrid(columns: columns, spacing: 1) {
                    ForEach(days) { day in
                        cell(day)
                    }
                }
                .padding(.horizontal, 22)
                .padding(.bottom, 16)
            }
        }
    }

    private var weekdayHeader: some View {
        LazyVGrid(columns: columns, spacing: 1) {
            // By position: a locale whose short weekday names repeat would
            // otherwise lose a column.
            ForEach(
                Array(AssignmentCalendar.weekdaySymbols().enumerated()),
                id: \.offset
            ) { _, symbol in
                Text(symbol)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .accessibilityHidden(true)
            }
        }
        .padding(.horizontal, 22)
        .padding(.bottom, 4)
    }

    private func cell(_ day: CalendarDay) -> some View {
        let isSelected = selectedDay.map {
            Calendar.autoupdatingCurrent.isDate($0, inSameDayAs: day.date)
        } ?? false

        return VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text("\(day.dayNumber)")
                    .font(.caption.weight(day.isToday ? .bold : .regular))
                    .foregroundStyle(dayNumberStyle(day))
                    .padding(.horizontal, 5)
                    .padding(.vertical, 1)
                    .background {
                        if day.isToday {
                            Capsule().fill(Color.accentColor)
                        }
                    }
                Spacer()
            }

            // Two entries fit comfortably; the rest are counted so the row
            // height stays even across the month.
            ForEach(day.items.prefix(2)) { item in
                CalendarEventChip(item: item, isCompact: true)
            }

            if day.items.count > 2 {
                Text("+\(day.items.count - 2) more")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 4)
            }

            Spacer(minLength: 0)
        }
        .padding(4)
        .frame(minHeight: 62, idealHeight: 86, maxHeight: 96, alignment: .topLeading)
        .frame(maxWidth: .infinity)
        .background(day.isInDisplayedMonth ? Color.clear : Color.secondary.opacity(0.05))
        .overlay {
            RoundedRectangle(cornerRadius: 4)
                .strokeBorder(
                    isSelected ? Color.accentColor : Color.secondary.opacity(0.18),
                    lineWidth: isSelected ? 2 : 0.5
                )
        }
        .contentShape(Rectangle())
        .onTapGesture {
            onSelect(isSelected ? nil : day.date)
        }
        .contextMenu {
            Button("Open in Day View") {
                onOpenDay(day.date)
            }
        }
        // Anchored to the day that was clicked, so the answer appears where the
        // question was asked instead of somewhere below the fold.
        .popover(
            isPresented: Binding(
                get: { isSelected },
                set: { shown in
                    if !shown {
                        onSelect(nil)
                    }
                }
            ),
            arrowEdge: .bottom
        ) {
            dayPopover(day)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityLabel(day))
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }

    private func dayNumberStyle(_ day: CalendarDay) -> some ShapeStyle {
        if day.isToday {
            return AnyShapeStyle(.white)
        }
        return AnyShapeStyle(day.isInDisplayedMonth ? .primary : .tertiary)
    }

    private func accessibilityLabel(_ day: CalendarDay) -> String {
        let date = day.date.formatted(date: .complete, time: .omitted)
        guard !day.items.isEmpty else {
            return "\(date), no assignments"
        }
        let count = day.items.count == 1
            ? "1 assignment"
            : "\(day.items.count) assignments"
        return "\(date), \(count): "
            + day.items.map(\.title).joined(separator: ", ")
    }

    private func dayPopover(_ day: CalendarDay) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(day.date.formatted(date: .complete, time: .omitted))
                    .font(.headline)
                Spacer(minLength: 12)
                Button("Open Day") {
                    onSelect(nil)
                    onOpenDay(day.date)
                }
                .buttonStyle(.link)
            }

            if day.items.isEmpty {
                Text("Nothing due on this day")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(day.items) { item in
                    Button {
                        onSelect(nil)
                        onOpen(item)
                    } label: {
                        AssignmentRowView(item: item, now: now)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)

                    if item.id != day.items.last?.id {
                        Divider()
                    }
                }
            }
        }
        .padding(16)
        .frame(minWidth: 320, maxWidth: 420)
    }
}

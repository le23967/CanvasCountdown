import SwiftUI

/// A month grid of the same events the list is showing.
///
/// It reads `filteredAssignments`, so the course filter, the search query and
/// the completed/ignored toggle all apply here exactly as they do in the list.
/// Switching view never changes which events are in scope.
struct AssignmentCalendarView: View {
    @Bindable var viewModel: MainViewModel
    let onOpen: (AssignmentListItem) -> Void

    private let columns = Array(
        repeating: GridItem(.flexible(minimum: 70), spacing: 1),
        count: 7
    )

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            monthBar
            weekdayHeader

            // Scrolls, so a tall month can never push the page header off the
            // top of the window or hide what is below it.
            ScrollView {
                LazyVGrid(columns: columns, spacing: 1) {
                    ForEach(viewModel.calendarDays) { day in
                        dayCell(day)
                    }
                }
                .padding(.horizontal, 22)
                .padding(.bottom, 16)
            }
        }
    }

    private var monthBar: some View {
        HStack(spacing: 10) {
            Text(viewModel.calendarMonthTitle)
                .font(.headline)
                .accessibilityAddTraits(.isHeader)

            Spacer()

            Button {
                viewModel.showCalendarMonth(offsetBy: -1)
            } label: {
                Image(systemName: "chevron.left")
            }
            .help("Previous month")
            .accessibilityLabel("Previous month")

            Button("Today") {
                viewModel.showCurrentMonth()
            }
            .help("Show the current month")

            Button {
                viewModel.showCalendarMonth(offsetBy: 1)
            } label: {
                Image(systemName: "chevron.right")
            }
            .help("Next month")
            .accessibilityLabel("Next month")
        }
        .padding(.horizontal, 22)
        .padding(.bottom, 10)
    }

    private var weekdayHeader: some View {
        LazyVGrid(columns: columns, spacing: 1) {
            ForEach(AssignmentCalendar.weekdaySymbols(), id: \.self) { symbol in
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

    private func dayCell(_ day: CalendarDay) -> some View {
        let isSelected = viewModel.selectedCalendarDay.map {
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
                Text(item.title)
                    .font(.caption2)
                    .lineLimit(1)
                    .padding(.horizontal, 4)
                    .padding(.vertical, 1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        item.isCompleted
                            ? Color.secondary.opacity(0.15)
                            : Color.accentColor.opacity(0.18),
                        in: RoundedRectangle(cornerRadius: 3)
                    )
                    .strikethrough(item.isCompleted)
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
        .frame(height: 86, alignment: .topLeading)
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
            viewModel.selectCalendarDay(isSelected ? nil : day.date)
        }
        // Anchored to the day that was clicked, so the answer appears where the
        // question was asked instead of somewhere below the fold.
        .popover(
            isPresented: Binding(
                get: { isSelected },
                set: { shown in
                    if !shown {
                        viewModel.selectCalendarDay(nil)
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
            Text(day.date.formatted(date: .complete, time: .omitted))
                .font(.headline)

            if day.items.isEmpty {
                Text("Nothing due on this day")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(day.items) { item in
                    Button {
                        viewModel.selectCalendarDay(nil)
                        onOpen(item)
                    } label: {
                        AssignmentRowView(item: item, now: viewModel.currentDate)
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

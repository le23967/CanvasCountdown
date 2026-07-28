import SwiftUI

/// The whole year as twelve small month grids.
///
/// Nothing is written in the squares at this size, so a day that has something
/// due is marked with a dot instead. Clicking a day opens it; clicking a month
/// name opens the month.
struct CalendarYearGrid: View {
    let months: [CalendarMonthSummary]
    let onOpenDay: (Date) -> Void
    let onOpenMonth: (Date) -> Void
    let label: (AssignmentListItem) -> EventLabel?

    private let monthColumns = [
        GridItem(.adaptive(minimum: 190, maximum: 320), spacing: 24)
    ]
    private let dayColumns = Array(
        repeating: GridItem(.flexible(minimum: 14), spacing: 1),
        count: 7
    )

    var body: some View {
        ScrollView {
            LazyVGrid(columns: monthColumns, alignment: .leading, spacing: 26) {
                ForEach(months) { month in
                    monthGrid(month)
                }
            }
            .padding(.horizontal, 22)
            .padding(.bottom, 24)
        }
    }

    private func monthGrid(_ month: CalendarMonthSummary) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Button {
                onOpenMonth(month.start)
            } label: {
                Text(month.title)
                    .font(.title3.weight(.medium))
                    .foregroundStyle(Color.accentColor)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Open this month")

            LazyVGrid(columns: dayColumns, spacing: 2) {
                // By position, not by value: the narrow symbols repeat — English
                // has two Ts and two Ss — and identifying them by their text
                // silently dropped Thursday and Sunday from the heading.
                ForEach(
                    Array(AssignmentCalendar.narrowWeekdaySymbols().enumerated()),
                    id: \.offset
                ) { _, symbol in
                    Text(symbol)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity)
                        .accessibilityHidden(true)
                }

                ForEach(month.days) { day in
                    dayCell(day)
                }
            }
        }
    }

    private func dayCell(_ day: CalendarDay) -> some View {
        Button {
            onOpenDay(day.date)
        } label: {
            VStack(spacing: 1) {
                Text("\(day.dayNumber)")
                    .font(.caption2.weight(day.isToday ? .bold : .regular))
                    .foregroundStyle(numberStyle(day))
                    .frame(width: 20, height: 20)
                    .background {
                        if day.isToday {
                            Circle().fill(Color.accentColor)
                        }
                    }

                Circle()
                    .fill(day.items.isEmpty ? Color.clear : dotColor(day))
                    .frame(width: 4, height: 4)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
            .opacity(day.isInDisplayedMonth ? 1 : 0)
        }
        .buttonStyle(.plain)
        .disabled(!day.isInDisplayedMonth)
        .accessibilityLabel(accessibilityLabel(day))
    }

    private func numberStyle(_ day: CalendarDay) -> some ShapeStyle {
        day.isToday ? AnyShapeStyle(.white) : AnyShapeStyle(.primary)
    }

    /// One dot for the day, so it takes the colour of the first labelled thing
    /// on it. At this size there is room to say "something is here", not to
    /// list what.
    private func dotColor(_ day: CalendarDay) -> Color {
        let live = day.items.filter { !$0.isCompleted && !$0.isIgnored }
        guard !live.isEmpty else {
            return .secondary
        }
        if let labelled = live.compactMap({ label($0) }).first {
            return Color(nsColor: labelled.color)
        }
        return .accentColor
    }

    private func accessibilityLabel(_ day: CalendarDay) -> String {
        let date = day.date.formatted(date: .complete, time: .omitted)
        guard !day.items.isEmpty else {
            return date
        }
        return day.items.count == 1
            ? "\(date), 1 assignment"
            : "\(date), \(day.items.count) assignments"
    }
}

import SwiftUI

/// The day and the week: an hour grid with the deadlines placed on it.
///
/// One view for both, because a week is seven day columns and nothing else. A
/// deadline written as midnight is not drawn at the top of the grid — Canvas
/// means "by the end of this day" by it, so those sit in the strip above the
/// hours where they can be read.
struct CalendarTimeGridView: View {
    let days: [CalendarDay]
    let now: Date
    let onOpen: (AssignmentListItem) -> Void
    let onOpenDay: (Date) -> Void
    let label: (AssignmentListItem) -> EventLabel?

    private let hourHeight: CGFloat = 44
    private let gutterWidth: CGFloat = 54
    private let chipHeight: CGFloat = 34
    private let calendar = Calendar.autoupdatingCurrent

    private func labelColor(_ item: AssignmentListItem) -> Color? {
        label(item).map { Color(nsColor: $0.color) }
    }

    private var totalHeight: CGFloat {
        CGFloat(CalendarTimeGrid.hoursShown) * hourHeight
    }

    var body: some View {
        VStack(spacing: 0) {
            columnHeader
            allDayStrip
            Divider()

            ScrollViewReader { proxy in
                ScrollView {
                    HStack(alignment: .top, spacing: 0) {
                        hourGutter
                        ForEach(days) { day in
                            column(day)
                            if day.id != days.last?.id {
                                Divider()
                            }
                        }
                    }
                    .frame(height: totalHeight)
                    .padding(.top, 8)
                    .padding(.trailing, 22)
                }
                .onAppear {
                    proxy.scrollTo(initialHour, anchor: .top)
                }
                .onChange(of: days.first?.date) { _, _ in
                    proxy.scrollTo(initialHour, anchor: .top)
                }
            }
        }
    }

    // MARK: - Header

    private var columnHeader: some View {
        HStack(alignment: .top, spacing: 0) {
            // Height and all: an empty colour with only a width set is still
            // free to grow vertically, and it pushed the whole hour grid down
            // the window.
            Color.clear.frame(width: gutterWidth, height: 1)

            ForEach(days) { day in
                Button {
                    onOpenDay(day.date)
                } label: {
                    VStack(spacing: 2) {
                        Text(day.date, format: .dateTime.weekday(.abbreviated))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text("\(day.dayNumber)")
                            .font(.title3.weight(day.isToday ? .bold : .regular))
                            .foregroundStyle(day.isToday ? .white : .primary)
                            .frame(width: 30, height: 30)
                            .background {
                                if day.isToday {
                                    Circle().fill(Color.accentColor)
                                }
                            }
                    }
                    .frame(maxWidth: .infinity)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Open this day")
            }
        }
        .padding(.trailing, 22)
        .padding(.bottom, 6)
    }

    /// Everything due "on this day" with no time of its own.
    @ViewBuilder
    private var allDayStrip: some View {
        let allDay = days.map {
            CalendarTimeGrid.allDayItems(in: $0.items, calendar: calendar)
        }

        if allDay.contains(where: { !$0.isEmpty }) {
            HStack(alignment: .top, spacing: 0) {
                Text("all-day")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .frame(width: gutterWidth, alignment: .trailing)
                    .padding(.trailing, 6)

                ForEach(Array(days.enumerated()), id: \.element.id) { index, day in
                    VStack(spacing: 2) {
                        ForEach(allDay[index]) { item in
                            Button {
                                onOpen(item)
                            } label: {
                                CalendarEventChip(
                                    item: item,
                                    labelColor: labelColor(item),
                                    isCompact: days.count > 1
                                )
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .top)
                    .padding(.horizontal, 2)
                    .accessibilityLabel(
                        "All day on \(day.date.formatted(date: .abbreviated, time: .omitted))"
                    )
                }
            }
            .padding(.trailing, 22)
            .padding(.bottom, 6)
        }
    }

    // MARK: - Grid

    private var hourGutter: some View {
        ZStack(alignment: .topLeading) {
            ForEach(Array(CalendarTimeGrid.hourLabels().enumerated()), id: \.offset) { hour, label in
                Text(label)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .frame(width: gutterWidth - 8, alignment: .trailing)
                    .offset(y: CGFloat(hour) * hourHeight - 6)
                    .accessibilityHidden(true)
            }
        }
        .frame(width: gutterWidth, height: totalHeight, alignment: .topLeading)
    }

    private func column(_ day: CalendarDay) -> some View {
        let entries = CalendarTimeGrid.timedEntries(in: day.items, calendar: calendar)

        return GeometryReader { proxy in
            ZStack(alignment: .topLeading) {
                hourLines

                // Invisible anchors, so the scroll view can be pointed at an
                // hour without the grid itself having to be a list.
                VStack(spacing: 0) {
                    ForEach(0..<CalendarTimeGrid.hoursShown, id: \.self) { hour in
                        Color.clear
                            .frame(height: hourHeight)
                            .id(hour)
                    }
                }

                ForEach(entries) { entry in
                    let width = max(
                        28,
                        (proxy.size.width - 4) / CGFloat(entry.columnCount) - 2
                    )
                    Button {
                        onOpen(entry.item)
                    } label: {
                        CalendarEventChip(
                            item: entry.item,
                            labelColor: labelColor(entry.item),
                            showsTime: true
                        )
                            .frame(width: width, height: chipHeight, alignment: .topLeading)
                    }
                    .buttonStyle(.plain)
                    .offset(
                        x: 2 + CGFloat(entry.column) * (width + 2),
                        y: CGFloat(entry.minuteOfDay) / 60 * hourHeight
                    )
                }

                if day.isToday {
                    nowLine
                }
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: totalHeight)
    }

    private var hourLines: some View {
        ZStack(alignment: .topLeading) {
            ForEach(0..<CalendarTimeGrid.hoursShown, id: \.self) { hour in
                Rectangle()
                    .fill(Color.secondary.opacity(hour % 6 == 0 ? 0.22 : 0.10))
                    .frame(height: 0.5)
                    .offset(y: CGFloat(hour) * hourHeight)
            }
        }
        .accessibilityHidden(true)
    }

    private var nowLine: some View {
        let minute = CalendarTimeGrid.minuteOfDay(now, calendar: calendar)
        return ZStack(alignment: .leading) {
            Rectangle()
                .fill(Color.red)
                .frame(height: 1.5)
            Circle()
                .fill(Color.red)
                .frame(width: 6, height: 6)
                .offset(x: -3)
        }
        .offset(y: CGFloat(minute) / 60 * hourHeight)
        .accessibilityLabel("Now")
    }

    /// Opens on the first deadline of the day rather than on midnight, with an
    /// hour of context above it.
    private var initialHour: Int {
        let minutes = days
            .flatMap { CalendarTimeGrid.timedEntries(in: $0.items, calendar: calendar) }
            .map(\.minuteOfDay)
        guard let earliest = minutes.min() else {
            return 8
        }
        return max(0, earliest / 60 - 1)
    }
}

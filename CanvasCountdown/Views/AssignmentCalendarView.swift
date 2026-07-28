import SwiftUI

/// The calendar, at whichever of the four scales is showing.
///
/// Every grid reads `filteredAssignments`, so the course filter, the search
/// query and the completed/ignored toggle apply here exactly as they do in the
/// list. Switching scale never changes which events are in scope, only how much
/// time is on screen.
struct AssignmentCalendarView: View {
    @Bindable var viewModel: MainViewModel
    let onOpen: (AssignmentListItem) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            navigationBar
            grid
        }
    }

    @ViewBuilder
    private var grid: some View {
        switch viewModel.calendarScale {
        case .day:
            CalendarTimeGridView(
                days: [viewModel.calendarDay],
                now: viewModel.currentDate,
                onOpen: onOpen,
                onOpenDay: { viewModel.showCalendarDate($0, scale: .day) },
                label: { viewModel.label(for: $0) }
            )
        case .week:
            CalendarTimeGridView(
                days: viewModel.calendarWeekDays,
                now: viewModel.currentDate,
                onOpen: onOpen,
                onOpenDay: { viewModel.showCalendarDate($0, scale: .day) },
                label: { viewModel.label(for: $0) }
            )
        case .month:
            CalendarMonthGrid(
                days: viewModel.calendarDays,
                selectedDay: viewModel.selectedCalendarDay,
                now: viewModel.currentDate,
                onSelect: { viewModel.selectCalendarDay($0) },
                onOpenDay: { viewModel.showCalendarDate($0, scale: .day) },
                onOpen: onOpen,
                label: { viewModel.label(for: $0) }
            )
        case .year:
            CalendarYearGrid(
                months: viewModel.calendarYearMonths,
                onOpenDay: { viewModel.showCalendarDate($0, scale: .day) },
                onOpenMonth: { viewModel.showCalendarDate($0, scale: .month) },
                label: { viewModel.label(for: $0) }
            )
        }
    }

    private var navigationBar: some View {
        HStack(spacing: 12) {
            Text(viewModel.calendarTitle)
                .font(.title3.weight(.semibold))
                .lineLimit(1)
                .accessibilityAddTraits(.isHeader)

            Spacer(minLength: 8)

            scalePicker
            goToDateButton
            stepControls
        }
        .padding(.horizontal, 22)
        .padding(.bottom, 10)
    }

    private var scalePicker: some View {
        Picker(
            "Calendar scale",
            selection: Binding(
                get: { viewModel.calendarScale },
                set: { viewModel.showCalendarScale($0) }
            )
        ) {
            ForEach(CalendarScale.allCases) { scale in
                Text(scale.title).tag(scale)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
        .help("Show a day, a week, a month or the whole year")
    }

    /// Typing a date is the fastest way across a year, and the only way that
    /// does not depend on knowing how many times to press an arrow.
    private var goToDateButton: some View {
        Button {
            viewModel.isShowingCalendarDateEntry = true
        } label: {
            Image(systemName: "calendar.badge.clock")
        }
        .help("Go to a date")
        .accessibilityLabel("Go to a date")
        .popover(isPresented: $viewModel.isShowingCalendarDateEntry, arrowEdge: .bottom) {
            CalendarDateEntryField(viewModel: viewModel)
        }
    }

    private var stepControls: some View {
        HStack(spacing: 6) {
            Button {
                viewModel.showCalendar(offsetBy: -1)
            } label: {
                Image(systemName: "chevron.left")
            }
            .help("Previous \(viewModel.calendarScale.title.lowercased())")
            .accessibilityLabel("Previous \(viewModel.calendarScale.title.lowercased())")

            Button("Today") {
                viewModel.showToday()
            }
            .help("Show today")
            .disabled(viewModel.isShowingToday)

            Button {
                viewModel.showCalendar(offsetBy: 1)
            } label: {
                Image(systemName: "chevron.right")
            }
            .help("Next \(viewModel.calendarScale.title.lowercased())")
            .accessibilityLabel("Next \(viewModel.calendarScale.title.lowercased())")
        }
    }
}

/// The Go to Date field: type a date, see the day it resolves to, press Return.
///
/// The resolved day is shown before it is committed, because "8/9" means two
/// different days in two different places and the user should see which one
/// they are about to get.
struct CalendarDateEntryField: View {
    @Bindable var viewModel: MainViewModel
    @FocusState private var isFieldFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Go to Date")
                .font(.headline)

            TextField(
                "Date",
                text: $viewModel.calendarDateEntry,
                prompt: Text("14 Aug 2026, 2026-08-14, today")
            )
            .textFieldStyle(.roundedBorder)
            .frame(width: 240)
            .focused($isFieldFocused)
            .onSubmit(commit)

            Group {
                if viewModel.calendarDateEntry.isEmpty {
                    Text("Type a date, or a month and a day.")
                        .foregroundStyle(.secondary)
                } else if let resolved = viewModel.calendarDateEntryResult {
                    Label(
                        resolved.formatted(date: .complete, time: .omitted),
                        systemImage: "arrow.turn.down.right"
                    )
                    .foregroundStyle(.secondary)
                } else {
                    Label("Not a date this app can read", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.orange)
                }
            }
            .font(.caption)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)

            HStack {
                Spacer()
                Button("Go", action: commit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(viewModel.calendarDateEntryResult == nil)
            }
        }
        .padding(16)
        .frame(width: 280)
        .onAppear {
            isFieldFocused = true
        }
    }

    private func commit() {
        if viewModel.commitCalendarDateEntry() {
            viewModel.isShowingCalendarDateEntry = false
        }
    }
}

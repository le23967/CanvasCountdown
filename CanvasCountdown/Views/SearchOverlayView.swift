import SwiftUI

/// Search as a panel over the window, in the shape everyone already knows.
///
/// The field used to live in the toolbar, where making room for it meant
/// taking the other six controls away — so the moment someone went looking for
/// something, everything they might have clicked instead vanished. A panel
/// costs nothing: the toolbar and the list stay exactly as they were, the
/// results sit in the panel rather than behind it, and Escape puts everything
/// back untouched.
///
/// Sits in the upper third rather than dead centre, for the same reason
/// Spotlight does: the results grow downwards, and a panel centred on its empty
/// state jumps as soon as it has anything to say.
struct SearchOverlayView: View {
    @Bindable var viewModel: MainViewModel

    @FocusState private var isFieldFocused: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private let panelWidth: CGFloat = 560

    var body: some View {
        ZStack(alignment: .top) {
            // A click anywhere outside puts the panel away, which is the
            // gesture people already try first.
            Color.black.opacity(0.18)
                .ignoresSafeArea()
                .contentShape(Rectangle())
                .onTapGesture {
                    viewModel.dismissSearch()
                }
                .accessibilityHidden(true)

            panel
                .frame(width: panelWidth)
                .padding(.top, 96)
        }
        .transition(
            reduceMotion
                ? .opacity
                : .opacity.combined(with: .scale(scale: 0.97, anchor: .top))
        )
        .onAppear {
            isFieldFocused = true
        }
        .onExitCommand {
            viewModel.dismissSearch()
        }
    }

    private var panel: some View {
        VStack(spacing: 0) {
            field

            if viewModel.hasSearchQuery {
                Divider()
                results
            }
        }
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .strokeBorder(Color.primary.opacity(0.10), lineWidth: 1)
        }
        .shadow(color: .black.opacity(0.28), radius: 26, y: 12)
    }

    private var field: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .font(.title3)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)

            TextField(
                "Search assignments and courses",
                text: $viewModel.searchText
            )
            .textFieldStyle(.plain)
            .font(.title3)
            .focused($isFieldFocused)
            .onSubmit {
                viewModel.openHighlightedSearchResult()
            }
            .onChange(of: viewModel.searchText) { _, _ in
                viewModel.searchQueryDidChange()
            }
            .accessibilityLabel("Search assignments and courses")

            if viewModel.hasSearchQuery {
                Button {
                    viewModel.clearSearchQuery()
                    isFieldFocused = true
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Clear search")
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
        // Arrow keys move the highlight without the field losing focus, so
        // typing and choosing are the same gesture.
        .onKeyPress(.upArrow) {
            viewModel.moveSearchHighlight(by: -1)
            return .handled
        }
        .onKeyPress(.downArrow) {
            viewModel.moveSearchHighlight(by: 1)
            return .handled
        }
    }

    @ViewBuilder
    private var results: some View {
        let results = viewModel.searchResults
        if results.isEmpty {
            HStack(spacing: 8) {
                Image(systemName: "questionmark.circle")
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
                Text("Nothing matches “\(viewModel.searchText)”")
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
        } else {
            // No scroll view: the result count is already capped, so the rows
            // fit as they are. A scroll view here would stretch to its own
            // maximum and leave a panel mostly full of empty space.
            VStack(spacing: 0) {
                ForEach(results) { item in
                    SearchResultRow(
                        item: item,
                        now: viewModel.currentDate,
                        label: viewModel.label(for: item),
                        isHighlighted:
                            item.id == viewModel.highlightedSearchResultID
                    )
                    .contentShape(Rectangle())
                    .onTapGesture {
                        viewModel.openSearchResult(item)
                    }
                    .onHover { isInside in
                        if isInside {
                            viewModel.highlightedSearchResultID = item.id
                        }
                    }
                }
            }
            .padding(6)

            Divider()

            HStack(spacing: 14) {
                hint("↑↓", "Move")
                hint("↩", "Open")
                hint("esc", "Close")
                Spacer(minLength: 0)
                Text(
                    results.count == 1
                        ? "1 result"
                        : "\(results.count) results"
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
        }
    }

    /// Says which keys do what, because a panel that only answers to keys
    /// nobody was told about is a panel that only works for whoever built it.
    private func hint(_ key: String, _ meaning: String) -> some View {
        HStack(spacing: 4) {
            Text(key)
                .font(.caption2.weight(.semibold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 4))
            Text(meaning)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(key) to \(meaning)")
    }
}

/// One found assignment: what it is, which course, and how long is left.
struct SearchResultRow: View {
    let item: AssignmentListItem
    let now: Date
    let label: EventLabel?
    let isHighlighted: Bool

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: item.isCompleted
                ? "checkmark.circle.fill"
                : "calendar")
                .foregroundStyle(item.isCompleted ? .green : .secondary)
                .frame(width: 18)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    if let label {
                        Circle()
                            .fill(Color(nsColor: label.color))
                            .frame(width: 7, height: 7)
                            .accessibilityHidden(true)
                    }
                    Text(item.title)
                        .lineLimit(1)
                }

                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            Text(remaining)
                .font(.caption.weight(.medium))
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background {
            if isHighlighted {
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color.accentColor.opacity(0.18))
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isHighlighted ? [.isSelected] : [])
    }

    private var subtitle: String {
        let due = item.dueDate.formatted(date: .abbreviated, time: .shortened)
        guard let course = item.normalizedCourseName else {
            return due
        }
        return "\(course) · \(due)"
    }

    private var remaining: String {
        let days = item.remainingDays(relativeTo: now)
        if item.isCompleted {
            return "Done"
        }
        switch days {
        case 0:
            return "Today"
        case 1:
            return "1 day"
        case ..<0:
            let overdue = -days
            return overdue == 1 ? "1 day ago" : "\(overdue) days ago"
        default:
            return "\(days) days"
        }
    }
}

import AppKit
import SwiftData
import SwiftUI

@main
@MainActor
struct CanvasCountdownApp: App {
    private let modelContainer: ModelContainer?
    private let screenshotImportViewModel: ScreenshotImportViewModel?
    private let startupErrorMessage: String?
    @State private var viewModel: MainViewModel?

    init() {
        do {
            let dependencies = try AppDependencies.make()
            modelContainer = dependencies.modelContainer
            let mainViewModel = dependencies.viewModel
            screenshotImportViewModel = ScreenshotImportViewModel(
                coordinator: dependencies.screenshotImportCoordinator,
                existingProvider: { [weak mainViewModel] in
                    guard let mainViewModel else {
                        return []
                    }
                    return try await mainViewModel.currentSnapshots()
                }
            )
            startupErrorMessage = nil
            _viewModel = State(initialValue: mainViewModel)
        } catch {
            modelContainer = nil
            screenshotImportViewModel = nil
            startupErrorMessage =
                "Canvas Countdown could not open its local database. "
                + "Quit the app and try again. Your Canvas feed URL remains "
                + "protected in Keychain."
            _viewModel = State(initialValue: nil)
        }
    }

    @ViewBuilder
    private var rootView: some View {
        if let viewModel, let modelContainer, let screenshotImportViewModel {
            MainView(
                viewModel: viewModel,
                screenshotImportViewModel: screenshotImportViewModel
            )
                .modelContainer(modelContainer)
        } else {
            ContentUnavailableView {
                Label(
                    "Local database unavailable",
                    systemImage: "externaldrive.badge.exclamationmark"
                )
            } description: {
                Text(
                    startupErrorMessage
                        ?? "Canvas Countdown could not start."
                )
            } actions: {
                Button("Quit Canvas Countdown") {
                    NSApplication.shared.terminate(nil)
                }
                .keyboardShortcut("q", modifiers: .command)
            }
            .frame(minWidth: 620, minHeight: 420)
        }
    }

    var body: some Scene {
        WindowGroup {
            rootView
        }
        .defaultSize(width: 1_040, height: 720)
        .windowToolbarStyle(.unified)
        .commands {
            SidebarCommands()
            // The calendar's own shortcuts, in the menu the system calendar
            // puts them in, so ⌘1 … ⌘4 mean here what they mean there.
            CommandGroup(after: .sidebar) {
                Divider()

                ForEach(CalendarScale.allCases) { scale in
                    Button(scale.title) {
                        viewModel?.showCalendarSection(scale)
                    }
                    .keyboardShortcut(
                        KeyEquivalent(scale.shortcut),
                        modifiers: .command
                    )
                    .disabled(viewModel == nil)
                }

                Divider()

                Button("Today") {
                    viewModel?.showCalendarSection()
                }
                .keyboardShortcut("t", modifiers: .command)
                .disabled(viewModel == nil)

                Button("Go to Date…") {
                    viewModel?.presentCalendarDateEntry()
                }
                .keyboardShortcut("t", modifiers: [.command, .shift])
                .disabled(viewModel == nil)
            }
            CommandGroup(replacing: .newItem) {
                Button("New Manual Event…") {
                    viewModel?.presentNewManualEvent()
                }
                .keyboardShortcut("n", modifiers: .command)
                .disabled(viewModel == nil)

                Divider()

                Button("Import Canvas Feed…") {
                    viewModel?.presentFeedImport()
                }
                .disabled(viewModel == nil)

                Button("Import from Canvas Screenshot…") {
                    viewModel?.presentScreenshotImport()
                }
                .keyboardShortcut("i", modifiers: [.command, .shift])
                .disabled(viewModel == nil)
            }
            CommandGroup(after: .windowArrangement) {
                Button("Assistant") {
                    viewModel?.toggleAssistant()
                }
                .keyboardShortcut("a", modifiers: [.command, .option])
                .disabled(viewModel == nil)
            }
            // Undo lives in the Edit menu, where anyone looking for it will
            // look, rather than only on a toast that times out or a keyboard
            // shortcut nobody was told about. Deliberately not plain ⌘Z: that
            // belongs to the text fields in the editor sheet, and taking it
            // from them to save a menu item would be a poor trade.
            CommandGroup(after: .undoRedo) {
                Button(viewModel?.undoAdditionMenuTitle ?? "Undo Adding") {
                    guard let viewModel else {
                        return
                    }
                    Task { await viewModel.undoLastAddition() }
                }
                .keyboardShortcut("z", modifiers: [.command, .option])
                .disabled(viewModel?.canUndoAddition != true)

                Divider()
            }
            CommandGroup(after: .textEditing) {
                Button("Find") {
                    viewModel?.presentSearch()
                }
                .keyboardShortcut("f", modifiers: .command)
                .disabled(viewModel == nil)
            }
        }
    }
}

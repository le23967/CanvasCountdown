import Foundation
import UserNotifications
import XCTest
@testable import CanvasCountdown

/// Row interaction: opening an event, what each event type allows, and the
/// keyboard equivalents.
@MainActor
final class EventInteractionTests: XCTestCase {
    func testOpeningAManualEventPresentsTheEditor() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let manual = try XCTUnwrap(
            context.viewModel.assignments.first(where: \.isManual)
        )

        context.viewModel.presentEditor(for: manual)

        XCTAssertTrue(context.viewModel.isShowingManualEditor)
        XCTAssertFalse(context.viewModel.isShowingImportedDetails)
        XCTAssertEqual(context.viewModel.manualEventDraft.eventID, manual.id)
        XCTAssertEqual(context.viewModel.manualEventDraft.title, manual.title)
    }

    func testOpeningAnImportedEventPresentsDetailsRatherThanTheEditor() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let imported = try XCTUnwrap(
            context.viewModel.assignments.first(where: { !$0.isManual })
        )

        context.viewModel.presentEditor(for: imported)

        XCTAssertTrue(context.viewModel.isShowingImportedDetails)
        XCTAssertFalse(
            context.viewModel.isShowingManualEditor,
            "Canvas owns the title and deadline, so it is not freely editable"
        )
        XCTAssertEqual(
            context.viewModel.currentImportedEventDetails?.id,
            imported.id
        )
    }

    func testImportedDetailsFollowTheLatestLocalState() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let imported = try XCTUnwrap(
            context.viewModel.assignments.first(where: { !$0.isManual })
        )
        context.viewModel.presentEditor(for: imported)
        XCTAssertEqual(
            context.viewModel.currentImportedEventDetails?.isCompleted,
            false
        )

        context.viewModel.toggleCompleted(imported)
        try await waitUntil {
            context.viewModel.assignments
                .first { $0.id == imported.id }?.isCompleted == true
        }

        XCTAssertEqual(
            context.viewModel.currentImportedEventDetails?.isCompleted,
            true,
            "The open sheet reflects the change immediately"
        )
    }

    func testReturnOpensTheFocusedRow() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let manual = try XCTUnwrap(
            context.viewModel.assignments.first(where: \.isManual)
        )

        context.viewModel.selectedEventID = manual.id
        context.viewModel.openSelectedEvent()

        XCTAssertTrue(context.viewModel.isShowingManualEditor)
    }

    func testReturnWithoutAFocusedRowDoesNothing() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.selectedEventID = nil
        context.viewModel.openSelectedEvent()

        XCTAssertFalse(context.viewModel.isShowingManualEditor)
        XCTAssertFalse(context.viewModel.isShowingImportedDetails)
    }

    func testDeleteOffersOnlyManualEvents() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let manual = try XCTUnwrap(
            context.viewModel.assignments.first(where: \.isManual)
        )
        let imported = try XCTUnwrap(
            context.viewModel.assignments.first(where: { !$0.isManual })
        )

        context.viewModel.selectedEventID = manual.id
        XCTAssertEqual(context.viewModel.deletionCandidate()?.id, manual.id)

        context.viewModel.selectedEventID = imported.id
        XCTAssertNil(
            context.viewModel.deletionCandidate(),
            "A Canvas assignment is not the app's to delete"
        )

        context.viewModel.selectedEventID = nil
        XCTAssertNil(context.viewModel.deletionCandidate())
    }

    func testSelectedItemTracksTheFocusedRow() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let imported = try XCTUnwrap(
            context.viewModel.assignments.first(where: { !$0.isManual })
        )

        context.viewModel.selectedEventID = imported.id
        XCTAssertEqual(context.viewModel.selectedItem?.id, imported.id)

        context.viewModel.selectedEventID = UUID()
        XCTAssertNil(
            context.viewModel.selectedItem,
            "A stale selection must not resolve to the wrong row"
        )
    }

    func testCompletedAndIgnoredRemainAvailableForImportedEvents() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let imported = try XCTUnwrap(
            context.viewModel.assignments.first(where: { !$0.isManual })
        )

        context.viewModel.toggleIgnored(imported)
        try await waitUntil {
            context.viewModel.assignments
                .first { $0.id == imported.id }?.isIgnored == true
        }

        context.viewModel.toggleCompleted(imported)
        try await waitUntil {
            context.viewModel.assignments
                .first { $0.id == imported.id }?.isCompleted == true
        }
    }

    func testDeletingAnImportedEventThroughTheViewModelIsRefused() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let imported = try XCTUnwrap(
            context.viewModel.assignments.first(where: { !$0.isManual })
        )

        context.viewModel.deleteManualEvent(imported)
        try await Task.sleep(for: .milliseconds(200))

        XCTAssertTrue(
            context.viewModel.assignments.contains { $0.id == imported.id },
            "Only manual events may be deleted"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
    }

    private func makeContext() throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let due = calendar.date(byAdding: .day, value: 3, to: .now) ?? .now

        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Imported quiz",
                    courseName: "PHYS200",
                    dueDate: due,
                    source: .canvasCalendarFeed
                ),
                AssignmentSnapshot(
                    title: "Hand written",
                    courseName: nil,
                    dueDate: due.addingTimeInterval(86_400),
                    source: .manual
                ),
            ]
        )

        let suiteName = "EventInteractionTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: InteractionCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: ScopeDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel)
    }

    private func waitUntil(
        _ condition: @MainActor () -> Bool
    ) async throws {
        for _ in 0..<200 {
            if condition() {
                return
            }
            try await Task.sleep(for: .milliseconds(5))
        }
        XCTFail("Timed out waiting for the view model to update")
    }
}

private actor InteractionCoordinatorStub: FeedRefreshCoordinating {
    func preview(feedURL: URL, now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func preview(now: Date) throws -> FeedPreview {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func importSelected(
        _ events: [ParsedCalendarEvent],
        from feedURL: URL,
        at date: Date
    ) throws -> RefreshResult {
        throw RefreshCoordinatorError.noEventsSelected
    }

    func refresh(now: Date, trigger: RefreshTrigger) throws -> RefreshResult {
        throw RefreshCoordinatorError.noSavedFeedURL
    }

    func diagnosticSnapshot() -> FeedRefreshDiagnostic? { nil }
}

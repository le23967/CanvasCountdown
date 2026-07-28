import Foundation
import XCTest
@testable import CanvasCountdown

/// Labels: the library's rules, and what putting one on an event does to the
/// list and the calendar. All fixtures are invented.
@MainActor
final class EventLabelTests: XCTestCase {
    // MARK: - The library

    func testDefaultsAreUsableAndDistinct() {
        let library = EventLabelLibrary.defaults

        XCTAssertFalse(library.labels.isEmpty)
        XCTAssertEqual(
            Set(library.labels.map(\.name)).count,
            library.labels.count,
            "Two labels with the same name cannot be told apart in a menu"
        )
        XCTAssertTrue(library.labels.allSatisfy { NSColor(hex: $0.colorHex) != nil })
    }

    func testAddingReturnsTheLabelAndKeepsIt() throws {
        var library = EventLabelLibrary.empty
        let added = try library.add(name: "  Society  ", colorHex: "#30A46C")

        XCTAssertEqual(added.name, "Society", "Surrounding space is not part of a name")
        XCTAssertEqual(library.labels.map(\.id), [added.id])
    }

    func testNamesMustBeUsableAndUnique() throws {
        var library = EventLabelLibrary.empty
        _ = try library.add(name: "Important", colorHex: "#E5484D")

        XCTAssertEqual(library.validate(name: ""), .empty)
        XCTAssertEqual(library.validate(name: "   "), .empty)
        XCTAssertEqual(library.validate(name: "important"), .duplicate)
        XCTAssertEqual(library.validate(name: " IMPORTANT "), .duplicate)
        XCTAssertEqual(
            library.validate(name: String(repeating: "A", count: 25)),
            .tooLong
        )
        XCTAssertNil(library.validate(name: "Personal"))
        XCTAssertNil(
            library.validate(name: "Important", excluding: library.labels[0].id),
            "A label may keep its own name while being edited"
        )
    }

    func testRenamingAndRecolouring() throws {
        var library = EventLabelLibrary.empty
        let label = try library.add(name: "Important", colorHex: "#E5484D")

        try library.rename(label.id, to: "Urgent")
        library.recolor(label.id, to: "#3E63DD")

        XCTAssertEqual(library.labels.first?.name, "Urgent")
        XCTAssertEqual(library.labels.first?.colorHex, "#3E63DD")
    }

    func testARenameToATakenNameIsRefused() throws {
        var library = EventLabelLibrary.empty
        let first = try library.add(name: "Important", colorHex: "#E5484D")
        _ = try library.add(name: "Personal", colorHex: "#30A46C")

        XCTAssertThrowsError(try library.rename(first.id, to: "Personal")) { error in
            XCTAssertEqual(error as? EventLabelNameError, .duplicate)
        }
        XCTAssertEqual(library.labels.first?.name, "Important")
    }

    func testAnUnreadableColourIsNotStored() throws {
        var library = EventLabelLibrary.empty
        let label = try library.add(name: "Important", colorHex: "#E5484D")

        library.recolor(label.id, to: "not a colour")

        XCTAssertEqual(library.labels.first?.colorHex, "#E5484D")
    }

    func testAnUnusableLabelIsDroppedWhenRead() throws {
        let json = """
        {"version":1,"labels":[
            {"id":"\(UUID().uuidString)","name":"Good","colorHex":"#30A46C"},
            {"id":"\(UUID().uuidString)","name":"  ","colorHex":"#30A46C"},
            {"id":"\(UUID().uuidString)","name":"Bad colour","colorHex":"zzz"}
        ]}
        """
        let library = try JSONDecoder().decode(
            EventLabelLibrary.self,
            from: Data(json.utf8)
        )

        XCTAssertEqual(library.labels.map(\.name), ["Good"])
    }

    func testALibraryFromALaterVersionIsLeftAloneRatherThanHalfRead() throws {
        let json = """
        {"version":99,"labels":[
            {"id":"\(UUID().uuidString)","name":"From the future","colorHex":"#30A46C"}
        ]}
        """
        let library = try JSONDecoder().decode(
            EventLabelLibrary.self,
            from: Data(json.utf8)
        )

        XCTAssertTrue(library.labels.isEmpty)
    }

    // MARK: - Putting one on an event

    func testLabellingAnEventShowsUpOnTheItemAndPersists() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let label = try XCTUnwrap(context.viewModel.eventLabels.first)
        let item = try XCTUnwrap(context.viewModel.assignments.first)

        context.viewModel.setLabel(label.id, for: item)
        try await Task.sleep(for: .milliseconds(120))

        let updated = try XCTUnwrap(
            context.viewModel.assignments.first { $0.id == item.id }
        )
        XCTAssertEqual(updated.labelID, label.id)
        XCTAssertEqual(context.viewModel.label(for: updated)?.name, label.name)
        XCTAssertEqual(context.viewModel.labelUsageCount(label.id), 1)
    }

    func testTheSameCallTakesTheLabelOffAgain() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let label = try XCTUnwrap(context.viewModel.eventLabels.first)
        let item = try XCTUnwrap(context.viewModel.assignments.first)

        context.viewModel.setLabel(label.id, for: item)
        try await Task.sleep(for: .milliseconds(120))
        let labelled = try XCTUnwrap(
            context.viewModel.assignments.first { $0.id == item.id }
        )

        context.viewModel.setLabel(nil, for: labelled)
        try await Task.sleep(for: .milliseconds(120))

        let cleared = try XCTUnwrap(
            context.viewModel.assignments.first { $0.id == item.id }
        )
        XCTAssertNil(cleared.labelID)
        XCTAssertNil(context.viewModel.label(for: cleared))
    }

    func testTheLabelReachesTheCalendarToo() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let label = try XCTUnwrap(context.viewModel.eventLabels.first)
        let item = try XCTUnwrap(context.viewModel.assignments.first)

        context.viewModel.setLabel(label.id, for: item)
        try await Task.sleep(for: .milliseconds(120))

        context.viewModel.sidebarSelection = .calendar
        context.viewModel.showCalendarDate(item.dueDate, scale: .day)
        let onTheDay = try XCTUnwrap(
            context.viewModel.calendarDay.items.first { $0.id == item.id }
        )

        XCTAssertEqual(
            context.viewModel.label(for: onTheDay)?.id,
            label.id,
            "The list and the calendar must agree about a colour"
        )
    }

    func testDeletingALabelTakesItOffEverythingCarryingIt() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let label = try XCTUnwrap(context.viewModel.eventLabels.first)
        for item in context.viewModel.assignments {
            context.viewModel.setLabel(label.id, for: item)
        }
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertGreaterThan(context.viewModel.labelUsageCount(label.id), 0)

        context.viewModel.deleteEventLabel(label.id)
        try await Task.sleep(for: .milliseconds(200))

        XCTAssertFalse(context.viewModel.eventLabels.contains { $0.id == label.id })
        XCTAssertTrue(
            context.viewModel.assignments.allSatisfy { $0.labelID == nil },
            "No event may be left pointing at a colour that no longer exists"
        )
    }

    func testLabelsSurviveARestart() async throws {
        let context = try makeContext()
        await context.viewModel.start()

        context.viewModel.addEventLabel()
        let added = try XCTUnwrap(context.viewModel.eventLabels.last)

        XCTAssertEqual(
            SettingsStore(defaults: context.defaults).eventLabels.labels.last?.name,
            added.name
        )
    }

    func testAddedLabelsAreNamedAndColouredWithoutAsking() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let before = context.viewModel.eventLabels.count

        context.viewModel.addEventLabel()
        context.viewModel.addEventLabel()

        let labels = context.viewModel.eventLabels
        XCTAssertEqual(labels.count, before + 2)
        XCTAssertEqual(
            Set(labels.map(\.name)).count,
            labels.count,
            "A second unnamed label must not collide with the first"
        )
        XCTAssertTrue(labels.allSatisfy { NSColor(hex: $0.colorHex) != nil })
    }

    func testResettingSettingsDoesNotThrowAwayTheLabels() async throws {
        let context = try makeContext()
        await context.viewModel.start()
        let store = SettingsStore(defaults: context.defaults)
        let before = store.eventLabels.labels.map(\.id)

        store.reset()

        XCTAssertEqual(
            store.eventLabels.labels.map(\.id),
            before,
            "Labels are the user's own work, not a preference with a default"
        )
    }

    // MARK: - Helpers

    private struct Context {
        let viewModel: MainViewModel
        let defaults: UserDefaults
    }

    private func makeContext() throws -> Context {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        func due(inDays days: Int) -> Date {
            let start = calendar.startOfDay(for: .now)
            let day = calendar.date(byAdding: .day, value: days, to: start) ?? start
            return calendar.date(bySettingHour: 16, minute: 0, second: 0, of: day) ?? day
        }

        let repository = ScopeRepositoryStub(
            snapshots: [
                AssignmentSnapshot(
                    title: "Example Quiz",
                    courseName: "99999 Example Interaction Course",
                    dueDate: due(inDays: 2),
                    source: .canvasCalendarFeed
                ),
                AssignmentSnapshot(
                    title: "Example Poster Submission",
                    courseName: "99999 Example Interaction Course",
                    dueDate: due(inDays: 9),
                    source: .canvasCalendarFeed
                ),
            ]
        )

        let suiteName = "EventLabelTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)

        let viewModel = MainViewModel(
            repository: repository,
            refreshCoordinator: LabelCoordinatorStub(),
            feedURLStore: IsolatedFeedURLStore(),
            settingsStore: SettingsStore(defaults: defaults),
            dockRenderer: AppearanceRecordingDockSpy(),
            notificationScheduler: InertNotificationScheduler(),
            calendar: calendar,
            automaticActivityEnabled: false
        )
        return Context(viewModel: viewModel, defaults: defaults)
    }
}

private actor LabelCoordinatorStub: FeedRefreshCoordinating {
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

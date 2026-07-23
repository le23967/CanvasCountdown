import Foundation
import XCTest
@testable import CanvasCountdown

/// The optional assistant: what may leave this Mac, what happens to a bad
/// reply, and the promise that nothing it says is saved without review.
///
/// No test makes a real network request. All fixtures are invented.
final class AssistantTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Australia/Sydney")!
        return calendar
    }

    private func date(_ year: Int, _ month: Int, _ day: Int, hour: Int = 9) -> Date {
        calendar.date(
            from: DateComponents(year: year, month: month, day: day, hour: hour)
        )!
    }

    private func item(
        _ title: String,
        course: String?,
        due: Date
    ) -> AssignmentListItem {
        AssignmentListItem(
            id: UUID(),
            title: title,
            courseName: course,
            dueDate: due,
            isCompleted: false,
            isIgnored: false,
            isManual: false
        )
    }

    // MARK: - Defaults and privacy posture

    func testDefaultsAreAvailableButNeverSendAnythingAway() {
        let defaults = AssistantSettings.defaults

        // Available out of the box, so the toolbar button does something when
        // pressed rather than leading to a dead end.
        XCTAssertTrue(defaults.isEnabled)

        // What must never drift: the default configuration cannot upload
        // anything. Choosing a cloud provider is a separate, deliberate act.
        XCTAssertEqual(defaults.provider, .local)
        XCTAssertTrue(
            defaults.staysOnThisMac,
            "The default must never reach off this Mac"
        )
        XCTAssertFalse(defaults.provider.requiresAPIKey)
    }

    func testSwitchingProviderCarriesItsOwnEndpointAndModel() {
        let groq = AssistantSettings.applying(.groq, to: .defaults)

        XCTAssertEqual(groq.provider, .groq)
        XCTAssertTrue(groq.baseURL.contains("api.groq.com"))
        XCTAssertFalse(groq.staysOnThisMac)
        XCTAssertTrue(groq.provider.requiresAPIKey)

        let backToLocal = AssistantSettings.applying(.local, to: groq)
        XCTAssertTrue(backToLocal.staysOnThisMac)
        XCTAssertFalse(backToLocal.provider.requiresAPIKey)
    }

    func testLocalProviderPointedAtARemoteHostIsNotClaimedAsPrivate() {
        var settings = AssistantSettings.defaults
        settings.provider = .local
        settings.baseURL = "https://example.com/v1"

        XCTAssertFalse(
            settings.staysOnThisMac,
            "Naming it local does not keep the data here; the address decides"
        )
    }

    func testDigestCarriesOnlyTheThreeAgreedFields() {
        let due = date(2026, 8, 4, hour: 16)
        let source = item("Example Quiz", course: "99999 Example Course", due: due)
        let digest = AssistantAssignmentDigest(source)

        XCTAssertEqual(digest.title, "Example Quiz")
        XCTAssertEqual(digest.courseName, "99999 Example Course")
        XCTAssertEqual(digest.dueDate, due)

        // Completion and ignore state are local decisions and are not included.
        let mirror = Mirror(reflecting: digest)
        let fields = mirror.children.compactMap(\.label)
        XCTAssertEqual(Set(fields), ["title", "courseName", "dueDate"])
        XCTAssertFalse(fields.contains("isCompleted"))
        XCTAssertFalse(fields.contains("isIgnored"))
        XCTAssertFalse(fields.contains("id"))
    }

    func testDigestLineMentionsOnlyTitleCourseAndRemainingDays() {
        let now = date(2026, 7, 28)
        let digest = AssistantAssignmentDigest(
            item("Example Quiz", course: "99999 Example Course", due: date(2026, 8, 4))
        )

        let line = digest.line(now: now, calendar: calendar)

        XCTAssertTrue(line.contains("Example Quiz"))
        XCTAssertTrue(line.contains("99999 Example Course"))
        XCTAssertTrue(line.contains("7 days"))
    }

    // MARK: - Reading the model's reply

    func testWellFormedReplyBecomesDrafts() {
        let reply = """
        {"tasks":[{"title":"Essay draft","course":null,"due":"2026-08-07T17:00"}]}
        """

        let drafts = ChatCompletionsAssistantService.parseDrafts(
            reply,
            sourceText: "essay draft due next Friday 5pm",
            calendar: calendar
        )

        XCTAssertEqual(drafts.count, 1)
        XCTAssertEqual(drafts[0].title, "Essay draft")
        XCTAssertNil(drafts[0].courseName)
        XCTAssertEqual(
            calendar.component(.hour, from: try! XCTUnwrap(drafts[0].dueDate)),
            17
        )
        XCTAssertTrue(drafts[0].canBeSaved)
    }

    func testReplyWrappedInProseIsStillRead() {
        // Small local models often add commentary around the JSON.
        let reply = """
        Sure! Here is the task you asked for:
        {"tasks":[{"title":"Reading","course":"99999 Example Course","due":"2026-08-09T23:59"}]}
        Let me know if you want more.
        """

        let drafts = ChatCompletionsAssistantService.parseDrafts(
            reply,
            sourceText: "reading by Sunday",
            calendar: calendar
        )

        XCTAssertEqual(drafts.count, 1)
        XCTAssertEqual(drafts[0].courseName, "99999 Example Course")
    }

    func testMalformedReplyProducesNothingRatherThanAGuess() {
        for reply in [
            "I could not work that out.",
            "{ not json",
            "{\"tasks\": \"nope\"}",
            "",
        ] {
            XCTAssertTrue(
                ChatCompletionsAssistantService.parseDrafts(
                    reply,
                    sourceText: "something",
                    calendar: calendar
                ).isEmpty,
                "A reply that cannot be read must not become a deadline: \(reply)"
            )
        }
    }

    func testTaskWithoutADateArrivesUnsaveable() {
        let reply = """
        {"tasks":[{"title":"Read chapter 4","course":null,"due":null}]}
        """

        let drafts = ChatCompletionsAssistantService.parseDrafts(
            reply,
            sourceText: "read chapter 4",
            calendar: calendar
        )

        XCTAssertEqual(drafts.count, 1)
        XCTAssertNil(drafts[0].dueDate)
        XCTAssertFalse(
            drafts[0].canBeSaved,
            "Review must supply a date; the app will not invent one"
        )
    }

    func testUnparseableDateLeavesTheTaskWithoutOne() {
        let reply = """
        {"tasks":[{"title":"Essay","course":null,"due":"next Friday"}]}
        """

        let drafts = ChatCompletionsAssistantService.parseDrafts(
            reply,
            sourceText: "essay",
            calendar: calendar
        )

        XCTAssertEqual(drafts.count, 1)
        XCTAssertNil(drafts[0].dueDate)
        XCTAssertFalse(drafts[0].canBeSaved)
    }

    func testEntriesWithoutATitleAreDropped() {
        let reply = """
        {"tasks":[{"title":"","due":"2026-08-07T17:00"},
                  {"title":"Good one","due":"2026-08-07T17:00"}]}
        """

        let drafts = ChatCompletionsAssistantService.parseDrafts(
            reply,
            sourceText: "x",
            calendar: calendar
        )

        XCTAssertEqual(drafts.map(\.title), ["Good one"])
    }

    func testSeveralTasksInOneReply() {
        let reply = """
        {"tasks":[{"title":"First","due":"2026-08-07T17:00"},
                  {"title":"Second","due":"2026-08-09T23:59"}]}
        """

        let drafts = ChatCompletionsAssistantService.parseDrafts(
            reply,
            sourceText: "two things",
            calendar: calendar
        )

        XCTAssertEqual(drafts.map(\.title), ["First", "Second"])
        XCTAssertTrue(drafts.allSatisfy(\.include))
    }

    // MARK: - Errors

    func testDisabledAssistantRefusesBeforeAnyRequest() async {
        var settings = AssistantSettings.defaults
        settings.isEnabled = false
        let service = ChatCompletionsAssistantService(
            settings: settings,
            apiKey: nil,
            session: Self.refusingSession(),
            calendar: calendar
        )

        do {
            _ = try await service.summarise(
                [AssistantAssignmentDigest(item("X", course: nil, due: date(2026, 8, 4)))],
                now: date(2026, 7, 28)
            )
            XCTFail("A disabled assistant must not reach the network")
        } catch {
            XCTAssertEqual(error as? AssistantError, .notConfigured)
        }
    }

    func testGroqWithoutAKeyRefusesBeforeAnyRequest() async {
        var settings = AssistantSettings.applying(.groq, to: .defaults)
        settings.isEnabled = true
        let service = ChatCompletionsAssistantService(
            settings: settings,
            apiKey: nil,
            session: Self.refusingSession(),
            calendar: calendar
        )

        do {
            _ = try await service.draftTasks(from: "essay", now: date(2026, 7, 28))
            XCTFail("A missing key must be caught before anything is sent")
        } catch {
            XCTAssertEqual(error as? AssistantError, .missingAPIKey)
        }
    }

    func testEmptyWorkloadIsAnsweredWithoutAskingTheModel() async throws {
        var settings = AssistantSettings.defaults
        settings.isEnabled = true
        let service = ChatCompletionsAssistantService(
            settings: settings,
            apiKey: nil,
            session: Self.refusingSession(),
            calendar: calendar
        )

        let summary = try await service.summarise([], now: date(2026, 7, 28))

        XCTAssertEqual(summary, "Nothing is coming up.")
    }

    func testEmptyInputProducesNoDraftsWithoutAskingTheModel() async throws {
        var settings = AssistantSettings.defaults
        settings.isEnabled = true
        let service = ChatCompletionsAssistantService(
            settings: settings,
            apiKey: nil,
            session: Self.refusingSession(),
            calendar: calendar
        )

        let drafts = try await service.draftTasks(from: "   ", now: date(2026, 7, 28))

        XCTAssertTrue(drafts.isEmpty)
    }

    func testErrorsCarryCodesNotContent() {
        for error in [
            AssistantError.notConfigured,
            .missingAPIKey,
            .invalidEndpoint,
            .unreachable,
            .rateLimited,
            .unauthorised,
            .badResponse,
            .cancelled,
        ] {
            XCTAssertTrue(error.diagnosticCode.hasPrefix("assistant."))
            XCTAssertFalse(
                error.diagnosticCode.contains(" "),
                "A diagnostic code carries no prose that could include a prompt"
            )
            XCTAssertNotNil(error.errorDescription)
        }
    }

    /// A session that fails any request, so a test that accidentally reaches the
    /// network fails loudly instead of going out to a real host.
    private static func refusingSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [RefusingProtocol.self]
        return URLSession(configuration: configuration)
    }
}

private final class RefusingProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        client?.urlProtocol(
            self,
            didFailWithError: URLError(.notConnectedToInternet)
        )
    }

    override func stopLoading() {}
}

import CoreGraphics
import Foundation

/// Where the assistant runs.
///
/// Both speak the same chat completions request shape, so one client serves
/// either; the difference that matters is not the protocol, it is whether
/// anything leaves this Mac.
enum AssistantProvider: String, Codable, CaseIterable, Identifiable, Sendable {
    /// A model served on this machine, such as Ollama. Nothing is uploaded.
    case local
    /// Groq's hosted models. Assignment titles, courses and due dates are sent
    /// to a third party.
    case groq

    var id: String { rawValue }

    var title: String {
        switch self {
        case .local:
            "On this Mac"
        case .groq:
            "Groq (cloud)"
        }
    }

    /// Said plainly, because this is the whole decision.
    var privacySummary: String {
        switch self {
        case .local:
            "Runs on this Mac. Nothing is uploaded."
        case .groq:
            "Assignment titles, course names and due dates are sent to Groq. Nothing else is."
        }
    }

    /// An honest note about quality, so the private option is not oversold.
    var qualityNote: String {
        switch self {
        case .local:
            "A small local model is usually less reliable at reading dates out of a sentence. Check what it produces."
        case .groq:
            "Hosted models are generally better at reading dates, at the cost of sending those three fields off this Mac."
        }
    }

    var requiresAPIKey: Bool {
        self == .groq
    }

    var defaultBaseURL: String {
        switch self {
        case .local:
            "http://localhost:11434/v1"
        case .groq:
            "https://api.groq.com/openai/v1"
        }
    }

    /// Offered as a convenience only. Any model name can still be typed, so a
    /// new release does not have to wait for an app update.
    static let groqModels = [
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "mixtral-8x7b-32768",
        "gemma2-9b-it",
    ]

    var defaultModel: String {
        switch self {
        case .local:
            "llama3.1:8b"
        case .groq:
            "llama-3.3-70b-versatile"
        }
    }
}

/// Everything about the assistant except the API key, which lives in Keychain.
struct AssistantSettings: Equatable, Codable, Sendable {
    /// On by default, but only ever local by default: a model on this Mac
    /// uploads nothing, so the feature can be available without the app quietly
    /// deciding to send anything anywhere. Choosing Groq is a separate,
    /// deliberate act.
    var isEnabled: Bool
    var provider: AssistantProvider
    var baseURL: String
    var model: String
    var presentation: AssistantPresentationPreference
    /// Last width the user left the sidebar at, if it was a usable one.
    var sidebarWidth: CGFloat?

    static let defaults = AssistantSettings(
        isEnabled: true,
        provider: .local,
        baseURL: AssistantProvider.local.defaultBaseURL,
        model: AssistantProvider.local.defaultModel,
        presentation: .sidebar,
        sidebarWidth: nil
    )

    /// Switching provider carries its own sensible endpoint and model.
    static func applying(
        _ provider: AssistantProvider,
        to settings: AssistantSettings
    ) -> AssistantSettings {
        var updated = settings
        updated.provider = provider
        updated.baseURL = provider.defaultBaseURL
        updated.model = provider.defaultModel
        return updated
    }

    var resolvedURL: URL? {
        URL(string: baseURL.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// True when the endpoint is on this machine, whatever provider is named.
    /// A user pointing "local" at a remote host is still sending data away, and
    /// the interface should not claim otherwise.
    var staysOnThisMac: Bool {
        guard let host = resolvedURL?.host?.lowercased() else {
            return false
        }
        return host == "localhost" || host == "127.0.0.1" || host == "::1"
    }
}

/// The exact payload the assistant is allowed to see.
///
/// Built explicitly rather than by serialising the stored model, so a new field
/// on an assignment can never start leaving the Mac by accident.
struct AssistantAssignmentDigest: Equatable, Sendable {
    let title: String
    let courseName: String?
    let dueDate: Date

    init(_ item: AssignmentListItem) {
        self.title = item.title
        self.courseName = item.normalizedCourseName
        self.dueDate = item.dueDate
    }

    func line(now: Date, calendar: Calendar) -> String {
        let days = calendar.dateComponents(
            [.day],
            from: calendar.startOfDay(for: now),
            to: calendar.startOfDay(for: dueDate)
        ).day ?? 0
        let course = courseName.map { " (\($0))" } ?? ""
        return "- \(title)\(course), due in \(days) days"
    }
}

enum AssistantError: LocalizedError, Equatable, Sendable {
    case notConfigured
    case missingAPIKey
    case invalidEndpoint
    case unreachable
    case rateLimited
    case unauthorised
    case badResponse
    case cancelled

    var errorDescription: String? {
        switch self {
        case .notConfigured:
            "Turn on the assistant in Settings first."
        case .missingAPIKey:
            "Add your Groq API key in Settings, or switch to a model on this Mac."
        case .invalidEndpoint:
            "That assistant address is not a valid URL."
        case .unreachable:
            "The assistant could not be reached. If it runs on this Mac, check that it is running."
        case .rateLimited:
            "The assistant is rate limited. Wait a moment and try again."
        case .unauthorised:
            "The assistant rejected the API key."
        case .badResponse:
            "The assistant returned something this app could not read."
        case .cancelled:
            "The request was cancelled."
        }
    }

    /// Safe for diagnostics: a code only, never a prompt or a reply.
    var diagnosticCode: String {
        switch self {
        case .notConfigured: "assistant.not-configured"
        case .missingAPIKey: "assistant.missing-key"
        case .invalidEndpoint: "assistant.invalid-endpoint"
        case .unreachable: "assistant.unreachable"
        case .rateLimited: "assistant.rate-limited"
        case .unauthorised: "assistant.unauthorised"
        case .badResponse: "assistant.bad-response"
        case .cancelled: "assistant.cancelled"
        }
    }
}

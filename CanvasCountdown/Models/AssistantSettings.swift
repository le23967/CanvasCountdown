import CoreGraphics
import Foundation

/// Where the assistant runs.
///
/// Not which company: every hosted service worth using speaks the same chat
/// completions request shape, so one client serves all of them and the address,
/// the model name and the API key are the whole configuration. The difference
/// that matters is not the brand, it is whether anything leaves this Mac.
enum AssistantProvider: String, Codable, CaseIterable, Identifiable, Sendable {
    /// A model served on this machine, such as Ollama. Nothing is uploaded.
    case local
    /// Any hosted service. Assignment titles, courses and due dates are sent
    /// to whichever one the user pointed the address at.
    case cloud

    var id: String { rawValue }

    /// Older preferences named the single supported service rather than the
    /// kind of service. Anything unrecognised reads as local, which is the only
    /// safe direction to guess in: it cannot start uploading by accident.
    init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        switch raw {
        case "groq":
            self = .cloud
        default:
            self = AssistantProvider(rawValue: raw) ?? .local
        }
    }

    var title: String {
        switch self {
        case .local:
            "On this Mac"
        case .cloud:
            "AI (Cloud)"
        }
    }

    /// Said plainly, because this is the whole decision.
    var privacySummary: String {
        switch self {
        case .local:
            "Runs on this Mac. Nothing is uploaded."
        case .cloud:
            "Assignment titles, course names and due dates are sent to the service you choose. Nothing else is."
        }
    }

    /// An honest note about quality, so the private option is not oversold.
    var qualityNote: String {
        switch self {
        case .local:
            "A small local model is usually less reliable at reading dates out of a sentence. Check what it produces."
        case .cloud:
            "Hosted models are generally better at reading dates, at the cost of sending those three fields off this Mac."
        }
    }

    var requiresAPIKey: Bool {
        self == .cloud
    }

    var defaultBaseURL: String {
        switch self {
        case .local:
            "http://localhost:11434/v1"
        case .cloud:
            AssistantService.openAI.baseURL
        }
    }

    var defaultModel: String {
        switch self {
        case .local:
            "llama3.1:8b"
        case .cloud:
            AssistantService.openAI.models[0]
        }
    }
}

/// A service known to speak the OpenAI chat completions shape, offered as a
/// starting point.
///
/// Only ever a convenience: the address and the model stay plain text fields,
/// so a service that ships a new model tomorrow works today, and one missing
/// from this list works by typing its address. Nothing here is a restriction.
struct AssistantService: Identifiable, Equatable, Sendable {
    let name: String
    let baseURL: String
    /// A couple of current model names, cheapest-first where that is knowable.
    let models: [String]
    let note: String
    /// Where to get a key, for the services that need one.
    let keysURL: String?

    var id: String { name }

    static let openAI = AssistantService(
        name: "OpenAI",
        baseURL: "https://api.openai.com/v1",
        models: ["gpt-4o-mini", "gpt-4o"],
        note: "ChatGPT's models. Needs an OpenAI API key.",
        keysURL: "https://platform.openai.com/api-keys"
    )

    static let anthropic = AssistantService(
        name: "Claude",
        baseURL: "https://api.anthropic.com/v1",
        models: ["claude-haiku-4-5", "claude-sonnet-5"],
        note: "Anthropic's models, through their OpenAI-compatible endpoint.",
        keysURL: "https://console.anthropic.com/settings/keys"
    )

    static let groq = AssistantService(
        name: "Groq",
        baseURL: "https://api.groq.com/openai/v1",
        models: ["llama-3.3-70b-versatile", "openai/gpt-oss-120b"],
        note: "Very fast, and free at low volume.",
        keysURL: "https://console.groq.com/keys"
    )

    static let deepSeek = AssistantService(
        name: "DeepSeek",
        baseURL: "https://api.deepseek.com/v1",
        models: ["deepseek-chat"],
        note: "Inexpensive, and good at structured replies.",
        keysURL: "https://platform.deepseek.com/api_keys"
    )

    static let qwen = AssistantService(
        name: "Qwen",
        baseURL: "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
        models: ["qwen-plus", "qwen-turbo"],
        note: "Alibaba's models, through DashScope.",
        keysURL: "https://dashscope.console.aliyun.com"
    )

    static let openRouter = AssistantService(
        name: "OpenRouter",
        baseURL: "https://openrouter.ai/api/v1",
        models: ["openai/gpt-4o-mini", "anthropic/claude-haiku-4.5"],
        note: "One key for models from many providers.",
        keysURL: "https://openrouter.ai/keys"
    )

    static let ollama = AssistantService(
        name: "Ollama (on this Mac)",
        baseURL: "http://localhost:11434/v1",
        models: ["llama3.1:8b", "qwen2.5:7b"],
        note: "Runs locally. No key, and nothing is uploaded.",
        keysURL: nil
    )

    /// The hosted services, in the order the picker offers them.
    static let cloudServices: [AssistantService] = [
        .openAI, .anthropic, .groq, .deepSeek, .qwen, .openRouter,
    ]

    static let allServices: [AssistantService] = cloudServices + [.ollama]

    /// The service a given address belongs to, if it is one of the known ones.
    /// Used only to label a saved profile, never to restrict what can be typed.
    static func matching(baseURL: String) -> AssistantService? {
        let trimmed = baseURL
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        return allServices.first {
            trimmed.hasPrefix($0.baseURL.lowercased())
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
    /// Which saved profile these values came from, so the API key can be found
    /// and the switcher knows what is selected. Nil for a configuration typed
    /// in by hand that has not been saved under a name.
    var activeProfileID: UUID?

    static let defaults = AssistantSettings(
        isEnabled: true,
        provider: .local,
        baseURL: AssistantProvider.local.defaultBaseURL,
        model: AssistantProvider.local.defaultModel,
        presentation: .sidebar,
        sidebarWidth: nil,
        activeProfileID: nil
    )

    init(
        isEnabled: Bool,
        provider: AssistantProvider,
        baseURL: String,
        model: String,
        presentation: AssistantPresentationPreference,
        sidebarWidth: CGFloat?,
        activeProfileID: UUID? = nil
    ) {
        self.isEnabled = isEnabled
        self.provider = provider
        self.baseURL = baseURL
        self.model = model
        self.presentation = presentation
        self.sidebarWidth = sidebarWidth
        self.activeProfileID = activeProfileID
    }

    /// Switching to a saved profile carries its endpoint, its model and, through
    /// its id, its own API key.
    static func applying(
        _ profile: AssistantProfile,
        to settings: AssistantSettings
    ) -> AssistantSettings {
        var updated = settings
        updated.provider = profile.provider
        updated.baseURL = profile.baseURL
        updated.model = profile.model
        updated.activeProfileID = profile.id
        return updated
    }

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
        AssistantEndpoint.isLocal(baseURL)
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

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// Where the assistant runs.
/// </summary>
/// <remarks>
/// Not which company: every hosted service worth using speaks the same chat
/// completions request shape, so one client serves all of them and the address,
/// the model name and the API key are the whole configuration. The difference
/// that matters is not the brand, it is whether anything leaves this PC.
///
/// A stored value is always read by <see cref="AssistantProviderJsonConverter"/>,
/// which every property of this type carries. It cannot be declared on the enum
/// itself: the app-wide string-enum converter in <see cref="JsonDefaults"/>
/// outranks a converter declared on a type, and it would refuse an old value
/// outright — or, ignoring case, read "Cloud" as cloud — where the stored value
/// must read as local.
/// </remarks>
public enum AssistantProvider
{
    /// <summary>A model served on this machine. Nothing is uploaded.</summary>
    Local,

    /// <summary>
    /// Any hosted service. Assignment titles, courses and due dates are sent
    /// to whichever one the user pointed the address at.
    /// </summary>
    Cloud,
}

public static class AssistantProviderExtensions
{
    /// <summary>The stored spelling, which is also the identity a picker uses.</summary>
    public static string RawValue(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.Local => "local",
        AssistantProvider.Cloud => "cloud",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string Title(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.Local => "On this PC",
        AssistantProvider.Cloud => "AI (Cloud)",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Said plainly, because this is the whole decision.</summary>
    public static string PrivacySummary(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.Local => "Runs on this PC. Nothing is uploaded.",
        AssistantProvider.Cloud => "Assignment titles, course names and due dates are sent to the service you choose. Nothing else is.",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>An honest note about quality, so the private option is not oversold.</summary>
    public static string QualityNote(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.Local => "A small local model is usually less reliable at reading dates out of a sentence. Check what it produces.",
        AssistantProvider.Cloud => "Hosted models are generally better at reading dates, at the cost of sending those three fields off this PC.",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static bool RequiresApiKey(this AssistantProvider provider) => provider == AssistantProvider.Cloud;

    public static string DefaultBaseUrl(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.Local => "http://localhost:11434/v1",
        AssistantProvider.Cloud => AssistantService.OpenAI.BaseUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string DefaultModel(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.Local => "llama3.1:8b",
        AssistantProvider.Cloud => AssistantService.OpenAI.Models[0],
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}

/// <summary>
/// Reads a stored <see cref="AssistantProvider"/> the way the macOS decoder does.
/// </summary>
/// <remarks>
/// Older preferences named the single supported service rather than the kind
/// of service. Anything unrecognised reads as local, which is the only safe
/// direction to guess in: it cannot start uploading by accident. The match is
/// exact, as Swift's raw values are, so a stray capital letter is unrecognised
/// too, and reads as local. Something that is not text at all is refused, as
/// the macOS decoder refuses it, so the settings it belongs to fall back to
/// their defaults — which are local.
/// </remarks>
public sealed class AssistantProviderJsonConverter : JsonConverter<AssistantProvider>
{
    public override AssistantProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("An assistant provider is stored as text.");
        }
        return FromStored(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, AssistantProvider value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.RawValue());

    public static AssistantProvider FromStored(string raw) => raw switch
    {
        "groq" => AssistantProvider.Cloud,
        "cloud" => AssistantProvider.Cloud,
        "local" => AssistantProvider.Local,
        _ => AssistantProvider.Local,
    };
}

/// <summary>
/// A service known to speak the common chat completions shape, offered as a
/// starting point.
/// </summary>
/// <remarks>
/// Only ever a convenience: the address and the model stay plain text fields,
/// so a service that ships a new model tomorrow works today, and one missing
/// from this list works by typing its address. Nothing here is a restriction.
/// The entries are product data and match the macOS catalog.
/// </remarks>
public sealed record AssistantService
{
    public required string Name { get; init; }

    public required string BaseUrl { get; init; }

    /// <summary>A couple of current model names, cheapest-first where that is knowable.</summary>
    public required ValueList<string> Models { get; init; }

    public required string Note { get; init; }

    /// <summary>Where to get a key, for the services that need one.</summary>
    public required string? KeysUrl { get; init; }

    public string Id => Name;

    public static AssistantService OpenAI { get; } = new()
    {
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Models = ["gpt-4o-mini", "gpt-4o"],
        Note = "ChatGPT's models. Needs an OpenAI API key.",
        KeysUrl = "https://platform.openai.com/api-keys",
    };

    public static AssistantService Anthropic { get; } = new()
    {
        Name = "Claude",
        BaseUrl = "https://api.anthropic.com/v1",
        Models = ["claude-haiku-4-5", "claude-sonnet-5"],
        Note = "Anthropic's models, through their OpenAI-compatible endpoint.",
        KeysUrl = "https://console.anthropic.com/settings/keys",
    };

    public static AssistantService Groq { get; } = new()
    {
        Name = "Groq",
        BaseUrl = "https://api.groq.com/openai/v1",
        Models = ["llama-3.3-70b-versatile", "openai/gpt-oss-120b"],
        Note = "Very fast, and free at low volume.",
        KeysUrl = "https://console.groq.com/keys",
    };

    public static AssistantService DeepSeek { get; } = new()
    {
        Name = "DeepSeek",
        BaseUrl = "https://api.deepseek.com/v1",
        Models = ["deepseek-chat"],
        Note = "Inexpensive, and good at structured replies.",
        KeysUrl = "https://platform.deepseek.com/api_keys",
    };

    public static AssistantService Qwen { get; } = new()
    {
        Name = "Qwen",
        BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
        Models = ["qwen-plus", "qwen-turbo"],
        Note = "Alibaba's models, through DashScope.",
        KeysUrl = "https://dashscope.console.aliyun.com",
    };

    public static AssistantService OpenRouter { get; } = new()
    {
        Name = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Models = ["openai/gpt-4o-mini", "anthropic/claude-haiku-4.5"],
        Note = "One key for models from many providers.",
        KeysUrl = "https://openrouter.ai/keys",
    };

    public static AssistantService Ollama { get; } = new()
    {
        Name = "Ollama (on this PC)",
        BaseUrl = "http://localhost:11434/v1",
        Models = ["llama3.1:8b", "qwen2.5:7b"],
        Note = "Runs locally. No key, and nothing is uploaded.",
        KeysUrl = null,
    };

    /// <summary>The hosted services, in the order the picker offers them.</summary>
    public static ValueList<AssistantService> CloudServices { get; } =
        [OpenAI, Anthropic, Groq, DeepSeek, Qwen, OpenRouter];

    public static ValueList<AssistantService> AllServices { get; } = CloudServices.Add(Ollama);

    /// <summary>
    /// The service a given address belongs to, if it is one of the known ones.
    /// Used only to label a saved profile, never to restrict what can be typed.
    /// </summary>
    public static AssistantService? Matching(string baseUrl)
    {
        var trimmed = baseUrl.Trim().ToLowerInvariant();
        return AllServices.FirstOrDefault(
            service => trimmed.StartsWith(service.BaseUrl.ToLowerInvariant(), StringComparison.Ordinal));
    }
}

/// <summary>
/// Everything about the assistant except the API key, which lives in
/// Credential Manager through <see cref="AssistantKeyStore"/>.
/// </summary>
/// <remarks>
/// Stored as one value under <see cref="PreferencesKey"/>. A missing
/// <see cref="SidebarWidth"/> or <see cref="ActiveProfileId"/> reads as none;
/// any other missing field makes the whole value unreadable, as it does on
/// macOS, so the caller falls back to <see cref="Defaults"/> — which upload
/// nothing.
/// </remarks>
public sealed record AssistantSettings
{
    /// <summary>The preferences key the macOS settings store uses for this value.</summary>
    public const string PreferencesKey = "CanvasCountdown.settings.assistant";

    /// <summary>
    /// On by default, but only ever local by default: a model on this PC
    /// uploads nothing, so the feature can be available without the app quietly
    /// deciding to send anything anywhere. Choosing a hosted service is a
    /// separate, deliberate act.
    /// </summary>
    public required bool IsEnabled { get; init; }

    [JsonConverter(typeof(AssistantProviderJsonConverter))]
    public required AssistantProvider Provider { get; init; }

    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    [JsonConverter(typeof(AssistantPresentationPreferenceJsonConverter))]
    public required AssistantPresentationPreference Presentation { get; init; }

    /// <summary>Last width the user left the sidebar at, if it was a usable one.</summary>
    public double? SidebarWidth { get; init; }

    /// <summary>
    /// Which saved profile these values came from, so the API key can be found
    /// and the switcher knows what is selected. Null for a configuration typed
    /// in by hand that has not been saved under a name.
    /// </summary>
    public Guid? ActiveProfileId { get; init; }

    public static AssistantSettings Defaults { get; } = new()
    {
        IsEnabled = true,
        Provider = AssistantProvider.Local,
        BaseUrl = AssistantProvider.Local.DefaultBaseUrl(),
        Model = AssistantProvider.Local.DefaultModel(),
        Presentation = AssistantPresentationPreference.Sidebar,
        SidebarWidth = null,
        ActiveProfileId = null,
    };

    /// <summary>
    /// Switching to a saved profile carries its endpoint, its model and, through
    /// its id, its own API key.
    /// </summary>
    public static AssistantSettings Applying(AssistantProfile profile, AssistantSettings settings) => settings with
    {
        Provider = profile.Provider,
        BaseUrl = profile.BaseUrl,
        Model = profile.Model,
        ActiveProfileId = profile.Id,
    };

    /// <summary>Switching provider carries its own sensible endpoint and model.</summary>
    public static AssistantSettings Applying(AssistantProvider provider, AssistantSettings settings) => settings with
    {
        Provider = provider,
        BaseUrl = provider.DefaultBaseUrl(),
        Model = provider.DefaultModel(),
    };

    /// <summary>
    /// Points the live configuration at one of the known services. Whether it
    /// is local is read off the service's address; a service with no suggested
    /// model leaves the model as it was.
    /// </summary>
    public AssistantSettings UsingService(AssistantService service) => this with
    {
        Provider = AssistantEndpoint.IsLocal(service.BaseUrl) ? AssistantProvider.Local : AssistantProvider.Cloud,
        BaseUrl = service.BaseUrl,
        Model = service.Models.IsEmpty ? Model : service.Models[0],
    };

    /// <summary>
    /// The width the user dragged the sidebar to, kept only when usable, so a
    /// bad drag cannot leave an unopenable panel or replace a good width.
    /// </summary>
    public AssistantSettings RememberingSidebarWidth(double width)
        => AssistantLayout.IsValidSidebarWidth(width) ? this with { SidebarWidth = width } : this;

    /// <summary>
    /// The address as something to send a request to, or null when it is empty
    /// or cannot be read as an address at all. An address with no scheme or
    /// host still resolves, as it does on macOS; the request to it then fails
    /// as unreachable.
    /// </summary>
    [JsonIgnore]
    public Uri? ResolvedUrl => AssistantEndpoint.Resolve(BaseUrl);

    /// <summary>
    /// True when the endpoint is on this machine, whatever provider is named.
    /// A user pointing "local" at a remote host is still sending data away, and
    /// the interface should not claim otherwise.
    /// </summary>
    [JsonIgnore]
    public bool StaysOnThisMachine => AssistantEndpoint.IsLocal(BaseUrl);

    /// <summary>
    /// The label beside the assistant's name. Read off the endpoint and nothing
    /// else, so pointing a "local" configuration at a remote host cannot
    /// produce a false claim of privacy: a remote endpoint is labelled the same
    /// whichever provider the settings name.
    /// </summary>
    [JsonIgnore]
    public string PrivacyLabel => StaysOnThisMachine
        ? "On this PC"
        : "Cloud · " + AssistantProvider.Cloud.Title();

    /// <summary>
    /// The sentence Settings shows where the choice is made, read off the
    /// address rather than the provider for the same reason as
    /// <see cref="PrivacyLabel"/>.
    /// </summary>
    [JsonIgnore]
    public string PrivacySummary => StaysOnThisMachine
        ? AssistantProvider.Local.PrivacySummary()
        : AssistantProvider.Cloud.PrivacySummary();

    /// <summary>
    /// Where to get a key for the service the address belongs to, when the
    /// provider needs one and the service is a known one.
    /// </summary>
    [JsonIgnore]
    public string? KeysUrl => Provider.RequiresApiKey() ? AssistantService.Matching(BaseUrl)?.KeysUrl : null;
}

/// <summary>
/// The exact payload the assistant is allowed to see.
/// </summary>
/// <remarks>
/// Built explicitly rather than by serialising the stored model, so a new field
/// on an assignment can never start leaving this PC by accident.
/// </remarks>
public sealed record AssistantAssignmentDigest
{
    public AssistantAssignmentDigest(AssignmentListItem item)
    {
        Title = item.Title;
        CourseName = item.NormalizedCourseName;
        DueDate = item.DueDate;
    }

    public string Title { get; }

    public string? CourseName { get; }

    public DateTimeOffset DueDate { get; }

    public string Line(DateTimeOffset now, AppCalendar calendar)
    {
        var days = calendar.DaysBetween(now, DueDate);
        var course = CourseName is null ? string.Empty : $" ({CourseName})";
        return string.Create(CultureInfo.InvariantCulture, $"- {Title}{course}, due in {days} days");
    }

    /// <summary>
    /// Only the three agreed fields, and only for what is currently in scope:
    /// the upcoming events on screen that are not completed, ignored or past.
    /// </summary>
    /// <remarks>
    /// With an assignment in context the list narrows to that one item: asking
    /// about a single deadline is not a reason to send the whole term. When the
    /// assignment in context is no longer in scope, the whole scope is sent, as
    /// it is on macOS.
    /// </remarks>
    public static ValueList<AssistantAssignmentDigest> ForScope(
        IEnumerable<AssignmentListItem> visibleUpcoming,
        AssistantContext? context,
        DateTimeOffset now)
    {
        var scoped = visibleUpcoming
            .Where(item => !item.IsCompleted && !item.IsIgnored && item.DueDate >= now)
            .ToList();
        if (context is not null && scoped.FirstOrDefault(item => item.Id == context.Id) is { } focused)
        {
            return [new AssistantAssignmentDigest(focused)];
        }
        return scoped.Select(item => new AssistantAssignmentDigest(item)).ToValueList();
    }
}

public enum AssistantError
{
    NotConfigured,
    MissingApiKey,
    InvalidEndpoint,
    Unreachable,
    RateLimited,
    Unauthorised,
    BadResponse,
    Cancelled,
}

public static class AssistantErrorExtensions
{
    public static string Description(this AssistantError error) => error switch
    {
        AssistantError.NotConfigured => "Turn on the assistant in Settings first.",
        AssistantError.MissingApiKey => "Add your Groq API key in Settings, or switch to a model on this PC.",
        AssistantError.InvalidEndpoint => "That assistant address is not a valid URL.",
        AssistantError.Unreachable => "The assistant could not be reached. If it runs on this PC, check that it is running.",
        AssistantError.RateLimited => "The assistant is rate limited. Wait a moment and try again.",
        AssistantError.Unauthorised => "The assistant rejected the API key.",
        AssistantError.BadResponse => "The assistant returned something this app could not read.",
        AssistantError.Cancelled => "The request was cancelled.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    /// <summary>Safe for diagnostics: a code only, never a prompt or a reply.</summary>
    public static string DiagnosticCode(this AssistantError error) => error switch
    {
        AssistantError.NotConfigured => "assistant.not-configured",
        AssistantError.MissingApiKey => "assistant.missing-key",
        AssistantError.InvalidEndpoint => "assistant.invalid-endpoint",
        AssistantError.Unreachable => "assistant.unreachable",
        AssistantError.RateLimited => "assistant.rate-limited",
        AssistantError.Unauthorised => "assistant.unauthorised",
        AssistantError.BadResponse => "assistant.bad-response",
        AssistantError.Cancelled => "assistant.cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}

/// <summary>
/// A failed request to the assistant. The message is written for people and
/// never carries a prompt, a reply or a key; <see cref="DiagnosticCode"/> is
/// safe to put in exported diagnostics.
/// </summary>
public sealed class AssistantException : Exception
{
    public AssistantException(AssistantError error)
        : base(error.Description())
    {
        Error = error;
    }

    public AssistantError Error { get; }

    public string DiagnosticCode => Error.DiagnosticCode();
}

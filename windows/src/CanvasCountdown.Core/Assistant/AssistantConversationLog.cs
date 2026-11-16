using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// Everything asked of the assistant, kept between launches.
/// </summary>
/// <remarks>
/// A conversation that only lived while the panel was open meant there was no
/// way to tell whether something had already been asked, or to read an answer
/// again the next morning. It is the user's own record, so it is stored beside
/// their labels and models rather than with the settings that a reset puts
/// back, and a reset leaves it alone.
///
/// Versioned and self-repairing for the same reason the labels are: a log
/// written by a newer build reads as empty rather than being rewritten over.
/// It holds what was said and when, and nothing else — never a key.
/// </remarks>
[JsonConverter(typeof(AssistantConversationLogJsonConverter))]
public sealed record AssistantConversationLog
{
    /// <summary>The preferences key the macOS settings store uses for the log.</summary>
    public const string PreferencesKey = "CanvasCountdown.settings.assistantConversation";

    public const int CurrentVersion = 1;

    /// <summary>
    /// Enough to look back over weeks of asking, bounded so an install that
    /// runs for a year does not grow this without end. The oldest go first,
    /// which is also the order in which they stop being worth keeping.
    /// </summary>
    public const int MaximumMessages = 200;

    /// <summary>
    /// How much of it the panel shows before offering the rest.
    /// </summary>
    /// <remarks>
    /// Kept between launches, this can run to weeks, and the newest exchange is
    /// the one being read. Showing all of it every time would push the question
    /// field — and the drafting section under it — below a wall of scrollback.
    /// </remarks>
    public const int RecentConversationLimit = 6;

    public AssistantConversationLog(IEnumerable<AssistantMessage> messages, int version = CurrentVersion)
    {
        Version = version;
        Messages = Usable(messages);
    }

    public static AssistantConversationLog Empty { get; } = new([]);

    public int Version { get; init; }

    public ValueList<AssistantMessage> Messages { get; private init; }

    public bool IsEmpty => Messages.IsEmpty;

    /// <summary>The newest exchanges, newest last, so the last thing on screen is the last thing said.</summary>
    public ValueList<AssistantMessage> RecentMessages => Messages.Count <= RecentConversationLimit
        ? Messages
        : Messages.Skip(Messages.Count - RecentConversationLimit).ToValueList();

    /// <summary>How many are hidden behind "Show earlier".</summary>
    public int EarlierMessageCount => Math.Max(0, Messages.Count - RecentConversationLimit);

    public AssistantConversationLog Append(AssistantMessage message) => this with { Messages = Usable(Messages.Add(message)) };

    public AssistantConversationLog RemoveAll() => this with { Messages = [] };

    /// <summary>
    /// The date to put above each message, or null where it belongs to the same
    /// day as the one before it.
    /// </summary>
    /// <remarks>
    /// Kept between launches, so this is a record rather than a scrollback: it
    /// says which day each exchange happened on. Read against what is on screen
    /// rather than the whole record, so the first message shown always says
    /// which day it belongs to.
    /// </remarks>
    public static ValueList<string?> DayHeadings(
        IReadOnlyList<AssistantMessage> visible,
        DateTimeOffset now,
        AppCalendar calendar,
        CultureInfo culture)
    {
        var headings = new List<string?>(visible.Count);
        var today = calendar.DateOf(now);
        for (var index = 0; index < visible.Count; index++)
        {
            var date = visible[index].Date;
            if (index > 0 && calendar.IsSameDay(visible[index - 1].Date, date))
            {
                headings.Add(null);
                continue;
            }
            var day = calendar.DateOf(date);
            if (day == today)
            {
                headings.Add("Today");
            }
            else if (day == today.AddDays(-1))
            {
                headings.Add("Yesterday");
            }
            else
            {
                headings.Add(AssistantText.WeekdayDayAndMonth(calendar.ToLocal(date), culture));
            }
        }
        return headings.ToValueList();
    }

    /// <summary>An empty turn says nothing and cannot be read back, so it is not kept.</summary>
    private static ValueList<AssistantMessage> Usable(IEnumerable<AssistantMessage> messages)
    {
        var kept = messages.Where(message => message.Text.Trim().Length > 0).ToList();
        if (kept.Count <= MaximumMessages)
        {
            return kept.ToValueList();
        }
        return kept.Skip(kept.Count - MaximumMessages).ToValueList();
    }
}

/// <summary>
/// Reads and writes <see cref="AssistantConversationLog"/> the way the macOS
/// decoder does: a log from a newer build reads as empty without its messages
/// being looked at, a missing version reads as current, missing messages as
/// none, and anything else malformed is refused.
/// </summary>
public sealed class AssistantConversationLogJsonConverter : JsonConverter<AssistantConversationLog>
{
    public override AssistantConversationLog Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = AssistantJson.RequireObject(document.RootElement);
        var version = AssistantJson.ReadVersion(root, AssistantConversationLog.CurrentVersion);
        if (version > AssistantConversationLog.CurrentVersion)
        {
            return new AssistantConversationLog([], version);
        }
        return new AssistantConversationLog(
            AssistantJson.ReadOptionalList<AssistantMessage>(root, "messages", options),
            version);
    }

    public override void Write(Utf8JsonWriter writer, AssistantConversationLog value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        writer.WritePropertyName("messages");
        JsonSerializer.Serialize(writer, value.Messages, options);
        writer.WriteEndObject();
    }
}

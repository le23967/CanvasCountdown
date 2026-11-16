using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CanvasCountdown;

/// <summary>
/// Reading and writing JSON the way the macOS assistant does: forgiving where
/// <c>JSONSerialization</c> is (a trailing comma, the first of two keys with
/// the same name), strict everywhere else.
/// </summary>
internal static class AssistantJson
{
    /// <summary>
    /// Text the model or a local server will read goes out as written, not as
    /// <c>\u</c> escapes, as it does from macOS.
    /// </summary>
    internal static JsonWriterOptions WriterOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = true,
    };

    internal static JsonElement RequireObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? element
            : throw new JsonException("Expected an object.");

    /// <summary>
    /// A stored version: absent or null reads as <paramref name="current"/>;
    /// a whole number, even one written as <c>1.0</c>, is read; anything else
    /// is refused, as Swift's decoder refuses it.
    /// </summary>
    internal static int ReadVersion(JsonElement root, int current)
    {
        if (!TryGetFirst(root, "version", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return current;
        }
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt32(out var whole))
            {
                return whole;
            }
            if (element.TryGetDouble(out var number)
                && Math.Floor(number) == number
                && number >= int.MinValue
                && number <= int.MaxValue)
            {
                return (int)number;
            }
        }
        throw new JsonException("The version is not a whole number.");
    }

    /// <summary>A list that may be absent or null, which reads as empty; anything present must be a readable list.</summary>
    internal static List<T> ReadOptionalList<T>(JsonElement root, string name, JsonSerializerOptions options)
    {
        if (!TryGetFirst(root, name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        return element.Deserialize<List<T>>(options) ?? [];
    }

    /// <summary>The first property with this exact name, as <c>JSONSerialization</c> keeps the first of two.</summary>
    internal static bool TryGetFirst(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Swift's <c>as? [[String: Any]]</c>: an array whose every element is an
    /// object. One stray value makes the whole array unreadable.
    /// </summary>
    internal static bool IsArrayOfObjects(JsonElement element)
        => element.ValueKind == JsonValueKind.Array
            && element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object);

    /// <summary>The property's text, or null when it is missing or not text.</summary>
    internal static string? StringValue(JsonElement element, string name)
        => TryGetFirst(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The property as Swift's <c>as? NSNumber</c> reads it: any number, and
    /// true or false as one or zero.
    /// </summary>
    internal static double? NumberValue(JsonElement element, string name)
    {
        if (!TryGetFirst(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    /// <summary>The same, as <c>int64Value</c> reads it: a fraction is dropped.</summary>
    internal static long? WholeNumberValue(JsonElement element, string name)
    {
        if (TryGetFirst(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var whole))
        {
            return whole;
        }
        return NumberValue(element, name) is { } number
            ? (long)Math.Clamp(Math.Truncate(number), long.MinValue, long.MaxValue)
            : null;
    }

    /// <summary>A JSON body, labelled <c>application/json</c> with no charset, as macOS sends it.</summary>
    internal static ByteArrayContent Content(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}

/// <summary>
/// Text measured and written the way the macOS app does it.
/// </summary>
internal static class AssistantText
{
    /// <summary>
    /// The characters a person sees — Swift's <c>Character</c>s — rather than
    /// UTF-16 units, so "é" typed as two code points is one character.
    /// </summary>
    internal static List<string> Characters(string text)
    {
        var characters = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            characters.Add(enumerator.GetTextElement());
        }
        return characters;
    }

    internal static int CharacterCount(string text) => new StringInfo(text).LengthInTextElements;

    internal static string CharacterPrefix(string text, int count)
    {
        var info = new StringInfo(text);
        return count >= info.LengthInTextElements ? text : info.SubstringByTextElements(0, count);
    }

    /// <summary>
    /// The first scalar of a character, which is what Swift asks when it says
    /// whether a character is a letter or a number. Broken text reads as
    /// neither.
    /// </summary>
    internal static Rune FirstRune(string character)
        => Rune.DecodeFromUtf16(character, out var rune, out _) == System.Buffers.OperationStatus.Done
            ? rune
            : Rune.ReplacementChar;

    /// <summary>A UUID as Foundation writes it: upper-case, hyphenated.</summary>
    internal static string UuidString(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>
    /// Foundation's <c>UUID(uuidString:)</c>: exactly the hyphenated form, in
    /// either case, with nothing around it.
    /// </summary>
    internal static Guid? ParseUuid(string text)
        => text.Length == 36 && Guid.TryParseExact(text, "D", out var id) ? id : null;

    /// <summary>
    /// The day and the abbreviated month, in the order and punctuation the
    /// culture uses for a day of the month: "Aug 8", "8 Aug", "8. Aug.".
    /// </summary>
    internal static string DayAndMonth(DateTime local, CultureInfo culture)
        => local.ToString(
            culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal),
            culture);

    /// <summary>The same, after the abbreviated weekday: "Sat, Aug 8".</summary>
    internal static string WeekdayDayAndMonth(DateTime local, CultureInfo culture)
        => local.ToString("ddd", culture) + ", " + DayAndMonth(local, culture);
}

/// <summary>The transport for the assistant and the local model server.</summary>
internal static class AssistantHttp
{
    /// <summary>
    /// Shared, because a client per request leaves sockets behind. It keeps no
    /// cookies, as the macOS session keeps none, and has no timeout of its own:
    /// each request sets the one it is owed.
    /// </summary>
    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(
        new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    internal static HttpClient ClientFor(HttpMessageHandler? handler)
        => handler is null
            ? Shared.Value
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}

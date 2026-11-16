using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// The one set of JSON rules everything the app writes to disk follows.
/// </summary>
/// <remarks>
/// Property names are camelCase and enums are written as camelCase names, so a
/// stored value reads the same as the Swift raw values it was ported from
/// (<c>"canvasCalendarFeed"</c>, <c>"everySixHours"</c>). Dates are ISO 8601.
/// A type whose decoding has to be forgiving — an unknown provider must read
/// as local, a newer file must read as empty rather than be overwritten —
/// carries its own converter; this default is strict on purpose.
/// </remarks>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Decodes, answering null for anything that is not a readable <typeparamref name="T"/>.</summary>
    public static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }
}

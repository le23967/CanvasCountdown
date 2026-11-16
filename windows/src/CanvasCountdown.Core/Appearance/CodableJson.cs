using System.Text.Json;

namespace CanvasCountdown;

/// <summary>
/// Reading and writing JSON the way Swift's <c>Codable</c> does, for the
/// settings this module stores.
/// </summary>
/// <remarks>
/// The default serializer quietly fills a missing property with its default
/// and reads <c>null</c> into a string that must not be null. Swift's decoder
/// refuses both, and the stores rely on that refusal: an unreadable value
/// falls back to a sensible default instead of half-loading. Every failure
/// here is a <see cref="JsonException"/>, which is what
/// <see cref="IPreferencesStore.TryGet{T}"/> treats as "not readable".
/// </remarks>
internal static class CodableJson
{
    public static JsonElement Object(JsonElement element, string what)
        => element.ValueKind == JsonValueKind.Object
            ? element
            : throw new JsonException($"Expected {what} to be an object.");

    public static string RequiredString(JsonElement container, string key)
        => Required(container, key) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw Mismatch(key, "a string");

    public static int RequiredInt(JsonElement container, string key)
        => Required(container, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : throw Mismatch(key, "a whole number");

    /// <summary>Swift's <c>decodeIfPresent(Int.self)</c>: absent or null reads as null.</summary>
    public static long? OptionalLong(JsonElement container, string key)
    {
        if (!Present(container, key, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw Mismatch(key, "a whole number");
    }

    public static bool RequiredBool(JsonElement container, string key)
        => Required(container, key).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Mismatch(key, "true or false"),
        };

    public static Guid RequiredUuid(JsonElement container, string key)
        => ParseUuid(RequiredString(container, key), key);

    public static Guid? OptionalUuid(JsonElement container, string key)
    {
        if (!Present(container, key, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? ParseUuid(value.GetString()!, key)
            : throw Mismatch(key, "a UUID string");
    }

    public static JsonElement RequiredObject(JsonElement container, string key)
        => Required(container, key) is { ValueKind: JsonValueKind.Object } value
            ? value
            : throw Mismatch(key, "an object");

    public static JsonElement RequiredArray(JsonElement container, string key)
        => Required(container, key) is { ValueKind: JsonValueKind.Array } value
            ? value
            : throw Mismatch(key, "an array");

    /// <summary>Swift's <c>decodeIfPresent([T].self)</c>: absent or null reads as null.</summary>
    public static JsonElement? OptionalArray(JsonElement container, string key)
    {
        if (!Present(container, key, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Array ? value : throw Mismatch(key, "an array");
    }

    /// <summary>
    /// A Swift string-backed enum: the raw value must match a case exactly.
    /// Raw values are the camelCase case names, as Swift spells them.
    /// </summary>
    public static TEnum RequiredEnum<TEnum>(JsonElement container, string key)
        where TEnum : struct, Enum
    {
        var raw = RequiredString(container, key);
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(RawValue(candidate), raw, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        throw new JsonException($"\"{raw}\" is not a known value for \"{key}\".");
    }

    public static string RawValue<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>Upper case, as Swift's <c>UUID.uuidString</c> writes it.</summary>
    public static string UuidString(Guid id) => id.ToString("D").ToUpperInvariant();

    private static JsonElement Required(JsonElement container, string key)
    {
        if (!container.TryGetProperty(key, out var value))
        {
            throw new JsonException($"\"{key}\" is missing.");
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            throw new JsonException($"\"{key}\" is null.");
        }
        return value;
    }

    private static bool Present(JsonElement container, string key, out JsonElement value)
        => container.TryGetProperty(key, out value) && value.ValueKind != JsonValueKind.Null;

    private static Guid ParseUuid(string text, string key)
        => Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw new JsonException($"\"{key}\" is not a UUID.");

    private static JsonException Mismatch(string key, string expected)
        => new($"\"{key}\" is not {expected}.");
}

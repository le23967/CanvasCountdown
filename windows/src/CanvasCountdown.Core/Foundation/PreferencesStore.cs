using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CanvasCountdown;

/// <summary>
/// Non-sensitive preferences, by key: the Windows stand-in for
/// <c>UserDefaults</c>. Secrets never go here; they go to
/// <see cref="ISecretStore"/>.
/// </summary>
public interface IPreferencesStore
{
    bool Contains(string key);

    /// <summary>
    /// The stored value. False when nothing was ever written under
    /// <paramref name="key"/>, and also when what was written cannot be read as
    /// a <typeparamref name="T"/> — a value from a newer build, or a damaged
    /// file — so callers fall back to their default instead of failing.
    /// </summary>
    bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value);

    void Set<T>(string key, T value);

    void Remove(string key);
}

public static class PreferencesStoreExtensions
{
    /// <summary>The stored value, or <paramref name="fallback"/> when absent or unreadable.</summary>
    public static T Get<T>(this IPreferencesStore store, string key, T fallback)
        => store.TryGet<T>(key, out var value) ? value : fallback;

    /// <summary>
    /// The stored value or null. Distinguishes "never written" from a stored
    /// <c>false</c> or <c>0</c>, which a plain getter cannot.
    /// </summary>
    public static T? GetOptional<T>(this IPreferencesStore store, string key)
        where T : struct
        => store.TryGet<T>(key, out var value) ? value : null;
}

/// <summary>
/// Preferences kept as one JSON object, in a file or (with no path) in memory.
/// </summary>
/// <remarks>
/// Every write goes to a temporary file that then replaces the real one, so a
/// crash or a full disk mid-write leaves the previous preferences intact
/// rather than half a file. A file that cannot be parsed is moved aside, not
/// overwritten: it may hold the user's labels and saved themes, and a later
/// build may be able to read what this one cannot.
/// </remarks>
public sealed class JsonPreferencesStore : IPreferencesStore
{
    private readonly object gate = new();
    private readonly string? filePath;
    private readonly JsonObject values;

    /// <param name="filePath">Where to keep the preferences, or null for memory only.</param>
    public JsonPreferencesStore(string? filePath = null)
    {
        this.filePath = filePath;
        values = filePath is null ? new JsonObject() : Load(filePath);
    }

    public static JsonPreferencesStore InMemory() => new();

    public bool Contains(string key)
    {
        lock (gate)
        {
            return values.ContainsKey(key);
        }
    }

    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        JsonNode? node;
        lock (gate)
        {
            if (!values.TryGetPropertyValue(key, out node))
            {
                value = default;
                return false;
            }
            node = node?.DeepClone();
        }

        try
        {
            var decoded = node is null ? default : node.Deserialize<T>(JsonDefaults.Options);
            if (decoded is null)
            {
                value = default;
                return false;
            }
            value = decoded;
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
        catch (NotSupportedException)
        {
            value = default;
            return false;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (gate)
        {
            values[key] = value is null ? null : JsonSerializer.SerializeToNode(value, JsonDefaults.Options);
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (gate)
        {
            if (values.Remove(key))
            {
                Save();
            }
        }
    }

    private void Save()
    {
        if (filePath is null)
        {
            return;
        }
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = filePath + ".writing";
        File.WriteAllText(temporaryPath, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(filePath))
        {
            File.Replace(temporaryPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, filePath);
        }
    }

    private static JsonObject Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new JsonObject();
        }
        try
        {
            var text = File.ReadAllText(filePath);
            if (JsonNode.Parse(text) is JsonObject parsed)
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
            // Unreadable right now (locked, say). Starting empty in memory is
            // safe only because the next write would overwrite it, so set the
            // file aside first.
        }

        try
        {
            var asidePath = $"{filePath}.unreadable-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(filePath, asidePath);
        }
        catch (IOException)
        {
        }
        return new JsonObject();
    }
}

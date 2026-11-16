using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// A named assistant configuration the user kept.
/// </summary>
/// <remarks>
/// The app used to hold exactly two: one local, one hosted, each overwritten
/// whenever the other was chosen. That made "try the fast one for a quick
/// question, use the careful one for drafting" mean retyping an address, a
/// model name and a key every time. A profile is that configuration with a name
/// on it, so switching is one click.
///
/// The API key is deliberately absent. It lives in Credential Manager under
/// this profile's id (<see cref="AssistantKeyStore"/>), so it is never written
/// to preferences or diagnostics.
/// </remarks>
public sealed record AssistantProfile
{
    public AssistantProfile()
    {
    }

    /// <summary>A profile pointed at one of the known services, ready to name.</summary>
    [SetsRequiredMembers]
    public AssistantProfile(AssistantService service, string? name = null)
    {
        Name = name ?? service.Name;
        Provider = service.BaseUrl.StartsWith("http://localhost", StringComparison.Ordinal)
            ? AssistantProvider.Local
            : AssistantProvider.Cloud;
        BaseUrl = service.BaseUrl;
        Model = service.Models.IsEmpty ? string.Empty : service.Models[0];
    }

    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    [JsonConverter(typeof(AssistantProviderJsonConverter))]
    public required AssistantProvider Provider { get; init; }

    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    /// <summary>
    /// A model to fill in by hand, for a service that is not on the list.
    /// Starts local, because that is the setting that cannot upload anything
    /// while it is half-configured.
    /// </summary>
    public static AssistantProfile Blank() => new()
    {
        Name = "New Model",
        Provider = AssistantProvider.Local,
        BaseUrl = AssistantProvider.Local.DefaultBaseUrl(),
        Model = AssistantProvider.Local.DefaultModel(),
    };

    /// <summary>
    /// True when this profile's endpoint is on this machine, whatever it is
    /// named. Reading the address rather than the label is what stops the
    /// interface claiming privacy the configuration does not provide.
    /// </summary>
    [JsonIgnore]
    public bool StaysOnThisMachine => AssistantEndpoint.IsLocal(BaseUrl);

    /// <summary>
    /// What the switcher shows beside the name: the model, which is the thing
    /// being switched between.
    /// </summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var trimmed = Model.Trim();
            return trimmed.Length == 0 ? "No model set" : trimmed;
        }
    }

    /// <summary>The tooltip on the row's privacy mark, read off the address.</summary>
    [JsonIgnore]
    public string PrivacyDescription => StaysOnThisMachine
        ? "Runs on this PC"
        : "Sends titles, courses and dates to this service";

    /// <summary>What a screen reader says for the row's privacy mark, read off the address.</summary>
    [JsonIgnore]
    public string PrivacyAccessibilityLabel => StaysOnThisMachine ? "Runs on this PC" : "Cloud model";
}

/// <summary>Whether an address is served from this machine.</summary>
public static class AssistantEndpoint
{
    /// <remarks>
    /// Decided by the host the address names and nothing else. Loopback
    /// spellings that .NET writes in their canonical form (127.1, a long-hand
    /// ::1) count as this machine here, which is true of them; they are read
    /// as remote on macOS, which only ever errs towards "cloud".
    /// </remarks>
    public static bool IsLocal(string baseUrl)
    {
        if (Resolve(baseUrl) is not { IsAbsoluteUri: true } url)
        {
            return false;
        }
        var host = url.DnsSafeHost.ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1";
    }

    /// <summary>
    /// The trimmed address as a URI: absolute when it names a scheme and a
    /// host, relative when it is just a word, null when it is empty or is not
    /// an address at all.
    /// </summary>
    internal static Uri? Resolve(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        return Uri.TryCreate(trimmed, UriKind.RelativeOrAbsolute, out var url) ? url : null;
    }

    /// <summary>True when the address names a host, which is what makes a saved profile selectable.</summary>
    internal static bool HasHost(string baseUrl)
        => Resolve(baseUrl) is { IsAbsoluteUri: true } url && url.Host.Length > 0;

    /// <summary>
    /// Where a request to <paramref name="component"/> under the address goes.
    /// Refused unless that is an absolute http or https address, the only kind a
    /// request can be sent to, so a word or a file path fails the same way
    /// whatever transport is in use — as unreachable, as it does on macOS.
    /// </summary>
    internal static Uri RequestUri(Uri baseUrl, string component)
    {
        var url = AppendingPathComponent(baseUrl, component);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException("The assistant address is not one a request can be sent to.");
        }
        return url;
    }

    /// <summary>
    /// Foundation's <c>appendingPathComponent</c>: the component goes after the
    /// path, with one slash between them, and the query and fragment stay.
    /// </summary>
    internal static Uri AppendingPathComponent(Uri url, string component)
    {
        if (!url.IsAbsoluteUri)
        {
            var original = url.OriginalString;
            var joined = original.EndsWith('/') ? original + component : original + "/" + component;
            return new Uri(joined, UriKind.Relative);
        }
        var path = url.AbsolutePath;
        var newPath = path.EndsWith('/') ? path + component : path + "/" + component;
        return Rebuilt(url, newPath);
    }

    /// <summary>
    /// Foundation's <c>lastPathComponent</c>, percent-decoded, ignoring trailing
    /// slashes. Empty for a URI with no path.
    /// </summary>
    internal static string LastPathComponent(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return string.Empty;
        }
        var trimmed = url.AbsolutePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return Uri.UnescapeDataString(slash < 0 ? trimmed : trimmed[(slash + 1)..]);
    }

    /// <summary>
    /// Foundation's <c>deleteLastPathComponent</c>: the last component and any
    /// trailing slashes go, leaving the path ending in a slash.
    /// </summary>
    internal static Uri DeletingLastPathComponent(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return url;
        }
        var trimmed = url.AbsolutePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var newPath = slash < 0 ? "/" : trimmed[..(slash + 1)];
        return Rebuilt(url, newPath);
    }

    private static Uri Rebuilt(Uri url, string path)
        => new(url.GetLeftPart(UriPartial.Authority) + path + url.Query + url.Fragment, UriKind.Absolute);
}

/// <summary>Why a profile name was refused, in the words the row shows.</summary>
public enum AssistantProfileNameError
{
    Empty,
    TooLong,
    Duplicate,
}

public static class AssistantProfileNameErrorExtensions
{
    public static string Message(this AssistantProfileNameError error) => error switch
    {
        AssistantProfileNameError.Empty => "Give the model a name.",
        AssistantProfileNameError.TooLong => string.Create(
            CultureInfo.InvariantCulture,
            $"Names can be up to {AssistantProfileLibrary.MaximumNameLength} characters."),
        AssistantProfileNameError.Duplicate => "There is already a model with that name.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}

/// <summary>
/// The saved profiles, and every rule about changing them.
/// </summary>
/// <remarks>
/// Versioned for the same reason the countdown presets and the labels are:
/// this outlives any one build, and a library written by a newer version is
/// left alone rather than half-read and rewritten.
///
/// Each change returns the new library; where the macOS method also answered
/// something (the new id, a naming error), that comes back through an out
/// parameter.
/// </remarks>
[JsonConverter(typeof(AssistantProfileLibraryJsonConverter))]
public sealed record AssistantProfileLibrary
{
    /// <summary>The preferences key the macOS settings store uses for the library.</summary>
    public const string PreferencesKey = "CanvasCountdown.settings.assistantProfiles";

    public const int CurrentVersion = 1;

    public const int MaximumNameLength = 40;

    public const int MaximumCount = 12;

    public AssistantProfileLibrary(ValueList<AssistantProfile> profiles, int version = CurrentVersion)
    {
        Version = version;
        Profiles = profiles;
    }

    public static AssistantProfileLibrary Empty { get; } = new([]);

    public int Version { get; init; }

    public ValueList<AssistantProfile> Profiles { get; private init; }

    public bool IsFull => Profiles.Count >= MaximumCount;

    public AssistantProfile? Profile(Guid? id)
        => id is null ? null : Profiles.FirstOrDefault(profile => profile.Id == id);

    /// <summary>
    /// A profile with no name or no address cannot be selected, so it is not
    /// kept. The model may be blank: a local server can be asked for one later.
    /// </summary>
    internal static bool IsUsable(AssistantProfile profile)
        => profile.Name.Trim().Length > 0 && AssistantEndpoint.HasHost(profile.BaseUrl);

    // Naming

    /// <remarks>
    /// Length is counted in characters as a person sees them, so an accented
    /// letter or an emoji counts once, as it does on macOS. Names compare
    /// ignoring case and the way an accent was typed.
    /// </remarks>
    public AssistantProfileNameError? Validate(string name, Guid? excluding = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return AssistantProfileNameError.Empty;
        }
        if (AssistantText.CharacterCount(trimmed) > MaximumNameLength)
        {
            return AssistantProfileNameError.TooLong;
        }
        var clash = Profiles.Any(profile =>
            profile.Id != excluding
            && string.Equals(profile.Name, trimmed, StringComparison.InvariantCultureIgnoreCase));
        return clash ? AssistantProfileNameError.Duplicate : null;
    }

    /// <summary>
    /// A taken name gains a number — "Campus" becomes "Campus 2", then
    /// "Campus 3" — so adding the same service twice does not fail on the name.
    /// </summary>
    public string AvailableName(string startingFrom)
    {
        var trimmed = startingFrom.Trim();
        var seed = trimmed.Length == 0 ? "Model" : trimmed;
        if (Validate(seed) is null)
        {
            return seed;
        }
        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{seed} {suffix}");
            if (Validate(candidate) is null)
            {
                return candidate;
            }
        }
        return Guid.NewGuid().ToString("D").ToUpperInvariant();
    }

    // Changing the library

    /// <summary>Adds a profile under a free name. <paramref name="id"/> is the new profile's id, so the caller can select what it just added, or null when it was not kept.</summary>
    public AssistantProfileLibrary Add(AssistantProfile profile, out Guid? id)
    {
        if (IsFull || !IsUsable(profile))
        {
            id = null;
            return this;
        }
        var stored = profile with { Name = AvailableName(profile.Name) };
        id = stored.Id;
        return this with { Profiles = Profiles.Add(stored) };
    }

    public AssistantProfileLibrary Add(AssistantProfile profile) => Add(profile, out _);

    /// <summary>Renames a profile; a refused name changes nothing and comes back as <paramref name="error"/>.</summary>
    public AssistantProfileLibrary Rename(Guid id, string name, out AssistantProfileNameError? error)
    {
        error = Validate(name, excluding: id);
        if (error is not null)
        {
            return this;
        }
        var index = Profiles.FindIndex(profile => profile.Id == id);
        if (index < 0)
        {
            return this;
        }
        return this with { Profiles = Profiles.SetItem(index, Profiles[index] with { Name = name.Trim() }) };
    }

    /// <summary>Writes the live address and model back over a saved profile.</summary>
    public AssistantProfileLibrary Update(
        Guid id,
        AssistantProvider provider,
        string baseUrl,
        string model,
        out bool updated)
    {
        var index = Profiles.FindIndex(profile => profile.Id == id);
        if (index < 0)
        {
            updated = false;
            return this;
        }
        updated = true;
        var changed = Profiles[index] with { Provider = provider, BaseUrl = baseUrl, Model = model };
        return this with { Profiles = Profiles.SetItem(index, changed) };
    }

    public AssistantProfileLibrary Duplicate(Guid id, out Guid? copyId)
    {
        if (Profile(id) is not { } existing)
        {
            copyId = null;
            return this;
        }
        return Add(
            new AssistantProfile
            {
                Name = AvailableName($"{existing.Name} Copy"),
                Provider = existing.Provider,
                BaseUrl = existing.BaseUrl,
                Model = existing.Model,
            },
            out copyId);
    }

    public AssistantProfileLibrary Remove(Guid id) => this with { Profiles = Profiles.RemoveAll(profile => profile.Id == id) };

    /// <summary>
    /// The starting set, built from whatever the assistant was already
    /// configured with.
    /// </summary>
    /// <remarks>
    /// An existing install has one working configuration and, if it is a hosted
    /// one, a key under the old single-key account. Making that configuration
    /// the first profile is what carries both across without the user noticing
    /// anything happened.
    /// </remarks>
    public static AssistantProfileLibrary Migrated(AssistantSettings settings)
    {
        var name = AssistantService.Matching(settings.BaseUrl)?.Name ?? settings.Provider.Title();
        return new AssistantProfileLibrary(
        [
            new AssistantProfile
            {
                Name = name,
                Provider = settings.Provider,
                BaseUrl = settings.BaseUrl,
                Model = settings.Model,
            },
        ]);
    }

    /// <summary>
    /// Builds the starting library from whatever the assistant was already set
    /// to, selects it, and carries the existing API key onto it. Null when
    /// there is already a library, so it runs once, on the first launch after
    /// saved models arrived.
    /// </summary>
    /// <remarks>
    /// Anyone upgrading finds their configuration already named and working
    /// rather than an empty list and an assistant that stopped answering. Only
    /// a hosted configuration had a key to carry. A key that cannot be moved is
    /// left where it was rather than stopping the upgrade.
    /// </remarks>
    public static async Task<(AssistantProfileLibrary Library, AssistantSettings Settings)?> MigrateIfNeededAsync(
        AssistantProfileLibrary library,
        AssistantSettings settings,
        AssistantKeyStore keys)
    {
        if (!library.Profiles.IsEmpty)
        {
            return null;
        }
        var migrated = Migrated(settings);
        if (migrated.Profiles.IsEmpty)
        {
            return null;
        }
        var first = migrated.Profiles[0];
        if (first.Provider.RequiresApiKey())
        {
            try
            {
                await keys.AdoptLegacyKeyAsync(first.Id).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // As on macOS: the move is a convenience, and the key it could
                // not carry is still under the old account.
            }
        }
        return (migrated, settings with { ActiveProfileId = first.Id });
    }

    /// <summary>
    /// Editing the address or the model edits the model it belongs to.
    /// </summary>
    /// <remarks>
    /// There is no separate save step: a saved model that quietly disagreed
    /// with the fields above it would be worse than no saving at all. Answers
    /// this same library when nothing differs.
    /// </remarks>
    public AssistantProfileLibrary Syncing(AssistantSettings settings)
    {
        if (settings.ActiveProfileId is not { } id || Profile(id) is not { } existing)
        {
            return this;
        }
        if (existing.Provider == settings.Provider
            && existing.BaseUrl == settings.BaseUrl
            && existing.Model == settings.Model)
        {
            return this;
        }
        return Update(id, settings.Provider, settings.BaseUrl, settings.Model, out _);
    }

    /// <summary>
    /// Switches the assistant to a saved model: its address, its model name and,
    /// through its id, its own API key. Null when there is no such model or it
    /// is already the one in use.
    /// </summary>
    public AssistantSettings? Selecting(Guid id, AssistantSettings settings)
    {
        if (Profile(id) is not { } profile || profile.Id == settings.ActiveProfileId)
        {
            return null;
        }
        return AssistantSettings.Applying(profile, settings);
    }

    /// <summary>
    /// Deleting a model takes its API key with it, and moves to another so the
    /// assistant is never left pointing at nothing.
    /// </summary>
    /// <remarks>
    /// The key goes whether or not the model was the one in use. A key that
    /// cannot be removed does not stop the deletion, as on macOS.
    /// </remarks>
    public async Task<(AssistantProfileLibrary Library, AssistantSettings Settings)> DeletingAsync(
        Guid id,
        AssistantSettings settings,
        AssistantKeyStore keys)
    {
        var library = Remove(id);
        var updated = settings;
        if (settings.ActiveProfileId == id)
        {
            updated = library.Profiles.IsEmpty
                ? settings with { ActiveProfileId = null }
                : AssistantSettings.Applying(library.Profiles[0], settings);
        }
        try
        {
            await keys.DeleteAsync(id).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The model is gone either way; an orphaned key names no model and
            // is never read again.
        }
        return (library, updated);
    }

    /// <summary>
    /// Whether the composer is worth a model chip. One model is just the
    /// model; the control only earns its place once there is something to
    /// switch to, and never while the assistant is off.
    /// </summary>
    public bool ShowsModelPicker(AssistantSettings settings) => settings.IsEnabled && Profiles.Count > 1;

    /// <summary>What the model chip says to a screen reader and in its tooltip.</summary>
    public string ModelDescription(Guid? activeProfileId)
        => Profile(activeProfileId) is { } active
            ? $"AI model, using {active.Name}"
            : "Choose which AI model to use";

    /// <summary>The model chip's own label.</summary>
    public string ModelChipTitle(Guid? activeProfileId) => Profile(activeProfileId)?.Name ?? "Model";
}

/// <summary>
/// Reads and writes <see cref="AssistantProfileLibrary"/> the way the macOS
/// decoder does.
/// </summary>
/// <remarks>
/// A library from a newer build reads as empty, keeping its version, and its
/// profiles are not even looked at; rewriting what cannot be read would
/// destroy it. A missing version reads as the current one, missing profiles
/// as none, and unusable profiles are dropped. Anything else malformed is
/// refused, so the caller falls back to an empty library.
/// </remarks>
public sealed class AssistantProfileLibraryJsonConverter : JsonConverter<AssistantProfileLibrary>
{
    public override AssistantProfileLibrary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = AssistantJson.RequireObject(document.RootElement);
        var version = AssistantJson.ReadVersion(root, AssistantProfileLibrary.CurrentVersion);
        if (version > AssistantProfileLibrary.CurrentVersion)
        {
            return new AssistantProfileLibrary([], version);
        }
        var profiles = AssistantJson.ReadOptionalList<AssistantProfile>(root, "profiles", options);
        return new AssistantProfileLibrary(profiles.Where(AssistantProfileLibrary.IsUsable).ToValueList(), version);
    }

    public override void Write(Utf8JsonWriter writer, AssistantProfileLibrary value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        writer.WritePropertyName("profiles");
        JsonSerializer.Serialize(writer, value.Profiles, options);
        writer.WriteEndObject();
    }
}

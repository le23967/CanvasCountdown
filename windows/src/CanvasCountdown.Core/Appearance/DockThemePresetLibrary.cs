using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>A colour scheme the user named and kept.</summary>
/// <remarks>
/// Only the colours are saved. Size and weight stay with the countdown
/// settings rather than the theme, because those are about legibility where
/// the countdown is shown, not about how the tile is meant to look.
/// </remarks>
[JsonConverter(typeof(UserDockThemePresetJsonConverter))]
public sealed record UserDockThemePreset
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public required DockColours Colours { get; init; }
}

/// <summary>Why a name was refused.</summary>
public enum DockPresetNameError
{
    Empty,
    TooLong,
    Duplicate,
}

public static class DockPresetNameErrorExtensions
{
    /// <summary>The words the naming sheet shows.</summary>
    public static string Message(this DockPresetNameError error) => error switch
    {
        DockPresetNameError.Empty => "Give the preset a name.",
        DockPresetNameError.TooLong => $"Names can be up to {DockThemePresetLibrary.MaximumNameLength} characters.",
        DockPresetNameError.Duplicate => "There is already a theme with that name.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}

/// <summary>
/// The answer to a proposed name: the name to store, trimmed, or why it was
/// refused. The Mac's <c>Result&lt;String, DockPresetNameError&gt;</c>.
/// </summary>
public sealed record DockPresetNameValidation
{
    private DockPresetNameValidation(string? name, DockPresetNameError? error)
    {
        Name = name;
        Error = error;
    }

    /// <summary>The trimmed name, when it was accepted.</summary>
    public string? Name { get; }

    public DockPresetNameError? Error { get; }

    public bool IsSuccess => Error is null;

    public static DockPresetNameValidation Success(string name) => new(name, null);

    public static DockPresetNameValidation Failure(DockPresetNameError error) => new(null, error);
}

/// <summary>The saved presets, and every rule about changing them.</summary>
/// <remarks>
/// Versioned because these outlive any one build: a library written by a later
/// version is left alone rather than half-read, and a preset that cannot be
/// used is dropped instead of taking the rest of the settings down with it.
/// </remarks>
[JsonConverter(typeof(DockThemePresetLibraryJsonConverter))]
public sealed record DockThemePresetLibrary
{
    public const int CurrentVersion = 1;

    public const int MaximumNameLength = 40;

    public DockThemePresetLibrary(ValueList<UserDockThemePreset> presets, int version = CurrentVersion)
    {
        Version = version;
        Presets = presets;
    }

    public static DockThemePresetLibrary Empty { get; } = new(ValueList<UserDockThemePreset>.Empty);

    public int Version { get; init; }

    public ValueList<UserDockThemePreset> Presets { get; private init; }

    public UserDockThemePreset? Preset(Guid id)
    {
        foreach (var preset in Presets)
        {
            if (preset.Id == id)
            {
                return preset;
            }
        }
        return null;
    }

    /// <summary>
    /// Trims the name and checks it against both the built-in themes and the
    /// saved ones, ignoring case, so the picker can never show two identical
    /// rows that mean different things.
    /// </summary>
    public static DockPresetNameValidation Validate(string name, DockThemePresetLibrary library, Guid? excluding = null)
    {
        var trimmed = FoundationText.TrimmingWhitespaceAndNewlines(name);
        if (trimmed.Length == 0)
        {
            return DockPresetNameValidation.Failure(DockPresetNameError.Empty);
        }
        if (FoundationText.CharacterCount(trimmed) > MaximumNameLength)
        {
            return DockPresetNameValidation.Failure(DockPresetNameError.TooLong);
        }
        var taken = DockThemePresets.Selectable.Select(preset => preset.Title())
            .Append(DockThemePreset.Custom.Title())
            .Concat(library.Presets.Where(preset => preset.Id != excluding).Select(preset => preset.Name));
        if (taken.Any(existing => FoundationText.CaseInsensitiveEquals(existing, trimmed)))
        {
            return DockPresetNameValidation.Failure(DockPresetNameError.Duplicate);
        }
        return DockPresetNameValidation.Success(trimmed);
    }

    /// <summary>
    /// "Green Gradient" becomes "Green Gradient Copy", then "Green Gradient
    /// Copy 2", so duplicating twice does not fail on the name.
    /// </summary>
    public string AvailableCopyName(UserDockThemePreset preset)
    {
        var copy = $"{preset.Name} Copy";
        if (Validate(copy, this) is { IsSuccess: true, Name: { } name })
        {
            return name;
        }
        for (var suffix = 2; suffix <= 99; suffix++)
        {
            if (Validate($"{copy} {suffix}", this) is { IsSuccess: true, Name: { } numbered })
            {
                return numbered;
            }
        }
        return CodableJson.UuidString(Guid.NewGuid());
    }

    public DockThemePresetLibrary Add(string name, DockColours colours) => Add(name, colours, out _);

    /// <summary>
    /// Adds a preset under a validated name. <paramref name="id"/> is the new
    /// preset's id, so the caller can select what it just saved, or null when
    /// the name was refused and nothing changed.
    /// </summary>
    public DockThemePresetLibrary Add(string name, DockColours colours, out Guid? id)
    {
        if (Validate(name, this) is not { IsSuccess: true, Name: { } validated })
        {
            id = null;
            return this;
        }
        var preset = new UserDockThemePreset { Name = validated, Colours = colours };
        id = preset.Id;
        return this with { Presets = Presets.Add(preset) };
    }

    public DockThemePresetLibrary Rename(Guid id, string name) => Rename(id, name, out _);

    public DockThemePresetLibrary Rename(Guid id, string name, out bool renamed)
    {
        var index = Presets.FindIndex(preset => preset.Id == id);
        if (Validate(name, this, excluding: id) is not { IsSuccess: true, Name: { } validated } || index < 0)
        {
            renamed = false;
            return this;
        }
        renamed = true;
        return this with { Presets = Presets.SetItem(index, Presets[index] with { Name = validated }) };
    }

    public DockThemePresetLibrary Update(Guid id, DockColours colours) => Update(id, colours, out _);

    public DockThemePresetLibrary Update(Guid id, DockColours colours, out bool updated)
    {
        var index = Presets.FindIndex(preset => preset.Id == id);
        if (index < 0)
        {
            updated = false;
            return this;
        }
        updated = true;
        return this with { Presets = Presets.SetItem(index, Presets[index] with { Colours = colours }) };
    }

    public DockThemePresetLibrary Duplicate(Guid id) => Duplicate(id, out _);

    public DockThemePresetLibrary Duplicate(Guid id, out Guid? copyId)
    {
        if (Preset(id) is not { } existing)
        {
            copyId = null;
            return this;
        }
        return Add(AvailableCopyName(existing), existing.Colours, out copyId);
    }

    public DockThemePresetLibrary Remove(Guid id) => this with { Presets = Presets.RemoveAll(preset => preset.Id == id) };

    /// <summary>
    /// Works out what the current colours amount to: a built-in theme, a saved
    /// preset, a saved preset with edits, or something not yet named.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, so the label cannot drift away from the
    /// colours actually in force.
    /// </remarks>
    public DockThemeStatus Status(DockAppearance appearance)
    {
        if (appearance.UserPresetId is { } id && Preset(id) is { } preset)
        {
            return preset.Colours == appearance.Colours
                ? new DockThemeStatus.Saved(preset)
                : new DockThemeStatus.Modified(preset);
        }
        if (appearance.Preset.Colours() is { } colours && colours == appearance.Colours)
        {
            return new DockThemeStatus.BuiltIn(appearance.Preset);
        }
        return new DockThemeStatus.Unsaved();
    }

    /// <summary>A preset with an unreadable colour cannot be applied, so it is not kept.</summary>
    internal static bool IsUsable(UserDockThemePreset preset)
    {
        if (FoundationText.TrimmingWhitespaceAndNewlines(preset.Name).Length == 0)
        {
            return false;
        }
        var colours = preset.Colours;
        return ColorHex.IsValid(colours.BackgroundTop)
            && ColorHex.IsValid(colours.BackgroundBottom)
            && ColorHex.IsValid(colours.Number)
            && ColorHex.IsValid(colours.Label);
    }
}

/// <summary>What the theme row is showing, which is not always a preset.</summary>
public abstract record DockThemeStatus
{
    private DockThemeStatus()
    {
    }

    public sealed record BuiltIn(DockThemePreset Preset) : DockThemeStatus;

    public sealed record Saved(UserDockThemePreset Preset) : DockThemeStatus;

    /// <summary>A saved preset with unsaved edits on top of it.</summary>
    public sealed record Modified(UserDockThemePreset Preset) : DockThemeStatus;

    /// <summary>Colours that match nothing, and have never been named.</summary>
    public sealed record Unsaved : DockThemeStatus;

    public string Title => this switch
    {
        BuiltIn builtIn => builtIn.Preset.Title(),
        Saved saved => saved.Preset.Name,
        Modified modified => $"{modified.Preset.Name} — Modified",
        _ => "Custom — Unsaved",
    };

    /// <summary>Whether the sheet and the update/revert buttons have anything to act on.</summary>
    public UserDockThemePreset? EditedPreset => this is Modified modified ? modified.Preset : null;

    public bool IsSavable => this is Modified or Unsaved;
}

/// <summary>A row in the theme picker.</summary>
/// <remarks>
/// The picker offers built-ins and saved presets together, so its selection has
/// to be able to name either. <see cref="Unsaved"/> is the tag for the row that
/// only exists to show the current state; picking it does nothing.
/// </remarks>
public abstract record DockThemeChoice
{
    private DockThemeChoice()
    {
    }

    public sealed record BuiltIn(DockThemePreset Preset) : DockThemeChoice;

    public sealed record Saved(Guid Id) : DockThemeChoice;

    public sealed record Unsaved : DockThemeChoice;

    /// <summary>Edits still belong to the preset they came from.</summary>
    public static DockThemeChoice From(DockThemeStatus status) => status switch
    {
        DockThemeStatus.BuiltIn builtIn => new BuiltIn(builtIn.Preset),
        DockThemeStatus.Saved saved => new Saved(saved.Preset.Id),
        DockThemeStatus.Modified modified => new Saved(modified.Preset.Id),
        _ => new Unsaved(),
    };
}

internal sealed class UserDockThemePresetJsonConverter : JsonConverter<UserDockThemePreset>
{
    public override UserDockThemePreset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Decode(JsonElement.ParseValue(ref reader));

    public override void Write(Utf8JsonWriter writer, UserDockThemePreset value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", CodableJson.UuidString(value.Id));
        writer.WriteString("name", value.Name);
        writer.WritePropertyName("colours");
        JsonSerializer.Serialize(writer, value.Colours, options);
        writer.WriteEndObject();
    }

    internal static UserDockThemePreset Decode(JsonElement element)
    {
        var preset = CodableJson.Object(element, "a preset");
        return new UserDockThemePreset
        {
            Id = CodableJson.RequiredUuid(preset, "id"),
            Name = CodableJson.RequiredString(preset, "name"),
            Colours = DockColoursJsonConverter.Decode(CodableJson.RequiredObject(preset, "colours")),
        };
    }
}

/// <summary>
/// The Mac's custom decoder: a missing version reads as the current one; a
/// version from a later build reads as an empty library without looking at
/// the presets; otherwise every preset must decode, and the unusable ones are
/// dropped.
/// </summary>
internal sealed class DockThemePresetLibraryJsonConverter : JsonConverter<DockThemePresetLibrary>
{
    public override DockThemePresetLibrary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var element = CodableJson.Object(JsonElement.ParseValue(ref reader), "the preset library");
        var version = ClampedVersion(CodableJson.OptionalLong(element, "version") ?? DockThemePresetLibrary.CurrentVersion);
        if (version > DockThemePresetLibrary.CurrentVersion)
        {
            // Written by a newer build. Showing none is honest; rewriting what
            // we cannot read would destroy it.
            return new DockThemePresetLibrary(ValueList<UserDockThemePreset>.Empty, version);
        }
        var presets = new List<UserDockThemePreset>();
        if (CodableJson.OptionalArray(element, "presets") is { } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                presets.Add(UserDockThemePresetJsonConverter.Decode(item));
            }
        }
        return new DockThemePresetLibrary(presets.Where(DockThemePresetLibrary.IsUsable).ToValueList(), version);
    }

    public override void Write(Utf8JsonWriter writer, DockThemePresetLibrary value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        writer.WritePropertyName("presets");
        JsonSerializer.Serialize(writer, value.Presets, options);
        writer.WriteEndObject();
    }

    internal static int ClampedVersion(long version) => (int)Math.Clamp(version, int.MinValue, int.MaxValue);
}

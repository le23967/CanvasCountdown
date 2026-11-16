using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>A coloured mark the user puts on an event.</summary>
/// <remarks>
/// Deliberately one concept rather than two. "How urgent is this" and "what
/// kind of thing is this" are the same operation on the same field — a name and
/// a colour — and keeping them apart would mean two managers, two filters and
/// two colours competing for the same row. A label is whatever the user says it
/// is: Important, Personal, Society, Reading.
/// </remarks>
[JsonConverter(typeof(EventLabelJsonConverter))]
public sealed record EventLabel
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    /// <summary>
    /// <c>"#RRGGBB"</c>. Stored as text so a label survives a colour space
    /// change and can be read in preferences.
    /// </summary>
    public required string ColorHex { get; init; }

    public SrgbColor Color => SrgbColor.FromHex(ColorHex) ?? SrgbColor.SystemGray;
}

/// <summary>Why a label name was refused.</summary>
public enum EventLabelNameError
{
    Empty,
    TooLong,
    Duplicate,
}

public static class EventLabelNameErrorExtensions
{
    /// <summary>The words the label sheet shows.</summary>
    public static string Message(this EventLabelNameError error) => error switch
    {
        EventLabelNameError.Empty => "Give the label a name.",
        EventLabelNameError.TooLong => $"Names can be up to {EventLabelLibrary.MaximumNameLength} characters.",
        EventLabelNameError.Duplicate => "There is already a label with that name.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}

/// <summary>A label name refused while adding or renaming.</summary>
public sealed class EventLabelNameException : Exception
{
    public EventLabelNameException(EventLabelNameError error)
        : base(error.Message())
    {
        Error = error;
    }

    public EventLabelNameError Error { get; }
}

/// <summary>The labels themselves, and every rule about changing them.</summary>
/// <remarks>
/// Versioned and self-repairing for the same reason the countdown theme
/// presets are: this outlives any one build, and a library it cannot read is
/// dropped rather than taking the rest of the settings with it.
/// </remarks>
[JsonConverter(typeof(EventLabelLibraryJsonConverter))]
public sealed record EventLabelLibrary
{
    public const int CurrentVersion = 1;

    public const int MaximumNameLength = 24;

    public const int MaximumCount = 24;

    public EventLabelLibrary(ValueList<EventLabel> labels, int version = CurrentVersion)
    {
        Version = version;
        Labels = labels;
    }

    /// <summary>
    /// A starting set covering both of the things labels are for, so the
    /// feature is usable before anyone opens Settings. All of them can be
    /// renamed, recoloured or deleted.
    /// </summary>
    public static EventLabelLibrary Defaults { get; } = new(
    [
        new EventLabel { Id = BuiltIn.Important, Name = "Important", ColorHex = "#E5484D" },
        new EventLabel { Id = BuiltIn.InProgress, Name = "In Progress", ColorHex = "#F5A623" },
        new EventLabel { Id = BuiltIn.Personal, Name = "Personal", ColorHex = "#30A46C" },
        new EventLabel { Id = BuiltIn.Activity, Name = "Activity", ColorHex = "#3E63DD" },
    ]);

    public static EventLabelLibrary Empty { get; } = new(ValueList<EventLabel>.Empty);

    /// <summary>
    /// Written down rather than generated, because an event stores the id of
    /// its label. A fresh id on every launch would point every labelled event
    /// at a label that no longer exists, and the colours would quietly vanish
    /// on the next start.
    /// </summary>
    public static class BuiltIn
    {
        public static Guid Important { get; } = new("4C1D0E9A-0000-4000-A000-000000000001");

        public static Guid InProgress { get; } = new("4C1D0E9A-0000-4000-A000-000000000002");

        public static Guid Personal { get; } = new("4C1D0E9A-0000-4000-A000-000000000003");

        public static Guid Activity { get; } = new("4C1D0E9A-0000-4000-A000-000000000004");
    }

    public int Version { get; init; }

    public ValueList<EventLabel> Labels { get; private init; }

    public EventLabel? Label(Guid? id)
    {
        if (id is not { } wanted)
        {
            return null;
        }
        foreach (var label in Labels)
        {
            if (label.Id == wanted)
            {
                return label;
            }
        }
        return null;
    }

    public bool IsFull => Labels.Count >= MaximumCount;

    /// <summary>
    /// Names are unique so a colour can be talked about. Case and surrounding
    /// space do not make two labels different.
    /// </summary>
    public EventLabelNameError? Validate(string name, Guid? excluding = null)
    {
        var trimmed = FoundationText.TrimmingWhitespaceAndNewlines(name);
        if (trimmed.Length == 0)
        {
            return EventLabelNameError.Empty;
        }
        if (FoundationText.CharacterCount(trimmed) > MaximumNameLength)
        {
            return EventLabelNameError.TooLong;
        }
        var clash = Labels.Any(label => label.Id != excluding && FoundationText.CaseInsensitiveEquals(label.Name, trimmed));
        return clash ? EventLabelNameError.Duplicate : null;
    }

    /// <summary>Adds a label, answering it through <paramref name="label"/>.</summary>
    /// <exception cref="EventLabelNameException">The name is empty, too long or taken.</exception>
    public EventLabelLibrary Add(string name, string colorHex, out EventLabel label)
    {
        if (Validate(name) is { } error)
        {
            throw new EventLabelNameException(error);
        }
        label = new EventLabel { Name = FoundationText.TrimmingWhitespaceAndNewlines(name), ColorHex = colorHex };
        return this with { Labels = Labels.Add(label) };
    }

    /// <exception cref="EventLabelNameException">The name is empty, too long or taken by another label.</exception>
    public EventLabelLibrary Rename(Guid id, string name)
    {
        if (Validate(name, excluding: id) is { } error)
        {
            throw new EventLabelNameException(error);
        }
        var index = Labels.FindIndex(label => label.Id == id);
        if (index < 0)
        {
            return this;
        }
        return this with { Labels = Labels.SetItem(index, Labels[index] with { Name = FoundationText.TrimmingWhitespaceAndNewlines(name) }) };
    }

    /// <summary>A colour that cannot be read is not stored; the label keeps the one it had.</summary>
    public EventLabelLibrary Recolor(Guid id, string colorHex)
    {
        var index = Labels.FindIndex(label => label.Id == id);
        if (index < 0 || SrgbColor.FromHex(colorHex) is null)
        {
            return this;
        }
        return this with { Labels = Labels.SetItem(index, Labels[index] with { ColorHex = colorHex }) };
    }

    public EventLabelLibrary Remove(Guid id) => this with { Labels = Labels.RemoveAll(label => label.Id == id) };

    internal static bool IsUsable(EventLabel label)
        => FoundationText.TrimmingWhitespaceAndNewlines(label.Name).Length > 0
            && SrgbColor.FromHex(label.ColorHex) is not null;
}

internal sealed class EventLabelJsonConverter : JsonConverter<EventLabel>
{
    public override EventLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Decode(JsonElement.ParseValue(ref reader));

    public override void Write(Utf8JsonWriter writer, EventLabel value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", CodableJson.UuidString(value.Id));
        writer.WriteString("name", value.Name);
        writer.WriteString("colorHex", value.ColorHex);
        writer.WriteEndObject();
    }

    internal static EventLabel Decode(JsonElement element)
    {
        var label = CodableJson.Object(element, "a label");
        return new EventLabel
        {
            Id = CodableJson.RequiredUuid(label, "id"),
            Name = CodableJson.RequiredString(label, "name"),
            ColorHex = CodableJson.RequiredString(label, "colorHex"),
        };
    }
}

/// <summary>
/// The Mac's custom decoder: a missing version reads as the current one; a
/// version from a later build reads as an empty library without looking at
/// the labels; otherwise every label must decode, and the unusable ones are
/// dropped.
/// </summary>
internal sealed class EventLabelLibraryJsonConverter : JsonConverter<EventLabelLibrary>
{
    public override EventLabelLibrary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var element = CodableJson.Object(JsonElement.ParseValue(ref reader), "the label library");
        var version = DockThemePresetLibraryJsonConverter.ClampedVersion(
            CodableJson.OptionalLong(element, "version") ?? EventLabelLibrary.CurrentVersion);
        if (version > EventLabelLibrary.CurrentVersion)
        {
            // Written by a newer build. Showing none is honest; rewriting what
            // we cannot read would destroy it.
            return new EventLabelLibrary(ValueList<EventLabel>.Empty, version);
        }
        var labels = new List<EventLabel>();
        if (CodableJson.OptionalArray(element, "labels") is { } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                labels.Add(EventLabelJsonConverter.Decode(item));
            }
        }
        return new EventLabelLibrary(labels.Where(EventLabelLibrary.IsUsable).ToValueList(), version);
    }

    public override void Write(Utf8JsonWriter writer, EventLabelLibrary value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        writer.WritePropertyName("labels");
        JsonSerializer.Serialize(writer, value.Labels, options);
        writer.WriteEndObject();
    }
}

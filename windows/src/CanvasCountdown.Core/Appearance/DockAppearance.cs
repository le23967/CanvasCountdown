using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

public enum DockNumberSize
{
    Small,
    Medium,
    Large,
    ExtraLarge,
}

public static class DockNumberSizeExtensions
{
    /// <summary>The stored value, as the Mac app spells it (<c>"extraLarge"</c>).</summary>
    public static string RawValue(this DockNumberSize size) => CodableJson.RawValue(size);

    public static string Title(this DockNumberSize size) => size switch
    {
        DockNumberSize.Small => "Small",
        DockNumberSize.Medium => "Medium",
        DockNumberSize.Large => "Large",
        DockNumberSize.ExtraLarge => "Extra Large",
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>
    /// Fraction of the largest size that still fits the tile for the value
    /// being drawn. Applied after fitting, so digit-count adaptation is
    /// preserved and nothing can clip, while the choice stays visually obvious.
    /// </summary>
    public static double FontScale(this DockNumberSize size) => size switch
    {
        DockNumberSize.Small => 0.58,
        DockNumberSize.Medium => 0.72,
        DockNumberSize.Large => 0.88,
        DockNumberSize.ExtraLarge => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>Short description used by the preview tiles and accessibility labels.</summary>
    public static string AccessibilityDescription(this DockNumberSize size)
        => $"Select {size.Title().ToLowerInvariant()} countdown";
}

public enum DockNumberWeight
{
    Bold,
    Heavy,
}

public static class DockNumberWeightExtensions
{
    public static string RawValue(this DockNumberWeight weight) => CodableJson.RawValue(weight);

    public static string Title(this DockNumberWeight weight) => weight switch
    {
        DockNumberWeight.Bold => "Bold",
        DockNumberWeight.Heavy => "Heavy",
        _ => throw new ArgumentOutOfRangeException(nameof(weight)),
    };

    /// <summary>
    /// The OpenType weight to draw with. The Mac's "heavy" sits between bold
    /// and black, which is 800 (ExtraBold) on the OpenType scale — not
    /// DirectWrite's <c>Heavy</c>, which is 900.
    /// </summary>
    public static int FontWeight(this DockNumberWeight weight) => weight switch
    {
        DockNumberWeight.Bold => 700,
        DockNumberWeight.Heavy => 800,
        _ => throw new ArgumentOutOfRangeException(nameof(weight)),
    };
}

public enum DockThemePreset
{
    DefaultBlue,
    DaysMatter,
    Dark,
    HighContrast,
    Custom,
}

public static class DockThemePresets
{
    /// <summary>
    /// Presets the picker offers. <see cref="DockThemePreset.Custom"/> only
    /// appears once the user has changed a colour themselves.
    /// </summary>
    public static IReadOnlyList<DockThemePreset> Selectable { get; } =
        [DockThemePreset.DefaultBlue, DockThemePreset.DaysMatter, DockThemePreset.Dark, DockThemePreset.HighContrast];

    public static string RawValue(this DockThemePreset preset) => CodableJson.RawValue(preset);

    public static string Title(this DockThemePreset preset) => preset switch
    {
        DockThemePreset.DefaultBlue => "Default Blue",
        DockThemePreset.DaysMatter => "Days Matter Inspired",
        DockThemePreset.Dark => "Dark",
        DockThemePreset.HighContrast => "High Contrast",
        DockThemePreset.Custom => "Custom",
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    public static DockColours? Colours(this DockThemePreset preset) => preset switch
    {
        DockThemePreset.DefaultBlue => new DockColours
        {
            BackgroundTop = "#21448C",
            BackgroundBottom = "#3D7AD1",
            Number = "#FFFFFF",
            Label = "#EAF1FF",
        },
        DockThemePreset.DaysMatter => new DockColours
        {
            BackgroundTop = "#F5F5F7",
            BackgroundBottom = "#E3E4E8",
            Number = "#D83A2E",
            Label = "#5A5A5F",
        },
        DockThemePreset.Dark => new DockColours
        {
            BackgroundTop = "#2B2B2F",
            BackgroundBottom = "#131316",
            Number = "#F2F2F7",
            Label = "#A1A1AA",
        },
        DockThemePreset.HighContrast => new DockColours
        {
            BackgroundTop = "#000000",
            BackgroundBottom = "#000000",
            Number = "#FFFFFF",
            Label = "#FFFF00",
        },
        DockThemePreset.Custom => null,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };
}

/// <summary>
/// The four colours of the countdown tile, as <c>"#RRGGBB"</c> text. The Mac
/// app's <c>DockAppearance.Colours</c>; a nested type cannot share its name
/// with the property that holds it in C#.
/// </summary>
[JsonConverter(typeof(DockColoursJsonConverter))]
public sealed record DockColours
{
    public required string BackgroundTop { get; init; }

    public required string BackgroundBottom { get; init; }

    public required string Number { get; init; }

    public required string Label { get; init; }
}

/// <summary>Everything the user can choose about how the countdown tile looks.</summary>
[JsonConverter(typeof(DockAppearanceJsonConverter))]
public sealed record DockAppearance
{
    /// <summary>
    /// WCAG contrast floors, used to <em>advise</em> and never to overrule. The
    /// countdown is very large text, which the guidelines allow at 3:1; the
    /// small header is held to the 4.5:1 body-text figure.
    /// </summary>
    /// <remarks>
    /// Falling below either of these used to rewrite the colour to black or
    /// white. It no longer does. These are the user's colours on the user's own
    /// countdown: a guideline is a reason to tell somebody, not a reason to
    /// overrule them and hand back a tile they did not ask for.
    /// </remarks>
    public const double MinimumNumberContrastRatio = 3.0;

    public const double MinimumLabelContrastRatio = 4.5;

    public required DockThemePreset Preset { get; init; }

    public required DockNumberSize NumberSize { get; init; }

    public required DockNumberWeight NumberWeight { get; init; }

    public required DockColours Colours { get; init; }

    /// <summary>
    /// Set when the colours came from a preset the user saved. Absent for the
    /// built-in themes and for colours that have never been named.
    /// </summary>
    public Guid? UserPresetId { get; init; }

    public static DockAppearance Defaults { get; } = new()
    {
        Preset = DockThemePreset.DefaultBlue,
        NumberSize = DockNumberSize.Medium,
        NumberWeight = DockNumberWeight.Heavy,
        Colours = DockThemePreset.DefaultBlue.Colours() ?? new DockColours
        {
            BackgroundTop = "#21448C",
            BackgroundBottom = "#3D7AD1",
            Number = "#FFFFFF",
            Label = "#EAF1FF",
        },
    };

    public static DockAppearance Applying(DockThemePreset preset, DockAppearance appearance)
    {
        if (preset.Colours() is not { } colours)
        {
            return appearance;
        }
        return appearance with { Preset = preset, Colours = colours, UserPresetId = null };
    }

    /// <summary>
    /// Puts a saved preset's colours in force, and records which one, so the
    /// theme row can say "Green Gradient" rather than "Custom".
    /// </summary>
    public static DockAppearance Applying(UserDockThemePreset preset, DockAppearance appearance)
        => appearance with { Preset = DockThemePreset.Custom, Colours = preset.Colours, UserPresetId = preset.Id };

    public SrgbColor BackgroundTopColor => SrgbColor.FromHex(Colours.BackgroundTop) ?? SrgbColor.SystemBlue;

    public SrgbColor BackgroundBottomColor => SrgbColor.FromHex(Colours.BackgroundBottom) ?? SrgbColor.SystemBlue;

    public SrgbColor NumberColor => SrgbColor.FromHex(Colours.Number) ?? SrgbColor.White;

    public SrgbColor LabelColor => SrgbColor.FromHex(Colours.Label) ?? SrgbColor.White;

    /// <summary>
    /// Contrast of the number against the middle of the background gradient,
    /// which is the worst case a gradient can present.
    /// </summary>
    public double NumberContrastRatio => SrgbColor.ContrastRatio(NumberColor, AverageBackgroundColor);

    public double LabelContrastRatio => SrgbColor.ContrastRatio(LabelColor, AverageBackgroundColor);

    /// <summary>Whether both colours clear the floors above.</summary>
    /// <remarks>
    /// Read only to decide whether to mention it in Settings. Nothing acts on
    /// it: colours below the floor are stored and drawn exactly as chosen.
    /// </remarks>
    public bool HasSufficientContrast
        => NumberContrastRatio >= MinimumNumberContrastRatio
            && LabelContrastRatio >= MinimumLabelContrastRatio;

    public SrgbColor AverageBackgroundColor => SrgbColor.Blend(BackgroundTopColor, BackgroundBottomColor);
}

/// <summary>
/// Swift's synthesized <c>Codable</c> for the colours: all four keys present
/// and strings.
/// </summary>
internal sealed class DockColoursJsonConverter : JsonConverter<DockColours>
{
    public override DockColours Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Decode(CodableJson.Object(JsonElement.ParseValue(ref reader), "colours"));

    public override void Write(Utf8JsonWriter writer, DockColours value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("backgroundTop", value.BackgroundTop);
        writer.WriteString("backgroundBottom", value.BackgroundBottom);
        writer.WriteString("number", value.Number);
        writer.WriteString("label", value.Label);
        writer.WriteEndObject();
    }

    internal static DockColours Decode(JsonElement element) => new()
    {
        BackgroundTop = CodableJson.RequiredString(element, "backgroundTop"),
        BackgroundBottom = CodableJson.RequiredString(element, "backgroundBottom"),
        Number = CodableJson.RequiredString(element, "number"),
        Label = CodableJson.RequiredString(element, "label"),
    };
}

/// <summary>
/// Swift's synthesized <c>Codable</c> for the appearance: every key required
/// except the saved-preset id, and an unknown theme, size or weight makes the
/// whole value unreadable, so the store falls back to the defaults. The id is
/// written under the Mac's key, <c>"userPresetID"</c>.
/// </summary>
internal sealed class DockAppearanceJsonConverter : JsonConverter<DockAppearance>
{
    public override DockAppearance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var element = CodableJson.Object(JsonElement.ParseValue(ref reader), "the appearance");
        return new DockAppearance
        {
            Preset = CodableJson.RequiredEnum<DockThemePreset>(element, "preset"),
            NumberSize = CodableJson.RequiredEnum<DockNumberSize>(element, "numberSize"),
            NumberWeight = CodableJson.RequiredEnum<DockNumberWeight>(element, "numberWeight"),
            Colours = DockColoursJsonConverter.Decode(CodableJson.RequiredObject(element, "colours")),
            UserPresetId = CodableJson.OptionalUuid(element, "userPresetID"),
        };
    }

    public override void Write(Utf8JsonWriter writer, DockAppearance value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("preset", value.Preset.RawValue());
        writer.WriteString("numberSize", value.NumberSize.RawValue());
        writer.WriteString("numberWeight", value.NumberWeight.RawValue());
        writer.WritePropertyName("colours");
        JsonSerializer.Serialize(writer, value.Colours, options);
        if (value.UserPresetId is { } id)
        {
            writer.WriteString("userPresetID", CodableJson.UuidString(id));
        }
        writer.WriteEndObject();
    }
}

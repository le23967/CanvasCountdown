namespace CanvasCountdown;

/// <summary>
/// An opaque sRGB colour with components from 0 to 1: what the Mac app read
/// out of its <c>"#RRGGBB"</c> strings with <c>NSColor(hex:)</c>.
/// </summary>
/// <remarks>
/// Colours are stored as text, so a label or a theme survives a colour-space
/// change and can be read in the preferences file. This type exists only to
/// measure them (contrast, blending) and to hand them to a renderer.
/// </remarks>
public readonly record struct SrgbColor(double Red, double Green, double Blue)
{
    public static SrgbColor White { get; } = new(1, 1, 1);

    public static SrgbColor Black { get; } = new(0, 0, 0);

    /// <summary>
    /// The Mac's system blue in the light appearance, which it fell back to for
    /// a background it could not read.
    /// </summary>
    public static SrgbColor SystemBlue { get; } = FromBytes(0x00, 0x88, 0xFF);

    /// <summary>
    /// The Mac's system grey in the light appearance, which it fell back to for
    /// a label colour it could not read.
    /// </summary>
    public static SrgbColor SystemGray { get; } = FromBytes(0x8E, 0x8E, 0x93);

    /// <summary>
    /// Reads <c>"#RRGGBB"</c> the way the Mac did: surrounding whitespace, every
    /// <c>#</c> and every space are ignored, and what is left must be six
    /// characters that Swift reads as a hexadecimal <c>UInt32</c>. That
    /// includes a leading sign, so <c>"+12345"</c> is #012345.
    /// </summary>
    public static SrgbColor? FromHex(string? hex)
    {
        if (hex is null)
        {
            return null;
        }
        var value = FoundationText.TrimmingWhitespaceAndNewlines(hex)
            .Replace("#", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (FoundationText.CharacterCount(value) != 6 || !TryParseSwiftHex(value, out var number))
        {
            return null;
        }
        return FromBytes((byte)((number & 0xFF0000) >> 16), (byte)((number & 0x00FF00) >> 8), (byte)(number & 0x0000FF));
    }

    public static SrgbColor FromBytes(byte red, byte green, byte blue) => new(red / 255.0, green / 255.0, blue / 255.0);

    /// <summary><c>"#RRGGBB"</c>, each channel rounded half away from zero as Swift's <c>round</c> does.</summary>
    public string HexString
    {
        get
        {
            var (red, green, blue) = ToBytes();
            return $"#{red:X2}{green:X2}{blue:X2}";
        }
    }

    public (byte Red, byte Green, byte Blue) ToBytes() => (Channel(Red), Channel(Green), Channel(Blue));

    /// <summary>WCAG relative luminance.</summary>
    public double RelativeLuminance => (0.2126 * Linear(Red)) + (0.7152 * Linear(Green)) + (0.0722 * Linear(Blue));

    public static double ContrastRatio(SrgbColor first, SrgbColor second)
    {
        var lighter = Math.Max(first.RelativeLuminance, second.RelativeLuminance);
        var darker = Math.Min(first.RelativeLuminance, second.RelativeLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>The midpoint of two colours, channel by channel.</summary>
    public static SrgbColor Blend(SrgbColor first, SrgbColor second)
        => new((first.Red + second.Red) / 2, (first.Green + second.Green) / 2, (first.Blue + second.Blue) / 2);

    public override string ToString() => HexString;

    private static double Linear(double component)
        => component <= 0.03928 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);

    private static byte Channel(double component)
        => (byte)Math.Clamp(Math.Round(component * 255, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// Swift's <c>UInt32(text, radix: 16)</c>: an optional sign, then one or
    /// more hexadecimal digits, in range. A minus sign is accepted only for
    /// zero, which is the one negative number a <c>UInt32</c> can hold.
    /// </summary>
    internal static bool TryParseSwiftHex(string text, out uint value)
    {
        value = 0;
        var digits = text.AsSpan();
        var isNegative = false;
        if (digits.Length > 0 && (digits[0] == '+' || digits[0] == '-'))
        {
            isNegative = digits[0] == '-';
            digits = digits[1..];
        }
        if (digits.IsEmpty)
        {
            return false;
        }
        ulong accumulated = 0;
        foreach (var character in digits)
        {
            int digit;
            if (character is >= '0' and <= '9')
            {
                digit = character - '0';
            }
            else if (character is >= 'a' and <= 'f')
            {
                digit = character - 'a' + 10;
            }
            else if (character is >= 'A' and <= 'F')
            {
                digit = character - 'A' + 10;
            }
            else
            {
                return false;
            }
            accumulated = (accumulated * 16) + (ulong)digit;
            if (accumulated > uint.MaxValue)
            {
                return false;
            }
        }
        if (isNegative && accumulated != 0)
        {
            return false;
        }
        value = (uint)accumulated;
        return true;
    }
}

/// <summary>
/// Hex validation shared by the theme library and the colour wells: the Mac's
/// <c>NSColorHex</c>.
/// </summary>
/// <remarks>
/// Deliberately stricter than <see cref="SrgbColor.FromHex"/> about spaces:
/// a space inside the digits makes a theme colour invalid even though the
/// parser would skip it. Both rules are kept as the Mac had them.
/// </remarks>
public static class ColorHex
{
    public static bool IsValid(string value)
    {
        var trimmed = FoundationText.TrimmingWhitespaceAndNewlines(value)
            .Replace("#", string.Empty, StringComparison.Ordinal);
        return FoundationText.CharacterCount(trimmed) == 6 && SrgbColor.TryParseSwiftHex(trimmed, out _);
    }
}

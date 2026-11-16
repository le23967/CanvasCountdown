using System.Globalization;
using System.Text;

namespace CanvasCountdown;

/// <summary>
/// The Swift and Foundation text rules the feed code depends on, written once
/// so the parser, the coordinator, the stores and the repository apply them
/// identically.
/// </summary>
/// <remarks>
/// The macOS app was written against Swift's <c>String</c>, which differs from
/// a .NET <c>string</c> in ways that change what a feed means:
/// <list type="bullet">
/// <item>A Swift <c>Character</c> is a whole grapheme cluster, so a delimiter
/// with a combining mark after it is not that delimiter, and counts are of
/// clusters, not UTF-16 units.</item>
/// <item>Two strings are equal when they are canonically equivalent (é as one
/// code point or as e + U+0301), and sets and dictionaries key them that way,
/// keeping whichever spelling arrived first. The ports of those collections
/// use <see cref="CanonicalComparer"/>.</item>
/// <item>Foundation's whitespace set includes U+200B ZERO WIDTH SPACE and
/// excludes the information separators U+001C–U+001F.</item>
/// <item>Case mapping is Unicode's full mapping: ß uppercases to SS, ı to I,
/// a ligature to its letters, and İ lowercases to i + U+0307. .NET's invariant
/// mapping leaves all of those alone.</item>
/// </list>
/// Getting any of these wrong silently changes which events match which, so
/// each is reproduced here rather than approximated at each call site.
/// </remarks>
internal static class SwiftText
{
    /// <summary>Foundation's <c>CharacterSet.whitespacesAndNewlines</c>, read off a scan of every BMP scalar.</summary>
    private static readonly char[] WhitespacesAndNewlines =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u0085', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u200B',
        '\u2028', '\u2029', '\u202F', '\u205F', '\u3000',
    ];

    /// <summary>Trims as <c>trimmingCharacters(in: .whitespacesAndNewlines)</c> does.</summary>
    public static string Trimmed(string value) => value.Trim(WhitespacesAndNewlines);

    /// <summary>Foundation's <c>CharacterSet.whitespaces</c>: the same set without the line and paragraph breaks.</summary>
    public static bool IsWhitespace(char value) => value switch
    {
        '\t' or ' ' or '\u00A0' or '\u1680' or '\u202F' or '\u205F' or '\u3000' => true,
        >= '\u2000' and <= '\u200B' => true,
        _ => false,
    };

    /// <summary>Swift's <c>uppercased()</c>: full Unicode case mapping, independent of locale.</summary>
    public static string Uppercased(string value)
    {
        var upper = value.ToUpperInvariant();
        if (IsAscii(upper))
        {
            return upper;
        }
        StringBuilder? builder = null;
        for (var index = 0; index < upper.Length; index++)
        {
            var expansion = upper[index] switch
            {
                '\u0131' => "I",
                '\u00DF' => "SS",
                '\uFB00' => "FF",
                '\uFB01' => "FI",
                '\uFB02' => "FL",
                '\uFB03' => "FFI",
                '\uFB04' => "FFL",
                '\uFB05' or '\uFB06' => "ST",
                _ => null,
            };
            if (expansion is null)
            {
                builder?.Append(upper[index]);
                continue;
            }
            builder ??= new StringBuilder(upper, 0, index, upper.Length + 4);
            builder.Append(expansion);
        }
        return builder?.ToString() ?? upper;
    }

    /// <summary>
    /// Swift's <c>lowercased()</c> and Foundation's <c>lowercased(with: en_US_POSIX)</c>,
    /// which agree except for the final-sigma context rule (not reproduced).
    /// </summary>
    public static string Lowercased(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains('\u0130') ? lower.Replace("\u0130", "i\u0307", StringComparison.Ordinal) : lower;
    }

    /// <summary>The composed form, which compares and hashes the way Swift's canonical equivalence does.</summary>
    public static string Composed(string value) => value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);

    /// <summary>Trimmed and lowercased as en_US_POSIX does: the form an identifier or course key is compared in.</summary>
    public static string Identity(string value) => Lowercased(Trimmed(value));

    /// <summary>Equality and hashing by canonical equivalence, as Swift's <c>Set</c> and <c>Dictionary</c> key strings.</summary>
    public static IEqualityComparer<string> CanonicalComparer { get; } = new CanonicalStringComparer();

    private sealed class CanonicalStringComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => CanonicallyEqual(x, y);

        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Composed(obj));
    }

    /// <summary>Swift <c>String</c> equality: canonical equivalence.</summary>
    public static bool CanonicallyEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return string.Equals(left, right, StringComparison.Ordinal)
            || string.Equals(Composed(left), Composed(right), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>caseInsensitiveCompare(_:) == .orderedSame</c> against an ASCII word.
    /// No non-ASCII character folds onto the letters these words use, so only
    /// an ASCII value can match.
    /// </summary>
    public static bool EqualsAsciiIgnoringCase(string value, string asciiWord)
    {
        if (value.Length != asciiWord.Length || !IsAscii(value))
        {
            return false;
        }
        return string.Equals(value, asciiWord, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>caseInsensitiveCompare(_:) == .orderedSame</c> for time-zone names:
    /// canonical equivalence, then case folding (including ſ folding to s).
    /// </summary>
    public static bool EqualsIgnoringCase(string left, string right)
    {
        var composedLeft = Composed(left).Replace('\u017F', 's');
        var composedRight = Composed(right).Replace('\u017F', 's');
        return string.Equals(composedLeft, composedRight, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The length, in UTF-16 units, of the grapheme cluster (Swift <c>Character</c>) starting at <paramref name="index"/>.</summary>
    public static int ElementLength(string text, int index)
    {
        var current = text[index];
        // Between two ASCII characters there is always a boundary, except
        // inside CR LF; this keeps the common case off the segmentation tables.
        if (current < 0x80 && current != '\r' && (index + 1 == text.Length || text[index + 1] < 0x80))
        {
            return 1;
        }
        return StringInfo.GetNextTextElementLength(text.AsSpan(index));
    }

    /// <summary>
    /// True when the cluster at <paramref name="index"/> is the single ASCII
    /// character <paramref name="ascii"/> — or canonically equivalent to it,
    /// which for the delimiters the parser looks for means only U+037E GREEK
    /// QUESTION MARK standing for a semicolon.
    /// </summary>
    public static bool IsCharacter(string text, int index, int length, char ascii)
        => length == 1 && (text[index] == ascii || (ascii == ';' && text[index] == '\u037E'));

    /// <summary>Swift's <c>count</c>: the number of grapheme clusters.</summary>
    public static int CountCharacters(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index += ElementLength(text, index))
        {
            count++;
        }
        return count;
    }

    /// <summary>The start of each grapheme cluster, plus <c>text.Length</c> at the end.</summary>
    public static List<int> CharacterBoundaries(string text)
    {
        var boundaries = new List<int>(text.Length + 1);
        for (var index = 0; index < text.Length; index += ElementLength(text, index))
        {
            boundaries.Add(index);
        }
        boundaries.Add(text.Length);
        return boundaries;
    }

    /// <summary>
    /// Swift's <c>Character.isNumber</c>: the cluster's first scalar has a
    /// numeric value. .NET's data lacks the Han numerals (三), which Swift counts.
    /// </summary>
    public static bool IsNumber(string text, int index)
        => Rune.TryGetRuneAt(text, index, out var rune) && Rune.GetNumericValue(rune) != -1;

    /// <summary>Swift's <c>hasSuffix</c>: the suffix must match whole characters.</summary>
    public static bool HasSuffix(string text, string suffix)
    {
        var composedText = Composed(text);
        var composedSuffix = Composed(suffix);
        if (!composedText.EndsWith(composedSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var start = composedText.Length - composedSuffix.Length;
        return start == 0 || IsAscii(composedText) || CharacterBoundaries(composedText).BinarySearch(start) >= 0;
    }

    /// <summary>Swift's <c>contains(_:)</c> on strings: a match must start and end on character boundaries.</summary>
    public static bool Contains(string text, string needle)
    {
        var composedText = Composed(text);
        var composedNeedle = Composed(needle);
        var at = composedText.IndexOf(composedNeedle, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }
        if (IsAscii(composedText))
        {
            return true;
        }
        var boundaries = CharacterBoundaries(composedText);
        for (; at >= 0; at = composedText.IndexOf(composedNeedle, at + 1, StringComparison.Ordinal))
        {
            if (boundaries.BinarySearch(at) >= 0 && boundaries.BinarySearch(at + composedNeedle.Length) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Swift's <c>Int(_:)</c>: an optional leading sign, then ASCII digits only,
    /// in 64-bit range; anything else, including surrounding space, is nil.
    /// </summary>
    public static long? ParseInteger(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }
        var index = 0;
        var negative = false;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            index = 1;
            if (text.Length == 1)
            {
                return null;
            }
        }
        // Accumulate negatively so long.MinValue is reachable.
        long value = 0;
        for (; index < text.Length; index++)
        {
            var digit = text[index] - '0';
            if (digit is < 0 or > 9)
            {
                return null;
            }
            if (value < (long.MinValue + digit) / 10)
            {
                return null;
            }
            value = (value * 10) - digit;
        }
        if (negative)
        {
            return value;
        }
        return value == long.MinValue ? null : -value;
    }

    public static bool IsAscii(string text)
    {
        foreach (var character in text)
        {
            if (character >= 0x80)
            {
                return false;
            }
        }
        return true;
    }
}

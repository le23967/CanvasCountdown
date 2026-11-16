using System.Globalization;
using System.Text;

namespace CanvasCountdown;

/// <summary>
/// The handful of Foundation string rules the names, labels and release notes
/// depend on, so the Windows version accepts and refuses exactly what the Mac
/// does.
/// </summary>
/// <remarks>
/// .NET's nearest built-ins each differ from Foundation somewhere that a user
/// can type: <c>string.Length</c> counts an emoji as two, ordinal
/// case-insensitive comparison misses "ß" against "SS", and culture comparison
/// treats a zero-width space as absent. Each rule here is checked against what
/// Foundation actually answered on a Mac.
/// </remarks>
internal static class FoundationText
{
    /// <summary>Swift's <c>trimmingCharacters(in: .whitespacesAndNewlines)</c>.</summary>
    public static string TrimmingWhitespaceAndNewlines(string text) => text.Trim();

    /// <summary>
    /// Swift's <c>trimmingCharacters(in: .whitespaces)</c>: spaces and tabs
    /// only, so a carriage return survives it.
    /// </summary>
    public static string TrimmingWhitespace(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsHorizontalWhitespace(text[start]))
        {
            start++;
        }
        while (end > start && IsHorizontalWhitespace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary>
    /// Characters as a person counts them, which is what Swift's
    /// <c>String.count</c> answers: "🎓" is one, not two.
    /// </summary>
    public static int CharacterCount(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The first <paramref name="count"/> characters, counted as people count them.</summary>
    public static string Prefix(string text, int count)
    {
        var info = new StringInfo(text);
        return count >= info.LengthInTextElements ? text : info.SubstringByTextElements(0, count);
    }

    /// <summary>
    /// Foundation's <c>caseInsensitiveCompare(_:) == .orderedSame</c>: full
    /// Unicode case folding ("Straße" matches "STRASSE", "ﬁ" matches "FI") over
    /// canonically equivalent text (a precomposed "é" matches "e" plus an
    /// accent), with nothing ignored and no width folding.
    /// </summary>
    public static bool CaseInsensitiveEquals(string left, string right)
        => string.Equals(Fold(left), Fold(right), StringComparison.Ordinal);

    private static bool IsHorizontalWhitespace(char character)
        => character == '\t' || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (FullFoldings.TryGetValue(rune.Value, out var expansion))
            {
                builder.Append(expansion);
                continue;
            }
            // Dotless i has no folding of its own; through upper case it would
            // become a plain "i", which Foundation keeps distinct.
            var folded = rune.Value == 0x0131
                ? rune
                : Rune.ToLowerInvariant(Rune.ToUpperInvariant(rune));
            builder.Append(folded.ToString());
        }
        return builder.ToString().Normalize(NormalizationForm.FormD);
    }

    /// <summary>
    /// The one-to-many foldings from Unicode's CaseFolding.txt that simple case
    /// mapping cannot express.
    /// </summary>
    private static readonly Dictionary<int, string> FullFoldings = new()
    {
        [0x00DF] = "ss",
        [0x1E9E] = "ss",
        [0x0149] = "ʼn",
        [0xFB00] = "ff",
        [0xFB01] = "fi",
        [0xFB02] = "fl",
        [0xFB03] = "ffi",
        [0xFB04] = "ffl",
        [0xFB05] = "st",
        [0xFB06] = "st",
        [0x0587] = "եւ",
        [0xFB13] = "մն",
        [0xFB14] = "մե",
        [0xFB15] = "մի",
        [0xFB16] = "վն",
        [0xFB17] = "մխ",
    };
}

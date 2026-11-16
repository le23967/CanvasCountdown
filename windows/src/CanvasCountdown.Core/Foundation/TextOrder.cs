using System.Globalization;

namespace CanvasCountdown;

/// <summary>
/// How names are ordered for people: Swift's <c>localizedStandardCompare</c>.
/// </summary>
/// <remarks>
/// Case-insensitive and number-aware, so "Lab 2" comes before "Lab 10" and
/// "essay" sits beside "Essay". Plain ordinal order puts every capital letter
/// before every small one and "10" before "2", which reads as broken in a list
/// of assignments.
/// </remarks>
public static class TextOrder
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    public static int Compare(string? left, string? right)
        => CultureInfo.CurrentCulture.CompareInfo.Compare(left, right, Options);

    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);
}

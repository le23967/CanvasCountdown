using System.Collections.Concurrent;
using System.Globalization;

namespace CanvasCountdown;

/// <summary>A zone the parser can read times in, and the identifier the macOS app reported for it.</summary>
internal sealed record ResolvedTimeZone(TimeZoneInfo Zone, string Identifier);

/// <summary>
/// Turns the time-zone names calendar producers write into zones, the way the
/// macOS app did.
/// </summary>
/// <remarks>
/// The order is the Swift parser's, because it decides the answer whenever two
/// steps disagree: "BST" is an exact ICU id (Bangladesh) before it is an
/// abbreviation (London), and "EST" is the fixed −5 zone, not New York.
/// <list type="number">
/// <item>Exactly as <c>TimeZone(identifier:)</c>: a "GMT+10"-style offset, or
/// any id ICU knows, spelt exactly.</item>
/// <item>As <c>TimeZone(abbreviation:)</c>: Foundation's fixed table.</item>
/// <item>The six Windows names the Swift code mapped by hand, in any case.</item>
/// <item>A known id that matches ignoring case.</item>
/// <item>The longest known id the name ends with, which is how
/// "/mozilla.org/20050126_1/Australia/Sydney" resolves.</item>
/// <item>Not in the Swift code: any other Windows zone name ("Tokyo Standard
/// Time"), through .NET's own mapping, because Outlook writes them and the
/// macOS app could only read six. It runs last, so it can never change an
/// answer the Swift steps would have given.</item>
/// </list>
/// </remarks>
internal static class IcsTimeZones
{
    private static readonly HashSet<string> AcceptedIdentifiers =
        new(IcsTimeZoneData.KnownIdentifiers.Concat(IcsTimeZoneData.OtherAcceptedIdentifiers), StringComparer.Ordinal);

    private static readonly string[] KnownIdentifiersLongestFirst =
        IcsTimeZoneData.KnownIdentifiers.OrderByDescending(identifier => identifier.Length).ToArray();

    private static readonly Dictionary<string, string> Abbreviations =
        IcsTimeZoneData.Abbreviations.ToDictionary(pair => pair.Abbreviation, pair => pair.Identifier, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> WindowsNames = new(StringComparer.Ordinal)
    {
        ["eastern standard time"] = "America/New_York",
        ["central standard time"] = "America/Chicago",
        ["mountain standard time"] = "America/Denver",
        ["pacific standard time"] = "America/Los_Angeles",
        ["aus eastern standard time"] = "Australia/Sydney",
        ["new zealand standard time"] = "Pacific/Auckland",
    };

    private static readonly ConcurrentDictionary<string, ResolvedTimeZone?> KnownCache = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> LoadedZones = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<int, ResolvedTimeZone> FixedOffsetZones = new();

    /// <summary>
    /// The parser's <c>resolveTimeZone</c>: a name the calendar's own VTIMEZONE
    /// blocks defined, as written or lowercased, before any known zone. The
    /// aliases are keyed with <see cref="SwiftText.CanonicalComparer"/>.
    /// </summary>
    public static ResolvedTimeZone? Resolve(string identifier, IReadOnlyDictionary<string, ResolvedTimeZone> aliases)
    {
        if (aliases.TryGetValue(identifier, out var alias)
            || aliases.TryGetValue(SwiftText.Lowercased(identifier), out alias))
        {
            return alias;
        }
        return ResolveKnown(identifier);
    }

    /// <summary>The parser's <c>resolveKnownTimeZone</c>.</summary>
    public static ResolvedTimeZone? ResolveKnown(string rawIdentifier)
        => KnownCache.GetOrAdd(SwiftText.Trimmed(rawIdentifier), static identifier =>
            Exact(identifier)
            ?? FromAbbreviation(identifier)
            ?? FromSwiftWindowsTable(identifier)
            ?? IgnoringCase(identifier)
            ?? LongestSuffix(identifier)
            ?? FromWindowsName(identifier));

    /// <summary>
    /// <c>TimeZone(identifier:)</c>. ICU looks ids up exactly as spelt, so a
    /// disk lookup — which on macOS would also find "australia/sydney" — is
    /// never used to decide whether a name is known.
    /// </summary>
    public static ResolvedTimeZone? Exact(string identifier)
    {
        if (TryParseGmtOffset(identifier, out var offsetSeconds))
        {
            return FixedOffset(offsetSeconds);
        }
        if (!AcceptedIdentifiers.Contains(identifier))
        {
            return null;
        }
        var zone = Load(identifier);
        return zone is null ? null : new ResolvedTimeZone(zone, identifier);
    }

    /// <summary>
    /// Foundation's grammar for offset names: "GMT" or "UTC", a sign, then H,
    /// HH, HHMM, H:MM or HH:MM (a full stop works as the colon), at most 18
    /// hours. Its quirks are kept as observed: HH:MM does not range-check the
    /// minutes ("GMT+10:60" is +11:00), the other forms do.
    /// </summary>
    public static bool TryParseGmtOffset(string identifier, out int offsetSeconds)
    {
        offsetSeconds = 0;
        if (identifier.Length is < 3 or > 9 || !SwiftText.IsAscii(identifier)
            || !(identifier.StartsWith("GMT", StringComparison.Ordinal) || identifier.StartsWith("UTC", StringComparison.Ordinal)))
        {
            return false;
        }
        if (identifier.Length == 3)
        {
            return true;
        }
        if (identifier.Length < 5 || identifier[3] is not ('+' or '-'))
        {
            return false;
        }
        var sign = identifier[3] == '-' ? -1 : 1;
        var digits = identifier.AsSpan(4);
        int hours;
        var minutes = 0;
        switch (digits.Length)
        {
            case 1 when IsDigit(digits[0]):
                hours = digits[0] - '0';
                break;
            case 2 when IsDigit(digits[0]) && IsDigit(digits[1]):
                hours = Number(digits[..2]);
                break;
            case 4 when IsDigit(digits[0]) && digits[1] is ':' or '.' && IsDigit(digits[2]) && IsDigit(digits[3]):
                hours = digits[0] - '0';
                minutes = Number(digits[2..]);
                if (minutes > 59)
                {
                    return false;
                }
                break;
            case 4 when IsDigit(digits[0]) && IsDigit(digits[1]) && IsDigit(digits[2]) && IsDigit(digits[3]):
                hours = Number(digits[..2]);
                minutes = Number(digits[2..]);
                if (minutes > 59)
                {
                    return false;
                }
                break;
            case 5 when IsDigit(digits[0]) && IsDigit(digits[1]) && digits[2] is ':' or '.' && IsDigit(digits[3]) && IsDigit(digits[4]):
                hours = Number(digits[..2]);
                minutes = Number(digits[3..]);
                break;
            default:
                return false;
        }
        var total = (hours * 3600) + (minutes * 60);
        if (hours > 18 || total > 18 * 3600)
        {
            return false;
        }
        offsetSeconds = sign * total;
        return true;

        static bool IsDigit(char value) => value is >= '0' and <= '9';
        static int Number(ReadOnlySpan<char> value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static ResolvedTimeZone FixedOffset(int offsetSeconds)
        => FixedOffsetZones.GetOrAdd(offsetSeconds, static seconds =>
        {
            if (seconds == 0)
            {
                return new ResolvedTimeZone(TimeZoneInfo.Utc, "GMT");
            }
            var magnitude = Math.Abs(seconds);
            var identifier = string.Create(
                CultureInfo.InvariantCulture,
                $"GMT{(seconds < 0 ? '-' : '+')}{magnitude / 3600:00}{magnitude % 3600 / 60:00}");
            var zone = TimeZoneInfo.CreateCustomTimeZone(identifier, TimeSpan.FromSeconds(seconds), identifier, identifier);
            return new ResolvedTimeZone(zone, identifier);
        });

    private static ResolvedTimeZone? FromAbbreviation(string identifier)
        => Abbreviations.TryGetValue(identifier, out var mapped) ? Exact(mapped) : null;

    private static ResolvedTimeZone? FromSwiftWindowsTable(string identifier)
        => WindowsNames.TryGetValue(SwiftText.Lowercased(identifier), out var mapped) ? Exact(mapped) : null;

    private static ResolvedTimeZone? IgnoringCase(string identifier)
    {
        foreach (var known in IcsTimeZoneData.KnownIdentifiers)
        {
            if (SwiftText.EqualsIgnoringCase(known, identifier))
            {
                return Exact(known);
            }
        }
        return null;
    }

    private static ResolvedTimeZone? LongestSuffix(string identifier)
    {
        foreach (var known in KnownIdentifiersLongestFirst)
        {
            if (SwiftText.HasSuffix(identifier, known))
            {
                return Exact(known);
            }
        }
        return null;
    }

    /// <summary>
    /// A Windows zone name, spelt exactly. ICU's table also answers for some
    /// spellings that are not Windows names at all ("utc"), so a name only
    /// counts when the zone it maps to maps back to it.
    /// </summary>
    private static ResolvedTimeZone? FromWindowsName(string identifier)
    {
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(identifier, out var iana)
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var windowsId)
            || !string.Equals(windowsId, identifier, StringComparison.Ordinal))
        {
            return null;
        }
        if (Exact(iana) is { } exact)
        {
            return exact;
        }
        var zone = Load(iana);
        return zone is null ? null : new ResolvedTimeZone(zone, iana);
    }

    /// <summary>
    /// The platform's zone for an id already known to be valid: its own tz
    /// data on macOS and Linux, its registry (through ICU) on Windows. ICU's
    /// legacy ids ("PST", "AET") have no file, so they go through the Windows
    /// name ICU maps them to and back. The thirteen "SystemV/…" ids ICU still
    /// carries have neither, and do not load.
    /// </summary>
    private static TimeZoneInfo? Load(string identifier)
        => LoadedZones.GetOrAdd(identifier, static id =>
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId)
                && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone))
            {
                return zone;
            }
            return null;
        });
}

using System.Text;

namespace CanvasCountdown;

/// <summary>The subset of an iCalendar event that Canvas Countdown imports.</summary>
public sealed record ParsedCalendarEvent
{
    public required string? Uid { get; init; }

    public required string Summary { get; init; }

    public required DateTimeOffset StartDate { get; init; }

    public required DateTimeOffset? EndDate { get; init; }

    public required string? Description { get; init; }

    public required string? Url { get; init; }

    public required DateTimeOffset? LastModified { get; init; }

    public required bool IsAllDay { get; init; }

    /// <summary>
    /// The zone a zoned time was read in, as the macOS app named it: "UTC" for
    /// a time ending in Z, otherwise the resolved zone's identifier. Null for
    /// floating, all-day and numeric-offset times.
    /// </summary>
    public required string? TimeZoneIdentifier { get; init; }

    /// <summary>SEQUENCE, when present and an integer. 64-bit, as Swift's <c>Int</c> was.</summary>
    public long? Sequence { get; init; }
}

/// <summary>A recoverable problem with one entry in a feed. Valid entries are still returned.</summary>
public sealed record IcsParseWarning
{
    public required string? EventUid { get; init; }

    public required int? Line { get; init; }

    public required string Message { get; init; }
}

public sealed record IcsParseResult
{
    public required ValueList<ParsedCalendarEvent> Events { get; init; }

    public required ValueList<IcsParseWarning> Warnings { get; init; }

    /// <summary>UIDs the feed explicitly cancelled, normalized (trimmed, lowercased).</summary>
    public ValueSet<string> CancelledEventUids { get; init; } = ValueSet<string>.Empty;
}

public enum IcsParserError
{
    InvalidTextEncoding,
    NotCalendarData,
}

public sealed class IcsParserException : Exception
{
    public IcsParserException(IcsParserError error)
        : base(Describe(error))
    {
        Error = error;
    }

    public IcsParserError Error { get; }

    private static string Describe(IcsParserError error) => error switch
    {
        IcsParserError.InvalidTextEncoding => "The calendar feed is not valid UTF-8 text.",
        _ => "The downloaded content is not an iCalendar feed.",
    };
}

public interface IIcsParser
{
    IReadOnlyList<ParsedCalendarEvent> Parse(byte[] data, TimeZoneInfo defaultTimeZone);

    IcsParseResult ParseResult(byte[] data, TimeZoneInfo defaultTimeZone)
        => new() { Events = Parse(data, defaultTimeZone).ToValueList(), Warnings = [] };
}

/// <summary>
/// A dependency-free RFC 5545 parser for the fields used by Canvas Countdown.
/// </summary>
/// <remarks>
/// Parsing is deliberately tolerant at the event level: a malformed VEVENT
/// produces a warning and does not prevent other valid deadlines from being
/// imported.
///
/// Every character-level rule here is Swift's, because the macOS app is the
/// specification and feeds that parsed there must parse the same way here: a
/// delimiter is a whole grapheme cluster (see <see cref="SwiftText"/>), dates
/// are read as a strict <c>DateFormatter</c> read them (see
/// <see cref="IcsDateFormat"/>), and zone names resolve as Foundation resolved
/// them (see <see cref="IcsTimeZones"/>).
/// </remarks>
public sealed class IcsParser : IIcsParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IReadOnlyList<ParsedCalendarEvent> Parse(byte[] data, TimeZoneInfo defaultTimeZone)
        => ParseResult(data, defaultTimeZone).Events;

    /// <summary>
    /// Decodes strict UTF-8 and parses. Foundation's decoder drops one leading
    /// byte-order mark and the Swift parser drops one more, so up to two go.
    /// </summary>
    public IcsParseResult ParseResult(byte[] data, TimeZoneInfo defaultTimeZone)
    {
        ArgumentNullException.ThrowIfNull(data);
        string text;
        try
        {
            text = StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new IcsParserException(IcsParserError.InvalidTextEncoding);
        }
        for (var removed = 0; removed < 2 && text.Length > 0 && text[0] == '\uFEFF'; removed++)
        {
            text = text[1..];
        }
        return ParseResult(text, defaultTimeZone);
    }

    public IReadOnlyList<ParsedCalendarEvent> Parse(string text, TimeZoneInfo defaultTimeZone)
        => ParseResult(text, defaultTimeZone).Events;

    /// <param name="text">The feed text. Unlike the byte overload, a leading byte-order mark is kept, as in Swift.</param>
    /// <param name="defaultTimeZone">The zone for floating times and all-day dates when the calendar names none.</param>
    public IcsParseResult ParseResult(string text, TimeZoneInfo defaultTimeZone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(defaultTimeZone);

        var contentLines = new List<ContentLine>();
        foreach (var sourceLine in Unfold(text))
        {
            if (ParseContentLine(sourceLine.Text, sourceLine.Number) is { } contentLine)
            {
                contentLines.Add(contentLine);
            }
        }

        var isCalendar = contentLines.Any(line => line.Name == "BEGIN" && SwiftText.Uppercased(line.Value) == "VCALENDAR");
        if (!isCalendar)
        {
            throw new IcsParserException(IcsParserError.NotCalendarData);
        }

        var timeZoneAliases = TimeZoneAliases(contentLines);
        var calendarTimeZone = CalendarTimeZone(contentLines, timeZoneAliases);
        var floatingTimeZone = calendarTimeZone?.Zone ?? defaultTimeZone;
        var calendarMethodIsCancellation = contentLines.Any(line =>
            line.Name == "METHOD" && SwiftText.EqualsAsciiIgnoringCase(SwiftText.Trimmed(line.Value), "CANCEL"));

        var events = new List<ParsedCalendarEvent>();
        var warnings = new List<IcsParseWarning>();
        var cancelledEventUids = new HashSet<string>(SwiftText.CanonicalComparer);
        List<ContentLine>? eventLines = null;
        var nestedComponentDepth = 0;

        foreach (var line in contentLines)
        {
            if (eventLines is null)
            {
                if (line.Name == "BEGIN" && ComponentName(line) == "VEVENT")
                {
                    eventLines = [];
                    nestedComponentDepth = 0;
                }
                continue;
            }

            if (line.Name == "BEGIN")
            {
                nestedComponentDepth++;
                continue;
            }

            if (line.Name == "END")
            {
                if (nestedComponentDepth > 0)
                {
                    nestedComponentDepth--;
                    continue;
                }

                if (ComponentName(line) == "VEVENT")
                {
                    var eventResult = MakeEvent(eventLines, floatingTimeZone, timeZoneAliases, calendarMethodIsCancellation);
                    if (eventResult.Event is { } parsedEvent)
                    {
                        events.Add(parsedEvent);
                    }
                    if (eventResult.CancelledUid is { } cancelledUid)
                    {
                        cancelledEventUids.Add(NormalizedUid(cancelledUid));
                    }
                    warnings.AddRange(eventResult.Warnings);
                    eventLines = null;
                }
                continue;
            }

            if (nestedComponentDepth == 0)
            {
                eventLines.Add(line);
            }
        }

        if (eventLines is not null)
        {
            warnings.Add(new IcsParseWarning
            {
                EventUid = FirstValue("UID", eventLines),
                Line = eventLines.Count > 0 ? eventLines[0].SourceLine : null,
                Message = "An unfinished VEVENT was ignored.",
            });
        }

        var activeEvents = Deduplicated(events.Where(parsedEvent =>
            parsedEvent.Uid is not { } uid || !cancelledEventUids.Contains(NormalizedUid(uid))));
        return new IcsParseResult
        {
            Events = activeEvents.ToValueList(),
            Warnings = warnings.ToValueList(),
            CancelledEventUids = cancelledEventUids.ToValueSet(),
        };
    }

    /// <summary>The component a BEGIN or END line names.</summary>
    private static string ComponentName(ContentLine line) => SwiftText.Uppercased(SwiftText.Trimmed(line.Value));

    /// <summary>Trimmed and lowercased as en_US_POSIX does: the form UIDs are compared in everywhere.</summary>
    internal static string NormalizedUid(string uid) => SwiftText.Identity(uid);

    private sealed class SourceLine(string text, int number)
    {
        public string Text { get; set; } = text;

        public int Number { get; } = number;
    }

    private sealed record ContentLine(string Name, IReadOnlyDictionary<string, string> Parameters, string Value, int SourceLine);

    private sealed record ParsedDate(DateTimeOffset Date, bool IsAllDay, string? TimeZoneIdentifier);

    private sealed record EventConstructionResult(ParsedCalendarEvent? Event, IReadOnlyList<IcsParseWarning> Warnings, string? CancelledUid);

    private sealed class DateParsingException(string message) : Exception(message)
    {
        public static DateParsingException InvalidValue(string value) => new($"“{value}” is not a valid iCalendar date.");

        public static DateParsingException UnknownTimeZone(string identifier) => new($"The time zone “{identifier}” is not recognized.");
    }

    /// <summary>
    /// Keeps one entry per UID: the higher SEQUENCE, or with equal or absent
    /// sequences the later LAST-MODIFIED. With no revision metadata at all,
    /// RFC 5545 producers conventionally emit the later revision last, so the
    /// later entry wins.
    /// </summary>
    private static List<ParsedCalendarEvent> Deduplicated(IEnumerable<ParsedCalendarEvent> events)
    {
        var result = new List<ParsedCalendarEvent>();
        var indexByUid = new Dictionary<string, int>(SwiftText.CanonicalComparer);

        foreach (var parsedEvent in events)
        {
            if (parsedEvent.Uid is not { } uid)
            {
                result.Add(parsedEvent);
                continue;
            }
            var key = NormalizedUid(uid);
            if (!indexByUid.TryGetValue(key, out var existingIndex))
            {
                indexByUid[key] = result.Count;
                result.Add(parsedEvent);
                continue;
            }

            var existing = result[existingIndex];
            bool shouldReplace;
            if (existing.Sequence != parsedEvent.Sequence && (existing.Sequence is not null || parsedEvent.Sequence is not null))
            {
                shouldReplace = (parsedEvent.Sequence ?? long.MinValue) > (existing.Sequence ?? long.MinValue);
            }
            else
            {
                shouldReplace = (existing.LastModified, parsedEvent.LastModified) switch
                {
                    ({ } existingDate, { } incomingDate) => incomingDate >= existingDate,
                    (null, not null) => true,
                    (not null, null) => false,
                    (null, null) => true,
                };
            }
            if (shouldReplace)
            {
                result[existingIndex] = parsedEvent;
            }
        }
        return result;
    }

    /// <summary>
    /// Joins folded lines. A physical line continues the previous one when its
    /// first character is a space or tab — a whole character, so a space
    /// carrying a combining mark does not count, exactly as in Swift.
    /// </summary>
    private static List<SourceLine> Unfold(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var physicalLines = normalized.Split('\n');
        var logicalLines = new List<SourceLine>(physicalLines.Length);

        for (var offset = 0; offset < physicalLines.Length; offset++)
        {
            var line = physicalLines[offset];
            var startsWithFold = line.Length > 0
                && line[0] is ' ' or '\t'
                && SwiftText.ElementLength(line, 0) == 1;
            if (startsWithFold && logicalLines.Count > 0)
            {
                logicalLines[^1].Text += line[1..];
            }
            else
            {
                logicalLines.Add(new SourceLine(line, offset + 1));
            }
        }
        return logicalLines;
    }

    private static ContentLine? ParseContentLine(string line, int sourceLine)
    {
        if (line.Length == 0 || FirstUnquotedColon(line) is not { } colonIndex)
        {
            return null;
        }

        var header = line[..colonIndex];
        var value = line[(colonIndex + 1)..];
        var headerParts = SplitUnquoted(header, ';');
        var name = SwiftText.Uppercased(SwiftText.Trimmed(headerParts[0]));
        if (name.Length == 0)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in headerParts.Skip(1))
        {
            if (FirstCharacter(part, '=') is not { } equalsIndex)
            {
                continue;
            }
            var key = SwiftText.Uppercased(SwiftText.Trimmed(part[..equalsIndex]));
            var parameterValue = SwiftText.Trimmed(part[(equalsIndex + 1)..]);
            parameters[key] = DecodeParameterText(StripQuotes(parameterValue));
        }

        return new ContentLine(name, parameters, value, sourceLine);
    }

    /// <summary>Removes one pair of surrounding double quotes, when the value is at least two characters.</summary>
    private static string StripQuotes(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }
        var boundaries = SwiftText.CharacterBoundaries(value);
        var characterCount = boundaries.Count - 1;
        var firstIsQuote = boundaries[1] == 1;
        var lastIsQuote = boundaries[^2] == value.Length - 1;
        return characterCount >= 2 && firstIsQuote && lastIsQuote ? value[1..^1] : value;
    }

    private static int? FirstUnquotedColon(string value)
    {
        var isQuoted = false;
        var isEscaped = false;
        for (var index = 0; index < value.Length;)
        {
            var length = SwiftText.ElementLength(value, index);
            if (isEscaped)
            {
                isEscaped = false;
            }
            else if (SwiftText.IsCharacter(value, index, length, '\\'))
            {
                isEscaped = true;
            }
            else if (SwiftText.IsCharacter(value, index, length, '"'))
            {
                isQuoted = !isQuoted;
            }
            else if (SwiftText.IsCharacter(value, index, length, ':') && !isQuoted)
            {
                return index;
            }
            index += length;
        }
        return null;
    }

    private static List<string> SplitUnquoted(string value, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var isQuoted = false;
        var isEscaped = false;
        for (var index = 0; index < value.Length;)
        {
            var length = SwiftText.ElementLength(value, index);
            if (isEscaped)
            {
                current.Append(value, index, length);
                isEscaped = false;
            }
            else if (SwiftText.IsCharacter(value, index, length, '\\'))
            {
                current.Append(value, index, length);
                isEscaped = true;
            }
            else if (SwiftText.IsCharacter(value, index, length, '"'))
            {
                current.Append(value, index, length);
                isQuoted = !isQuoted;
            }
            else if (SwiftText.IsCharacter(value, index, length, separator) && !isQuoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(value, index, length);
            }
            index += length;
        }
        result.Add(current.ToString());
        return result;
    }

    private static int? FirstCharacter(string value, char target)
    {
        for (var index = 0; index < value.Length;)
        {
            var length = SwiftText.ElementLength(value, index);
            if (SwiftText.IsCharacter(value, index, length, target))
            {
                return index;
            }
            index += length;
        }
        return null;
    }

    /// <summary>RFC 6868 parameter escapes: ^^, ^n and ^'.</summary>
    private static string DecodeParameterText(string value)
    {
        if (!value.Contains('^'))
        {
            return value;
        }
        var decoded = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var length = SwiftText.ElementLength(value, index);
            if (!SwiftText.IsCharacter(value, index, length, '^'))
            {
                decoded.Append(value, index, length);
                index += length;
                continue;
            }

            var next = index + length;
            if (next >= value.Length)
            {
                decoded.Append('^');
                break;
            }
            var nextLength = SwiftText.ElementLength(value, next);
            if (SwiftText.IsCharacter(value, next, nextLength, '^'))
            {
                decoded.Append('^');
            }
            else if (SwiftText.IsCharacter(value, next, nextLength, 'n') || SwiftText.IsCharacter(value, next, nextLength, 'N'))
            {
                decoded.Append('\n');
            }
            else if (SwiftText.IsCharacter(value, next, nextLength, '\''))
            {
                decoded.Append('"');
            }
            else
            {
                decoded.Append('^');
                decoded.Append(value, next, nextLength);
            }
            index = next + nextLength;
        }
        return decoded.ToString();
    }

    /// <summary>RFC 5545 text escapes. An unknown escape keeps the escaped character, to be liberal with older exporters.</summary>
    private static string DecodeText(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }
        var decoded = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var length = SwiftText.ElementLength(value, index);
            if (!SwiftText.IsCharacter(value, index, length, '\\'))
            {
                decoded.Append(value, index, length);
                index += length;
                continue;
            }
            var next = index + length;
            if (next >= value.Length)
            {
                decoded.Append('\\');
                break;
            }
            var nextLength = SwiftText.ElementLength(value, next);
            if (SwiftText.IsCharacter(value, next, nextLength, 'n') || SwiftText.IsCharacter(value, next, nextLength, 'N'))
            {
                decoded.Append('\n');
            }
            else
            {
                decoded.Append(value, next, nextLength);
            }
            index = next + nextLength;
        }
        return decoded.ToString();
    }

    private static ContentLine? FirstLine(string name, List<ContentLine> lines)
        => lines.FirstOrDefault(line => line.Name == name);

    private static string? FirstValue(string name, List<ContentLine> lines)
    {
        if (FirstLine(name, lines)?.Value is not { } rawValue)
        {
            return null;
        }
        var decoded = SwiftText.Trimmed(DecodeText(rawValue));
        return decoded.Length == 0 ? null : decoded;
    }

    private static EventConstructionResult MakeEvent(
        List<ContentLine> lines,
        TimeZoneInfo defaultTimeZone,
        IReadOnlyDictionary<string, ResolvedTimeZone> timeZoneAliases,
        bool calendarMethodIsCancellation)
    {
        var uid = FirstValue("UID", lines);
        var status = FirstValue("STATUS", lines);
        if (calendarMethodIsCancellation || (status is not null && SwiftText.EqualsAsciiIgnoringCase(status, "CANCELLED")))
        {
            return new EventConstructionResult(null, [], uid);
        }

        if (FirstLine("DTSTART", lines) is not { } startLine)
        {
            return new EventConstructionResult(
                null,
                [new IcsParseWarning { EventUid = uid, Line = lines.Count > 0 ? lines[0].SourceLine : null, Message = "An event without DTSTART was ignored." }],
                null);
        }

        ParsedDate parsedStart;
        try
        {
            parsedStart = ParseDate(startLine, defaultTimeZone, timeZoneAliases);
        }
        catch (DateParsingException error)
        {
            return new EventConstructionResult(
                null,
                [new IcsParseWarning { EventUid = uid, Line = startLine.SourceLine, Message = $"DTSTART could not be interpreted: {error.Message}" }],
                null);
        }

        var warnings = new List<IcsParseWarning>();
        DateTimeOffset? endDate = null;
        if (FirstLine("DTEND", lines) is { } endLine)
        {
            try
            {
                endDate = ParseDate(endLine, defaultTimeZone, timeZoneAliases).Date;
            }
            catch (DateParsingException)
            {
                warnings.Add(new IcsParseWarning
                {
                    EventUid = uid,
                    Line = endLine.SourceLine,
                    Message = "DTEND was ignored because it could not be interpreted.",
                });
            }
        }

        DateTimeOffset? lastModified = null;
        if (FirstLine("LAST-MODIFIED", lines) is { } modifiedLine)
        {
            try
            {
                lastModified = ParseDate(modifiedLine, defaultTimeZone, timeZoneAliases).Date;
            }
            catch (DateParsingException)
            {
                warnings.Add(new IcsParseWarning
                {
                    EventUid = uid,
                    Line = modifiedLine.SourceLine,
                    Message = "LAST-MODIFIED was ignored because it could not be interpreted.",
                });
            }
        }

        var parsedEvent = new ParsedCalendarEvent
        {
            Uid = uid,
            Summary = FirstValue("SUMMARY", lines) ?? "Untitled event",
            StartDate = parsedStart.Date,
            EndDate = endDate,
            Description = FirstValue("DESCRIPTION", lines),
            Url = FirstValue("URL", lines),
            LastModified = lastModified,
            IsAllDay = parsedStart.IsAllDay,
            TimeZoneIdentifier = parsedStart.TimeZoneIdentifier,
            Sequence = FirstValue("SEQUENCE", lines) is { } sequence ? SwiftText.ParseInteger(sequence) : null,
        };
        return new EventConstructionResult(parsedEvent, warnings, null);
    }

    private static ParsedDate ParseDate(
        ContentLine line,
        TimeZoneInfo defaultTimeZone,
        IReadOnlyDictionary<string, ResolvedTimeZone> aliases)
    {
        var rawValue = SwiftText.Trimmed(line.Value);
        var valueKind = line.Parameters.TryGetValue("VALUE", out var kind) ? SwiftText.Uppercased(kind) : null;
        if (valueKind == "DATE" || IsEightNumbers(rawValue))
        {
            var date = MakeDateOnly(rawValue, defaultTimeZone) ?? throw DateParsingException.InvalidValue(rawValue);
            return new ParsedDate(date, IsAllDay: true, TimeZoneIdentifier: null);
        }

        line.Parameters.TryGetValue("TZID", out var explicitTimeZoneId);
        TimeZoneInfo timeZone;
        string? exposedTimeZoneIdentifier;
        var dateValue = rawValue;

        if (EndsWithZ(rawValue))
        {
            dateValue = rawValue[..^1];
            timeZone = TimeZoneInfo.Utc;
            exposedTimeZoneIdentifier = "UTC";
        }
        else if (explicitTimeZoneId is not null)
        {
            var resolved = IcsTimeZones.Resolve(explicitTimeZoneId, aliases)
                ?? throw DateParsingException.UnknownTimeZone(explicitTimeZoneId);
            timeZone = resolved.Zone;
            exposedTimeZoneIdentifier = resolved.Identifier;
        }
        else if (HasNumericUtcOffset(rawValue))
        {
            var parsed = IcsDateFormat.ParseDateWithOffset(rawValue) ?? throw DateParsingException.InvalidValue(rawValue);
            return new ParsedDate(parsed, IsAllDay: false, TimeZoneIdentifier: null);
        }
        else
        {
            timeZone = defaultTimeZone;
            exposedTimeZoneIdentifier = null;
        }

        var dateTime = IcsDateFormat.ParseLocalDateTime(dateValue, timeZone) ?? throw DateParsingException.InvalidValue(rawValue);
        return new ParsedDate(dateTime, IsAllDay: false, exposedTimeZoneIdentifier);
    }

    /// <summary>Eight characters that are all numbers: a DATE written without VALUE=DATE.</summary>
    private static bool IsEightNumbers(string value)
    {
        var boundaries = SwiftText.CharacterBoundaries(value);
        if (boundaries.Count - 1 != 8)
        {
            return false;
        }
        for (var index = 0; index < 8; index++)
        {
            if (!SwiftText.IsNumber(value, boundaries[index]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><c>rawValue.uppercased().hasSuffix("Z")</c>: the last whole character is z or Z.</summary>
    private static bool EndsWithZ(string value)
        => value.Length > 0 && value[^1] is 'z' or 'Z' && SwiftText.CharacterBoundaries(value)[^2] == value.Length - 1;

    /// <summary>The last five characters are a sign and four numbers, as in "+1000".</summary>
    private static bool HasNumericUtcOffset(string value)
    {
        var boundaries = SwiftText.CharacterBoundaries(value);
        var count = boundaries.Count - 1;
        if (count < 5)
        {
            return false;
        }
        var signStart = boundaries[count - 5];
        if (boundaries[count - 4] - signStart != 1 || value[signStart] is not ('+' or '-'))
        {
            return false;
        }
        for (var index = count - 4; index < count; index++)
        {
            if (!SwiftText.IsNumber(value, boundaries[index]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A DATE value: the start of that day in the given zone, which on a day
    /// whose midnight is skipped is its first real moment. The year, month and
    /// day are read as Swift's <c>Int(_:)</c> reads them, and must name a real
    /// date.
    /// </summary>
    private static DateTimeOffset? MakeDateOnly(string value, TimeZoneInfo timeZone)
    {
        var boundaries = SwiftText.CharacterBoundaries(value);
        if (boundaries.Count - 1 != 8)
        {
            return null;
        }
        var year = SwiftText.ParseInteger(value[boundaries[0]..boundaries[4]]);
        var month = SwiftText.ParseInteger(value[boundaries[4]..boundaries[6]]);
        var day = SwiftText.ParseInteger(value[boundaries[6]..boundaries[8]]);
        if (year is not (>= 1 and <= 9999) || month is not (>= 1 and <= 12) || day is not >= 1
            || day > DateTime.DaysInMonth((int)year.Value, (int)month.Value))
        {
            return null;
        }
        var date = new DateOnly((int)year.Value, (int)month.Value, (int)day.Value);
        try
        {
            return new AppCalendar(timeZone).StartOfDay(date).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            // The first day of year 1 east of UTC starts before .NET's first instant.
            return null;
        }
    }

    /// <summary>X-WR-TIMEZONE: the zone a calendar asks its floating times to be read in.</summary>
    private static ResolvedTimeZone? CalendarTimeZone(List<ContentLine> lines, IReadOnlyDictionary<string, ResolvedTimeZone> aliases)
    {
        var identifier = lines.FirstOrDefault(line => line.Name == "X-WR-TIMEZONE")?.Value;
        return identifier is null ? null : IcsTimeZones.Resolve(SwiftText.Trimmed(identifier), aliases);
    }

    /// <summary>
    /// The names the calendar's own VTIMEZONE blocks define, each mapped to a
    /// real zone through its X-LIC-LOCATION or, failing that, its own TZID. A
    /// block that resolves to nothing defines nothing.
    /// </summary>
    private static Dictionary<string, ResolvedTimeZone> TimeZoneAliases(List<ContentLine> lines)
    {
        var result = new Dictionary<string, ResolvedTimeZone>(SwiftText.CanonicalComparer);
        string? currentTzid = null;
        string? location = null;
        var depth = 0;

        void SaveCurrentAlias()
        {
            if (currentTzid is not { } alias)
            {
                return;
            }
            var zone = (location is not null ? IcsTimeZones.ResolveKnown(location) : null) ?? IcsTimeZones.ResolveKnown(alias);
            if (zone is not null)
            {
                result[alias] = zone;
                result[SwiftText.Lowercased(alias)] = zone;
            }
        }

        foreach (var line in lines)
        {
            if (line.Name == "BEGIN" && ComponentName(line) == "VTIMEZONE")
            {
                currentTzid = null;
                location = null;
                depth = 1;
                continue;
            }
            if (depth <= 0)
            {
                continue;
            }
            if (line.Name == "BEGIN")
            {
                depth++;
            }
            else if (line.Name == "END")
            {
                depth--;
                if (depth == 0)
                {
                    SaveCurrentAlias();
                    currentTzid = null;
                    location = null;
                }
            }
            else if (depth == 1 && line.Name == "TZID")
            {
                currentTzid = SwiftText.Trimmed(DecodeText(line.Value));
            }
            else if (depth == 1 && line.Name == "X-LIC-LOCATION")
            {
                location = SwiftText.Trimmed(DecodeText(line.Value));
            }
        }
        return result;
    }
}

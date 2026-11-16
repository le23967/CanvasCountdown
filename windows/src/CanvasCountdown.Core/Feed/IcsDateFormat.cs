namespace CanvasCountdown;

/// <summary>
/// Reads iCalendar date-times exactly as the macOS app's strict
/// <c>DateFormatter</c> did.
/// </summary>
/// <remarks>
/// The Swift parser tries the patterns <c>yyyyMMdd'T'HHmmss.SSS</c>,
/// <c>yyyyMMdd'T'HHmmss</c> and <c>yyyyMMdd'T'HHmm</c> (and the same with a
/// trailing <c>Z</c> offset field) through a <c>DateFormatter</c> with
/// <c>isLenient = false</c>. What that accepts is ICU's behaviour, not the
/// pattern's appearance, and the parts that decide whether a deadline is kept
/// or dropped are:
/// <list type="bullet">
/// <item>A wall-clock time the clocks skip (02:30 on the night they jump from
/// 02:00 to 03:00) is rejected, so the event is dropped with a warning, not
/// moved. A time that happens twice is its <em>later</em> occurrence.</item>
/// <item>Field values are range-checked: no 24:00, no 30 February, no
/// minute 60.</item>
/// <item>Digits may be from any script's decimal set in the Basic
/// Multilingual Plane, white space may precede any field (but not a literal
/// such as the <c>T</c>), and only Foundation's non-newline white space may
/// follow the value.</item>
/// <item>Fractional seconds take any number of digits and keep milliseconds,
/// with ICU's 32-bit arithmetic for very long runs.</item>
/// <item>ICU parses a run of adjacent numeric fields (<c>yyyyMMdd</c>,
/// <c>HHmmss</c>) by trying the run with each field at its full width and, if
/// that fails, shortening only the first field: so "T235" reads as 02:35 and
/// a six-digit date as a two-digit year, which Apple places in the hundred
/// years from 1950.</item>
/// <item>A field that fails at the very start of the text reports position 0,
/// which ICU cannot tell from success, and a literal at the end of the text
/// matches nothing; Apple fills unset fields from 2000-01-01 00:00. So an
/// empty value reads as midnight on 1 January 2000 in the zone. This matters
/// beyond the date itself: an event that parses counts as present in the
/// feed, and one that does not is eventually archived.</item>
/// </list>
/// Each of these was confirmed against the Swift implementation, and the
/// tests pin them. Dates before the 1582 Gregorian reform, where Foundation
/// switches to the Julian calendar, are read here as proleptic Gregorian.
/// </remarks>
internal static class IcsDateFormat
{
    private enum Part
    {
        Year,
        Month,
        Day,
        Hour,
        Minute,
        Second,
        Fraction,
        Offset,
        Literal,
    }

    private readonly record struct Element(Part Part, int Width, char Literal = '\0')
    {
        public bool IsNumeric => Part is not (Part.Offset or Part.Literal);

        public static Element Field(Part part, int width) => new(part, width);

        public static Element Text(char literal) => new(Part.Literal, 1, literal);
    }

    private static readonly Element[] DatePart =
    [
        Element.Field(Part.Year, 4), Element.Field(Part.Month, 2), Element.Field(Part.Day, 2), Element.Text('T'),
        Element.Field(Part.Hour, 2), Element.Field(Part.Minute, 2),
    ];

    private static readonly Element[][] LocalPatterns =
    [
        [.. DatePart, Element.Field(Part.Second, 2), Element.Text('.'), Element.Field(Part.Fraction, 3)],
        [.. DatePart, Element.Field(Part.Second, 2)],
        DatePart,
    ];

    private static readonly Element[][] OffsetPatterns =
    [
        [.. DatePart, Element.Field(Part.Second, 2), Element.Field(Part.Offset, 1)],
        [.. DatePart, Element.Field(Part.Offset, 1)],
    ];

    /// <summary>The start of Apple's two-digit-year window, its <c>twoDigitStartDate</c> default.</summary>
    private static readonly DateTimeOffset TwoDigitStartDate = new(1950, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int MaxOffsetHour = 23;
    private const int MaxOffsetMinute = 59;
    private const int MaxOffsetSecond = 59;

    /// <summary>The Swift parser's <c>parseLocalDateTime</c>: a floating or zoned wall-clock time.</summary>
    public static DateTimeOffset? ParseLocalDateTime(string value, TimeZoneInfo timeZone)
    {
        foreach (var pattern in LocalPatterns)
        {
            if (Parse(value, pattern, timeZone) is { } date)
            {
                return date;
            }
        }
        return null;
    }

    /// <summary>The Swift parser's <c>parseDateWithOffset</c>: a time carrying its own numeric offset.</summary>
    public static DateTimeOffset? ParseDateWithOffset(string value)
    {
        foreach (var pattern in OffsetPatterns)
        {
            if (Parse(value, pattern, TimeZoneInfo.Utc) is { } date)
            {
                return date;
            }
        }
        return null;
    }

    private struct Fields
    {
        public int Year;
        public int Month;
        public int Day;
        public int Hour;
        public int Minute;
        public int Second;
        public int Millisecond;
        public bool AmbiguousYear;
        public TimeSpan? Offset;
    }

    private static DateTimeOffset? Parse(string text, Element[] pattern, TimeZoneInfo formatterZone)
    {
        // The window's first year as the formatter's own zone sees that
        // instant: 1949 in New York, 1950 in Sydney.
        var centuryStartYear = TimeZoneInfo.ConvertTime(TwoDigitStartDate, formatterZone).Year;
        var fields = new Fields { Year = 2000, Month = 1, Day = 1 };

        var position = 0;
        var runStart = -1;
        var runTextStart = 0;
        var runPass = 0;

        for (var index = 0; index < pattern.Length; index++)
        {
            var element = pattern[index];
            if (element.IsNumeric)
            {
                if (runStart < 0 && index + 1 < pattern.Length && pattern[index + 1].IsNumeric)
                {
                    runStart = index;
                    runTextStart = position;
                    runPass = 0;
                }
            }
            else
            {
                runStart = -1;
            }

            if (element.Part == Part.Literal)
            {
                if (position >= text.Length)
                {
                    continue;
                }
                if (text[position] != element.Literal)
                {
                    return null;
                }
                position++;
                continue;
            }

            if (runStart >= 0)
            {
                var count = element.Width;
                if (index == runStart)
                {
                    count -= runPass++;
                    if (count == 0)
                    {
                        return null;
                    }
                }
                var next = ParseNumber(text, position, element.Part, count, obeyCount: true, centuryStartYear, ref fields);
                if (next < 0)
                {
                    // Anything failing inside the run retries the whole run
                    // with its first field one digit shorter.
                    index = runStart - 1;
                    position = runTextStart;
                    continue;
                }
                position = next;
            }
            else
            {
                var next = element.Part == Part.Offset
                    ? ParseOffset(text, position, ref fields)
                    : ParseNumber(text, position, element.Part, element.Width, obeyCount: false, centuryStartYear, ref fields);
                if (next <= 0)
                {
                    return null;
                }
                position = next;
            }
        }

        for (; position < text.Length; position++)
        {
            if (!SwiftText.IsWhitespace(text[position]))
            {
                return null;
            }
        }

        var zone = fields.Offset is null ? formatterZone : null;
        var instant = Instant(fields, zone, fields.Offset);
        if (instant is null)
        {
            return null;
        }
        if (fields.AmbiguousYear && instant < TwoDigitStartDate)
        {
            fields.Year = centuryStartYear + 100;
            instant = Instant(fields, zone, fields.Offset);
        }
        return instant;
    }

    /// <summary>
    /// One numeric field, as ICU's <c>subParse</c> reads it: white space first,
    /// then digits. Inside a run each field must use exactly its width.
    /// Answers the position after it, or on failure the negated position it
    /// failed at — which is 0, indistinguishable from success, at the start.
    /// </summary>
    private static int ParseNumber(
        string text,
        int start,
        Part part,
        int count,
        bool obeyCount,
        int centuryStartYear,
        ref Fields fields)
    {
        while (true)
        {
            if (start >= text.Length)
            {
                return -start;
            }
            if (!char.IsWhiteSpace(text[start]))
            {
                break;
            }
            start++;
        }

        var limit = obeyCount ? start + count : text.Length;
        if (limit > text.Length)
        {
            return -start;
        }

        var position = start;
        long value = 0;
        while (position < limit && char.IsDigit(text[position]))
        {
            if (value <= int.MaxValue)
            {
                value = (value * 10) + (long)char.GetNumericValue(text[position]);
            }
            position++;
        }
        var digits = position - start;
        if (digits == 0 || (obeyCount && digits != count))
        {
            return -start;
        }

        var number = (int)Math.Min(value, int.MaxValue);
        switch (part)
        {
            case Part.Year:
                if (count < 3 && digits == 2)
                {
                    var ambiguousTwoDigitYear = centuryStartYear % 100;
                    fields.AmbiguousYear = number == ambiguousTwoDigitYear;
                    number += (centuryStartYear / 100 * 100) + (number < ambiguousTwoDigitYear ? 100 : 0);
                }
                fields.Year = number;
                break;
            case Part.Month:
                if (number is < 1 or > 12)
                {
                    return -start;
                }
                fields.Month = number;
                break;
            case Part.Day:
                if (number is < 1 or > 31)
                {
                    return -start;
                }
                fields.Day = number;
                break;
            case Part.Hour:
                fields.Hour = number;
                break;
            case Part.Minute:
                if (number > 59)
                {
                    return -start;
                }
                fields.Minute = number;
                break;
            case Part.Second:
                if (number > 59)
                {
                    return -start;
                }
                fields.Second = number;
                break;
            case Part.Fraction:
                fields.Millisecond = Milliseconds(number, digits);
                break;
        }
        return position;
    }

    /// <summary>
    /// ICU left-justifies fractional seconds into milliseconds in 32-bit
    /// integers: the parsed number is capped at <see cref="int.MaxValue"/> and
    /// divided by a power of ten that is allowed to overflow, which is why
    /// twenty nines read as one millisecond.
    /// </summary>
    private static int Milliseconds(int value, int digits)
    {
        if (digits < 3)
        {
            for (; digits < 3; digits++)
            {
                value *= 10;
            }
            return value;
        }
        var divisor = 1;
        for (; digits > 3; digits--)
        {
            divisor = unchecked(divisor * 10);
        }
        return divisor == 0 ? 0 : value / divisor;
    }

    /// <summary>
    /// The <c>Z</c> field, as ICU's ISO 8601 offset reader takes it: a sign,
    /// then either <c>hh:mm[:ss]</c> or abutting ASCII digits, keeping
    /// whichever reading goes further.
    /// </summary>
    private static int ParseOffset(string text, int start, ref Fields fields)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        if (start >= text.Length || text[start] is not ('+' or '-'))
        {
            return -start;
        }
        var sign = text[start] == '-' ? -1 : 1;

        var extendedEnd = ParseExtendedOffset(text, start + 1, out var extended);
        if (extendedEnd < 0)
        {
            return -start;
        }
        var end = extendedEnd;
        var seconds = extended;
        if (extendedEnd - start <= 3)
        {
            var basicEnd = ParseBasicOffset(text, start + 1, out var basic);
            if (basicEnd > extendedEnd)
            {
                end = basicEnd;
                seconds = basic;
            }
        }
        fields.Offset = TimeSpan.FromSeconds(sign * seconds);
        return end;
    }

    /// <summary>ICU's <c>parseAsciiOffsetFields</c> with a colon: hours of one or two digits, then <c>:mm</c> and <c>:ss</c>.</summary>
    private static int ParseExtendedOffset(string text, int start, out int seconds)
    {
        seconds = 0;
        Span<int> values = [0, 0, 0];
        Span<int> lengths = [0, -1, -1];
        var field = 0;
        for (var index = start; index < text.Length && field <= 2; index++)
        {
            var character = text[index];
            if (character == ':')
            {
                if (field == 0)
                {
                    if (lengths[0] == 0)
                    {
                        break;
                    }
                }
                else
                {
                    if (lengths[field] != -1)
                    {
                        break;
                    }
                    lengths[field] = 0;
                    continue;
                }
                // A one-digit hour followed by its colon.
                field = 1;
                lengths[1] = 0;
                continue;
            }
            if (lengths[field] == -1)
            {
                break;
            }
            if (character is < '0' or > '9')
            {
                break;
            }
            values[field] = (values[field] * 10) + (character - '0');
            lengths[field]++;
            if (lengths[field] >= 2)
            {
                field++;
            }
        }

        if (lengths[0] == 0)
        {
            return -1;
        }
        var parsedLength = lengths[0];
        var hour = values[0];
        var minute = 0;
        var second = 0;
        if (lengths[1] == 2)
        {
            minute = values[1];
            parsedLength += 3;
            if (lengths[2] == 2)
            {
                second = values[2];
                parsedLength += 3;
            }
        }
        if (hour > MaxOffsetHour || minute > MaxOffsetMinute || second > MaxOffsetSecond)
        {
            return -1;
        }
        seconds = (((hour * 60) + minute) * 60) + second;
        return start + parsedLength;
    }

    /// <summary>ICU's <c>parseAbuttingAsciiOffsetFields</c>: up to six digits, the longest valid H, HH, Hmm, HHmm, Hmmss or HHmmss.</summary>
    private static int ParseBasicOffset(string text, int start, out int seconds)
    {
        seconds = 0;
        Span<int> digits = stackalloc int[6];
        var count = 0;
        for (var index = start; count < 6 && index < text.Length && text[index] is >= '0' and <= '9'; index++)
        {
            digits[count++] = text[index] - '0';
        }
        for (; count >= 1; count--)
        {
            var (hour, minute, second) = count switch
            {
                1 => (digits[0], 0, 0),
                2 => ((digits[0] * 10) + digits[1], 0, 0),
                3 => (digits[0], (digits[1] * 10) + digits[2], 0),
                4 => ((digits[0] * 10) + digits[1], (digits[2] * 10) + digits[3], 0),
                5 => (digits[0], (digits[1] * 10) + digits[2], (digits[3] * 10) + digits[4]),
                _ => ((digits[0] * 10) + digits[1], (digits[2] * 10) + digits[3], (digits[4] * 10) + digits[5]),
            };
            if (hour <= MaxOffsetHour && minute <= MaxOffsetMinute && second <= MaxOffsetSecond)
            {
                seconds = (((hour * 60) + minute) * 60) + second;
                return start + count;
            }
        }
        return -1;
    }

    /// <summary>
    /// The instant the fields name, validated as ICU's strict calendar does. A
    /// skipped wall-clock time is an error; a repeated one is the later of its
    /// two instants. An instant .NET cannot represent (before year 1 in UTC)
    /// is treated as unreadable.
    /// </summary>
    private static DateTimeOffset? Instant(Fields fields, TimeZoneInfo? zone, TimeSpan? offset)
    {
        if (fields.Year is < 1 or > 9999
            || fields.Day > DateTime.DaysInMonth(fields.Year, fields.Month)
            || fields.Hour > 23
            || fields.Millisecond is < 0 or > 999)
        {
            return null;
        }
        var wallClock = new DateTime(
            fields.Year, fields.Month, fields.Day, fields.Hour, fields.Minute, fields.Second, fields.Millisecond, DateTimeKind.Unspecified);

        TimeSpan utcOffset;
        if (offset is { } explicitOffset)
        {
            utcOffset = explicitOffset;
        }
        else
        {
            if (zone!.IsInvalidTime(wallClock))
            {
                return null;
            }
            utcOffset = zone.IsAmbiguousTime(wallClock)
                ? zone.GetAmbiguousTimeOffsets(wallClock).Min()
                : zone.GetUtcOffset(wallClock);
        }

        var utcTicks = wallClock.Ticks - utcOffset.Ticks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
        {
            return null;
        }
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }
}

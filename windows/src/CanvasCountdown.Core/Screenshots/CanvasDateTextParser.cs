using System.Globalization;
using System.Text.RegularExpressions;

namespace CanvasCountdown;

/// <summary>
/// The result of reading one date phrase, with everything the review screen
/// needs to explain what happened.
/// </summary>
public sealed record ParsedScreenshotDate
{
    public required DateTimeOffset Date { get; init; }

    public required ValueList<ScreenshotImportWarning> Warnings { get; init; }

    public int? InferredYear { get; init; }

    /// <summary>The phrase after correction, when correcting changed it. Recognised text: review only.</summary>
    public string? CorrectedText { get; init; }

    public required bool HasExplicitTime { get; init; }

    public override string ToString()
        => $"{nameof(ParsedScreenshotDate)} {{ {nameof(Date)} = {Date:O}, {nameof(HasExplicitTime)} = {HasExplicitTime} }}";
}

/// <summary>
/// Reads Canvas date phrases out of recognised text.
/// </summary>
/// <remarks>
/// <para>
/// Written for OCR output rather than clean strings, but the corrections are
/// deliberately narrow: only substitutions that are unambiguous in a date are
/// applied, and each one is reported. Broad fuzzy matching would quietly turn a
/// misread into a confident wrong deadline, which is worse than asking.
/// </para>
/// <para>
/// Nothing here depends on the machine's language or time zone. Month names
/// are English whatever the system language; matching folds case by invariant
/// rules and reads only ASCII digits as numbers; and the zone a date is read in
/// is the <see cref="AppCalendar"/> passed in.
/// </para>
/// </remarks>
public static class CanvasDateTextParser
{
    private const RegexOptions Matching = RegexOptions.CultureInvariant;

    private const RegexOptions MatchingAnyCase = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>
    /// English month names are always accepted, whatever the system language,
    /// because Canvas renders them in English for most institutions.
    /// </summary>
    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["jan"] = 1, ["january"] = 1,
        ["feb"] = 2, ["february"] = 2,
        ["mar"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4,
        ["may"] = 5,
        ["jun"] = 6, ["june"] = 6,
        ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    /// <summary>Phrases that introduce a real deadline.</summary>
    public static ValueList<string> DueMarkers { get; } = ["due"];

    /// <summary>Phrases that introduce an availability window. These are never deadlines.</summary>
    public static ValueList<string> AvailabilityMarkers { get; } =
    [
        "not available until",
        "available until",
        "available from",
        "available after",
        "availability",
        "opens",
    ];

    private static readonly Regex WhitespaceRun = new(@"\s+", Matching);

    private static readonly Regex[] DueMarkerPatterns = DueMarkers
        .Select(marker => new Regex($@"\b{Regex.Escape(marker)}\b", Matching))
        .ToArray();

    private static readonly Regex OriginalTimePattern = new(@"\d{1,2}\s*[:.]\s*[0-9oOlI]{2}\s*(am|pm)?", MatchingAnyCase);

    private static readonly Regex MisreadAt = new(@"\b(al|af|ai)\b(?=\s*\d)", Matching);

    private static readonly Regex LetterOBetweenDigits = new(@"(?<=\d)[o](?=\d)", MatchingAnyCase);

    private static readonly Regex LetterOAfterSeparator = new(@"(?<=\d[:.])[o](?=\d)", MatchingAnyCase);

    private static readonly Regex LetterOBeforeSeparator = new(@"(?<=\d)[o](?=[:.]\d)", MatchingAnyCase);

    private static readonly Regex FullStopBetweenHourAndMinute = new(@"(?<=\d)\.(?=\d\d\b)", Matching);

    private static readonly Regex ColonTime = new(@"(\d{1,2})\s*[:]\s*(\d{2})\s*(am|pm)?", MatchingAnyCase);

    private static readonly Regex BareFourDigitTime = new(@"\bat\s+(\d{4})\b", MatchingAnyCase);

    private static readonly Regex HourWithMeridiem = new(@"(\d{1,2})\s*(am|pm)\b", MatchingAnyCase);

    // Longest names first, so "september" is tried before "sep".
    private static readonly string MonthNames = string.Join(
        "|",
        Months.Keys.OrderByDescending(name => name.Length).ThenBy(name => name, StringComparer.Ordinal));

    private static readonly Regex DayThenMonth = new($@"\b(\d{{1,2}})\s+({MonthNames})\b(?:\s+(\d{{4}}))?", MatchingAnyCase);

    private static readonly Regex MonthThenDay = new($@"\b({MonthNames})\s+(\d{{1,2}})\b(?:,?\s+(\d{{4}}))?", MatchingAnyCase);

    public static bool IsAvailabilityLine(string text)
    {
        var lowered = NormalisedForMatching(text);
        return AvailabilityMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }

    public static bool IsDueLine(string text)
    {
        if (IsAvailabilityLine(text))
        {
            return false;
        }
        var lowered = NormalisedForMatching(text);
        return DueMarkerPatterns.Any(pattern => pattern.IsMatch(lowered));
    }

    /// <summary>Collapses OCR spacing so phrase matching is not defeated by "Due  4  Aug".</summary>
    public static string NormalisedForMatching(string text)
        => WhitespaceRun.Replace(text.ToLowerInvariant(), " ").Trim();

    /// <summary>
    /// Parses one phrase such as "Due 4 Aug at 16:00", read in
    /// <paramref name="calendar"/>'s time zone.
    /// </summary>
    /// <remarks>
    /// Returns null when nothing date-like is present; an availability phrase
    /// is refused outright so it can never become a deadline.
    /// </remarks>
    public static ParsedScreenshotDate? Parse(string text, DateTimeOffset now, AppCalendar calendar)
    {
        if (IsAvailabilityLine(text))
        {
            return null;
        }

        var warnings = new List<ScreenshotImportWarning>();
        string? correctedText = null;

        var cleaned = CorrectOcrNoise(text);
        if (cleaned != NormalisedForMatching(text))
        {
            correctedText = cleaned;
        }

        ParsedTime? time;
        var timeResult = ParseTime(cleaned);
        switch (timeResult.Kind)
        {
            case TimeParseKind.None:
                time = null;
                warnings.Add(ScreenshotImportWarning.MissingTime);
                break;
            case TimeParseKind.Invalid:
                // A time was written but cannot exist. Falling back to end of
                // day would invent a deadline the screenshot never showed.
                return null;
            default:
                time = timeResult.Time;
                // Report the substitution using what the screenshot actually said.
                if (OriginalTimeText(text) is { } original
                    && !string.Equals(original, timeResult.Time.Rendered, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(ScreenshotImportWarning.TimeCorrected(original, timeResult.Time.Rendered));
                }
                break;
        }

        int? inferredYear = null;
        (int Year, int Month, int Day)? components = null;

        if (ParseRelativeDay(cleaned, now, calendar) is { } relative)
        {
            components = (relative.Year, relative.Month, relative.Day);
        }
        else if (ParseDayAndMonth(cleaned) is { } absolute)
        {
            if (absolute.Year is { } year)
            {
                components = (year, absolute.Month, absolute.Day);
            }
            else
            {
                var inferred = InferYear(absolute.Day, absolute.Month, now, calendar);
                components = (inferred, absolute.Month, absolute.Day);
                inferredYear = inferred;
                warnings.Add(ScreenshotImportWarning.YearInferred(inferred));
            }
        }

        if (components is not { } parts)
        {
            return null;
        }

        var hour = time?.Hour ?? 23;
        var minute = time?.Minute ?? 59;

        // Rejects 31 February and friends: a rolled-over date is a wrong date.
        if (!IsRealDate(parts.Day, parts.Month, parts.Year, calendar)
            || Instant(calendar, parts.Year, parts.Month, parts.Day, hour, minute) is not { } date)
        {
            return null;
        }

        if (date < now)
        {
            warnings.Add(ScreenshotImportWarning.DateInPast);
        }

        return new ParsedScreenshotDate
        {
            Date = date,
            Warnings = warnings.ToValueList(),
            InferredYear = inferredYear,
            CorrectedText = correctedText,
            HasExplicitTime = time is not null,
        };
    }

    /// <summary>The time exactly as it appeared before any correction, for the warning.</summary>
    public static string? OriginalTimeText(string text)
    {
        var match = OriginalTimePattern.Match(NormalisedForMatching(text));
        return match.Success ? match.Value.Trim() : null;
    }

    /// <summary>Only substitutions that cannot mean anything else inside a date.</summary>
    public static string CorrectOcrNoise(string text)
    {
        var value = NormalisedForMatching(text);
        // "al" and "af" are common misreads of "at" between a date and a time.
        value = MisreadAt.Replace(value, "at");
        // A letter O inside an otherwise numeric time.
        value = LetterOBetweenDigits.Replace(value, "0");
        value = LetterOAfterSeparator.Replace(value, "0");
        value = LetterOBeforeSeparator.Replace(value, "0");
        // A full stop between hour and minute.
        value = FullStopBetweenHourAndMinute.Replace(value, ":");
        return value;
    }

    /// <summary>
    /// Chooses the nearest plausible year, preferring the next twelve months.
    /// The choice is always surfaced as a warning rather than assumed correct.
    /// </summary>
    public static int InferYear(int day, int month, DateTimeOffset now, AppCalendar calendar)
    {
        var currentYear = calendar.DateOf(now).Year;

        DateTimeOffset thisYear;
        try
        {
            // Read leniently, as Foundation reads date components: a day past
            // the end of the month runs on into the next one. Only the parse
            // that follows decides whether the date is real.
            var date = new DateOnly(currentYear, 1, 1).AddMonths(month - 1).AddDays(day - 1);
            thisYear = calendar.FromLocal(date, new TimeOnly(23, 59));
        }
        catch (ArgumentException)
        {
            return currentYear;
        }

        // Anything more than a month behind is far likelier to be next year's
        // deadline than one already missed.
        var monthAgo = calendar.AddDays(now, -31);
        return thisYear < monthAgo ? currentYear + 1 : currentYear;
    }

    public static bool IsRealDate(int day, int month, int year, AppCalendar calendar)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31)
        {
            return false;
        }
        // Years a date can be held in at all.
        if (year is < 1 or > 9999 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }
        try
        {
            // A day the zone skipped outright (Samoa lost 30 December 2011)
            // starts on the next one, and is no more real than 31 February.
            var date = new DateOnly(year, month, day);
            return calendar.DateOf(calendar.StartOfDay(date)) == date;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private enum TimeParseKind
    {
        None,
        Invalid,
        Parsed,
    }

    private readonly record struct ParsedTime(int Hour, int Minute, string Rendered);

    /// <summary>
    /// Distinguishes "no time was written" from "a time was written but is
    /// impossible". The two must not be treated alike: the first falls back to
    /// end of day, the second rejects the whole phrase.
    /// </summary>
    private readonly record struct TimeParseResult(TimeParseKind Kind, ParsedTime Time = default)
    {
        public static TimeParseResult None => new(TimeParseKind.None);

        public static TimeParseResult Invalid => new(TimeParseKind.Invalid);

        public static TimeParseResult Parsed(int hour, int minute)
            => new(TimeParseKind.Parsed, new ParsedTime(hour, minute, Render(hour, minute)));
    }

    private static TimeParseResult ParseTime(string text)
    {
        // 16:00, 4:00pm, 4 pm
        var colon = ColonTime.Match(text);
        if (colon.Success)
        {
            var hour = AsciiNumber(colon.Groups[1].Value) ?? 0;
            var minute = AsciiNumber(colon.Groups[2].Value) ?? 0;
            var meridiem = colon.Groups[3].Value;
            if (ResolveHour(hour, meridiem) is not { } resolved || minute is < 0 or > 59)
            {
                return TimeParseResult.Invalid;
            }
            return TimeParseResult.Parsed(resolved, minute);
        }

        // A bare four-digit time such as "at 1600".
        var bare = BareFourDigitTime.Match(text);
        if (bare.Success)
        {
            var digits = bare.Groups[1].Value;
            var hour = AsciiNumber(digits[..2]) ?? 0;
            var minute = AsciiNumber(digits[^2..]) ?? 0;
            if (hour is < 0 or > 23 || minute is < 0 or > 59)
            {
                return TimeParseResult.Invalid;
            }
            return TimeParseResult.Parsed(hour, minute);
        }

        // "4pm" with no minutes.
        var meridiemOnly = HourWithMeridiem.Match(text);
        if (meridiemOnly.Success)
        {
            var hour = AsciiNumber(meridiemOnly.Groups[1].Value) ?? 0;
            if (ResolveHour(hour, meridiemOnly.Groups[2].Value) is not { } resolved)
            {
                return TimeParseResult.Invalid;
            }
            return TimeParseResult.Parsed(resolved, 0);
        }
        return TimeParseResult.None;
    }

    private static int? ResolveHour(int hour, string meridiem) => meridiem switch
    {
        "am" => hour is >= 1 and <= 12 ? (hour == 12 ? 0 : hour) : null,
        "pm" => hour is >= 1 and <= 12 ? (hour == 12 ? 12 : hour + 12) : null,
        _ => hour is >= 0 and <= 23 ? hour : null,
    };

    private static DateOnly? ParseRelativeDay(string text, DateTimeOffset now, AppCalendar calendar)
    {
        if (text.Contains("today", StringComparison.Ordinal))
        {
            return calendar.DateOf(now);
        }
        if (text.Contains("tomorrow", StringComparison.Ordinal))
        {
            return calendar.DateOf(now).AddDays(1);
        }
        return null;
    }

    private static (int Day, int Month, int? Year)? ParseDayAndMonth(string text)
    {
        // "4 Aug 2026" or "4 Aug"
        var dayFirst = DayThenMonth.Match(text);
        if (dayFirst.Success)
        {
            if (AsciiNumber(dayFirst.Groups[1].Value) is not { } day
                || !Months.TryGetValue(dayFirst.Groups[2].Value, out var month))
            {
                return null;
            }
            return (day, month, AsciiNumber(dayFirst.Groups[3].Value));
        }

        // "Aug 4 2026" or "Aug 4"
        var monthFirst = MonthThenDay.Match(text);
        if (monthFirst.Success)
        {
            if (!Months.TryGetValue(monthFirst.Groups[1].Value, out var month)
                || AsciiNumber(monthFirst.Groups[2].Value) is not { } day)
            {
                return null;
            }
            return (day, month, AsciiNumber(monthFirst.Groups[3].Value));
        }
        return null;
    }

    /// <summary>
    /// A number written in ASCII digits, or null. The patterns match any
    /// decimal digit, as the macOS ones do, and the macOS conversion reads only
    /// ASCII, so a digit from another script reads as no number at all.
    /// </summary>
    private static int? AsciiNumber(string digits)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Render(int hour, int minute)
        => string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00}");

    private static DateTimeOffset? Instant(AppCalendar calendar, int year, int month, int day, int hour, int minute)
    {
        try
        {
            return calendar.FromLocal(year, month, day, hour, minute);
        }
        catch (ArgumentException)
        {
            // The first and last days a date can hold may fall outside the
            // range once the zone's offset is applied, and a zone's earliest
            // local mean time is not a whole number of minutes.
            return null;
        }
    }
}

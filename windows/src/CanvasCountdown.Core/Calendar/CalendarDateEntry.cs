using System.Globalization;
using System.Text;

namespace CanvasCountdown;

/// <summary>
/// Reads a date the user typed into the calendar's date field.
/// </summary>
/// <remarks>
/// Deliberately narrow rather than clever. Everything it accepts resolves to
/// exactly one day, and anything it cannot resolve is refused, because taking a
/// wrong guess here silently moves the calendar somewhere the user did not ask
/// for. Missing pieces come from the day already on screen, never from "the
/// next one that sounds right".
///
/// The culture is always passed in, so what a string means never depends on
/// the language of the machine reading it.
/// </remarks>
public static class CalendarDateEntry
{
    /// <summary>What the field says before anything is typed.</summary>
    public const string EmptyHint = "Type a date, or a month and a day.";

    /// <summary>What the field says when the text is not a day.</summary>
    public const string UnreadableMessage = "Not a date this app can read";

    /// <summary>
    /// Full month names and the abbreviations Canvas itself uses. English is
    /// always accepted whatever the system language, and the culture's own month
    /// names are accepted on top of it.
    /// </summary>
    private static readonly Dictionary<string, int> EnglishMonths = new(StringComparer.Ordinal)
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

    private static readonly char[] NumericSeparators = ['-', '/', '.', ' '];

    /// <summary>The first instant of the day the entry lands on, or null when the text says nothing definite.</summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="reference">The day the calendar is showing, used for the parts the text leaves out.</param>
    /// <param name="calendar">The calendar the day is found in.</param>
    /// <param name="culture">Decides whether "8/9" is a day then a month, and adds its own month names to the English ones.</param>
    /// <param name="now">What "today" means, and which century a two-digit year is in.</param>
    public static DateTimeOffset? Date(
        string text,
        DateTimeOffset reference,
        AppCalendar calendar,
        CultureInfo culture,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Composed, so a letter typed as a base and an accent compares equal
        // to the same letter typed whole, as Swift's strings always compare.
        var cleaned = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);
        if (cleaned.Length == 0)
        {
            return null;
        }
        var formats = CalendarDateFormats.For(culture);

        // Order matters. The culture's own parser is tried last and only
        // against its exact short date pattern: asked to read "2026-13-01" a
        // lenient parser happily returns a day in 2007, and a confident wrong
        // answer is the one failure mode this is written to avoid.
        var match = RelativeDate(cleaned, calendar, now)
            ?? NumericDate(cleaned, reference, calendar, formats, now)
            ?? NamedMonthDate(cleaned, reference, calendar, formats, now)
            ?? LocaleFormatted(cleaned, formats);
        return match is { } day ? CalendarDates.StartOfDay(day, calendar) : null;
    }

    /// <summary>
    /// What the Go to Date field shows under the text, and whether Go can be
    /// pressed.
    /// </summary>
    /// <remarks>
    /// The resolved day is shown before it is committed, because "8/9" means
    /// two different days in two different places and the user should see
    /// which one they are about to get.
    /// </remarks>
    public static CalendarDateEntryPreview Preview(
        string text,
        DateTimeOffset reference,
        AppCalendar calendar,
        CultureInfo culture,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return new CalendarDateEntryPreview { State = CalendarDateEntryState.Empty, Message = EmptyHint };
        }
        if (Date(text, reference, calendar, culture, now) is { } date)
        {
            var formats = CalendarDateFormats.For(culture);
            return new CalendarDateEntryPreview
            {
                State = CalendarDateEntryState.Resolved,
                Date = date,
                Message = formats.Text(calendar.DateOf(date), formats.FullDate),
            };
        }
        return new CalendarDateEntryPreview { State = CalendarDateEntryState.Unreadable, Message = UnreadableMessage };
    }

    private static DateOnly? RelativeDate(string text, AppCalendar calendar, DateTimeOffset now) => text switch
    {
        "today" or "now" => calendar.DateOf(now),
        "tomorrow" => CalendarDates.AddingDays(calendar.DateOf(now), 1),
        "yesterday" => CalendarDates.AddingDays(calendar.DateOf(now), -1),
        _ => null,
    };

    /// <summary>
    /// What the user would get from typing a date the way their own system
    /// writes one.
    /// </summary>
    private static DateOnly? LocaleFormatted(string text, CalendarDateFormats formats)
        => DateTime.TryParseExact(text, formats.Format.ShortDatePattern, formats.Format, DateTimeStyles.None, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;

    /// <summary>
    /// Digits and separators: 2026-08-14, 2026/8/14, 14.8.2026, 14/8, or a bare
    /// day of the month.
    /// </summary>
    private static DateOnly? NumericDate(
        string text,
        DateTimeOffset reference,
        AppCalendar calendar,
        CalendarDateFormats formats,
        DateTimeOffset now)
    {
        var pieces = text.Split(NumericSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0)
        {
            return null;
        }
        var parts = new long[pieces.Length];
        for (var index = 0; index < pieces.Length; index++)
        {
            if (!TryParseInteger(pieces[index], out parts[index]))
            {
                return null;
            }
        }

        var shown = calendar.DateOf(reference);
        switch (parts.Length)
        {
            case 1:
                return Day(parts[0], shown.Month, shown.Year);
            case 2:
            {
                // Two numbers are a day and a month, in whatever order this
                // culture writes them, unless the first is plainly a year.
                if (parts[0] > 31)
                {
                    return Day(1, parts[1], parts[0]);
                }
                var (day, month) = formats.DayComesBeforeMonth ? (parts[0], parts[1]) : (parts[1], parts[0]);
                return Day(day, month, shown.Year);
            }
            case 3:
            {
                if (parts[0] > 31)
                {
                    return Day(parts[2], parts[1], parts[0]);
                }
                var (day, month) = formats.DayComesBeforeMonth ? (parts[0], parts[1]) : (parts[1], parts[0]);
                return Day(day, month, FullYear(parts[2], calendar, now));
            }
            default:
                return null;
        }
    }

    /// <summary>"14 aug", "aug 14", "august 14 2026", "aug 2026".</summary>
    private static DateOnly? NamedMonthDate(
        string text,
        DateTimeOffset reference,
        AppCalendar calendar,
        CalendarDateFormats formats,
        DateTimeOffset now)
    {
        var words = Words(text);
        if (words.Count == 0)
        {
            return null;
        }

        int? month = null;
        var numbers = new List<long>();
        foreach (var word in words)
        {
            if (TryParseInteger(word, out var value))
            {
                numbers.Add(value);
            }
            else if (MonthNumber(word, formats) is { } named)
            {
                month = named;
            }
            else
            {
                return null;
            }
        }

        if (month is not { } monthValue)
        {
            return null;
        }
        long dayValue = 1;
        foreach (var number in numbers)
        {
            if (number <= 31)
            {
                dayValue = number;
                break;
            }
        }
        long yearValue = calendar.DateOf(reference).Year;
        foreach (var number in numbers)
        {
            if (number > 31)
            {
                yearValue = FullYear(number, calendar, now);
                break;
            }
        }
        return Day(dayValue, monthValue, yearValue);
    }

    private static int? MonthNumber(string word, CalendarDateFormats formats)
    {
        if (EnglishMonths.TryGetValue(word, out var english))
        {
            return english;
        }
        // The month as written with a day beside it, its abbreviation, and the
        // month on its own: in some languages those are three different words.
        var names = formats.Format.MonthGenitiveNames.Take(12)
            .Concat(formats.Format.AbbreviatedMonthGenitiveNames.Take(12))
            .Concat(formats.Format.MonthNames.Take(12));
        var index = 0;
        foreach (var name in names)
        {
            var lowered = name.ToLowerInvariant().Normalize(NormalizationForm.FormC);
            if (lowered == word || lowered.StartsWith(word, StringComparison.Ordinal))
            {
                return index % 12 + 1;
            }
            index++;
        }
        return null;
    }

    /// <summary>
    /// Two digits mean this century, which is the only reading that is ever
    /// useful for a deadline.
    /// </summary>
    private static long FullYear(long value, AppCalendar calendar, DateTimeOffset now)
    {
        if (value >= 100)
        {
            return value;
        }
        var century = calendar.DateOf(now).Year / 100 * 100;
        return century + value;
    }

    /// <summary>
    /// Rejects the days that do not exist, so "31 february" is refused rather
    /// than rolled into March — and the years past 9999, which .NET cannot
    /// represent.
    /// </summary>
    private static DateOnly? Day(long day, long month, long year)
    {
        if (month is < 1 or > 12 || day < 1 || year is < 1 or > 9999)
        {
            return null;
        }
        if (day > DateTime.DaysInMonth((int)year, (int)month))
        {
            return null;
        }
        return new DateOnly((int)year, (int)month, (int)day);
    }

    /// <summary>An integer the way Swift's <c>Int(String)</c> reads one: ASCII digits and an optional sign.</summary>
    private static bool TryParseInteger(string text, out long value)
        => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// The runs of letters and digits in the text, split at everything else.
    /// Read a whole character at a time, so an accented letter typed as a
    /// letter and a combining mark stays one letter.
    /// </summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (Rune.DecodeFromUtf16(element, out var rune, out _) == System.Buffers.OperationStatus.Done
                && (Rune.IsLetter(rune) || Rune.IsNumber(rune)))
            {
                current.Append(element);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }
        return words;
    }
}

public enum CalendarDateEntryState
{
    /// <summary>Nothing typed yet.</summary>
    Empty,

    /// <summary>The text names one day.</summary>
    Resolved,

    /// <summary>The text is not a day this app can read.</summary>
    Unreadable,
}

/// <summary>What the Go to Date field shows under what was typed.</summary>
public sealed record CalendarDateEntryPreview
{
    public required CalendarDateEntryState State { get; init; }

    /// <summary>The first instant of the day Go would move to.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>
    /// The hint, the day written out in full ("Friday, August 14, 2026"), or
    /// the refusal.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>Go is offered only when the text names a day.</summary>
    public bool CanGo => Date is not null;
}

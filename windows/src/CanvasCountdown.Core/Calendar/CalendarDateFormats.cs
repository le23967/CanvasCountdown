using System.Globalization;
using System.Text;

namespace CanvasCountdown;

/// <summary>
/// The calendar's date and time text in one culture: the .NET patterns that
/// show the same parts the macOS app asked its formatter for.
/// </summary>
/// <remarks>
/// The Swift built its text from templates ("dMMMy", "yMMMM", "j") that the
/// system turned into each locale's own order. .NET has no template API, so
/// each template is mapped to the culture pattern that carries the same parts:
/// the long date for a full date, the year-month pattern for a month, the
/// long date without its weekday and with a short month name for an
/// abbreviated date, and the short time without its minutes for an hour.
///
/// Always Gregorian. The grids are built in the Gregorian calendar whatever
/// the culture, and a Hijri or Buddhist month name over a Gregorian month of
/// squares would describe a different month from the one on screen.
/// </remarks>
internal sealed class CalendarDateFormats
{
    /// <summary>"y": the year on its own.</summary>
    public const string YearOnly = "yyyy";

    /// <summary>
    /// "MMMM" on its own. With no day beside it .NET writes the standalone
    /// (nominative) form, as the Swift template did: "январь", not "января".
    /// </summary>
    public const string StandaloneMonth = "MMMM";

    private static readonly char[] TrimmedSeparators = [' ', '\u00A0', '\u202F', ',', '\u060C', '\uFF0C', '\u3001'];

    private CalendarDateFormats(DateTimeFormatInfo format)
    {
        Format = format;
    }

    public DateTimeFormatInfo Format { get; }

    public static CalendarDateFormats For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var format = culture.DateTimeFormat;
        if (format.Calendar is GregorianCalendar)
        {
            return new CalendarDateFormats(format);
        }
        var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
        if (gregorian is null)
        {
            return new CalendarDateFormats(CultureInfo.InvariantCulture.DateTimeFormat);
        }
        var copy = (DateTimeFormatInfo)format.Clone();
        copy.Calendar = gregorian;
        return new CalendarDateFormats(copy);
    }

    /// <summary>"EEEEdMMMMy", and the complete date style: "Friday, August 14, 2026".</summary>
    public string FullDate => Format.LongDatePattern;

    /// <summary>"yMMMM": "August 2026".</summary>
    public string MonthAndYear => Format.YearMonthPattern;

    /// <summary>"dMMM": "Aug 14".</summary>
    public string DayAndShortMonth => WithShortMonth(Format.MonthDayPattern);

    /// <summary>"dMMMy", and the abbreviated date style: "Aug 14, 2026".</summary>
    public string ShortDate => WithShortMonth(WithoutWeekday(Format.LongDatePattern));

    /// <summary>Hour and minute in the user's own clock: "4:30 PM", "16:30".</summary>
    public string Time => Format.ShortTimePattern;

    /// <summary>"j": the hour alone in the user's own clock: "4 PM", "16".</summary>
    public string Hour => HourOnly(Format.ShortTimePattern);

    /// <summary>
    /// Whether this culture writes the day before the month in a numeric date,
    /// read from its short date pattern. True when the pattern does not say.
    /// </summary>
    public bool DayComesBeforeMonth
    {
        get
        {
            var tokens = Tokenize(Format.ShortDatePattern);
            var day = tokens.FindIndex(token => token.Field == 'd' && token.Text.Length <= 2);
            var month = tokens.FindIndex(token => token.Field == 'M');
            if (day < 0 || month < 0)
            {
                return true;
            }
            return day < month;
        }
    }

    public string Text(DateTime local, string pattern) => local.ToString(AsCustomPattern(pattern), Format);

    public string Text(DateOnly date, string pattern) => Text(date.ToDateTime(TimeOnly.MinValue), pattern);

    public string AbbreviatedWeekday(DateOnly date) => Format.GetAbbreviatedDayName(date.DayOfWeek);

    internal static string WithoutWeekday(string pattern)
    {
        var tokens = Tokenize(pattern);
        tokens.RemoveAll(token => token.Field == 'd' && token.Text.Length >= 3);
        while (tokens.Count > 0 && IsTrimmedSeparator(tokens[0]))
        {
            tokens.RemoveAt(0);
        }
        while (tokens.Count > 0 && IsTrimmedSeparator(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        return Join(tokens);
    }

    internal static string WithShortMonth(string pattern)
    {
        var tokens = Tokenize(pattern);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Field == 'M' && tokens[index].Text.Length >= 4)
            {
                tokens[index] = new Token("MMM", 'M');
            }
        }
        return Join(tokens);
    }

    internal static string HourOnly(string timePattern)
    {
        var tokens = Tokenize(timePattern);
        var kept = new List<Token>(tokens.Count);
        foreach (var token in tokens)
        {
            if (token.Field is 'm' or 's' or 'f' or 'F')
            {
                if (kept.Count > 0 && kept[^1].Field == '\0' && kept[^1].Text is ":" or ".")
                {
                    kept.RemoveAt(kept.Count - 1);
                }
                continue;
            }
            kept.Add(token);
        }
        while (kept.Count > 0 && kept[0].Field == '\0' && string.IsNullOrWhiteSpace(kept[0].Text))
        {
            kept.RemoveAt(0);
        }
        while (kept.Count > 0 && kept[^1].Field == '\0' && string.IsNullOrWhiteSpace(kept[^1].Text))
        {
            kept.RemoveAt(kept.Count - 1);
        }
        return Join(kept);
    }

    /// <summary>
    /// A pattern of one letter is read by .NET as a standard format ("H" is
    /// not one, and throws), so a single specifier is marked as custom.
    /// </summary>
    private static string AsCustomPattern(string pattern) => pattern.Length == 1 ? "%" + pattern : pattern;

    private static bool IsTrimmedSeparator(Token token)
        => token.Field == '\0' && token.Text.Length == 1 && Array.IndexOf(TrimmedSeparators, token.Text[0]) >= 0;

    private static string Join(List<Token> tokens)
    {
        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            builder.Append(token.Text);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Splits a .NET date pattern into runs of one pattern letter and pieces of
    /// literal text, keeping quoted and escaped text whole so it is never
    /// mistaken for a field.
    /// </summary>
    private static List<Token> Tokenize(string pattern)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < pattern.Length)
        {
            var character = pattern[index];
            if (character is '\'' or '"')
            {
                var end = index + 1;
                while (end < pattern.Length && pattern[end] != character)
                {
                    end += pattern[end] == '\\' ? 2 : 1;
                }
                end = Math.Min(end + 1, pattern.Length);
                tokens.Add(new Token(pattern[index..end], '\0'));
                index = end;
            }
            else if (character == '\\')
            {
                var end = Math.Min(index + 2, pattern.Length);
                tokens.Add(new Token(pattern[index..end], '\0'));
                index = end;
            }
            else if (character == '%')
            {
                index++;
            }
            else if (char.IsAsciiLetter(character))
            {
                var end = index;
                while (end < pattern.Length && pattern[end] == character)
                {
                    end++;
                }
                tokens.Add(new Token(pattern[index..end], character));
                index = end;
            }
            else
            {
                tokens.Add(new Token(character.ToString(), '\0'));
                index++;
            }
        }
        return tokens;
    }

    /// <param name="Field">The pattern letter of a field, or <c>'\0'</c> for literal text.</param>
    private readonly record struct Token(string Text, char Field);
}

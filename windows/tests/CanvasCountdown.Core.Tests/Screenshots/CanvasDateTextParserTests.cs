namespace CanvasCountdown.Tests.Screenshots;

/// <summary>Reading deadlines out of OCR text. Every fixture here is invented.</summary>
public sealed class CanvasDateTextParserTests
{
    private static readonly AppCalendar Calendar = AppCalendar.ForTimeZone("Australia/Sydney");

    private static DateTimeOffset Now(int year, int month, int day) => Calendar.FromLocal(year, month, day, 9, 0);

    private static ParsedScreenshotDate? Parse(string text, DateTimeOffset? reference = null)
        => CanvasDateTextParser.Parse(text, reference ?? Now(2026, 7, 28), Calendar);

    private static ParsedScreenshotDate Parsed(string text, DateTimeOffset? reference = null)
    {
        var result = Parse(text, reference);
        Assert.NotNull(result);
        return result;
    }

    private static DateTime Local(DateTimeOffset instant) => Calendar.ToLocal(instant);

    // Formats

    [Fact]
    public void DayFirstTwentyFourHourFormat()
    {
        var result = Parsed("Due 4 Aug at 16:00");
        var parts = Local(result.Date);

        Assert.Equal(4, parts.Day);
        Assert.Equal(8, parts.Month);
        Assert.Equal(16, parts.Hour);
        Assert.Equal(0, parts.Minute);
        Assert.True(result.HasExplicitTime);
    }

    [Fact]
    public void MonthFirstTwelveHourFormat()
    {
        var result = Parsed("Due Aug 4 at 4:00pm");
        var parts = Local(result.Date);

        Assert.Equal(8, parts.Month);
        Assert.Equal(4, parts.Day);
        Assert.True(parts.Hour == 16, "4:00pm is 16:00");
    }

    [Fact]
    public void MiddayAndMidnightMeridiem()
    {
        var noon = Parsed("Due 4 Aug at 12:00pm");
        var midnight = Parsed("Due 4 Aug at 12:00am");

        Assert.Equal(12, Local(noon.Date).Hour);
        Assert.Equal(0, Local(midnight.Date).Hour);
    }

    [Fact]
    public void ExplicitYearIsUsed()
    {
        var result = Parsed("Due 4 August 2026 at 16:00");

        Assert.Equal(2026, Local(result.Date).Year);
        Assert.Null(result.InferredYear);
        Assert.DoesNotContain(ScreenshotImportWarning.YearInferred(2026), result.Warnings);
    }

    [Fact]
    public void LateDeadlinesInTheSameYear()
    {
        foreach (var (text, month, day) in new[]
        {
            ("Due 30 Aug at 23:59", 8, 30),
            ("Due 6 Sep at 23:59", 9, 6),
            ("Due 25 Oct at 23:59", 10, 25),
        })
        {
            var parts = Local(Parsed(text).Date);
            Assert.Equal((text, month, day, 23, 59), (text, parts.Month, parts.Day, parts.Hour, parts.Minute));
        }
    }

    [Fact]
    public void TodayAndTomorrow()
    {
        var reference = Now(2026, 7, 28);
        var today = Parsed("Due Today at 23:59", reference);
        var tomorrow = Parsed("Due Tomorrow at 16:00", reference);

        Assert.Equal(28, Local(today.Date).Day);
        Assert.Equal(29, Local(tomorrow.Date).Day);
        Assert.Equal(16, Local(tomorrow.Date).Hour);
    }

    // Year inference

    [Fact]
    public void YearIsInferredForwardAndReported()
    {
        // Read in December, a January deadline belongs to the next year.
        var result = Parsed("Due 15 Jan at 23:59", Now(2026, 12, 20));

        Assert.Equal(2027, Local(result.Date).Year);
        Assert.Equal(2027, result.InferredYear);
        Assert.Contains(ScreenshotImportWarning.YearInferred(2027), result.Warnings);
    }

    [Fact]
    public void RecentlyPastDateKeepsThisYearAndIsFlagged()
    {
        var result = Parsed("Due 20 Jul at 23:59", Now(2026, 7, 28));

        Assert.Equal(2026, Local(result.Date).Year);
        Assert.True(
            result.Warnings.Contains(ScreenshotImportWarning.DateInPast),
            "A date already gone must be shown as such, never quietly moved");
    }

    // OCR noise

    [Fact]
    public void AtIsRecoveredFromCommonMisreads()
    {
        var result = Parsed("Due 4 Aug al 16:00");

        Assert.Equal(16, Local(result.Date).Hour);
        Assert.NotNull(result.CorrectedText);
    }

    [Fact]
    public void LetterOInsideATimeBecomesZero()
    {
        var result = Parsed("Due 4 Aug at 16.O0");
        var parts = Local(result.Date);

        Assert.Equal(16, parts.Hour);
        Assert.Equal(0, parts.Minute);
        Assert.True(
            result.Warnings.Any(warning => warning.Kind == ScreenshotImportWarningKind.TimeCorrected),
            "The substitution must be explained, not applied silently");
    }

    [Fact]
    public void BareFourDigitTime()
    {
        var parts = Local(Parsed("Due 4 Aug at 1600").Date);

        Assert.Equal(16, parts.Hour);
        Assert.Equal(0, parts.Minute);
    }

    [Fact]
    public void RepeatedSpacingIsTolerated()
    {
        var result = Parsed("Due  4  Aug  at  16:00");

        Assert.Equal(16, Local(result.Date).Hour);
    }

    [Fact]
    public void MissingTimeFallsBackToEndOfDayAndSaysSo()
    {
        var result = Parsed("Due 4 Aug");
        var parts = Local(result.Date);

        Assert.Equal(23, parts.Hour);
        Assert.Equal(59, parts.Minute);
        Assert.False(result.HasExplicitTime);
        Assert.Contains(ScreenshotImportWarning.MissingTime, result.Warnings);
    }

    // Availability must never become a deadline

    [Fact]
    public void AvailabilityPhrasesAreRefused()
    {
        foreach (var text in new[]
        {
            "Not available until 4 Aug at 14:40",
            "Available from 1 Aug at 09:00",
            "Available until 30 Aug at 23:59",
            "Opens 2 Aug at 08:00",
        })
        {
            Assert.True(Parse(text) is null, $"{text} is an availability window, not a deadline");
            Assert.True(CanvasDateTextParser.IsAvailabilityLine(text), text);
            Assert.False(CanvasDateTextParser.IsDueLine(text), text);
        }
    }

    [Fact]
    public void DueLineIsRecognised()
    {
        Assert.True(CanvasDateTextParser.IsDueLine("Due 4 Aug at 16:00"));
        Assert.True(CanvasDateTextParser.IsDueLine("due today at 23:59"));
        Assert.False(CanvasDateTextParser.IsDueLine("Example Quiz"));
    }

    // Invalid input

    [Fact]
    public void ImpossibleDatesAreRejected()
    {
        Assert.True(Parse("Due 31 Feb at 16:00") is null, "31 February does not exist");
        Assert.True(Parse("Due 4 Aug at 29:00") is null, "hour 29 does not exist");
        Assert.True(Parse("Due 4 Aug at 16:88") is null, "minute 88 does not exist");
        Assert.Null(Parse("Due 32 Aug at 16:00"));
    }

    [Fact]
    public void TextWithoutADateReturnsNothing()
    {
        Assert.Null(Parse("Example Poster Submission"));
        Assert.Null(Parse("10 pts"));
        Assert.Null(Parse(""));
    }

    [Fact]
    public void RealDateValidation()
    {
        Assert.True(CanvasDateTextParser.IsRealDate(29, 2, 2028, Calendar));
        Assert.False(CanvasDateTextParser.IsRealDate(29, 2, 2026, Calendar));
        Assert.False(CanvasDateTextParser.IsRealDate(31, 4, 2026, Calendar));
    }

    [Fact]
    public void DaylightSavingTransitionUsesTheCalendar()
    {
        // Sydney moves its clocks on 4 October 2026.
        var parts = Local(Parsed("Due 4 Oct at 23:59", Now(2026, 9, 1)).Date);

        Assert.Equal(10, parts.Month);
        Assert.Equal(4, parts.Day);
        Assert.Equal(23, parts.Hour);
    }
}

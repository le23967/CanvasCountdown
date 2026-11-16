using System.Globalization;
using static CanvasCountdown.Tests.Calendar.CalendarTestData;

namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// Every shape of date the Swift field accepted, read against an explicit
/// culture so the answer cannot change with the machine's language.
/// </summary>
public sealed class CalendarDateEntryFormatTests
{
    private static readonly DateTimeOffset Now = At(2026, 7, 29);

    private static DateOnly? Parsed(string text, string culture = "en-AU", DateTimeOffset? reference = null, DateTimeOffset? now = null)
        => Parsed(text, CultureInfo.GetCultureInfo(culture), reference, now);

    private static DateOnly? Parsed(string text, CultureInfo culture, DateTimeOffset? reference = null, DateTimeOffset? now = null)
    {
        var result = CalendarDateEntry.Date(text, reference ?? Now, Sydney, culture, now ?? Now);
        return result is { } day ? Sydney.DateOf(day) : null;
    }

    [Theory]
    [InlineData("2026-08-14")]
    [InlineData("2026/8/14")]
    [InlineData("2026.08.14")]
    [InlineData("2026 8 14")]
    [InlineData("14.8.2026")]
    [InlineData("14/08/2026")]
    [InlineData("14-8-2026")]
    [InlineData("14/8/26")]
    [InlineData("14/8")]
    [InlineData("14 aug")]
    [InlineData("aug 14")]
    [InlineData("14 August 2026")]
    [InlineData("August 14, 2026")]
    [InlineData("Aug. 14")]
    [InlineData("  14 AUG  ")]
    public void EveryShapeTheSwiftAcceptedReadsAsTheSameDay(string text)
    {
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed(text));
    }

    [Fact]
    public void YearFirstNeedsNoCulture()
    {
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("2026-08-14", culture: "en-US"));
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("2026/8/14", culture: "de-DE"));
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("2026-08-14", culture: "ja-JP"));
    }

    [Theory]
    [InlineData("en-AU", 2026, 9, 8)]
    [InlineData("en-GB", 2026, 9, 8)]
    [InlineData("de-DE", 2026, 9, 8)]
    [InlineData("en-US", 2026, 8, 9)]
    [InlineData("ja-JP", 2026, 8, 9)]
    [InlineData("sv-SE", 2026, 8, 9)]
    public void TheCultureDecidesWhetherTheDayOrTheMonthComesFirst(string culture, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), Parsed("8/9", culture));
    }

    [Fact]
    public void ANumberThatCannotBeTheMonthIsNotSwappedIntoPlace()
    {
        // In the United States 14/8 would be the 8th of a fourteenth month,
        // and in Australia 8/13 the 8th of a thirteenth: both are refused
        // rather than quietly read the other way round.
        Assert.Null(Parsed("14/8", culture: "en-US"));
        Assert.Null(Parsed("8/13", culture: "en-AU"));
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("8/14", culture: "en-US"));
    }

    [Fact]
    public void AMonthWithoutADayIsItsFirstDay()
    {
        Assert.Equal(new DateOnly(2026, 8, 1), Parsed("aug 2026"));
        Assert.Equal(new DateOnly(2027, 2, 1), Parsed("2027-02"));
        Assert.Equal(new DateOnly(2026, 11, 1), Parsed("november"));
    }

    [Fact]
    public void ATwoDigitYearIsInTheCenturyOfToday()
    {
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("14/8/26"));
        Assert.Equal(new DateOnly(2126, 8, 14), Parsed("14/8/26", now: Sydney.FromLocal(2126, 1, 1, 12)));
    }

    [Fact]
    public void AYearAfterTheDayStaysTheReferenceYearWhenItIsSmall()
    {
        // As in the Swift: with a month name, only a number above 31 can be a
        // year, so "26" after the day is not one.
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("14 aug 26", reference: At(2026, 3, 1)));
        Assert.Equal(new DateOnly(2027, 8, 14), Parsed("14 aug 2027", reference: At(2026, 3, 1)));
    }

    [Theory]
    [InlineData("today", 2026, 7, 29)]
    [InlineData("now", 2026, 7, 29)]
    [InlineData("  Today  ", 2026, 7, 29)]
    [InlineData("TOMORROW", 2026, 7, 30)]
    [InlineData("yesterday\n", 2026, 7, 28)]
    public void RelativeWordsCountFromToday(string text, int year, int month, int day)
    {
        // The reference is elsewhere on purpose: "today" means today, not the
        // day on screen.
        Assert.Equal(new DateOnly(year, month, day), Parsed(text, reference: At(2027, 3, 3)));
    }

    [Fact]
    public void TomorrowIsTheNextCalendarDayAcrossAChangeOfClocks()
    {
        var lateBeforeTheChange = At(2026, 10, 3, hour: 23, minute: 30);

        Assert.Equal(new DateOnly(2026, 10, 4), Parsed("tomorrow", now: lateBeforeTheChange));
    }

    [Theory]
    [InlineData("sept 3", 9, 3)]
    [InlineData("SEPTEMBER 3", 9, 3)]
    [InlineData("3 sep", 9, 3)]
    [InlineData("jun 30", 6, 30)]
    [InlineData("1 dec", 12, 1)]
    public void CanvasAbbreviationsAreReadInAnyCase(string text, int month, int day)
    {
        Assert.Equal(new DateOnly(2026, month, day), Parsed(text));
    }

    [Theory]
    [InlineData("de-DE", "14 märz", 3)]
    [InlineData("de-DE", "14. Mär", 3)]
    [InlineData("fr-FR", "14 août 2026", 8)]
    [InlineData("fr-FR", "14 févr.", 2)]
    [InlineData("ru-RU", "14 августа", 8)]
    [InlineData("ru-RU", "14 август", 8)]
    [InlineData("pl-PL", "14 sierpnia", 8)]
    public void TheCulturesOwnMonthNamesAreReadToo(string culture, string text, int month)
    {
        Assert.Equal(new DateOnly(2026, month, 14), Parsed(text, culture));
    }

    [Fact]
    public void EnglishMonthsAreReadWhateverTheCulture()
    {
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("14 aug", culture: "de-DE"));
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("august 14", culture: "ja-JP"));
    }

    [Fact]
    public void AnAccentTypedAsASeparateMarkStillMatches()
    {
        // "août" typed as "aou" followed by a combining circumflex.
        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("14 aou\u0302t", culture: "fr-FR"));
    }

    [Fact]
    public void TheCulturesOwnShortDateIsReadLast()
    {
        var compact = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        compact.DateTimeFormat.ShortDatePattern = "yyyyMMdd";

        Assert.Equal(new DateOnly(2026, 8, 14), Parsed("20260814", compact));
        Assert.Null(Parsed("20261314", compact));
    }

    [Theory]
    [InlineData("32/1/2026")]
    [InlineData("0 aug")]
    [InlineData("aug 0")]
    [InlineData("29 feb 2027")]
    [InlineData("14th aug")]
    [InlineData("14 aug sometime")]
    [InlineData("1/2/3/4")]
    [InlineData("---")]
    [InlineData("10000-01-01")]
    [InlineData("99999999999999999999")]
    // Arabic-Indic digits: Swift's Int reads only ASCII ones.
    [InlineData("\u0661\u0664/\u0668")]
    public void WhatCannotBeOneDayIsRefused(string text)
    {
        Assert.Null(Parsed(text));
    }

    [Fact]
    public void TheLastDayDotNetCanRepresentIsStillADay()
    {
        Assert.Equal(new DateOnly(9999, 12, 31), Parsed("9999-12-31"));
    }

    [Fact]
    public void ALeapDayIsReadOnlyInALeapYear()
    {
        Assert.Equal(new DateOnly(2028, 2, 29), Parsed("29/2/2028"));
        Assert.Null(Parsed("29/2/2026"));
    }

    [Fact]
    public void TheFieldExplainsItselfBeforeAnythingIsTyped()
    {
        var preview = CalendarDateEntry.Preview("", Now, Sydney, English, Now);

        Assert.Equal(CalendarDateEntryState.Empty, preview.State);
        Assert.Equal("Type a date, or a month and a day.", preview.Message);
        Assert.False(preview.CanGo);
    }

    [Fact]
    public void TheFieldShowsTheDayBeforeGoingThere()
    {
        // "8/9" is two different days in two different places; the field says
        // which one Go will open.
        var american = CalendarDateEntry.Preview("8/9", Now, Sydney, English, Now);
        var australian = CalendarDateEntry.Preview("8/9", Now, Sydney, Australian, Now);

        Assert.Equal(CalendarDateEntryState.Resolved, american.State);
        Assert.Equal("Sunday, August 9, 2026", american.Message);
        Assert.True(american.CanGo);
        Assert.Equal(Sydney.StartOfDay(new DateOnly(2026, 8, 9)), american.Date);
        Assert.Equal(new DateOnly(2026, 9, 8), Sydney.DateOf(australian.Date!.Value));
    }

    [Theory]
    [InlineData("sometime next term")]
    [InlineData("   ")]
    public void TheFieldSaysPlainlyWhenItCannotReadTheText(string text)
    {
        var preview = CalendarDateEntry.Preview(text, Now, Sydney, English, Now);

        Assert.Equal(CalendarDateEntryState.Unreadable, preview.State);
        Assert.Equal("Not a date this app can read", preview.Message);
        Assert.Null(preview.Date);
        Assert.False(preview.CanGo);
    }
}

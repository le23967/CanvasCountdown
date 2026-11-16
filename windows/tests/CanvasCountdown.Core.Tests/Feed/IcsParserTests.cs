namespace CanvasCountdown.Tests.Feed;

public sealed class IcsParserTests
{
    private static readonly IcsParser Parser = new();
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "representative.ics"));

    private static DateTimeOffset At(TimeZoneInfo zone, int year, int month, int day, int hour = 0, int minute = 0)
        => new AppCalendar(zone).FromLocal(year, month, day, hour, minute);

    [Fact]
    public void ParsesUtcDateAndRelevantFields()
    {
        var parsed = Parser.ParseResult(Fixture(), Utc).Events.Single(item => item.Uid == "utc-1@example.edu");

        Assert.Equal("Research proposal", parsed.Summary);
        Assert.Equal(At(Utc, 2026, 8, 3, 6, 30), parsed.StartDate);
        Assert.Equal(At(Utc, 2026, 8, 3, 7, 30), parsed.EndDate);
        Assert.Equal(At(Utc, 2026, 7, 28, 0, 15), parsed.LastModified);
        Assert.Equal("https://canvas.example.edu/courses/42/assignments/9", parsed.Url);
        Assert.Equal("Upload a PDF", parsed.Description);
        Assert.False(parsed.IsAllDay);
        Assert.Equal("UTC", parsed.TimeZoneIdentifier);
    }

    [Fact]
    public void ParsesAllDayDateInDefaultTimeZone()
    {
        var parsed = Parser.Parse(Fixture(), Sydney).Single(item => item.Uid == "all-day-1@example.edu");

        Assert.Equal(At(Sydney, 2026, 8, 4), parsed.StartDate);
        Assert.Equal(At(Sydney, 2026, 8, 5), parsed.EndDate);
        Assert.True(parsed.IsAllDay);
        Assert.Null(parsed.TimeZoneIdentifier);
    }

    [Fact]
    public void ParsesTzidDateAcrossDst()
    {
        var parsed = Parser.Parse(Fixture(), Utc).Single(item => item.Uid == "tzid-1@example.edu");

        Assert.Equal(At(NewYork, 2026, 11, 2, 9, 45), parsed.StartDate);
        Assert.Equal("America/New_York", parsed.TimeZoneIdentifier);
    }

    [Fact]
    public void FloatingDateTimeUsesSuppliedTimeZone()
    {
        var text = Ics.Calendar("BEGIN:VEVENT\r\nUID:floating@example.edu\r\nDTSTART:20260812T235900\r\nSUMMARY:Floating deadline\r\nEND:VEVENT");

        var parsed = Assert.Single(Parser.Parse(text, Sydney));

        Assert.Equal(At(Sydney, 2026, 8, 12, 23, 59), parsed.StartDate);
        Assert.Null(parsed.TimeZoneIdentifier);
    }

    [Fact]
    public void FoldedLinesAndEscapedText()
    {
        var parsed = Parser.Parse(Fixture(), Utc).Single(item => item.Uid == "folded-1@example.edu");

        Assert.Equal("A very long assignment title, with detail", parsed.Summary);
        Assert.Equal("Line one\nLine two; bring notes, laptop\\charger", parsed.Description);
    }

    [Fact]
    public void CancelledAndDatelessEventsAreIgnored()
    {
        var result = Parser.ParseResult(Fixture(), Utc);

        Assert.DoesNotContain(result.Events, item => item.Uid == "cancelled@example.edu");
        Assert.DoesNotContain(result.Events, item => item.Uid == "missing-date@example.edu");
        Assert.Contains(result.Warnings, warning => warning.EventUid == "missing-date@example.edu" && warning.Message.Contains("without DTSTART", StringComparison.Ordinal));
    }

    [Fact]
    public void VTimeZoneLocationResolvesCustomTzid()
    {
        var text = Ics.Calendar(
            "BEGIN:VTIMEZONE\r\nTZID:University Time\r\nX-LIC-LOCATION:Australia/Sydney\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:alias@example.edu\r\nDTSTART;TZID=\"University Time\":20261004T150000\r\nSUMMARY:Custom zone\r\nEND:VEVENT");

        var parsed = Assert.Single(Parser.Parse(text, Utc));

        Assert.Equal(At(Sydney, 2026, 10, 4, 15), parsed.StartDate);
        Assert.Equal("Australia/Sydney", parsed.TimeZoneIdentifier);
    }

    [Fact]
    public void AWindowsZoneNameIsUnderstood()
    {
        var text = Ics.Calendar("BEGIN:VEVENT\r\nUID:windows@example.edu\r\nDTSTART;TZID=AUS Eastern Standard Time:20260814T235900\r\nSUMMARY:Exported from Outlook\r\nEND:VEVENT");

        var parsed = Assert.Single(Parser.Parse(text, Utc));

        Assert.Equal(At(Sydney, 2026, 8, 14, 23, 59), parsed.StartDate);
    }

    [Fact]
    public void NestedAlarmDoesNotReplaceEventFields()
    {
        var text = Ics.Calendar(
            "BEGIN:VEVENT\r\nUID:alarm@example.edu\r\nDTSTART:20260801T100000Z\r\nSUMMARY:Real title\r\nDESCRIPTION:Real description\r\n"
            + "BEGIN:VALARM\r\nSUMMARY:Alarm title\r\nDESCRIPTION:Alarm description\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VEVENT");

        var parsed = Assert.Single(Parser.Parse(text, Utc));

        Assert.Equal("Real title", parsed.Summary);
        Assert.Equal("Real description", parsed.Description);
    }

    [Fact]
    public void InvalidEventDateWarnsWithoutLosingValidEvents()
    {
        var text = Ics.Calendar(
            "BEGIN:VEVENT\r\nUID:bad@example.edu\r\nDTSTART:tomorrow-ish\r\nSUMMARY:Bad\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:good@example.edu\r\nDTSTART:20260801T100000Z\r\nSUMMARY:Good\r\nEND:VEVENT");

        var result = Parser.ParseResult(text, Utc);

        Assert.Equal(["good@example.edu"], result.Events.Select(item => item.Uid));
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("bad@example.edu", warning.EventUid);
    }

    [Fact]
    public void NonCalendarContentProducesClearError()
    {
        var error = Assert.Throws<IcsParserException>(() => Parser.Parse("<html>Sign in</html>", Utc));

        Assert.Equal(IcsParserError.NotCalendarData, error.Error);
    }

    [Fact]
    public void UnknownTzidSkipsOnlyAffectedEvent()
    {
        var text = Ics.Calendar(
            "BEGIN:VEVENT\r\nUID:unknown-zone@example.edu\r\nDTSTART;TZID=Moon/Base:20260801T100000\r\nSUMMARY:Unknown zone\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:valid@example.edu\r\nDTSTART:20260801T100000Z\r\nSUMMARY:Valid\r\nEND:VEVENT");

        var result = Parser.ParseResult(text, Utc);

        Assert.Equal(["valid@example.edu"], result.Events.Select(item => item.Uid));
        Assert.Equal("unknown-zone@example.edu", result.Warnings[0].EventUid);
        Assert.Contains("not recognized", result.Warnings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateUidKeepsOnlyNewestRevisionRegardlessOfFeedOrder()
    {
        var older = "BEGIN:VEVENT\r\nUID:dup@example.edu\r\nSEQUENCE:1\r\nDTSTART:20260801T100000Z\r\nSUMMARY:Old\r\nEND:VEVENT";
        var newer = "BEGIN:VEVENT\r\nUID:dup@example.edu\r\nSEQUENCE:2\r\nDTSTART:20260802T100000Z\r\nSUMMARY:New\r\nEND:VEVENT";

        var forwards = Assert.Single(Parser.Parse(Ics.Calendar(older + "\r\n" + newer), Utc));
        var backwards = Assert.Single(Parser.Parse(Ics.Calendar(newer + "\r\n" + older), Utc));

        Assert.Equal("New", forwards.Summary);
        Assert.Equal("New", backwards.Summary);
    }

    [Fact]
    public void CancellationTombstoneWinsOverActiveDuplicateUid()
    {
        var text = Ics.Calendar(
            "BEGIN:VEVENT\r\nUID:gone@example.edu\r\nDTSTART:20260801T100000Z\r\nSUMMARY:Still here?\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:GONE@example.edu \r\nSTATUS:CANCELLED\r\nEND:VEVENT");

        var result = Parser.ParseResult(text, Utc);

        Assert.Empty(result.Events);
        Assert.Contains("gone@example.edu", result.CancelledEventUids);
    }

    [Fact]
    public void AByteOrderMarkAndBareLineFeedsAreAccepted()
    {
        var text = "﻿" + Ics.Calendar(Ics.Event("bom@example.edu", "20260801T100000Z", "With BOM")).Replace("\r\n", "\n", StringComparison.Ordinal);

        var parsed = Assert.Single(Parser.Parse(System.Text.Encoding.UTF8.GetBytes(text), Utc));

        Assert.Equal("bom@example.edu", parsed.Uid);
    }
}

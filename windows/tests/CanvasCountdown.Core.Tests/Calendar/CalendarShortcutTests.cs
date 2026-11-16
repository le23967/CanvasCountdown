namespace CanvasCountdown.Tests.Calendar;

/// <summary>
/// The keys and words that come with the four scales: Ctrl in place of ⌘,
/// everything else as on the Mac.
/// </summary>
public sealed class CalendarShortcutTests
{
    [Fact]
    public void TheMenuListsTheScalesThenTodayThenGoToDate()
    {
        Assert.Equal(
            ["Day Ctrl+1", "Week Ctrl+2", "Month Ctrl+3", "Year Ctrl+4", "Today Ctrl+T", "Go to Date… Ctrl+Shift+T"],
            CalendarShortcuts.All.Select(shortcut => $"{shortcut.Title} {shortcut.KeyText}"));
        Assert.Equal(
            ["Day", "Today"],
            CalendarShortcuts.All.Where(shortcut => shortcut.StartsGroup).Select(shortcut => shortcut.Title));
    }

    [Fact]
    public void EachScaleHasOneShortcutThatShowsIt()
    {
        foreach (var scale in CalendarScaleExtensions.AllCases)
        {
            var shortcut = Assert.Single(CalendarShortcuts.All, candidate => candidate.Scale == scale);
            Assert.Equal(CalendarCommand.ShowScale, shortcut.Command);
            Assert.Equal(scale.Title(), shortcut.Title);
            Assert.Equal(scale.Shortcut(), shortcut.Key);
            Assert.Equal(CalendarShortcutModifiers.Control, shortcut.Modifiers);
        }
    }

    [Fact]
    public void NoTwoShortcutsShareKeys()
    {
        var combinations = CalendarShortcuts.All.Select(shortcut => (shortcut.Key, shortcut.Modifiers)).ToList();

        Assert.Equal(combinations.Count, combinations.Distinct().Count());
    }

    [Theory]
    [InlineData('1', CalendarShortcutModifiers.Control, CalendarCommand.ShowScale, CalendarScale.Day)]
    [InlineData('4', CalendarShortcutModifiers.Control, CalendarCommand.ShowScale, CalendarScale.Year)]
    [InlineData('t', CalendarShortcutModifiers.Control, CalendarCommand.ShowToday, null)]
    [InlineData('T', CalendarShortcutModifiers.Control, CalendarCommand.ShowToday, null)]
    [InlineData('T', CalendarShortcutModifiers.Control | CalendarShortcutModifiers.Shift, CalendarCommand.GoToDate, null)]
    public void AKeyPressFindsItsCommand(char key, CalendarShortcutModifiers modifiers, CalendarCommand command, CalendarScale? scale)
    {
        var shortcut = CalendarShortcuts.Matching(key, modifiers);

        Assert.NotNull(shortcut);
        Assert.Equal(command, shortcut.Command);
        Assert.Equal(scale, shortcut.Scale);
    }

    [Theory]
    [InlineData('1', CalendarShortcutModifiers.None)]
    [InlineData('1', CalendarShortcutModifiers.Control | CalendarShortcutModifiers.Shift)]
    [InlineData('5', CalendarShortcutModifiers.Control)]
    [InlineData('T', CalendarShortcutModifiers.Shift)]
    public void OtherKeysAreLeftAlone(char key, CalendarShortcutModifiers modifiers)
    {
        Assert.Null(CalendarShortcuts.Matching(key, modifiers));
    }

    [Fact]
    public void TheScalesAreTheSystemCalendarsFourInItsOrder()
    {
        Assert.Equal([CalendarScale.Day, CalendarScale.Week, CalendarScale.Month, CalendarScale.Year], CalendarScaleExtensions.AllCases);
        Assert.Equal(["Day", "Week", "Month", "Year"], CalendarScaleExtensions.AllCases.Select(scale => scale.Title()));
        Assert.Equal(['1', '2', '3', '4'], CalendarScaleExtensions.AllCases.Select(scale => scale.Shortcut()));
    }

    [Fact]
    public void TheArrowsSayWhatTheyStepBy()
    {
        Assert.Equal("Previous day", CalendarScale.Day.PreviousTitle());
        Assert.Equal("Next week", CalendarScale.Week.NextTitle());
        Assert.Equal("Previous month", CalendarScale.Month.PreviousTitle());
        Assert.Equal("Next year", CalendarScale.Year.NextTitle());
    }

    [Theory]
    [InlineData(CalendarScale.Day, "\"day\"")]
    [InlineData(CalendarScale.Week, "\"week\"")]
    [InlineData(CalendarScale.Month, "\"month\"")]
    [InlineData(CalendarScale.Year, "\"year\"")]
    public void AScaleIsStoredByTheMacsRawValue(CalendarScale scale, string json)
    {
        Assert.Equal(json, JsonDefaults.Serialize(scale));
        Assert.Equal(scale, JsonDefaults.Deserialize<CalendarScale>(json));
    }

    [Fact]
    public void AStoredScaleThisBuildDoesNotKnowFallsBack()
    {
        const string key = "CanvasCountdown.settings.calendarScale";
        var store = JsonPreferencesStore.InMemory();

        store.Set(key, CalendarScale.Week);
        Assert.Equal(CalendarScale.Week, store.Get(key, CalendarScale.Month));

        store.Set(key, "fortnight");
        Assert.Equal(CalendarScale.Month, store.Get(key, CalendarScale.Month));
    }
}

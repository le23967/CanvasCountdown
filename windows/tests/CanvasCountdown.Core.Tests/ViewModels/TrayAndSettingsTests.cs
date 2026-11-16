namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// The tray countdown follows every change to what is due, and settings apply
/// and persist the moment they change, with no Save step. Ported from the Mac
/// app's DockAndViewModelTests and ImmediateSettingsTests, plus the Windows
/// clock-change path.
/// </summary>
public sealed class TrayAndSettingsTests
{
    private static string IcsAt(DateTimeOffset due, string uid = "essay@canvas.example.edu", string title = "Essay [COMP2050]")
        => string.Join(
            "\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTART:{due.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}",
            $"SUMMARY:{title}",
            "END:VEVENT",
            "END:VCALENDAR");

    // ------------------------------------------------------------------
    // The tray

    [Fact]
    public void StartDrawsTheNearestEligibleAssignmentInCalendarDays() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddAsync("Overdue", ViewModelHarness.Day(-1), manual: true);
        await harness.AddAsync("Completed", ViewModelHarness.Day(1), manual: true, completed: true);
        await harness.AddAsync("Ignored", ViewModelHarness.Day(1), manual: true, ignored: true);
        await harness.AddAsync("Nearest", ViewModelHarness.Day(2), manual: true);
        await harness.AddAsync("Later", ViewModelHarness.Day(5), manual: true);
        harness.Settings.DockDisplayLanguage = DockDisplayLanguage.Chinese;

        await harness.ViewModel.StartAsync();

        Assert.Equal(2, harness.Dock.LastDays);
        Assert.Equal("倒数日", harness.Dock.LastLabel);
        Assert.Equal("Nearest", harness.Dock.Renders[^1].Item?.Title);
    });

    [Fact]
    public void ImportAndManualRefreshRedrawTheTray() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        harness.Fetcher.Payload = IcsAt(ViewModelHarness.Day(4));

        var preview = await harness.ViewModel.PreviewFeedAsync(ViewModelHarness.FeedUrl);
        await harness.ViewModel.ImportFeedAsync(ViewModelHarness.FeedUrl, preview.Select(item => item.Id).ToHashSet());
        Assert.Equal(4, harness.Dock.LastDays);

        // Canvas moves the deadline; the next refresh brings it in.
        harness.Fetcher.Payload = IcsAt(ViewModelHarness.Day(1));
        harness.ViewModel.RefreshManually();

        await ViewModelHarness.WaitUntil(
            () => harness.Dock.LastDays == 1 && harness.ViewModel.LastRefreshDate is not null,
            "the refresh has redrawn the tray");
    });

    [Fact]
    public void CompletingIgnoringAndDeletingRedrawTheTray() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddManualAsync("First", ViewModelHarness.Day(1));
        await harness.AddManualAsync("Second", ViewModelHarness.Day(3));
        await harness.ViewModel.StartAsync();
        Assert.Equal(1, harness.Dock.LastDays);

        await harness.ViewModel.ToggleCompletedAsync(harness.Item("First"));
        Assert.Equal(3, harness.Dock.LastDays);
        Assert.True(harness.Item("First").IsCompleted);

        await harness.ViewModel.ToggleIgnoredAsync(harness.Item("Second"));
        Assert.Null(harness.Dock.LastDays);
        Assert.True(harness.Item("Second").IsIgnored);

        await harness.ViewModel.ToggleIgnoredAsync(harness.Item("Second"));
        Assert.Equal(3, harness.Dock.LastDays);
        Assert.False(harness.Item("Second").IsIgnored);

        await harness.ViewModel.DeleteManualEventAsync(harness.Item("Second"));
        Assert.Null(harness.Dock.LastDays);
        Assert.DoesNotContain(harness.ViewModel.Assignments, item => item.Title == "Second");
    });

    [Fact]
    public void AddingAndRedatingAManualEventRedrawTheTray() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        Assert.Null(harness.Dock.LastDays);

        await harness.ViewModel.SaveManualEventAsync(new ManualEventDraft { Title = "Manual", DueDate = ViewModelHarness.Day(5) });
        Assert.Equal(5, harness.Dock.LastDays);

        var saved = harness.Item("Manual");
        await harness.ViewModel.SaveManualEventAsync(ManualEventDraft.From(saved) with { DueDate = ViewModelHarness.Day(1) });
        Assert.Equal(1, harness.Dock.LastDays);
        Assert.Single(harness.ViewModel.Assignments);
    });

    /// <summary>
    /// Windows says when the clock jumps (a time-zone change, waking from
    /// sleep); the count is worked out again then, not at the next refresh.
    /// </summary>
    [Fact]
    public void AClockChangeRecountsTheDays() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddManualAsync("Essay", ViewModelHarness.Day(3));
        await harness.ViewModel.StartAsync();
        Assert.Equal(3, harness.Dock.LastDays);

        harness.Clock.Advance(TimeSpan.FromDays(2));
        harness.ViewModel.SystemTimeDidChange();

        await ViewModelHarness.WaitUntil(() => harness.Dock.LastDays == 1, "the tray counts from the new date");
    });

    // ------------------------------------------------------------------
    // Settings apply at once

    [Fact]
    public void TheRefreshIntervalPersistsWithoutAnExplicitSave() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        harness.ViewModel.UpdateSettings(form => form with { RefreshInterval = RefreshInterval.Daily });

        Assert.Equal(RefreshInterval.Daily, harness.Settings.RefreshInterval);
        // Already written, not waiting for a save.
        Assert.Equal(RefreshInterval.Daily, new SettingsStore(harness.Preferences).RefreshInterval);
    });

    [Fact]
    public void TheLabelLanguageRedrawsTheTrayImmediately() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddManualAsync("Essay", ViewModelHarness.Day(2));
        await harness.ViewModel.StartAsync();

        harness.ViewModel.UpdateSettings(form => form with { DockLabel = DockLabelOption.Chinese });

        Assert.Equal("倒数日", harness.Dock.LastLabel);
        Assert.Equal(DockDisplayLanguage.Chinese, new SettingsStore(harness.Preferences).DockDisplayLanguage);
    });

    [Fact]
    public void ANewAppearanceIsHandedToTheTrayOnce() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        var before = harness.Dock.Appearances.Count;

        harness.ViewModel.ApplyDockTheme(DockThemePreset.Dark);
        harness.ViewModel.UpdateSettings(form => form with { RefreshInterval = RefreshInterval.Hourly });

        Assert.Equal(before + 1, harness.Dock.Appearances.Count);
        Assert.Equal(DockThemePreset.Dark, harness.Dock.Appearances[^1].Preset);
        Assert.Equal(DockThemePreset.Dark, new SettingsStore(harness.Preferences).DockAppearance.Preset);
    });

    [Fact]
    public void TheConfirmationIsShownAndThenCleared() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        harness.ViewModel.UpdateSettings(form => form with { RefreshInterval = RefreshInterval.Hourly });
        Assert.Equal("Settings updated", harness.ViewModel.StatusMessage);

        // A confirmation must not stay on screen for good.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.4));
        await ViewModelHarness.WaitUntil(() => harness.ViewModel.StatusMessage is null, "the confirmation has gone");
    });

    /// <summary>
    /// Opening Settings draws every control with its current value, and some
    /// report it straight back. Nothing changed, so nothing is announced.
    /// </summary>
    [Fact]
    public void ReapplyingTheSameSettingsSaysNothing() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        var renders = harness.Dock.Renders.Count;

        harness.ViewModel.UpdateSettings(form => form with { RefreshInterval = form.RefreshInterval });
        harness.ViewModel.ApplySettings(harness.ViewModel.SettingsForm with { });

        Assert.Null(harness.ViewModel.StatusMessage);
        Assert.Equal(renders, harness.Dock.Renders.Count);
    });

    [Fact]
    public void TheFeedUrlIsNeverWrittenBackFromTheSettingsForm() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        harness.ViewModel.UpdateSettings(form => form with { FeedUrl = "https://canvas.example.edu/tampered.ics" });

        // Only the preview and import flow may write the stored feed URL.
        Assert.Null(await harness.FeedUrlStore.LoadFeedUrlAsync());
        Assert.False(harness.ViewModel.HasConfiguredFeed);
    });

    [Fact]
    public void ReminderChangesAreDebouncedIntoASingleReschedule() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.AddManualAsync("Essay", ViewModelHarness.Day(10));
        await harness.ViewModel.StartAsync();
        harness.Notifications.Reschedules.Clear();

        foreach (var hours in new[] { 2, 4, 6, 8 })
        {
            var schedule = new ReminderSchedule([]).Add(new ReminderRule { Amount = hours, Unit = ReminderUnit.Hours });
            harness.ViewModel.UpdateSettings(form => form with { ReminderSchedule = schedule });
        }
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1.2));
        await ViewModelHarness.WaitUntil(() => harness.Notifications.Reschedules.Count >= 1, "the reminders were rescheduled");
        await Task.Delay(50);

        // Rapid edits collapse into one reschedule, not one per keystroke…
        Assert.Single(harness.Notifications.Reschedules);
        Assert.Equal([480], harness.Notifications.Reschedules[0].Schedule.Rules.Select(rule => rule.OffsetMinutes));
        // …and the final choice is still persisted at once.
        Assert.Equal([480], new SettingsStore(harness.Preferences).ReminderSchedule.Rules.Select(rule => rule.OffsetMinutes));
    });

    [Fact]
    public void LaunchAtSignInGoesThroughTheStartupEntry() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();

        harness.ViewModel.UpdateSettings(form => form with { LaunchAtLogin = true });

        Assert.True(harness.LaunchAtLogin.IsEnabled);
        Assert.True(new SettingsStore(harness.Preferences).LaunchAtLogin);
        Assert.True(harness.ViewModel.SettingsForm.LaunchAtLogin);
    });

    [Fact]
    public void AStartupEntryThatCannotBeWrittenLeavesTheSwitchOff() => ViewModelHarness.OnUiThread(async () =>
    {
        var refusing = new RefusingLaunchAtLogin();
        using var harness = new ViewModelHarness(dependencies => dependencies with { LaunchAtLogin = refusing });
        await harness.ViewModel.StartAsync();

        harness.ViewModel.UpdateSettings(form => form with { LaunchAtLogin = true, RefreshInterval = RefreshInterval.Daily });

        // The switch says what is true, and nothing else from that change is half-applied.
        Assert.False(harness.ViewModel.SettingsForm.LaunchAtLogin);
        Assert.NotNull(harness.ViewModel.ErrorMessage);
        Assert.NotEqual(RefreshInterval.Daily, new SettingsStore(harness.Preferences).RefreshInterval);
    });

    [Fact]
    public void KeepingRunningInTheNotificationAreaIsRemembered() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        await harness.ViewModel.StartAsync();
        Assert.True(harness.ViewModel.SettingsForm.KeepsRunningInNotificationArea);

        harness.ViewModel.UpdateSettings(form => form with { KeepsRunningInNotificationArea = false });

        Assert.False(new SettingsStore(harness.Preferences).KeepsRunningInNotificationArea);
    });

    /// <summary>
    /// Midnight shown as 11:59 PM is a reading, not an edit: switching it off
    /// brings Canvas's own time straight back, and the day count never moves.
    /// </summary>
    [Fact]
    public void TheMidnightRuleChangesOnlyTheTimeShown() => ViewModelHarness.OnUiThread(async () =>
    {
        using var harness = new ViewModelHarness();
        var midnight = ViewModelHarness.Sydney.StartOfDay(ViewModelHarness.Day(3));
        var stored = await harness.AddImportedAsync("Essay", midnight, "COMP2050");
        await harness.ViewModel.StartAsync();

        var shown = harness.Item("Essay");
        Assert.Equal(new TimeOnly(23, 59), ViewModelHarness.Sydney.TimeOf(shown.DueDate));
        Assert.Equal(ViewModelHarness.Sydney.DateOf(midnight), ViewModelHarness.Sydney.DateOf(shown.DueDate));
        var days = harness.Dock.LastDays;

        harness.ViewModel.UpdateSettings(form => form with { TreatsMidnightAsEndOfDay = false });

        await ViewModelHarness.WaitUntil(
            () => ViewModelHarness.Sydney.TimeOf(harness.Item("Essay").DueDate) == TimeOnly.MinValue,
            "the original time is shown again");
        Assert.Equal(days, harness.Dock.LastDays);
        // Nothing Canvas sent was altered.
        Assert.Equal(midnight, (await harness.Repository.FetchAllAsync()).Single(snapshot => snapshot.Id == stored.Id).DueDate);
    });

    private sealed class RefusingLaunchAtLogin : ILaunchAtLogin
    {
        public bool IsEnabled => false;

        public void SetEnabled(bool enabled) => throw new UnauthorizedAccessException("The startup entry could not be written.");
    }
}

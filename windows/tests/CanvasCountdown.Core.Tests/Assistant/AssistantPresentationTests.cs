using System.Text.Json;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// How the assistant decides where to appear, and what must survive that
/// decision changing. All fixtures are invented.
/// </summary>
/// <remarks>
/// The macOS tests that drove the view model here are ported against the
/// Core decision the view model calls.
/// </remarks>
public sealed class AssistantPresentationTests
{
    /// <summary>A detail column with room to spare.</summary>
    private const double WideWindow = 1_400;

    /// <summary>
    /// About what a half-screen window leaves: the case that used to lose the
    /// sidebar altogether.
    /// </summary>
    private const double NarrowWindow = 620;

    private static readonly DateTimeOffset Now = AssistantFixtures.Date(2026, 8, 3, hour: 10);

    // Choosing a presentation

    [Fact]
    public void TheSidebarIsShownWhateverTheWindowIsDoing()
    {
        // Split screen and full screen must not undo what the user chose.
        Assert.Equal(
            ActiveAssistantPresentation.Sidebar,
            AssistantLayout.Presentation(AssistantPresentationPreference.Sidebar, ActiveAssistantPresentation.Closed));
    }

    [Fact]
    public void APopoverChoiceIsAlsoHonoured()
    {
        Assert.Equal(
            ActiveAssistantPresentation.Popover,
            AssistantLayout.Presentation(AssistantPresentationPreference.Popover, ActiveAssistantPresentation.Closed));
    }

    [Fact]
    public void ThereIsNoAutomaticPreference()
    {
        // The width decides how wide the panel is, never whether it exists.
        Assert.Equal(
            [AssistantPresentationPreference.Sidebar, AssistantPresentationPreference.Popover],
            Enum.GetValues<AssistantPresentationPreference>());
    }

    [Fact]
    public void AStoredAutomaticPreferenceReadsAsSidebar()
    {
        var options = new JsonSerializerOptions { Converters = { new AssistantPresentationPreferenceJsonConverter() } };

        var decoded = JsonSerializer.Deserialize<AssistantPresentationPreference>("\"automatic\"", options);

        Assert.Equal(AssistantPresentationPreference.Sidebar, decoded);

        // And where it is actually stored, inside the assistant settings.
        var settings = JsonDefaults.Deserialize<AssistantSettings>(
            """{"isEnabled":true,"provider":"local","baseURL":"http://localhost:11434/v1","model":"llama3.1:8b","presentation":"automatic"}""")!;
        Assert.Equal(AssistantPresentationPreference.Sidebar, settings.Presentation);
    }

    [Fact]
    public void ASeparateWindowIsNeverTakenAwayByResizing()
    {
        var presentation = AssistantLayout.Presentation(
            AssistantPresentationPreference.Sidebar,
            ActiveAssistantPresentation.SeparateWindow);

        Assert.Equal(ActiveAssistantPresentation.SeparateWindow, presentation);
    }

    // Width bounds

    [Fact]
    public void AWideWindowGetsThePreferredWidth()
    {
        Assert.Equal(400, AssistantLayout.FittedSidebarWidth(preferred: 400, availableWidth: WideWindow));
    }

    [Fact]
    public void TheSidebarNeverAsksForMoreThanTheColumnHas()
    {
        // The whole point: on a split-screen window the panel is trimmed rather
        // than the window being widened to fit it.
        for (double available = 200; available <= 1_600; available += 20)
        {
            var width = AssistantLayout.FittedSidebarWidth(
                preferred: AssistantLayout.SidebarMaximum,
                availableWidth: available);

            Assert.True(width <= available, "A panel wider than the column would force the window to resize");
            Assert.True(width > 0);
        }
    }

    [Fact]
    public void AHalfScreenColumnGetsANarrowerPanelRatherThanNone()
    {
        var width = AssistantLayout.FittedSidebarWidth(
            preferred: AssistantLayout.SidebarIdeal,
            availableWidth: NarrowWindow);

        Assert.True(width < AssistantLayout.SidebarIdeal);
        Assert.True(width <= NarrowWindow - AssistantLayout.MainContentFloor);
        Assert.True(width >= AssistantLayout.SidebarMinimum);
    }

    [Fact]
    public void TheListKeepsItsFloorWhileThereIsRoomForBoth()
    {
        var available = AssistantLayout.MainContentFloor + AssistantLayout.SidebarMinimum + 20;
        var width = AssistantLayout.FittedSidebarWidth(
            preferred: AssistantLayout.SidebarMaximum,
            availableWidth: available);

        Assert.Equal(available - AssistantLayout.MainContentFloor, width);
        Assert.True(width >= AssistantLayout.SidebarMinimum);
    }

    [Fact]
    public void ATinyWindowSplitsTheDifferenceInsteadOfHidingThePanel()
    {
        const double available = 400;
        var width = AssistantLayout.FittedSidebarWidth(
            preferred: AssistantLayout.SidebarIdeal,
            availableWidth: available);

        Assert.Equal(available / 2, width);
    }

    [Fact]
    public void AnUnmeasuredWindowFallsBackToThePreferredWidth()
    {
        Assert.Equal(400, AssistantLayout.FittedSidebarWidth(preferred: 400, availableWidth: 0));
    }

    [Fact]
    public void SidebarWidthStaysWithinItsBounds()
    {
        Assert.Equal(AssistantLayout.SidebarMinimum, AssistantLayout.ClampedSidebarWidth(10));
        Assert.Equal(AssistantLayout.SidebarMaximum, AssistantLayout.ClampedSidebarWidth(9_999));
        Assert.Equal(400, AssistantLayout.ClampedSidebarWidth(400));
        Assert.Equal(AssistantLayout.SidebarIdeal, AssistantLayout.ClampedSidebarWidth(null));
        Assert.Equal(AssistantLayout.SidebarIdeal, AssistantLayout.ClampedSidebarWidth(double.NaN));
    }

    [Fact]
    public void DeclaredBoundsMatchTheIntendedRange()
    {
        Assert.Equal(300, AssistantLayout.SidebarMinimum);
        Assert.Equal(380, AssistantLayout.SidebarIdeal);
        Assert.Equal(440, AssistantLayout.SidebarMaximum);
    }

    [Fact]
    public void OnlyAValidWidthIsRemembered()
    {
        var store = JsonPreferencesStore.InMemory();
        var settings = AssistantSettings.Defaults.RememberingSidebarWidth(400);
        store.Set(AssistantSettings.PreferencesKey, settings);

        Assert.Equal(400, store.Get(AssistantSettings.PreferencesKey, AssistantSettings.Defaults).SidebarWidth);

        settings = settings.RememberingSidebarWidth(2_000);
        store.Set(AssistantSettings.PreferencesKey, settings);

        // An out-of-range width must not replace a good one.
        Assert.Equal(400, store.Get(AssistantSettings.PreferencesKey, AssistantSettings.Defaults).SidebarWidth);
    }

    // One presentation at a time

    [Fact]
    public void ASeparateWindowIsNeverTheDefault()
    {
        var opened = AssistantLayout.Presentation(
            AssistantSettings.Defaults.Presentation,
            ActiveAssistantPresentation.Closed);

        Assert.NotEqual(ActiveAssistantPresentation.SeparateWindow, opened);
        Assert.Equal(ActiveAssistantPresentation.Sidebar, opened);
    }

    /// <summary>
    /// The reported bug: a split-screen window could not open the assistant at
    /// all, because the panel was chosen away on width. Nothing about the
    /// window reaches this decision any more.
    /// </summary>
    [Fact]
    public void TheSidebarOpensWhateverTheWindowIsDoing()
    {
        var opened = AssistantLayout.Presentation(
            AssistantSettings.Defaults.Presentation,
            ActiveAssistantPresentation.Closed);

        Assert.Equal(ActiveAssistantPresentation.Sidebar, opened);
        Assert.True(AssistantLayout.ClampedSidebarWidth(AssistantSettings.Defaults.SidebarWidth) > 0);
    }

    [Fact]
    public void APinnedSidebarSurvivesResizing()
    {
        Assert.Equal(
            ActiveAssistantPresentation.Sidebar,
            AssistantLayout.Presentation(AssistantPresentationPreference.Sidebar, ActiveAssistantPresentation.Closed));
    }

    [Fact]
    public void APinnedPopoverSurvivesResizing()
    {
        Assert.Equal(
            ActiveAssistantPresentation.Popover,
            AssistantLayout.Presentation(AssistantPresentationPreference.Popover, ActiveAssistantPresentation.Closed));
    }

    // Context

    [Fact]
    public void ContextNarrowsWhatIsSentRatherThanAddingToIt()
    {
        AssignmentListItem[] upcoming =
        [
            AssistantFixtures.Item("Example Quiz", "99999 Example Interaction Course", AssistantFixtures.Date(2026, 8, 5, hour: 12)),
            AssistantFixtures.Item("Example Poster Submission", "99999 Example Interaction Course", AssistantFixtures.Date(2026, 8, 12, hour: 12)),
        ];
        var all = AssistantAssignmentDigest.ForScope(upcoming, null, Now).Count;
        Assert.True(all > 1);

        var context = new AssistantContext(upcoming[0]);

        // Asking about one deadline must not send the whole term.
        Assert.Single(AssistantAssignmentDigest.ForScope(upcoming, context, Now));

        Assert.Equal(all, AssistantAssignmentDigest.ForScope(upcoming, null, Now).Count);
    }

    [Fact]
    public void TheContextChipTruncatesALongTitle()
    {
        var item = AssistantFixtures.Item(new string('A', 90), "99999 Example Course", Now);
        var context = new AssistantContext(item);

        Assert.True(context.ChipTitle.Length <= 32);
        Assert.EndsWith("…", context.ChipTitle, StringComparison.Ordinal);
    }

    // Preference and privacy

    [Fact]
    public void TheSidebarIsTheDefaultPreference()
    {
        Assert.Equal(AssistantPresentationPreference.Sidebar, AssistantSettings.Defaults.Presentation);
    }

    [Fact]
    public void ThePrivacyLabelMatchesTheConfiguredService()
    {
        var settings = AssistantSettings.Defaults;
        Assert.Equal("On this PC", settings.PrivacyLabel);
        Assert.True(settings.StaysOnThisMachine);

        var form = AssistantSettings.Applying(AssistantProvider.Cloud, settings);

        Assert.True(
            form.PrivacyLabel.StartsWith("Cloud", StringComparison.Ordinal),
            "A cloud service must never be labelled as staying on this PC");
        Assert.False(form.StaysOnThisMachine);
    }

    [Fact]
    public void LocalPointedAtARemoteHostIsNotLabelledPrivate()
    {
        var form = AssistantSettings.Defaults with { BaseUrl = "https://example.com/v1" };

        Assert.False(form.StaysOnThisMachine);
        Assert.StartsWith("Cloud", form.PrivacyLabel, StringComparison.Ordinal);
    }
}

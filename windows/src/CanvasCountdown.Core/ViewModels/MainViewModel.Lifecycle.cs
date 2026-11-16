using System.Runtime.InteropServices;

namespace CanvasCountdown;

/// <summary>Everything the main view model is built from, so an isolated launch and a test can swap any of it.</summary>
public sealed record MainViewModelDependencies
{
    public required IAssignmentRepository Repository { get; init; }

    public required IFeedRefreshCoordinator RefreshCoordinator { get; init; }

    public required IFeedUrlStore FeedUrlStore { get; init; }

    public required SettingsStore SettingsStore { get; init; }

    public required IDockRenderer DockRenderer { get; init; }

    public required INotificationScheduling NotificationScheduler { get; init; }

    public required AssistantKeyStore AssistantKeyStore { get; init; }

    public required ILocalModelManaging LocalModels { get; init; }

    public required ICourseBlocklist CourseBlocklist { get; init; }

    public required Func<AssistantSettings, string?, AppCalendar, IAssistantServicing> AssistantFactory { get; init; }

    public required IReleaseChecking ReleaseChecker { get; init; }

    public required IUpdateDownloading UpdateDownloader { get; init; }

    public required ILaunchAtLogin LaunchAtLogin { get; init; }

    /// <summary>Installs a downloaded, verified update and restarts into it. Absent where that is not possible.</summary>
    public IUpdateInstaller? UpdateInstaller { get; init; }

    /// <summary>The running version. Injected: under a test host the entry assembly is the runner, not the app.</summary>
    public Func<AppVersion?> CurrentVersion { get; init; } = () => null;

    /// <summary>How a link is opened. Injected so no test opens a browser.</summary>
    public Action<Uri> OpenUrl { get; init; } = _ => { };

    public Func<AppCalendar> Calendar { get; init; } = () => AppCalendar.Current;

    public Func<System.Globalization.CultureInfo> Culture { get; init; } = () => System.Globalization.CultureInfo.CurrentCulture;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public bool AutomaticActivityEnabled { get; init; } = true;
}

/// <summary>Runs a downloaded installer and leaves the app so it can be replaced.</summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// Checks the file is still exactly the one that was verified, starts the
    /// installer and asks the app to quit. Throws if it could not be started,
    /// in which case nothing has changed.
    /// </summary>
    /// <remarks>
    /// The check is repeated here, not only after the download, because the
    /// folder the installer waits in is writable by anything running as the
    /// user.
    /// </remarks>
    void InstallAndRelaunch(string installerPath, string expectedSha256);
}

public sealed partial class MainViewModel
{
    private readonly IReleaseChecking releaseChecker;
    private readonly IUpdateDownloading updateDownloader;
    private readonly IUpdateInstaller? updateInstaller;
    private readonly Func<AppVersion?> currentVersion;
    private readonly Action<Uri> openUrl;

    public async Task StartAsync()
    {
        if (hasStarted)
        {
            return;
        }
        hasStarted = true;
        IsLoading = true;
        ErrorMessage = null;
        Changed();

        Uri? feedUrl = null;
        try
        {
            var snapshots = await repository.FetchAllAsync();
            feedUrl = await feedUrlStore.LoadFeedUrlAsync();
            var permission = await notificationScheduler.AuthorizationStatusAsync();
            Apply(snapshots);
            storedFeedUrlString = feedUrl?.AbsoluteUri ?? string.Empty;
            SettingsForm = SettingsFormState.From(settingsStore.Snapshot, feedUrl?.AbsoluteUri);
            NotificationPermission = permission;
            dockRenderer.Apply(settingsStore.DockAppearance);
            await MigrateAssistantProfilesIfNeededAsync();
            await LoadAssistantKeyAsync();
            await RefreshBlockedCoursesAsync();
            SynchronizeDock();

            if (automaticActivityEnabled && feedUrl is null && Assignments.Count == 0)
            {
                PresentFeedImport(onboarding: true);
            }
        }
        catch (Exception error)
        {
            Present(error);
        }
        IsLoading = false;
        Changed();

        if (!automaticActivityEnabled)
        {
            return;
        }

        StartAutomaticRefreshLoop();
        StartCountdownTransitionLoop();

        if (HasConfiguredFeed)
        {
            await RefreshAsync(RefreshTrigger.Launch, announcesResult: false);
        }
        else
        {
            await SynchronizeNotificationsAsync();
        }

        await CheckForUpdateAsync();
    }

    // ------------------------------------------------------------------
    // Keeping up with what has been published

    /// <summary>
    /// How long a check lasts before another one is worth making. A day: this
    /// app is published a few times a month at most, and asking GitHub more
    /// often would spend someone's network on an answer that cannot have changed.
    /// </summary>
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromSeconds(86_400);

    /// <summary>A newer release than the one running, that has not been waved away.</summary>
    public AppRelease? AvailableUpdate { get; private set; }

    /// <summary>Whether to show the update banner; a bool for the same reason as <see cref="HasStatusMessage"/>.</summary>
    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public bool IsCheckingForUpdate { get; private set; }

    public bool IsDownloadingUpdate { get; private set; }

    public string? UpdateMessage { get; private set; }

    /// <summary>Set only by the manual check, so a background check that finds nothing stays silent while the button always answers.</summary>
    public string? UpdateCheckAnnouncement { get; private set; }

    public AppVersion? RunningVersion => currentVersion();

    public string RunningVersionDescription => RunningVersion?.ToString() ?? "unknown";

    /// <summary>Whether enough time has passed to ask again.</summary>
    public bool ShouldCheckForUpdate(DateTimeOffset now)
    {
        if (settingsStore.LastUpdateCheck is not { } last)
        {
            return true;
        }
        return now - last >= UpdateCheckInterval || last > now;
    }

    /// <summary>
    /// Looks for something newer.
    /// </summary>
    /// <remarks>
    /// Silent by design when it is the launch check: an app that opens with a
    /// message every day is an app people stop reading. Only a newer version
    /// than the running one, and only one that has not been dismissed, puts
    /// anything on screen.
    /// </remarks>
    public async Task CheckForUpdateAsync(bool force = false, DateTimeOffset? at = null)
    {
        var now = at ?? Now;
        if (IsCheckingForUpdate || (!force && !ShouldCheckForUpdate(now)))
        {
            return;
        }
        if (RunningVersion is not { } running)
        {
            // No version to compare against means every answer would be a
            // guess, and "update available" is not something to guess at.
            return;
        }

        IsCheckingForUpdate = true;
        if (force)
        {
            UpdateCheckAnnouncement = null;
            UpdateMessage = null;
        }
        Changed();

        try
        {
            var latest = await releaseChecker.LatestReleaseAsync();
            settingsStore.LastUpdateCheck = now;

            if (latest is null || latest.Version <= running)
            {
                AvailableUpdate = null;
                if (force)
                {
                    UpdateCheckAnnouncement = $"{RunningVersionDescription} is the newest version";
                }
                return;
            }
            // A manual check answers even about a version already dismissed:
            // asking is a change of mind.
            if (force)
            {
                settingsStore.DismissedUpdateVersion = null;
            }
            if (settingsStore.DismissedUpdateVersion == latest.Version.ToString())
            {
                return;
            }
            AvailableUpdate = latest;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A launch check that cannot reach GitHub says nothing. Being
            // offline is not news, and it is not the user's problem to solve.
            if (force)
            {
                UpdateCheckAnnouncement = error.Message;
            }
        }
        finally
        {
            IsCheckingForUpdate = false;
            Changed();
        }
    }

    public Task CheckForUpdateManuallyAsync() => CheckForUpdateAsync(force: true);

    /// <summary>Puts this version away until there is a newer one.</summary>
    public void DismissUpdate()
    {
        settingsStore.DismissedUpdateVersion = AvailableUpdate?.Version.ToString();
        AvailableUpdate = null;
        UpdateMessage = null;
        Changed();
    }

    public void ClearUpdateMessage()
    {
        UpdateMessage = null;
        UpdateCheckAnnouncement = null;
        Changed();
    }

    /// <summary>
    /// Downloads the installer, checks it is exactly the file GitHub published,
    /// and hands over to it.
    /// </summary>
    /// <remarks>
    /// Unlike the macOS build, which is sandboxed and can only open the disk
    /// image, a Windows copy can install over itself — so it does, but only
    /// with a file whose size and SHA-256 match what the release says. The
    /// installer sets the running copy aside rather than overwriting it, puts
    /// it back if anything fails, and starts whichever version it ends with.
    /// Anything that goes wrong before the handover leaves the app exactly as
    /// it was and says why.
    /// </remarks>
    public async Task DownloadUpdateAsync()
    {
        if (AvailableUpdate is not { } release || IsDownloadingUpdate)
        {
            return;
        }
        if (release.DownloadUrl is null)
        {
            OpenReleasePage();
            return;
        }

        IsDownloadingUpdate = true;
        UpdateMessage = null;
        Changed();
        try
        {
            var file = await updateDownloader.DownloadAsync(release);
            if (updateInstaller is null)
            {
                updateDownloader.Reveal(file);
                UpdateMessage = $"{release.Version} is in your Downloads. Open it to finish.";
                return;
            }
            UpdateMessage = $"Installing {release.Version}… Canvas Countdown will reopen by itself.";
            Changed();
            updateInstaller.InstallAndRelaunch(file, release.Installer?.Sha256 ?? string.Empty);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            UpdateMessage = error.Message;
        }
        finally
        {
            IsDownloadingUpdate = false;
            Changed();
        }
    }

    public void OpenReleasePage()
    {
        if (AvailableUpdate?.PageUrl is { } url)
        {
            openUrl(url);
        }
    }

    /// <summary>Said once after the installer restarts the app.</summary>
    public void AnnounceUpdateResult(bool succeeded)
    {
        if (succeeded)
        {
            ShowTransientStatus($"Updated to {RunningVersionDescription}");
        }
        else
        {
            ErrorMessage = $"The update could not be installed, so nothing was changed. You are still on {RunningVersionDescription}.";
            Changed();
        }
    }

    // ------------------------------------------------------------------
    // The Canvas feed

    public void RefreshManually() => _ = RefreshAsync(RefreshTrigger.Manual, announcesResult: true);

    public async Task<IReadOnlyList<ImportPreviewItem>> PreviewFeedAsync(Uri feedUrl)
    {
        var preview = await refreshCoordinator.PreviewAsync(feedUrl, Now);
        parsedPreviewEvents.Clear();

        var endOfDay = settingsStore.TreatsMidnightAsEndOfDay;
        var items = new List<ImportPreviewItem>();
        for (var index = 0; index < preview.Events.Count; index++)
        {
            var parsed = preview.Events[index];
            var id = PreviewIdentifier(parsed, index);
            parsedPreviewEvents[id] = parsed;
            var (title, courseName) = AssignmentMetadata(parsed.Summary);
            items.Add(new ImportPreviewItem
            {
                Id = id,
                Title = title,
                CourseName = courseName,
                // Shown the way it will be listed once imported, so the
                // preview is not the one place still saying midnight.
                DueDate = DueTimePolicy.EffectiveImportedDueDate(parsed.StartDate, endOfDay, Calendar),
                Details = parsed.Description,
            });
        }
        return items;
    }

    public async Task<ImportSummary> ImportFeedAsync(Uri feedUrl, IReadOnlySet<string> selectedIds)
    {
        var selected = selectedIds
            .Where(parsedPreviewEvents.ContainsKey)
            .Select(id => parsedPreviewEvents[id])
            .ToArray();
        var result = await refreshCoordinator.ImportSelectedAsync(selected, feedUrl, Now);

        storedFeedUrlString = feedUrl.AbsoluteUri;
        SettingsForm = SettingsForm with { FeedUrl = feedUrl.AbsoluteUri };
        await ReloadAssignmentsAndSynchronizeAsync();
        StartAutomaticRefreshLoop();
        var summary = ImportSummary.From(result.ImportResult);
        StatusMessage = summary.Message;
        Changed();
        return summary;
    }

    public async Task RemoveFeedAsync()
    {
        await feedUrlStore.DeleteFeedUrlAsync();
        await refreshCoordinator.ClearImportSelectionsAsync();
        storedFeedUrlString = string.Empty;
        SettingsForm = SettingsForm with { FeedUrl = string.Empty };
        StartAutomaticRefreshLoop();
        StatusMessage = "Canvas feed removed";
        Changed();
    }

    private async Task RefreshAsync(RefreshTrigger trigger, bool announcesResult)
    {
        if (IsRefreshing || !HasConfiguredFeed)
        {
            if (announcesResult && !HasConfiguredFeed)
            {
                ErrorMessage = new RefreshCoordinatorException(RefreshCoordinatorError.NoSavedFeedUrl).Message;
                Changed();
            }
            return;
        }

        IsRefreshing = true;
        ErrorMessage = null;
        Changed();
        try
        {
            var result = await refreshCoordinator.RefreshAsync(Now, trigger);
            LastRefreshDate = result.RefreshedAt;
            await ReloadAssignmentsAndSynchronizeAsync();
            if (announcesResult)
            {
                StatusMessage = ImportSummary.From(result.ImportResult).Message;
            }
        }
        catch (Exception error)
        {
            Present(error);
        }
        finally
        {
            IsRefreshing = false;
            Changed();
        }
    }

    // ------------------------------------------------------------------
    // Keeping everything in step

    private async Task ReloadAssignmentsAndSynchronizeAsync()
    {
        var snapshots = await repository.FetchAllAsync();
        Apply(snapshots);
        SynchronizeDock();
        StartCountdownTransitionLoop();
        await ScheduleNotificationsBestEffortAsync(snapshots);
    }

    private void Apply(IReadOnlyList<AssignmentSnapshot> snapshots)
    {
        var endOfDay = settingsStore.TreatsMidnightAsEndOfDay;
        var calendar = Calendar;
        Assignments = snapshots
            .Select(snapshot => AssignmentListItem.FromSnapshot(snapshot, endOfDay, calendar))
            .Order(Comparer<AssignmentListItem>.Create(CompareByDueDateThenTitle))
            .ToArray();

        if (SelectedCourse is { } course && !AvailableCourses.Contains(course))
        {
            selectedCourse = null;
        }
        Changed();
    }

    private IReadOnlyList<AssignmentListItem> DockEligibleAssignments => CourseScopedAssignments;

    /// <summary>The countdown the tray shows, re-read now.</summary>
    public CountdownSelection<AssignmentListItem>? DockSelection
        => CountdownCalculator.NearestUpcoming(DockEligibleAssignments, CurrentDate, Calendar);

    private void SynchronizeDock()
    {
        CurrentDate = Now;
        var selection = DockSelection;
        dockRenderer.Render(selection?.DaysRemaining, settingsStore.DockLabel, selection?.Event);
        Changed();
    }

    private async Task SynchronizeNotificationsAsync()
    {
        try
        {
            var snapshots = await repository.FetchAllAsync();
            await ScheduleNotificationsAsync(snapshots);
        }
        catch (Exception error)
        {
            // Reminder failures should not roll back saved assignments or
            // settings, but they remain visible and actionable to the user.
            Present(error);
        }
    }

    private Task ScheduleNotificationsAsync(IReadOnlyList<AssignmentSnapshot> snapshots)
    {
        var endOfDay = settingsStore.TreatsMidnightAsEndOfDay;
        var calendar = Calendar;
        return notificationScheduler.RescheduleAsync(
            snapshots.Select(snapshot => NotificationCandidate.FromSnapshot(snapshot, endOfDay, calendar)).ToArray(),
            settingsStore.ReminderSchedule,
            Now,
            calendar);
    }

    private async Task ScheduleNotificationsBestEffortAsync(IReadOnlyList<AssignmentSnapshot> snapshots)
    {
        try
        {
            await ScheduleNotificationsAsync(snapshots);
        }
        catch (Exception error)
        {
            // The assignment change has already committed. Surface the
            // reminder failure without making the editor retry and create a
            // duplicate manual event.
            Present(error);
        }
    }

    private void StartAutomaticRefreshLoop()
    {
        automaticRefreshLoop?.Cancel();
        automaticRefreshLoop = null;
        if (!automaticActivityEnabled || !HasConfiguredFeed)
        {
            return;
        }
        var cancellation = new CancellationTokenSource();
        automaticRefreshLoop = cancellation;
        _ = RunAutomaticRefreshLoopAsync(cancellation.Token);
    }

    private async Task RunAutomaticRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(settingsStore.RefreshInterval.Duration(), clock, cancellationToken);
                await RefreshAsync(RefreshTrigger.Automatic, announcesResult: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartCountdownTransitionLoop()
    {
        countdownTransitionLoop?.Cancel();
        countdownTransitionLoop = null;
        if (!automaticActivityEnabled)
        {
            return;
        }
        var cancellation = new CancellationTokenSource();
        countdownTransitionLoop = cancellation;
        _ = RunCountdownTransitionLoopAsync(cancellation.Token);
    }

    /// <summary>
    /// Wakes at the next instant the countdown can change — local midnight, or
    /// the moment the nearest deadline passes — so an overdue assignment never
    /// sits in the tray until the next refresh.
    /// </summary>
    private async Task RunCountdownTransitionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = Now;
                if (CountdownCalculator.NextTransitionDate(DockEligibleAssignments, now, Calendar) is not { } transition)
                {
                    return;
                }
                var wait = transition - now + TimeSpan.FromMilliseconds(250);
                if (wait < TimeSpan.FromMilliseconds(250))
                {
                    wait = TimeSpan.FromMilliseconds(250);
                }
                await Task.Delay(wait, clock, cancellationToken);
                SynchronizeDock();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Called when Windows reports the clock or the time zone changed, or the
    /// PC woke from sleep: a timer that slept through midnight would otherwise
    /// leave yesterday's number in the tray.
    /// </summary>
    public void SystemTimeDidChange()
    {
        TimeZoneInfo.ClearCachedData();
        _ = ReloadAfterSettingsChangeAsync();
        StartCountdownTransitionLoop();
    }

    // ------------------------------------------------------------------
    // Notifications, data, diagnostics

    public async Task<bool> RequestNotificationPermissionAsync()
    {
        // Never ask again after a refusal: nothing would appear, and repeating
        // the request would look like the button is broken.
        if (!CanRequestNotificationPermission)
        {
            return NotificationPermission == NotificationAuthorizationStatus.Authorized;
        }
        var granted = await notificationScheduler.RequestAuthorizationAsync();
        NotificationPermission = await notificationScheduler.AuthorizationStatusAsync();
        if (granted)
        {
            await SynchronizeNotificationsAsync();
        }
        Changed();
        return granted;
    }

    /// <summary>Re-reads whether Windows will show reminders, for when the user comes back from Settings.</summary>
    public async Task RefreshNotificationPermissionAsync()
    {
        NotificationPermission = await notificationScheduler.AuthorizationStatusAsync();
        Changed();
    }

    public async Task ResetLocalDataAsync()
    {
        await repository.DeleteAllAsync();
        await feedUrlStore.DeleteFeedUrlAsync();
        await refreshCoordinator.ClearImportSelectionsAsync();
        await courseBlocklist.ClearBlockedCoursesAsync();
        await RefreshBlockedCoursesAsync();
        await notificationScheduler.CancelAllAsync();

        if (launchAtLogin.IsEnabled)
        {
            launchAtLogin.SetEnabled(false);
        }
        settingsStore.Reset();

        storedFeedUrlString = string.Empty;
        SettingsForm = SettingsFormState.From(settingsStore.Snapshot, feedUrl: null);
        Assignments = [];
        selectedCourse = null;
        // Nothing survives the reset, so there is nothing left to undo.
        LastAddition = null;
        DismissUndoToast();
        SynchronizeDock();
        StartAutomaticRefreshLoop();
        StartCountdownTransitionLoop();
        StatusMessage = "Local data reset";
        Changed();
    }

    public async Task<string> ExportDiagnosticsAsync()
    {
        var diagnostic = await refreshCoordinator.DiagnosticSnapshotAsync();
        var lines = new List<string>
        {
            "Canvas Countdown Diagnostics",
            $"generatedAt={Now:O}",
            $"system={RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}",
            $"version={RunningVersionDescription}",
            $"feedConfigured={HasConfiguredFeed.ToString().ToLowerInvariant()}",
            $"assignmentCount={Assignments.Count}",
            $"completedCount={Assignments.Count(item => item.IsCompleted)}",
            $"ignoredCount={Assignments.Count(item => item.IsIgnored)}",
            $"manualCount={Assignments.Count(item => item.IsManual)}",
            $"refreshInterval={(int)settingsStore.RefreshInterval}",
            $"reminderRuleCount={settingsStore.ReminderSchedule.Rules.Count}",
            $"enabledReminderOffsetsMinutes=[{string.Join(", ", settingsStore.ReminderSchedule.EnabledRules.Select(rule => rule.OffsetMinutes).Order())}]",
            $"countdownLanguage={JsonNamingCamel(settingsStore.DockDisplayLanguage.ToString())}",
            $"countdownNumberSize={JsonNamingCamel(settingsStore.DockAppearance.NumberSize.ToString())}",
            $"countdownTheme={JsonNamingCamel(settingsStore.DockAppearance.Preset.ToString())}",
            $"assistantEnabled={settingsStore.Assistant.IsEnabled.ToString().ToLowerInvariant()}",
            $"assistantProvider={JsonNamingCamel(settingsStore.Assistant.Provider.ToString())}",
            $"assistantStaysLocal={settingsStore.Assistant.StaysOnThisMachine.ToString().ToLowerInvariant()}",
            $"countdownCountMode={JsonNamingCamel(settingsStore.DockCountMode.ToString())}",
            $"selectedCourseCount={settingsStore.SelectedCourses.Count}",
            $"launchAtLogin={launchAtLogin.IsEnabled.ToString().ToLowerInvariant()}",
            $"notificationPermission={JsonNamingCamel(NotificationPermission.ToString())}",
        };
        if (diagnostic is not null)
        {
            lines.Add(string.Empty);
            lines.Add("Last Feed Operation");
            lines.Add(diagnostic.ExportText);
        }
        return DiagnosticRedactor.Redact(string.Join("\n", lines));
    }

    private static string JsonNamingCamel(string name)
        => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    // ------------------------------------------------------------------
    // Helpers for the feed preview

    private static string PreviewIdentifier(ParsedCalendarEvent parsed, int index)
    {
        var stable = parsed.Uid ?? parsed.Url ?? $"{parsed.Summary}|{parsed.StartDate.ToUnixTimeMilliseconds()}";
        return $"{index}|{stable}";
    }

    /// <summary>
    /// Canvas titles its feed entries "Title [Course]", sometimes with an
    /// "Assignment:" prefix. The course is split off only when what is inside
    /// the brackets is plausibly a course name.
    /// </summary>
    internal static (string Title, string? CourseName) AssignmentMetadata(string summary)
    {
        var title = summary.Trim();
        if (title.StartsWith("assignment:", StringComparison.OrdinalIgnoreCase))
        {
            title = title["assignment:".Length..].Trim();
        }

        var openingBracket = title.LastIndexOf('[');
        if (!title.EndsWith(']') || openingBracket <= 0)
        {
            return (title, null);
        }

        var course = title[(openingBracket + 1)..^1].Trim();
        var assignmentTitle = title[..openingBracket].Trim();
        if (course.Length == 0 || course.Length > 120 || assignmentTitle.Length == 0)
        {
            return (title, null);
        }
        return (assignmentTitle, course);
    }
}

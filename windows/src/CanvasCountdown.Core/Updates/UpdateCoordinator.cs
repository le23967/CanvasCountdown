using CommunityToolkit.Mvvm.ComponentModel;

namespace CanvasCountdown;

/// <summary>
/// The two things the update check remembers between launches.
/// </summary>
/// <remarks>
/// Both are bookkeeping and an answer the user gave, not preferences: neither
/// belongs in the settings snapshot, and neither is touched by resetting
/// settings. Exactly one object may own them. A settings store that writes
/// every key back from its own copy whenever anything changes would otherwise
/// put back a stale dismissed version — and asking again about the version
/// somebody just waved away is how an update notice becomes something people
/// learn to ignore.
/// </remarks>
public interface IUpdateCheckRecord
{
    /// <summary>A version someone has said they are not interested in.</summary>
    string? DismissedUpdateVersion { get; set; }

    /// <summary>When the last check happened, so a day's gap survives a relaunch.</summary>
    DateTimeOffset? LastUpdateCheck { get; set; }
}

/// <summary>The update record kept in the preferences, under the Mac app's keys.</summary>
public sealed class PreferencesUpdateCheckRecord : IUpdateCheckRecord
{
    public const string DismissedUpdateVersionKey = "CanvasCountdown.settings.dismissedUpdateVersion";

    public const string LastUpdateCheckKey = "CanvasCountdown.settings.lastUpdateCheck";

    private readonly IPreferencesStore store;

    public PreferencesUpdateCheckRecord(IPreferencesStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string? DismissedUpdateVersion
    {
        get => store.TryGet<string>(DismissedUpdateVersionKey, out var version) ? version : null;
        set => Write(DismissedUpdateVersionKey, value);
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => store.GetOptional<DateTimeOffset>(LastUpdateCheckKey);
        set
        {
            if (value is { } stamp)
            {
                store.Set(LastUpdateCheckKey, stamp);
            }
            else
            {
                store.Remove(LastUpdateCheckKey);
            }
        }
    }

    private void Write(string key, string? value)
    {
        if (value is null)
        {
            store.Remove(key);
        }
        else
        {
            store.Set(key, value);
        }
    }
}

/// <summary>Keeping up with what has been published.</summary>
/// <remarks>
/// The update half of the Mac app's main view model, kept whole so its rules
/// cannot be half-ported: a check at most once a day, a dismissed version that
/// stays dismissed, and a button that always answers. Used from the UI thread,
/// as the view model it came from was.
/// </remarks>
public sealed partial class UpdateCoordinator : ObservableObject
{
    /// <summary>
    /// How long a check lasts before another one is worth making. A day: this
    /// app is published a few times a month at most, and asking GitHub more
    /// often would spend someone's network on an answer that cannot have
    /// changed. Elapsed time, not a calendar day.
    /// </summary>
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromSeconds(86_400);

    private readonly IReleaseChecking releaseChecker;
    private readonly IUpdateDownloading updateDownloader;
    private readonly IUpdateCheckRecord record;
    private readonly Func<AppVersion?> currentVersion;
    private readonly Action<Uri> openUrl;
    private readonly TimeProvider timeProvider;

    /// <param name="releaseChecker">Where releases are read from.</param>
    /// <param name="updateDownloader">What fetches and checks the installer.</param>
    /// <param name="record">Where the last check and the dismissed version are kept.</param>
    /// <param name="currentVersion">The running app's version. Never the entry assembly's under a test runner.</param>
    /// <param name="openUrl">Opens a page in the browser.</param>
    /// <param name="timeProvider">The clock for checks that do not pass one.</param>
    public UpdateCoordinator(
        IReleaseChecking releaseChecker,
        IUpdateDownloading updateDownloader,
        IUpdateCheckRecord record,
        Func<AppVersion?> currentVersion,
        Action<Uri> openUrl,
        TimeProvider? timeProvider = null)
    {
        this.releaseChecker = releaseChecker ?? throw new ArgumentNullException(nameof(releaseChecker));
        this.updateDownloader = updateDownloader ?? throw new ArgumentNullException(nameof(updateDownloader));
        this.record = record ?? throw new ArgumentNullException(nameof(record));
        this.currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        this.openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A newer release than the one running, that has not been waved away.</summary>
    [ObservableProperty]
    public partial AppRelease? AvailableUpdate { get; private set; }

    [ObservableProperty]
    public partial bool IsCheckingForUpdate { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloadingUpdate { get; private set; }

    [ObservableProperty]
    public partial string? UpdateMessage { get; private set; }

    /// <summary>
    /// Set only by the manual check, so a background check that finds nothing
    /// stays silent while the button always answers.
    /// </summary>
    [ObservableProperty]
    public partial string? UpdateCheckAnnouncement { get; private set; }

    public AppVersion? RunningVersion => currentVersion();

    public string RunningVersionDescription => RunningVersion?.ToString() ?? "unknown";

    /// <summary>Whether enough time has passed to ask again.</summary>
    /// <remarks>
    /// A check stamped in the future counts as stale: a clock put back must not
    /// lock the check out until it catches up. Somebody whose PC had the wrong
    /// date would otherwise stop being told about updates for as long as the
    /// stamp stayed ahead.
    /// </remarks>
    public bool ShouldCheckForUpdate(DateTimeOffset now)
    {
        if (record.LastUpdateCheck is not { } last)
        {
            return true;
        }
        return now - last >= UpdateCheckInterval || last > now;
    }

    public bool ShouldCheckForUpdate() => ShouldCheckForUpdate(timeProvider.GetUtcNow());

    public Task CheckForUpdateAsync(bool force = false, CancellationToken cancellationToken = default)
        => CheckForUpdateAsync(force, timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>Looks for something newer.</summary>
    /// <remarks>
    /// Silent by design when it is the launch check: an app that opens with a
    /// message every day is an app people stop reading. Only a newer version
    /// than the running one, and only one that has not been dismissed, puts
    /// anything on screen. <paramref name="force"/> is the button: it ignores
    /// the daily gate, reconsiders a dismissed version, and always answers.
    /// </remarks>
    public async Task CheckForUpdateAsync(bool force, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (IsCheckingForUpdate)
        {
            return;
        }
        if (!force && !ShouldCheckForUpdate(now))
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

        try
        {
            var latest = await releaseChecker.LatestReleaseAsync(cancellationToken);
            record.LastUpdateCheck = now;

            if (latest is null || !(latest.Version > running))
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
                record.DismissedUpdateVersion = null;
            }
            if (record.DismissedUpdateVersion == latest.Version.ToString())
            {
                return;
            }
            AvailableUpdate = latest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A launch check that cannot reach GitHub says nothing. Being
            // offline is not news, and it is not the user's problem to solve.
            if (force)
            {
                UpdateCheckAnnouncement = exception.Message;
            }
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    /// <summary>The button. Always answers, through <see cref="UpdateCheckAnnouncement"/> or <see cref="AvailableUpdate"/>.</summary>
    public void CheckForUpdateManually() => _ = CheckForUpdateAsync(force: true);

    /// <summary>Puts this version away until there is a newer one.</summary>
    public void DismissUpdate()
    {
        record.DismissedUpdateVersion = AvailableUpdate?.Version.ToString();
        AvailableUpdate = null;
        UpdateMessage = null;
    }

    public void ClearUpdateMessage()
    {
        UpdateMessage = null;
        UpdateCheckAnnouncement = null;
    }

    /// <summary>
    /// Downloads the installer, checks it, and hands it on; a release without
    /// an installer opens its page instead.
    /// </summary>
    /// <remarks>
    /// A failed download says so and keeps the offer up, rather than clearing
    /// the banner as though it had worked.
    /// </remarks>
    public async Task DownloadUpdateAsync(CancellationToken cancellationToken = default)
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
        try
        {
            var file = await updateDownloader.DownloadAsync(release, cancellationToken);
            updateDownloader.Reveal(file);
            UpdateMessage = $"{release.Version} is in your Downloads. Run the installer to finish.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateMessage = exception.Message;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    public void OpenReleasePage()
    {
        if (AvailableUpdate?.PageUrl is not { } url)
        {
            return;
        }
        openUrl(url);
    }
}

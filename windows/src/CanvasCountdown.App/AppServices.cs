using System.Diagnostics;
using System.Runtime.InteropServices;
using CanvasCountdown.App.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CanvasCountdown.App;

/// <summary>
/// What the app is made of, decided once at launch.
/// </summary>
/// <remarks>
/// An ordinary launch uses the real things: the database and preferences in
/// %LOCALAPPDATA%\CanvasCountdown, Credential Manager, scheduled toasts, the
/// tray. An isolated launch (<see cref="AppEnvironment.IsIsolatedLaunch"/>)
/// swaps every one that could touch the user's data or the network for an
/// in-memory or inert stand-in, so the UI tests can drive the real window
/// without reading the feed URL, contacting Canvas, scheduling a reminder or
/// changing the startup entry.
/// </remarks>
internal sealed class AppServices : IDisposable
{
    private static AppServices? current;
    private readonly TrayCountdown tray;
    private readonly ScreenshotImportCoordinator screenshotCoordinator;
    private MainWindow? window;
    private bool hasAnnouncedStillRunning;
    private bool isQuitting;

    private AppServices(
        DispatcherQueue dispatcher,
        MainViewModel viewModel,
        ScreenshotImportViewModel screenshotImport,
        ScreenshotImportCoordinator screenshotCoordinator,
        TrayCountdown tray,
        ToastReminders? reminders,
        IPreferencesStore preferences)
    {
        Dispatcher = dispatcher;
        ViewModel = viewModel;
        ScreenshotImport = screenshotImport;
        this.screenshotCoordinator = screenshotCoordinator;
        this.tray = tray;
        Reminders = reminders;
        Preferences = preferences;
    }

    public static AppServices Current => current ?? throw new InvalidOperationException("Canvas Countdown has not started yet.");

    public DispatcherQueue Dispatcher { get; }

    public MainViewModel ViewModel { get; }

    public ScreenshotImportViewModel ScreenshotImport { get; }

    public ToastReminders? Reminders { get; }

    public IPreferencesStore Preferences { get; }

    /// <summary>The main window's handle, for the file picker and anything else Windows asks to parent.</summary>
    public IntPtr WindowHandle => window?.Handle ?? IntPtr.Zero;

    /// <summary>Takes back a screenshot import: removes what it added and puts back what it changed.</summary>
    public async Task UndoScreenshotImportAsync(ScreenshotImportCommit commit)
        => await screenshotCoordinator.UndoAsync(commit, DateTimeOffset.UtcNow);

    public static AppServices Start(DispatcherQueue dispatcher)
    {
        current = AppEnvironment.IsIsolatedLaunch ? CreateIsolated(dispatcher) : CreateForReal(dispatcher);
        return current;
    }

    private static UpdateArchitecture Architecture
        => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? UpdateArchitecture.Arm64
            : UpdateArchitecture.X64;

    private static AppServices CreateForReal(DispatcherQueue dispatcher)
    {
        AppIdentity.EnsureRegistered();
        var startup = new StartupRegistration();
        startup.RefreshPathIfEnabled();

        var data = AppEnvironment.DataDirectory;
        var preferences = new JsonPreferencesStore(Path.Combine(data, "settings.json"));
        var settings = new SettingsStore(preferences);
        var secrets = new CredentialManagerSecretStore();
        var repository = new SqliteAssignmentRepository(Path.Combine(data, "CanvasCountdown.sqlite"));
        var feedUrlStore = new SecretFeedUrlStore(secrets);
        var blocklist = new PreferencesCourseBlocklistStore(preferences);
        var coordinator = new RefreshCoordinator(
            new CanvasFeedFetcher(HttpClients.CreateFeedHandler()),
            new IcsParser(),
            repository,
            feedUrlStore,
            new PreferencesFeedExclusionStore(preferences),
            blocklist);
        var reminders = new ToastReminders();
        var tray = new TrayCountdown(dispatcher, settings);

        AppServices? services = null;
        var viewModel = new MainViewModel(new MainViewModelDependencies
        {
            Repository = repository,
            RefreshCoordinator = coordinator,
            FeedUrlStore = feedUrlStore,
            SettingsStore = settings,
            DockRenderer = tray,
            NotificationScheduler = new NotificationService(reminders),
            AssistantKeyStore = new AssistantKeyStore(secrets),
            LocalModels = new OllamaModelManager(),
            CourseBlocklist = blocklist,
            AssistantFactory = (assistant, key, calendar) => new ChatCompletionsAssistantService(assistant, key, calendar),
            ReleaseChecker = new GitHubReleaseChecker(Architecture),
            UpdateDownloader = new DownloadsFolderUpdateDownloader(Path.Combine(data, "Updates"), reveal: RevealInExplorer),
            UpdateInstaller = new InstallerUpdateInstaller(() => services?.Quit(closeWindow: true)),
            LaunchAtLogin = startup,
            CurrentVersion = () => AppVersion.Parse(AppEnvironment.Version),
            OpenUrl = OpenInBrowser,
        });
        var screenshotCoordinator = new ScreenshotImportCoordinator(new WindowsOcrRecognizer(), repository);
        var screenshotImport = new ScreenshotImportViewModel(
            screenshotCoordinator,
            () => viewModel.CurrentSnapshotsAsync(),
            TimeProvider.System,
            () => AppCalendar.Current);

        services = new AppServices(dispatcher, viewModel, screenshotImport, screenshotCoordinator, tray, reminders, preferences);
        return services;
    }

    private static AppServices CreateIsolated(DispatcherQueue dispatcher)
    {
        var preferences = JsonPreferencesStore.InMemory();
        var settings = new SettingsStore(preferences);
        var secrets = new InMemorySecretStore();
        var repository = SqliteAssignmentRepository.InMemory();
        var feedUrlStore = new SecretFeedUrlStore(secrets);
        var blocklist = new PreferencesCourseBlocklistStore(preferences);
        var coordinator = new RefreshCoordinator(
            new OfflineFeedFetcher(),
            new IcsParser(),
            repository,
            feedUrlStore,
            new PreferencesFeedExclusionStore(preferences),
            blocklist);
        var tray = new TrayCountdown(dispatcher, settings);

        AppServices? services = null;
        var viewModel = new MainViewModel(new MainViewModelDependencies
        {
            Repository = repository,
            RefreshCoordinator = coordinator,
            FeedUrlStore = feedUrlStore,
            SettingsStore = settings,
            DockRenderer = tray,
            NotificationScheduler = new NotificationService(new InertNotificationDelivery()),
            AssistantKeyStore = new AssistantKeyStore(secrets),
            LocalModels = new OllamaModelManager(),
            CourseBlocklist = blocklist,
            AssistantFactory = (assistant, key, calendar) => new DemoAssistantService(calendar),
            // Nothing in an isolated launch may reach the network.
            ReleaseChecker = new InertReleaseChecker(),
            UpdateDownloader = new InertUpdateDownloader(),
            LaunchAtLogin = new InMemoryLaunchAtLogin(),
            CurrentVersion = () => AppVersion.Parse(AppEnvironment.Version),
            OpenUrl = _ => { },
            AutomaticActivityEnabled = false,
        });
        var screenshotCoordinator = new ScreenshotImportCoordinator(new WindowsOcrRecognizer(), repository);
        var screenshotImport = new ScreenshotImportViewModel(
            screenshotCoordinator,
            () => viewModel.CurrentSnapshotsAsync(),
            TimeProvider.System,
            () => AppCalendar.Current);

        if (Environment.GetEnvironmentVariable("CANVAS_COUNTDOWN_DEMO_DATA") == "1")
        {
            DemoData.SeedAsync(repository, settings).GetAwaiter().GetResult();
        }

        services = new AppServices(dispatcher, viewModel, screenshotImport, screenshotCoordinator, tray, reminders: null, preferences);
        return services;
    }

    public void Attach(MainWindow mainWindow)
    {
        window = mainWindow;
        tray.Attach(mainWindow, quit: () => Quit(closeWindow: true), refresh: ViewModel.RefreshManually);
        tray.SystemTimeChanged += (_, _) => ViewModel.SystemTimeDidChange();
    }

    public void WindowDidLoad(MainWindow mainWindow) => tray.WindowDidLoad(mainWindow);

    /// <summary>Launch arguments, from this launch or one handed over: reminder links, and the installer's verdict.</summary>
    public void HandleArguments(IReadOnlyList<string> arguments, bool isFirstLaunch)
    {
        foreach (var argument in arguments)
        {
            if (AppIdentity.EventIdFromLink(argument) is { } eventId)
            {
                _ = OpenEventWhenLoadedAsync(eventId);
            }
        }
        if (isFirstLaunch && arguments.Contains("--updated", StringComparer.OrdinalIgnoreCase))
        {
            ViewModel.AnnounceUpdateResult(succeeded: true);
        }
        if (isFirstLaunch && arguments.Contains("--update-failed", StringComparer.OrdinalIgnoreCase))
        {
            ViewModel.AnnounceUpdateResult(succeeded: false);
        }
    }

    private async Task OpenEventWhenLoadedAsync(Guid eventId)
    {
        // The list may still be loading when a reminder is what started the app.
        for (var attempt = 0; attempt < 50 && (ViewModel.IsLoading || ViewModel.Assignments.Count == 0); attempt++)
        {
            await Task.Delay(100);
        }
        ViewModel.OpenEvent(eventId);
    }

    /// <summary>
    /// Says once — the first time the window is closed — that the app is still
    /// running and where to find it, so closing does not look like quitting
    /// and the countdown does not look like it vanished.
    /// </summary>
    public void AnnounceStillRunningOnce()
    {
        const string key = "CanvasCountdown.windows.announcedStillRunning";
        if (hasAnnouncedStillRunning || Preferences.Get(key, false))
        {
            return;
        }
        hasAnnouncedStillRunning = true;
        Preferences.Set(key, true);
        Reminders?.ShowNow(
            "Canvas Countdown is still running",
            "The countdown stays in the notification area by the clock. Click it to open the window, or right-click it to quit. You can change this in Settings.");
    }

    public void Quit(bool closeWindow)
    {
        if (isQuitting)
        {
            return;
        }
        isQuitting = true;
        tray.Dispose();
        ViewModel.Dispose();
        if (closeWindow)
        {
            window?.CloseForGood();
        }
        Application.Current.Exit();
    }

    private static void OpenInBrowser(Uri url)
    {
        if (url.Scheme is "https" or "http")
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private static void RevealInExplorer(string file)
        => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", file }, UseShellExecute = false });

    public void Dispose()
    {
        tray.Dispose();
        ViewModel.Dispose();
    }
}

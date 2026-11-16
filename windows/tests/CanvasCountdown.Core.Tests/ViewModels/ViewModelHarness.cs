using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CanvasCountdown.Tests.Assistant;
using CanvasCountdown.Tests.Updates;
using Microsoft.Extensions.Time.Testing;

namespace CanvasCountdown.Tests.ViewModels;

/// <summary>
/// A <see cref="MainViewModel"/> built from real parts wherever a real part
/// can run in a test: the SQLite database (in memory), the settings store,
/// the feed parser and refresh coordinator, the course blocklist. Only what
/// reaches outside the process is replaced: the download, the tray, Windows
/// notifications, the release check and the installer.
/// </summary>
/// <remarks>
/// The clock is fake and starts at 10:00 on Tuesday 28 July 2026 in Sydney,
/// so "in three days" means the same thing on every machine and the
/// view model's own timers (the undo countdown, the debounced reschedule) run
/// only when a test moves the clock.
/// </remarks>
internal sealed class ViewModelHarness : IDisposable
{
    public static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");
    public static readonly CultureInfo Australian = CultureInfo.GetCultureInfo("en-AU");

    /// <summary>10:00 in Sydney on 28 July 2026.</summary>
    public static readonly DateTimeOffset Start = new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

    public static readonly Uri FeedUrl = new("https://canvas.example.edu/feeds/calendar.ics?feed=private");

    public ViewModelHarness(Func<MainViewModelDependencies, MainViewModelDependencies>? customise = null)
    {
        Clock = new FakeTimeProvider(Start);
        Clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));
        Preferences = JsonPreferencesStore.InMemory();
        Secrets = new InMemorySecretStore();
        Repository = SqliteAssignmentRepository.InMemory();
        FeedUrlStore = new SecretFeedUrlStore(Secrets);
        Settings = new SettingsStore(Preferences);
        Blocklist = new PreferencesCourseBlocklistStore(Preferences);
        Coordinator = new RefreshCoordinator(
            Fetcher,
            new IcsParser(),
            Repository,
            FeedUrlStore,
            new PreferencesFeedExclusionStore(Preferences),
            Blocklist,
            () => Sydney.TimeZone);

        var dependencies = new MainViewModelDependencies
        {
            Repository = Repository,
            RefreshCoordinator = Coordinator,
            FeedUrlStore = FeedUrlStore,
            SettingsStore = Settings,
            DockRenderer = Dock,
            NotificationScheduler = Notifications,
            AssistantKeyStore = new AssistantKeyStore(Secrets),
            LocalModels = new StubModelManager(reachable: false),
            CourseBlocklist = Blocklist,
            AssistantFactory = (_, _, _) => Assistant,
            ReleaseChecker = Releases,
            UpdateDownloader = Downloads,
            UpdateInstaller = Installer,
            LaunchAtLogin = LaunchAtLogin,
            CurrentVersion = () => AppVersion.Parse("1.0.0"),
            OpenUrl = OpenedUrls.Add,
            Calendar = () => Sydney,
            Culture = () => Australian,
            Clock = Clock,
            AutomaticActivityEnabled = false,
        };
        ViewModel = new MainViewModel(customise is null ? dependencies : customise(dependencies));
    }

    public FakeTimeProvider Clock { get; }

    public JsonPreferencesStore Preferences { get; }

    public InMemorySecretStore Secrets { get; }

    public SqliteAssignmentRepository Repository { get; }

    public SecretFeedUrlStore FeedUrlStore { get; }

    public SettingsStore Settings { get; }

    public PreferencesCourseBlocklistStore Blocklist { get; }

    public RefreshCoordinator Coordinator { get; }

    public SwitchableFetcher Fetcher { get; } = new();

    public DockRendererSpy Dock { get; } = new();

    public NotificationSchedulerSpy Notifications { get; } = new();

    public AssistantStub Assistant { get; } = new();

    public ReleaseCheckerStub Releases { get; } = new();

    public UpdateDownloaderSpy Downloads { get; } = new();

    public UpdateInstallerSpy Installer { get; } = new();

    public InMemoryLaunchAtLogin LaunchAtLogin { get; } = new();

    public List<Uri> OpenedUrls { get; } = [];

    public MainViewModel ViewModel { get; }

    /// <summary>Noon in Sydney, <paramref name="days"/> calendar days from the start.</summary>
    public static DateTimeOffset Day(int days, int hour = 12, int minute = 0)
        => Sydney.FromLocal(Sydney.DateOf(Start).AddDays(days).ToDateTime(new TimeOnly(hour, minute)));

    /// <summary>Puts an event in the database as though a Canvas feed had delivered it.</summary>
    public async Task<AssignmentSnapshot> AddImportedAsync(string title, DateTimeOffset due, string? course = null, string? externalId = null)
    {
        var id = externalId ?? $"{title.Replace(' ', '-')}@canvas.example.edu";
        await Repository.UpsertAsync(
            [new AssignmentImportRecord { ExternalId = id, Title = title, CourseName = course, DueDate = due }],
            Start);
        return (await Repository.FetchAllAsync()).Single(snapshot => snapshot.ExternalId == id);
    }

    public async Task<AssignmentSnapshot> AddManualAsync(string title, DateTimeOffset due, string? course = null)
        => await Repository.SaveManualAsync(new ManualAssignmentDraft { Title = title, CourseName = course, DueDate = due }, Start);

    public async Task<AssignmentSnapshot> AddAsync(string title, DateTimeOffset due, string? course = null, bool completed = false, bool ignored = false, bool manual = false)
    {
        var snapshot = manual ? await AddManualAsync(title, due, course) : await AddImportedAsync(title, due, course);
        if (completed || ignored)
        {
            await Repository.UpdateStatusAsync(snapshot.Id, completed ? true : null, ignored ? true : null, Start);
        }
        return snapshot;
    }

    public AssignmentListItem Item(string title)
        => ViewModel.Assignments.FirstOrDefault(item => item.Title == title)
            ?? throw new Xunit.Sdk.XunitException($"No event called “{title}” is loaded. Loaded: {string.Join(", ", ViewModel.Assignments.Select(item => item.Title))}");

    /// <summary>Saves a feed URL the way connecting a feed does, so refreshes have somewhere to read from.</summary>
    public async Task SaveFeedAsync(string ics)
    {
        Fetcher.Payload = ics;
        await FeedUrlStore.SaveFeedUrlAsync(FeedUrl);
    }

    /// <summary>
    /// Waits, in real time, for work the view model started without awaiting
    /// (a manual refresh, a toggle from a menu) to show up in its state.
    /// </summary>
    public static async Task WaitUntil(Func<bool> condition, string description)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(5);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting until {description}.");
    }

    /// <summary>Moves the fake clock in small steps, letting each timer that falls due run before the next.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (var moved = TimeSpan.Zero; moved < by; moved += step)
        {
            Clock.Advance(by - moved < step ? by - moved : step);
            await Task.Yield();
            await Task.Delay(1);
        }
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        Repository.Dispose();
    }

    /// <summary>
    /// Runs a test as the window runs the view model: every continuation on one
    /// thread, in order, the way the UI thread would. Work that is still
    /// pending when the test ends is let go rather than raised.
    /// </summary>
    public static void OnUiThread(Func<Task> test)
    {
        var previous = SynchronizationContext.Current;
        var context = new SingleThread();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class SingleThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The test is over; nothing is left to observe it.
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;

        public void Complete() => queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach (var (callback, state) in queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}

/// <summary>A feed download whose answer the test sets, and changes, as it goes.</summary>
internal sealed class SwitchableFetcher : IFeedFetcher
{
    public string? Payload { get; set; }

    public Exception? Failure { get; set; }

    public int CallCount { get; private set; }

    public Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (Failure is not null)
        {
            return Task.FromException<byte[]>(Failure);
        }
        if (Payload is null)
        {
            return Task.FromException<byte[]>(CanvasFeedFetchException.EmptyResponse());
        }
        return Task.FromResult(Encoding.UTF8.GetBytes(Payload));
    }
}

/// <summary>Remembers every countdown the tray was asked to draw.</summary>
internal sealed class DockRendererSpy : IDockRenderer
{
    public List<(int? Days, string Label, AssignmentListItem? Item)> Renders { get; } = [];

    public List<DockAppearance> Appearances { get; } = [];

    public int? LastDays => Renders.Count == 0 ? null : Renders[^1].Days;

    public string? LastLabel => Renders.Count == 0 ? null : Renders[^1].Label;

    public void Render(int? daysRemaining, string label, AssignmentListItem? item) => Renders.Add((daysRemaining, label, item));

    public void Apply(DockAppearance appearance) => Appearances.Add(appearance);
}

internal sealed class NotificationSchedulerSpy : INotificationScheduling
{
    public NotificationAuthorizationStatus Status { get; set; } = NotificationAuthorizationStatus.Authorized;

    public List<(IReadOnlyList<NotificationCandidate> Candidates, ReminderSchedule Schedule)> Reschedules { get; } = [];

    public Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Status);

    public Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Status == NotificationAuthorizationStatus.Authorized);

    public Task RescheduleAsync(
        IReadOnlyList<NotificationCandidate> candidates,
        ReminderSchedule schedule,
        DateTimeOffset now,
        AppCalendar calendar,
        CancellationToken cancellationToken = default)
    {
        Reschedules.Add((candidates, schedule));
        return Task.CompletedTask;
    }

    public int CancelAllCount { get; private set; }

    public Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        CancelAllCount++;
        return Task.CompletedTask;
    }
}

internal sealed class UpdateInstallerSpy : IUpdateInstaller
{
    public Exception? Failure { get; set; }

    public List<(string Path, string Sha256)> Installs { get; } = [];

    public void InstallAndRelaunch(string installerPath, string expectedSha256)
    {
        if (Failure is not null)
        {
            throw Failure;
        }
        Installs.Add((installerPath, expectedSha256));
    }
}

/// <summary>An assistant that answers from a script and records what it was asked.</summary>
internal sealed class AssistantStub : IAssistantServicing
{
    public string Answer { get; set; } = "Alpha is due first.";

    public ValueList<AssistantDraftTask> Drafts { get; set; } = [];

    public List<string> Questions { get; } = [];

    public List<IReadOnlyList<AssistantAssignmentDigest>> DigestsSeen { get; } = [];

    public Task<string> AnswerAsync(
        string question,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Questions.Add(question);
        DigestsSeen.Add(digests);
        return Task.FromResult(Answer);
    }

    public Task<string> SummariseAsync(IReadOnlyList<AssistantAssignmentDigest> digests, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        DigestsSeen.Add(digests);
        return Task.FromResult(Answer);
    }

    public Task<ValueList<AssistantDraftTask>> DraftTasksAsync(string text, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Questions.Add(text);
        return Task.FromResult(Drafts);
    }

    public Task<ValueList<AssistantDraftTask>> ReviseDraftsAsync(
        IReadOnlyList<AssistantDraftTask> drafts,
        string instruction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Questions.Add(instruction);
        return Task.FromResult(Drafts);
    }
}

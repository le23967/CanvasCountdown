using System.Text;

namespace CanvasCountdown.Tests.Feed;

/// <summary>Hands out a scripted sequence of downloads, repeating the last one.</summary>
internal sealed class ScriptedFetcher : IFeedFetcher
{
    private readonly IReadOnlyList<Func<byte[]>> steps;
    private int next;

    public ScriptedFetcher(params Func<byte[]>[] steps)
    {
        this.steps = steps;
    }

    public static ScriptedFetcher Returning(params string[] payloads)
        => new(payloads.Select(payload => (Func<byte[]>)(() => Encoding.UTF8.GetBytes(payload))).ToArray());

    public int CallCount { get; private set; }

    public Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (steps.Count == 0)
        {
            throw CanvasFeedFetchException.EmptyResponse();
        }
        var step = steps[Math.Min(next, steps.Count - 1)];
        next++;
        return Task.FromResult(step());
    }
}

/// <summary>A refresh coordinator over an in-memory database, as every feed test needs one.</summary>
internal sealed class FeedHarness : IDisposable
{
    public static readonly Uri FeedUrl = new("https://canvas.example.edu/feeds/calendar.ics?feed=private");

    public FeedHarness(IFeedFetcher fetcher, int archiveThreshold = FeedReconciliationRequest.DefaultArchiveThreshold, bool savesFeedUrl = true)
    {
        Preferences = JsonPreferencesStore.InMemory();
        Secrets = new InMemorySecretStore();
        Repository = SqliteAssignmentRepository.InMemory();
        FeedUrlStore = new SecretFeedUrlStore(Secrets);
        Exclusions = new PreferencesFeedExclusionStore(Preferences);
        Blocklist = new PreferencesCourseBlocklistStore(Preferences);
        if (savesFeedUrl)
        {
            FeedUrlStore.SaveFeedUrlAsync(FeedUrl).GetAwaiter().GetResult();
        }
        Coordinator = new RefreshCoordinator(
            fetcher,
            new IcsParser(),
            Repository,
            FeedUrlStore,
            Exclusions,
            Blocklist,
            () => TimeZoneInfo.Utc,
            missingRefreshArchiveThreshold: archiveThreshold);
    }

    public JsonPreferencesStore Preferences { get; }

    public InMemorySecretStore Secrets { get; }

    public SqliteAssignmentRepository Repository { get; }

    public SecretFeedUrlStore FeedUrlStore { get; }

    public PreferencesFeedExclusionStore Exclusions { get; }

    public PreferencesCourseBlocklistStore Blocklist { get; }

    public RefreshCoordinator Coordinator { get; }

    /// <summary>09:00 UTC on 28 July 2026, plus whole days.</summary>
    public static DateTimeOffset Day(int offset) => new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero).AddDays(offset);

    public void Dispose() => Repository.Dispose();
}

internal static class Ics
{
    public static string Calendar(string body)
        => string.Join("\r\n", "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Canvas Countdown Windows Tests//EN", body.ReplaceLineEndings("\r\n"), "END:VCALENDAR");

    public static string Event(string uid, string start, string summary, string? extra = null)
        => string.Join(
            "\r\n",
            new[] { "BEGIN:VEVENT", $"UID:{uid}", $"DTSTART:{start}", $"SUMMARY:{summary}", extra, "END:VEVENT" }.Where(line => line is not null));

    public static string Full => Calendar(
        Event("alpha@example.edu", "20260810T100000Z", "Assignment: Alpha task [COMP1000]") + "\r\n"
        + Event("beta@example.edu", "20260811T100000Z", "Assignment: Beta task [COMP1000]"));

    public static string Partial => Calendar(Event("alpha@example.edu", "20260810T100000Z", "Assignment: Alpha task [COMP1000]"));

    public static string Empty => Calendar(string.Empty).Replace("\r\n\r\n", "\r\n", StringComparison.Ordinal);
}

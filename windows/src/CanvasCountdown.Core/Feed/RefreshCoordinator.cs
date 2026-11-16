using System.Globalization;

namespace CanvasCountdown;

public sealed record FeedPreview
{
    public required ValueList<ParsedCalendarEvent> Events { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public required int ReceivedByteCount { get; init; }

    public required int ExcludedEventCount { get; init; }

    public required ValueSet<string> ActiveExternalIds { get; init; }

    public ValueSet<string> CancelledExternalIds { get; init; } = ValueSet<string>.Empty;

    /// <summary>
    /// A feed that parsed cleanly but described no events at all is treated as
    /// suspicious rather than authoritative. Absence from such a response must
    /// not count against anything already stored.
    /// </summary>
    public bool CarriesAuthoritativeEventList => !ActiveExternalIds.IsEmpty || !CancelledExternalIds.IsEmpty;
}

public enum RefreshTrigger
{
    Launch,
    Manual,
    Automatic,
    Onboarding,
}

public sealed record RefreshResult(ImportResult ImportResult, int ImportedEventCount, DateTimeOffset RefreshedAt);

public enum FeedRefreshOutcome
{
    Success,
    Failure,
}

/// <summary>What the last feed operation did, as numbers and codes only.</summary>
public sealed record FeedRefreshDiagnostic
{
    public required DateTimeOffset AttemptedAt { get; init; }

    public required RefreshTrigger Trigger { get; init; }

    public required FeedRefreshOutcome Outcome { get; init; }

    public int? ReceivedByteCount { get; init; }

    public int? ParsedEventCount { get; init; }

    public int? EligibleEventCount { get; init; }

    public int? InsertedCount { get; init; }

    public int? UpdatedCount { get; init; }

    public int? UnchangedCount { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>A line-based, deliberately secret-free diagnostic summary.</summary>
    public string ExportText
    {
        get
        {
            var lines = new List<string>
            {
                $"attemptedAt={AttemptedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}",
                $"trigger={Camel(Trigger.ToString())}",
                $"outcome={Camel(Outcome.ToString())}",
            };
            Add(lines, "receivedBytes", ReceivedByteCount);
            Add(lines, "parsedEvents", ParsedEventCount);
            Add(lines, "eligibleEvents", EligibleEventCount);
            Add(lines, "inserted", InsertedCount);
            Add(lines, "updated", UpdatedCount);
            Add(lines, "unchanged", UnchangedCount);
            if (ErrorCode is not null)
            {
                lines.Add($"error={ErrorCode}");
            }
            return string.Join("\n", lines);
        }
    }

    private static void Add(List<string> lines, string name, int? value)
    {
        if (value is { } number)
        {
            lines.Add($"{name}={number.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

public interface IFeedRefreshCoordinator
{
    Task<FeedPreview> PreviewAsync(Uri feedUrl, DateTimeOffset now);

    Task<FeedPreview> PreviewAsync(DateTimeOffset now);

    Task<RefreshResult> ImportSelectedAsync(IReadOnlyList<ParsedCalendarEvent> events, Uri feedUrl, DateTimeOffset at);

    Task<RefreshResult> RefreshAsync(DateTimeOffset now, RefreshTrigger trigger);

    Task<FeedRefreshDiagnostic?> DiagnosticSnapshotAsync();

    Task ClearImportSelectionsAsync() => Task.CompletedTask;
}

public enum RefreshCoordinatorError
{
    NoSavedFeedUrl,
    NoEventsSelected,
    OperationAlreadyInProgress,
    RefreshFailed,
}

public sealed class RefreshCoordinatorException(RefreshCoordinatorError error) : Exception(Describe(error))
{
    public RefreshCoordinatorError Error { get; } = error;

    public string DiagnosticCode => Error switch
    {
        RefreshCoordinatorError.NoSavedFeedUrl => "refresh.no-saved-url",
        RefreshCoordinatorError.NoEventsSelected => "refresh.no-events-selected",
        RefreshCoordinatorError.OperationAlreadyInProgress => "refresh.in-progress",
        _ => "refresh.failed",
    };

    private static string Describe(RefreshCoordinatorError error) => error switch
    {
        RefreshCoordinatorError.NoSavedFeedUrl => "Add your Canvas calendar feed in Settings first.",
        RefreshCoordinatorError.NoEventsSelected => "Select at least one assignment to import.",
        RefreshCoordinatorError.OperationAlreadyInProgress => "A calendar refresh is already in progress.",
        _ => "The calendar refresh could not be completed.",
    };
}

/// <summary>
/// Previews, imports and refreshes the Canvas feed, and decides what each
/// refresh means for what is already stored.
/// </summary>
/// <remarks>
/// <para>One operation at a time: a second asks to wait its turn and is told so.</para>
/// <para>
/// A refresh never deletes. Events are archived only after repeated absence
/// from feeds that actually listed something (see
/// <see cref="FeedPreview.CarriesAuthoritativeEventList"/>); an explicit
/// cancellation or a deselection in the import preview archives at once.
/// Courses the user removed are filtered out before anything is written, which
/// is the only thing that keeps a finished course from coming back.
/// </para>
/// </remarks>
public sealed class RefreshCoordinator : IFeedRefreshCoordinator
{
    private readonly IFeedFetcher fetcher;
    private readonly IIcsParser parser;
    private readonly IAssignmentRepository repository;
    private readonly IFeedUrlStore feedUrlStore;
    private readonly IFeedExclusionStore exclusionStore;
    private readonly ICourseBlocklist courseBlocklist;
    private readonly Func<TimeZoneInfo> defaultTimeZone;
    private readonly int recentlyOverdueDayLimit;
    private readonly int missingRefreshArchiveThreshold;
    private readonly object gate = new();

    private bool operationInProgress;
    private FeedRefreshDiagnostic? lastDiagnostic;
    private ValueList<ParsedCalendarEvent> lastPreviewEvents = ValueList<ParsedCalendarEvent>.Empty;
    private ValueSet<string> lastPreviewActiveExternalIds = ValueSet<string>.Empty;

    public RefreshCoordinator(
        IFeedFetcher fetcher,
        IIcsParser parser,
        IAssignmentRepository repository,
        IFeedUrlStore feedUrlStore,
        IFeedExclusionStore exclusionStore,
        ICourseBlocklist courseBlocklist,
        Func<TimeZoneInfo>? defaultTimeZone = null,
        int recentlyOverdueDayLimit = 30,
        int missingRefreshArchiveThreshold = FeedReconciliationRequest.DefaultArchiveThreshold)
    {
        this.fetcher = fetcher;
        this.parser = parser;
        this.repository = repository;
        this.feedUrlStore = feedUrlStore;
        this.exclusionStore = exclusionStore;
        this.courseBlocklist = courseBlocklist;
        this.defaultTimeZone = defaultTimeZone ?? (() => TimeZoneInfo.Local);
        this.recentlyOverdueDayLimit = Math.Max(0, recentlyOverdueDayLimit);
        this.missingRefreshArchiveThreshold = Math.Max(1, missingRefreshArchiveThreshold);
    }

    public async Task<FeedPreview> PreviewAsync(Uri feedUrl, DateTimeOffset now)
    {
        BeginOperation();
        try
        {
            var preview = await LoadPreviewAsync(feedUrl, now);
            Remember(preview);
            SetDiagnostic(PreviewDiagnostic(preview, RefreshTrigger.Onboarding));
            return preview;
        }
        catch (Exception error)
        {
            SetDiagnostic(FailureDiagnostic(now, RefreshTrigger.Onboarding, error));
            throw PresentationSafe(error);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<FeedPreview> PreviewAsync(DateTimeOffset now)
    {
        BeginOperation();
        try
        {
            var feedUrl = await feedUrlStore.LoadFeedUrlAsync()
                ?? throw new RefreshCoordinatorException(RefreshCoordinatorError.NoSavedFeedUrl);
            var preview = await LoadPreviewAsync(feedUrl, now);
            Remember(preview);
            SetDiagnostic(PreviewDiagnostic(preview, RefreshTrigger.Manual));
            return preview;
        }
        catch (Exception error)
        {
            SetDiagnostic(FailureDiagnostic(now, RefreshTrigger.Manual, error));
            throw PresentationSafe(error);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<RefreshResult> ImportSelectedAsync(IReadOnlyList<ParsedCalendarEvent> events, Uri feedUrl, DateTimeOffset at)
    {
        BeginOperation();
        try
        {
            if (events.Count == 0)
            {
                throw new RefreshCoordinatorException(RefreshCoordinatorError.NoEventsSelected);
            }
            var secureUrl = FeedUrlValidator.Validated(feedUrl);
            var previouslySavedUrl = await feedUrlStore.LoadFeedUrlAsync();
            var existingExclusions = previouslySavedUrl == secureUrl
                ? await exclusionStore.LoadExcludedUidsAsync()
                : ValueSet<string>.Empty;
            var selectedUids = ExternalIds(events);
            ValueList<ParsedCalendarEvent> previewSource;
            ValueSet<string> previewActive;
            lock (gate)
            {
                previewSource = lastPreviewEvents;
                previewActive = lastPreviewActiveExternalIds;
            }
            var previewUids = ExternalIds(previewSource.IsEmpty ? events : previewSource);
            var exclusions = existingExclusions
                .Union(previewUids.Except(selectedUids))
                .Except(selectedUids);

            var importResult = await repository.UpsertAsync(events.Select(ImportRecord).ToArray(), at);
            var activeExternalIds = previewActive.IsEmpty ? selectedUids : previewActive;
            await repository.ReconcileCanvasFeedAsync(new FeedReconciliationRequest
            {
                ActiveExternalIds = activeExternalIds,
                ExcludedExternalIds = exclusions,
                ObservedAt = at,
                ArchiveThreshold = missingRefreshArchiveThreshold,
                // An import describes only what the user chose to bring in, so
                // it must not start counting absences against anything stored.
                CountsAbsentEventsAsMissing = false,
            });

            // The address is kept only once the chosen entries are safely in.
            await feedUrlStore.SaveFeedUrlAsync(secureUrl);
            await exclusionStore.SaveExcludedUidsAsync(exclusions);

            var result = new RefreshResult(importResult, events.Count, at);
            SetDiagnostic(ImportDiagnostic(result, RefreshTrigger.Onboarding, receivedByteCount: null, parsedEventCount: events.Count));
            return result;
        }
        catch (Exception error)
        {
            SetDiagnostic(FailureDiagnostic(at, RefreshTrigger.Onboarding, error));
            throw PresentationSafe(error);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<RefreshResult> RefreshAsync(DateTimeOffset now, RefreshTrigger trigger)
    {
        BeginOperation();
        try
        {
            var feedUrl = await feedUrlStore.LoadFeedUrlAsync()
                ?? throw new RefreshCoordinatorException(RefreshCoordinatorError.NoSavedFeedUrl);
            var preview = await LoadPreviewAsync(feedUrl, now);
            var exclusions = await exclusionStore.LoadExcludedUidsAsync();
            var importable = preview.Events
                .Where(parsed => parsed.Uid is not { } uid || !exclusions.Contains(SwiftText.Identity(uid)))
                .ToArray();
            var importResult = await repository.UpsertAsync(importable.Select(ImportRecord).ToArray(), now);
            await repository.ReconcileCanvasFeedAsync(new FeedReconciliationRequest
            {
                ActiveExternalIds = preview.ActiveExternalIds,
                CancelledExternalIds = preview.CancelledExternalIds,
                ExcludedExternalIds = exclusions,
                ObservedAt = now,
                ArchiveThreshold = missingRefreshArchiveThreshold,
                CountsAbsentEventsAsMissing = preview.CarriesAuthoritativeEventList,
            });
            var result = new RefreshResult(importResult, importable.Length, now);
            SetDiagnostic(ImportDiagnostic(result, trigger, preview.ReceivedByteCount, preview.Events.Count + preview.ExcludedEventCount));
            return result;
        }
        catch (Exception error)
        {
            SetDiagnostic(FailureDiagnostic(now, trigger, error));
            throw PresentationSafe(error);
        }
        finally
        {
            EndOperation();
        }
    }

    public Task<FeedRefreshDiagnostic?> DiagnosticSnapshotAsync()
    {
        lock (gate)
        {
            return Task.FromResult(lastDiagnostic);
        }
    }

    public Task ClearImportSelectionsAsync() => exclusionStore.ClearExcludedUidsAsync();

    private void BeginOperation()
    {
        lock (gate)
        {
            if (operationInProgress)
            {
                throw new RefreshCoordinatorException(RefreshCoordinatorError.OperationAlreadyInProgress);
            }
            operationInProgress = true;
        }
    }

    private void EndOperation()
    {
        lock (gate)
        {
            operationInProgress = false;
        }
    }

    private void SetDiagnostic(FeedRefreshDiagnostic diagnostic)
    {
        lock (gate)
        {
            lastDiagnostic = diagnostic;
        }
    }

    private void Remember(FeedPreview preview)
    {
        lock (gate)
        {
            lastPreviewEvents = preview.Events;
            lastPreviewActiveExternalIds = preview.ActiveExternalIds;
        }
    }

    private async Task<FeedPreview> LoadPreviewAsync(Uri feedUrl, DateTimeOffset now)
    {
        var secureUrl = FeedUrlValidator.Validated(feedUrl);
        var data = await fetcher.FetchAsync(secureUrl);
        var zone = defaultTimeZone();
        var parseResult = parser.ParseResult(data, zone);
        var parsedEvents = parseResult.Events;
        var cutoff = new AppCalendar(zone).AddDays(now, -recentlyOverdueDayLimit);
        var activeEvents = parsedEvents.Where(parsed => !IsAvailabilityMarker(parsed)).ToArray();

        // A course the user removed stays removed. This is the only thing that
        // stops the next refresh importing an old course all over again.
        var blockedCourses = await courseBlocklist.BlockedCourseKeysAsync();
        var eligibleEvents = activeEvents
            .Where(parsed => parsed.StartDate >= cutoff)
            .Where(parsed => !IsInBlockedCourse(parsed, blockedCourses))
            .Order(Comparer<ParsedCalendarEvent>.Create((left, right) =>
            {
                var byDate = left.StartDate.CompareTo(right.StartDate);
                return byDate != 0 ? byDate : TextOrder.Compare(left.Summary, right.Summary);
            }))
            .ToValueList();

        return new FeedPreview
        {
            Events = eligibleEvents,
            FetchedAt = now,
            ReceivedByteCount = data.Length,
            ExcludedEventCount = parsedEvents.Count - eligibleEvents.Count,
            ActiveExternalIds = ExternalIds(activeEvents),
            CancelledExternalIds = parseResult.CancelledEventUids.Select(SwiftText.Identity).ToValueSet(),
        };
    }

    private static ValueSet<string> ExternalIds(IEnumerable<ParsedCalendarEvent> events)
        => events.Select(parsed => parsed.Uid).OfType<string>().Select(SwiftText.Identity).ToValueSet();

    private static AssignmentImportRecord ImportRecord(ParsedCalendarEvent parsed)
    {
        var (title, courseName) = AssignmentMetadata(parsed.Summary);
        return new AssignmentImportRecord
        {
            ExternalId = parsed.Uid,
            Title = title,
            CourseName = courseName,
            DueDate = parsed.StartDate,
            Source = AssignmentSource.CanvasCalendarFeed,
            SourceUrl = parsed.Url,
        };
    }

    /// <summary>
    /// Canvas titles its feed entries "Title [Course]", sometimes with an
    /// "Assignment:" prefix. The course is split off only when what is inside
    /// the brackets is plausibly a course name.
    /// </summary>
    public static (string Title, string? CourseName) AssignmentMetadata(string summary)
    {
        var title = SwiftText.Trimmed(summary);
        if (title.StartsWith("assignment:", StringComparison.OrdinalIgnoreCase))
        {
            title = SwiftText.Trimmed(title["assignment:".Length..]);
        }

        var openingBracket = title.LastIndexOf('[');
        if (!title.EndsWith(']') || openingBracket <= 0)
        {
            return (title, null);
        }

        var course = SwiftText.Trimmed(title[(openingBracket + 1)..^1]);
        if (course.Length == 0 || SwiftText.CountCharacters(course) > 120)
        {
            return (title, null);
        }

        var titleWithoutCourse = SwiftText.Trimmed(title[..openingBracket]);
        return titleWithoutCourse.Length == 0 ? (title, null) : (titleWithoutCourse, course);
    }

    /// <summary>
    /// Reads the course out of the same summary the importer parses, so a block
    /// written against what the list showed matches what the feed sends.
    /// </summary>
    private static bool IsInBlockedCourse(ParsedCalendarEvent parsed, ValueSet<string> blocked)
        => !blocked.IsEmpty
            && CourseName.Normalized(AssignmentMetadata(parsed.Summary).CourseName) is { } course
            && blocked.Contains(course);

    private static bool IsAvailabilityMarker(ParsedCalendarEvent parsed)
    {
        var summary = $" {SwiftText.Lowercased(parsed.Summary)} ";
        return summary.Contains(" available from ", StringComparison.Ordinal)
            || summary.Contains(" available until ", StringComparison.Ordinal)
            || summary.Contains(" availability begins ", StringComparison.Ordinal)
            || summary.Contains(" availability ends ", StringComparison.Ordinal);
    }

    private static FeedRefreshDiagnostic PreviewDiagnostic(FeedPreview preview, RefreshTrigger trigger) => new()
    {
        AttemptedAt = preview.FetchedAt,
        Trigger = trigger,
        Outcome = FeedRefreshOutcome.Success,
        ReceivedByteCount = preview.ReceivedByteCount,
        ParsedEventCount = preview.Events.Count + preview.ExcludedEventCount,
        EligibleEventCount = preview.Events.Count,
    };

    private static FeedRefreshDiagnostic ImportDiagnostic(RefreshResult result, RefreshTrigger trigger, int? receivedByteCount, int parsedEventCount) => new()
    {
        AttemptedAt = result.RefreshedAt,
        Trigger = trigger,
        Outcome = FeedRefreshOutcome.Success,
        ReceivedByteCount = receivedByteCount,
        ParsedEventCount = parsedEventCount,
        EligibleEventCount = result.ImportedEventCount,
        InsertedCount = result.ImportResult.InsertedCount,
        UpdatedCount = result.ImportResult.UpdatedCount,
        UnchangedCount = result.ImportResult.UnchangedCount,
    };

    private static FeedRefreshDiagnostic FailureDiagnostic(DateTimeOffset at, RefreshTrigger trigger, Exception error) => new()
    {
        AttemptedAt = at,
        Trigger = trigger,
        Outcome = FeedRefreshOutcome.Failure,
        ErrorCode = DiagnosticCode(error),
    };

    internal static string DiagnosticCode(Exception error) => error switch
    {
        CanvasFeedFetchException fetch => fetch.DiagnosticCode,
        SecretStoreException secret => secret.DiagnosticCode,
        FeedUrlStoreException stored => stored.DiagnosticCode,
        FeedUrlValidationException validation => validation.DiagnosticCode,
        IcsParserException ics => ics.Error switch
        {
            IcsParserError.InvalidTextEncoding => "ics.invalid-text-encoding",
            _ => "ics.not-calendar-data",
        },
        AssignmentRepositoryException repository => repository.Error switch
        {
            AssignmentRepositoryError.EmptyTitle => "repository.empty-title",
            AssignmentRepositoryError.AssignmentNotFound => "repository.not-found",
            _ => "repository.not-manual",
        },
        RefreshCoordinatorException refresh => refresh.DiagnosticCode,
        _ => "refresh.unknown",
    };

    /// <summary>
    /// Errors the app wrote itself are shown as they are; anything else (a
    /// database fault, say) is shown as a plain "could not be completed", so no
    /// internal detail reaches the screen.
    /// </summary>
    private static Exception PresentationSafe(Exception error) => error switch
    {
        CanvasFeedFetchException or SecretStoreException or FeedUrlStoreException or FeedUrlValidationException
            or IcsParserException or AssignmentRepositoryException or RefreshCoordinatorException
            or OperationCanceledException => error,
        _ => new RefreshCoordinatorException(RefreshCoordinatorError.RefreshFailed),
    };
}

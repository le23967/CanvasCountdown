namespace CanvasCountdown;

/// <summary>Whether reminders may be shown.</summary>
public enum NotificationAuthorizationStatus
{
    /// <summary>Not asked yet: asking is still possible.</summary>
    NotDetermined,

    /// <summary>Turned off. Never ask again: nothing would appear, and the button would look broken.</summary>
    Denied,

    Authorized,

    /// <summary>Allowed quietly. Scheduled like <see cref="Authorized"/>.</summary>
    Provisional,
}

/// <summary>The reminder schedule as the rest of the app sees it.</summary>
/// <remarks>
/// There is deliberately no overload of <see cref="RescheduleAsync"/> that
/// supplies <c>now</c> or the calendar. Callers pass both, so what is
/// scheduled never depends on whichever clock or time zone happened to be
/// nearest.
/// </remarks>
public interface INotificationScheduling
{
    Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces every pending request owned by Canvas Countdown. This makes
    /// refreshes idempotent and removes stale reminders when a due date changes.
    /// </summary>
    Task RescheduleAsync(
        IReadOnlyList<NotificationCandidate> candidates,
        ReminderSchedule schedule,
        DateTimeOffset now,
        AppCalendar calendar,
        CancellationToken cancellationToken = default);

    Task CancelAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Where reminders are actually shown. On Windows, toast notifications; this
/// module only decides what they are.
/// </summary>
public interface INotificationDelivery
{
    Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks to show alerts with sound. True when allowed.</summary>
    Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the pending reminders this app owns exactly
    /// <paramref name="requests"/>: everything else it had pending goes.
    /// Reminders belonging to anything else are left alone.
    /// </summary>
    Task ReplacePendingAsync(IReadOnlyList<ReminderNotificationRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>Removes every pending reminder this app owns.</summary>
    Task RemoveAllPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>One reminder: when it fires, and what it says. Shown with the default sound.</summary>
public sealed record ReminderNotificationRequest
{
    /// <summary>
    /// <c>canvas-countdown.assignment.&lt;ID&gt;.&lt;offset minutes&gt;</c>, with the
    /// id in upper case as the Mac wrote it. One event and one offset make one
    /// identifier, so scheduling the same reminder twice replaces it.
    /// </summary>
    /// <remarks>
    /// Up to 71 characters: longer than a Windows toast tag or group may be,
    /// so the toast delivery has to map it rather than use it as a tag.
    /// </remarks>
    public required string Identifier { get; init; }

    public required Guid AssignmentId { get; init; }

    public required int OffsetMinutes { get; init; }

    /// <summary>The due date less the rule's offset, in elapsed minutes.</summary>
    public required DateTimeOffset FireDate { get; init; }

    /// <summary>
    /// When it is shown: the start of the minute <see cref="FireDate"/> falls
    /// in, in the calendar's time zone. The Mac's trigger matched the year,
    /// month, day, hour and minute, so it fired at the top of that minute.
    /// </summary>
    public required DateTimeOffset TriggerDate { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }
}

/// <summary>Turns the schedule into reminders and hands them to the delivery.</summary>
/// <remarks>
/// Rescheduling and cancelling are serialized: a refresh and a settings change
/// arriving together would otherwise interleave their removals and additions
/// and leave a mixture of both.
/// </remarks>
public sealed class NotificationService : INotificationScheduling
{
    public const string IdentifierPrefix = "canvas-countdown.assignment.";

    private readonly INotificationDelivery delivery;
    private readonly SemaphoreSlim schedulingLock = new(1, 1);

    public NotificationService(INotificationDelivery delivery)
    {
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    public Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default)
        => delivery.AuthorizationStatusAsync(cancellationToken);

    public Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
        => delivery.RequestAuthorizationAsync(cancellationToken);

    /// <summary>
    /// Leaves exactly the reminders <see cref="Requests"/> describes pending,
    /// or none when reminders are not allowed: stale ones go either way.
    /// </summary>
    public async Task RescheduleAsync(
        IReadOnlyList<NotificationCandidate> candidates,
        ReminderSchedule schedule,
        DateTimeOffset now,
        AppCalendar calendar,
        CancellationToken cancellationToken = default)
    {
        await schedulingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await delivery.AuthorizationStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is not (NotificationAuthorizationStatus.Authorized or NotificationAuthorizationStatus.Provisional))
            {
                await delivery.RemoveAllPendingAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var requests = Requests(candidates, schedule, now, calendar);
            await delivery.ReplacePendingAsync(requests, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            schedulingLock.Release();
        }
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        await schedulingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await delivery.RemoveAllPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            schedulingLock.Release();
        }
    }

    /// <summary>
    /// Which reminder fires when, for which event.
    /// </summary>
    /// <remarks>
    /// Completed and ignored events get none. Each enabled rule fires its
    /// offset in elapsed minutes before the due date — across a daylight-saving
    /// change, "1 day before" is 24 hours before, as the Mac counted it — and a
    /// rule whose moment is not after <paramref name="now"/> is skipped. Two
    /// rules can never describe the same instant, but a feed change could still
    /// land two candidates on one identifier; the later one replaces the
    /// earlier, as adding a request whose identifier is already pending does,
    /// so no identifier is ever submitted twice.
    /// </remarks>
    public static ValueList<ReminderNotificationRequest> Requests(
        IEnumerable<NotificationCandidate> candidates,
        ReminderSchedule schedule,
        DateTimeOffset now,
        AppCalendar calendar)
    {
        var rules = schedule.EnabledRules.OrderByDescending(rule => rule.OffsetMinutes).ToList();
        var requests = new List<ReminderNotificationRequest>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (candidate.IsCompleted || candidate.IsIgnored)
            {
                continue;
            }
            var scheduledOffsets = new HashSet<int>();
            foreach (var rule in rules)
            {
                if (scheduledOffsets.Contains(rule.OffsetMinutes)
                    || FireDate(candidate.DueDate, rule.OffsetMinutes) is not { } fireDate
                    || fireDate <= now)
                {
                    continue;
                }

                var request = new ReminderNotificationRequest
                {
                    Identifier = Identifier(candidate.Id, rule.OffsetMinutes),
                    AssignmentId = candidate.Id,
                    OffsetMinutes = rule.OffsetMinutes,
                    FireDate = calendar.InZone(fireDate),
                    TriggerDate = StartOfMinute(fireDate, calendar),
                    Title = NotificationTitle(rule),
                    Body = NotificationBody(candidate),
                };
                if (positions.TryGetValue(request.Identifier, out var existing))
                {
                    requests[existing] = request;
                }
                else
                {
                    positions[request.Identifier] = requests.Count;
                    requests.Add(request);
                }
                scheduledOffsets.Add(rule.OffsetMinutes);
            }
        }
        return requests.ToValueList();
    }

    public static string Identifier(Guid assignmentId, int offsetMinutes)
        => $"{IdentifierPrefix}{CodableJson.UuidString(assignmentId)}.{offsetMinutes}";

    public static string NotificationTitle(ReminderRule rule) => rule.Unit switch
    {
        ReminderUnit.Days => rule.Amount switch
        {
            0 => "Assignment due today",
            1 => "Assignment due tomorrow",
            _ => $"Assignment due in {rule.Amount} days",
        },
        _ => rule.Amount == 1 ? "Assignment due in 1 hour" : $"Assignment due in {rule.Amount} hours",
    };

    public static string NotificationBody(NotificationCandidate candidate)
    {
        var courseName = candidate.CourseName is null
            ? null
            : FoundationText.TrimmingWhitespaceAndNewlines(candidate.CourseName);
        return string.IsNullOrEmpty(courseName) ? candidate.Title : $"{candidate.Title} · {courseName}";
    }

    private static DateTimeOffset? FireDate(DateTimeOffset dueDate, int offsetMinutes)
    {
        try
        {
            return dueDate.AddMinutes(-offsetMinutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset StartOfMinute(DateTimeOffset instant, AppCalendar calendar)
    {
        var intoMinute = TimeSpan.FromTicks(calendar.ToLocal(instant).Ticks % TimeSpan.TicksPerMinute);
        return calendar.InZone(instant - intoMinute);
    }
}

/// <summary>
/// Accepts every call and shows nothing, so an automated run never registers
/// a reminder with Windows. Reports reminders as turned off, so nothing is
/// ever offered to it.
/// </summary>
public sealed class InertNotificationDelivery : INotificationDelivery
{
    public Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(NotificationAuthorizationStatus.Denied);

    public Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task ReplacePendingAsync(IReadOnlyList<ReminderNotificationRequest> requests, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RemoveAllPendingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

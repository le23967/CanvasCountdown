namespace CanvasCountdown.Tests.Reminders;

/// <summary>
/// Which reminder fires when, for which event, and what it says — and that the
/// delivery is handed exactly that. All fixtures are invented.
/// </summary>
public sealed class NotificationServiceTests
{
    // Sydney moves its clocks forward at 02:00 on 4 October 2026 and back at
    // 03:00 on 5 April 2026.
    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    private static readonly Guid QuizId = Guid.Parse("4c1d0e9a-1111-4000-a000-00000000c0de");

    private static NotificationCandidate Quiz(DateTimeOffset due, string? course = "COMP2050")
        => new() { Id = QuizId, Title = "Week 6 Quiz", CourseName = course, DueDate = due };

    [Fact]
    public void EachEnabledRuleFiresItsOffsetBeforeTheDeadline()
    {
        var now = Sydney.FromLocal(2026, 8, 1, 9, 0);
        var due = Sydney.FromLocal(2026, 8, 14, 23, 59);

        var requests = NotificationService.Requests([Quiz(due)], ReminderSchedule.Defaults, now, Sydney);

        Assert.Equal(
            [
                Sydney.FromLocal(2026, 8, 7, 23, 59),
                Sydney.FromLocal(2026, 8, 11, 23, 59),
                Sydney.FromLocal(2026, 8, 13, 23, 59),
                Sydney.FromLocal(2026, 8, 14, 23, 59),
            ],
            requests.Select(request => request.FireDate));
        Assert.Equal(
            ["Assignment due in 7 days", "Assignment due in 3 days", "Assignment due tomorrow", "Assignment due today"],
            requests.Select(request => request.Title));
        Assert.All(requests, request => Assert.Equal("Week 6 Quiz · COMP2050", request.Body));
        Assert.All(requests, request => Assert.Equal(QuizId, request.AssignmentId));
    }

    [Fact]
    public void IdentifiersAreTheMacsWithTheIdInUpperCase()
    {
        var now = Sydney.FromLocal(2026, 8, 1, 9, 0);
        var due = Sydney.FromLocal(2026, 8, 14, 23, 59);

        var requests = NotificationService.Requests([Quiz(due)], ReminderSchedule.Defaults, now, Sydney);

        Assert.Equal(
            [
                "canvas-countdown.assignment.4C1D0E9A-1111-4000-A000-00000000C0DE.10080",
                "canvas-countdown.assignment.4C1D0E9A-1111-4000-A000-00000000C0DE.4320",
                "canvas-countdown.assignment.4C1D0E9A-1111-4000-A000-00000000C0DE.1440",
                "canvas-countdown.assignment.4C1D0E9A-1111-4000-A000-00000000C0DE.0",
            ],
            requests.Select(request => request.Identifier));
        Assert.All(requests, request => Assert.StartsWith(NotificationService.IdentifierPrefix, request.Identifier, StringComparison.Ordinal));
    }

    [Fact]
    public void AReminderWhoseMomentHasPassedIsSkipped()
    {
        var due = Sydney.FromLocal(2026, 8, 14, 23, 59);
        var twoDaysBefore = Sydney.FromLocal(2026, 8, 12, 12, 0);

        var requests = NotificationService.Requests([Quiz(due)], ReminderSchedule.Defaults, twoDaysBefore, Sydney);

        Assert.Equal([1_440, 0], requests.Select(request => request.OffsetMinutes));

        // Exactly at its moment is not after now: not scheduled.
        var atTheMoment = NotificationService.Requests([Quiz(due)], ReminderSchedule.Defaults, Sydney.FromLocal(2026, 8, 13, 23, 59), Sydney);
        Assert.Equal([0], atTheMoment.Select(request => request.OffsetMinutes));
    }

    /// <summary>
    /// The Mac subtracted the offset in minutes, which is elapsed time: across
    /// the night the clocks go forward, "1 day before" a midnight deadline is
    /// 24 hours before it, which reads 23:00 the evening before.
    /// </summary>
    [Fact]
    public void RemindersCountBackInElapsedMinutesAcrossDaylightSaving()
    {
        var now = Sydney.FromLocal(2026, 9, 20, 9, 0);
        var due = Sydney.FromLocal(2026, 10, 5, 0, 0);
        var oneDay = new ReminderSchedule([new ReminderRule { Amount = 1, Unit = ReminderUnit.Days }]);

        var request = Assert.Single(NotificationService.Requests([Quiz(due)], oneDay, now, Sydney));

        Assert.Equal(TimeSpan.FromHours(24), due - request.FireDate);
        Assert.Equal(new DateTime(2026, 10, 3, 23, 0, 0), Sydney.ToLocal(request.FireDate));
        Assert.Equal(TimeSpan.FromHours(10), request.FireDate.Offset);

        var autumnDue = Sydney.FromLocal(2026, 4, 6, 0, 0);
        var autumn = Assert.Single(NotificationService.Requests([Quiz(autumnDue)], oneDay, Sydney.FromLocal(2026, 3, 20, 9, 0), Sydney));
        Assert.Equal(new DateTime(2026, 4, 5, 1, 0, 0), Sydney.ToLocal(autumn.FireDate));
    }

    /// <summary>
    /// The Mac's trigger matched the minute, so a deadline with seconds fires
    /// at the top of the minute it falls in.
    /// </summary>
    [Fact]
    public void ARequestIsShownAtTheStartOfItsMinute()
    {
        var now = Sydney.FromLocal(2026, 8, 1, 9, 0);
        var due = Sydney.FromLocal(2026, 8, 14, 23, 59, 59);
        var dueDay = new ReminderSchedule([new ReminderRule { Amount = 0, Unit = ReminderUnit.Days }]);

        var request = Assert.Single(NotificationService.Requests([Quiz(due)], dueDay, now, Sydney));

        Assert.Equal(due, request.FireDate);
        Assert.Equal(Sydney.FromLocal(2026, 8, 14, 23, 59), request.TriggerDate);
        Assert.Equal(TimeSpan.FromHours(10), request.TriggerDate.Offset);
    }

    [Theory]
    [InlineData(0, ReminderUnit.Days, "Assignment due today")]
    [InlineData(1, ReminderUnit.Days, "Assignment due tomorrow")]
    [InlineData(2, ReminderUnit.Days, "Assignment due in 2 days")]
    [InlineData(1, ReminderUnit.Hours, "Assignment due in 1 hour")]
    [InlineData(6, ReminderUnit.Hours, "Assignment due in 6 hours")]
    public void TitlesSayHowLongIsLeft(int amount, ReminderUnit unit, string title)
    {
        Assert.Equal(title, NotificationService.NotificationTitle(new ReminderRule { Amount = amount, Unit = unit }));
    }

    [Theory]
    [InlineData("COMP2050", "Week 6 Quiz · COMP2050")]
    [InlineData("  STAT1010 \n", "Week 6 Quiz · STAT1010")]
    [InlineData("   ", "Week 6 Quiz")]
    [InlineData(null, "Week 6 Quiz")]
    public void TheBodyNamesTheCourseWhenThereIsOne(string? course, string body)
    {
        Assert.Equal(body, NotificationService.NotificationBody(Quiz(DateTimeOffset.UnixEpoch, course)));
    }

    [Fact]
    public void DisabledRulesFireNothing()
    {
        var now = Sydney.FromLocal(2026, 8, 1, 9, 0);
        var due = Sydney.FromLocal(2026, 8, 14, 23, 59);
        var schedule = ReminderSchedule.Defaults.SetEnabled(false, ReminderSchedule.Defaults.Rules[0].Id);

        var requests = NotificationService.Requests([Quiz(due)], schedule, now, Sydney);

        Assert.Equal([4_320, 1_440, 0], requests.Select(request => request.OffsetMinutes));
    }

    /// <summary>
    /// Adding a request whose identifier is already pending replaces it, so
    /// two candidates with one id leave one request per offset: the later one.
    /// </summary>
    [Fact]
    public void TwoCandidatesWithOneIdLeaveOneRequestEach()
    {
        var now = Sydney.FromLocal(2026, 8, 1, 9, 0);
        var first = Quiz(Sydney.FromLocal(2026, 8, 14, 23, 59));
        var second = first with { Title = "Week 6 Quiz (moved)", DueDate = Sydney.FromLocal(2026, 8, 21, 23, 59) };

        var requests = NotificationService.Requests([first, second], ReminderSchedule.Defaults, now, Sydney);

        Assert.Equal(4, requests.Count);
        Assert.Equal(requests.Count, requests.Select(request => request.Identifier).Distinct().Count());
        Assert.All(requests, request => Assert.Equal("Week 6 Quiz (moved) · COMP2050", request.Body));
        Assert.Equal(Sydney.FromLocal(2026, 8, 21, 23, 59), requests[^1].FireDate);
    }

    /// <summary>
    /// The same correction the list applies reaches the reminders: "1 day
    /// before" a Canvas deadline exported as midnight is the evening before
    /// the end of that day, never the previous midnight. Manual events are
    /// left as the user typed them.
    /// </summary>
    [Fact]
    public void CandidatesCountBackFromTheDeadlineAsShown()
    {
        var midnight = Sydney.FromLocal(2026, 8, 14, 0, 0);
        var canvas = new AssignmentSnapshot { Title = "Lab report", CourseName = "DESN1500", DueDate = midnight, Source = AssignmentSource.CanvasCalendarFeed };
        var manual = canvas with { Source = AssignmentSource.Manual };

        var corrected = NotificationCandidate.FromSnapshot(canvas, treatsMidnightAsEndOfDay: true, Sydney);
        var asStored = NotificationCandidate.FromSnapshot(canvas, treatsMidnightAsEndOfDay: false, Sydney);
        var typed = NotificationCandidate.FromSnapshot(manual, treatsMidnightAsEndOfDay: true, Sydney);

        Assert.Equal(Sydney.FromLocal(2026, 8, 14, 23, 59), corrected.DueDate);
        Assert.Equal(midnight, asStored.DueDate);
        Assert.Equal(midnight, typed.DueDate);
        Assert.Equal(canvas.Id, corrected.Id);
        Assert.Equal("DESN1500", corrected.CourseName);
    }

    // The service and its delivery

    [Theory]
    [InlineData(NotificationAuthorizationStatus.Authorized)]
    [InlineData(NotificationAuthorizationStatus.Provisional)]
    public async Task WhenAllowedTheDeliveryIsGivenExactlyThePlan(NotificationAuthorizationStatus status)
    {
        var delivery = new RecordingDelivery { Status = status };
        var service = new NotificationService(delivery);
        var now = Sydney.FromLocal(2026, 8, 1, 9, 0);
        var candidates = new[] { Quiz(Sydney.FromLocal(2026, 8, 14, 23, 59)) };

        await service.RescheduleAsync(candidates, ReminderSchedule.Defaults, now, Sydney);

        var replaced = Assert.Single(delivery.Replacements);
        Assert.Equal(NotificationService.Requests(candidates, ReminderSchedule.Defaults, now, Sydney), replaced);
        Assert.Equal(0, delivery.RemoveAllCount);
    }

    /// <summary>
    /// Not allowed means nothing is scheduled, and anything left over from
    /// before permission was withdrawn goes too.
    /// </summary>
    [Theory]
    [InlineData(NotificationAuthorizationStatus.Denied)]
    [InlineData(NotificationAuthorizationStatus.NotDetermined)]
    public async Task WhenNotAllowedNothingIsLeftPending(NotificationAuthorizationStatus status)
    {
        var delivery = new RecordingDelivery { Status = status };
        var service = new NotificationService(delivery);

        await service.RescheduleAsync([Quiz(Sydney.FromLocal(2026, 8, 14, 23, 59))], ReminderSchedule.Defaults, Sydney.FromLocal(2026, 8, 1, 9, 0), Sydney);

        Assert.Empty(delivery.Replacements);
        Assert.Equal(1, delivery.RemoveAllCount);
    }

    [Fact]
    public async Task CancellingRemovesEverythingPending()
    {
        var delivery = new RecordingDelivery { Status = NotificationAuthorizationStatus.Authorized };

        await new NotificationService(delivery).CancelAllAsync();

        Assert.Equal(1, delivery.RemoveAllCount);
    }

    [Fact]
    public async Task PermissionQuestionsGoToTheDelivery()
    {
        var delivery = new RecordingDelivery { Status = NotificationAuthorizationStatus.NotDetermined, Grants = true };
        var service = new NotificationService(delivery);

        Assert.Equal(NotificationAuthorizationStatus.NotDetermined, await service.AuthorizationStatusAsync());
        Assert.True(await service.RequestAuthorizationAsync());
        Assert.Equal(1, delivery.AuthorizationRequests);
    }

    /// <summary>Reschedules arriving together run one after the other, never interleaved.</summary>
    [Fact]
    public async Task ReschedulesDoNotOverlap()
    {
        var delivery = new RecordingDelivery { Status = NotificationAuthorizationStatus.Authorized, ReplaceDelay = TimeSpan.FromMilliseconds(20) };
        var service = new NotificationService(delivery);
        var candidates = new[] { Quiz(Sydney.FromLocal(2026, 8, 14, 23, 59)) };

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.RescheduleAsync(candidates, ReminderSchedule.Defaults, Sydney.FromLocal(2026, 8, 1, 9, 0), Sydney)));

        Assert.Equal(5, delivery.Replacements.Count);
        Assert.Equal(1, delivery.MaximumConcurrentReplacements);
    }

    /// <summary>
    /// An automated run must never register a reminder with Windows: the inert
    /// delivery reports reminders as turned off and refuses permission.
    /// </summary>
    [Fact]
    public async Task TheInertDeliveryNeverShowsAnything()
    {
        var service = new NotificationService(new InertNotificationDelivery());

        Assert.Equal(NotificationAuthorizationStatus.Denied, await service.AuthorizationStatusAsync());
        Assert.False(await service.RequestAuthorizationAsync());
        await service.RescheduleAsync([Quiz(Sydney.FromLocal(2026, 8, 14, 23, 59))], ReminderSchedule.Defaults, Sydney.FromLocal(2026, 8, 1, 9, 0), Sydney);
        await service.CancelAllAsync();
    }

    private sealed class RecordingDelivery : INotificationDelivery
    {
        private int concurrent;

        public NotificationAuthorizationStatus Status { get; init; }

        public bool Grants { get; init; }

        public TimeSpan ReplaceDelay { get; init; }

        public List<ValueList<ReminderNotificationRequest>> Replacements { get; } = [];

        public int RemoveAllCount { get; private set; }

        public int AuthorizationRequests { get; private set; }

        public int MaximumConcurrentReplacements { get; private set; }

        public Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Status);

        public Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            AuthorizationRequests++;
            return Task.FromResult(Grants);
        }

        public async Task ReplacePendingAsync(IReadOnlyList<ReminderNotificationRequest> requests, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref concurrent);
            lock (Replacements)
            {
                MaximumConcurrentReplacements = Math.Max(MaximumConcurrentReplacements, now);
            }
            await Task.Delay(ReplaceDelay, cancellationToken);
            lock (Replacements)
            {
                Replacements.Add(requests.ToValueList());
            }
            Interlocked.Decrement(ref concurrent);
        }

        public Task RemoveAllPendingAsync(CancellationToken cancellationToken = default)
        {
            RemoveAllCount++;
            return Task.CompletedTask;
        }
    }
}

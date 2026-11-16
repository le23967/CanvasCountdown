using CanvasCountdown.Tests.Appearance;

namespace CanvasCountdown.Tests.Reminders;

/// <summary>
/// The reminder schedule: validation, editing, persistence, and the promise that
/// scheduling never submits a duplicate.
/// </summary>
public sealed class ReminderScheduleTests
{
    private const string ScheduleKey = "CanvasCountdown.settings.reminderSchedule";

    private static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    // Schedule model

    [Fact]
    public void DefaultsMatchTheDocumentedSchedule()
    {
        var schedule = ReminderSchedule.Defaults;

        Assert.Equal([7 * 1_440, 3 * 1_440, 1_440, 0], schedule.Rules.Select(rule => rule.OffsetMinutes));
        Assert.All(schedule.Rules, rule => Assert.True(rule.IsEnabled));
    }

    [Fact]
    public void CustomDayAndHourOffsetsAreAccepted()
    {
        var schedule = new ReminderSchedule([])
            .Add(new ReminderRule { Amount = 14, Unit = ReminderUnit.Days })
            .Add(new ReminderRule { Amount = 6, Unit = ReminderUnit.Hours })
            .Add(new ReminderRule { Amount = 0, Unit = ReminderUnit.Days });

        Assert.Equal([14 * 1_440, 360, 0], schedule.Rules.Select(rule => rule.OffsetMinutes));
        Assert.Equal("6 hours before", schedule.Rules[1].Title);
        Assert.Equal("On the due day", schedule.Rules[2].Title);
    }

    [Fact]
    public void DuplicateOffsetsAreRejectedAcrossUnits()
    {
        var schedule = new ReminderSchedule([]).Add(new ReminderRule { Amount = 1, Unit = ReminderUnit.Days });

        var sameDay = Assert.Throws<ReminderScheduleException>(() => schedule.Add(new ReminderRule { Amount = 1, Unit = ReminderUnit.Days }));
        Assert.Equal(ReminderScheduleError.DuplicateOffset, sameDay.Error);

        // Zero hours is the same instant as the due-day reminder.
        schedule = schedule.Add(new ReminderRule { Amount = 0, Unit = ReminderUnit.Days });
        var zeroHours = Assert.Throws<ReminderScheduleException>(() => schedule.Add(new ReminderRule { Amount = 0, Unit = ReminderUnit.Hours }));
        Assert.Equal(ReminderScheduleError.DuplicateOffset, zeroHours.Error);
        Assert.Equal(2, schedule.Rules.Count);
    }

    [Fact]
    public void NegativeAndOversizedValuesAreClamped()
    {
        var negative = new ReminderRule { Amount = -5, Unit = ReminderUnit.Days }.Normalized();
        var hugeDays = new ReminderRule { Amount = 9_999, Unit = ReminderUnit.Days }.Normalized();
        var hugeHours = new ReminderRule { Amount = 400, Unit = ReminderUnit.Hours }.Normalized();

        Assert.Equal(0, negative.Amount);
        Assert.Equal(ReminderRule.MaximumDays, hugeDays.Amount);
        Assert.Equal(ReminderRule.MaximumHours, hugeHours.Amount);
    }

    [Fact]
    public void ScheduleIsCappedAtTenRules()
    {
        var schedule = new ReminderSchedule([]);
        for (var day = 1; day <= ReminderRule.MaximumRuleCount; day++)
        {
            schedule = schedule.Add(new ReminderRule { Amount = day, Unit = ReminderUnit.Days });
        }

        Assert.False(schedule.CanAddRule);
        var error = Assert.Throws<ReminderScheduleException>(() => schedule.Add(new ReminderRule { Amount = 40, Unit = ReminderUnit.Days }));
        Assert.Equal(ReminderScheduleError.TooManyRules, error.Error);
    }

    [Fact]
    public void UpdateAndDeleteChangeTheSchedule()
    {
        var schedule = new ReminderSchedule([]).Add(new ReminderRule { Amount = 5, Unit = ReminderUnit.Days });
        var rule = schedule.Rules[0] with { Amount = 2, Unit = ReminderUnit.Hours };

        schedule = schedule.Update(rule);
        Assert.Equal([120], schedule.Rules.Select(existing => existing.OffsetMinutes));

        schedule = schedule.Remove(rule.Id);
        Assert.Empty(schedule.Rules);
    }

    [Fact]
    public void UpdateRejectsCollidingWithAnotherRule()
    {
        var schedule = new ReminderSchedule([])
            .Add(new ReminderRule { Amount = 5, Unit = ReminderUnit.Days })
            .Add(new ReminderRule { Amount = 2, Unit = ReminderUnit.Days });
        var second = schedule.Rules[^1] with { Amount = 5 };

        var error = Assert.Throws<ReminderScheduleException>(() => schedule.Update(second));
        Assert.Equal(ReminderScheduleError.DuplicateOffset, error.Error);
    }

    [Fact]
    public void DisabledRulesAreKeptButNotScheduled()
    {
        var schedule = ReminderSchedule.Defaults;
        var first = schedule.Rules[0];

        schedule = schedule.SetEnabled(false, first.Id);

        Assert.Equal(4, schedule.Rules.Count);
        Assert.Equal(3, schedule.EnabledRules.Count);
    }

    [Fact]
    public void LegacyDayOffsetsMigrate()
    {
        var migrated = ReminderSchedule.FromLegacyDayOffsets(new ValueSet<int>([3, 0, 7]));

        Assert.Equal([7 * 1_440, 3 * 1_440, 0], migrated.Rules.Select(rule => rule.OffsetMinutes));
        Assert.All(migrated.Rules, rule => Assert.Equal(ReminderUnit.Days, rule.Unit));
    }

    [Fact]
    public void ScheduleSurvivesAStoreRoundTrip()
    {
        using var file = new PreferencesFile();
        var schedule = new ReminderSchedule([])
            .Add(new ReminderRule { Amount = 12, Unit = ReminderUnit.Hours })
            .Add(new ReminderRule { Amount = 21, Unit = ReminderUnit.Days });
        file.Open().Set(ScheduleKey, schedule);

        var reloaded = file.Open().Get(ScheduleKey, ReminderSchedule.Defaults);

        Assert.Equal([21 * 1_440, 720], reloaded.Rules.Select(rule => rule.OffsetMinutes));
        Assert.Equal(schedule, reloaded);
    }

    // Scheduling against real due dates

    /// <summary>
    /// The Mac ran this through a test double that repeated the service's
    /// expansion; here it runs through the expansion itself.
    /// </summary>
    [Fact]
    public void CompletedAndIgnoredCandidatesAreExcludedFromScheduling()
    {
        var schedule = ReminderSchedule.Defaults;
        var now = Sydney.FromLocal(2026, 8, 3, 9, 0);
        // Far enough out that every default rule still lies in the future; a
        // reminder whose moment has already passed is correctly skipped.
        var due = now.AddDays(10);

        var requests = NotificationService.Requests(
            [
                new NotificationCandidate { Id = Guid.NewGuid(), Title = "Active", DueDate = due },
                new NotificationCandidate { Id = Guid.NewGuid(), Title = "Done", DueDate = due, IsCompleted = true },
                new NotificationCandidate { Id = Guid.NewGuid(), Title = "Ignored", DueDate = due, IsIgnored = true },
            ],
            schedule,
            now,
            Sydney);

        var identifiers = requests.Select(request => request.Identifier).ToList();
        Assert.True(identifiers.Distinct().Count() == identifiers.Count, "No duplicate notification request may be submitted");
        Assert.True(identifiers.Count == 4, "Only the active assignment is scheduled, once per enabled rule");
        Assert.All(requests, request => Assert.Equal("Active", request.Body));
    }

    // Windows additions

    [Theory]
    [InlineData(0, ReminderUnit.Days, "On the due day")]
    [InlineData(1, ReminderUnit.Days, "1 day before")]
    [InlineData(3, ReminderUnit.Days, "3 days before")]
    [InlineData(1, ReminderUnit.Hours, "1 hour before")]
    [InlineData(6, ReminderUnit.Hours, "6 hours before")]
    [InlineData(0, ReminderUnit.Hours, "On the due day")]
    public void RulesDescribeThemselves(int amount, ReminderUnit unit, string title)
    {
        Assert.Equal(title, new ReminderRule { Amount = amount, Unit = unit }.Title);
    }

    [Fact]
    public void AnHoursRuleOfZeroBecomesTheDueDayReminder()
    {
        var rule = new ReminderRule { Amount = 0, Unit = ReminderUnit.Hours }.Normalized();

        Assert.Equal(ReminderUnit.Days, rule.Unit);
        Assert.True(rule.IsDueDayReminder);
    }

    [Fact]
    public void BuildingAScheduleKeepsTheFirstOfEachInstantAndAtMostTen()
    {
        var first = new ReminderRule { Amount = 1, Unit = ReminderUnit.Days };
        var sameInstant = new ReminderRule { Amount = 1, Unit = ReminderUnit.Days, IsEnabled = false };
        var clampedToTwentyThreeHours = new ReminderRule { Amount = 24, Unit = ReminderUnit.Hours }.Normalized();
        Assert.Equal(23 * 60, clampedToTwentyThreeHours.OffsetMinutes);
        var many = Enumerable.Range(2, 20).Select(day => new ReminderRule { Amount = day, Unit = ReminderUnit.Days });

        var schedule = new ReminderSchedule(new[] { first, sameInstant }.Concat(many));

        Assert.Equal(ReminderRule.MaximumRuleCount, schedule.Rules.Count);
        Assert.Contains(first, schedule.Rules);
        Assert.DoesNotContain(schedule.Rules, rule => rule.Id == sameInstant.Id);
        Assert.Equal(schedule.Rules.OrderByDescending(rule => rule.OffsetMinutes), schedule.Rules);
    }

    [Fact]
    public void ResettingRestoresTheDefaults()
    {
        var schedule = new ReminderSchedule([]).Add(new ReminderRule { Amount = 9, Unit = ReminderUnit.Hours });

        Assert.Equal(ReminderSchedule.Defaults, schedule.ResetToDefaults());
    }

    [Fact]
    public void RulesCanBeMoved()
    {
        var schedule = ReminderSchedule.Defaults.Move([3], 0);

        Assert.Equal([0, 7 * 1_440, 3 * 1_440, 1_440], schedule.Rules.Select(rule => rule.OffsetMinutes));
    }

    [Fact]
    public void LegacyOffsetsThatClampToTheSameDayKeepOne()
    {
        var migrated = ReminderSchedule.FromLegacyDayOffsets([-1, 0, 2]);

        Assert.Equal([2 * 1_440, 0], migrated.Rules.Select(rule => rule.OffsetMinutes));
        Assert.Empty(ReminderSchedule.FromLegacyDayOffsets([]).Rules);
    }

    [Fact]
    public void UpdatingARuleThatIsNotThereChangesNothing()
    {
        var schedule = ReminderSchedule.Defaults;

        Assert.Equal(schedule, schedule.Update(new ReminderRule { Amount = 2, Unit = ReminderUnit.Days }));
    }

    /// <summary>
    /// The Mac's decoder takes the stored rules as written, without the
    /// clamping and de-duplication that building a schedule applies.
    /// </summary>
    [Fact]
    public void AStoredScheduleIsReadAsWritten()
    {
        const string json = """
            {"rules":[
              {"id":"6F9619FF-8B86-D011-B42D-00CF4FC964FF","amount":400,"unit":"days","isEnabled":true},
              {"id":"7F9619FF-8B86-D011-B42D-00CF4FC964FF","amount":0,"unit":"hours","isEnabled":false}
            ]}
            """;

        var schedule = JsonDefaults.Deserialize<ReminderSchedule>(json)!;

        Assert.Equal([400, 0], schedule.Rules.Select(rule => rule.Amount));
        Assert.Equal(ReminderUnit.Hours, schedule.Rules[1].Unit);
        Assert.False(schedule.Rules[1].IsEnabled);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rules\":[{\"id\":\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\",\"amount\":1,\"unit\":\"weeks\",\"isEnabled\":true}]}")]
    [InlineData("{\"rules\":[{\"id\":\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\",\"amount\":1,\"unit\":\"days\"}]}")]
    [InlineData("{\"rules\":[{\"id\":\"not-a-uuid\",\"amount\":1,\"unit\":\"days\",\"isEnabled\":true}]}")]
    [InlineData("{\"rules\":[{\"id\":\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\",\"amount\":1.5,\"unit\":\"days\",\"isEnabled\":true}]}")]
    public void AScheduleThatCannotBeReadIsRefusedWhole(string json)
    {
        Assert.Null(JsonDefaults.TryDeserialize<ReminderSchedule>(json));
    }

    [Fact]
    public void TheStoredFormIsTheMacs()
    {
        var rule = new ReminderRule { Id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), Amount = 3, Unit = ReminderUnit.Days };

        Assert.Equal(
            "{\"rules\":[{\"id\":\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\",\"amount\":3,\"unit\":\"days\",\"isEnabled\":true}]}",
            JsonDefaults.Serialize(new ReminderSchedule([rule])));
    }

    [Fact]
    public void RefusalsSayWhy()
    {
        Assert.Equal("There is already a reminder at that time.", new ReminderScheduleException(ReminderScheduleError.DuplicateOffset).Message);
        Assert.Equal("You can keep up to 10 reminders.", new ReminderScheduleException(ReminderScheduleError.TooManyRules).Message);
        Assert.Equal("Enter a reminder time within the supported range.", new ReminderScheduleException(ReminderScheduleError.InvalidAmount).Message);
        Assert.Equal("days", ReminderUnit.Days.Title());
        Assert.Equal("hours", ReminderUnit.Hours.Title());
    }
}

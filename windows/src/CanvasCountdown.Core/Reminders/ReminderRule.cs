using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

public enum ReminderUnit
{
    Days,
    Hours,
}

public static class ReminderUnitExtensions
{
    public static string RawValue(this ReminderUnit unit) => CodableJson.RawValue(unit);

    public static string Title(this ReminderUnit unit) => unit switch
    {
        ReminderUnit.Days => "days",
        ReminderUnit.Hours => "hours",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };
}

/// <summary>One entry in the user's reminder schedule.</summary>
/// <remarks>
/// A rule of zero days is the due-day reminder. Everything else fires the given
/// distance before the deadline.
/// </remarks>
[JsonConverter(typeof(ReminderRuleJsonConverter))]
public sealed record ReminderRule
{
    /// <summary>A schedule longer than this is unmanageable and floods the notification centre.</summary>
    public const int MaximumRuleCount = 10;

    public const int MaximumDays = 365;

    public const int MaximumHours = 23;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required int Amount { get; init; }

    public required ReminderUnit Unit { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Seven, three and one day before, and on the day. The ids are made once
    /// per run, so <see cref="ReminderSchedule.Defaults"/> and a schedule reset
    /// to the defaults are equal.
    /// </summary>
    public static ValueList<ReminderRule> Defaults { get; } =
    [
        new ReminderRule { Amount = 7, Unit = ReminderUnit.Days },
        new ReminderRule { Amount = 3, Unit = ReminderUnit.Days },
        new ReminderRule { Amount = 1, Unit = ReminderUnit.Days },
        new ReminderRule { Amount = 0, Unit = ReminderUnit.Days },
    ];

    /// <summary>
    /// How far before the deadline this rule fires. Used as the identity for
    /// duplicate detection and for the notification request identifier, so two
    /// rules can never produce two requests for the same instant.
    /// </summary>
    public int OffsetMinutes => Unit switch
    {
        ReminderUnit.Days => Amount * 24 * 60,
        ReminderUnit.Hours => Amount * 60,
        _ => throw new InvalidOperationException($"Unknown reminder unit {Unit}."),
    };

    public bool IsDueDayReminder => OffsetMinutes == 0;

    public string Title
    {
        get
        {
            if (IsDueDayReminder)
            {
                return "On the due day";
            }
            return Unit switch
            {
                ReminderUnit.Days => Amount == 1 ? "1 day before" : $"{Amount} days before",
                _ => Amount == 1 ? "1 hour before" : $"{Amount} hours before",
            };
        }
    }

    /// <summary>
    /// Clamps a rule into the supported range. An hours rule of zero becomes the
    /// due-day reminder so the two spellings cannot both exist.
    /// </summary>
    public ReminderRule Normalized()
    {
        var amount = Math.Max(0, Amount);
        return Unit switch
        {
            ReminderUnit.Days => this with { Amount = Math.Min(amount, MaximumDays) },
            _ when amount == 0 => this with { Amount = amount, Unit = ReminderUnit.Days },
            _ => this with { Amount = Math.Min(amount, MaximumHours) },
        };
    }
}

public enum ReminderScheduleError
{
    DuplicateOffset,
    TooManyRules,
    InvalidAmount,
}

public sealed class ReminderScheduleException : Exception
{
    public ReminderScheduleException(ReminderScheduleError error)
        : base(Describe(error))
    {
        Error = error;
    }

    public ReminderScheduleError Error { get; }

    private static string Describe(ReminderScheduleError error) => error switch
    {
        ReminderScheduleError.DuplicateOffset => "There is already a reminder at that time.",
        ReminderScheduleError.TooManyRules => $"You can keep up to {ReminderRule.MaximumRuleCount} reminders.",
        ReminderScheduleError.InvalidAmount => "Enter a reminder time within the supported range.",
        _ => "The reminder could not be saved.",
    };
}

/// <summary>The user's whole reminder schedule, with the rules that keep it sane.</summary>
/// <remarks>
/// Building one clamps every rule, drops a rule that lands on an instant
/// another already holds, keeps at most <see cref="ReminderRule.MaximumRuleCount"/>
/// and orders them furthest-out first. Reading one back from the preferences
/// does none of that: like the Mac's decoder, it takes the stored rules as
/// they were written.
/// </remarks>
[JsonConverter(typeof(ReminderScheduleJsonConverter))]
public sealed record ReminderSchedule
{
    public ReminderSchedule()
        : this(ReminderRule.Defaults)
    {
    }

    public ReminderSchedule(IEnumerable<ReminderRule> rules)
    {
        Rules = Deduplicated(rules.Select(rule => rule.Normalized()));
    }

    public static ReminderSchedule Defaults { get; } = new();

    public ValueList<ReminderRule> Rules { get; private init; }

    public ValueList<ReminderRule> EnabledRules => Rules.Where(rule => rule.IsEnabled).ToValueList();

    public bool CanAddRule => Rules.Count < ReminderRule.MaximumRuleCount;

    public bool Contains(int offsetMinutes, Guid? excluding = null)
        => Rules.Any(rule => rule.OffsetMinutes == offsetMinutes && rule.Id != excluding);

    /// <exception cref="ReminderScheduleException">The schedule is full, or a rule already fires at that time.</exception>
    public ReminderSchedule Add(ReminderRule rule)
    {
        var normalized = rule.Normalized();
        if (normalized.Amount < 0)
        {
            throw new ReminderScheduleException(ReminderScheduleError.InvalidAmount);
        }
        if (!CanAddRule)
        {
            throw new ReminderScheduleException(ReminderScheduleError.TooManyRules);
        }
        if (Contains(normalized.OffsetMinutes))
        {
            throw new ReminderScheduleException(ReminderScheduleError.DuplicateOffset);
        }
        return this with { Rules = Sorted(Rules.Add(normalized)) };
    }

    /// <summary>Replaces the rule with the same id. A rule that is not in the schedule changes nothing.</summary>
    /// <exception cref="ReminderScheduleException">Another rule already fires at that time.</exception>
    public ReminderSchedule Update(ReminderRule rule)
    {
        var normalized = rule.Normalized();
        var index = Rules.FindIndex(existing => existing.Id == normalized.Id);
        if (index < 0)
        {
            return this;
        }
        if (Contains(normalized.OffsetMinutes, excluding: normalized.Id))
        {
            throw new ReminderScheduleException(ReminderScheduleError.DuplicateOffset);
        }
        return this with { Rules = Sorted(Rules.SetItem(index, normalized)) };
    }

    public ReminderSchedule Remove(Guid id) => this with { Rules = Rules.RemoveAll(rule => rule.Id == id) };

    /// <summary>Swift's <c>move(fromOffsets:toOffset:)</c> over the rules as shown.</summary>
    public ReminderSchedule Move(IEnumerable<int> source, int destination) => this with { Rules = Rules.Move(source, destination) };

    public ReminderSchedule SetEnabled(bool isEnabled, Guid id)
    {
        var index = Rules.FindIndex(rule => rule.Id == id);
        if (index < 0)
        {
            return this;
        }
        return this with { Rules = Rules.SetItem(index, Rules[index] with { IsEnabled = isEnabled }) };
    }

    public ReminderSchedule ResetToDefaults() => this with { Rules = ReminderRule.Defaults };

    /// <summary>Migration path from the original whole-day offset list.</summary>
    public static ReminderSchedule FromLegacyDayOffsets(IEnumerable<int> offsets)
    {
        var distinct = offsets.Distinct().OrderDescending().ToList();
        if (distinct.Count == 0)
        {
            return new ReminderSchedule([]);
        }
        return new ReminderSchedule(distinct.Select(offset => new ReminderRule { Amount = Math.Max(0, offset), Unit = ReminderUnit.Days }));
    }

    internal static ReminderSchedule AsStored(ValueList<ReminderRule> rules) => new(ValueList<ReminderRule>.Empty) { Rules = rules };

    /// <summary>Furthest-out reminder first, matching how the schedule reads on screen.</summary>
    private static ValueList<ReminderRule> Sorted(IEnumerable<ReminderRule> rules)
        => rules.OrderByDescending(rule => rule.OffsetMinutes).ToValueList();

    private static ValueList<ReminderRule> Deduplicated(IEnumerable<ReminderRule> rules)
    {
        var seen = new HashSet<int>();
        var result = new List<ReminderRule>();
        foreach (var rule in rules)
        {
            if (seen.Add(rule.OffsetMinutes))
            {
                result.Add(rule);
            }
        }
        return Sorted(result.Take(ReminderRule.MaximumRuleCount));
    }
}

/// <summary>Swift's synthesized <c>Codable</c>: all four keys required.</summary>
internal sealed class ReminderRuleJsonConverter : JsonConverter<ReminderRule>
{
    public override ReminderRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Decode(JsonElement.ParseValue(ref reader));

    public override void Write(Utf8JsonWriter writer, ReminderRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", CodableJson.UuidString(value.Id));
        writer.WriteNumber("amount", value.Amount);
        writer.WriteString("unit", value.Unit.RawValue());
        writer.WriteBoolean("isEnabled", value.IsEnabled);
        writer.WriteEndObject();
    }

    internal static ReminderRule Decode(JsonElement element)
    {
        var rule = CodableJson.Object(element, "a reminder");
        return new ReminderRule
        {
            Id = CodableJson.RequiredUuid(rule, "id"),
            Amount = CodableJson.RequiredInt(rule, "amount"),
            Unit = CodableJson.RequiredEnum<ReminderUnit>(rule, "unit"),
            IsEnabled = CodableJson.RequiredBool(rule, "isEnabled"),
        };
    }
}

/// <summary>
/// Swift's synthesized <c>Codable</c>: <c>{"rules": [...]}</c>, read back
/// exactly as stored.
/// </summary>
internal sealed class ReminderScheduleJsonConverter : JsonConverter<ReminderSchedule>
{
    public override ReminderSchedule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var element = CodableJson.Object(JsonElement.ParseValue(ref reader), "the reminder schedule");
        var rules = CodableJson.RequiredArray(element, "rules")
            .EnumerateArray()
            .Select(ReminderRuleJsonConverter.Decode)
            .ToValueList();
        return ReminderSchedule.AsStored(rules);
    }

    public override void Write(Utf8JsonWriter writer, ReminderSchedule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("rules");
        JsonSerializer.Serialize(writer, value.Rules, options);
        writer.WriteEndObject();
    }
}

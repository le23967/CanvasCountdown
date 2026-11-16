using System.Globalization;
using System.Text;

namespace CanvasCountdown;

/// <summary>
/// What "Save Selected" is about to do, in the words the confirmation shows.
/// </summary>
/// <remarks>
/// The review lists rows; this says what saving them means as one sentence —
/// how many, under which course, over what dates — because several tasks are
/// written at once and the count is the thing worth checking. Kept apart from
/// the view so the numbers can be tested without a window, and worked out from
/// the same rule the save itself uses (<see cref="AssistantDraftTask.WillBeSaved"/>).
/// </remarks>
public sealed record AssistantSaveSummary
{
    public required int SavingCount { get; init; }

    /// <summary>Rows that will not be written: unticked, or still missing a date.</summary>
    public required int SkippedCount { get; init; }

    public required ValueList<string> Courses { get; init; }

    public required int WithoutCourseCount { get; init; }

    public required DateTimeOffset? Earliest { get; init; }

    public required DateTimeOffset? Latest { get; init; }

    /// <summary>
    /// Something was typed into the follow-up field and never applied. Saving
    /// now would throw that sentence away, which is the mistake this catches.
    /// </summary>
    public required bool HasUnappliedChange { get; init; }

    public bool CanSave => SavingCount > 0;

    public string Title => SavingCount == 1
        ? "Add 1 task?"
        : string.Create(CultureInfo.InvariantCulture, $"Add {SavingCount} tasks?");

    /// <summary>
    /// The confirming button. Says the number again, so the last thing read
    /// before the click is what is about to happen.
    /// </summary>
    public string ConfirmTitle => SavingCount == 1
        ? "Add 1 Task"
        : string.Create(CultureInfo.InvariantCulture, $"Add {SavingCount} Tasks");

    /// <summary>
    /// What the confirmation says about these drafts, counted by the same rule
    /// the save uses, with whatever is still in the box as the unapplied change.
    /// </summary>
    /// <remarks>
    /// Courses are named once each, in the order they first appear; a course
    /// typed two ways that read the same counts once, as on macOS.
    /// </remarks>
    public static AssistantSaveSummary For(IReadOnlyList<AssistantDraftTask> drafts, string composerInput)
    {
        var saving = drafts.Where(draft => draft.WillBeSaved).ToList();
        var courses = saving
            .Select(draft => draft.CourseName?.Trim())
            .Where(course => !string.IsNullOrEmpty(course))
            .Select(course => course!)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinctCourses = courses
            .Where(course => seen.Add(course.Normalize(NormalizationForm.FormC)))
            .ToValueList();
        var dates = saving
            .Where(draft => draft.DueDate is not null)
            .Select(draft => draft.DueDate!.Value)
            .Order()
            .ToList();

        return new AssistantSaveSummary
        {
            SavingCount = saving.Count,
            SkippedCount = drafts.Count - saving.Count,
            Courses = distinctCourses,
            WithoutCourseCount = saving.Count - courses.Count,
            Earliest = dates.Count == 0 ? null : dates[0],
            Latest = dates.Count == 0 ? null : dates[^1],
            HasUnappliedChange = AssistantComposer.HasTypedText(composerInput),
        };
    }

    /// <summary>
    /// The lines under the title, in the order they matter: what is being left
    /// out first, because that is the surprise.
    /// </summary>
    /// <remarks>
    /// Dates are read in <paramref name="calendar"/>'s zone and written with
    /// <paramref name="culture"/>'s day and abbreviated month.
    /// </remarks>
    public ValueList<string> DetailLines(AppCalendar calendar, CultureInfo culture)
    {
        var lines = new List<string>();

        if (HasUnappliedChange)
        {
            lines.Add("You typed a change that has not been applied yet. Saving now leaves it out.");
        }

        if (SkippedCount > 0)
        {
            lines.Add(SkippedCount == 1
                ? "1 draft is not ticked or has no date, and will not be added."
                : string.Create(CultureInfo.InvariantCulture, $"{SkippedCount} drafts are not ticked or have no date, and will not be added."));
        }

        if (CourseLine() is { } courseLine)
        {
            lines.Add(courseLine);
        }

        if (DateLine(calendar, culture) is { } dateLine)
        {
            lines.Add(dateLine);
        }

        lines.Add("They are added as manual events, and can be undone.");
        return lines.ToValueList();
    }

    private string? CourseLine()
    {
        var without = WithoutCourseCount;
        switch (Courses.Count, without)
        {
            case (0, 0):
                return null;
            case (0, _):
                return without == 1
                    ? "Without a course."
                    : string.Create(CultureInfo.InvariantCulture, $"All {without} without a course.");
            case (1, 0):
                return $"Under {Courses[0]}.";
            case (1, _):
                return string.Create(CultureInfo.InvariantCulture, $"Under {Courses[0]}, and {without} without a course.");
            default:
                var named = string.Join(", ", Courses);
                return without == 0
                    ? $"Under {named}."
                    : string.Create(CultureInfo.InvariantCulture, $"Under {named}, and {without} without a course.");
        }
    }

    private string? DateLine(AppCalendar calendar, CultureInfo culture)
    {
        if (Earliest is not { } earliest || Latest is not { } latest)
        {
            return null;
        }
        var first = AssistantText.DayAndMonth(calendar.ToLocal(earliest), culture);
        if (calendar.IsSameDay(earliest, latest))
        {
            return $"Due {first}.";
        }
        return $"Due {first} to {AssistantText.DayAndMonth(calendar.ToLocal(latest), culture)}.";
    }
}

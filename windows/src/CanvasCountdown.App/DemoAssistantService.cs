using System.Globalization;

namespace CanvasCountdown.App;

/// <summary>
/// A stand-in assistant for isolated launches: the UI tests and screenshots.
/// </summary>
/// <remarks>
/// It contacts nothing. Its answers are worked out from the same digests a
/// real model would be given, so the panel can be shown doing what it does —
/// answering, drafting from a sentence, revising the drafts — without a model,
/// a network or a key, and without anyone's real coursework.
/// </remarks>
internal sealed class DemoAssistantService(AppCalendar calendar) : IAssistantServicing
{
    public Task<string> AnswerAsync(
        string question,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Describe(digests, now, "Most urgent"));

    public Task<string> SummariseAsync(
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Describe(digests, now, "This week"));

    public Task<ValueList<AssistantDraftTask>> DraftTasksAsync(string text, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var due = calendar.SettingTime(calendar.AddDays(now, 3), 23, 59);
        ValueList<AssistantDraftTask> drafts =
        [
            new AssistantDraftTask { Title = text.Trim().TrimEnd('.'), DueDate = due, SourceText = text },
        ];
        return Task.FromResult(drafts);
    }

    public Task<ValueList<AssistantDraftTask>> ReviseDraftsAsync(
        IReadOnlyList<AssistantDraftTask> drafts,
        string instruction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Understands one kind of request: "put them under COMP2050".
        var words = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var course = words.LastOrDefault(word => word.Any(char.IsDigit));
        return Task.FromResult(course is null
            ? drafts.ToValueList()
            : drafts.Select(draft => draft with { CourseName = course.Trim('.', ',') }).ToValueList());
    }

    private string Describe(IReadOnlyList<AssistantAssignmentDigest> digests, DateTimeOffset now, string heading)
    {
        if (digests.Count == 0)
        {
            return "Nothing is due in the courses you are looking at.";
        }
        var first = digests[0];
        var days = calendar.DaysBetween(now, first.DueDate);
        var when = days switch
        {
            0 => "today",
            1 => "tomorrow",
            _ => $"in {days.ToString(CultureInfo.InvariantCulture)} days",
        };
        return $"{heading}: {first.Title}{(first.CourseName is { } course ? $" ({course})" : string.Empty)}, due {when}. You have {digests.Count} deadline{(digests.Count == 1 ? string.Empty : "s")} ahead.";
    }
}

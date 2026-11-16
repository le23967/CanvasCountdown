using System.Globalization;

namespace CanvasCountdown;

/// <summary>
/// The drafts waiting to be checked, and what has been asked to produce them.
/// </summary>
/// <remarks>
/// Drafting a task is a conversation, not a single shot. Correcting one wrong
/// row used to mean retyping the whole sentence, and the reply was a fresh
/// guess that threw away whatever had already been fixed by hand. So the
/// sentence that started the round stays on screen above the drafts, each
/// follow-up edits the drafts that exist, and a follow-up that fails or changes
/// nothing leaves the review exactly as it was.
///
/// Nothing here is written anywhere until <see cref="SaveAsync"/>, which can
/// only add events.
/// </remarks>
public sealed record AssistantDraftReview
{
    /// <summary>Said when a sentence produced no drafts at all.</summary>
    public const string NothingReadMessage = "Nothing could be read from that. Try naming the task and when it is due.";

    /// <summary>
    /// Said when a follow-up came back identical. Silence would read as "it
    /// worked", and the drafts would look wrong for no reason anyone could see.
    /// </summary>
    public const string NoChangeMessage = "That did not change anything. Try naming what should be different.";

    public static AssistantDraftReview Empty { get; } = new();

    public ValueList<AssistantDraftTask> Drafts { get; private init; }

    /// <summary>
    /// Everything asked in this round, oldest first, so the drafts on screen
    /// read as the result of a conversation instead of appearing from nowhere.
    /// This is also where the sentence goes once the box has been emptied, so
    /// nothing typed is lost by clearing it.
    /// </summary>
    public ValueList<string> History { get; private init; }

    public bool IsEmpty => Drafts.IsEmpty;

    public bool HasSaveableDrafts => Drafts.Any(draft => draft.WillBeSaved);

    /// <summary>A follow-up needs drafts to change, and waits for the last one to finish.</summary>
    public bool CanRevise(bool isBusy) => !Drafts.IsEmpty && !isBusy;

    /// <summary>
    /// What the confirmation says, worked out here so it is the same count the
    /// save itself will use.
    /// </summary>
    public AssistantSaveSummary Summary(string composerInput) => AssistantSaveSummary.For(Drafts, composerInput);

    /// <summary>
    /// The review a first sentence opens, or null when it produced nothing to
    /// review — in which case <see cref="NothingReadMessage"/> is what to say,
    /// and the sentence is worth leaving in the box to be edited rather than
    /// retyped.
    /// </summary>
    public static AssistantDraftReview? Started(IReadOnlyList<AssistantDraftTask> drafts, string request)
    {
        if (drafts.Count == 0)
        {
            return null;
        }
        return new AssistantDraftReview
        {
            Drafts = drafts.ToValueList(),
            History = [request.Trim()],
        };
    }

    /// <summary>
    /// The review after a follow-up, or null when the reply changed nothing —
    /// in which case <see cref="NoChangeMessage"/> is what to say, and the turn
    /// is not shown as if it had happened.
    /// </summary>
    public AssistantDraftReview? Revised(IReadOnlyList<AssistantDraftTask> revised, string instruction)
    {
        var drafts = revised.ToValueList();
        if (drafts == Drafts)
        {
            return null;
        }
        return this with { Drafts = drafts, History = History.Add(instruction.Trim()) };
    }

    /// <summary>A correction made by hand, to the draft with the same id; anything else is ignored.</summary>
    public AssistantDraftReview Updating(AssistantDraftTask draft)
    {
        var index = Drafts.FindIndex(existing => existing.Id == draft.Id);
        return index < 0 ? this : this with { Drafts = Drafts.SetItem(index, draft) };
    }

    /// <summary>
    /// Removes one draft without going back to the model. Faster than asking
    /// for it to be dropped, and it cannot misunderstand which one is meant.
    /// </summary>
    public AssistantDraftReview Removing(Guid id) => this with { Drafts = Drafts.RemoveAll(draft => draft.Id == id) };

    /// <summary>
    /// Saves only the drafts the user kept, through the ordinary manual-event
    /// path, so nothing the model produced bypasses validation.
    /// </summary>
    /// <remarks>
    /// Each kept draft becomes a new manual event — never an edit, whatever id
    /// the draft carries — and then gets the label chosen for it. Nothing that
    /// already exists is changed or removed. Every id created is remembered,
    /// because a model asked for several tasks at once can produce several
    /// wrong ones at once, and taking those back one row at a time is exactly
    /// the kind of tidying nobody should have to do to recover from a bad
    /// guess. A draft that fails is reported and the rest still go in; one
    /// whose label could not be set is reported too, and is not among the ids
    /// to take back, as on macOS.
    /// </remarks>
    public static async Task<AssistantSaveOutcome> SaveAsync(
        IAssignmentRepository repository,
        IReadOnlyList<AssistantDraftTask> drafts,
        DateTimeOffset now)
    {
        var saved = new List<Guid>();
        var errors = new List<Exception>();
        foreach (var draft in drafts)
        {
            if (!draft.WillBeSaved || draft.ToManualDraft() is not { } manual)
            {
                continue;
            }
            try
            {
                var snapshot = await repository.SaveManualAsync(manual, now).ConfigureAwait(false);
                if (draft.LabelId is { } labelId)
                {
                    await repository.SetLabelAsync(snapshot.Id, labelId, now).ConfigureAwait(false);
                }
                saved.Add(snapshot.Id);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        return new AssistantSaveOutcome
        {
            SavedIds = saved.ToValueList(),
            Errors = errors.ToValueList(),
        };
    }
}

/// <summary>What a save wrote, and what went wrong on the way.</summary>
public sealed record AssistantSaveOutcome
{
    /// <summary>The events created, which are exactly what an undo takes back.</summary>
    public required ValueList<Guid> SavedIds { get; init; }

    public required ValueList<Exception> Errors { get; init; }

    /// <summary>Nothing was added, so there is nothing to offer to undo.</summary>
    public bool OffersUndo => !SavedIds.IsEmpty;

    /// <summary>What the undo toast says: "3 tasks added".</summary>
    public string UndoMessage => SavedIds.Count == 1
        ? "1 task added"
        : string.Create(CultureInfo.InvariantCulture, $"{SavedIds.Count} tasks added");

    /// <summary>What the Edit menu says: "Undo Adding 3 Tasks".</summary>
    public string UndoMenuTitle => SavedIds.Count == 1
        ? "Undo Adding 1 Task"
        : string.Create(CultureInfo.InvariantCulture, $"Undo Adding {SavedIds.Count} Tasks");
}

using System.Globalization;

namespace CanvasCountdown;

/// <summary>
/// Everything typed into the assistant goes into one box. This is what that
/// box will do, and what it says about it, worked out from the mode, the text
/// and whether drafts are on screen — so every view of the panel says the same
/// thing.
/// </summary>
/// <remarks>
/// There used to be three fields stacked one above another — ask, describe a
/// task, change these drafts — each with its own button. The third looked like
/// the save field, which made it the one place where pressing the obvious thing
/// threw away a sentence and wrote the drafts unchanged. One box, saying
/// underneath it what it is about to do, cannot do that.
/// </remarks>
public static class AssistantComposer
{
    /// <summary>Common openers, so a blank box is not the first thing anyone meets.</summary>
    public static ValueList<string> Suggestions { get; } =
    [
        "What is most urgent?",
        "How many are due this week?",
        "What should I start today?",
    ];

    /// <summary>
    /// The one card in the empty state that says the box has a second job.
    /// Without it the mode chip is a control nobody has a reason to open, and
    /// writing a task in a sentence goes back to being undiscoverable. Choosing
    /// it switches the mode to <see cref="AssistantComposerMode.AddTask"/>.
    /// </summary>
    public const string AddTaskSuggestion = "Add a task in your own words";

    public const string ModeHelp = "Whether this box asks a question or writes a task";

    /// <summary>
    /// What the box will do when it is sent, as it stands right now.
    /// </summary>
    /// <remarks>
    /// Drafts on screen own it, whatever mode was chosen: a sentence typed while
    /// a review is open is about that review, never a new request. This is the
    /// rule that used to need a third field to express.
    ///
    /// On <see cref="AssistantComposerMode.Automatic"/> this changes as the
    /// sentence is typed, which is the point: the panel shows what it has read
    /// before anything is sent.
    /// </remarks>
    public static AssistantComposerRole Role(AssistantComposerMode mode, string input, bool hasDrafts)
    {
        if (hasDrafts)
        {
            return AssistantComposerRole.ReviseDrafts;
        }
        return mode switch
        {
            AssistantComposerMode.Ask => AssistantComposerRole.Ask,
            AssistantComposerMode.AddTask => AssistantComposerRole.AddTask,
            AssistantComposerMode.Automatic => AssistantRequestReader.ReadsAsQuestion(input)
                ? AssistantComposerRole.Ask
                : AssistantComposerRole.AddTask,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>
    /// Something is typed and has not been sent. While this is true, Enter
    /// belongs to the box rather than to Save Selected.
    /// </summary>
    public static bool HasTypedText(string input) => input.Trim().Length > 0;

    public static bool CanSend(string input, bool isBusy) => !isBusy && HasTypedText(input);

    /// <summary>
    /// Says what the box is for, in the box.
    /// </summary>
    /// <remarks>
    /// Read off the mode rather than the role, so that on automatic it names
    /// both jobs instead of naming whichever one an empty box happens to
    /// resolve to.
    /// </remarks>
    public static string Placeholder(AssistantComposerMode mode, AssistantComposerRole role)
    {
        if (role == AssistantComposerRole.ReviseDrafts)
        {
            return "Change these — “put them under 41021”";
        }
        return mode switch
        {
            AssistantComposerMode.Automatic => "Ask about your deadlines, or describe a task",
            AssistantComposerMode.Ask => "Ask about your deadlines",
            AssistantComposerMode.AddTask => "Describe a task — essay draft due next Friday at 5pm",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>
    /// The line under the box: while drafts are on screen, that a sentence will
    /// change them; on automatic, what the sentence has been read as. Null when
    /// there is nothing to say.
    /// </summary>
    public static string? Hint(AssistantComposerMode mode, AssistantComposerRole role, string input, int draftCount)
        => role == AssistantComposerRole.ReviseDrafts
            ? RevisingNotice(draftCount)
            : AutomaticReading(mode, role, input);

    /// <summary>
    /// What automatic has made of the sentence so far.
    /// </summary>
    /// <remarks>
    /// The whole safety of guessing rests on this line: it appears as soon as
    /// there is something to read, so a wrong reading is caught and the chip
    /// changed before Enter is ever pressed.
    /// </remarks>
    public static string? AutomaticReading(AssistantComposerMode mode, AssistantComposerRole role, string input)
    {
        if (mode != AssistantComposerMode.Automatic || role == AssistantComposerRole.ReviseDrafts || !HasTypedText(input))
        {
            return null;
        }
        return role == AssistantComposerRole.AddTask ? "will add a task" : "will answer";
    }

    /// <summary>What a screen reader says for <see cref="AutomaticReading"/>.</summary>
    public static string? AutomaticReadingAccessibilityLabel(AssistantComposerMode mode, AssistantComposerRole role, string input)
    {
        if (AutomaticReading(mode, role, input) is null)
        {
            return null;
        }
        return role == AssistantComposerRole.AddTask ? "Read as a task to add" : "Read as a question to answer";
    }

    /// <summary>
    /// Replaces the mode chip while drafts are on screen, because the box is no
    /// longer the user's to point somewhere: a sentence typed now is about the
    /// review above it. This line is also where the reassurance about ticks and
    /// labels lives, next to the thing it reassures about.
    /// </summary>
    public static string RevisingNotice(int draftCount) => draftCount == 1
        ? "Changing the draft above · ticks and labels kept"
        : string.Create(CultureInfo.InvariantCulture, $"Changing the {draftCount} drafts above · ticks and labels kept");

    /// <summary>What a screen reader calls the box.</summary>
    public static string FieldDescription(AssistantComposerRole role) => role switch
    {
        AssistantComposerRole.Ask => "Ask the assistant",
        AssistantComposerRole.AddTask => "Describe a task",
        AssistantComposerRole.ReviseDrafts => "Change the drafts above",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The send control's tooltip and accessible name.</summary>
    public static string SendDescription(AssistantComposerRole role) => role switch
    {
        AssistantComposerRole.Ask => "Send this question",
        AssistantComposerRole.AddTask => "Read tasks out of this sentence",
        AssistantComposerRole.ReviseDrafts => "Rewrite the drafts above using this sentence",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>
    /// Which of the two jobs the box has, named rather than drawn. A glyph here
    /// would be the eye button all over again.
    /// </summary>
    public static string ModeAccessibilityLabel(AssistantComposerMode mode) => $"Composer mode, {mode.Title()}";
}

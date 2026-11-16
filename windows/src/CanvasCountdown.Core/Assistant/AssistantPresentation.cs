using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// Where the user asked the assistant to appear.
/// </summary>
/// <remarks>
/// There is no automatic mode. Choosing the container from the window width
/// meant a tiled or split-screen window silently lost the sidebar, which is the
/// one case where the panel is most wanted and least replaceable. The width now
/// decides how wide the panel is, never whether it exists.
///
/// Stored values are read by <see cref="AssistantPresentationPreferenceJsonConverter"/>,
/// attached to each property of this type for the reason given on
/// <see cref="AssistantProvider"/>.
/// </remarks>
public enum AssistantPresentationPreference
{
    Sidebar,
    Popover,
}

public static class AssistantPresentationPreferenceExtensions
{
    public static string RawValue(this AssistantPresentationPreference preference) => preference switch
    {
        AssistantPresentationPreference.Sidebar => "sidebar",
        AssistantPresentationPreference.Popover => "popover",
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };

    public static string Title(this AssistantPresentationPreference preference) => preference switch
    {
        AssistantPresentationPreference.Sidebar => "Sidebar",
        AssistantPresentationPreference.Popover => "Popover",
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };
}

/// <summary>
/// Reads a stored <see cref="AssistantPresentationPreference"/> the way the
/// macOS decoder does.
/// </summary>
/// <remarks>
/// Anything unrecognised — notably the "automatic" that used to be stored —
/// reads as a sidebar, so an old preferences file opens on the new default
/// rather than throwing the whole assistant value back to its defaults.
/// </remarks>
public sealed class AssistantPresentationPreferenceJsonConverter : JsonConverter<AssistantPresentationPreference>
{
    public override AssistantPresentationPreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("An assistant presentation is stored as text.");
        }
        return reader.GetString() switch
        {
            "popover" => AssistantPresentationPreference.Popover,
            _ => AssistantPresentationPreference.Sidebar,
        };
    }

    public override void Write(Utf8JsonWriter writer, AssistantPresentationPreference value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.RawValue());
}

/// <summary>
/// What is actually on screen. Exactly one at a time.
/// </summary>
/// <remarks>
/// A single value rather than a handful of booleans: with <c>IsSidebarOpen</c>,
/// <c>IsPopoverOpen</c> and <c>IsWindowOpen</c> it is possible to be in two
/// places at once, and every close path has to remember to clear all three.
/// </remarks>
public enum ActiveAssistantPresentation
{
    Closed,
    Sidebar,
    Popover,
    SeparateWindow,
}

public static class ActiveAssistantPresentationExtensions
{
    public static bool IsOpen(this ActiveAssistantPresentation presentation)
        => presentation != ActiveAssistantPresentation.Closed;
}

/// <summary>
/// What the one box in the assistant is for.
/// </summary>
/// <remarks>
/// There used to be three fields — ask, describe a task, change these drafts —
/// stacked one above another, each with its own button. The third read as the
/// save field, which meant the one place a typed sentence could be thrown away
/// by pressing the wrong thing. One box that says what it is doing replaces all
/// three.
///
/// <see cref="Automatic"/> is the default (and the enum's default value) so
/// that the box costs nothing to use: most sentences say plainly enough which
/// of the two they are, and the panel shows what it read before anything is
/// sent. The other two are there for when that guess is not wanted — choosing
/// <see cref="Ask"/> means a sentence is never read as a task, however much it
/// sounds like one.
/// </remarks>
public enum AssistantComposerMode
{
    Automatic,
    Ask,
    AddTask,
}

public static class AssistantComposerModeExtensions
{
    /// <summary>
    /// Kept to two words: this is the label on a chip under the field, not a
    /// sentence.
    /// </summary>
    public static string Title(this AssistantComposerMode mode) => mode switch
    {
        AssistantComposerMode.Automatic => "Automatic",
        AssistantComposerMode.Ask => "Ask",
        AssistantComposerMode.AddTask => "Add task",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>
/// What the box will actually do when Enter is pressed right now.
/// </summary>
/// <remarks>
/// Not the same as the mode: drafts on screen take the box over, because a
/// sentence typed while a review is open is about that review, and on automatic
/// the sentence itself decides. <see cref="AssistantComposer.Role"/> works it out.
/// </remarks>
public enum AssistantComposerRole
{
    Ask,
    AddTask,
    ReviseDrafts,
}

/// <summary>
/// Reads a sentence and says which of the two it is.
/// </summary>
/// <remarks>
/// Deliberately local and deliberately plain. Sending the sentence to the model
/// to be classified would cost a round trip before anything happened, and would
/// make a box that is meant to feel free to type in the slowest part of the
/// panel. Neither answer is expensive to be wrong about: a question produces an
/// answer, and a task produces a review that writes nothing until it is
/// confirmed. The panel says which way it read the sentence while it is still
/// being typed, so a wrong guess is corrected before it is sent, not after.
///
/// Words are read as a person sees characters, not as UTF-16 units, so an
/// accented letter typed as two code points stays inside its word, as it does
/// on macOS.
/// </remarks>
public static class AssistantRequestReader
{
    /// <summary>
    /// Words that open a question about the list rather than name a thing to
    /// put on it. "Should I start the essay today" has no question mark and is
    /// still plainly a question.
    /// </summary>
    private static readonly HashSet<string> QuestionOpeners = new(StringComparer.Ordinal)
    {
        "am", "are", "can", "could", "did", "do", "does", "explain", "has",
        "have", "how", "is", "list", "must", "shall", "should", "show",
        "summarise", "summarize", "tell", "was", "were", "what", "whats",
        "when", "where", "which", "who", "why", "will", "would",
    };

    /// <summary>Verbs that ask for something to be put on the list.</summary>
    private static readonly HashSet<string> AddingOpeners = new(StringComparer.Ordinal)
    {
        "add", "book", "create", "make", "new", "note", "plan", "put",
        "remind", "schedule", "set",
    };

    /// <summary>Words that only appear when a sentence is naming a time.</summary>
    private static readonly HashSet<string> TimeWords = new(StringComparer.Ordinal)
    {
        "today", "tonight", "tomorrow", "monday", "tuesday", "wednesday",
        "thursday", "friday", "saturday", "sunday", "mon", "tue", "tues",
        "wed", "thu", "thur", "thurs", "fri", "sat", "sun", "january",
        // "may" is left out on purpose: it is a modal verb far more often than
        // it is a month, and a date in May still carries its day number.
        "february", "march", "april", "june", "july", "august",
        "september", "october", "november", "december", "jan", "feb", "mar",
        "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "am", "pm", "noon", "midnight", "morning", "afternoon", "evening",
        "weekly", "fortnightly", "daily",
    };

    public static bool ReadsAsQuestion(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            // An empty box has not said anything yet, and asking is what most
            // people open the panel to do.
            return true;
        }
        if (trimmed.EndsWith('?') || trimmed.EndsWith('？'))
        {
            return true;
        }

        var lowered = trimmed.ToLowerInvariant();
        var words = Words(lowered);
        if (words.Count == 0)
        {
            return true;
        }
        var first = words[0];
        if (QuestionOpeners.Contains(first))
        {
            return true;
        }
        if (AddingOpeners.Contains(first))
        {
            return false;
        }
        // Nothing said when. Whatever this is, it cannot become a task with a
        // due date, so it goes to the side that can answer it.
        return !NamesATime(lowered, words);
    }

    private static bool NamesATime(string text, IReadOnlyList<string> words)
    {
        if (words.Any(TimeWords.Contains))
        {
            return true;
        }
        // A clock time written as one word: 5pm, 11am.
        if (words.Any(word =>
        {
            if (!word.EndsWith("am", StringComparison.Ordinal) && !word.EndsWith("pm", StringComparison.Ordinal))
            {
                return false;
            }
            var number = AssistantText.Characters(word);
            number.RemoveRange(number.Count - 2, 2);
            return number.Count > 0 && number.All(IsNumber);
        }))
        {
            return true;
        }
        // A bare number, but only one small enough to be a day or an hour. A
        // course code is a run of digits too, and "anything for 41021" is a
        // question about the list, not a task due in the year 41021.
        if (words.Any(word =>
        {
            var characters = AssistantText.Characters(word);
            return characters.Count is >= 1 and <= 2 && characters.All(IsNumber);
        }))
        {
            return true;
        }
        // 26/8, 17:00, 26.8 — the separator is what makes these a date or a
        // time rather than two unrelated numbers.
        var all = AssistantText.Characters(text);
        for (var index = 1; index < all.Count - 1; index++)
        {
            if ((all[index] == ":" || all[index] == "/") && IsNumber(all[index - 1]) && IsNumber(all[index + 1]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The runs of letters and numbers, split on everything else.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var character in AssistantText.Characters(text))
        {
            if (IsLetter(character) || IsNumber(character))
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }
        return words;
    }

    private static bool IsLetter(string character) => Rune.IsLetter(AssistantText.FirstRune(character));

    private static bool IsNumber(string character) => Rune.IsNumber(AssistantText.FirstRune(character));
}

/// <summary>How much room the assistant panel takes beside the assignments.</summary>
public static class AssistantLayout
{
    public const double SidebarMinimum = 300;

    public const double SidebarIdeal = 380;

    public const double SidebarMaximum = 440;

    /// <summary>
    /// What the assignment list keeps when the panel is open. Below this the
    /// titles are unreadable, so the panel gives width back instead of taking
    /// more.
    /// </summary>
    public const double MainContentFloor = 300;

    /// <summary>
    /// The room the list and the panel need to sit beside each other. Below it
    /// something has to give, and the navigation pane is the cheapest thing to
    /// fold away: it is one click to bring back, and unlike the window's own
    /// width it is the app's to decide.
    /// </summary>
    public static double MinimumWidthForBoth => MainContentFloor + SidebarMinimum;

    /// <summary>
    /// The presentation to use, given what the user asked for and what is
    /// already on screen.
    /// </summary>
    /// <remarks>The window's width is not a parameter: it cannot take a container away.</remarks>
    public static ActiveAssistantPresentation Presentation(
        AssistantPresentationPreference preference,
        ActiveAssistantPresentation current)
    {
        // A detached window is only ever chosen explicitly, so nothing here
        // takes it away.
        if (current == ActiveAssistantPresentation.SeparateWindow)
        {
            return ActiveAssistantPresentation.SeparateWindow;
        }
        return preference switch
        {
            AssistantPresentationPreference.Sidebar => ActiveAssistantPresentation.Sidebar,
            AssistantPresentationPreference.Popover => ActiveAssistantPresentation.Popover,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
    }

    /// <summary>
    /// The width to actually draw the panel at, which is the preferred width
    /// trimmed to what the window already has.
    /// </summary>
    /// <remarks>
    /// This is what keeps a split-screen window the size the user left it: the
    /// panel is only ever as wide as the space beside the list, so opening it
    /// never asks for a wider window. On a window too narrow to satisfy both,
    /// the two halve it rather than one of them disappearing.
    /// </remarks>
    public static double FittedSidebarWidth(double? preferred, double availableWidth)
    {
        var wanted = ClampedSidebarWidth(preferred);
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return wanted;
        }
        var roomBesideTheList = availableWidth - MainContentFloor;
        if (roomBesideTheList < SidebarMinimum)
        {
            return Math.Min(wanted, availableWidth / 2);
        }
        return Math.Min(wanted, roomBesideTheList);
    }

    /// <summary>
    /// Keeps a stored width inside the usable range, so a bad value from
    /// preferences cannot produce an unusable panel.
    /// </summary>
    public static double ClampedSidebarWidth(double? width)
    {
        if (width is not { } value || !double.IsFinite(value) || value <= 0)
        {
            return SidebarIdeal;
        }
        return Math.Min(Math.Max(value, SidebarMinimum), SidebarMaximum);
    }

    public static bool IsValidSidebarWidth(double width)
        => double.IsFinite(width) && width >= SidebarMinimum && width <= SidebarMaximum;
}

/// <summary>The assignment an answer is about, kept short enough for a chip.</summary>
public sealed record AssistantContext
{
    public AssistantContext(AssignmentListItem item)
    {
        Id = item.Id;
        Title = item.Title;
        CourseName = item.NormalizedCourseName;
    }

    public Guid Id { get; }

    public string Title { get; }

    public string? CourseName { get; }

    /// <summary>
    /// Truncated for display. A full Canvas course title would stretch the chip
    /// across the panel. Counted in characters as a person sees them.
    /// </summary>
    public string ChipTitle => AssistantText.CharacterCount(Title) <= 32
        ? Title
        : AssistantText.CharacterPrefix(Title, 31).Trim() + "…";
}

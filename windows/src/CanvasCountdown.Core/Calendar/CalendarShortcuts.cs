namespace CanvasCountdown;

[Flags]
public enum CalendarShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
}

/// <summary>What a calendar shortcut does, named for the view model call it makes.</summary>
public enum CalendarCommand
{
    /// <summary>
    /// <c>ShowCalendarSection(scale)</c>: moves to the calendar first, so
    /// Ctrl+2 from the list means "show me the week" rather than quietly
    /// changing a setting for a screen that is not open.
    /// </summary>
    ShowScale,

    /// <summary><c>ShowCalendarSection()</c>: moves to the calendar and shows today.</summary>
    ShowToday,

    /// <summary><c>PresentCalendarDateEntry()</c>: moves to the calendar and opens Go to Date.</summary>
    GoToDate,
}

/// <summary>One of the calendar's menu commands and the keys that run it.</summary>
public sealed record CalendarShortcut
{
    /// <summary>The menu item's text.</summary>
    public required string Title { get; init; }

    public required CalendarCommand Command { get; init; }

    /// <summary>The scale <see cref="CalendarCommand.ShowScale"/> shows; null for the others.</summary>
    public CalendarScale? Scale { get; init; }

    /// <summary>The key, as printed on it: a digit, or a capital letter.</summary>
    public required char Key { get; init; }

    public required CalendarShortcutModifiers Modifiers { get; init; }

    /// <summary>A menu puts a separator above this command.</summary>
    public bool StartsGroup { get; init; }

    /// <summary>The keys the way Windows writes them in menus and tooltips: "Ctrl+1", "Ctrl+Shift+T".</summary>
    public string KeyText
    {
        get
        {
            var parts = new List<string>(3);
            if (Modifiers.HasFlag(CalendarShortcutModifiers.Control))
            {
                parts.Add("Ctrl");
            }
            if (Modifiers.HasFlag(CalendarShortcutModifiers.Shift))
            {
                parts.Add("Shift");
            }
            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }
}

/// <summary>
/// The calendar's keyboard shortcuts, in the order and groups the menu lists
/// them.
/// </summary>
/// <remarks>
/// On the Mac they are ⌘1 … ⌘4 for the scales, ⌘T for today and ⌘⇧T to type
/// a date, so they mean in this app what they mean in the system calendar,
/// and they sit in the View menu where it puts them. Windows has no ⌘ key:
/// here they are Ctrl+1 … Ctrl+4, Ctrl+T and Ctrl+Shift+T, and nothing else
/// about them changes.
/// </remarks>
public static class CalendarShortcuts
{
    public static ValueList<CalendarShortcut> All { get; } = Build();

    /// <summary>The command a key press runs, or null when it is not one of the calendar's.</summary>
    /// <param name="key">The key pressed; letters match whichever case they arrive in.</param>
    /// <param name="modifiers">Exactly the modifiers held: Ctrl+Shift+1 is not Ctrl+1.</param>
    public static CalendarShortcut? Matching(char key, CalendarShortcutModifiers modifiers)
    {
        var pressed = char.ToUpperInvariant(key);
        foreach (var shortcut in All)
        {
            if (shortcut.Key == pressed && shortcut.Modifiers == modifiers)
            {
                return shortcut;
            }
        }
        return null;
    }

    private static ValueList<CalendarShortcut> Build()
    {
        var shortcuts = new List<CalendarShortcut>();
        foreach (var scale in CalendarScaleExtensions.AllCases)
        {
            shortcuts.Add(new CalendarShortcut
            {
                Title = scale.Title(),
                Command = CalendarCommand.ShowScale,
                Scale = scale,
                Key = scale.Shortcut(),
                Modifiers = CalendarShortcutModifiers.Control,
                StartsGroup = scale == CalendarScale.Day,
            });
        }
        shortcuts.Add(new CalendarShortcut
        {
            Title = "Today",
            Command = CalendarCommand.ShowToday,
            Key = 'T',
            Modifiers = CalendarShortcutModifiers.Control,
            StartsGroup = true,
        });
        shortcuts.Add(new CalendarShortcut
        {
            Title = "Go to Date…",
            Command = CalendarCommand.GoToDate,
            Key = 'T',
            Modifiers = CalendarShortcutModifiers.Control | CalendarShortcutModifiers.Shift,
        });
        return shortcuts.ToValueList();
    }
}

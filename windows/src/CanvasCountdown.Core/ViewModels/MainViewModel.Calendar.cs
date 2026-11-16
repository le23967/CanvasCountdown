namespace CanvasCountdown;

public sealed partial class MainViewModel
{
    // ------------------------------------------------------------------
    // Calendar

    private DateTimeOffset calendarAnchor;
    private string calendarDateEntry = string.Empty;

    /// <summary>
    /// The day the calendar is centred on. Which grid is drawn around it is
    /// <see cref="CalendarScale"/>; the two together are the whole of "where am I".
    /// </summary>
    public DateTimeOffset CalendarAnchor
    {
        get => calendarAnchor;
        private set => calendarAnchor = value;
    }

    public DateTimeOffset? SelectedCalendarDay { get; private set; }

    /// <summary>What the user typed into Go to Date, kept here so it survives the box being closed and reopened.</summary>
    public string CalendarDateEntry
    {
        get => calendarDateEntry;
        set
        {
            if (calendarDateEntry == value)
            {
                return;
            }
            calendarDateEntry = value;
            Changed();
        }
    }

    /// <summary>Opened from the calendar bar and from Ctrl+Shift+T, so it lives here rather than inside the bar.</summary>
    public bool IsShowingCalendarDateEntry { get; set; }

    /// <summary>
    /// Day, week, month or year. Persisted, because a person who works in the
    /// week view expects to come back to the week view.
    /// </summary>
    public CalendarScale CalendarScale => settingsStore.CalendarScale;

    /// <summary>The month grid, built from exactly the events the list would show.</summary>
    public IReadOnlyList<CalendarDay> CalendarDays
        => AssignmentCalendar.Month(CalendarAnchor, FilteredAssignments, Calendar, CurrentDate);

    public IReadOnlyList<CalendarDay> CalendarWeekDays
        => AssignmentCalendar.Week(CalendarAnchor, FilteredAssignments, Calendar, CurrentDate);

    public CalendarDay CalendarDay
        => AssignmentCalendar.Day(CalendarAnchor, FilteredAssignments, Calendar, CurrentDate);

    public IReadOnlyList<CalendarMonthSummary> CalendarYearMonths
        => AssignmentCalendar.Year(CalendarAnchor, FilteredAssignments, Calendar, CurrentDate, Culture);

    public string CalendarTitle => AssignmentCalendar.Title(CalendarScale, CalendarAnchor, Calendar, Culture);

    /// <summary>Used by the month grid's day list, and by anything that wants the day the user last clicked.</summary>
    public IReadOnlyList<AssignmentListItem> SelectedDayItems
        => SelectedCalendarDay is { } day ? Items(day) : [];

    public IReadOnlyList<AssignmentListItem> Items(DateTimeOffset day) => AssignmentCalendar.Items(day, FilteredAssignments, Calendar);

    public void ShowCalendarScale(CalendarScale scale)
    {
        if (scale == CalendarScale)
        {
            return;
        }
        SettingsForm = SettingsForm with { CalendarScale = scale };
        ApplySettings(SettingsForm);
        SelectedCalendarDay = null;
        Changed();
    }

    /// <summary>
    /// One step of whatever is on screen: a day in Day, a week in Week, and so
    /// on, so the arrows always mean what the switcher says.
    /// </summary>
    public void ShowCalendar(int steps)
    {
        // Anchored to the start of the period first, so paging through the
        // months from the 31st does not slide a day earlier each time.
        CalendarAnchor = AssignmentCalendar.Step(CalendarAnchor, CalendarScale, steps, Calendar);
        SelectedCalendarDay = null;
        Changed();
    }

    public void ShowToday()
    {
        CalendarAnchor = CurrentDate;
        SelectedCalendarDay = null;
        Changed();
    }

    /// <summary>
    /// Moves the calendar to a day, optionally changing scale with it — how the
    /// year view drills into a month and the month view into a day.
    /// </summary>
    public void ShowCalendarDate(DateTimeOffset date, CalendarScale? scale = null)
    {
        CalendarAnchor = date;
        SelectedCalendarDay = null;
        if (scale is { } newScale)
        {
            ShowCalendarScale(newScale);
        }
        Changed();
    }

    public bool IsShowingToday => AssignmentCalendar.IsShowingToday(CalendarScale, CalendarAnchor, CurrentDate, Calendar);

    /// <summary>
    /// What Go to Date would land on, so the field can show the day before it
    /// is committed and disable itself when the text means nothing.
    /// </summary>
    public DateTimeOffset? CalendarDateEntryResult
        => CanvasCountdown.CalendarDateEntry.Date(calendarDateEntry, CalendarAnchor, Calendar, Culture, CurrentDate);

    /// <summary>What the Go to Date box says under the field as the date is typed.</summary>
    public CalendarDateEntryPreview CalendarDateEntryPreview
        => CanvasCountdown.CalendarDateEntry.Preview(calendarDateEntry, CalendarAnchor, Calendar, Culture, CurrentDate);

    /// <summary>Returns whether it went anywhere, so the field can stay open on a typo.</summary>
    public bool CommitCalendarDateEntry()
    {
        if (CalendarDateEntryResult is not { } target)
        {
            return false;
        }
        ShowCalendarDate(target);
        calendarDateEntry = string.Empty;
        IsShowingCalendarDateEntry = false;
        Changed();
        return true;
    }

    public void SelectCalendarDay(DateTimeOffset? day)
    {
        SelectedCalendarDay = day;
        Changed();
    }

    /// <summary>
    /// What the keyboard shortcuts call: it moves to the calendar first, so
    /// Ctrl+2 from the list means "show me the week" rather than quietly
    /// changing a setting for a screen that is not open.
    /// </summary>
    public void ShowCalendarSection(CalendarScale? scale = null)
    {
        SidebarSelection = SidebarDestination.Calendar;
        if (scale is { } chosen)
        {
            ShowCalendarScale(chosen);
        }
        else
        {
            ShowToday();
        }
    }

    public void PresentCalendarDateEntry()
    {
        SidebarSelection = SidebarDestination.Calendar;
        IsShowingCalendarDateEntry = true;
        Changed();
    }
}

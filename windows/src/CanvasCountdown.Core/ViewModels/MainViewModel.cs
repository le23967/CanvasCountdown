using CommunityToolkit.Mvvm.ComponentModel;

namespace CanvasCountdown;

/// <summary>
/// Everything the main window shows and does, with no Windows types in it.
/// </summary>
/// <remarks>
/// A port of the macOS <c>MainViewModel</c>, kept close to it on purpose so the
/// two can be read side by side. Derived values (the filtered list, the
/// nearest deadline, the calendar grids, search results) are computed on read
/// from a small amount of state, exactly as the Swift does; after any change
/// the view model announces that everything may have changed and the window
/// re-reads what it shows.
///
/// Continuations of the async methods resume on whatever synchronisation
/// context called them — the UI thread in the app — so nothing here touches a
/// dispatcher.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAssignmentRepository repository;
    private readonly IFeedRefreshCoordinator refreshCoordinator;
    private readonly IFeedUrlStore feedUrlStore;
    private readonly SettingsStore settingsStore;
    private readonly IDockRenderer dockRenderer;
    private readonly INotificationScheduling notificationScheduler;
    private readonly Func<AppCalendar> calendarProvider;
    private readonly Func<System.Globalization.CultureInfo> cultureProvider;
    private readonly TimeProvider clock;
    private readonly ILaunchAtLogin launchAtLogin;

    /// <summary>
    /// Disabled for isolated (automated) launches so a test host never starts
    /// a refresh loop, a countdown timer, or a launch-time feed download.
    /// </summary>
    private readonly bool automaticActivityEnabled;

    private readonly Dictionary<string, ParsedCalendarEvent> parsedPreviewEvents = new(StringComparer.Ordinal);
    private string storedFeedUrlString = string.Empty;
    private CancellationTokenSource? automaticRefreshLoop;
    private CancellationTokenSource? countdownTransitionLoop;
    private CancellationTokenSource? notificationDebounce;
    private CancellationTokenSource? transientStatusTimer;
    private bool hasStarted;
    private bool isDisposed;

    private SidebarDestination sidebarSelection = SidebarDestination.Upcoming;
    private string searchText = string.Empty;
    private string? selectedCourse;
    private bool showCompletedAndIgnored;

    public MainViewModel(MainViewModelDependencies dependencies)
    {
        repository = dependencies.Repository;
        refreshCoordinator = dependencies.RefreshCoordinator;
        feedUrlStore = dependencies.FeedUrlStore;
        settingsStore = dependencies.SettingsStore;
        dockRenderer = dependencies.DockRenderer;
        notificationScheduler = dependencies.NotificationScheduler;
        calendarProvider = dependencies.Calendar;
        cultureProvider = dependencies.Culture;
        clock = dependencies.Clock;
        launchAtLogin = dependencies.LaunchAtLogin;
        automaticActivityEnabled = dependencies.AutomaticActivityEnabled;
        assistantKeyStore = dependencies.AssistantKeyStore;
        localModels = dependencies.LocalModels;
        courseBlocklist = dependencies.CourseBlocklist;
        assistantFactory = dependencies.AssistantFactory;
        releaseChecker = dependencies.ReleaseChecker;
        updateDownloader = dependencies.UpdateDownloader;
        currentVersion = dependencies.CurrentVersion;
        openUrl = dependencies.OpenUrl;
        updateInstaller = dependencies.UpdateInstaller;

        var now = clock.GetUtcNow();
        CurrentDate = now;
        CalendarAnchor = now;
        ManualEventDraft = ManualEventDraft.New(now, Calendar);
        SettingsForm = SettingsFormState.From(settingsStore.Snapshot, feedUrl: null);
    }

    private AppCalendar Calendar => calendarProvider();

    /// <summary>The reader's language and date conventions, for every date the window writes.</summary>
    public System.Globalization.CultureInfo Culture => cultureProvider();

    private DateTimeOffset Now => clock.GetUtcNow();

    // ------------------------------------------------------------------
    // State

    public SidebarDestination SidebarSelection
    {
        get => sidebarSelection;
        set
        {
            if (sidebarSelection == value)
            {
                return;
            }
            sidebarSelection = value;
            // Moving to another section leaves search behind rather than
            // carrying a stale query and a focused field into it.
            DismissSearch();
            Changed();
        }
    }

    public IReadOnlyList<AssignmentListItem> Assignments { get; private set; } = [];

    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText == value)
            {
                return;
            }
            searchText = value;
            SearchQueryDidChange();
            Changed();
        }
    }

    /// <summary>
    /// Search is a panel over the window. This is the one switch for it; it is
    /// never inferred from whether the query happens to be empty.
    /// </summary>
    public bool IsSearchModeActive { get; private set; }

    /// <summary>Mirrors the field's focus, so focus is testable and can be released from anywhere.</summary>
    public bool IsSearchFieldFocused { get; set; }

    public string? SelectedCourse
    {
        get => selectedCourse;
        set
        {
            if (selectedCourse == value)
            {
                return;
            }
            selectedCourse = value;
            Changed();
        }
    }

    public bool ShowCompletedAndIgnored
    {
        get => showCompletedAndIgnored;
        set
        {
            if (showCompletedAndIgnored == value)
            {
                return;
            }
            showCompletedAndIgnored = value;
            Changed();
        }
    }

    public DateTimeOffset CurrentDate { get; private set; }

    public SettingsFormState SettingsForm { get; private set; }

    public NotificationAuthorizationStatus NotificationPermission { get; private set; } = NotificationAuthorizationStatus.NotDetermined;

    public bool IsLoading { get; private set; }

    public bool IsRefreshing { get; private set; }

    public bool IsShowingFeedImport { get; set; }

    public bool FeedImportIsOnboarding { get; private set; }

    public string FeedImportInitialUrl { get; private set; } = string.Empty;

    public bool IsShowingManualEditor { get; set; }

    public bool IsShowingScreenshotImport { get; set; }

    public bool IsShowingImportedDetails { get; set; }

    public AssignmentListItem? ImportedEventDetails { get; private set; }

    public ManualEventDraft ManualEventDraft { get; private set; }

    /// <summary>Row the list has focus on, used for keyboard actions.</summary>
    public Guid? SelectedEventId { get; set; }

    public string? ErrorMessage { get; private set; }

    public string? StatusMessage { get; private set; }

    public DateTimeOffset? LastRefreshDate { get; private set; }

    public bool HasConfiguredFeed => storedFeedUrlString.Length > 0;

    /// <summary>Re-read everything. Coarse, and correct: every value on screen is derived from the state above.</summary>
    private void Changed() => OnPropertyChanged(string.Empty);

    // ------------------------------------------------------------------
    // The lists

    public IReadOnlyList<string> AvailableCourses
        => Assignments
            .Select(item => item.NormalizedCourseName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(TextOrder.Comparer)
            .ToArray();

    public int UpcomingCount
        => VisibleUpcomingAssignments.Count(item => item.DueDate >= CurrentDate && !item.IsCompleted && !item.IsIgnored);

    public AssignmentListItem? NearestAssignment
        => CountdownCalculator.NearestUpcoming(VisibleUpcomingAssignments, CurrentDate, Calendar)?.Event;

    /// <summary>
    /// What Upcoming is actually showing: the persistent course scope from
    /// Settings, narrowed further by the toolbar filter.
    /// </summary>
    /// <remarks>
    /// The tray countdown deliberately does not follow the toolbar filter. That
    /// filter is a transient way to look through the list and is not
    /// persisted, so having the countdown jump around while browsing, then
    /// revert on the next launch, would be surprising. It follows the saved
    /// Settings scope.
    /// </remarks>
    public IReadOnlyList<AssignmentListItem> VisibleUpcomingAssignments
        => CourseScopedAssignments.Where(MatchesSelectedCourse).ToArray();

    private bool MatchesSelectedCourse(AssignmentListItem item)
        => SelectedCourse is null || item.NormalizedCourseName == SelectedCourse;

    /// <summary>Null selects every course.</summary>
    public void SelectCourse(string? course) => SelectedCourse = course;

    /// <summary>
    /// Assignments in scope for Upcoming, the nearest-deadline card and the
    /// tray countdown. All Events and Completed deliberately ignore this: the
    /// course selection is a focus filter, never a deletion.
    /// </summary>
    public IReadOnlyList<AssignmentListItem> CourseScopedAssignments
        => Assignments.Where(IsInSelectedCourseScope).ToArray();

    public bool IsInSelectedCourseScope(AssignmentListItem item)
    {
        if (settingsStore.DockCountMode != DockCountMode.SelectedCourses)
        {
            return true;
        }
        // Manual entries and imported events without a course are never
        // silently hidden by a course selection.
        var courseName = item.NormalizedCourseName;
        return courseName is null || settingsStore.SelectedCourses.Contains(courseName);
    }

    public IReadOnlyList<AssignmentListItem> FilteredAssignments
    {
        get
        {
            var destination = SidebarSelection;
            if (destination == SidebarDestination.Settings)
            {
                return [];
            }

            var now = CurrentDate;
            return Assignments
                .Where(item => destination switch
                {
                    SidebarDestination.Upcoming => item.DueDate >= now
                        && IsInSelectedCourseScope(item)
                        && (ShowCompletedAndIgnored || (!item.IsCompleted && !item.IsIgnored)),
                    // The calendar shows the whole library. Restricting it to
                    // what is still ahead would leave every past week blank,
                    // which is not what a month grid is for.
                    SidebarDestination.AllEvents or SidebarDestination.Calendar
                        => ShowCompletedAndIgnored || (!item.IsCompleted && !item.IsIgnored),
                    SidebarDestination.Completed => item.IsCompleted,
                    _ => false,
                })
                .Where(MatchesSelectedCourse)
                // Deliberately not narrowed by the search query. The panel
                // shows what it found; rearranging the list underneath it as
                // well would mean closing the search left you somewhere you did
                // not choose to be.
                .Order(Comparer<AssignmentListItem>.Create(CompareByDueDateThenTitle))
                .ToArray();
        }
    }

    private static int CompareByDueDateThenTitle(AssignmentListItem left, AssignmentListItem right)
    {
        var byDate = left.DueDate.CompareTo(right.DueDate);
        return byDate != 0 ? byDate : TextOrder.Compare(left.Title, right.Title);
    }

    /// <summary>Kept short so a long Canvas course title cannot stretch the toolbar.</summary>
    public string CourseFilterButtonTitle
        => SelectedCourse is null ? "All Courses" : MenuTitle(SelectedCourse, maximumLength: 18);

    public string CourseFilterDescription
    {
        get
        {
            var courses = SelectedCourse is null ? "showing all courses" : $"showing {SelectedCourse}";
            return ShowCompletedAndIgnored
                ? $"Filter, {courses}, including completed and ignored"
                : $"Filter, {courses}";
        }
    }

    /// <summary>
    /// Whether anything is being filtered out, by either rule. The
    /// completed-and-ignored switch lives inside a menu, so the toolbar has to
    /// be able to say that it is on — otherwise turning it on and forgetting
    /// looks like missing events.
    /// </summary>
    public bool IsFilterActive => SelectedCourse is not null || ShowCompletedAndIgnored;

    /// <summary>
    /// Truncates a long course title for display. The full title stays
    /// available through the tooltip and accessibility name.
    /// </summary>
    public static string MenuTitle(string? course, int maximumLength = 44)
    {
        if (course is null)
        {
            return "All Courses";
        }
        if (course.Length <= maximumLength)
        {
            return course;
        }
        return course[..(maximumLength - 1)].Trim() + "…";
    }

    // ------------------------------------------------------------------
    // Managing courses

    /// <summary>Courses the user removed, read back so Settings can offer to undo it.</summary>
    public IReadOnlyList<string> BlockedCourses { get; private set; } = [];

    /// <summary>
    /// Every course with something stored against it, plus how much, so
    /// removing one says what it is about to take with it.
    /// </summary>
    public IReadOnlyList<ManagedCourse> ManagedCourses
    {
        get
        {
            var counts = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
            foreach (var item in Assignments)
            {
                var name = item.NormalizedCourseName;
                var key = CourseName.Normalized(name);
                if (name is null || key is null)
                {
                    continue;
                }
                counts[key] = (name, (counts.TryGetValue(key, out var existing) ? existing.Count : 0) + 1);
            }
            return counts.Values
                .Select(entry => new ManagedCourse(entry.Name, entry.Count))
                .OrderBy(course => course.Name, TextOrder.Comparer)
                .ToArray();
        }
    }

    public async Task RefreshBlockedCoursesAsync()
    {
        BlockedCourses = (await courseBlocklist.LoadBlockedCoursesAsync()).Sorted();
        Changed();
    }

    /// <summary>
    /// Removes a course entirely: its events go, and the name is blocked so the
    /// next Canvas refresh does not import them all over again.
    /// </summary>
    public async Task RemoveCourseAsync(string courseName)
    {
        try
        {
            await courseBlocklist.BlockAsync(courseName);
            var removed = await repository.DeleteEventsInCourseAsync(courseName);
            if (SettingsForm.SelectedCourses.Contains(courseName))
            {
                SettingsForm = SettingsForm with { SelectedCourses = SettingsForm.SelectedCourses.Remove(courseName) };
                settingsStore.SelectedCourses = SettingsForm.SelectedCourses;
            }
            if (SelectedCourse == courseName)
            {
                SelectedCourse = null;
            }
            await RefreshBlockedCoursesAsync();
            await ReloadAssignmentsAndSynchronizeAsync();
            ShowTransientStatus(removed == 1
                ? $"Removed “{courseName}” and 1 event"
                : $"Removed “{courseName}” and {removed} events");
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    /// <summary>
    /// Lets a course back in. Nothing reappears until the next refresh, which
    /// the status message says outright rather than leaving it to be guessed.
    /// </summary>
    public async Task AllowCourseAsync(string courseName)
    {
        await courseBlocklist.UnblockAsync(courseName);
        await RefreshBlockedCoursesAsync();
        ShowTransientStatus(HasConfiguredFeed
            ? $"“{courseName}” will return on the next refresh"
            : $"“{courseName}” is no longer blocked");
    }

    // ------------------------------------------------------------------
    // Events

    public void PresentFeedImport(string? initialUrl = null, bool onboarding = false)
    {
        FeedImportInitialUrl = initialUrl ?? SettingsForm.FeedUrl;
        FeedImportIsOnboarding = onboarding;
        parsedPreviewEvents.Clear();
        IsShowingFeedImport = true;
        Changed();
    }

    /// <summary>
    /// Opens the screenshot import. A secondary method: the Canvas Calendar
    /// Feed stays the recommended way to keep deadlines current.
    /// </summary>
    public void PresentScreenshotImport()
    {
        PrepareForToolbarAction();
        IsShowingScreenshotImport = true;
        Changed();
    }

    /// <summary>
    /// Called once a screenshot import commits, so the list, the card, the
    /// sidebar count, the countdown and reminders all catch up at once — and
    /// so the import gets the same way back as anything else added in a batch.
    /// </summary>
    public async Task ScreenshotImportDidFinishAsync(ScreenshotImportCommit commit, Func<ScreenshotImportCommit, Task> undo)
    {
        try
        {
            await ReloadAssignmentsAndSynchronizeAsync();
            StatusMessage = "Screenshot import complete";
            Changed();
        }
        catch (Exception error)
        {
            Present(error);
        }
        if (!commit.CanUndo)
        {
            return;
        }
        var count = commit.Added.Count + commit.Replaced.Count;
        OfferUndo(
            commit.Added.Select(snapshot => snapshot.Id).ToArray(),
            count == 1 ? "1 event imported" : $"{count} events imported",
            count == 1 ? "Undo Importing 1 Event" : $"Undo Importing {count} Events",
            () => undo(commit));
    }

    /// <summary>Snapshot used for duplicate checking during screenshot review.</summary>
    public Task<IReadOnlyList<AssignmentSnapshot>> CurrentSnapshotsAsync() => repository.FetchAllAsync();

    public void PresentNewManualEvent()
    {
        ManualEventDraft = ManualEventDraft.New(Now, Calendar);
        IsShowingManualEditor = true;
        Changed();
    }

    /// <summary>
    /// Opens the right surface for a row: full editing for a manual event, and
    /// a details sheet limited to local state for anything Canvas owns.
    /// </summary>
    public void PresentEditor(AssignmentListItem item)
    {
        if (item.IsManual)
        {
            ManualEventDraft = ManualEventDraft.From(item);
            IsShowingManualEditor = true;
        }
        else
        {
            ImportedEventDetails = item;
            IsShowingImportedDetails = true;
        }
        Changed();
    }

    /// <summary>
    /// The imported event currently open in the details sheet, refreshed from
    /// the latest snapshot so its toggles reflect reality.
    /// </summary>
    public AssignmentListItem? CurrentImportedEventDetails
        => ImportedEventDetails is null
            ? null
            : Assignments.FirstOrDefault(item => item.Id == ImportedEventDetails.Id) ?? ImportedEventDetails;

    public async Task SaveManualEventAsync(ManualEventDraft draft)
    {
        var isNew = draft.EventId is null;
        var saved = await repository.SaveManualAsync(draft.ToAssignmentDraft(), Now);
        await ReloadAssignmentsAndSynchronizeAsync();

        // An event typed by hand can be a mistake in the same way one the
        // assistant proposed can, so it gets the same way back rather than a
        // status message that only says it happened.
        if (!isNew)
        {
            StatusMessage = "Event updated";
            Changed();
            return;
        }
        OfferUndo([saved.Id], $"Added “{saved.Title}”", $"Undo Adding “{saved.Title}”");
    }

    public async Task ToggleCompletedAsync(AssignmentListItem item)
    {
        try
        {
            await repository.UpdateStatusAsync(item.Id, !item.IsCompleted, null, Now);
            await ReloadAssignmentsAndSynchronizeAsync();
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    public async Task ToggleIgnoredAsync(AssignmentListItem item)
    {
        try
        {
            await repository.UpdateStatusAsync(item.Id, null, !item.IsIgnored, Now);
            await ReloadAssignmentsAndSynchronizeAsync();
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    /// <summary>Keyboard Enter on the focused row.</summary>
    public void OpenSelectedEvent()
    {
        if (SelectedItem is { } item)
        {
            PresentEditor(item);
        }
    }

    /// <summary>
    /// Keyboard Delete on the focused row. Only manual events can be deleted;
    /// imported ones are Canvas's to remove.
    /// </summary>
    public AssignmentListItem? DeletionCandidate() => SelectedItem is { IsManual: true } item ? item : null;

    public AssignmentListItem? SelectedItem
        => SelectedEventId is { } id ? Assignments.FirstOrDefault(item => item.Id == id) : null;

    public async Task DeleteManualEventAsync(AssignmentListItem item)
    {
        if (!item.IsManual)
        {
            return;
        }
        try
        {
            await repository.DeleteAsync(item.Id);
            await ReloadAssignmentsAndSynchronizeAsync();
            StatusMessage = "Manual event deleted";
            Changed();
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    /// <summary>Opens the event a reminder was about, if it still exists.</summary>
    public void OpenEvent(Guid id)
    {
        if (Assignments.FirstOrDefault(item => item.Id == id) is { } item)
        {
            SelectedEventId = id;
            PresentEditor(item);
        }
    }

    // ------------------------------------------------------------------
    // Labels

    public IReadOnlyList<EventLabel> EventLabels => settingsStore.EventLabels.Labels;

    public bool CanAddEventLabel => !settingsStore.EventLabels.IsFull;

    public EventLabel? Label(AssignmentListItem item) => settingsStore.EventLabels.Label(item.LabelId);

    /// <summary>
    /// Null takes the label off. The same call does both, so a row's menu is one
    /// list with a "None" at the top rather than an assign and a clear.
    /// </summary>
    public async Task SetLabelAsync(Guid? labelId, AssignmentListItem item)
    {
        try
        {
            await repository.SetLabelAsync(item.Id, labelId, Now);
            await ReloadAssignmentsAndSynchronizeAsync();
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    public EventLabelNameError? ValidateLabelName(string name, Guid? excludedId = null)
        => settingsStore.EventLabels.Validate(name, excludedId);

    public int LabelUsageCount(Guid id) => Assignments.Count(item => item.LabelId == id);

    private static readonly string[] LabelPalette =
    [
        "#E5484D", "#F5A623", "#30A46C", "#3E63DD",
        "#8E4EC6", "#0091FF", "#D6409F", "#12A594",
    ];

    /// <summary>
    /// Adds a label the user can then rename in place, rather than making them
    /// name it in a dialog before they can see it.
    /// </summary>
    public void AddEventLabel()
    {
        var used = EventLabels.Select(label => label.ColorHex.ToUpperInvariant()).ToHashSet();
        var colorHex = LabelPalette.FirstOrDefault(color => !used.Contains(color))
            ?? LabelPalette[EventLabels.Count % LabelPalette.Length];

        var name = "New Label";
        var suffix = 2;
        while (ValidateLabelName(name) == EventLabelNameError.Duplicate)
        {
            name = $"New Label {suffix}";
            suffix++;
        }
        AddEventLabel(name, colorHex);
    }

    public EventLabelNameError? AddEventLabel(string name, string colorHex)
    {
        try
        {
            var library = settingsStore.EventLabels;
            var updated = library.Add(name, colorHex, out var label);
            settingsStore.EventLabels = updated;
            ShowTransientStatus($"Added “{label.Name}”");
            return null;
        }
        catch (EventLabelNameException error)
        {
            return error.Error;
        }
    }

    public EventLabelNameError? RenameEventLabel(Guid id, string name)
    {
        try
        {
            settingsStore.EventLabels = settingsStore.EventLabels.Rename(id, name);
            Changed();
            return null;
        }
        catch (EventLabelNameException error)
        {
            return error.Error;
        }
    }

    public void RecolorEventLabel(Guid id, string colorHex)
    {
        settingsStore.EventLabels = settingsStore.EventLabels.Recolor(id, colorHex);
        Changed();
    }

    /// <summary>
    /// Deleting a label takes it off everything carrying it, so no event is
    /// left pointing at a colour that no longer exists.
    /// </summary>
    public async Task DeleteEventLabelAsync(Guid id)
    {
        var name = settingsStore.EventLabels.Label(id)?.Name;
        settingsStore.EventLabels = settingsStore.EventLabels.Remove(id);
        try
        {
            var cleared = await repository.ClearLabelAsync(id, Now);
            await ReloadAssignmentsAndSynchronizeAsync();
            if (name is not null)
            {
                ShowTransientStatus(cleared == 0
                    ? $"Deleted “{name}”"
                    : $"Deleted “{name}” and removed it from {cleared} event{(cleared == 1 ? "" : "s")}");
            }
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    // ------------------------------------------------------------------
    // Undoing an addition

    /// <summary>
    /// Something just added, and what it would take to take it back. Covers
    /// both the assistant and the ordinary editor: adding an event by hand can
    /// be a mistake in exactly the same way.
    /// </summary>
    public sealed record UndoableAddition(ValueList<Guid> EventIds, string Message, string MenuTitle)
    {
        /// <summary>
        /// How to take it back when deleting the new rows is not the whole
        /// story — a screenshot import that also updated rows from an earlier
        /// import of the same screenshot puts those back as they were.
        /// </summary>
        public Func<Task>? Undo { get; init; }
    }

    /// <summary>
    /// How long the toast stays up. Long enough to notice and read, short
    /// enough not to sit in the way — and it is not the only way back, so it
    /// can afford to leave.
    /// </summary>
    public const int UndoToastDuration = 10;

    private CancellationTokenSource? undoToastTimer;

    /// <summary>
    /// The last addition, kept until it is undone or another one replaces it.
    /// Outlives the toast on purpose: the command bar keeps an "Undo Adding…"
    /// button for as long as this exists, which is what makes it reachable by
    /// someone who never learned a keyboard shortcut.
    /// </summary>
    public UndoableAddition? LastAddition { get; private set; }

    /// <summary>Seconds left on the toast, or null once it has gone. The addition stays undoable either way.</summary>
    public int? UndoToastSecondsRemaining { get; private set; }

    public bool IsUndoToastVisible => UndoToastSecondsRemaining is not null;

    public bool CanUndoAddition => LastAddition is not null;

    /// <summary>Names what it would take back, so choosing it is not a guess.</summary>
    public string UndoAdditionMenuTitle => LastAddition?.MenuTitle ?? "Undo Adding";

    private void OfferUndo(IReadOnlyList<Guid> eventIds, string message, string menuTitle, Func<Task>? undo = null)
    {
        // A pending offer is replaced rather than queued: undo means the last
        // thing that happened, and two of them at once would be ambiguous.
        LastAddition = new UndoableAddition(eventIds.ToValueList(), message, menuTitle) { Undo = undo };
        StatusMessage = null;
        transientStatusTimer?.Cancel();
        StartUndoToastCountdown();
        Changed();
    }

    private void StartUndoToastCountdown()
    {
        undoToastTimer?.Cancel();
        var cancellation = new CancellationTokenSource();
        undoToastTimer = cancellation;
        UndoToastSecondsRemaining = UndoToastDuration;
        _ = RunUndoToastCountdownAsync(cancellation.Token);
    }

    private async Task RunUndoToastCountdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
                if (UndoToastSecondsRemaining is not { } remaining)
                {
                    return;
                }
                if (remaining <= 1)
                {
                    // The toast goes; the way back stays, in the command bar.
                    UndoToastSecondsRemaining = null;
                    Changed();
                    return;
                }
                UndoToastSecondsRemaining = remaining - 1;
                OnPropertyChanged(nameof(UndoToastSecondsRemaining));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Puts the toast away without touching what was added, and without taking the undo itself away.</summary>
    public void DismissUndoToast()
    {
        undoToastTimer?.Cancel();
        undoToastTimer = null;
        UndoToastSecondsRemaining = null;
        Changed();
    }

    /// <summary>Deletes exactly what that addition created, and nothing else.</summary>
    public async Task UndoLastAdditionAsync()
    {
        if (LastAddition is not { } addition)
        {
            return;
        }
        LastAddition = null;
        DismissUndoToast();

        var removed = 0;
        if (addition.Undo is { } undo)
        {
            try
            {
                await undo();
                removed = addition.EventIds.Count;
            }
            catch (Exception error)
            {
                Present(error);
            }
        }
        foreach (var id in addition.Undo is null ? addition.EventIds : ValueList<Guid>.Empty)
        {
            try
            {
                await repository.DeleteAsync(id);
                removed++;
            }
            catch (AssignmentRepositoryException error) when (error.Error == AssignmentRepositoryError.AssignmentNotFound)
            {
                // Already gone, by hand or by a reset. Nothing to undo, and
                // nothing worth interrupting anyone about.
            }
            catch (Exception error)
            {
                Present(error);
            }
        }

        try
        {
            await ReloadAssignmentsAndSynchronizeAsync();
        }
        catch (Exception error)
        {
            Present(error);
        }
        ShowTransientStatus(removed == 1 ? "1 event removed" : $"{removed} events removed");
    }

    // ------------------------------------------------------------------
    // Messages

    /// <summary>Briefly confirms a change without leaving a banner sitting on screen.</summary>
    public void ShowTransientStatus(string message)
    {
        StatusMessage = message;
        transientStatusTimer?.Cancel();
        var cancellation = new CancellationTokenSource();
        transientStatusTimer = cancellation;
        Changed();
        _ = ClearStatusLaterAsync(message, cancellation.Token);
    }

    private async Task ClearStatusLaterAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), clock, cancellationToken);
            if (StatusMessage == message)
            {
                StatusMessage = null;
                Changed();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ClearError()
    {
        ErrorMessage = null;
        Changed();
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        Changed();
    }

    private void Present(Exception error)
    {
        if (error is OperationCanceledException)
        {
            return;
        }
        ErrorMessage = error.Message;
        Changed();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        automaticRefreshLoop?.Cancel();
        countdownTransitionLoop?.Cancel();
        notificationDebounce?.Cancel();
        transientStatusTimer?.Cancel();
        undoToastTimer?.Cancel();
        pullCancellation?.Cancel();
    }
}

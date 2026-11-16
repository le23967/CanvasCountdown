using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanvasCountdown;

/// <summary>
/// One row of the review, as the screen binds to it.
/// </summary>
/// <remarks>
/// <para>
/// The candidate stays the sheet's. Every edit made here goes back through
/// <see cref="ScreenshotImportViewModel.Update"/>, as the macOS row's binding
/// did, so a due date typed in answers the missing-date warning however it
/// arrived. The row object itself survives edits — only its values change — so
/// a text box being typed into keeps its focus.
/// </para>
/// <para>
/// Dates are shown and edited as a day and a time of day in the app's own time
/// zone, so the date and time pickers never do calendar arithmetic of their own.
/// </para>
/// </remarks>
public sealed partial class ScreenshotImportRowViewModel : ObservableObject
{
    private static readonly (string Name, Func<ScreenshotImportRowViewModel, object?> Read)[] Observed =
    [
        (nameof(Include), row => row.Include),
        (nameof(Title), row => row.Title),
        (nameof(CourseName), row => row.CourseName),
        (nameof(CourseTitle), row => row.CourseTitle),
        (nameof(DueDate), row => row.DueDate),
        (nameof(DueDay), row => row.DueDay),
        (nameof(DueTimeOfDay), row => row.DueTimeOfDay),
        (nameof(HasDueDate), row => row.HasDueDate),
        (nameof(SelectedDuplicateAction), row => row.SelectedDuplicateAction),
        (nameof(Status), row => row.Status),
        (nameof(StatusTitle), row => row.StatusTitle),
        (nameof(CanBeImported), row => row.CanBeImported),
        (nameof(ConfidenceText), row => row.ConfidenceText),
        (nameof(RecognisedText), row => row.RecognisedText),
        (nameof(Warnings), row => row.Warnings),
        (nameof(IsPossibleDuplicate), row => row.IsPossibleDuplicate),
        (nameof(CanApplyCourseToScreenshot), row => row.CanApplyCourseToScreenshot),
        (nameof(IncludeAccessibilityLabel), row => row.IncludeAccessibilityLabel),
        (nameof(CourseAccessibilityLabel), row => row.CourseAccessibilityLabel),
    ];

    private readonly ScreenshotImportViewModel owner;
    private ScreenshotImportCandidate candidate;

    internal ScreenshotImportRowViewModel(ScreenshotImportViewModel owner, ScreenshotImportCandidate candidate)
    {
        this.owner = owner;
        this.candidate = candidate;
    }

    public ScreenshotImportCandidate Candidate => candidate;

    public Guid Id => candidate.Id;

    public Guid SourceScreenshotId => candidate.SourceScreenshotId;

    /// <summary>The tick box. Shown disabled while <see cref="CanBeImported"/> is false.</summary>
    public bool Include
    {
        get => candidate.Include;
        set => Edit(candidate with { Include = value });
    }

    public string Title
    {
        get => candidate.Title;
        set => Edit(candidate with { Title = value });
    }

    public string? CourseName
    {
        get => candidate.CourseName;
        set => Edit(candidate with { CourseName = value });
    }

    /// <summary>What the course menu shows: the course, or "No Course".</summary>
    public string CourseTitle => candidate.CourseName ?? "No Course";

    /// <summary>The courses the course menu offers below "No Course".</summary>
    public ValueList<string> CourseChoices => owner.ExistingCourseNames;

    public DateTimeOffset? DueDate
    {
        get => candidate.DueDate;
        set => Edit(candidate with { DueDate = value });
    }

    /// <summary>The due day in the app's time zone; setting it keeps the time of day.</summary>
    public DateOnly? DueDay
    {
        get => candidate.DueDate is { } due ? owner.Calendar.DateOf(due) : null;
        set
        {
            if (value is { } day && candidate.DueDate is { } due)
            {
                var calendar = owner.Calendar;
                Edit(candidate with { DueDate = calendar.FromLocal(day, calendar.TimeOf(due)) });
            }
        }
    }

    /// <summary>The due time of day in the app's time zone; setting it keeps the day.</summary>
    public TimeOnly? DueTimeOfDay
    {
        get => candidate.DueDate is { } due ? owner.Calendar.TimeOf(due) : null;
        set
        {
            if (value is { } time && candidate.DueDate is { } due)
            {
                var calendar = owner.Calendar;
                Edit(candidate with { DueDate = calendar.FromLocal(calendar.DateOf(due), time) });
            }
        }
    }

    /// <summary>False shows "Set a due date…" in place of the date and time pickers.</summary>
    public bool HasDueDate => candidate.DueDate is not null;

    public DuplicateResolution SelectedDuplicateAction
    {
        get => candidate.SelectedDuplicateAction;
        set => Edit(candidate with { SelectedDuplicateAction = value });
    }

    /// <summary>The choices offered for a possible duplicate, under "Already stored".</summary>
    public ValueList<DuplicateResolution> DuplicateChoices => DuplicateResolutionExtensions.AllCases;

    public ScreenshotCandidateStatus Status => candidate.Status;

    /// <summary>The status in words, shown with its icon: status is never colour alone.</summary>
    public string StatusTitle => candidate.Status.Title();

    public bool CanBeImported => candidate.CanBeImported;

    /// <summary>"Confidence 92%", or null when the recogniser reported no confidence.</summary>
    public string? ConfidenceText => candidate.OcrConfidence is { } confidence
        ? string.Create(CultureInfo.InvariantCulture, $"Confidence {(int)(confidence * 100)}%")
        : null;

    /// <summary>The due line exactly as recognised, or null when there was none.</summary>
    public string? RecognisedText => candidate.OriginalDueText.Length == 0
        ? null
        : $"Recognised: {candidate.OriginalDueText}";

    /// <summary>Each shown with its message; a blocking one in red, the rest in orange.</summary>
    public ValueList<ScreenshotImportWarning> Warnings => candidate.Warnings;

    /// <summary>True shows the "Already stored" choice.</summary>
    public bool IsPossibleDuplicate => candidate.PossibleDuplicate;

    public bool CanApplyCourseToScreenshot => candidate.CourseName is not null;

    public string IncludeAccessibilityLabel => $"Include {candidate.Title}";

    public string CourseAccessibilityLabel => $"Course, {candidate.CourseName ?? "none"}";

    /// <summary>"Set a due date…": a day from now, for the user to change.</summary>
    [RelayCommand]
    private void SetDueDate()
    {
        // A placeholder, as on macOS, so elapsed time is all it needs.
        Edit(candidate with { DueDate = owner.Now + TimeSpan.FromSeconds(86_400) });
    }

    /// <summary>A course from the menu, or null for "No Course".</summary>
    [RelayCommand]
    private void ChooseCourse(string? course) => Edit(candidate with { CourseName = course });

    /// <summary>"Apply This Course To All Rows From This Screenshot".</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCourseToScreenshot))]
    private void ApplyCourseToScreenshot() => owner.ApplyCourse(candidate.CourseName, candidate.SourceScreenshotId);

    internal void Refresh(ScreenshotImportCandidate next)
    {
        var before = Observed.Select(property => property.Read(this)).ToArray();
        candidate = next;
        for (var index = 0; index < Observed.Length; index++)
        {
            if (!Equals(before[index], Observed[index].Read(this)))
            {
                OnPropertyChanged(Observed[index].Name);
            }
        }
        ApplyCourseToScreenshotCommand.NotifyCanExecuteChanged();
    }

    internal void CourseChoicesChanged() => OnPropertyChanged(nameof(CourseChoices));

    private void Edit(ScreenshotImportCandidate edited) => owner.Update(edited);
}

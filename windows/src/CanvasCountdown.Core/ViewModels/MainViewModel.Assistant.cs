namespace CanvasCountdown;

public sealed partial class MainViewModel
{
    private readonly AssistantKeyStore assistantKeyStore;
    private readonly ILocalModelManaging localModels;
    private readonly ICourseBlocklist courseBlocklist;

    /// <summary>
    /// How a request to the assistant is built. Injected so the drafting and
    /// revision flow can be exercised without a model or a network.
    /// </summary>
    private readonly Func<AssistantSettings, string?, AppCalendar, IAssistantServicing> assistantFactory;

    private CancellationTokenSource? pullCancellation;
    private string assistantComposerInput = string.Empty;
    private AssistantComposerMode assistantComposerMode = AssistantComposerMode.Automatic;

    // ------------------------------------------------------------------
    // Where the assistant is

    /// <summary>
    /// One value, so the assistant cannot be in two places at once and every
    /// close path clears the same thing.
    /// </summary>
    public ActiveAssistantPresentation AssistantPresentation { get; private set; } = ActiveAssistantPresentation.Closed;

    /// <summary>The assignment a question is about, if any.</summary>
    public AssistantContext? AssistantContext { get; private set; }

    /// <summary>
    /// The one thing typed into the assistant, whatever it is for. Kept here
    /// rather than in the panel, so moving the panel never resets what was
    /// typed or said.
    /// </summary>
    public string AssistantComposerInput
    {
        get => assistantComposerInput;
        set
        {
            if (assistantComposerInput == value)
            {
                return;
            }
            assistantComposerInput = value;
            Changed();
        }
    }

    /// <summary>
    /// Which of the two things the box is for, when there are no drafts to take
    /// it over. Starts on automatic, and returns there after a save: choosing
    /// before typing is friction on every single sentence.
    /// </summary>
    public AssistantComposerMode AssistantComposerMode
    {
        get => assistantComposerMode;
        set
        {
            if (assistantComposerMode == value)
            {
                return;
            }
            assistantComposerMode = value;
            Changed();
        }
    }

    public string? AssistantSummary { get; private set; }

    public IReadOnlyList<AssistantDraftTask> AssistantDrafts { get; private set; } = [];

    public bool IsAssistantBusy { get; private set; }

    public string? AssistantErrorMessage { get; private set; }

    /// <summary>Read back so the field can show what is stored without the key ever touching preferences or diagnostics.</summary>
    public string AssistantApiKey { get; private set; } = string.Empty;

    public bool IsOllamaReachable { get; private set; }

    public IReadOnlyList<LocalModel> InstalledModels { get; private set; } = [];

    public string? PullingModel { get; private set; }

    public double PullProgress { get; private set; }

    public string? ModelMessage { get; private set; }

    public bool IsAssistantOpen => AssistantPresentation.IsOpen();

    public AssistantPresentationPreference AssistantPreference => settingsStore.Assistant.Presentation;

    /// <summary>The width the user left the panel at, trimmed to what the window has by the layout.</summary>
    public double PreferredSidebarWidth => AssistantLayout.ClampedSidebarWidth(settingsStore.Assistant.SidebarWidth);

    /// <summary>
    /// Accurate by construction: it reads the endpoint rather than the provider
    /// name, so pointing "local" at a remote host cannot produce a false claim.
    /// </summary>
    public string AssistantPrivacyLabel => settingsStore.Assistant.PrivacyLabel;

    public bool AssistantStaysOnThisPc => settingsStore.Assistant.StaysOnThisMachine;

    /// <summary>
    /// The single writer for the presentation. Every path that changes where
    /// the assistant lives goes through here rather than assigning directly.
    /// </summary>
    private void MoveAssistant(ActiveAssistantPresentation presentation)
    {
        if (presentation == AssistantPresentation)
        {
            return;
        }
        AssistantPresentation = presentation;
        Changed();
    }

    public void ToggleAssistant()
    {
        if (AssistantPresentation.IsOpen())
        {
            CloseAssistant();
        }
        else
        {
            OpenAssistant();
        }
    }

    public void OpenAssistant(AssignmentListItem? item = null)
    {
        if (item is not null)
        {
            AssistantContext = new AssistantContext(item);
        }
        MoveAssistant(AssistantLayout.Presentation(AssistantPreference, AssistantPresentation));
        Changed();
    }

    public void CloseAssistant()
    {
        AssistantPresentation = ActiveAssistantPresentation.Closed;
        Changed();
    }

    /// <summary>Explicit only: a detached window is never chosen for the user.</summary>
    public void OpenAssistantInSeparateWindow() => MoveAssistant(ActiveAssistantPresentation.SeparateWindow);

    public void ShowAssistantAsSidebar() => MoveAssistant(ActiveAssistantPresentation.Sidebar);

    public void SetAssistantPreference(AssistantPresentationPreference preference)
    {
        SettingsForm = SettingsForm with { Assistant = SettingsForm.Assistant with { Presentation = preference } };
        ApplySettings(SettingsForm);
        if (AssistantPresentation.IsOpen() && AssistantPresentation != ActiveAssistantPresentation.SeparateWindow)
        {
            MoveAssistant(AssistantLayout.Presentation(preference, AssistantPresentation));
        }
    }

    /// <summary>Stored only when usable, so a bad drag cannot leave an unopenable panel.</summary>
    public void RememberSidebarWidth(double width)
    {
        if (!AssistantLayout.IsValidSidebarWidth(width))
        {
            return;
        }
        SettingsForm = SettingsForm with { Assistant = SettingsForm.Assistant with { SidebarWidth = width } };
        ApplySettings(SettingsForm);
    }

    public void ClearAssistantContext()
    {
        AssistantContext = null;
        Changed();
    }

    /// <summary>Common openers, so a blank box is not the first thing anyone meets.</summary>
    public static IReadOnlyList<string> AssistantSuggestions { get; } =
    [
        "What is most urgent?",
        "How many are due this week?",
        "What should I start today?",
    ];

    // ------------------------------------------------------------------
    // Local models

    /// <summary>Refreshes what is installed, and whether Ollama is answering at all.</summary>
    public async Task RefreshLocalModelsAsync()
    {
        if (settingsStore.Assistant.ResolvedUrl is not { } url)
        {
            IsOllamaReachable = false;
            Changed();
            return;
        }
        IsOllamaReachable = await localModels.IsReachableAsync(url);
        if (!IsOllamaReachable)
        {
            InstalledModels = [];
            Changed();
            return;
        }
        try
        {
            InstalledModels = await localModels.InstalledModelsAsync(url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            InstalledModels = [];
        }
        Changed();
    }

    public void DownloadModel(string name)
    {
        if (settingsStore.Assistant.ResolvedUrl is not { } url)
        {
            return;
        }
        pullCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        pullCancellation = cancellation;
        PullingModel = name;
        PullProgress = 0;
        ModelMessage = null;
        Changed();
        _ = PullAsync(name, url, cancellation.Token);
    }

    private async Task PullAsync(string name, Uri url, CancellationToken cancellationToken)
    {
        var progress = new Progress<double>(fraction =>
        {
            PullProgress = fraction;
            OnPropertyChanged(nameof(PullProgress));
        });
        try
        {
            await localModels.PullAsync(name, url, progress, cancellationToken);
            await FinishDownloadAsync($"{name} is ready to use.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await FinishDownloadAsync(error.Message);
        }
    }

    private async Task FinishDownloadAsync(string message)
    {
        PullingModel = null;
        await RefreshLocalModelsAsync();
        ModelMessage = message;
        Changed();
    }

    public void CancelModelDownload()
    {
        pullCancellation?.Cancel();
        pullCancellation = null;
        PullingModel = null;
        ModelMessage = "Download cancelled.";
        Changed();
    }

    public async Task DeleteModelAsync(string name)
    {
        if (settingsStore.Assistant.ResolvedUrl is not { } url)
        {
            return;
        }
        try
        {
            await localModels.DeleteAsync(name, url);
            await RefreshLocalModelsAsync();
            ModelMessage = $"{name} was removed.";
        }
        catch (Exception error)
        {
            ModelMessage = error.Message;
        }
        Changed();
    }

    public void UseModel(string name)
    {
        SettingsForm = SettingsForm with { Assistant = SettingsForm.Assistant with { Model = name } };
        ApplySettings(SettingsForm);
    }

    // ------------------------------------------------------------------
    // Asking

    private IAssistantServicing MakeAssistant()
        => assistantFactory(settingsStore.Assistant, AssistantApiKey.Length == 0 ? null : AssistantApiKey, Calendar);

    /// <summary>Kept in settings rather than here, so closing the window or quitting no longer throws away what was asked.</summary>
    public IReadOnlyList<AssistantMessage> Conversation => settingsStore.AssistantConversation.Messages;

    /// <summary>
    /// How much of it the panel shows before offering the rest. It can run to
    /// weeks, and the newest exchange is the one being read; showing all of it
    /// every time would push the box below a wall of scrollback.
    /// </summary>
    public const int RecentConversationLimit = 6;

    public IReadOnlyList<AssistantMessage> RecentConversation
        => Conversation.Skip(Math.Max(0, Conversation.Count - RecentConversationLimit)).ToArray();

    public int EarlierConversationCount => Math.Max(0, Conversation.Count - RecentConversationLimit);

    public async Task AskAsync(string question)
    {
        var trimmed = question.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        var history = Conversation;
        RecordConversation(new AssistantMessage { Role = AssistantMessageRole.User, Text = trimmed, Date = Now });
        IsAssistantBusy = true;
        AssistantErrorMessage = null;
        Changed();
        try
        {
            var reply = await MakeAssistant().AnswerAsync(trimmed, history, AssistantDigests(), Now);
            RecordConversation(new AssistantMessage { Role = AssistantMessageRole.Assistant, Text = reply.Trim(), Date = Now });
        }
        catch (Exception error)
        {
            AssistantErrorMessage = error.Message;
        }
        finally
        {
            IsAssistantBusy = false;
            Changed();
        }
    }

    private void RecordConversation(AssistantMessage message)
    {
        settingsStore.AssistantConversation = settingsStore.AssistantConversation.Append(message);
    }

    /// <summary>
    /// Clearing throws away something that would otherwise have kept, so the
    /// panel asks first rather than doing it on one click.
    /// </summary>
    public bool IsConfirmingClearConversation { get; set; }

    public void RequestClearConversation()
    {
        if (Conversation.Count == 0)
        {
            return;
        }
        IsConfirmingClearConversation = true;
        Changed();
    }

    public void ClearConversation()
    {
        IsConfirmingClearConversation = false;
        settingsStore.AssistantConversation = settingsStore.AssistantConversation.RemoveAll();
        AssistantErrorMessage = null;
        Changed();
    }

    public async Task SummariseWorkloadAsync()
    {
        IsAssistantBusy = true;
        AssistantErrorMessage = null;
        Changed();
        try
        {
            AssistantSummary = await MakeAssistant().SummariseAsync(AssistantDigests(), Now);
        }
        catch (Exception error)
        {
            AssistantSummary = null;
            AssistantErrorMessage = error.Message;
        }
        finally
        {
            IsAssistantBusy = false;
            Changed();
        }
    }

    /// <summary>
    /// Everything asked in this round, oldest first, so the drafts on screen can
    /// be read as the result of a conversation instead of appearing from nowhere.
    /// </summary>
    public IReadOnlyList<string> AssistantDraftHistory { get; private set; } = [];

    public bool CanReviseAssistantDrafts => AssistantDrafts.Count > 0 && !IsAssistantBusy;

    // ------------------------------------------------------------------
    // The one box

    /// <summary>
    /// What the box will do when it is sent, as it stands right now.
    /// </summary>
    /// <remarks>
    /// Drafts on screen own it, whatever mode was chosen: a sentence typed while
    /// a review is open is about that review, never a new request. On automatic
    /// this changes as the sentence is typed, which is the point: the panel
    /// shows what it has read before anything is sent.
    /// </remarks>
    public AssistantComposerRole AssistantComposerRole
    {
        get
        {
            if (AssistantDrafts.Count > 0)
            {
                return AssistantComposerRole.ReviseDrafts;
            }
            return AssistantComposerMode switch
            {
                AssistantComposerMode.Ask => AssistantComposerRole.Ask,
                AssistantComposerMode.AddTask => AssistantComposerRole.AddTask,
                _ => AssistantRequestReader.ReadsAsQuestion(AssistantComposerInput)
                    ? AssistantComposerRole.Ask
                    : AssistantComposerRole.AddTask,
            };
        }
    }

    private string TrimmedComposerInput => AssistantComposerInput.Trim();

    public bool CanSendComposer => !IsAssistantBusy && TrimmedComposerInput.Length > 0;

    /// <summary>
    /// Sends what is in the box to whichever of the three it belongs to.
    /// </summary>
    /// <remarks>
    /// Each branch decides when the field is emptied, because the three are not
    /// alike: a question is gone the moment it is asked, while a sentence that
    /// failed to produce anything is worth keeping so it can be edited rather
    /// than retyped.
    /// </remarks>
    public async Task SendComposerAsync()
    {
        var text = TrimmedComposerInput;
        if (!CanSendComposer)
        {
            return;
        }
        switch (AssistantComposerRole)
        {
            case AssistantComposerRole.Ask:
                assistantComposerInput = string.Empty;
                await AskAsync(text);
                break;
            case AssistantComposerRole.AddTask:
                await DraftTaskAsync(text);
                break;
            case AssistantComposerRole.ReviseDrafts:
                await ReviseAssistantDraftsAsync(text);
                break;
        }
    }

    public async Task DraftTaskAsync(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        IsAssistantBusy = true;
        AssistantErrorMessage = null;
        Changed();
        try
        {
            var drafts = await MakeAssistant().DraftTasksAsync(trimmed, Now);
            if (drafts.Count == 0)
            {
                AssistantErrorMessage = "Nothing could be read from that. Try naming the task and when it is due.";
                return;
            }
            AssistantDrafts = drafts;
            AssistantDraftHistory = [trimmed];
            // Only now. The sentence is on screen above the drafts from here
            // on, so emptying the box loses nothing; a failure above leaves it
            // exactly as typed.
            assistantComposerInput = string.Empty;
        }
        catch (Exception error)
        {
            AssistantErrorMessage = error.Message;
        }
        finally
        {
            IsAssistantBusy = false;
            Changed();
        }
    }

    /// <summary>
    /// Changes the drafts already on screen.
    /// </summary>
    /// <remarks>
    /// "Put them all under 41021" is a sentence about the list that exists, not
    /// a new request, so it is sent with that list attached. A reply that
    /// cannot be read leaves the drafts exactly as they were: a follow-up that
    /// wipes the review would be worse than one that does nothing.
    /// </remarks>
    public async Task ReviseAssistantDraftsAsync(string instruction)
    {
        var trimmed = instruction.Trim();
        if (trimmed.Length == 0 || AssistantDrafts.Count == 0)
        {
            return;
        }
        IsAssistantBusy = true;
        AssistantErrorMessage = null;
        Changed();

        var before = AssistantDrafts;
        try
        {
            var revised = await MakeAssistant().ReviseDraftsAsync(before, trimmed, Now);
            if (revised.SequenceEqual(before))
            {
                AssistantErrorMessage = "That did not change anything. Try naming what should be different.";
                return;
            }
            AssistantDrafts = revised;
            AssistantDraftHistory = [.. AssistantDraftHistory, trimmed];
            assistantComposerInput = string.Empty;
        }
        catch (Exception error)
        {
            AssistantErrorMessage = error.Message;
        }
        finally
        {
            IsAssistantBusy = false;
            Changed();
        }
    }

    public void UpdateAssistantDraft(AssistantDraftTask draft)
    {
        var drafts = AssistantDrafts.ToList();
        var index = drafts.FindIndex(existing => existing.Id == draft.Id);
        if (index < 0)
        {
            return;
        }
        drafts[index] = draft;
        AssistantDrafts = drafts;
        Changed();
    }

    /// <summary>
    /// Removes one draft without going back to the model. Faster than asking
    /// for it to be dropped, and it cannot misunderstand which one is meant.
    /// </summary>
    public void RemoveAssistantDraft(Guid id)
    {
        AssistantDrafts = AssistantDrafts.Where(draft => draft.Id != id).ToArray();
        Changed();
    }

    public void ClearAssistantDrafts()
    {
        AssistantDrafts = [];
        AssistantDraftHistory = [];
        assistantComposerInput = string.Empty;
        AssistantErrorMessage = null;
        IsConfirmingAssistantSave = false;
        Changed();
    }

    public bool HasSaveableDrafts => AssistantDrafts.Any(draft => draft.Include && draft.CanBeSaved);

    /// <summary>Whether the panel is showing "this is what is about to be added".</summary>
    public bool IsConfirmingAssistantSave { get; private set; }

    /// <summary>
    /// Asks before writing, rather than writing on the click.
    /// </summary>
    /// <remarks>
    /// Saving is the one step here that leaves the panel and changes the list,
    /// and several tasks arrive at once. Undo afterwards is not enough on its
    /// own: the point of a review is to be told what is about to happen while
    /// it can still simply not happen.
    /// </remarks>
    public void RequestSaveAssistantDrafts()
    {
        if (!HasSaveableDrafts)
        {
            return;
        }
        IsConfirmingAssistantSave = true;
        Changed();
    }

    public void CancelSaveAssistantDrafts()
    {
        IsConfirmingAssistantSave = false;
        Changed();
    }

    /// <summary>What that confirmation says, worked out here so it is the same count that the save itself will use.</summary>
    public AssistantSaveSummary AssistantSaveSummary
        => AssistantSaveSummary.For(AssistantDrafts, AssistantComposerInput);

    /// <summary>
    /// Saves only the drafts the user kept, through the ordinary manual-event
    /// path, so nothing the model produced bypasses validation.
    /// </summary>
    /// <remarks>
    /// Every id created here is remembered, because a model asked for several
    /// tasks at once can produce several wrong ones at once, and taking those
    /// back one row at a time is exactly the kind of tidying nobody should have
    /// to do to recover from a bad guess.
    /// </remarks>
    public async Task SaveAssistantDraftsAsync(IReadOnlyList<AssistantDraftTask> drafts)
    {
        var savedIds = new List<Guid>();
        foreach (var draft in drafts.Where(draft => draft.Include && draft.CanBeSaved))
        {
            if (draft.DueDate is not { } dueDate)
            {
                continue;
            }
            try
            {
                var snapshot = await repository.SaveManualAsync(
                    new ManualAssignmentDraft
                    {
                        Title = draft.Title.Trim(),
                        CourseName = draft.CourseName?.Trim(),
                        DueDate = dueDate,
                    },
                    Now);
                if (draft.LabelId is { } labelId)
                {
                    await repository.SetLabelAsync(snapshot.Id, labelId, Now);
                }
                savedIds.Add(snapshot.Id);
            }
            catch (Exception error)
            {
                Present(error);
            }
        }

        ClearAssistantDrafts();
        // The round is over, so the box goes back to needing no choice rather
        // than staying pointed at the job that just finished.
        AssistantComposerMode = AssistantComposerMode.Automatic;
        if (savedIds.Count == 0)
        {
            return;
        }

        try
        {
            await ReloadAssignmentsAndSynchronizeAsync();
        }
        catch (Exception error)
        {
            Present(error);
        }
        OfferUndo(
            savedIds,
            savedIds.Count == 1 ? "1 task added" : $"{savedIds.Count} tasks added",
            savedIds.Count == 1 ? "Undo Adding 1 Task" : $"Undo Adding {savedIds.Count} Tasks");
    }

    // ------------------------------------------------------------------
    // Keys and saved models

    public async Task LoadAssistantKeyAsync()
    {
        try
        {
            AssistantApiKey = await assistantKeyStore.LoadAsync(settingsStore.Assistant.ActiveProfileId) ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AssistantApiKey = string.Empty;
        }
        Changed();
    }

    public async Task SaveAssistantKeyAsync(string key)
    {
        var profileId = settingsStore.Assistant.ActiveProfileId;
        try
        {
            await assistantKeyStore.SaveAsync(key, profileId);
            AssistantApiKey = key.Trim();
            ShowTransientStatus("Assistant key saved");
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    public IReadOnlyList<AssistantProfile> AssistantProfiles => settingsStore.AssistantProfiles.Profiles;

    public AssistantProfile? ActiveAssistantProfile => settingsStore.AssistantProfiles.Profile(settingsStore.Assistant.ActiveProfileId);

    public bool CanAddAssistantProfile => !settingsStore.AssistantProfiles.IsFull;

    /// <summary>
    /// Whether the assistant's box is worth a model chooser. One model is just
    /// the model; the control only earns its place once there is something to
    /// switch to.
    /// </summary>
    public bool ShowsAssistantModelPicker => settingsStore.Assistant.IsEnabled && AssistantProfiles.Count > 1;

    public string AssistantModelDescription
        => ActiveAssistantProfile is { } active ? $"AI model, using {active.Name}" : "Choose which AI model to use";

    /// <summary>
    /// Builds the starting library from whatever the assistant was already set
    /// to, and carries the existing API key onto it. Runs once, so a first
    /// launch finds the configuration already named and working.
    /// </summary>
    private async Task MigrateAssistantProfilesIfNeededAsync()
    {
        if (settingsStore.AssistantProfiles.Profiles.Count > 0)
        {
            return;
        }
        var library = AssistantProfileLibrary.Migrated(settingsStore.Assistant);
        if (library.Profiles.FirstOrDefault() is not { } first)
        {
            return;
        }
        settingsStore.AssistantProfiles = library;
        settingsStore.Assistant = settingsStore.Assistant with { ActiveProfileId = first.Id };
        SettingsForm = SettingsForm with { Assistant = SettingsForm.Assistant with { ActiveProfileId = first.Id } };
        if (first.Provider.RequiresApiKey())
        {
            try
            {
                await assistantKeyStore.AdoptLegacyKeyAsync(first.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Switches the assistant to a saved model. Its address, its model name and its own API key all come with it.</summary>
    public async Task SelectAssistantProfileAsync(Guid id)
    {
        if (settingsStore.AssistantProfiles.Profile(id) is not { } profile || profile.Id == settingsStore.Assistant.ActiveProfileId)
        {
            return;
        }
        SettingsForm = SettingsForm with { Assistant = AssistantSettings.Applying(profile, SettingsForm.Assistant) };
        ApplySettings(SettingsForm);
        await LoadAssistantKeyAsync();
        ShowTransientStatus($"Using “{profile.Name}”");
    }

    /// <summary>Adds a model and selects it, so the fields below are already editing the thing that was just added.</summary>
    public Guid? AddAssistantProfile(AssistantProfile profile)
    {
        var library = settingsStore.AssistantProfiles.Add(profile, out var id);
        if (id is not { } addedId)
        {
            return null;
        }
        settingsStore.AssistantProfiles = library;
        if (library.Profile(addedId) is not { } saved)
        {
            return null;
        }
        SettingsForm = SettingsForm with { Assistant = AssistantSettings.Applying(saved, SettingsForm.Assistant) };
        ApplySettings(SettingsForm);
        _ = LoadAssistantKeyAsync();
        ShowTransientStatus($"Added “{saved.Name}”");
        return addedId;
    }

    public void AddAssistantProfile(AssistantService service) => AddAssistantProfile(new AssistantProfile(service));

    /// <summary>
    /// A model to fill in by hand, for a service that is not on the list.
    /// Starts local, because that is the setting that cannot upload anything
    /// while it is half-configured.
    /// </summary>
    public void AddBlankAssistantProfile()
        => AddAssistantProfile(new AssistantProfile
        {
            Name = "New Model",
            Provider = AssistantProvider.Local,
            BaseUrl = AssistantProvider.Local.DefaultBaseUrl(),
            Model = AssistantProvider.Local.DefaultModel(),
        });

    public AssistantProfileNameError? RenameAssistantProfile(Guid id, string name)
    {
        var library = settingsStore.AssistantProfiles.Rename(id, name, out var error);
        if (error is not null)
        {
            return error;
        }
        settingsStore.AssistantProfiles = library;
        Changed();
        return null;
    }

    public void DuplicateAssistantProfile(Guid id)
    {
        var library = settingsStore.AssistantProfiles.Duplicate(id, out var copy);
        if (copy is not { } copyId)
        {
            return;
        }
        settingsStore.AssistantProfiles = library;
        if (library.Profile(copyId)?.Name is { } name)
        {
            ShowTransientStatus($"Duplicated as “{name}”");
        }
    }

    /// <summary>
    /// Deleting a model takes its API key with it, and moves to another so the
    /// assistant is never left pointing at nothing.
    /// </summary>
    public async Task DeleteAssistantProfileAsync(Guid id)
    {
        var name = settingsStore.AssistantProfiles.Profile(id)?.Name;
        var library = settingsStore.AssistantProfiles.Remove(id);
        settingsStore.AssistantProfiles = library;

        if (settingsStore.Assistant.ActiveProfileId == id)
        {
            SettingsForm = library.Profiles.FirstOrDefault() is { } next
                ? SettingsForm with { Assistant = AssistantSettings.Applying(next, SettingsForm.Assistant) }
                : SettingsForm with { Assistant = SettingsForm.Assistant with { ActiveProfileId = null } };
            ApplySettings(SettingsForm);
        }

        try
        {
            await assistantKeyStore.DeleteAsync(id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
        await LoadAssistantKeyAsync();
        if (name is not null)
        {
            ShowTransientStatus($"Deleted “{name}”");
        }
    }

    public AssistantProfileNameError? ValidateAssistantProfileName(string name, Guid? excludedId = null)
        => settingsStore.AssistantProfiles.Validate(name, excludedId);

    /// <summary>
    /// Points the live configuration at one of the known services. The name is
    /// left alone: it is the user's, and a service is only a starting point.
    /// </summary>
    public void UseAssistantService(AssistantService service)
    {
        var assistant = SettingsForm.Assistant with
        {
            Provider = AssistantEndpoint.IsLocal(service.BaseUrl) ? AssistantProvider.Local : AssistantProvider.Cloud,
            BaseUrl = service.BaseUrl,
        };
        if (service.Models.FirstOrDefault() is { } model)
        {
            assistant = assistant with { Model = model };
        }
        SettingsForm = SettingsForm with { Assistant = assistant };
        ApplySettings(SettingsForm);
    }

    /// <summary>
    /// Only the three agreed fields, and only for what is currently in scope.
    /// With an assignment in context the list narrows to that one item: asking
    /// about a single deadline is not a reason to send the whole term.
    /// </summary>
    public IReadOnlyList<AssistantAssignmentDigest> AssistantDigests()
        => AssistantAssignmentDigest.ForScope(VisibleUpcomingAssignments, AssistantContext, CurrentDate);

    public bool IsAssistantEnabled => settingsStore.Assistant.IsEnabled;

    public AssistantSettings AssistantSettings => settingsStore.Assistant;
}

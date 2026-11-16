namespace CanvasCountdown;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Applies a settings change the moment the user makes it.
    /// </summary>
    /// <remarks>
    /// Persistence and anything cheap happen immediately. Reminder rescheduling
    /// is debounced, because dragging through a picker or editing a reminder
    /// would otherwise rebuild every pending reminder on each keystroke.
    /// </remarks>
    public void ApplySettings(SettingsFormState form)
    {
        // Controls report their own value back when they are first drawn —
        // a radio group does, on loading. That is not a change, and saying
        // "Settings updated" for it would be saying something untrue.
        if (form == SettingsForm)
        {
            return;
        }

        // The feed URL is kept in Credential Manager and only ever changes
        // through the preview and import flow, so the form's copy is never
        // written back.
        if (form.LaunchAtLogin != launchAtLogin.IsEnabled)
        {
            try
            {
                launchAtLogin.SetEnabled(form.LaunchAtLogin);
            }
            catch (Exception error)
            {
                Present(error);
                SettingsForm = SettingsForm with { LaunchAtLogin = launchAtLogin.IsEnabled };
                Changed();
                return;
            }
        }

        SettingsForm = form;
        settingsStore.RefreshInterval = form.RefreshInterval;
        settingsStore.DockDisplayLanguage = form.DockLabel.ModelValue();
        settingsStore.DockCountMode = form.DockCourseScope.ModelValue();
        settingsStore.SelectedCourses = form.SelectedCourses;
        settingsStore.LaunchAtLogin = launchAtLogin.IsEnabled;
        settingsStore.CalendarScale = form.CalendarScale;
        settingsStore.KeepsRunningInNotificationArea = form.KeepsRunningInNotificationArea;

        // Every deadline on screen is re-read through the policy, so the change
        // has to reach the list rather than waiting for the next refresh.
        var dueTimesChanged = settingsStore.TreatsMidnightAsEndOfDay != form.TreatsMidnightAsEndOfDay;
        settingsStore.TreatsMidnightAsEndOfDay = form.TreatsMidnightAsEndOfDay;

        var remindersChanged = settingsStore.ReminderSchedule != form.ReminderSchedule;
        settingsStore.ReminderSchedule = form.ReminderSchedule;

        settingsStore.Assistant = form.Assistant;
        // Editing the address or the model edits the model it belongs to.
        // There is no separate save step: a saved model that quietly disagreed
        // with the fields above it would be worse than no saving at all.
        SyncActiveAssistantProfile(form.Assistant);

        // Stored exactly as chosen. Low-contrast colours used to be rewritten
        // here, which meant picking white and being handed black — the one
        // place in the app where a control did not do what it said.
        var appearance = form.DockAppearance;
        if (settingsStore.DockAppearance != appearance)
        {
            settingsStore.DockAppearance = appearance;
            dockRenderer.Apply(appearance);
        }

        SynchronizeDock();
        StartAutomaticRefreshLoop();
        StartCountdownTransitionLoop();

        if (dueTimesChanged)
        {
            _ = ReloadAfterSettingsChangeAsync();
        }

        if (remindersChanged)
        {
            ScheduleDebouncedNotificationUpdate();
        }
        else
        {
            ShowTransientStatus("Settings updated");
        }
        Changed();
    }

    private async Task ReloadAfterSettingsChangeAsync()
    {
        try
        {
            await ReloadAssignmentsAndSynchronizeAsync();
        }
        catch (Exception error)
        {
            Present(error);
        }
    }

    private void SyncActiveAssistantProfile(AssistantSettings settings)
    {
        if (settings.ActiveProfileId is not { } id
            || settingsStore.AssistantProfiles.Profile(id) is not { } existing
            || (existing.Provider == settings.Provider && existing.BaseUrl == settings.BaseUrl && existing.Model == settings.Model))
        {
            return;
        }
        var library = settingsStore.AssistantProfiles.Update(id, settings.Provider, settings.BaseUrl, settings.Model, out var updated);
        if (updated)
        {
            settingsStore.AssistantProfiles = library;
        }
    }

    /// <summary>Changes one setting, the way every switch and picker in Settings does.</summary>
    public void UpdateSettings(Func<SettingsFormState, SettingsFormState> change) => ApplySettings(change(SettingsForm));

    // ------------------------------------------------------------------
    // Countdown appearance

    public void ApplyDockTheme(DockThemePreset preset)
        => ApplySettings(SettingsForm with { DockAppearance = DockAppearance.Applying(preset, SettingsForm.DockAppearance) });

    /// <summary>
    /// Reverts the appearance only. Saved themes are the user's own work and
    /// survive this, so "Reset to Defaults" is never a way to lose them.
    /// </summary>
    public void ResetDockAppearance() => ApplySettings(SettingsForm with { DockAppearance = DockAppearance.Defaults });

    public IReadOnlyList<UserDockThemePreset> DockThemePresets => settingsStore.DockThemePresets.Presets;

    public DockThemeStatus DockThemeStatus => settingsStore.DockThemePresets.Status(SettingsForm.DockAppearance);

    public void ApplyDockTheme(UserDockThemePreset preset)
        => ApplySettings(SettingsForm with { DockAppearance = DockAppearance.Applying(preset, SettingsForm.DockAppearance) });

    public DockPresetNameError? ValidateDockPresetName(string name, Guid? excludedId = null)
        => DockThemePresetLibrary.Validate(name, settingsStore.DockThemePresets, excludedId).Error;

    /// <summary>
    /// Saves the colours currently in force under a new name and selects it.
    /// What is on screen is what is saved: a theme that came back different
    /// from the thing that was named would make the whole library untrustworthy.
    /// </summary>
    public DockPresetNameError? SaveDockThemePreset(string name)
    {
        if (ValidateDockPresetName(name) is { } error)
        {
            return error;
        }
        var chosen = SettingsForm.DockAppearance;
        var library = settingsStore.DockThemePresets.Add(name, chosen.Colours, out var id);
        if (id is not { } savedId)
        {
            return DockPresetNameError.Duplicate;
        }
        settingsStore.DockThemePresets = library;
        if (library.Preset(savedId) is not { } saved)
        {
            return null;
        }
        ApplySettings(SettingsForm with { DockAppearance = DockAppearance.Applying(saved, chosen) });
        ShowTransientStatus($"Saved “{saved.Name}”");
        return null;
    }

    /// <summary>Writes the current colours back over the theme they were edited from.</summary>
    public void UpdateSelectedDockThemePreset()
    {
        if (DockThemeStatus.EditedPreset is not { } preset)
        {
            return;
        }
        var chosen = SettingsForm.DockAppearance;
        var library = settingsStore.DockThemePresets.Update(preset.Id, chosen.Colours, out var updated);
        if (!updated)
        {
            return;
        }
        settingsStore.DockThemePresets = library;
        ApplySettings(SettingsForm with { DockAppearance = chosen with { UserPresetId = preset.Id } });
        ShowTransientStatus($"Updated “{preset.Name}”");
    }

    /// <summary>Throws away edits and goes back to the theme as saved.</summary>
    public void RevertToSelectedDockThemePreset()
    {
        if (DockThemeStatus.EditedPreset is { } preset)
        {
            ApplyDockTheme(preset);
        }
    }

    public DockPresetNameError? RenameDockThemePreset(Guid id, string name)
    {
        if (ValidateDockPresetName(name, id) is { } error)
        {
            return error;
        }
        var library = settingsStore.DockThemePresets.Rename(id, name, out var renamed);
        if (!renamed)
        {
            return DockPresetNameError.Duplicate;
        }
        settingsStore.DockThemePresets = library;
        Changed();
        return null;
    }

    public void DuplicateDockThemePreset(Guid id)
    {
        var library = settingsStore.DockThemePresets.Duplicate(id, out var copy);
        if (copy is not { } copyId)
        {
            return;
        }
        settingsStore.DockThemePresets = library;
        ShowTransientStatus($"Duplicated as “{library.Preset(copyId)?.Name ?? string.Empty}”");
    }

    /// <summary>Deleting the theme in use leaves its colours alone; only the name goes.</summary>
    public void DeleteDockThemePreset(Guid id)
    {
        var name = settingsStore.DockThemePresets.Preset(id)?.Name;
        settingsStore.DockThemePresets = settingsStore.DockThemePresets.Remove(id);
        if (SettingsForm.DockAppearance.UserPresetId == id)
        {
            ApplySettings(SettingsForm with
            {
                DockAppearance = SettingsForm.DockAppearance with { UserPresetId = null, Preset = DockThemePreset.Custom },
            });
        }
        if (name is not null)
        {
            ShowTransientStatus($"Deleted “{name}”");
        }
        Changed();
    }

    /// <summary>
    /// True when the chosen colours fall below the readability floors. Nothing
    /// acts on this; Settings mentions it once, next to the colours it is about.
    /// </summary>
    public bool DockAppearanceIsLowContrast => !SettingsForm.DockAppearance.HasSufficientContrast;

    // ------------------------------------------------------------------
    // Reminder schedule

    public bool CanAddReminder => SettingsForm.ReminderSchedule.CanAddRule;

    public void AddReminder(int amount, ReminderUnit unit)
        => ApplyReminderSchedule(SettingsForm.ReminderSchedule.Add(new ReminderRule { Amount = amount, Unit = unit }));

    public void UpdateReminder(ReminderRule rule) => ApplyReminderSchedule(SettingsForm.ReminderSchedule.Update(rule));

    public void RemoveReminder(Guid id) => ApplyReminderSchedule(SettingsForm.ReminderSchedule.Remove(id));

    public void SetReminderEnabled(bool isEnabled, Guid id) => ApplyReminderSchedule(SettingsForm.ReminderSchedule.SetEnabled(isEnabled, id));

    public void ResetRemindersToDefaults() => ApplyReminderSchedule(SettingsForm.ReminderSchedule.ResetToDefaults());

    private void ApplyReminderSchedule(ReminderSchedule schedule) => ApplySettings(SettingsForm with { ReminderSchedule = schedule });

    /// <summary>
    /// Windows never shows a permission prompt for notifications: they are on
    /// unless someone turned them off in Settings, and then the only way back
    /// is Settings. So this is true only when there is nothing to fix.
    /// </summary>
    public bool CanRequestNotificationPermission => NotificationPermission == NotificationAuthorizationStatus.NotDetermined;

    private void ScheduleDebouncedNotificationUpdate()
    {
        notificationDebounce?.Cancel();
        var cancellation = new CancellationTokenSource();
        notificationDebounce = cancellation;
        _ = DebouncedNotificationUpdateAsync(cancellation.Token);
    }

    private async Task DebouncedNotificationUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), clock, cancellationToken);
            await SynchronizeNotificationsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            ShowTransientStatus("Notifications rescheduled");
        }
        catch (OperationCanceledException)
        {
        }
    }
}

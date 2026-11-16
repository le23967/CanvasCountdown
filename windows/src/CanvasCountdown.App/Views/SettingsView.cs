using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using CanvasCountdown.App.Infrastructure;
using CanvasCountdown.App.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace CanvasCountdown.App.Views;

/// <summary>
/// Settings, as one scrolling page of sections, in the order the macOS app
/// has them.
/// </summary>
/// <remarks>
/// Every change applies the moment it is made; there is no Save button to
/// forget. Each section is redrawn only when what it shows changes, and text
/// is written back when the field is left, so nothing is rebuilt mid-word.
/// </remarks>
public sealed partial class SettingsView : UserControl
{
    private readonly Dictionary<string, (StackPanel Host, Func<string> Key, Action<StackPanel> Build)> sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> renderedKeys = new(StringComparer.Ordinal);
    private ReminderRule? editingRule;
    private string? reminderError;
    private string? labelError;
    private string? themeError;

    public SettingsView()
    {
        var page = new StackPanel { Spacing = 16, Padding = new Thickness(24, 4, 24, 32), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        Add(page, "feed", () => $"{ViewModel.HasConfiguredFeed}|{ViewModel.SettingsForm.FeedUrl.Length}", BuildFeed);
        Add(page, "refresh", () => ViewModel.SettingsForm.RefreshInterval.ToString(), BuildRefresh);
        Add(page, "due", () => ViewModel.SettingsForm.TreatsMidnightAsEndOfDay.ToString(), BuildDueTimes);
        Add(page, "notifications", () => $"{ViewModel.NotificationPermission}|{string.Join(",", ViewModel.SettingsForm.ReminderSchedule.Rules.Select(rule => rule.GetHashCode()))}|{editingRule?.Id}|{reminderError}", BuildNotifications);
        Add(page, "appearance", () => $"{ViewModel.SettingsForm.DockAppearance.GetHashCode()}|{string.Join(",", ViewModel.DockThemePresets.Select(preset => preset.GetHashCode()))}|{themeError}", BuildAppearance);
        Add(page, "scope", () => $"{ViewModel.SettingsForm.DockCourseScope}|{ViewModel.SettingsForm.SelectedCourses.GetHashCode()}|{string.Join(",", ViewModel.AvailableCourses)}", BuildCourseScope);
        Add(page, "courses", () => $"{string.Join(",", ViewModel.ManagedCourses.Select(course => course.GetHashCode()))}|{string.Join(",", ViewModel.BlockedCourses)}", BuildCourses);
        Add(page, "labels", () => $"{string.Join(",", ViewModel.EventLabels.Select(label => label.GetHashCode()))}|{string.Join(",", ViewModel.EventLabels.Select(label => ViewModel.LabelUsageCount(label.Id)))}|{labelError}", BuildLabels);
        Add(page, "assistant", AssistantKey, BuildAssistant);
        Add(page, "system", () => $"{ViewModel.SettingsForm.LaunchAtLogin}|{ViewModel.SettingsForm.KeepsRunningInNotificationArea}", BuildSystem);
        Add(page, "updates", () => $"{ViewModel.IsCheckingForUpdate}|{ViewModel.AvailableUpdate?.Version}|{ViewModel.UpdateCheckAnnouncement}", BuildUpdates);
        Add(page, "data", () => "static", BuildData);

        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(this, "SettingsPage");
        ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) =>
        {
            Render(force: true);
            _ = ViewModel.RefreshNotificationPermissionAsync();
        };
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    private string AssistantKey()
        => string.Join(
            "|",
            ViewModel.SettingsForm.Assistant.GetHashCode(),
            string.Join(",", ViewModel.AssistantProfiles.Select(profile => profile.GetHashCode())),
            ViewModel.AssistantApiKey.Length,
            ViewModel.IsOllamaReachable,
            string.Join(",", ViewModel.InstalledModels.Select(model => model.Name)),
            ViewModel.PullingModel,
            ViewModel.ModelMessage);

    private void Add(StackPanel page, string name, Func<string> key, Action<StackPanel> build)
    {
        var host = new StackPanel();
        page.Children.Add(host);
        sections[name] = (host, key, build);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel.SidebarSelection == SidebarDestination.Settings)
        {
            Render(force: false);
        }
    }

    private void Render(bool force)
    {
        foreach (var (name, (host, key, build)) in sections)
        {
            var current = key();
            if (!force && renderedKeys.TryGetValue(name, out var previous) && previous == current)
            {
                continue;
            }
            renderedKeys[name] = current;
            host.Children.Clear();
            build(host);
        }
    }

    private void Rerender(string name)
    {
        renderedKeys.Remove(name);
        Render(force: false);
    }

    // ------------------------------------------------------------------
    // Canvas Calendar Feed

    private void BuildFeed(StackPanel host)
    {
        var body = Card(host, "Canvas Calendar Feed");
        var url = new PasswordBox
        {
            Password = ViewModel.SettingsForm.FeedUrl,
            PlaceholderText = "Canvas calendar feed URL",
            PasswordRevealMode = PasswordRevealMode.Hidden,
        };
        AutomationProperties.SetName(url, "Canvas calendar feed URL");
        var reveal = new CheckBox { Content = "Show the URL" };
        reveal.Checked += (_, _) => url.PasswordRevealMode = PasswordRevealMode.Visible;
        reveal.Unchecked += (_, _) => url.PasswordRevealMode = PasswordRevealMode.Hidden;
        body.Children.Add(url);
        body.Children.Add(reveal);

        var connect = new Button
        {
            Content = ViewModel.HasConfiguredFeed ? "Preview & Update…" : "Connect Feed…",
            Style = Resource<Style>("AccentButtonStyle"),
        };
        connect.Click += (_, _) => ViewModel.PresentFeedImport(url.Password, onboarding: !ViewModel.HasConfiguredFeed);
        var buttons = Row(connect);
        if (ViewModel.HasConfiguredFeed)
        {
            var remove = new Button { Content = "Remove Feed" };
            remove.Click += async (_, _) =>
            {
                if (await Dialogs.ConfirmAsync(XamlRoot, "Remove the Canvas feed?", "Automatic refresh will stop. Existing imported assignments remain on this PC.", "Remove Feed") == ContentDialogResult.Primary)
                {
                    await Run(ViewModel.RemoveFeedAsync);
                }
            };
            buttons.Children.Add(remove);
        }
        body.Children.Add(buttons);
        body.Children.Add(Note("Stored in Windows Credential Manager. The URL is never written to app preferences or diagnostics."));
    }

    private void BuildRefresh(StackPanel host)
    {
        var body = Card(host, "Automatic Refresh");
        var picker = new ComboBox { Header = "Automatic refresh", MinWidth = 220 };
        foreach (var interval in new[] { RefreshInterval.Hourly, RefreshInterval.EverySixHours, RefreshInterval.EveryTwelveHours, RefreshInterval.Daily })
        {
            picker.Items.Add(new ComboBoxItem { Content = interval.Title(), Tag = interval });
        }
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().First(item => (RefreshInterval)item.Tag == ViewModel.SettingsForm.RefreshInterval);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: RefreshInterval chosen })
            {
                ViewModel.UpdateSettings(form => form with { RefreshInterval = chosen });
            }
        };
        body.Children.Add(picker);
        body.Children.Add(Note("Automatic refresh occurs while Canvas Countdown is running. You can refresh manually at any time."));
    }

    private void BuildDueTimes(StackPanel host)
    {
        var body = Card(host, "Due Times");
        var toggle = new ToggleSwitch { Header = "Show midnight deadlines as 11:59 PM", IsOn = ViewModel.SettingsForm.TreatsMidnightAsEndOfDay };
        toggle.Toggled += (_, _) => ViewModel.UpdateSettings(form => form with { TreatsMidnightAsEndOfDay = toggle.IsOn });
        body.Children.Add(toggle);
        body.Children.Add(Note("Only the time changes. The day, the days-left count and the tray number stay exactly as they are, and nothing Canvas sent is altered — turning this off brings the original times straight back."));
        body.Children.Add(Note("Most Canvas deadlines are 11:59 PM, but an all-day entry reaches this PC as 00:00, which reads as the start of the day instead of the end. Events you added yourself are never adjusted."));
    }

    // ------------------------------------------------------------------
    // Notifications

    private void BuildNotifications(StackPanel host)
    {
        var body = Card(host, "Notifications");
        if (ViewModel.NotificationPermission == NotificationAuthorizationStatus.Denied)
        {
            var open = new Button { Content = "Open Notification Settings" };
            open.Click += (_, _) => Launch("ms-settings:notifications");
            body.Children.Add(Row(new TextBlock { Text = "Notifications are turned off for Canvas Countdown in Windows Settings.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, open));
        }
        else
        {
            body.Children.Add(new TextBlock { Text = "Notifications are on", Foreground = Resource<Brush>("SystemFillColorSuccessBrush") });
        }

        foreach (var rule in ViewModel.SettingsForm.ReminderSchedule.Rules)
        {
            var toggle = new ToggleSwitch { IsOn = rule.IsEnabled, OnContent = rule.Title, OffContent = rule.Title, MinWidth = 0 };
            AutomationProperties.SetName(toggle, rule.Title);
            var id = rule.Id;
            toggle.Toggled += (_, _) => ViewModel.SetReminderEnabled(toggle.IsOn, id);
            var edit = new Button { Content = "Edit" };
            AutomationProperties.SetName(edit, $"Edit {rule.Title}");
            edit.Click += (_, _) =>
            {
                editingRule = rule;
                reminderError = null;
                Rerender("notifications");
            };
            var delete = new Button { Content = "Delete" };
            AutomationProperties.SetName(delete, $"Delete {rule.Title}");
            delete.Click += (_, _) => ViewModel.RemoveReminder(id);
            body.Children.Add(Row(toggle, edit, delete));
        }

        // Add, or change the one being edited.
        var amount = new NumberBox
        {
            Value = editingRule?.Amount ?? 1,
            Minimum = 0,
            Maximum = ReminderRule.MaximumDays,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 120,
        };
        AutomationProperties.SetName(amount, "Reminder amount");
        var unit = new ComboBox { MinWidth = 110 };
        foreach (var choice in new[] { ReminderUnit.Days, ReminderUnit.Hours })
        {
            unit.Items.Add(new ComboBoxItem { Content = choice.Title(), Tag = choice });
        }
        unit.SelectedIndex = editingRule?.Unit == ReminderUnit.Hours ? 1 : 0;
        AutomationProperties.SetName(unit, "Reminder unit");
        unit.SelectionChanged += (_, _) => amount.Maximum = unit.SelectedIndex == 1 ? ReminderRule.MaximumHours : ReminderRule.MaximumDays;
        var commit = new Button
        {
            Content = editingRule is null ? "Add" : "Save",
            IsEnabled = editingRule is not null || ViewModel.CanAddReminder,
        };
        commit.Click += (_, _) =>
        {
            var chosenUnit = unit.SelectedIndex == 1 ? ReminderUnit.Hours : ReminderUnit.Days;
            var value = double.IsNaN(amount.Value) ? 0 : (int)amount.Value;
            try
            {
                if (editingRule is { } rule)
                {
                    ViewModel.UpdateReminder(rule with { Amount = value, Unit = chosenUnit });
                }
                else
                {
                    ViewModel.AddReminder(value, chosenUnit);
                }
                editingRule = null;
                reminderError = null;
            }
            catch (ReminderScheduleException exception)
            {
                reminderError = exception.Message;
            }
            Rerender("notifications");
        };
        var editor = Row(amount, unit, new TextBlock { Text = "before", VerticalAlignment = VerticalAlignment.Center }, commit);
        if (editingRule is not null)
        {
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) =>
            {
                editingRule = null;
                reminderError = null;
                Rerender("notifications");
            };
            editor.Children.Add(cancel);
            body.Children.Add(Note($"Editing {editingRule.Title}"));
        }
        body.Children.Add(editor);
        if (reminderError is not null)
        {
            body.Children.Add(new TextBlock { Text = reminderError, Foreground = Resource<Brush>("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap });
        }

        var reset = new Button { Content = "Reset Reminders" };
        reset.Click += (_, _) => ViewModel.ResetRemindersToDefaults();
        body.Children.Add(Row(reset, Note($"{ViewModel.SettingsForm.ReminderSchedule.Rules.Count} of {ReminderRule.MaximumRuleCount}")));
        body.Children.Add(Note("Reminders are handed to Windows and arrive even when Canvas Countdown is closed. They are rebuilt whenever a due date or the schedule changes, so no duplicate arrives. Clicking one opens the event."));
    }

    // ------------------------------------------------------------------
    // The countdown's appearance and saved themes

    private void BuildAppearance(StackPanel host)
    {
        var body = Card(host, "Countdown Appearance");
        var appearance = ViewModel.SettingsForm.DockAppearance;

        // The real renderer, at the sizes Windows actually draws it.
        var previews = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var sample = ViewModel.DockSelection?.DaysRemaining ?? 7;
        foreach (var size in new[] { 16, 24, 32 })
        {
            previews.Children.Add(Preview(appearance, sample, size, $"{size} px"));
        }
        body.Children.Add(new TextBlock { Text = "In the notification area, at 100%, 150% and 200% display scale:" });
        body.Children.Add(previews);
        body.Children.Add(Note("A tray icon has room for the number only. Point at it for the full countdown — how many days, and what is due."));

        var weight = new RadioButtons { Header = "Number weight", MaxColumns = 2 };
        foreach (var choice in new[] { DockNumberWeight.Bold, DockNumberWeight.Heavy })
        {
            weight.Items.Add(new RadioButton { Content = choice.Title(), Tag = choice });
        }
        weight.SelectedIndex = appearance.NumberWeight == DockNumberWeight.Heavy ? 1 : 0;
        weight.SelectionChanged += (_, _) =>
        {
            if (weight.SelectedItem is RadioButton { Tag: DockNumberWeight chosen })
            {
                ViewModel.UpdateSettings(form => form with { DockAppearance = form.DockAppearance with { NumberWeight = chosen } });
            }
        };
        body.Children.Add(weight);

        // Themes: the four built in, then the user's own.
        body.Children.Add(new TextBlock { Text = $"Theme: {ViewModel.DockThemeStatus.Title}", Style = Resource<Style>("BodyStrongTextBlockStyle") });
        var builtIns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var preset in DockThemePresets.Selectable)
        {
            var button = new Button { Content = preset.Title() };
            button.Click += (_, _) => ViewModel.ApplyDockTheme(preset);
            builtIns.Children.Add(button);
        }
        body.Children.Add(Wrap(builtIns));

        foreach (var saved in ViewModel.DockThemePresets)
        {
            var apply = new Button { Content = saved.Name };
            apply.Click += (_, _) => ViewModel.ApplyDockTheme(saved);
            var rename = new Button { Content = "Rename…" };
            rename.Click += async (_, _) =>
            {
                if (await AskForName("Rename theme", saved.Name) is { } name)
                {
                    themeError = ViewModel.RenameDockThemePreset(saved.Id, name)?.Message();
                    Rerender("appearance");
                }
            };
            var duplicate = new Button { Content = "Duplicate" };
            duplicate.Click += (_, _) => ViewModel.DuplicateDockThemePreset(saved.Id);
            var delete = new Button { Content = "Delete" };
            delete.Click += (_, _) => ViewModel.DeleteDockThemePreset(saved.Id);
            body.Children.Add(Row(apply, rename, duplicate, delete));
        }

        var save = new Button { Content = "Save as Theme…" };
        save.Click += async (_, _) =>
        {
            if (await AskForName("Save these colours as a theme", string.Empty) is { } name)
            {
                themeError = ViewModel.SaveDockThemePreset(name)?.Message();
                Rerender("appearance");
            }
        };
        var actions = Row(save);
        if (ViewModel.DockThemeStatus.EditedPreset is { } edited)
        {
            var update = new Button { Content = $"Update “{edited.Name}”" };
            update.Click += (_, _) => ViewModel.UpdateSelectedDockThemePreset();
            var revert = new Button { Content = "Revert" };
            revert.Click += (_, _) => ViewModel.RevertToSelectedDockThemePreset();
            actions.Children.Add(update);
            actions.Children.Add(revert);
        }
        body.Children.Add(actions);
        if (themeError is not null)
        {
            body.Children.Add(new TextBlock { Text = themeError, Foreground = Resource<Brush>("SystemFillColorCriticalBrush") });
        }

        body.Children.Add(ColourRow("Background top", appearance.Colours.BackgroundTop, hex => form => form with { DockAppearance = form.DockAppearance with { Colours = form.DockAppearance.Colours with { BackgroundTop = hex }, Preset = DockThemePreset.Custom } }));
        body.Children.Add(ColourRow("Background bottom", appearance.Colours.BackgroundBottom, hex => form => form with { DockAppearance = form.DockAppearance with { Colours = form.DockAppearance.Colours with { BackgroundBottom = hex }, Preset = DockThemePreset.Custom } }));
        body.Children.Add(ColourRow("Number", appearance.Colours.Number, hex => form => form with { DockAppearance = form.DockAppearance with { Colours = form.DockAppearance.Colours with { Number = hex }, Preset = DockThemePreset.Custom } }));
        if (ViewModel.DockAppearanceIsLowContrast)
        {
            body.Children.Add(Note("These colours are low contrast on this background. They are drawn exactly as chosen."));
        }

        var reset = new Button { Content = "Reset to Defaults" };
        reset.Click += (_, _) => ViewModel.ResetDockAppearance();
        body.Children.Add(reset);

        // Said plainly, not discovered: Windows hides new tray icons.
        var taskbar = new Button { Content = "Open Taskbar Settings" };
        taskbar.Click += (_, _) => Launch("ms-settings:taskbar");
        body.Children.Add(Note("Windows 11 tucks new tray icons behind the ^ by the clock. To keep the countdown in view, drag it out onto the taskbar, or turn on Canvas Countdown under Settings → Personalisation → Taskbar → Other system tray icons. While the window is open, the taskbar button also shows the number."));
        body.Children.Add(taskbar);
    }

    private static StackPanel Preview(DockAppearance appearance, int days, int size, string caption)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.None };
        var bytes = TileRenderer.RenderPng(appearance, days, null, size);
        var bitmap = new BitmapImage();
        using (var stream = new InMemoryRandomAccessStream())
        {
            stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            bitmap.SetSource(stream);
        }
        image.Source = bitmap;
        AutomationProperties.SetName(image, $"Countdown preview at {size} pixels");
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Border { Width = 40, Height = 40, Child = image },
                new TextBlock { Text = caption, Style = Resource<Style>("CaptionTextBlockStyle"), HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
    }

    private StackPanel ColourRow(string title, string hex, Func<string, Func<SettingsFormState, SettingsFormState>> change)
    {
        var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 32, Height = 20, RadiusX = 4, RadiusY = 4, Fill = Palette.FromHex(hex) };
        var picker = new ColorPicker
        {
            Color = Palette.ColorFromHex(hex),
            IsAlphaEnabled = false,
            IsColorPreviewVisible = true,
            IsMoreButtonVisible = false,
        };
        var flyout = new Flyout { Content = picker };
        flyout.Closed += (_, _) =>
        {
            var chosen = Palette.HexFromColor(picker.Color);
            if (!string.Equals(chosen, hex, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.UpdateSettings(change(chosen));
            }
        };
        var button = new Button { Content = Row(swatch, new TextBlock { Text = hex.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center }), Flyout = flyout };
        AutomationProperties.SetName(button, $"{title} colour, {hex}");
        return Row(new TextBlock { Text = title, Width = 160, VerticalAlignment = VerticalAlignment.Center }, button);
    }

    // ------------------------------------------------------------------
    // Course filtering, and removing courses

    private void BuildCourseScope(StackPanel host)
    {
        var body = Card(host, "Course Filtering");
        var scope = new RadioButtons { Header = "Include" };
        scope.Items.Add(new RadioButton { Content = DockCourseScopeOption.AllAssignments.Title(), Tag = DockCourseScopeOption.AllAssignments });
        scope.Items.Add(new RadioButton { Content = DockCourseScopeOption.SelectedCourses.Title(), Tag = DockCourseScopeOption.SelectedCourses });
        scope.SelectedIndex = ViewModel.SettingsForm.DockCourseScope == DockCourseScopeOption.SelectedCourses ? 1 : 0;
        scope.SelectionChanged += (_, _) =>
        {
            if (scope.SelectedItem is RadioButton { Tag: DockCourseScopeOption chosen })
            {
                ViewModel.UpdateSettings(form => form with { DockCourseScope = chosen });
            }
        };
        body.Children.Add(scope);

        if (ViewModel.SettingsForm.DockCourseScope == DockCourseScopeOption.SelectedCourses)
        {
            if (ViewModel.AvailableCourses.Count == 0)
            {
                body.Children.Add(Note("Courses appear here after events are imported."));
            }
            else
            {
                body.Children.Add(new TextBlock { Text = "Included courses", Style = Resource<Style>("BodyStrongTextBlockStyle") });
                foreach (var course in ViewModel.AvailableCourses)
                {
                    var box = new CheckBox { Content = course, IsChecked = ViewModel.SettingsForm.SelectedCourses.Contains(course) };
                    box.Click += (_, _) => ViewModel.UpdateSettings(form => form with
                    {
                        SelectedCourses = box.IsChecked == true ? form.SelectedCourses.Add(course) : form.SelectedCourses.Remove(course),
                    });
                    body.Children.Add(box);
                }
            }
        }
        body.Children.Add(Note("Applies to Upcoming, the nearest deadline card and the tray countdown. All Events and Completed always show everything, and events without a course are always included."));
    }

    private void BuildCourses(StackPanel host)
    {
        var body = Card(host, "Courses");
        if (ViewModel.ManagedCourses.Count == 0)
        {
            body.Children.Add(Note("Courses appear here once events are imported."));
        }
        foreach (var course in ViewModel.ManagedCourses)
        {
            var remove = new Button { Content = "Remove…" };
            ToolTipService.SetToolTip(remove, "Remove this course and everything in it");
            AutomationProperties.SetName(remove, $"Remove {course.Name}");
            remove.Click += async (_, _) =>
            {
                var message = $"“{course.Name}” and its {course.EventCountDescription} will be deleted from this PC, and the Canvas feed will stop importing it. You can allow it again later.";
                if (await Dialogs.ConfirmAsync(XamlRoot, "Remove this course?", message, "Remove Course") == ContentDialogResult.Primary)
                {
                    await ViewModel.RemoveCourseAsync(course.Name);
                }
            };
            var text = new StackPanel { Width = 360 };
            text.Children.Add(new TextBlock { Text = course.Name, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Note(course.EventCountDescription));
            body.Children.Add(Row(text, remove));
        }
        if (ViewModel.BlockedCourses.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Removed", Style = Resource<Style>("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 8, 0, 0) });
            foreach (var name in ViewModel.BlockedCourses)
            {
                var allow = new Button { Content = "Allow Again" };
                AutomationProperties.SetName(allow, $"Allow {name} again");
                allow.Click += async (_, _) => await ViewModel.AllowCourseAsync(name);
                body.Children.Add(Row(new TextBlock { Text = name, Width = 360, VerticalAlignment = VerticalAlignment.Center, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") }, allow));
            }
        }
        body.Children.Add(Note("Removing a course permanently deletes its events on this PC and stops the Canvas feed from importing it again. Manual events are never touched. Allowing a course again brings it back on the next refresh."));
    }

    // ------------------------------------------------------------------
    // Labels

    private void BuildLabels(StackPanel host)
    {
        var body = Card(host, "Labels");
        if (ViewModel.EventLabels.Count == 0)
        {
            body.Children.Add(Note("No labels yet. Add one to colour events in the list and the calendar."));
        }
        foreach (var label in ViewModel.EventLabels)
        {
            var id = label.Id;
            var colour = ColourRow(string.Empty, label.ColorHex, hex => form => form);
            // The row's own picker writes straight to the label.
            if (colour.Children[1] is Button { Flyout: Flyout { Content: ColorPicker picker } flyout })
            {
                flyout.Closed += (_, _) => ViewModel.RecolorEventLabel(id, Palette.HexFromColor(picker.Color));
            }
            colour.Children.RemoveAt(0);

            var name = new TextBox { Text = label.Name, MinWidth = 200 };
            AutomationProperties.SetName(name, $"Label name, {label.Name}");
            void Commit()
            {
                if (name.Text != label.Name)
                {
                    labelError = ViewModel.RenameEventLabel(id, name.Text)?.Message();
                    if (labelError is not null)
                    {
                        Rerender("labels");
                    }
                }
            }
            name.LostFocus += (_, _) => Commit();
            name.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    Commit();
                }
            };
            var uses = ViewModel.LabelUsageCount(id);
            var delete = new Button { Content = "Delete" };
            AutomationProperties.SetName(delete, $"Delete {label.Name}");
            delete.Click += (_, _) => _ = ViewModel.DeleteEventLabelAsync(id);
            body.Children.Add(Row(colour, name, Note(uses == 1 ? "1 event" : $"{uses} events"), delete));
        }
        if (labelError is not null)
        {
            body.Children.Add(new TextBlock { Text = labelError, Foreground = Resource<Brush>("SystemFillColorCriticalBrush") });
        }
        var add = new Button { Content = "Add Label", IsEnabled = ViewModel.CanAddEventLabel };
        add.Click += (_, _) => ViewModel.AddEventLabel();
        body.Children.Add(add);
        body.Children.Add(Note("A label is a name and a colour — Important, Personal, Society, whatever you need. Put one on an event from the Actions menu beside it, and it colours that event in the list and on the calendar."));
    }

    // ------------------------------------------------------------------
    // The assistant

    private void BuildAssistant(StackPanel host)
    {
        var body = Card(host, "AI Assistant (optional)");
        var assistant = ViewModel.SettingsForm.Assistant;
        var enabled = new ToggleSwitch { Header = "Use an AI assistant", IsOn = assistant.IsEnabled };
        enabled.Toggled += (_, _) => ViewModel.UpdateSettings(form => form with { Assistant = form.Assistant with { IsEnabled = enabled.IsOn } });
        body.Children.Add(enabled);
        body.Children.Add(Note("Anything it proposes is reviewed by you before it is saved. It never changes an event on its own."));
        if (!assistant.IsEnabled)
        {
            return;
        }

        // Saved models.
        if (ViewModel.AssistantProfiles.Count == 0)
        {
            body.Children.Add(Note("No models saved yet. Add one below, or just fill in the address and model."));
        }
        foreach (var profile in ViewModel.AssistantProfiles)
        {
            var isActive = profile.Id == ViewModel.ActiveAssistantProfile?.Id;
            var select = new RadioButton { Content = profile.Name, IsChecked = isActive, GroupName = "assistant-profile" };
            ToolTipService.SetToolTip(select, profile.Subtitle);
            var id = profile.Id;
            select.Checked += (_, _) => _ = ViewModel.SelectAssistantProfileAsync(id);
            var rename = new Button { Content = "Rename…" };
            rename.Click += async (_, _) =>
            {
                if (await AskForName("Rename model", profile.Name) is { } name && ViewModel.RenameAssistantProfile(id, name) is { } error)
                {
                    await Dialogs.InformAsync(XamlRoot, "That name cannot be used", error.Message());
                }
            };
            var duplicate = new Button { Content = "Duplicate" };
            duplicate.Click += (_, _) => ViewModel.DuplicateAssistantProfile(id);
            var delete = new Button { Content = "Delete", IsEnabled = ViewModel.AssistantProfiles.Count > 1 };
            delete.Click += (_, _) => _ = ViewModel.DeleteAssistantProfileAsync(id);
            body.Children.Add(Row(select, rename, duplicate, delete));
        }

        var addModel = new DropDownButton { Content = "Add a Model…", IsEnabled = ViewModel.CanAddAssistantProfile };
        ToolTipService.SetToolTip(addModel, ViewModel.CanAddAssistantProfile ? "Start from a known service, or from nothing" : "That is as many models as this keeps");
        var addMenu = new MenuFlyout();
        foreach (var service in AssistantService.AllServices)
        {
            var item = new MenuFlyoutItem { Text = $"{service.Name} — {service.Note}" };
            item.Click += (_, _) => ViewModel.AddAssistantProfile(service);
            addMenu.Items.Add(item);
        }
        addMenu.Items.Add(new MenuFlyoutSeparator());
        var blank = new MenuFlyoutItem { Text = "Blank…" };
        blank.Click += (_, _) => ViewModel.AddBlankAssistantProfile();
        addMenu.Items.Add(blank);
        addModel.Flyout = addMenu;
        body.Children.Add(addModel);

        // Where it runs, said off the address actually configured.
        var runs = new RadioButtons { Header = "Runs" };
        foreach (var provider in new[] { AssistantProvider.Local, AssistantProvider.Cloud })
        {
            runs.Items.Add(new RadioButton { Content = provider.Title(), Tag = provider });
        }
        runs.SelectedIndex = assistant.Provider == AssistantProvider.Cloud ? 1 : 0;
        runs.SelectionChanged += (_, _) =>
        {
            if (runs.SelectedItem is RadioButton { Tag: AssistantProvider chosen } && chosen != ViewModel.SettingsForm.Assistant.Provider)
            {
                ViewModel.UpdateSettings(form => form with { Assistant = AssistantSettings.Applying(chosen, form.Assistant) });
            }
        };
        body.Children.Add(runs);
        body.Children.Add(new TextBlock
        {
            Text = assistant.PrivacySummary,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Resource<Brush>(assistant.StaysOnThisMachine ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush"),
        });
        body.Children.Add(Note(assistant.Provider.QualityNote()));

        var address = new TextBox { Header = "Address", Text = assistant.BaseUrl };
        AutomationProperties.SetName(address, "Assistant address");
        address.LostFocus += (_, _) =>
        {
            if (address.Text != ViewModel.SettingsForm.Assistant.BaseUrl)
            {
                ViewModel.UpdateSettings(form => form with { Assistant = form.Assistant with { BaseUrl = address.Text.Trim() } });
            }
        };
        var model = new TextBox { Header = "Model", Text = assistant.Model };
        AutomationProperties.SetName(model, "Assistant model");
        model.LostFocus += (_, _) =>
        {
            if (model.Text != ViewModel.SettingsForm.Assistant.Model)
            {
                ViewModel.UpdateSettings(form => form with { Assistant = form.Assistant with { Model = model.Text.Trim() } });
            }
        };
        body.Children.Add(address);
        body.Children.Add(model);

        body.Children.Add(Note("Works with any service that speaks the common chat-completions format:"));
        var services = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var service in AssistantService.AllServices)
        {
            var link = new HyperlinkButton { Content = service.Name, Padding = new Thickness(4, 2, 4, 2) };
            ToolTipService.SetToolTip(link, $"{service.Note} {service.BaseUrl}");
            link.Click += (_, _) => ViewModel.UseAssistantService(service);
            services.Children.Add(link);
        }
        body.Children.Add(Wrap(services));
        if (assistant.KeysUrl is { } keys)
        {
            var link = new HyperlinkButton { Content = "Where to get a key", NavigateUri = new Uri(keys) };
            body.Children.Add(link);
        }

        if (assistant.StaysOnThisMachine)
        {
            BuildLocalModels(body, assistant);
        }

        if (assistant.Provider.RequiresApiKey())
        {
            var key = new PasswordBox { Header = "API key", Password = ViewModel.AssistantApiKey, PasswordRevealMode = PasswordRevealMode.Peek };
            AutomationProperties.SetName(key, "Assistant API key");
            var saveKey = new Button { Content = "Save Key" };
            saveKey.Click += (_, _) => _ = ViewModel.SaveAssistantKeyAsync(key.Password);
            body.Children.Add(Row(key, saveKey));
            body.Children.Add(Note("Each model keeps its own key in Windows Credential Manager, never in preferences or diagnostics."));
        }
    }

    private void BuildLocalModels(StackPanel body, AssistantSettings assistant)
    {
        if (!ViewModel.IsOllamaReachable)
        {
            body.Children.Add(new TextBlock { Text = "Ollama is not responding on this PC.", Foreground = Resource<Brush>("SystemFillColorCautionBrush") });
            body.Children.Add(Note(OllamaSetup.Instructions));
            foreach (var command in OllamaSetup.SetupCommands)
            {
                var copy = new Button { Content = "Copy" };
                AutomationProperties.SetName(copy, $"Copy {command}");
                copy.Click += (_, _) =>
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(command);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                };
                body.Children.Add(Row(new TextBlock { Text = command, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true }, copy));
            }
            var download = new HyperlinkButton { Content = "Download Ollama", NavigateUri = new Uri(OllamaSetup.DownloadPage) };
            var check = new Button { Content = "Check Again" };
            check.Click += (_, _) => _ = ViewModel.RefreshLocalModelsAsync();
            body.Children.Add(Row(download, check));
        }
        else
        {
            if (ViewModel.InstalledModels.Count == 0)
            {
                body.Children.Add(Note("No models are installed yet. Download one below."));
            }
            foreach (var installed in ViewModel.InstalledModels)
            {
                var name = installed.Name;
                var row = Row(new TextBlock
                {
                    Text = $"{(name == assistant.Model ? "● " : string.Empty)}{name} — {installed.SizeDescription(CultureInfo.CurrentCulture)}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 300,
                });
                if (name != assistant.Model)
                {
                    var use = new Button { Content = "Use" };
                    use.Click += (_, _) => ViewModel.UseModel(name);
                    row.Children.Add(use);
                }
                var delete = new Button { Content = "Delete" };
                AutomationProperties.SetName(delete, $"Delete {name}");
                delete.Click += (_, _) => _ = ViewModel.DeleteModelAsync(name);
                row.Children.Add(delete);
                body.Children.Add(row);
            }
            if (ViewModel.PullingModel is { } pulling)
            {
                var cancel = new Button { Content = "Cancel" };
                cancel.Click += (_, _) => ViewModel.CancelModelDownload();
                body.Children.Add(Row(new TextBlock { Text = $"Downloading {pulling}", VerticalAlignment = VerticalAlignment.Center }, cancel));
                body.Children.Add(new ProgressBar { Value = ViewModel.PullProgress * 100, Maximum = 100 });
            }
            else
            {
                var download = new DropDownButton { Content = "Download a Model…" };
                var menu = new MenuFlyout();
                foreach (var suggestion in SuggestedModel.All)
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = $"{suggestion.Name} — {suggestion.ApproximateSize}. {suggestion.Note}",
                        IsEnabled = ViewModel.InstalledModels.All(model => model.Name != suggestion.Name),
                    };
                    item.Click += (_, _) => ViewModel.DownloadModel(suggestion.Name);
                    menu.Items.Add(item);
                }
                download.Flyout = menu;
                body.Children.Add(download);
            }
        }
        if (ViewModel.ModelMessage is { } message)
        {
            body.Children.Add(Note(message));
        }
    }

    // ------------------------------------------------------------------
    // System, updates, data

    private void BuildSystem(StackPanel host)
    {
        var body = Card(host, "System");
        var login = new ToggleSwitch { Header = "Launch Canvas Countdown when you sign in", IsOn = ViewModel.SettingsForm.LaunchAtLogin };
        login.Toggled += (_, _) => ViewModel.UpdateSettings(form => form with { LaunchAtLogin = login.IsOn });
        body.Children.Add(login);
        body.Children.Add(Note("It starts in the notification area, without opening the window. Windows lists it under Task Manager → Startup apps, where it can also be turned off."));
        var keep = new ToggleSwitch { Header = "Keep running in the notification area when the window is closed", IsOn = ViewModel.SettingsForm.KeepsRunningInNotificationArea };
        keep.Toggled += (_, _) => ViewModel.UpdateSettings(form => form with { KeepsRunningInNotificationArea = keep.IsOn });
        body.Children.Add(keep);
        body.Children.Add(Note("The countdown and the automatic refresh carry on while it runs there. Reminders arrive either way. Quit from the icon's right-click menu."));
    }

    private void BuildUpdates(StackPanel host)
    {
        var body = Card(host, "Updates");
        body.Children.Add(new TextBlock { Text = $"Version {ViewModel.RunningVersionDescription}" });
        var check = new Button { Content = "Check for Updates", IsEnabled = !ViewModel.IsCheckingForUpdate };
        AutomationProperties.SetAutomationId(check, "CheckForUpdates");
        check.Click += (_, _) => _ = ViewModel.CheckForUpdateManuallyAsync();
        var row = Row(check);
        if (ViewModel.IsCheckingForUpdate)
        {
            row.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        }
        if (ViewModel.AvailableUpdate is { } release)
        {
            row.Children.Add(Note($"{release.Version} is available"));
        }
        else if (ViewModel.UpdateCheckAnnouncement is { } announcement)
        {
            row.Children.Add(Note(announcement));
        }
        body.Children.Add(row);
        body.Children.Add(Note("Canvas Countdown checks once a day and tells you in the window. Download and Install fetches the new version, checks it is exactly the file that was published, installs it and reopens the app. If anything goes wrong part-way, the version you have now is put back."));
    }

    private void BuildData(StackPanel host)
    {
        var body = Card(host, "Data & Privacy");
        var export = new Button { Content = "Export Redacted Diagnostics…" };
        export.Click += async (_, _) => await ExportDiagnosticsAsync();
        var reset = new Button { Content = "Reset Local Data…" };
        reset.Click += async (_, _) =>
        {
            if (await Dialogs.ConfirmAsync(XamlRoot, "Reset local data?", "This permanently deletes all imported and manual assignments, local settings, and the saved feed URL.", "Reset") == ContentDialogResult.Primary)
            {
                await Run(ViewModel.ResetLocalDataAsync);
            }
        };
        body.Children.Add(Row(export, reset));
        body.Children.Add(Note("Diagnostics include app state and counts, never the private Canvas feed URL or event descriptions."));
        body.Children.Add(Note($"Everything is kept in {AppEnvironment.DataDirectory}, and the feed URL and AI keys in Credential Manager."));
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            var text = await ViewModel.ExportDiagnosticsAsync();
            var picker = new FileSavePicker { SuggestedFileName = "Canvas-Countdown-Diagnostics" };
            picker.FileTypeChoices.Add("Text", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppServices.Current.WindowHandle);
            if (await picker.PickSaveFileAsync() is { } file)
            {
                await Windows.Storage.FileIO.WriteTextAsync(file, DiagnosticRedactor.Redact(text));
                ViewModel.ShowTransientStatus("Diagnostics exported");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            await Dialogs.InformAsync(XamlRoot, "Diagnostics could not be exported", exception.Message);
        }
    }

    // ------------------------------------------------------------------
    // Pieces

    private async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dialogs.InformAsync(XamlRoot, "Canvas Countdown couldn’t complete that action", exception.Message);
        }
    }

    private async Task<string?> AskForName(string title, string current)
    {
        var box = new TextBox { Text = current, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    }

    private static StackPanel Card(StackPanel host, string title)
    {
        var heading = new TextBlock { Text = title, Style = Resource<Style>("SubtitleTextBlockStyle"), Margin = new Thickness(0, 8, 0, 4) };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        host.Children.Add(heading);
        var body = new StackPanel { Spacing = 10 };
        host.Children.Add(new Border
        {
            Child = body,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"),
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
        });
        return body;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }
        return row;
    }

    /// <summary>Lets a row of buttons scroll sideways rather than run off the edge of a narrow window.</summary>
    private static ScrollViewer Wrap(UIElement row)
        => new() { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = Resource<Style>("CaptionTextBlockStyle"),
        Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void Launch(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    private static T Resource<T>(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is T typed ? typed : default!;
}

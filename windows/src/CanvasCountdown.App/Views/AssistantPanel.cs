using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CanvasCountdown.App.Views;

/// <summary>
/// The assistant: one box that asks a question, drafts tasks from a sentence,
/// or changes the drafts on screen — and says which before anything is sent.
/// </summary>
/// <remarks>
/// Built in code rather than XAML so that the box being typed in is never
/// rebuilt under the cursor: the conversation and the drafts are redrawn only
/// when they themselves change, and edits made here are not echoed back.
/// </remarks>
public sealed partial class AssistantPanel : UserControl
{
    private readonly TextBlock privacyLabel = new() { Style = Resource<Style>("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox modelPicker = new() { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel contextChip = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel offNotice = new() { Spacing = 8, Padding = new Thickness(16) };
    private readonly StackPanel conversation = new() { Spacing = 10 };
    private readonly ScrollViewer conversationScroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel drafts = new() { Spacing = 8 };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly ComboBox mode = new() { MinWidth = 120 };
    private readonly TextBox composer = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
    private readonly TextBlock composerHint = new() { Style = Resource<Style>("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly Button send = new() { Content = "Send", Style = Resource<Style>("AccentButtonStyle") };
    private bool showsEarlier;
    private string conversationKey = string.Empty;
    private IReadOnlyList<AssistantDraftTask> renderedDrafts = [];
    private string draftsKey = string.Empty;
    private bool isUpdatingControls;

    public AssistantPanel()
    {
        Content = Build();
        ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => Render();
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    private UIElement Build()
    {
        var root = new Grid { Padding = new Thickness(14, 10, 14, 12), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: name, where the request goes, which model, close.
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "Assistant", Style = Resource<Style>("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        header.Children.Add(title);
        Grid.SetColumn(privacyLabel, 1);
        AutomationProperties.SetAutomationId(privacyLabel, "AssistantPrivacy");
        header.Children.Add(privacyLabel);
        Grid.SetColumn(modelPicker, 2);
        AutomationProperties.SetName(modelPicker, "AI model");
        modelPicker.SelectionChanged += OnModelChosen;
        header.Children.Add(modelPicker);
        var options = new Button { Content = "More" };
        var optionsMenu = new MenuFlyout();
        var clear = new MenuFlyoutItem { Text = "Clear Conversation…" };
        clear.Click += (_, _) => _ = ConfirmClearAsync();
        var close = new MenuFlyoutItem { Text = "Close Assistant" };
        close.Click += (_, _) => ViewModel.CloseAssistant();
        optionsMenu.Items.Add(clear);
        optionsMenu.Items.Add(close);
        options.Flyout = optionsMenu;
        AutomationProperties.SetName(options, "Assistant options");
        Grid.SetColumn(options, 3);
        header.Children.Add(options);
        root.Children.Add(header);

        Grid.SetRow(contextChip, 1);
        root.Children.Add(contextChip);

        var middle = new StackPanel { Spacing = 12 };
        middle.Children.Add(offNotice);
        middle.Children.Add(conversation);
        middle.Children.Add(drafts);
        conversationScroller.Content = middle;
        AutomationProperties.SetAutomationId(conversation, "AssistantConversation");
        Grid.SetRow(conversationScroller, 2);
        root.Children.Add(conversationScroller);

        // The one box.
        var bottom = new StackPanel { Spacing = 6 };
        error.CloseButtonClick += (_, _) => error.IsOpen = false;
        bottom.Children.Add(error);
        foreach (var choice in new[] { AssistantComposerMode.Automatic, AssistantComposerMode.Ask, AssistantComposerMode.AddTask })
        {
            mode.Items.Add(new ComboBoxItem { Content = choice.Title(), Tag = choice });
        }
        ToolTipService.SetToolTip(mode, "Whether this box asks a question or writes a task");
        mode.SelectionChanged += (_, _) =>
        {
            if (!isUpdatingControls && mode.SelectedItem is ComboBoxItem { Tag: AssistantComposerMode chosen })
            {
                ViewModel.AssistantComposerMode = chosen;
            }
        };
        AutomationProperties.SetAutomationId(composer, "AssistantComposer");
        composer.TextChanged += (_, _) =>
        {
            if (!isUpdatingControls)
            {
                ViewModel.AssistantComposerInput = composer.Text;
            }
        };
        composer.KeyDown += OnComposerKeyDown;
        AutomationProperties.SetAutomationId(send, "AssistantSend");
        send.Click += (_, _) => _ = ViewModel.SendComposerAsync();

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(mode);
        Grid.SetColumn(composer, 1);
        row.Children.Add(composer);
        Grid.SetColumn(send, 2);
        row.Children.Add(send);
        bottom.Children.Add(row);
        bottom.Children.Add(composerHint);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        // When it is off, say so and say where it is switched on.
        offNotice.Children.Add(new TextBlock { Text = "The assistant is off", Style = Resource<Style>("BodyStrongTextBlockStyle") });
        offNotice.Children.Add(new TextBlock
        {
            Text = "Turn it on in Settings. It can run on this PC, in which case nothing is uploaded.",
            TextWrapping = TextWrapping.Wrap,
        });
        var openSettings = new Button { Content = "Open Settings" };
        openSettings.Click += (_, _) => ViewModel.SidebarSelection = SidebarDestination.Settings;
        offNotice.Children.Add(openSettings);
        return root;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        var viewModel = ViewModel;
        if (!viewModel.IsAssistantOpen)
        {
            return;
        }
        isUpdatingControls = true;
        try
        {
            privacyLabel.Text = viewModel.AssistantPrivacyLabel;
            privacyLabel.Foreground = Resource<Brush>(viewModel.AssistantStaysOnThisPc ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
            ToolTipService.SetToolTip(privacyLabel, viewModel.AssistantSettings.PrivacySummary);

            RenderModels(viewModel);
            RenderContext(viewModel);

            offNotice.Visibility = viewModel.IsAssistantEnabled ? Visibility.Collapsed : Visibility.Visible;
            composer.IsEnabled = viewModel.IsAssistantEnabled;
            send.IsEnabled = viewModel.CanSendComposer && viewModel.IsAssistantEnabled;

            if (composer.Text != viewModel.AssistantComposerInput)
            {
                composer.Text = viewModel.AssistantComposerInput;
                composer.SelectionStart = composer.Text.Length;
            }
            mode.SelectedItem = mode.Items.OfType<ComboBoxItem>().First(item => (AssistantComposerMode)item.Tag == viewModel.AssistantComposerMode);
            var role = viewModel.AssistantComposerRole;
            composer.PlaceholderText = AssistantComposer.Placeholder(viewModel.AssistantComposerMode, role);
            AutomationProperties.SetName(composer, AssistantComposer.FieldDescription(role));
            composerHint.Text = AssistantComposer.Hint(viewModel.AssistantComposerMode, role, viewModel.AssistantComposerInput, viewModel.AssistantDrafts.Count) ?? string.Empty;
            ToolTipService.SetToolTip(send, AssistantComposer.SendDescription(role));
            AutomationProperties.SetName(send, AssistantComposer.SendDescription(role));

            error.Message = viewModel.AssistantErrorMessage ?? string.Empty;
            error.IsOpen = viewModel.AssistantErrorMessage is not null;

            RenderConversation(viewModel);
            RenderDrafts(viewModel);
        }
        finally
        {
            isUpdatingControls = false;
        }
    }

    private void RenderModels(MainViewModel viewModel)
    {
        modelPicker.Visibility = viewModel.ShowsAssistantModelPicker ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(modelPicker, viewModel.AssistantModelDescription);
        var profiles = viewModel.AssistantProfiles;
        if (modelPicker.Items.Count != profiles.Count || modelPicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag).Cast<Guid>().SequenceEqual(profiles.Select(profile => profile.Id)) == false)
        {
            modelPicker.Items.Clear();
            foreach (var profile in profiles)
            {
                modelPicker.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
            }
        }
        modelPicker.SelectedItem = modelPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (Guid)item.Tag == viewModel.ActiveAssistantProfile?.Id);
    }

    private void OnModelChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!isUpdatingControls && modelPicker.SelectedItem is ComboBoxItem { Tag: Guid id })
        {
            _ = ViewModel.SelectAssistantProfileAsync(id);
        }
    }

    private void RenderContext(MainViewModel viewModel)
    {
        contextChip.Children.Clear();
        if (viewModel.AssistantContext is not { } context)
        {
            contextChip.Visibility = Visibility.Collapsed;
            return;
        }
        contextChip.Visibility = Visibility.Visible;
        contextChip.Children.Add(new TextBlock { Text = "Asking about:", VerticalAlignment = VerticalAlignment.Center, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") });
        contextChip.Children.Add(new TextBlock { Text = context.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        var remove = new HyperlinkButton { Content = "Remove" };
        AutomationProperties.SetName(remove, "Remove assignment context");
        remove.Click += (_, _) => viewModel.ClearAssistantContext();
        contextChip.Children.Add(remove);
    }

    private void RenderConversation(MainViewModel viewModel)
    {
        var messages = showsEarlier ? viewModel.Conversation : viewModel.RecentConversation;
        var key = $"{viewModel.Conversation.Count}|{showsEarlier}|{viewModel.IsAssistantBusy}|{viewModel.AssistantSummary}|{viewModel.IsAssistantEnabled}";
        if (key == conversationKey)
        {
            return;
        }
        conversationKey = key;
        conversation.Children.Clear();
        if (!viewModel.IsAssistantEnabled)
        {
            return;
        }

        if (viewModel.Conversation.Count == 0 && !viewModel.IsAssistantBusy)
        {
            conversation.Children.Add(new TextBlock { Text = "Ask about your deadlines", Style = Resource<Style>("BodyStrongTextBlockStyle") });
            conversation.Children.Add(new TextBlock
            {
                Text = "Counts and dates come from your own list, not from the model.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            });
            foreach (var suggestion in MainViewModel.AssistantSuggestions)
            {
                var button = new Button { Content = suggestion, HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) =>
                {
                    viewModel.AssistantComposerMode = AssistantComposerMode.Ask;
                    _ = viewModel.AskAsync(suggestion);
                };
                conversation.Children.Add(button);
            }
            return;
        }

        if (viewModel.EarlierConversationCount > 0)
        {
            var toggle = new HyperlinkButton { Content = showsEarlier ? "Show recent only" : $"Show {viewModel.EarlierConversationCount} earlier" };
            toggle.Click += (_, _) =>
            {
                showsEarlier = !showsEarlier;
                conversationKey = string.Empty;
                RenderConversation(ViewModel);
            };
            conversation.Children.Add(toggle);
        }

        var calendar = AppCalendar.Current;
        var culture = CultureInfo.CurrentCulture;
        foreach (var message in messages)
        {
            var isUser = message.Role == AssistantMessageRole.User;
            var bubble = new StackPanel { Spacing = 2 };
            bubble.Children.Add(new TextBlock
            {
                Text = $"{(isUser ? "You" : "Assistant")} · {message.TimeText(calendar, culture)}",
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            });
            bubble.Children.Add(new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            conversation.Children.Add(new Border
            {
                Child = bubble,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = Resource<Brush>(isUser ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"),
            });
        }
        if (viewModel.AssistantSummary is { } summary)
        {
            conversation.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        if (viewModel.IsAssistantBusy)
        {
            conversation.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new ProgressRing { IsActive = true, Width = 16, Height = 16 }, new TextBlock { Text = "Thinking…" } },
            });
        }
        conversationScroller.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    private void RenderDrafts(MainViewModel viewModel)
    {
        var key = $"{viewModel.IsConfirmingAssistantSave}|{viewModel.EventLabels.Count}|{viewModel.AssistantDraftHistory.Count}";
        if (key == draftsKey && viewModel.AssistantDrafts.SequenceEqual(renderedDrafts))
        {
            return;
        }
        draftsKey = key;
        renderedDrafts = viewModel.AssistantDrafts.ToArray();
        drafts.Children.Clear();
        if (renderedDrafts.Count == 0)
        {
            return;
        }

        drafts.Children.Add(new TextBlock { Text = "Check these before saving", Style = Resource<Style>("BodyStrongTextBlockStyle") });
        if (viewModel.AssistantDraftHistory.Count > 0)
        {
            drafts.Children.Add(new TextBlock
            {
                Text = "Asked so far: " + string.Join(" → ", viewModel.AssistantDraftHistory.Select(line => $"“{line}”")),
                TextWrapping = TextWrapping.Wrap,
                Style = Resource<Style>("CaptionTextBlockStyle"),
            });
        }
        foreach (var draft in renderedDrafts)
        {
            drafts.Children.Add(DraftRow(draft));
        }

        if (viewModel.IsConfirmingAssistantSave)
        {
            var summary = viewModel.AssistantSaveSummary;
            var confirm = new StackPanel { Spacing = 6, Padding = new Thickness(10), Background = Resource<Brush>("CardBackgroundFillColorSecondaryBrush") };
            confirm.Children.Add(new TextBlock { Text = summary.Title, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
            foreach (var line in summary.DetailLines(AppCalendar.Current, CultureInfo.CurrentCulture))
            {
                confirm.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
            }
            var add = new Button { Content = summary.ConfirmTitle, Style = Resource<Style>("AccentButtonStyle"), IsEnabled = summary.CanSave };
            add.Click += (_, _) => _ = viewModel.SaveAssistantDraftsAsync(viewModel.AssistantDrafts);
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => viewModel.CancelSaveAssistantDrafts();
            confirm.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, cancel } });
            drafts.Children.Add(confirm);
        }
        else
        {
            var discard = new Button { Content = "Discard" };
            ToolTipService.SetToolTip(discard, "Throw these away and start from a new sentence");
            discard.Click += (_, _) => viewModel.ClearAssistantDrafts();
            var save = new Button { Content = "Save Selected…", Style = Resource<Style>("AccentButtonStyle"), IsEnabled = viewModel.HasSaveableDrafts };
            ToolTipService.SetToolTip(save, "Check what will be added, then add it");
            save.Click += (_, _) => viewModel.RequestSaveAssistantDrafts();
            drafts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { discard, save } });
        }
    }

    private Border DraftRow(AssistantDraftTask draft)
    {
        var calendar = AppCalendar.Current;
        var panel = new StackPanel { Spacing = 6 };

        var top = new Grid { ColumnSpacing = 6 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var include = new CheckBox { IsChecked = draft.Include, MinWidth = 0 };
        AutomationProperties.SetName(include, $"Include {draft.Title}");
        include.Click += (_, _) => Edit(draft, current => current with { Include = include.IsChecked == true });
        top.Children.Add(include);
        var title = new TextBox { Text = draft.Title, PlaceholderText = "Task name" };
        AutomationProperties.SetName(title, "Task name");
        title.LostFocus += (_, _) => Edit(draft, current => current with { Title = title.Text });
        Grid.SetColumn(title, 1);
        top.Children.Add(title);
        var remove = new Button { Content = "Remove" };
        ToolTipService.SetToolTip(remove, "Remove this draft");
        AutomationProperties.SetName(remove, $"Remove {draft.Title}");
        remove.Click += (_, _) => ViewModel.RemoveAssistantDraft(draft.Id);
        Grid.SetColumn(remove, 2);
        top.Children.Add(remove);
        panel.Children.Add(top);

        var course = new TextBox { Text = draft.CourseName ?? string.Empty, PlaceholderText = "Course (optional)" };
        AutomationProperties.SetName(course, $"Course for {draft.Title}");
        course.LostFocus += (_, _) => Edit(draft, current => current.WithCourseText(course.Text));
        panel.Children.Add(course);

        var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (draft.DueDate is { } due)
        {
            var local = calendar.ToLocal(due);
            var date = new CalendarDatePicker { Date = new DateTimeOffset(local.Date, TimeSpan.Zero) };
            AutomationProperties.SetName(date, "Due date");
            date.DateChanged += (_, args) =>
            {
                if (args.NewDate is { } picked)
                {
                    Edit(draft, current => current with
                    {
                        DueDate = calendar.FromLocal(DateOnly.FromDateTime(picked.Date), calendar.TimeOf(current.DueDate ?? due)),
                    });
                }
            };
            var time = new TimePicker { Time = local.TimeOfDay, MinuteIncrement = 1 };
            AutomationProperties.SetName(time, "Due time");
            time.SelectedTimeChanged += (_, args) =>
            {
                if (args.NewTime is { } picked)
                {
                    Edit(draft, current => current with
                    {
                        DueDate = calendar.FromLocal(calendar.DateOf(current.DueDate ?? due), TimeOnly.FromTimeSpan(picked)),
                    });
                }
            };
            when.Children.Add(date);
            when.Children.Add(time);
        }
        else
        {
            when.Children.Add(new TextBlock
            {
                Text = "No date was given",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Resource<Brush>("SystemFillColorCautionBrush"),
            });
            var set = new Button { Content = "Set a date" };
            set.Click += (_, _) =>
            {
                var now = DateTimeOffset.UtcNow;
                Edit(draft, current => current with { DueDate = calendar.SettingTime(calendar.AddDays(now, 1), 17, 0) }, rebuild: true);
            };
            when.Children.Add(set);
        }
        panel.Children.Add(when);

        var labels = ViewModel.EventLabels;
        var labelButton = new DropDownButton
        {
            Content = labels.FirstOrDefault(label => label.Id == draft.LabelId)?.Name ?? "Label",
            IsEnabled = labels.Count > 0,
        };
        ToolTipService.SetToolTip(labelButton, labels.Count == 0 ? "Add a label in Settings first" : "Put one of your labels on this task");
        AutomationProperties.SetName(labelButton, $"Label for {draft.Title}");
        var labelMenu = new MenuFlyout();
        var none = new RadioMenuFlyoutItem { Text = "None", GroupName = "draft-label", IsChecked = draft.LabelId is null };
        none.Click += (_, _) => Edit(draft, current => current with { LabelId = null }, rebuild: true);
        labelMenu.Items.Add(none);
        foreach (var label in labels)
        {
            var id = label.Id;
            var item = new RadioMenuFlyoutItem { Text = label.Name, GroupName = "draft-label", IsChecked = draft.LabelId == id };
            item.Click += (_, _) => Edit(draft, current => current with { LabelId = id }, rebuild: true);
            labelMenu.Items.Add(item);
        }
        labelButton.Flyout = labelMenu;
        panel.Children.Add(labelButton);

        return new Border
        {
            Child = panel,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"),
        };
    }

    /// <summary>
    /// Writes an edit to the view model without redrawing the row it came from,
    /// unless the row has to change shape (a date set, a label chosen).
    /// </summary>
    private void Edit(AssistantDraftTask original, Func<AssistantDraftTask, AssistantDraftTask> change, bool rebuild = false)
    {
        var current = ViewModel.AssistantDrafts.FirstOrDefault(draft => draft.Id == original.Id);
        if (current is null)
        {
            return;
        }
        var edited = change(current);
        if (edited == current)
        {
            return;
        }
        if (!rebuild)
        {
            renderedDrafts = renderedDrafts.Select(draft => draft.Id == edited.Id ? edited : draft).ToArray();
        }
        ViewModel.UpdateAssistantDraft(edited);
    }

    private void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _ = ViewModel.SendComposerAsync();
            e.Handled = true;
        }
    }

    private async Task ConfirmClearAsync()
    {
        ViewModel.RequestClearConversation();
        if (!ViewModel.IsConfirmingClearConversation)
        {
            return;
        }
        var result = await Infrastructure.Dialogs.ConfirmAsync(
            XamlRoot,
            "Clear this conversation?",
            "Everything asked and answered here is deleted. Your assignments are not touched.",
            "Clear Conversation",
            "Keep");
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ClearConversation();
        }
        else
        {
            ViewModel.IsConfirmingClearConversation = false;
        }
    }

    private static T Resource<T>(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is T typed ? typed : default!;
}

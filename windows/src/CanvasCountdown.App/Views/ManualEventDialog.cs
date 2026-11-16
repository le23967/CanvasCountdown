using CanvasCountdown.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CanvasCountdown.App.Views;

/// <summary>
/// Add Manual Event / Edit Event. Built in code: it is a short form, and the
/// dialog has to stay open with the error showing when a save fails, which a
/// plain ContentDialog only does if its primary button is handled by hand.
/// </summary>
internal static class ManualEventDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel viewModel, ManualEventDraft draft)
    {
        var calendar = AppCalendar.Current;
        var local = calendar.ToLocal(draft.DueDate);
        var isNew = draft.EventId is null;

        var title = new TextBox
        {
            Header = "Assignment name",
            Text = draft.Title,
            PlaceholderText = "Essay draft",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(title, "ManualTitle");

        var course = new AutoSuggestBox
        {
            Header = "Course (optional)",
            Text = draft.CourseName,
            PlaceholderText = "COMP2050",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(course, "Course name, optional");
        course.TextChanged += (box, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }
            var typed = box.Text.Trim();
            box.ItemsSource = typed.Length == 0
                ? null
                : viewModel.AvailableCourses.Where(name => name.Contains(typed, StringComparison.CurrentCultureIgnoreCase)).Take(8).ToArray();
        };
        course.SuggestionChosen += (box, args) => box.Text = args.SelectedItem as string ?? box.Text;

        var date = new CalendarDatePicker
        {
            Header = "Due date",
            Date = PickerDates.ToPicker(DateOnly.FromDateTime(local)),
            IsTodayHighlighted = true,
        };
        var time = new TimePicker
        {
            Header = "Time",
            Time = local.TimeOfDay,
            MinuteIncrement = 1,
        };

        var error = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false,
        };

        var content = new StackPanel { Spacing = 14, MinWidth = 380 };
        content.Children.Add(title);
        content.Children.Add(course);
        var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        when.Children.Add(date);
        when.Children.Add(time);
        content.Children.Add(when);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = isNew ? "Add Manual Event" : "Edit Event",
            Content = content,
            PrimaryButtonText = isNew ? "Add Event" : "Save Changes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = draft.CanSave,
        };
        title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = title.Text.Trim().Length > 0;

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var day = PickerDates.FromPicker(date.Date ?? DateTimeOffset.Now);
                var edited = draft with
                {
                    Title = title.Text,
                    CourseName = course.Text,
                    DueDate = calendar.FromLocal(day, TimeOnly.FromTimeSpan(time.Time)),
                };
                if (!edited.CanSave)
                {
                    args.Cancel = true;
                    return;
                }
                sender.IsPrimaryButtonEnabled = false;
                await viewModel.SaveManualEventAsync(edited);
            }
            catch (Exception exception)
            {
                // The form stays open with what was typed, so nothing has to be
                // entered twice.
                error.Message = exception.Message;
                error.IsOpen = true;
                sender.IsPrimaryButtonEnabled = true;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await Dialogs.ShowAsync(dialog);
        viewModel.IsShowingManualEditor = false;
    }
}

/// <summary>
/// The details of an event Canvas owns. Only what is the user's to change —
/// completed and ignored — can be changed here.
/// </summary>
internal static class ImportedEventDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel viewModel)
    {
        if (viewModel.CurrentImportedEventDetails is not { } item)
        {
            viewModel.IsShowingImportedDetails = false;
            return;
        }
        var calendar = AppCalendar.Current;
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        var content = new StackPanel { Spacing = 10, MinWidth = 380 };
        content.Children.Add(Field("Assignment", item.Title));
        if (item.NormalizedCourseName is { } courseName)
        {
            content.Children.Add(Field("Course", courseName));
        }
        content.Children.Add(Field("Due", DateText.Long(item.DueDate, calendar, culture)));
        content.Children.Add(Field("Countdown", NextDeadlinePresentation.CountdownDescription(item.RemainingDays(viewModel.CurrentDate, calendar))));
        content.Children.Add(new TextBlock
        {
            Text = "From Canvas. These fields come from your Canvas calendar and are replaced by the next refresh.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var completed = new ToggleSwitch { Header = "Completed", IsOn = item.IsCompleted };
        var ignored = new ToggleSwitch { Header = "Ignored", IsOn = item.IsIgnored };
        completed.Toggled += async (_, _) =>
        {
            if (viewModel.CurrentImportedEventDetails is { } current && current.IsCompleted != completed.IsOn)
            {
                await viewModel.ToggleCompletedAsync(current);
            }
        };
        ignored.Toggled += async (_, _) =>
        {
            if (viewModel.CurrentImportedEventDetails is { } current && current.IsIgnored != ignored.IsOn)
            {
                await viewModel.ToggleIgnoredAsync(current);
            }
        };

        content.Children.Add(new TextBlock
        {
            Text = "Kept on this PC",
            Margin = new Thickness(0, 8, 0, 0),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        content.Children.Add(completed);
        content.Children.Add(ignored);
        content.Children.Add(new TextBlock
        {
            Text = "Completed and ignored are yours. They survive every Canvas refresh, and an ignored assignment never reaches the tray countdown or a reminder.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = item.Title,
            Content = new ScrollViewer { Content = content },
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
        };
        await Dialogs.ShowAsync(dialog);
        viewModel.IsShowingImportedDetails = false;
    }

    private static StackPanel Field(string label, string value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        return panel;
    }
}

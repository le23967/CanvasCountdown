using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CanvasCountdown.App.Infrastructure;

/// <summary>
/// The actions an event offers, as one menu: the Actions button on a row,
/// a right-click, the calendar and the search panel all show the same list.
/// </summary>
internal static class EventActions
{
    public static MenuFlyout Menu(MainViewModel viewModel, AssignmentListItem item, XamlRoot xamlRoot)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(Item(
            item.IsCompleted ? "Mark Incomplete" : "Mark Complete",
            item.IsCompleted ? "" : "",
            () => _ = viewModel.ToggleCompletedAsync(item)));

        if (viewModel.IsAssistantEnabled)
        {
            menu.Items.Add(Item("Ask the Assistant…", "", () => viewModel.OpenAssistant(item)));
        }

        menu.Items.Add(Item(
            item.IsIgnored ? "Stop Ignoring" : "Ignore",
            item.IsIgnored ? "" : "",
            () => _ = viewModel.ToggleIgnoredAsync(item)));

        // One list with None at the top: assigning and clearing are the same
        // action, so they are not two menu items.
        var labels = new MenuFlyoutSubItem
        {
            Text = "Label",
            Icon = new FontIcon { Glyph = "" },
            IsEnabled = viewModel.EventLabels.Count > 0,
        };
        labels.Items.Add(new RadioMenuFlyoutItem
        {
            Text = "None",
            GroupName = "label",
            IsChecked = item.LabelId is null,
        }.With(radio => radio.Click += (_, _) => _ = viewModel.SetLabelAsync(null, item)));
        labels.Items.Add(new MenuFlyoutSeparator());
        foreach (var label in viewModel.EventLabels)
        {
            var labelId = label.Id;
            labels.Items.Add(new RadioMenuFlyoutItem
            {
                Text = label.Name,
                GroupName = "label",
                IsChecked = item.LabelId == labelId,
            }.With(radio => radio.Click += (_, _) => _ = viewModel.SetLabelAsync(labelId, item)));
        }
        menu.Items.Add(labels);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(item.IsManual ? "Edit…" : "Details…", "", () => viewModel.PresentEditor(item)));
        if (item.IsManual)
        {
            menu.Items.Add(Item("Delete…", "", () => _ = ConfirmDeleteAsync(xamlRoot, viewModel, item)));
        }
        return menu;
    }

    /// <summary>Only manual events can be deleted; imported ones are Canvas's to remove.</summary>
    public static async Task ConfirmDeleteAsync(XamlRoot xamlRoot, MainViewModel viewModel, AssignmentListItem item)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Delete manual event?",
            Content = $"“{item.Title}” will be permanently removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await viewModel.DeleteManualEventAsync(item);
        }
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private static T With<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}

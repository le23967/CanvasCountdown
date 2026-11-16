using System.Globalization;
using CanvasCountdown.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CanvasCountdown.App.Views;

/// <summary>
/// Connect Canvas / Import Canvas calendar: paste the feed URL, preview what it
/// holds, choose, import. Nothing is saved before the preview has been seen.
/// </summary>
internal static class FeedImportDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel viewModel)
    {
        var calendar = AppCalendar.Current;
        var culture = CultureInfo.CurrentCulture;
        IReadOnlyList<ImportPreviewItem> preview = [];
        var selected = new HashSet<string>(StringComparer.Ordinal);
        Uri? previewedUrl = null;

        // The URL is a password in all but name: hidden unless asked for.
        var url = new PasswordBox
        {
            Header = "Calendar feed URL",
            Password = viewModel.FeedImportInitialUrl,
            PlaceholderText = "https://…/feeds/calendars/user_….ics",
            PasswordRevealMode = FeedUrlPresentationPolicy.IsRevealedByDefault ? PasswordRevealMode.Visible : PasswordRevealMode.Peek,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(url, "FeedUrl");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(url, "Canvas calendar feed URL");
        var reveal = new CheckBox { Content = "Show the URL" };
        reveal.Checked += (_, _) => url.PasswordRevealMode = PasswordRevealMode.Visible;
        reveal.Unchecked += (_, _) => url.PasswordRevealMode = PasswordRevealMode.Hidden;

        var instructions = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Find it in Canvas:\n1. Open Calendar in Canvas.\n2. Choose Calendar Feed at the bottom of the sidebar.\n3. Copy the iCal feed link and paste it above.\n\nYour private feed URL is stored in Windows Credential Manager and is never included in diagnostics.",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        var busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };

        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Events available to import");
        var selectAll = new Button { Content = "Select All" };
        var selectNone = new Button { Content = "Select None" };
        var selection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        selection.Children.Add(selectAll);
        selection.Children.Add(selectNone);

        var urlSection = new StackPanel { Spacing = 10 };
        urlSection.Children.Add(url);
        urlSection.Children.Add(reveal);
        urlSection.Children.Add(instructions);

        var content = new StackPanel { Spacing = 12, MinWidth = 460 };
        content.Children.Add(new TextBlock
        {
            Text = "Preview your deadlines before anything is saved.",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        content.Children.Add(urlSection);
        content.Children.Add(selection);
        content.Children.Add(list);
        content.Children.Add(busy);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = viewModel.FeedImportIsOnboarding ? "Connect your Canvas calendar" : "Import Canvas calendar",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Preview",
            SecondaryButtonText = string.Empty,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void ShowError(string? message)
        {
            error.Message = message ?? string.Empty;
            error.IsOpen = message is not null;
        }

        void RenderPreview()
        {
            list.Items.Clear();
            foreach (var item in preview)
            {
                var id = item.Id;
                var due = DateText.Short(item.DueDate, calendar, culture);
                var box = new CheckBox
                {
                    IsChecked = selected.Contains(id),
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap },
                            new TextBlock
                            {
                                Text = item.CourseName is { } course ? $"{course} · {due}" : due,
                                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                            },
                        },
                    },
                };
                box.Checked += (_, _) => { selected.Add(id); UpdateButtons(); };
                box.Unchecked += (_, _) => { selected.Remove(id); UpdateButtons(); };
                list.Items.Add(box);
            }
            var hasPreview = preview.Count > 0;
            list.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
            selection.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
            urlSection.Visibility = hasPreview ? Visibility.Collapsed : Visibility.Visible;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            if (preview.Count == 0)
            {
                dialog.PrimaryButtonText = "Preview";
                dialog.SecondaryButtonText = string.Empty;
                dialog.IsPrimaryButtonEnabled = true;
                return;
            }
            dialog.PrimaryButtonText = selected.Count == 1 ? "Import 1 Event" : $"Import {selected.Count} Events";
            dialog.IsPrimaryButtonEnabled = selected.Count > 0;
            dialog.SecondaryButtonText = "Change URL";
        }

        selectAll.Click += (_, _) =>
        {
            selected.UnionWith(preview.Select(item => item.Id));
            RenderPreview();
        };
        selectNone.Click += (_, _) =>
        {
            selected.Clear();
            RenderPreview();
        };

        dialog.SecondaryButtonClick += (_, args) =>
        {
            // Back to the address, keeping what was typed.
            args.Cancel = true;
            preview = [];
            selected.Clear();
            ShowError(null);
            RenderPreview();
        };

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            sender.IsPrimaryButtonEnabled = false;
            busy.Visibility = Visibility.Visible;
            ShowError(null);
            try
            {
                if (preview.Count == 0)
                {
                    var feedUrl = FeedUrlInput.Normalize(url.Password);
                    var items = await viewModel.PreviewFeedAsync(feedUrl);
                    preview = items.OrderBy(item => item.DueDate).ToArray();
                    previewedUrl = feedUrl;
                    selected.Clear();
                    selected.UnionWith(preview.Select(item => item.Id));
                    if (preview.Count == 0)
                    {
                        ShowError("No importable dated events were found in this feed.");
                    }
                    RenderPreview();
                }
                else if (previewedUrl is not null)
                {
                    await viewModel.ImportFeedAsync(previewedUrl, selected);
                    deferral.Complete();
                    deferral = null;
                    sender.Hide();
                    return;
                }
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
            finally
            {
                busy.Visibility = Visibility.Collapsed;
                UpdateButtons();
                deferral?.Complete();
            }
        };

        await Dialogs.ShowAsync(dialog);
        viewModel.IsShowingFeedImport = false;
    }
}

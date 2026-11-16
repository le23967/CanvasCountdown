using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CanvasCountdown.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace CanvasCountdown.App.Views;

/// <summary>
/// Import from Canvas Screenshot. Every row it finds is shown for review, and
/// nothing is saved until the user chooses which ones to import.
/// </summary>
/// <remarks>
/// The screenshots are held in memory for as long as this is open and dropped
/// when it closes; nothing about them is written anywhere.
/// </remarks>
internal static class ScreenshotImportDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel viewModel)
    {
        var import = AppServices.Current.ScreenshotImport;
        import.ExistingCourseNames = viewModel.AvailableCourses.ToValueList();

        var body = new Grid { MinWidth = 640 };
        var error = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = true, IsOpen = false };
        var layout = new StackPanel { Spacing = 12 };
        layout.Children.Add(new TextBlock
        {
            Text = ScreenshotImportViewModel.PrivacyNote,
            Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
        });
        layout.Children.Add(error);
        layout.Children.Add(body);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Import from Canvas Screenshot",
            Content = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 620 },
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 920.0;

        var renderedStage = (ScreenshotImportStageKind?)null;
        void Render()
        {
            error.Message = import.ErrorMessage ?? import.NoticeMessage ?? string.Empty;
            error.Severity = import.ErrorMessage is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            error.IsOpen = !string.IsNullOrEmpty(error.Message);

            var stage = import.Stage.Kind;
            dialog.PrimaryButtonText = stage switch
            {
                ScreenshotImportStageKind.Reviewing => import.ImportButtonTitle,
                ScreenshotImportStageKind.Finished => "Done",
                _ => string.Empty,
            };
            dialog.IsPrimaryButtonEnabled = stage == ScreenshotImportStageKind.Finished || import.CanImport;
            dialog.SecondaryButtonText = stage == ScreenshotImportStageKind.Reviewing ? "Back" : string.Empty;
            dialog.CloseButtonText = stage == ScreenshotImportStageKind.Finished ? string.Empty : "Cancel";

            // The review rows are built once per review and edited in place,
            // so a text box keeps its focus while the counts above it change.
            if (renderedStage == stage && stage != ScreenshotImportStageKind.Recognising)
            {
                if (stage == ScreenshotImportStageKind.Reviewing && body.Children.FirstOrDefault() is StackPanel review)
                {
                    UpdateSummary(review, import);
                }
                return;
            }
            renderedStage = stage;
            body.Children.Clear();
            body.Children.Add(stage switch
            {
                ScreenshotImportStageKind.Recognising => Recognising(import),
                ScreenshotImportStageKind.Reviewing => Reviewing(import),
                ScreenshotImportStageKind.Finished => new TextBlock
                {
                    Text = import.FinishedMessage ?? "Import complete",
                    Style = Resource<Style>("SubtitleTextBlockStyle"),
                    TextWrapping = TextWrapping.Wrap,
                },
                _ => Choosing(root, import),
            });
        }

        PropertyChangedEventHandler onChange = (_, _) => Render();
        import.PropertyChanged += onChange;
        Render();

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            if (import.IsFinished)
            {
                return;
            }
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                await import.ImportSelectedAsync();
            }
            finally
            {
                deferral.Complete();
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            import.Discard();
        };

        try
        {
            await Dialogs.ShowAsync(dialog);
        }
        finally
        {
            import.PropertyChanged -= onChange;
            var commit = import.IsFinished ? import.LastCommit : null;
            // Closing lets the screenshots go; nothing about them is kept.
            import.Discard();
            viewModel.IsShowingScreenshotImport = false;
            if (commit is not null)
            {
                await viewModel.ScreenshotImportDidFinishAsync(commit, AppServices.Current.UndoScreenshotImportAsync);
            }
        }
    }

    private static UIElement Choosing(XamlRoot root, ScreenshotImportViewModel import)
    {
        var drop = new Border
        {
            AllowDrop = true,
            MinHeight = 180,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"),
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 32 },
                    new TextBlock { Text = "Drop screenshots here", HorizontalAlignment = HorizontalAlignment.Center, Style = Resource<Style>("BodyStrongTextBlockStyle") },
                    new TextBlock { Text = "PNG, JPEG, HEIC or TIFF", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") },
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(drop, "Screenshot drop area. Drop PNG, JPEG, HEIC or TIFF files here.");
        drop.DragOver += (_, e) =>
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        };
        drop.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(file => file.Path).ToArray();
            if (paths.Length == 0)
            {
                import.ErrorMessage = "Those items are not image files.";
                return;
            }
            await import.LoadAsync(paths);
        };

        var choose = new Button { Content = "Choose Screenshots…", Style = Resource<Style>("AccentButtonStyle") };
        choose.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in ScreenshotImportViewModel.SupportedFileExtensions)
            {
                picker.FileTypeFilter.Add(extension.StartsWith('.') ? extension : "." + extension);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppServices.Current.WindowHandle);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0)
            {
                await import.LoadAsync(files.Select(file => file.Path).ToArray());
            }
        };

        var paste = new Button { Content = "Paste Screenshot" };
        paste.Click += async (_, _) => await import.PasteFromClipboardAsync(await ReadClipboardImageAsync());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(choose);
        buttons.Children.Add(paste);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(drop);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("CaptionTextBlockStyle"),
            Text = "Every row is shown for you to check before anything is saved. A year that was not visible is inferred and marked; a row without a visible due date is kept for you to fill in, never given an invented one; and \"Not available until\" lines are never read as deadlines. Check every date before you import it.",
        });
        return panel;
    }

    private static UIElement Recognising(ScreenshotImportViewModel import)
    {
        var cancel = new Button { Content = "Cancel Reading", HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => import.CancelRecognition();
        return new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(0, 24, 0, 24),
            Children =
            {
                new ProgressRing { IsActive = true, Width = 32, Height = 32 },
                new TextBlock { Text = import.ProgressDisplayText, HorizontalAlignment = HorizontalAlignment.Center },
                cancel,
            },
        };
    }

    private static StackPanel Reviewing(ScreenshotImportViewModel import)
    {
        var panel = new StackPanel { Spacing = 10 };
        var summary = new TextBlock { Tag = "summary", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(summary);

        var selectAll = new Button { Content = "Select All Ready" };
        selectAll.Click += (_, _) => import.SelectAllReady();
        var deselect = new Button { Content = "Deselect All" };
        deselect.Click += (_, _) => import.DeselectAll();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { selectAll, deselect } });

        var list = new StackPanel { Spacing = 8 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Detected assignments awaiting review");
        foreach (var row in import.Rows)
        {
            list.Children.Add(Row(row));
        }
        panel.Children.Add(list);
        UpdateSummary(panel, import);
        return panel;
    }

    private static void UpdateSummary(StackPanel review, ScreenshotImportViewModel import)
    {
        if (review.Children.FirstOrDefault() is TextBlock summary)
        {
            summary.Text = $"Ready {import.ReadyCount} · Needs review {import.NeedsReviewCount} · Invalid {import.InvalidCount} · Duplicates {import.DuplicateCount}";
        }
    }

    private static Border Row(ScreenshotImportRowViewModel row)
    {
        var include = new CheckBox { IsChecked = row.Include, IsEnabled = row.CanBeImported, MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(include, row.IncludeAccessibilityLabel);
        include.Click += (_, _) => row.Include = include.IsChecked == true;

        var title = new TextBox { Text = row.Title, PlaceholderText = "Assignment name", MinWidth = 260 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(title, "Assignment name");
        title.LostFocus += (_, _) => row.Title = title.Text;

        var course = new ComboBox { MinWidth = 160, IsEditable = true, PlaceholderText = "No Course" };
        course.Items.Add("No Course");
        foreach (var choice in row.CourseChoices)
        {
            course.Items.Add(choice);
        }
        course.Text = row.CourseName ?? string.Empty;
        course.SelectedItem = row.CourseName is null ? "No Course" : row.CourseName;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(course, row.CourseAccessibilityLabel);
        course.SelectionChanged += (_, _) =>
        {
            var chosen = course.SelectedItem as string;
            row.CourseName = chosen is null or "No Course" ? null : chosen;
        };
        course.TextSubmitted += (_, args) => row.CourseName = string.IsNullOrWhiteSpace(args.Text) ? null : args.Text.Trim();

        var applyCourse = new HyperlinkButton { Content = "Apply this course to all rows from this screenshot", Command = row.ApplyCourseToScreenshotCommand };

        var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void BuildWhen()
        {
            when.Children.Clear();
            if (row.HasDueDate && row.DueDay is { } day && row.DueTimeOfDay is { } time)
            {
                var date = new CalendarDatePicker { Date = PickerDates.ToPicker(day) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(date, "Due date");
                date.DateChanged += (_, args) =>
                {
                    if (args.NewDate is { } picked)
                    {
                        row.DueDay = PickerDates.FromPicker(picked);
                    }
                };
                var clock = new TimePicker { Time = time.ToTimeSpan(), MinuteIncrement = 1 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clock, "Due time");
                clock.SelectedTimeChanged += (_, args) =>
                {
                    if (args.NewTime is { } picked)
                    {
                        row.DueTimeOfDay = TimeOnly.FromTimeSpan(picked);
                    }
                };
                when.Children.Add(date);
                when.Children.Add(clock);
            }
            else
            {
                var set = new Button { Content = "Set a due date…", Command = row.SetDueDateCommand };
                when.Children.Add(set);
            }
        }
        BuildWhen();

        var status = new TextBlock { Style = Resource<Style>("CaptionTextBlockStyle") };
        var warnings = new TextBlock { Style = Resource<Style>("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Foreground = Resource<Brush>("SystemFillColorCautionBrush") };
        var recognised = new TextBlock { Style = Resource<Style>("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Foreground = Resource<Brush>("TextFillColorSecondaryBrush"), IsTextSelectionEnabled = true };

        var duplicate = new ComboBox { Header = "Already stored" };
        foreach (var choice in row.DuplicateChoices)
        {
            duplicate.Items.Add(new ComboBoxItem { Content = choice.Title(), Tag = choice });
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(duplicate, "Action for possible duplicate");
        duplicate.SelectionChanged += (_, _) =>
        {
            if (duplicate.SelectedItem is ComboBoxItem { Tag: DuplicateResolution resolution })
            {
                row.SelectedDuplicateAction = resolution;
            }
        };

        void Update()
        {
            include.IsChecked = row.Include;
            include.IsEnabled = row.CanBeImported;
            status.Text = row.ConfidenceText is { } confidence ? $"{row.StatusTitle} · {confidence}" : row.StatusTitle;
            warnings.Text = string.Join("\n", row.Warnings.Select(warning => warning.Message));
            warnings.Visibility = row.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            recognised.Text = row.RecognisedText ?? string.Empty;
            recognised.Visibility = row.RecognisedText is null ? Visibility.Collapsed : Visibility.Visible;
            duplicate.Visibility = row.IsPossibleDuplicate ? Visibility.Visible : Visibility.Collapsed;
            duplicate.SelectedItem = duplicate.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is DuplicateResolution value && value == row.SelectedDuplicateAction);
            if (title.FocusState == FocusState.Unfocused && title.Text != row.Title)
            {
                title.Text = row.Title;
            }
            if ((when.Children.Count == 1) == row.HasDueDate)
            {
                BuildWhen();
            }
        }
        Update();
        row.PropertyChanged += (_, _) => Update();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(include);
        top.Children.Add(title);
        top.Children.Add(course);

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(top);
        content.Children.Add(when);
        content.Children.Add(applyCourse);
        content.Children.Add(status);
        content.Children.Add(warnings);
        content.Children.Add(recognised);
        content.Children.Add(duplicate);

        return new Border
        {
            Child = content,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"),
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
        };
    }

    /// <summary>The image on the clipboard as PNG bytes, or null when there is none.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadClipboardImageAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                return null;
            }
            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static T Resource<T>(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is T typed ? typed : default!;
}

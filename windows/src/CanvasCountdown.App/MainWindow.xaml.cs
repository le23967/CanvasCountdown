using System.ComponentModel;
using System.Runtime.InteropServices;
using CanvasCountdown.App.Infrastructure;
using CanvasCountdown.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CanvasCountdown.App;

public sealed partial class MainWindow : Window
{
    private bool isSyncingNavigation;
    private bool isQuitting;
    private bool isShowingError;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Title = "Canvas Countdown";
        SizeToFitDisplay();

        ViewModel.PropertyChanged += OnViewModelChanged;
        AppWindow.Closing += OnClosing;
        AddKeyboardShortcuts();
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Shows the window and brings it forward, from the tray or a second launch.</summary>
    public void Reveal()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    /// <summary>Closes for real: the tray menu's Quit, and before an update installs.</summary>
    public void CloseForGood()
    {
        isQuitting = true;
        Close();
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        SyncNavigation();
        await ViewModel.StartAsync();
        AppServices.Current.WindowDidLoad(this);
    }

    /// <summary>
    /// Closing the window leaves the app in the notification area, so the
    /// countdown and the refreshes carry on — unless that was switched off in
    /// Settings, in which case closing quits, as in most Windows apps.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (isQuitting || !ViewModel.SettingsForm.KeepsRunningInNotificationArea)
        {
            AppServices.Current.Quit(closeWindow: false);
            return;
        }
        args.Cancel = true;
        sender.Hide();
        AppServices.Current.AnnounceStillRunningOnce();
    }

    // ------------------------------------------------------------------
    // Navigation

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (isSyncingNavigation)
        {
            return;
        }
        if (args.IsSettingsSelected)
        {
            ViewModel.SidebarSelection = SidebarDestination.Settings;
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<SidebarDestination>(tag, out var destination))
        {
            ViewModel.SidebarSelection = destination;
        }
    }

    private void SyncNavigation()
    {
        isSyncingNavigation = true;
        Navigation.SelectedItem = ViewModel.SidebarSelection switch
        {
            SidebarDestination.AllEvents => AllEventsItem,
            SidebarDestination.Calendar => CalendarItem,
            SidebarDestination.Completed => CompletedItem,
            SidebarDestination.Settings => Navigation.SettingsItem,
            _ => UpcomingItem,
        };
        isSyncingNavigation = false;
    }

    // ------------------------------------------------------------------
    // The view model asks; the window shows

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        SyncNavigation();
        var root = Root.XamlRoot;
        if (root is null)
        {
            return;
        }

        if (ViewModel.IsShowingManualEditor && !DialogState.ManualEditor)
        {
            DialogState.ManualEditor = true;
            _ = ShowThenReset(ManualEventDialog.ShowAsync(root, ViewModel, ViewModel.ManualEventDraft), () => DialogState.ManualEditor = false);
        }
        if (ViewModel.IsShowingImportedDetails && !DialogState.ImportedDetails)
        {
            DialogState.ImportedDetails = true;
            _ = ShowThenReset(ImportedEventDialog.ShowAsync(root, ViewModel), () => DialogState.ImportedDetails = false);
        }
        if (ViewModel.IsShowingFeedImport && !DialogState.FeedImport)
        {
            DialogState.FeedImport = true;
            _ = ShowThenReset(FeedImportDialog.ShowAsync(root, ViewModel), () => DialogState.FeedImport = false);
        }
        if (ViewModel.IsShowingScreenshotImport && !DialogState.ScreenshotImport)
        {
            DialogState.ScreenshotImport = true;
            _ = ShowThenReset(ScreenshotImportDialog.ShowAsync(root, ViewModel), () => DialogState.ScreenshotImport = false);
        }
        if (ViewModel.ErrorMessage is { } message && !isShowingError)
        {
            isShowingError = true;
            _ = ShowError(root, message);
        }
    }

    private static async Task ShowThenReset(Task dialog, Action reset)
    {
        try
        {
            await dialog;
        }
        finally
        {
            reset();
        }
    }

    private async Task ShowError(XamlRoot root, string message)
    {
        try
        {
            await Dialogs.InformAsync(root, "Canvas Countdown couldn’t complete that action", message);
        }
        finally
        {
            ViewModel.ClearError();
            isShowingError = false;
        }
    }

    private static class DialogState
    {
        public static bool ManualEditor;
        public static bool ImportedDetails;
        public static bool FeedImport;
        public static bool ScreenshotImport;
    }

    // ------------------------------------------------------------------
    // The command bar

    private void OnUndoAddition(object sender, RoutedEventArgs e) => _ = ViewModel.UndoLastAdditionAsync();

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareForToolbarAction();
        ViewModel.RefreshManually();
    }

    private void OnAddManualEvent(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareForToolbarAction();
        ViewModel.PresentNewManualEvent();
    }

    private void OnImportFeed(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareForToolbarAction();
        ViewModel.PresentFeedImport();
    }

    private void OnImportScreenshot(object sender, RoutedEventArgs e) => ViewModel.PresentScreenshotImport();

    private void OnToggleAssistant(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareForToolbarAction();
        ViewModel.ToggleAssistant();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => ViewModel.PresentSearch();

    private void OnDismissUpdate(object sender, RoutedEventArgs e) => ViewModel.DismissUpdate();

    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _ = ViewModel.DownloadUpdateAsync();

    private void OnOpenReleasePage(object sender, RoutedEventArgs e) => ViewModel.OpenReleasePage();

    /// <summary>
    /// One flat menu, rebuilt each time it opens so it lists the courses there
    /// are now. "Show completed and ignored" sits at the top, written out as
    /// what it is: a filter, not a switch somewhere else.
    /// </summary>
    private void OnFilterMenuOpening(object? sender, object e)
    {
        FilterMenu.Items.Clear();
        var showAll = new ToggleMenuFlyoutItem
        {
            Text = "Show completed and ignored",
            IsChecked = ViewModel.ShowCompletedAndIgnored,
        };
        ToolTipService.SetToolTip(showAll, "Keep finished and ignored events in the list");
        showAll.Click += (_, _) => ViewModel.ShowCompletedAndIgnored = showAll.IsChecked;
        FilterMenu.Items.Add(showAll);
        FilterMenu.Items.Add(new MenuFlyoutSeparator());

        FilterMenu.Items.Add(CourseItem(null));
        var courses = ViewModel.AvailableCourses;
        if (courses.Count > 0)
        {
            FilterMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (var course in courses)
            {
                FilterMenu.Items.Add(CourseItem(course));
            }
        }
    }

    private RadioMenuFlyoutItem CourseItem(string? course)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = MainViewModel.MenuTitle(course),
            GroupName = "course",
            IsChecked = ViewModel.SelectedCourse == course,
        };
        ToolTipService.SetToolTip(item, course ?? "Show assignments from every course");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, course ?? "All Courses");
        item.Click += (_, _) => ViewModel.SelectCourse(course);
        return item;
    }

    // ------------------------------------------------------------------
    // Keyboard

    private void AddKeyboardShortcuts()
    {
        Add(VirtualKey.F, VirtualKeyModifiers.Control, () => ViewModel.PresentSearch());
        Add(VirtualKey.N, VirtualKeyModifiers.Control, () => ViewModel.PresentNewManualEvent());
        Add(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ViewModel.PresentScreenshotImport());
        Add(VirtualKey.R, VirtualKeyModifiers.Control, () => ViewModel.RefreshManually());
        Add(VirtualKey.A, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => ViewModel.ToggleAssistant());
        Add((VirtualKey)0xBC, VirtualKeyModifiers.Control, () => ViewModel.SidebarSelection = SidebarDestination.Settings);
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () =>
        {
            if (ViewModel.CanUndoAddition)
            {
                _ = ViewModel.UndoLastAdditionAsync();
            }
        }, onlyOutsideText: true);

        // Day, week, month and year, today, and Go to Date — ⌘ on the Mac,
        // Ctrl here. Each moves to the calendar first, so the key means
        // "show me the week" from anywhere.
        foreach (var shortcut in CalendarShortcuts.All)
        {
            var key = shortcut.Key switch
            {
                >= '1' and <= '9' => VirtualKey.Number0 + (shortcut.Key - '0'),
                _ => (VirtualKey)char.ToUpperInvariant(shortcut.Key),
            };
            var modifiers = VirtualKeyModifiers.Control;
            if (shortcut.Modifiers.HasFlag(CalendarShortcutModifiers.Shift))
            {
                modifiers |= VirtualKeyModifiers.Shift;
            }
            var command = shortcut;
            Add(key, modifiers, () =>
            {
                switch (command.Command)
                {
                    case CalendarCommand.ShowScale when command.Scale is { } scale:
                        ViewModel.ShowCalendarSection(scale);
                        break;
                    case CalendarCommand.ShowToday:
                        ViewModel.ShowCalendarSection();
                        break;
                    case CalendarCommand.GoToDate:
                        ViewModel.PresentCalendarDateEntry();
                        break;
                }
            });
        }
    }

    private void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool onlyOutsideText = false)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Ctrl+Z in a text box is the text box's own undo.
            if (onlyOutsideText && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox or AutoSuggestBox)
            {
                return;
            }
            action();
            args.Handled = true;
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>
    /// Opens at a comfortable size, but never larger than the screen it opens
    /// on: plenty of student laptops are 1366×768, where a fixed 1100×760
    /// window would open with its bottom edge under the taskbar.
    /// </summary>
    private void SizeToFitDisplay()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // Window sizes are in physical pixels; the content is not laid out
        // yet, so the display scale comes from the window itself.
        var dpi = GetDpiForWindow(Handle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var width = Math.Min((int)(1100 * scale), (int)(area.Width * 0.94));
        var height = Math.Min((int)(760 * scale), (int)(area.Height * 0.94));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2),
            width,
            height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

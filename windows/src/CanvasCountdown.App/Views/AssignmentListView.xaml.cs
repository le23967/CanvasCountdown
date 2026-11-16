using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CanvasCountdown.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CanvasCountdown.App.Views;

/// <summary>Upcoming, All Events and Completed: the next-deadline card and the list under it.</summary>
public sealed partial class AssignmentListView : UserControl
{
    private readonly ObservableCollection<AssignmentRowPresentation> rows = [];
    private bool isSyncingSelection;

    public AssignmentListView()
    {
        InitializeComponent();
        Items.ItemsSource = rows;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => Synchronize();
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Synchronize();

    private void Synchronize()
    {
        var viewModel = ViewModel;
        var section = viewModel.SidebarSelection;
        var calendar = AppCalendar.Current;
        var culture = CultureInfo.CurrentCulture;

        if (section is SidebarDestination.Upcoming or SidebarDestination.AllEvents or SidebarDestination.Completed)
        {
            var presented = viewModel.FilteredAssignments
                .Select(item => AssignmentRowPresentation.From(item, viewModel.Label(item), viewModel.CurrentDate, calendar, culture))
                .ToArray();
            CollectionSync.Apply(rows, presented, row => row.Id);
        }

        UpdateCard(viewModel, section, calendar, culture);
        UpdateEmptyState(viewModel, section);

        isSyncingSelection = true;
        Items.SelectedItem = viewModel.SelectedEventId is { } id ? rows.FirstOrDefault(row => row.Id == id) : null;
        isSyncingSelection = false;
    }

    private void UpdateCard(MainViewModel viewModel, SidebarDestination section, AppCalendar calendar, CultureInfo culture)
    {
        var nearest = section == SidebarDestination.Upcoming && !viewModel.IsLoading ? viewModel.NearestAssignment : null;
        if (nearest is null)
        {
            NextDeadlineCard.Visibility = Visibility.Collapsed;
            return;
        }
        var card = NextDeadlinePresentation.From(nearest, viewModel.CurrentDate, calendar, culture);
        CardDays.Text = card.DayCountText;
        CardDayLabel.Text = card.DayLabel;
        CardTitle.Text = nearest.Title;
        CardDetails.Text = nearest.NormalizedCourseName is { } course ? $"{course} · {card.DueText}" : card.DueText;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CardCount, card.AccessibilityLabel);
        NextDeadlineCard.Visibility = Visibility.Visible;
    }

    private void UpdateEmptyState(MainViewModel viewModel, SidebarDestination section)
    {
        var isEmpty = !viewModel.IsLoading && rows.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        Items.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!isEmpty)
        {
            return;
        }

        EmptyRefresh.Visibility = Visibility.Collapsed;
        EmptyConnect.Visibility = Visibility.Collapsed;
        EmptyAdd.Visibility = Visibility.Collapsed;
        EmptyScreenshot.Visibility = Visibility.Collapsed;

        if (viewModel.SelectedCourse is not null)
        {
            EmptyGlyph.Glyph = "";
            EmptyTitle.Text = "No events in this course";
            EmptyDescription.Text = "Choose another course from Filter, or All Courses to see everything.";
            return;
        }

        switch (section)
        {
            case SidebarDestination.Upcoming:
                EmptyGlyph.Glyph = "";
                EmptyTitle.Text = "No upcoming assignments";
                EmptyDescription.Text = viewModel.HasConfiguredFeed
                    ? "You’re caught up. Refresh to check Canvas for changes."
                    : "Connect a Canvas calendar feed or add a manual event.";
                EmptyRefresh.Visibility = viewModel.HasConfiguredFeed ? Visibility.Visible : Visibility.Collapsed;
                EmptyConnect.Visibility = viewModel.HasConfiguredFeed ? Visibility.Collapsed : Visibility.Visible;
                EmptyAdd.Visibility = Visibility.Visible;
                EmptyScreenshot.Visibility = Visibility.Visible;
                break;
            case SidebarDestination.Completed:
                EmptyGlyph.Glyph = "";
                EmptyTitle.Text = "No completed assignments";
                EmptyDescription.Text = "Mark an assignment complete from its Actions menu.";
                break;
            default:
                EmptyGlyph.Glyph = "";
                EmptyTitle.Text = "No events";
                EmptyDescription.Text = "Imported and manual events appear here.";
                break;
        }
    }

    // A single click opens the row: the editor for a manual event, the
    // details for one Canvas owns.
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AssignmentRowPresentation row)
        {
            ViewModel.SelectedEventId = row.Id;
            ViewModel.PresentEditor(row.Item);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isSyncingSelection && Items.SelectedItem is AssignmentRowPresentation row)
        {
            ViewModel.SelectedEventId = row.Id;
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when ViewModel.SelectedItem is not null:
                ViewModel.OpenSelectedEvent();
                e.Handled = true;
                break;
            case VirtualKey.Delete when ViewModel.DeletionCandidate() is { } item:
                _ = EventActions.ConfirmDeleteAsync(XamlRoot, ViewModel, item);
                e.Handled = true;
                break;
        }
    }

    private void OnActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AssignmentRowPresentation row } element)
        {
            EventActions.Menu(ViewModel, row.Item, XamlRoot).ShowAt(element);
        }
    }

    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AssignmentRowPresentation row } element)
        {
            var menu = EventActions.Menu(ViewModel, row.Item, XamlRoot);
            if (e.TryGetPosition(element, out var point))
            {
                menu.ShowAt(element, point);
            }
            else
            {
                menu.ShowAt(element);
            }
            e.Handled = true;
        }
    }

    private void OnEmptyRefresh(object sender, RoutedEventArgs e) => ViewModel.RefreshManually();

    private void OnEmptyConnect(object sender, RoutedEventArgs e) => ViewModel.PresentFeedImport(onboarding: true);

    private void OnEmptyAdd(object sender, RoutedEventArgs e) => ViewModel.PresentNewManualEvent();

    private void OnEmptyScreenshot(object sender, RoutedEventArgs e) => ViewModel.PresentScreenshotImport();
}

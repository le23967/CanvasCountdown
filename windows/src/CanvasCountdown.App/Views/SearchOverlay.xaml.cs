using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CanvasCountdown.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CanvasCountdown.App.Views;

/// <summary>One search result as the panel shows it.</summary>
public sealed record SearchResultRow(AssignmentListItem Item, string Title, string Subtitle, string Remaining, string AccessibilityLabel)
{
    public Guid Id => Item.Id;

    public static SearchResultRow From(AssignmentListItem item, DateTimeOffset now, AppCalendar calendar, CultureInfo culture)
    {
        var due = DateText.Short(item.DueDate, calendar, culture);
        var subtitle = item.NormalizedCourseName is { } course ? $"{course} · {due}" : due;
        var days = item.RemainingDays(now, calendar);
        var remaining = item.IsCompleted
            ? "Done"
            : days switch
            {
                0 => "Today",
                1 => "1 day",
                < 0 => -days == 1 ? "1 day ago" : $"{-days} days ago",
                _ => $"{days} days",
            };
        return new SearchResultRow(item, item.Title, subtitle, remaining, $"{item.Title}, {subtitle}, {remaining}");
    }
}

/// <summary>Ctrl+F: a panel over the window that searches everything stored.</summary>
public sealed partial class SearchOverlay : UserControl
{
    private readonly ObservableCollection<SearchResultRow> results = [];
    private bool wasActive;

    public SearchOverlay()
    {
        InitializeComponent();
        Results.ItemsSource = results;
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var viewModel = ViewModel;
        var rows = viewModel.SearchResults
            .Select(item => SearchResultRow.From(item, viewModel.CurrentDate, AppCalendar.Current, CultureInfo.CurrentCulture))
            .ToArray();
        CollectionSync.Apply(results, rows, row => row.Id);

        var highlighted = viewModel.HighlightedSearchResult;
        Results.SelectedItem = highlighted is null ? null : results.FirstOrDefault(row => row.Id == highlighted.Id);
        if (Results.SelectedItem is not null)
        {
            Results.ScrollIntoView(Results.SelectedItem);
        }

        NoMatches.Text = $"Nothing matches “{viewModel.SearchText.Trim()}”";
        NoMatches.Visibility = viewModel.HasSearchQuery && results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (viewModel.IsSearchModeActive && !wasActive)
        {
            // Opening the panel puts the cursor in the field, as the toolbar
            // button and Ctrl+F both promise.
            DispatcherQueue.TryEnqueue(() => Query.Focus(FocusState.Programmatic));
        }
        wasActive = viewModel.IsSearchModeActive;
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                ViewModel.MoveSearchHighlight(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ViewModel.MoveSearchHighlight(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ViewModel.OpenHighlightedSearchResult();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                ViewModel.HandleSearchEscape();
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultRow row)
        {
            ViewModel.OpenSearchResult(row.Item);
        }
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => ViewModel.DismissSearch();
}

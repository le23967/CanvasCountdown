using Microsoft.UI.Xaml;

namespace CanvasCountdown.App;

/// <summary>Small functions the XAML binds through, so the markup stays declarative.</summary>
public static class Show
{
    public static Visibility If(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Unless(bool condition) => condition ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility When(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility WhenNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IfAny(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Has(object? value) => value is not null;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static bool Not(bool value) => !value;

    public static bool CanRefresh(bool isRefreshing, bool hasConfiguredFeed) => !isRefreshing && hasConfiguredFeed;

    public static Visibility Section(SidebarDestination selection, string name)
        => string.Equals(selection.ToString(), name, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Upcoming, All Events and Completed share the list.</summary>
    public static Visibility ListSection(SidebarDestination selection, bool isLoading)
        => !isLoading && selection is SidebarDestination.Upcoming or SidebarDestination.AllEvents or SidebarDestination.Completed
            ? Visibility.Visible
            : Visibility.Collapsed;
}

/// <summary>Text the window shows that is not a plain property.</summary>
public static class Text
{
    public static string SectionTitle(SidebarDestination selection) => selection.Title();

    public static string FilterLabel(bool isActive) => isActive ? "Filtered" : "Filter";

    public static string AssistantToggleTip(bool isOpen)
        => isOpen ? "Hide the assistant (Ctrl+Alt+A)" : "Show the assistant (Ctrl+Alt+A)";

    public static string UpdateTitle(AppRelease? release)
        => release is null ? string.Empty : $"Version {release.Version} is available";

    public static string UpdateMessage(AppRelease? release, string? message)
        => !string.IsNullOrWhiteSpace(message) ? message! : release?.Summary ?? string.Empty;

    /// <summary>"Important • COMP2050 • Fri, Aug 14, 11:59 PM", leaving out whatever is missing.</summary>
    public static string RowDetails(string? labelName, string? courseText, string dueText)
        => string.Join(" • ", new[] { labelName, courseText, dueText }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static string ActionsFor(string title) => $"Actions for {title}";

    public static string UndoMessage(MainViewModel.UndoableAddition? addition) => addition?.Message ?? string.Empty;

    public static string UndoAdditionTip(string menuTitle) => $"{menuTitle} (Ctrl+Z)";

    public static string Seconds(int? seconds) => seconds is { } value ? $"{value}s" : string.Empty;

    public static string UndoToastDescription(MainViewModel.UndoableAddition? addition, int? seconds)
        => addition is null || seconds is null
            ? string.Empty
            : $"{addition.Message}. Undo available for {seconds} more seconds.";

    public static string RefreshStatus(bool isRefreshing, DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (isRefreshing)
        {
            return "Refreshing…";
        }
        return lastRefresh is { } last ? $"Updated {DateText.Relative(last, now)}" : string.Empty;
    }
}

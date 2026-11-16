namespace CanvasCountdown;

public sealed partial class MainViewModel
{
    // ------------------------------------------------------------------
    // Search

    /// <summary>
    /// Search is a panel over the window, not a field wedged into the toolbar.
    /// </summary>
    /// <remarks>
    /// The field used to replace the whole toolbar with itself and a Cancel
    /// button, which meant the things people came for disappeared the moment
    /// they went looking for something. A panel takes nothing away: the
    /// toolbar stays exactly as it was, the list stays exactly as it was, and
    /// the results are in the panel rather than hidden behind it.
    /// </remarks>
    public void PresentSearch()
    {
        IsSearchModeActive = true;
        IsSearchFieldFocused = true;
        HighlightedSearchResultId = SearchResults.FirstOrDefault()?.Id;
        Changed();
    }

    public void ToggleSearch()
    {
        if (IsSearchModeActive)
        {
            DismissSearch();
        }
        else
        {
            PresentSearch();
        }
    }

    /// <summary>Releases keyboard focus but leaves the panel and its query in place.</summary>
    public void DismissSearchFocus()
    {
        IsSearchFieldFocused = false;
        Changed();
    }

    /// <summary>
    /// Closes the panel and clears the query. Escape, a click on the dimmed
    /// backdrop, opening a result and a section change all land here, so
    /// leaving search always means the same thing.
    /// </summary>
    public void DismissSearch()
    {
        IsSearchFieldFocused = false;
        IsSearchModeActive = false;
        HighlightedSearchResultId = null;
        if (searchText.Length > 0)
        {
            searchText = string.Empty;
        }
        Changed();
    }

    public void HandleSearchEscape() => DismissSearch();

    public void ClearSearchQuery()
    {
        searchText = string.Empty;
        HighlightedSearchResultId = null;
        Changed();
    }

    /// <summary>
    /// Called before a toolbar action runs, so the field gives up focus and the
    /// action still happens on the same click.
    /// </summary>
    public void PrepareForToolbarAction() => DismissSearchFocus();

    /// <summary>The row the keyboard is on, so Enter has something to open.</summary>
    public Guid? HighlightedSearchResultId { get; private set; }

    /// <summary>How many results the panel shows before it stops. A panel that grows past the window is not a panel any more.</summary>
    public const int MaximumSearchResults = 8;

    /// <summary>
    /// Everything stored, not just the section on screen.
    /// </summary>
    /// <remarks>
    /// Searching only what is already visible would mean the answer depends on
    /// where you happened to be standing, which is the opposite of what a
    /// search is for. A completed assignment is still a thing you can look for.
    /// </remarks>
    public IReadOnlyList<AssignmentListItem> SearchResults
    {
        get
        {
            var query = searchText.Trim();
            if (query.Length == 0)
            {
                return [];
            }
            var now = CurrentDate;
            return Assignments
                .Where(item => Contains(item.Title, query) || Contains(item.NormalizedCourseName, query))
                .Order(Comparer<AssignmentListItem>.Create((left, right) =>
                {
                    // What is still ahead comes first, nearest deadline at the
                    // top; anything already past follows, most recent first.
                    var leftPast = left.DueDate < now;
                    var rightPast = right.DueDate < now;
                    if (leftPast != rightPast)
                    {
                        return leftPast ? 1 : -1;
                    }
                    return leftPast ? right.DueDate.CompareTo(left.DueDate) : left.DueDate.CompareTo(right.DueDate);
                }))
                .Take(MaximumSearchResults)
                .ToArray();
        }
    }

    private static bool Contains(string? text, string query)
        => text is not null
            && System.Globalization.CultureInfo.CurrentCulture.CompareInfo.IndexOf(
                text,
                query,
                System.Globalization.CompareOptions.IgnoreCase) >= 0;

    public bool HasSearchQuery => searchText.Trim().Length > 0;

    public AssignmentListItem? HighlightedSearchResult
    {
        get
        {
            var results = SearchResults;
            if (HighlightedSearchResultId is not { } id)
            {
                return results.FirstOrDefault();
            }
            return results.FirstOrDefault(item => item.Id == id) ?? results.FirstOrDefault();
        }
    }

    /// <summary>Moves the keyboard highlight, wrapping at both ends so holding an arrow key never dead-ends.</summary>
    public void MoveSearchHighlight(int offset)
    {
        var results = SearchResults;
        if (results.Count == 0)
        {
            HighlightedSearchResultId = null;
            Changed();
            return;
        }
        var index = HighlightedSearchResultId is { } current
            ? results.ToList().FindIndex(item => item.Id == current)
            : -1;
        if (index < 0)
        {
            HighlightedSearchResultId = offset >= 0 ? results[0].Id : results[^1].Id;
            Changed();
            return;
        }
        var next = ((index + offset) % results.Count + results.Count) % results.Count;
        HighlightedSearchResultId = results[next].Id;
        Changed();
    }

    /// <summary>Keeps the highlight on something that still exists as the query changes.</summary>
    public void SearchQueryDidChange()
    {
        var results = SearchResults;
        if (HighlightedSearchResultId is not { } current || results.All(item => item.Id != current))
        {
            HighlightedSearchResultId = results.FirstOrDefault()?.Id;
        }
    }

    /// <summary>
    /// Opens a result the way clicking its row would, and closes the panel:
    /// the search is over once it has been answered.
    /// </summary>
    public void OpenSearchResult(AssignmentListItem item)
    {
        SelectedEventId = item.Id;
        DismissSearch();
        PresentEditor(item);
    }

    public bool OpenHighlightedSearchResult()
    {
        if (HighlightedSearchResult is not { } item)
        {
            return false;
        }
        OpenSearchResult(item);
        return true;
    }
}

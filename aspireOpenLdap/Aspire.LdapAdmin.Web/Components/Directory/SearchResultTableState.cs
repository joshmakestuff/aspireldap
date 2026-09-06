using Aspire.LdapAdmin.Core;

namespace Aspire.LdapAdmin.Web.Components.Directory;

internal enum SearchResultSortColumn
{
    Dn,
    Name,
    Mail,
}

internal enum SearchResultSortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// Local presentation state for the bounded set already fetched from LDAP. Rows retain their
/// fetch index so equal sort values remain stable, and the visible page has one full-DN key.
/// </summary>
internal sealed class SearchResultTableState
{
    internal const int DefaultPageSize = 25;

    private readonly int _pageSize;
    private List<SearchResultRow> _rows = [];
    private List<SearchResultRow> _orderedRows = [];

    internal SearchResultTableState(LdapAdminSortOrder initialSortOrder, int pageSize = DefaultPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;

        if (initialSortOrder == LdapAdminSortOrder.Rdn)
        {
            SortColumn = SearchResultSortColumn.Dn;
            SortDirection = SearchResultSortDirection.Ascending;
        }
    }

    internal SearchResultSortColumn? SortColumn { get; private set; }

    internal SearchResultSortDirection SortDirection { get; private set; } = SearchResultSortDirection.Ascending;

    internal int PageNumber { get; private set; } = 1;

    internal int PageCount => _orderedRows.Count == 0 ? 0 : (_orderedRows.Count + _pageSize - 1) / _pageSize;

    internal int FetchedCount => _orderedRows.Count;

    internal int FirstVisibleNumber => FetchedCount == 0 ? 0 : ((PageNumber - 1) * _pageSize) + 1;

    internal int LastVisibleNumber => Math.Min(PageNumber * _pageSize, FetchedCount);

    internal bool HasPreviousPage => PageNumber > 1;

    internal bool HasNextPage => PageNumber < PageCount;

    /// <summary>The sole rendered-row sequence; each row is keyed by its full DN.</summary>
    internal IReadOnlyList<SearchResultRow> VisibleRows =>
        _orderedRows.Skip((PageNumber - 1) * _pageSize).Take(_pageSize).ToArray();

    internal void SetEntries(IReadOnlyList<LdapEntry> entries, string commonSuffix)
    {
        _rows = [.. entries.Select((entry, index) => new SearchResultRow(
            entry.Dn,
            entry,
            DnDisplay.Relative(entry.Dn, commonSuffix),
            First(entry, "cn", "displayName", "ou", "o"),
            First(entry, "mail"),
            index))];
        ApplySort();
        PageNumber = 1;
    }

    internal void SortBy(SearchResultSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDirection = SortDirection == SearchResultSortDirection.Ascending
                ? SearchResultSortDirection.Descending
                : SearchResultSortDirection.Ascending;
        }
        else
        {
            SortColumn = column;
            SortDirection = SearchResultSortDirection.Ascending;
        }

        ApplySort();
        PageNumber = 1;
    }

    internal void GoToPage(int pageNumber) =>
        PageNumber = PageCount == 0 ? 1 : Math.Clamp(pageNumber, 1, PageCount);

    internal string AriaSort(SearchResultSortColumn column) => SortColumn == column
        ? SortDirection == SearchResultSortDirection.Ascending ? "ascending" : "descending"
        : "none";

    internal string SortIndicator(SearchResultSortColumn column) => SortColumn == column
        ? SortDirection == SearchResultSortDirection.Ascending ? "ASC" : "DESC"
        : string.Empty;

    private void ApplySort()
    {
        if (SortColumn is null)
        {
            _orderedRows = [.. _rows];
            return;
        }

        var column = SortColumn.Value;
        var direction = SortDirection;
        _orderedRows = [.. _rows.OrderBy(
            static row => row,
            Comparer<SearchResultRow>.Create((left, right) => CompareRows(left, right, column, direction)))];
    }

    private static int CompareRows(
        SearchResultRow left,
        SearchResultRow right,
        SearchResultSortColumn column,
        SearchResultSortDirection direction)
    {
        var leftValue = Value(left, column);
        var rightValue = Value(right, column);
        var leftMissing = string.IsNullOrEmpty(leftValue);
        var rightMissing = string.IsNullOrEmpty(rightValue);
        if (leftMissing != rightMissing)
        {
            return leftMissing ? 1 : -1;
        }

        var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftValue, rightValue);
        if (comparison == 0)
        {
            return left.FetchIndex.CompareTo(right.FetchIndex);
        }

        return direction == SearchResultSortDirection.Ascending ? comparison : -comparison;
    }

    private static string Value(SearchResultRow row, SearchResultSortColumn column) => column switch
    {
        SearchResultSortColumn.Dn => row.DisplayDn,
        SearchResultSortColumn.Name => row.Name,
        SearchResultSortColumn.Mail => row.Mail,
        _ => string.Empty,
    };

    private static string First(LdapEntry entry, params string[] names)
    {
        foreach (var name in names)
        {
            var attribute = entry.Attributes.FirstOrDefault(
                attribute => attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !attribute.IsBinary);
            if (attribute?.Values.FirstOrDefault(static value => value.Length > 0) is { } value)
            {
                return value;
            }
        }

        return string.Empty;
    }
}

internal sealed record SearchResultRow(
    string Key,
    LdapEntry Entry,
    string DisplayDn,
    string Name,
    string Mail,
    int FetchIndex);

public sealed record SearchResultSelectionChange(LdapEntry Entry, bool Selected);

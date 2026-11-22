using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AwAUI.Shadcn;

/// <summary>
/// Headless table state for <see cref="DataTable{T}"/>, modelled on the core of TanStack Table:
/// column definitions and visibility, multi-column sorting with precedence, a global filter plus
/// per-column filters, pagination, and selection over the filtered row set.
/// <para>
/// All mutating methods raise <see cref="Changed"/> exactly once per logical change. A
/// <see cref="DataTable{T}"/> rendered with an external model rebuilds when that event fires; when no
/// model is supplied the component owns one. Row identity is the string returned by the
/// <c>rowKey</c> selector (or the row's invariant text).
/// </para>
/// <para>
/// Pipeline order is: per-column filters and the global filter, then sorting, then pagination.
/// Pagination counts therefore derive from the filtered/sorted row set, and the page index clamps to
/// the surviving range whenever the filter changes.
/// </para>
/// <para>
/// Selection is keyed by row identity and is global by default: it is not scoped to the current page.
/// <see cref="SelectAll"/>, <see cref="ToggleAllRowsSelected"/>, <see cref="IsAllRowsSelected"/> and
/// <see cref="IsSomeRowsSelected"/> operate on every row that survives the current filters. The page
/// helpers (<see cref="IsPageAllSelected"/>, <see cref="IsPageIndeterminate"/>,
/// <see cref="SetPageSelected"/>) remain available for page-scoped affordances.
/// </para>
/// </summary>
public sealed class DataTableModel<T>
{
    readonly List<T> _rows = new();
    readonly List<DataTableColumn<T>> _columns = new();
    readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    readonly List<T> _filtered = new();
    readonly List<T> _sorted = new();
    readonly List<SortEntry> _sorts = new();
    readonly Dictionary<string, string> _columnFilters = new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _visibility = new(StringComparer.Ordinal);

    object? _rowsSource;
    object? _columnsSource;
    Func<T, string>? _rowKey;

    string _filter = "";
    int _pageSize = 10;
    int _pageIndex;
    bool _filterDirty = true;
    bool _sortDirty = true;

    readonly record struct SortEntry(string Key, DataTableSortDirection Direction);

    /// <summary>Creates an empty model.</summary>
    public DataTableModel() { }

    /// <summary>Creates a model over the supplied rows and columns.</summary>
    public DataTableModel(IEnumerable<T>? rows = null, IEnumerable<DataTableColumn<T>>? columns = null,
        Func<T, string>? rowKey = null, int pageSize = 10)
    {
        if (rows is not null) _rows.AddRange(rows);
        if (columns is not null) _columns.AddRange(columns);
        _rowKey = rowKey;
        _pageSize = pageSize > 0 ? pageSize : 10;
        _rowsSource = rows;
        _columnsSource = columns;
    }

    /// <summary>Raised after any model state change.</summary>
    public event Action? Changed;

    /// <summary>Current source rows in insertion order.</summary>
    public IReadOnlyList<T> Rows => _rows;

    /// <summary>Current column definitions, including hidden columns.</summary>
    public IReadOnlyList<DataTableColumn<T>> Columns => _columns;

    /// <summary>Column definitions whose effective visibility is true, in definition order.</summary>
    public IReadOnlyList<DataTableColumn<T>> VisibleColumns
    {
        get
        {
            var visible = new List<DataTableColumn<T>>(_columns.Count);
            for (int i = 0; i < _columns.Count; i++)
                if (GetColumnVisibility(_columns[i].Key)) visible.Add(_columns[i]);
            return visible;
        }
    }

    /// <summary>Current global filter text (<see cref="string.Empty"/> when disabled).</summary>
    public string GlobalFilter => _filter;

    /// <summary>Per-column filter text keyed by column key. Empty when no column filter is active.</summary>
    public IReadOnlyDictionary<string, string> ColumnFilters => _columnFilters;

    /// <summary>True when the global filter or any per-column filter is active.</summary>
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(_filter) || _columnFilters.Count > 0;

    /// <summary>Active sort descriptors, primary sort first (precedence zero).</summary>
    public IReadOnlyList<DataTableSortDescriptor> SortDescriptors
    {
        get
        {
            if (_sorts.Count == 0) return Array.Empty<DataTableSortDescriptor>();
            var descriptors = new DataTableSortDescriptor[_sorts.Count];
            for (int i = 0; i < _sorts.Count; i++)
                descriptors[i] = new DataTableSortDescriptor(_sorts[i].Key, _sorts[i].Direction, i);
            return descriptors;
        }
    }

    /// <summary>
    /// Whether sort changes may accumulate instead of replacing the previous sort. Defaults to true,
    /// matching TanStack Table. Single-click <see cref="ToggleSort(string)"/> still replaces the sort;
    /// set this to false to make <see cref="AddSort"/> and multi-column toggles replace as well.
    /// </summary>
    public bool EnableMultiSort { get; set; } = true;

    /// <summary>Primary sort column key, or null when unsorted.</summary>
    public string? SortColumnKey => _sorts.Count == 0 ? null : _sorts[0].Key;

    /// <summary>Primary sort direction.</summary>
    public DataTableSortDirection SortDirection => _sorts.Count == 0 ? DataTableSortDirection.None : _sorts[0].Direction;

    /// <summary>The definition of the primary sort column, or null.</summary>
    public DataTableColumn<T>? SortColumn => FindColumn(SortColumnKey);

    /// <summary>Rows per page.</summary>
    public int PageSize => _pageSize;

    /// <summary>Zero-based page index.</summary>
    public int PageIndex => _pageIndex;

    /// <summary>Number of source rows.</summary>
    public int TotalCount => _rows.Count;

    /// <summary>Number of rows remaining after filtering.</summary>
    public int FilteredCount
    {
        get
        {
            EnsureFiltered();
            return _filtered.Count;
        }
    }

    /// <summary>Number of pages at the current filter and page size. Zero when no row matches.</summary>
    public int PageCount
    {
        get
        {
            EnsureFiltered();
            return _filtered.Count == 0 ? 0 : (_filtered.Count + _pageSize - 1) / _pageSize;
        }
    }

    /// <summary>True when a previous page exists.</summary>
    public bool CanPrevious => _pageIndex > 0;

    /// <summary>True when a next page exists.</summary>
    public bool CanNext
    {
        get
        {
            int count = PageCount;
            return count > 0 && _pageIndex < count - 1;
        }
    }

    /// <summary>Rows matching the global and per-column filters, in source order.</summary>
    public IReadOnlyList<T> FilteredRows
    {
        get
        {
            EnsureFiltered();
            return _filtered;
        }
    }

    /// <summary>Rows after filtering and sorting.</summary>
    public IReadOnlyList<T> SortedRows
    {
        get
        {
            EnsureSorted();
            return _sorted;
        }
    }

    /// <summary>Rows on the current page after filtering and sorting.</summary>
    public IReadOnlyList<T> PageRows
    {
        get
        {
            EnsureSorted();
            var page = new List<T>();
            if (_sorted.Count == 0) return page;
            int start = _pageIndex * _pageSize;
            if (start >= _sorted.Count) return page;
            int end = start + _pageSize;
            if (end > _sorted.Count) end = _sorted.Count;
            for (int i = start; i < end; i++) page.Add(_sorted[i]);
            return page;
        }
    }

    /// <summary>Keys of selected rows across all pages.</summary>
    public IReadOnlyCollection<string> SelectedKeys => _selected;

    /// <summary>Number of selected rows across all pages.</summary>
    public int SelectedCount => _selected.Count;

    // ------------------------------------------------------------ source mutation

    /// <summary>Replaces the source rows, preserving sort, filter, page size, and selection.</summary>
    public void SetRows(IEnumerable<T> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        _rows.Clear();
        _rows.AddRange(rows);
        _rowsSource = null;
        InvalidateRows();
        ClampPageIndex();
        RaiseChanged();
    }

    /// <summary>Replaces the column definitions. Stale visibility and filter entries are pruned.</summary>
    public void SetColumns(IEnumerable<DataTableColumn<T>> columns)
    {
        if (columns is null) throw new ArgumentNullException(nameof(columns));
        _columns.Clear();
        _columns.AddRange(columns);
        _columnsSource = null;
        PruneColumnState();
        ResetSortIfMissing();
        InvalidateRows();
        RaiseChanged();
    }

    // ------------------------------------------------------------ filters

    /// <summary>Sets the global filter, returns to the first page, and raises <see cref="Changed"/> once.</summary>
    public void SetGlobalFilter(string? filter)
    {
        string next = filter ?? "";
        if (string.Equals(_filter, next, StringComparison.Ordinal)) return;
        _filter = next;
        _pageIndex = 0;
        InvalidateRows();
        RaiseChanged();
    }

    /// <summary>
    /// Sets a per-column filter. Null, empty, or whitespace-only text removes the filter.
    /// Returns to the first page and raises <see cref="Changed"/> once.
    /// </summary>
    public void SetColumnFilter(string key, string? filter)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (FindColumn(key) is null) return;

        string next = filter ?? "";
        bool had = _columnFilters.TryGetValue(key, out string? existing);
        if (string.IsNullOrWhiteSpace(next))
        {
            if (!had) return;
            _columnFilters.Remove(key);
        }
        else
        {
            if (had && string.Equals(existing, next, StringComparison.Ordinal)) return;
            _columnFilters[key] = next;
        }

        _pageIndex = 0;
        InvalidateRows();
        RaiseChanged();
    }

    /// <summary>Returns the per-column filter text, or <see cref="string.Empty"/> when none is active.</summary>
    public string GetColumnFilter(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        return _columnFilters.TryGetValue(key, out string? filter) ? filter : "";
    }

    /// <summary>Removes every per-column filter and returns to the first page. Raises <see cref="Changed"/> once.</summary>
    public void ClearColumnFilters()
    {
        if (_columnFilters.Count == 0) return;
        _columnFilters.Clear();
        _pageIndex = 0;
        InvalidateRows();
        RaiseChanged();
    }

    /// <summary>Removes the global and per-column filters and returns to the first page. Raises <see cref="Changed"/> once.</summary>
    public void ClearAllFilters() => ResetFilters();

    /// <summary>Removes the global and per-column filters and returns to the first page. Raises <see cref="Changed"/> once.</summary>
    public void ResetFilters()
    {
        if (_filter.Length == 0 && _columnFilters.Count == 0) return;
        _filter = "";
        _columnFilters.Clear();
        _pageIndex = 0;
        InvalidateRows();
        RaiseChanged();
    }

    // ------------------------------------------------------------ sorting

    /// <summary>Activates a single sort by column key, replacing any existing sorts.</summary>
    public void SetSort(string? key, DataTableSortDirection direction)
    {
        string? nextKey = key;
        DataTableSortDirection nextDirection = direction;
        if (nextDirection == DataTableSortDirection.None || string.IsNullOrEmpty(nextKey) || FindColumn(nextKey) is null)
        {
            nextKey = null;
            nextDirection = DataTableSortDirection.None;
        }

        bool same = _sorts.Count == 0
            ? nextDirection == DataTableSortDirection.None
            : _sorts.Count == 1
                && string.Equals(_sorts[0].Key, nextKey, StringComparison.Ordinal)
                && _sorts[0].Direction == nextDirection;
        if (same) return;

        _sorts.Clear();
        if (nextDirection != DataTableSortDirection.None) _sorts.Add(new SortEntry(nextKey!, nextDirection));
        _pageIndex = 0;
        _sortDirty = true;
        RaiseChanged();
    }

    /// <summary>
    /// Cycles the sort for a sortable column: unsorted → ascending → descending → unsorted.
    /// <para>
    /// With <paramref name="multiColumn"/> true and <see cref="EnableMultiSort"/> enabled, other sorts
    /// are kept and this column is added as the least-significant sort (the model-level shift-click).
    /// Otherwise every other sort is replaced.
    /// </para>
    /// Returns to the first page.
    /// </summary>
    public void ToggleSort(string key) => ToggleSort(key, false);

    /// <inheritdoc cref="ToggleSort(string)"/>
    public void ToggleSort(string key, bool multiColumn)
    {
        var column = FindColumn(key);
        if (column is null || !column.EnableSorting) return;

        if (!multiColumn || !EnableMultiSort)
        {
            int currentIndex = IndexOfSort(key);
            DataTableSortDirection current = currentIndex < 0 ? DataTableSortDirection.None : _sorts[currentIndex].Direction;
            DataTableSortDirection next = current switch
            {
                DataTableSortDirection.Ascending => DataTableSortDirection.Descending,
                DataTableSortDirection.Descending => DataTableSortDirection.None,
                _ => DataTableSortDirection.Ascending,
            };
            SetSort(next == DataTableSortDirection.None ? null : key, next);
            return;
        }

        int index = IndexOfSort(key);
        if (index < 0)
        {
            _sorts.Add(new SortEntry(key, DataTableSortDirection.Ascending));
        }
        else if (_sorts[index].Direction == DataTableSortDirection.Ascending)
        {
            _sorts[index] = new SortEntry(key, DataTableSortDirection.Descending);
        }
        else if (_sorts[index].Direction == DataTableSortDirection.Descending)
        {
            _sorts.RemoveAt(index);
        }
        else
        {
            _sorts[index] = new SortEntry(key, DataTableSortDirection.Ascending);
        }

        _pageIndex = 0;
        _sortDirty = true;
        RaiseChanged();
    }

    /// <summary>
    /// Appends a sort (or updates the direction of an existing one) while keeping earlier sorts.
    /// When <see cref="EnableMultiSort"/> is off, a different existing sort is replaced. A
    /// <see cref="DataTableSortDirection.None"/> direction removes the sort. Returns to the first page.
    /// </summary>
    public void AddSort(string key, DataTableSortDirection direction = DataTableSortDirection.Ascending)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (FindColumn(key) is null) return;
        if (direction == DataTableSortDirection.None)
        {
            RemoveSort(key);
            return;
        }

        int index = IndexOfSort(key);
        if (index >= 0)
        {
            if (_sorts[index].Direction == direction) return;
            _sorts[index] = new SortEntry(key, direction);
        }
        else
        {
            if (!EnableMultiSort && _sorts.Count > 0) _sorts.Clear();
            _sorts.Add(new SortEntry(key, direction));
        }

        _pageIndex = 0;
        _sortDirty = true;
        RaiseChanged();
    }

    /// <summary>Removes one column's sort, keeping the others. Returns to the first page.</summary>
    public void RemoveSort(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        int index = IndexOfSort(key);
        if (index < 0) return;
        _sorts.RemoveAt(index);
        _pageIndex = 0;
        _sortDirty = true;
        RaiseChanged();
    }

    /// <summary>Removes every sort. Raises <see cref="Changed"/> once when something was sorted.</summary>
    public void ResetSorting()
    {
        if (_sorts.Count == 0) return;
        _sorts.Clear();
        _pageIndex = 0;
        _sortDirty = true;
        RaiseChanged();
    }

    /// <summary>Removes the primary sort (and therefore every sort, because sorts collapse to a single one).</summary>
    public void ClearSort() => ResetSorting();

    /// <summary>Direction of the sort on a column, or <see cref="DataTableSortDirection.None"/>.</summary>
    public DataTableSortDirection GetSortDirection(string key)
    {
        int index = IndexOfSort(key);
        return index < 0 ? DataTableSortDirection.None : _sorts[index].Direction;
    }

    /// <summary>Zero-based precedence of a sorted column, or -1 when it is not sorted.</summary>
    public int GetSortPrecedence(string key) => IndexOfSort(key);

    // ------------------------------------------------------------ column visibility

    /// <summary>Effective visibility of a column: an explicit model override, else the column's <see cref="DataTableColumn{T}.Visible"/>.</summary>
    public bool GetColumnVisibility(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (_visibility.TryGetValue(key, out bool visible)) return visible;
        return FindColumn(key)?.Visible ?? true;
    }

    /// <summary>
    /// Sets a column's effective visibility. Hiding an <see cref="DataTableColumn{T}.EnableHiding"/> false
    /// column is a no-op. Raises <see cref="Changed"/> once when the effective value changes.
    /// </summary>
    public void SetColumnVisibility(string key, bool visible)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        var column = FindColumn(key);
        if (column is null) return;
        if (!visible && !column.EnableHiding) return;
        if (GetColumnVisibility(key) == visible) return;
        _visibility[key] = visible;
        RaiseChanged();
    }

    /// <summary>Flips a column's visibility. No-op for columns that cannot be hidden. Raises <see cref="Changed"/> once.</summary>
    public void ToggleColumnVisibility(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        var column = FindColumn(key);
        if (column is null || !column.EnableHiding) return;
        SetColumnVisibility(key, !GetColumnVisibility(key));
    }

    // ------------------------------------------------------------ pagination

    /// <summary>Sets the page size and returns to the first page.</summary>
    public void SetPageSize(int pageSize)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (_pageSize == pageSize) return;
        _pageSize = pageSize;
        _pageIndex = 0;
        RaiseChanged();
    }

    /// <summary>Sets the page index, clamped to the available page range.</summary>
    public void SetPageIndex(int pageIndex)
    {
        int count = PageCount;
        int last = count == 0 ? 0 : count - 1;
        int next = pageIndex < 0 ? 0 : pageIndex > last ? last : pageIndex;
        if (next == _pageIndex) return;
        _pageIndex = next;
        RaiseChanged();
    }

    /// <summary>Goes to the first page.</summary>
    public void FirstPage() => SetPageIndex(0);

    /// <summary>Goes to the previous page.</summary>
    public void PreviousPage() => SetPageIndex(_pageIndex - 1);

    /// <summary>Goes to the next page.</summary>
    public void NextPage() => SetPageIndex(_pageIndex + 1);

    /// <summary>Goes to the last page.</summary>
    public void LastPage() => SetPageIndex(PageCount - 1);

    // ------------------------------------------------------------ selection

    /// <summary>Returns the stable key for a row.</summary>
    public string KeyOf(T row)
    {
        if (_rowKey is { } selector) return selector(row) ?? "";
        return Convert.ToString(row, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>True when the row key is selected.</summary>
    public bool IsSelected(string key) => _selected.Contains(key);

    /// <summary>True when the row is selected.</summary>
    public bool IsSelected(T row) => IsSelected(KeyOf(row));

    /// <summary>Selects or clears one row. Raises <see cref="Changed"/> once when the state changes.</summary>
    public void SetSelected(string key, bool selected)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        bool changed = selected ? _selected.Add(key) : _selected.Remove(key);
        if (changed) RaiseChanged();
    }

    /// <summary>Selects or clears one row.</summary>
    public void SetSelected(T row, bool selected) => SetSelected(KeyOf(row), selected);

    /// <summary>Toggles one row. Raises <see cref="Changed"/> once.</summary>
    public void ToggleSelected(T row) => ToggleRowSelected(row);

    /// <summary>Toggles one row by key. Raises <see cref="Changed"/> once.</summary>
    public void ToggleRowSelected(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        SetSelected(key, !_selected.Contains(key));
    }

    /// <summary>Toggles one row. Raises <see cref="Changed"/> once.</summary>
    public void ToggleRowSelected(T row) => ToggleRowSelected(KeyOf(row));

    /// <summary>Selects every row that survives the current filters. Raises <see cref="Changed"/> once.</summary>
    public void SelectAll()
    {
        bool changed = false;
        var rows = FilteredRows;
        for (int i = 0; i < rows.Count; i++)
            if (_selected.Add(KeyOf(rows[i]))) changed = true;
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// Selects every filtered row when not all of them are selected, otherwise clears the filtered
    /// rows from the selection. Raises <see cref="Changed"/> once.
    /// </summary>
    public void ToggleAllRowsSelected()
    {
        if (FilteredRows.Count == 0) return;
        if (IsAllRowsSelected) ClearFilteredSelection();
        else SelectAll();
    }

    /// <summary>True when every filtered row is selected and at least one row survives the filters.</summary>
    public bool IsAllRowsSelected
    {
        get
        {
            var rows = FilteredRows;
            if (rows.Count == 0) return false;
            for (int i = 0; i < rows.Count; i++)
                if (!_selected.Contains(KeyOf(rows[i]))) return false;
            return true;
        }
    }

    /// <summary>True when some, but not all, filtered rows are selected.</summary>
    public bool IsSomeRowsSelected
    {
        get
        {
            var rows = FilteredRows;
            if (rows.Count == 0) return false;
            bool any = false;
            bool all = true;
            for (int i = 0; i < rows.Count; i++)
            {
                if (_selected.Contains(KeyOf(rows[i]))) any = true;
                else all = false;
            }
            return any && !all;
        }
    }

    /// <summary>True when every row on the current page is selected (false for an empty page).</summary>
    public bool IsPageAllSelected
    {
        get
        {
            var rows = PageRows;
            if (rows.Count == 0) return false;
            for (int i = 0; i < rows.Count; i++)
                if (!_selected.Contains(KeyOf(rows[i]))) return false;
            return true;
        }
    }

    /// <summary>True when some but not all rows on the current page are selected.</summary>
    public bool IsPageIndeterminate
    {
        get
        {
            var rows = PageRows;
            if (rows.Count == 0) return false;
            bool any = false;
            bool all = true;
            for (int i = 0; i < rows.Count; i++)
            {
                if (_selected.Contains(KeyOf(rows[i]))) any = true;
                else all = false;
            }
            return any && !all;
        }
    }

    /// <summary>Selects or clears every row on the current page (other pages keep their selection).</summary>
    public void SetPageSelected(bool selected)
    {
        var rows = PageRows;
        bool changed = false;
        for (int i = 0; i < rows.Count; i++)
        {
            string key = KeyOf(rows[i]);
            if (selected ? _selected.Add(key) : _selected.Remove(key)) changed = true;
        }
        if (changed) RaiseChanged();
    }

    /// <summary>Removes the filtered rows from the selection, leaving rows outside the filter untouched.</summary>
    public void ClearFilteredSelection()
    {
        bool changed = false;
        var rows = FilteredRows;
        for (int i = 0; i < rows.Count; i++)
            if (_selected.Remove(KeyOf(rows[i]))) changed = true;
        if (changed) RaiseChanged();
    }

    /// <summary>Clears all selected rows across pages. Raises <see cref="Changed"/> once when something was selected.</summary>
    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        RaiseChanged();
    }

    /// <summary>Clears all selected rows across pages. Raises <see cref="Changed"/> once when something was selected.</summary>
    public void ResetSelection() => ClearSelection();

    /// <summary>Finds a column by its key.</summary>
    public DataTableColumn<T>? FindColumn(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        for (int i = 0; i < _columns.Count; i++)
            if (string.Equals(_columns[i].Key, key, StringComparison.Ordinal)) return _columns[i];
        return null;
    }

    // ------------------------------------------------------------ internal synchronization

    /// <summary>Updates rows, columns, and row identity without raising <see cref="Changed"/> (used during render).</summary>
    internal void SyncSource(IReadOnlyList<T> rows, IReadOnlyList<DataTableColumn<T>> columns, Func<T, string>? rowKey)
    {
        bool sourceChanged = false;
        if (!ReferenceEquals(_rowsSource, rows))
        {
            _rows.Clear();
            for (int i = 0; i < rows.Count; i++) _rows.Add(rows[i]);
            _rowsSource = rows;
            sourceChanged = true;
        }
        if (!ReferenceEquals(_columnsSource, columns))
        {
            _columns.Clear();
            for (int i = 0; i < columns.Count; i++) _columns.Add(columns[i]);
            _columnsSource = columns;
            sourceChanged = true;
        }
        if (!ReferenceEquals(_rowKey, rowKey))
        {
            _rowKey = rowKey;
            sourceChanged = true;
        }
        if (!sourceChanged) return;
        PruneColumnState();
        ResetSortIfMissing();
        InvalidateRows();
        ClampPageIndex();
    }

    /// <summary>Applies a page-size property change without raising <see cref="Changed"/> (used during render).</summary>
    internal void SyncPageSize(int pageSize)
    {
        if (pageSize <= 0 || pageSize == _pageSize) return;
        _pageSize = pageSize;
        _pageIndex = 0;
    }

    // ------------------------------------------------------------ computation

    void EnsureFiltered()
    {
        if (!_filterDirty) return;
        _filtered.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            T row = _rows[i];
            if (Matches(row)) _filtered.Add(row);
        }
        _filterDirty = false;
        _sortDirty = true;
    }

    void EnsureSorted()
    {
        EnsureFiltered();
        if (!_sortDirty) return;
        _sorted.Clear();

        if (_sorts.Count == 0)
        {
            _sorted.AddRange(_filtered);
            _sortDirty = false;
            return;
        }

        var comparer = Comparer<T>.Create(CompareRows);
        foreach (var row in _filtered.OrderBy(row => row, comparer)) _sorted.Add(row);
        _sortDirty = false;
    }

    int CompareRows(T x, T y)
    {
        for (int i = 0; i < _sorts.Count; i++)
        {
            SortEntry entry = _sorts[i];
            var column = FindColumn(entry.Key);
            if (column is null) continue;

            int result;
            if (column.SortComparison is { } comparison)
            {
                result = comparison(x, y);
            }
            else
            {
                var keySelector = column.SortValue ?? column.Value;
                if (keySelector is null) continue;
                var comparer = column.SortComparer ?? DataTableValueComparer.Instance;
                result = comparer.Compare(keySelector(x), keySelector(y));
            }

            if (result == 0) continue;
            bool ascending = entry.Direction != DataTableSortDirection.Descending;
            return result > 0 ? ascending ? 1 : -1 : ascending ? -1 : 1;
        }
        return 0;
    }

    bool Matches(T row)
    {
        if (!string.IsNullOrWhiteSpace(_filter) && !MatchesGlobal(row)) return false;
        foreach (var pair in _columnFilters)
        {
            var column = FindColumn(pair.Key);
            if (column is null) continue;
            if (!MatchesColumnFilter(row, column, pair.Value)) return false;
        }
        return true;
    }

    bool MatchesGlobal(T row)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            if (column.Filter is { } predicate)
            {
                if (predicate(row, _filter)) return true;
                continue;
            }

            object? value = ColumnValue(column, row);
            if (value is null) continue;
            if (DataTableValueComparer.ToText(value).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    bool MatchesColumnFilter(T row, DataTableColumn<T> column, string filter)
    {
        if (column.FilterFn is { } filterFn) return filterFn.IsMatch(ColumnValue(column, row), filter);
        if (column.Filter is { } predicate) return predicate(row, filter);
        object? value = ColumnValue(column, row);
        return DataTableFilterFn.Contains.IsMatch(value, filter);
    }

    static object? ColumnValue(DataTableColumn<T> column, T row) =>
        column.FilterValue is { } filterValue
            ? filterValue(row)
            : column.Value is { } accessor ? accessor(row) : null;

    void ClampPageIndex()
    {
        int count = PageCount;
        int last = count == 0 ? 0 : count - 1;
        if (_pageIndex > last) _pageIndex = last;
        if (_pageIndex < 0) _pageIndex = 0;
    }

    void InvalidateRows()
    {
        _filterDirty = true;
        _sortDirty = true;
    }

    void ResetSortIfMissing()
    {
        bool changed = false;
        for (int i = _sorts.Count - 1; i >= 0; i--)
        {
            if (FindColumn(_sorts[i].Key) is null)
            {
                _sorts.RemoveAt(i);
                changed = true;
            }
        }
        if (!changed) return;
        _pageIndex = 0;
        _sortDirty = true;
    }

    void PruneColumnState()
    {
        if (_columnFilters.Count > 0)
        {
            List<string>? stale = null;
            foreach (string key in _columnFilters.Keys)
                if (FindColumn(key) is null) (stale ??= new List<string>()).Add(key);
            if (stale is not null)
                for (int i = 0; i < stale.Count; i++) _columnFilters.Remove(stale[i]);
        }

        if (_visibility.Count > 0)
        {
            List<string>? stale = null;
            foreach (string key in _visibility.Keys)
                if (FindColumn(key) is null) (stale ??= new List<string>()).Add(key);
            if (stale is not null)
                for (int i = 0; i < stale.Count; i++) _visibility.Remove(stale[i]);
        }
    }

    int IndexOfSort(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < _sorts.Count; i++)
            if (string.Equals(_sorts[i].Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }

    void RaiseChanged() => Changed?.Invoke();
}

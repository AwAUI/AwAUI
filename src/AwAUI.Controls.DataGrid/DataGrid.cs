using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.DataGrid;

/// <summary>
/// A grid of rows and columns with column sorting, row selection, in-place cell editing, and fixed-height vertical
/// virtualization. The grid owns its scroll container, so rows outside the viewport are not instantiated.
/// <para>
/// Set <see cref="ItemsSource"/> to any sequence of row objects and describe the columns either explicitly through
/// <see cref="Columns"/> or automatically through <see cref="AutoGenerateColumns"/>. Column bindings are property
/// paths resolved with reflection; edits write back through the property setter.
/// </para>
/// </summary>
public sealed partial class DataGrid : Control
{
    const double DefaultRowHeight = 32;
    const double DefaultHeaderHeight = 32;
    const double DefaultRowHeaderWidth = 32;

    readonly Virtualizer _virtualizer = new Virtualizer(0);
    readonly ScrollController _scrollController = new ScrollController();
    readonly SelectionModel<int> _selection = new SelectionModel<int>();
    readonly List<int> _indices = new List<int>();
    readonly List<object?> _displayItems = new List<object?>();
    readonly List<DataGridColumn> _columns = new List<DataGridColumn>();
    readonly List<DataGridColumn> _autoColumns = new List<DataGridColumn>();
    readonly List<double> _columnWidths = new List<double>();
    readonly List<double> _columnOffsets = new List<double>();

    UIRoot? _root;
    long _revision;
    int _currentRow = -1;
    int _currentColumn = -1;
    int _builtColumnStart = -1;
    int _builtColumnEnd = -1;
    double _builtViewportWidth = -1;
    double _builtViewportHeight = -1;
    VirtualRange _builtRange;
    bool _hasBuilt;

    IEnumerable? _itemsSource;
    IList<DataGridColumn> _columnsSource = new List<DataGridColumn>();
    bool _autoGenerateColumns;
    double _rowHeight = DefaultRowHeight;
    DataGridLength _columnWidth = DataGridLength.Star(1);
    DataGridGridLines _gridLines;
    DataGridHeadersVisibility _headersVisibility = DataGridHeadersVisibility.Column;
    double _headerHeight = DefaultHeaderHeight;
    double _rowHeaderWidth = DefaultRowHeaderWidth;
    SelectionMode _selectionMode = SelectionMode.Extended;
    bool _canUserSortColumns = true;
    bool _isReadOnly;
    bool _isEnabled = true;
    bool _isVirtualized = true;
    int _overscan = 2;

    public DataGrid()
    {
        _scrollController.Scrolled += OnScrolled;
    }

    // ------------------------------------------------------------ 内容

    /// <summary>The row objects. Assign a new sequence to replace the rows; observable sources may also change in place.</summary>
    public IEnumerable? ItemsSource
    {
        get => _itemsSource;
        set
        {
            if (ReferenceEquals(_itemsSource, value)) return;
            _itemsSource = value;
            Invalidate();
        }
    }

    /// <summary>
    /// The columns, in display order. Assign a new list or mutate the current one. With <see cref="AutoGenerateColumns"/>
    /// enabled, generated text columns are added to this list (and removed again when the row type changes).
    /// </summary>
    public IList<DataGridColumn> Columns
    {
        get => _columnsSource;
        set
        {
            if (ReferenceEquals(_columnsSource, value)) return;
            _columnsSource = value ?? new List<DataGridColumn>();
            Invalidate();
        }
    }

    /// <summary>Generates a text column for every public readable property of the row type.</summary>
    public bool AutoGenerateColumns
    {
        get => _autoGenerateColumns;
        set
        {
            if (_autoGenerateColumns == value) return;
            _autoGenerateColumns = value;
            Invalidate();
        }
    }

    /// <summary>Height of every data row in logical pixels. Rows are clipped to this height.</summary>
    public double RowHeight
    {
        get => _rowHeight;
        set
        {
            if (_rowHeight.Equals(value)) return;
            _rowHeight = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Width applied to columns whose <see cref="DataGridColumn.Width"/> is null. The default is one star, so columns
    /// split the viewport. Use <see cref="DataGridLength.Auto"/> or pixels per column for other layouts.
    /// </summary>
    public DataGridLength ColumnWidth
    {
        get => _columnWidth;
        set
        {
            if (_columnWidth.Equals(value)) return;
            _columnWidth = value;
            Invalidate();
        }
    }

    /// <summary>Which separator lines are drawn between rows and columns.</summary>
    public DataGridGridLines GridLines
    {
        get => _gridLines;
        set
        {
            if (_gridLines == value) return;
            _gridLines = value;
            Invalidate();
        }
    }

    /// <summary>Which headers are displayed.</summary>
    public DataGridHeadersVisibility HeadersVisibility
    {
        get => _headersVisibility;
        set
        {
            if (_headersVisibility == value) return;
            _headersVisibility = value;
            Invalidate();
        }
    }

    /// <summary>Height of the sticky column-header row in logical pixels.</summary>
    public double HeaderHeight
    {
        get => _headerHeight;
        set
        {
            if (_headerHeight.Equals(value)) return;
            _headerHeight = value;
            Invalidate();
        }
    }

    /// <summary>Width of the optional row-header column in logical pixels.</summary>
    public double RowHeaderWidth
    {
        get => _rowHeaderWidth;
        set
        {
            if (_rowHeaderWidth.Equals(value)) return;
            _rowHeaderWidth = value;
            Invalidate();
        }
    }

    /// <summary>How rows react to pointer and keyboard selection gestures.</summary>
    public SelectionMode SelectionMode
    {
        get => _selectionMode;
        set
        {
            if (_selectionMode == value) return;
            _selectionMode = value;
            Invalidate();
        }
    }

    /// <summary>Whether clicking a column header cycles its sort direction.</summary>
    public bool CanUserSortColumns
    {
        get => _canUserSortColumns;
        set
        {
            if (_canUserSortColumns == value) return;
            _canUserSortColumns = value;
            Invalidate();
        }
    }

    /// <summary>Prevents every column from entering edit mode.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly == value) return;
            _isReadOnly = value;
            Invalidate();
        }
    }

    /// <summary>Whether the grid accepts focus and input.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            Invalidate();
        }
    }

    /// <summary>Mounts only the rows in and near the viewport. Off mounts every row (useful for small grids).</summary>
    public bool IsVirtualized
    {
        get => _isVirtualized;
        set
        {
            if (_isVirtualized == value) return;
            _isVirtualized = value;
            Invalidate();
        }
    }

    /// <summary>Number of rows mounted just outside each edge of the viewport.</summary>
    public int Overscan
    {
        get => _overscan;
        set
        {
            if (_overscan == value) return;
            _overscan = value;
            Invalidate();
        }
    }

    /// <summary>The number of displayed rows after sorting.</summary>
    public int RowCount => _displayItems.Count;

    /// <summary>The current row index (the cell that receives keyboard input), or -1.</summary>
    public int CurrentRowIndex => _currentRow;

    /// <summary>The current column index, or -1 when there are no columns.</summary>
    public int CurrentColumnIndex => _currentColumn;

    /// <summary>The columns currently displayed, including auto-generated columns.</summary>
    public IReadOnlyList<DataGridColumn> ActiveColumns => _columns;

    /// <summary>The width assigned to each active column by the most recent build, in logical pixels.</summary>
    public IReadOnlyList<double> ColumnActualWidths => _columnWidths;

    /// <summary>Requests a rebuild after mutating a property or a non-observable source in place.</summary>
    public void Refresh() => Invalidate();

    // ------------------------------------------------------------ 构建

    protected override Node Build()
    {
        AttachRoot();
        _revision++;
        UseObservableSource();

        var raw = ReadSource();
        GenerateAutoColumns(raw);
        EnsureColumns();
        var display = BuildDisplayItems(raw);
        UpdateDisplayItems(display);

        if (RowCount > 0)
        {
            if (_currentRow < 0 || _currentRow >= RowCount) _currentRow = _selection.SelectedIndex >= 0 ? _selection.SelectedIndex : 0;
            if (_columns.Count > 0 && (_currentColumn < 0 || _currentColumn >= _columns.Count)) _currentColumn = 0;
            if (_columns.Count == 0) _currentColumn = -1;
        }
        else
        {
            _currentRow = -1;
            _currentColumn = _columns.Count > 0 ? 0 : -1;
        }

        ApplySelectionProps();
        ApplySortState();

        double rowHeight = RowHeight > 0 && !double.IsInfinity(RowHeight) ? RowHeight : DefaultRowHeight;
        double headerHeight = ShowsColumnHeaders ? Math.Max(0, HeaderHeight) : 0;
        double rowHeaderWidth = ShowsRowHeaders ? Math.Max(0, RowHeaderWidth) : 0;
        double viewportWidth = _scrollController.ViewportSize.Width;
        double available = viewportWidth > 0 ? Math.Max(0, viewportWidth - rowHeaderWidth) : 0;

        var lengths = new List<DataGridLength>(_columns.Count);
        var autos = new List<double>(_columns.Count);
        foreach (var column in _columns)
        {
            lengths.Add(column.Width ?? ColumnWidth);
            autos.Add(column.Width is { IsAuto: true } || (column.Width is null && ColumnWidth.IsAuto)
                ? EstimateAutoWidth(column, display)
                : 0);
        }
        var widths = DataGridColumnWidths.Allocate(lengths, autos, available);
        for (int i = 0; i < widths.Length; i++)
        {
            var column = _columns[i];
            double width = widths[i];
            if (width < column.MinWidth) width = column.MinWidth;
            if (width > column.MaxWidth) width = column.MaxWidth;
            if (double.IsNaN(width) || double.IsInfinity(width) || width < 0) width = column.MinWidth;
            widths[i] = width;
        }

        _columnWidths.Clear();
        _columnOffsets.Clear();
        double x = rowHeaderWidth;
        for (int i = 0; i < widths.Length; i++)
        {
            _columnWidths.Add(widths[i]);
            _columnOffsets.Add(x);
            _columns[i].ActualWidth = widths[i];
            x += widths[i];
        }
        double totalWidth = x;

        ComputeVisibleColumns(viewportWidth, ref _builtColumnStart, ref _builtColumnEnd);

        double contentHeight = headerHeight + RowCount * rowHeight;
        IReadOnlyList<VirtualItem> rows;
        if (IsVirtualized)
        {
            ConfigureVirtualizer(RowCount, rowHeight, headerHeight);
            SyncVirtualizer();
            _builtRange = _virtualizer.GetRange();
            rows = _virtualizer.GetVirtualItems();
        }
        else
        {
            _builtRange = default;
            rows = Array.Empty<VirtualItem>();
        }

        var extent = new Div
        {
            Style = "position:relative;width:" + Pixels(totalWidth) + ";height:" + Pixels(contentHeight) + ";",
        };
        extent.Attributes["part"] = "extent";

        if (ShowsColumnHeaders)
            extent.Children.Add(BuildHeader(headerHeight, totalWidth, rowHeaderWidth));

        if (IsVirtualized)
        {
            foreach (var item in rows)
            {
                if (item.Index < 0 || item.Index >= RowCount) continue;
                extent.Children.Add(BuildRowComponent(item.Index, item.Start));
            }
        }
        else
        {
            for (int i = 0; i < RowCount; i++)
                extent.Children.Add(BuildRowComponent(i, headerHeight + i * rowHeight));
        }

        var grid = new Div
        {
            Role = Role.Grid,
            TabIndex = IsEnabled ? 0 : null,
            Style = "position:relative;overflow:auto;",
            ScrollController = _scrollController,
            OnKeyDown = HandleKeyDown,
        };
        grid.Children.Add(extent);
        grid.Attributes["aria-rowcount"] = (RowCount + (ShowsColumnHeaders ? 1 : 0)).ToString(CultureInfo.InvariantCulture);
        grid.Attributes["aria-colcount"] = _columns.Count.ToString(CultureInfo.InvariantCulture);
        grid.Attributes["aria-multiselectable"] = SelectionMode == SelectionMode.Single ? null : "true";
        grid.Attributes["aria-readonly"] = IsReadOnly ? "true" : null;
        grid.Attributes["aria-disabled"] = IsEnabled ? null : "true";
        grid.Attributes["data-gridlines"] = GridLines == DataGridGridLines.None ? null : GridLines.ToString().ToLowerInvariant();
        grid.Attributes["data-headers"] = HeadersVisibility.ToString().ToLowerInvariant();

        _builtViewportWidth = viewportWidth;
        _builtViewportHeight = _scrollController.ViewportSize.Height;
        _hasBuilt = true;

        string className = "aw-datagrid";
        if ((GridLines & DataGridGridLines.Horizontal) != 0) className += " aw-datagrid-gridlines-horizontal";
        if ((GridLines & DataGridGridLines.Vertical) != 0) className += " aw-datagrid-gridlines-vertical";
        return Apply(grid, className);
    }

    bool ShowsColumnHeaders => HeadersVisibility == DataGridHeadersVisibility.Column || HeadersVisibility == DataGridHeadersVisibility.All;

    bool ShowsRowHeaders => HeadersVisibility == DataGridHeadersVisibility.Row || HeadersVisibility == DataGridHeadersVisibility.All;

    /// <summary>Builds the realized row component created inside the grid build.</summary>
    internal DataGridRow BuildRowComponent(int index, double top) => new DataGridRow
    {
        Grid = this,
        RowIndex = index,
        Item = index >= 0 && index < _displayItems.Count ? _displayItems[index] : null,
        Top = top,
        IsSelected = _selection.IsSelected(index),
        Revision = _revision,
        Key = index,
    };

    /// <summary>Builds the row element; called from <see cref="DataGridRow.Build"/>.</summary>
    internal Node BuildRowNode(DataGridRow row)
    {
        double rowHeight = RowHeight > 0 && !double.IsInfinity(RowHeight) ? RowHeight : DefaultRowHeight;
        bool selected = row.RowIndex >= 0 && _selection.IsSelected(row.RowIndex);
        var div = new Div
        {
            Key = row.RowIndex,
            Class = "aw-datagrid-row",
            Style = "position:absolute;left:0;top:" + Pixels(row.Top) + ";width:" + Pixels(TotalWidth)
                + ";height:" + Pixels(rowHeight) + ";box-sizing:border-box;",
        };
        div.Role = Role.Row;
        div.Attributes["part"] = "row";
        div.Attributes["aria-rowindex"] = (row.RowIndex + 2).ToString(CultureInfo.InvariantCulture);
        div.Attributes["aria-selected"] = selected ? "true" : "false";
        div.Attributes["data-selected"] = selected ? "true" : "false";

        if (ShowsRowHeaders)
            div.Children.Add(BuildRowHeader(row.RowIndex, RowHeaderWidth));

        for (int column = Math.Max(0, _builtColumnStart); column <= _builtColumnEnd && column < _columns.Count; column++)
        {
            div.Children.Add(new DataGridCell
            {
                Grid = this,
                RowIndex = row.RowIndex,
                ColumnIndex = column,
                Column = _columns[column],
                Item = row.Item,
                Left = _columnOffsets[column],
                Width = _columnWidths[column],
                IsSelected = selected,
                IsCurrent = row.RowIndex == _currentRow && column == _currentColumn,
                IsEditing = _isEditing && row.RowIndex == _editRow && column == _editColumn,
                IsInvalid = _editInvalid && row.RowIndex == _editRow && column == _editColumn,
                Revision = _revision,
                Key = column,
            });
        }
        return div;
    }

    /// <summary>Builds a cell element; called from <see cref="DataGridCell.Build"/>.</summary>
    internal Node BuildCellNode(DataGridCell cell)
    {
        var column = cell.Column;
        Node content = column is null
            ? new UIText("")
            : cell.IsEditing
                ? column.BuildEditor(cell) ?? column.BuildDisplay(cell)
                : column.BuildDisplay(cell);

        var div = new Div
        {
            Key = cell.ColumnIndex,
            Class = "aw-datagrid-cell",
            Style = "position:absolute;top:0;left:" + Pixels(cell.Left) + ";width:" + Pixels(cell.Width)
                + ";height:100%;box-sizing:border-box;overflow:clip;",
            Children = { content },
            OnClick = e => HandleCellClick(cell.RowIndex, cell.ColumnIndex, e),
        };
        div.Role = Role.Cell;
        div.Attributes["part"] = "cell";
        div.Attributes["role"] = "gridcell";
        div.Attributes["aria-colindex"] = (cell.ColumnIndex + 1).ToString(CultureInfo.InvariantCulture);
        div.Attributes["aria-selected"] = cell.IsSelected ? "true" : "false";
        div.Attributes["data-selected"] = cell.IsSelected ? "true" : "false";
        div.Attributes["data-current"] = cell.IsCurrent ? "true" : null;
        div.Attributes["data-editing"] = cell.IsEditing ? "true" : null;
        div.Attributes["data-invalid"] = cell.IsInvalid ? "true" : null;
        div.Attributes["aria-invalid"] = cell.IsInvalid ? "true" : null;
        return div;
    }

    Div BuildHeader(double headerHeight, double totalWidth, double rowHeaderWidth)
    {
        var header = new Div
        {
            Class = "aw-datagrid-header",
            Style = "position:sticky;top:0;left:0;width:" + Pixels(totalWidth) + ";height:" + Pixels(headerHeight)
                + ";box-sizing:border-box;z-index:1;",
        };
        header.Role = Role.Row;
        header.Attributes["part"] = "header";
        header.Attributes["aria-rowindex"] = "1";

        if (ShowsRowHeaders)
        {
            var corner = new Div { Class = "aw-datagrid-corner", Style = CellStyle(0, rowHeaderWidth) };
            corner.Attributes["part"] = "cell";
            corner.Attributes["role"] = "columnheader";
            header.Children.Add(corner);
        }

        for (int i = Math.Max(0, _builtColumnStart); i <= _builtColumnEnd && i < _columns.Count; i++)
            header.Children.Add(BuildHeaderCell(i, headerHeight));
        return header;
    }

    Div BuildHeaderCell(int index, double headerHeight)
    {
        var column = _columns[index];
        var children = new NodeList { column.BuildHeaderContent() };
        if (column.SortDirection == DataGridSortDirection.Ascending)
            children.Add(new Span { Class = "aw-datagrid-sort-indicator", Children = { "▲" } });
        else if (column.SortDirection == DataGridSortDirection.Descending)
            children.Add(new Span { Class = "aw-datagrid-sort-indicator", Children = { "▼" } });

        var cell = new Div
        {
            Class = "aw-datagrid-column-header",
            Style = "position:absolute;top:0;left:" + Pixels(_columnOffsets[index]) + ";width:" + Pixels(_columnWidths[index])
                + ";height:" + Pixels(headerHeight) + ";box-sizing:border-box;overflow:clip;",
            Children = children,
            AriaLabel = column.GetHeaderText(),
        };
        cell.Attributes["part"] = "cell";
        cell.Attributes["role"] = "columnheader";
        cell.Attributes["aria-colindex"] = (index + 1).ToString(CultureInfo.InvariantCulture);
        cell.Attributes["data-sortable"] = CanUserSortColumns && column.CanUserSort && IsEnabled ? "true" : "false";
        cell.Attributes["data-sorted"] = column.SortDirection == DataGridSortDirection.Ascending ? "ascending"
            : column.SortDirection == DataGridSortDirection.Descending ? "descending" : null;
        if (CanUserSortColumns && column.CanUserSort && IsEnabled)
            cell.OnClick = e =>
            {
                e.PreventDefault();
                SortByColumn(index);
            };
        return cell;
    }

    static string CellStyle(double left, double width) =>
        "position:absolute;top:0;left:" + Pixels(left) + ";width:" + Pixels(width) + ";height:100%;box-sizing:border-box;overflow:clip;";

    Div BuildRowHeader(int rowIndex, double width)
    {
        var cell = new Div
        {
            Class = "aw-datagrid-row-header",
            Style = CellStyle(0, width),
            Children = { new UIText((rowIndex + 1).ToString(CultureInfo.InvariantCulture)) },
        };
        cell.Attributes["part"] = "cell";
        cell.Attributes["role"] = "rowheader";
        return cell;
    }

    double TotalWidth
    {
        get
        {
            double total = ShowsRowHeaders ? Math.Max(0, RowHeaderWidth) : 0;
            foreach (var width in _columnWidths) total += width;
            return total;
        }
    }

    // ------------------------------------------------------------ 数据与布局

    List<object?> ReadSource()
    {
        var items = new List<object?>();
        if (ItemsSource is IEnumerable source)
        {
            foreach (var item in source) items.Add(item);
        }
        return items;
    }

    void UseObservableSource()
    {
        UseEffect(() =>
        {
            if (ItemsSource is not INotifyCollectionChanged observable) return null;
            NotifyCollectionChangedEventHandler handler = (_, _) => Invalidate();
            observable.CollectionChanged += handler;
            return () => observable.CollectionChanged -= handler;
        }, ItemsSource);
    }

    void GenerateAutoColumns(List<object?> items)
    {
        Type? itemType = null;
        foreach (var item in items)
        {
            if (item is null) continue;
            itemType = item.GetType();
            break;
        }

        if (!AutoGenerateColumns || itemType is null)
        {
            RemoveAutoColumns();
            return;
        }

        // 父组件每次重建可能传入新的 Columns 列表：清掉已不在活动列表中的自动列，再按需补齐。
        for (int i = _autoColumns.Count - 1; i >= 0; i--)
            if (!Columns.Contains(_autoColumns[i])) _autoColumns.RemoveAt(i);
        if (_autoColumnType != itemType)
        {
            RemoveAutoColumns();
            _autoColumnType = itemType;
        }
        foreach (var property in DataGridBinding.GetBindableProperties(itemType))
        {
            if (HasColumn(property.Name)) continue;
            var column = new DataGridTextColumn
            {
                Header = property.Name,
                Binding = property.Name,
                IsAutoGenerated = true,
            };
            _autoColumns.Add(column);
            Columns.Add(column);
        }
    }

    Type? _autoColumnType;

    void RemoveAutoColumns()
    {
        for (int i = _autoColumns.Count - 1; i >= 0; i--) Columns.Remove(_autoColumns[i]);
        _autoColumns.Clear();
        _autoColumnType = null;
    }

    bool HasColumn(string binding)
    {
        foreach (var column in _autoColumns)
            if (string.Equals(column.Binding, binding, StringComparison.Ordinal) && Columns.Contains(column)) return true;
        foreach (var column in Columns)
            if (!column.IsAutoGenerated && string.Equals(column.Binding, binding, StringComparison.Ordinal)) return true;
        return false;
    }

    void UpdateDisplayItems(List<object?> display)
    {
        if (!SameSequence(_displayItems, display))
            RemapSelectionAfterReorder(_displayItems, display);
        _displayItems.Clear();
        _displayItems.AddRange(display);
        SyncSelectionSource(display.Count);
    }

    void SyncSelectionSource(int count)
    {
        if (_indices.Count != count)
        {
            _indices.Clear();
            for (int i = 0; i < count; i++) _indices.Add(i);
            if (_indices.Count == 0)
            {
                _selection.Clear();
            }
            else
            {
                foreach (var index in _selection.SelectedIndexes)
                    if (index >= count) _selection.Deselect(index);
                if (_selection.SelectedIndex >= count) _selection.SelectedIndex = -1;
            }
        }
        if (!ReferenceEquals(_selection.Source, _indices)) _selection.Source = _indices;
        _selection.SingleSelect = SelectionMode == SelectionMode.Single;
    }

    double EstimateAutoWidth(DataGridColumn column, List<object?> items)
    {
        int longest = column.GetHeaderText().Length;
        int limit = Math.Min(items.Count, 2_000);
        for (int i = 0; i < limit; i++)
        {
            int length = column.GetCellText(items[i]).Length;
            if (length > longest) longest = length;
        }
        double width = longest * 7.5 + 20;
        if (width < 40) width = 40;
        if (width > 400) width = 400;
        return width;
    }

    void EnsureColumns()
    {
        foreach (var old in _columns)
            if (!Columns.Contains(old)) old.Owner = null;
        _columns.Clear();
        foreach (var column in Columns)
        {
            column.Owner = this;
            _columns.Add(column);
        }
    }

    /// <summary>A column width or sort direction changed: rebuild so layout and rows pick it up.</summary>
    internal void OnColumnLayoutChanged() => Invalidate();

    void ConfigureVirtualizer(int count, double rowHeight, double headerHeight)
    {
        _virtualizer.SetOptions(new VirtualizerOptions
        {
            ItemSize = rowHeight,
            Overscan = Math.Max(0, Overscan),
            ScrollMargin = headerHeight,
            ScrollPaddingStart = headerHeight,
            MaxRenderedItems = 10_000,
        });
        _virtualizer.SetCount(count);
    }

    void SyncVirtualizer()
    {
        _virtualizer.ScrollOffset = Math.Max(0, _scrollController.OffsetY);
        _virtualizer.ViewportSize = Math.Max(0, _scrollController.ViewportSize.Height);
    }

    void ComputeVisibleColumns(double viewportWidth, ref int start, ref int end)
    {
        start = 0;
        end = _columns.Count - 1;
        if (_columns.Count == 0) return;
        if (viewportWidth <= 0)
        {
            return;
        }
        double offset = Math.Max(0, _scrollController.OffsetX);
        double right = offset + viewportWidth;
        int first = -1;
        int last = -1;
        for (int i = 0; i < _columns.Count; i++)
        {
            double left = _columnOffsets[i];
            double cellRight = left + _columnWidths[i];
            if (cellRight <= offset || left >= right) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0)
        {
            // 内容比视口窄或刚过末尾：至少渲染一列，避免空行。
            first = last = offset > 0 ? _columns.Count - 1 : 0;
        }
        if (first > 0) first--;
        if (last < _columns.Count - 1) last++;
        start = first;
        end = last;
    }

    bool VisibleColumnsChanged()
    {
        int start = 0, end = 0;
        ComputeVisibleColumns(_scrollController.ViewportSize.Width, ref start, ref end);
        return start != _builtColumnStart || end != _builtColumnEnd;
    }

    static bool SameSequence(List<object?> left, List<object?> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!ReferenceEquals(left[i], right[i]) && !Equals(left[i], right[i])) return false;
        return true;
    }

    // ------------------------------------------------------------ 滚动与生命周期

    void OnScrolled(ScrollEvent e)
    {
        if (!_hasBuilt) return;
        bool changed = false;
        if (IsVirtualized)
        {
            SyncVirtualizer();
            var after = _virtualizer.GetRange();
            changed = after.StartIndex != _builtRange.StartIndex || after.EndIndex != _builtRange.EndIndex
                || after.OverscanStartIndex != _builtRange.OverscanStartIndex || after.OverscanEndIndex != _builtRange.OverscanEndIndex;
        }
        if (changed || VisibleColumnsChanged()) Invalidate();
    }

    void OnLayoutCompleted(UIRoot root)
    {
        if (!ReferenceEquals(root, _root) || !_hasBuilt) return;
        double width = _scrollController.ViewportSize.Width;
        double height = _scrollController.ViewportSize.Height;
        if (Math.Abs(width - _builtViewportWidth) > 0.5 || Math.Abs(height - _builtViewportHeight) > 0.5)
        {
            InvalidateFromLayout();
            return;
        }
        if (IsVirtualized)
        {
            var before = _virtualizer.GetRange();
            SyncVirtualizer();
            var after = _virtualizer.GetRange();
            if (before.StartIndex != after.StartIndex || before.EndIndex != after.EndIndex
                || before.OverscanStartIndex != after.OverscanStartIndex || before.OverscanEndIndex != after.OverscanEndIndex)
            {
                InvalidateFromLayout();
                return;
            }
        }
        if (VisibleColumnsChanged()) InvalidateFromLayout();
    }

    void AttachRoot()
    {
        var root = Root;
        if (ReferenceEquals(root, _root)) return;
        if (_root is not null) _root.LayoutCompleted -= OnLayoutCompleted;
        _root = root;
        if (_root is not null) _root.LayoutCompleted += OnLayoutCompleted;
    }

    protected override void OnUnmount()
    {
        if (_root is not null) _root.LayoutCompleted -= OnLayoutCompleted;
        _root = null;
        base.OnUnmount();
    }

    static string Pixels(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture) + "px";
}

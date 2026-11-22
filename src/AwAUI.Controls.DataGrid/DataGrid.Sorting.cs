using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace AwAUI.Controls.DataGrid;

public sealed partial class DataGrid
{
    DataGridColumn? _sortColumn;

    /// <summary>Builds the displayed row order, applying the current column sort to a copy of the source rows.</summary>
    List<object?> BuildDisplayItems(List<object?> raw)
    {
        var order = new List<int>(raw.Count);
        for (int i = 0; i < raw.Count; i++) order.Add(i);

        var column = ResolveSortColumn();
        if (column is not null && column.SortDirection != DataGridSortDirection.None && raw.Count > 1)
        {
            bool ascending = column.SortDirection == DataGridSortDirection.Ascending;
            var comparer = column.Comparer;
            order.Sort((a, b) =>
            {
                int result = CompareValues(column.GetSortValue(raw[a]), column.GetSortValue(raw[b]), comparer);
                if (result == 0) return a.CompareTo(b); // 稳定排序：相等项保持源顺序（降序也不反转）
                return ascending ? result : -result;
            });
        }

        var display = new List<object?>(raw.Count);
        for (int i = 0; i < order.Count; i++) display.Add(raw[order[i]]);
        return display;
    }

    DataGridColumn? ResolveSortColumn()
    {
        if (_sortColumn is not null && _sortColumn.SortDirection != DataGridSortDirection.None) return _sortColumn;
        _sortColumn = null;
        foreach (var column in _columns)
        {
            if (column.SortDirection == DataGridSortDirection.None) continue;
            _sortColumn = column;
            break;
        }
        return _sortColumn;
    }

    /// <summary>Cycles the header's sort direction: none to ascending, ascending to descending, descending to none.</summary>
    void SortByColumn(int index)
    {
        if (!CanUserSortColumns || !IsEnabled || index < 0 || index >= _columns.Count) return;
        var column = _columns[index];
        if (!column.CanUserSort) return;
        var next = column.SortDirection == DataGridSortDirection.None ? DataGridSortDirection.Ascending
            : column.SortDirection == DataGridSortDirection.Ascending ? DataGridSortDirection.Descending
            : DataGridSortDirection.None;
        foreach (var other in _columns)
            if (!ReferenceEquals(other, column)) other.SortDirection = DataGridSortDirection.None;
        column.SortDirection = next;
        _sortColumn = next == DataGridSortDirection.None ? null : column;
        Invalidate();
    }

    /// <summary>Re-reads declarative sort directions supplied through <see cref="DataGridColumn.SortDirection"/>.</summary>
    void ApplySortState()
    {
        if (_sortColumn is not null && !_columns.Contains(_sortColumn)) _sortColumn = null;
        if (_sortColumn is not null && _sortColumn.SortDirection == DataGridSortDirection.None) _sortColumn = null;
        if (_sortColumn is null)
        {
            foreach (var column in _columns)
            {
                if (column.SortDirection == DataGridSortDirection.None) continue;
                _sortColumn = column;
                break;
            }
        }
    }

    static int CompareValues(object? left, object? right, IComparer? comparer)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        if (comparer is not null)
        {
            try
            {
                return comparer.Compare(left, right);
            }
            catch (Exception)
            {
                // 自定义比较器不能比较这对值时回退到 IComparable / 字符串比较。
            }
        }
        if (left is IComparable comparable)
        {
            try
            {
                return comparable.CompareTo(right);
            }
            catch (ArgumentException)
            {
            }
        }
        if (right is IComparable reverse)
        {
            try
            {
                return -reverse.CompareTo(left);
            }
            catch (ArgumentException)
            {
            }
        }
        return string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }
}

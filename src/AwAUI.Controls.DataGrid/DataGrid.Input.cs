using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.DataGrid;

public sealed partial class DataGrid
{
    void HandleKeyDown(KeyEvent e)
    {
        if (!IsEnabled || e.DefaultPrevented) return;
        string key = e.Key ?? "";
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        bool primary = e.HasPrimaryModifier;

        bool editorFocused = _isEditing && _editRef?.Current is { IsFocused: true };
        if (_isEditing)
        {
            if (key == "Escape")
            {
                e.PreventDefault();
                CancelEdit();
                return;
            }
            if (key == "Tab")
            {
                e.PreventDefault();
                if (CommitEdit()) MoveCell(shift ? -1 : 1);
                return;
            }
            if ((key == "Enter" || key == "NumpadEnter") && !editorFocused)
            {
                e.PreventDefault();
                if (CommitEdit()) MoveCurrent(1, 0, extend: false, keepSelection: false);
                return;
            }
            return;
        }

        if (_currentRow < 0 || RowCount == 0)
        {
            if (key == "Tab") return;
            return;
        }
        if (_currentColumn < 0) _currentColumn = _columns.Count > 0 ? 0 : -1;

        if (primary && (key == "a" || key == "A"))
        {
            if (SelectionMode == SelectionMode.Single) return;
            e.PreventDefault();
            ChangeSelection(() =>
            {
                _selection.SingleSelect = false;
                _selection.SelectAll();
            });
            return;
        }

        if (key == "Tab")
        {
            e.PreventDefault();
            MoveCell(shift ? -1 : 1);
            return;
        }

        if (key == "F2")
        {
            e.PreventDefault();
            BeginEdit();
            return;
        }

        if (key == "Escape")
        {
            if (_isEditing) CancelEdit();
            return;
        }

        if (key == " " || key == "Spacebar")
        {
            e.PreventDefault();
            int row = _currentRow;
            int column = _currentColumn;
            if (column >= 0 && column < _columns.Count && _columns[column] is DataGridCheckBoxColumn && CanEditCell(row, column))
            {
                ToggleCheckBoxCell(row, column);
                return;
            }
            if (SelectionMode == SelectionMode.Single)
                ChangeSelection(() => SelectSingle(row));
            else
                ChangeSelection(() => SelectionGestures.Update(_selection, row, PrimitiveSelectionMode,
                    range: false, toggle: true, rightButton: false, fromFocus: false));
            return;
        }

        if (key == "Enter" || key == "NumpadEnter")
        {
            e.PreventDefault();
            int row = _currentRow;
            int column = _currentColumn;
            if (column >= 0 && column < _columns.Count && _columns[column] is DataGridCheckBoxColumn && CanEditCell(row, column))
            {
                ToggleCheckBoxCell(row, column);
                return;
            }
            if (CanEditCell(row, column))
            {
                BeginEdit();
                return;
            }
            MoveCurrent(1, 0, extend: false, keepSelection: false);
            return;
        }

        int deltaRow = 0;
        int deltaColumn = 0;
        switch (key)
        {
            case "ArrowUp":
                deltaRow = -1;
                break;
            case "ArrowDown":
                deltaRow = 1;
                break;
            case "ArrowLeft":
                deltaColumn = -1;
                break;
            case "ArrowRight":
                deltaColumn = 1;
                break;
            case "Home":
                e.PreventDefault();
                MoveCurrentTo(primary ? 0 : _currentRow, 0, extend: shift, keepSelection: primary && !shift);
                return;
            case "End":
                e.PreventDefault();
                MoveCurrentTo(primary ? RowCount - 1 : _currentRow,
                    Math.Max(0, _columns.Count - 1), extend: shift, keepSelection: primary && !shift);
                return;
            case "PageUp":
                deltaRow = -PageSize;
                break;
            case "PageDown":
                deltaRow = PageSize;
                break;
            default:
                return;
        }

        e.PreventDefault();
        MoveCurrent(deltaRow, deltaColumn, extend: shift, keepSelection: primary && !shift);
    }

    int PageSize
    {
        get
        {
            double rowHeight = RowHeight > 0 && !double.IsInfinity(RowHeight) ? RowHeight : 32;
            double height = _scrollController.ViewportSize.Height;
            int size = height > 0 ? (int)(height / rowHeight) : 1;
            return Math.Max(1, size);
        }
    }

    void MoveCell(int direction)
    {
        if (_currentRow < 0 || _columns.Count == 0) return;
        int row = _currentRow;
        int column = _currentColumn < 0 ? 0 : _currentColumn;
        int next = column + direction;
        if (next < 0)
        {
            if (row > 0)
            {
                row--;
                next = _columns.Count - 1;
            }
            else
            {
                next = 0;
            }
        }
        else if (next >= _columns.Count)
        {
            if (row < RowCount - 1)
            {
                row++;
                next = 0;
            }
            else
            {
                next = _columns.Count - 1;
            }
        }
        MoveCurrentTo(row, next, extend: false, keepSelection: false);
    }

    void MoveCurrent(int deltaRow, int deltaColumn, bool extend, bool keepSelection)
    {
        if (RowCount == 0) return;
        int row = Math.Max(0, Math.Min(RowCount - 1, _currentRow + deltaRow));
        int column = _columns.Count == 0 ? -1 : Math.Max(0, Math.Min(_columns.Count - 1, _currentColumn + deltaColumn));
        MoveCurrentTo(row, column, extend, keepSelection);
    }

    void MoveCurrentTo(int row, int column, bool extend, bool keepSelection)
    {
        if (RowCount == 0) return;
        row = Math.Max(0, Math.Min(RowCount - 1, row));
        if (_columns.Count == 0) column = -1;
        else column = Math.Max(0, Math.Min(_columns.Count - 1, column));

        bool rowChanged = row != _currentRow;
        int previousRow = _currentRow;
        _currentRow = row;
        _currentColumn = column;
        if (rowChanged) ApplyRowNavigation(row, previousRow, extend, keepSelection);
        EnsureCellVisible(row, column);
        Invalidate();
    }

    void ApplyRowNavigation(int row, int previousRow, bool extend, bool keepSelection)
    {
        if (SelectionMode == SelectionMode.Single)
        {
            if (!keepSelection) ChangeSelection(() => SelectSingle(row));
            return;
        }
        if (extend)
        {
            int anchor = _selection.AnchorIndex >= 0 ? _selection.AnchorIndex : Math.Max(0, previousRow);
            ChangeSelection(() =>
            {
                _selection.SingleSelect = false;
                _selection.Clear();
                _selection.SelectRange(anchor, row);
                _selection.AnchorIndex = anchor;
            });
            return;
        }
        if (keepSelection || SelectionMode == SelectionMode.Multiple) return;
        ChangeSelection(() =>
        {
            _selection.SingleSelect = false;
            _selection.SelectedIndex = row;
        });
    }

    void EnsureCellVisible(int row, int column)
    {
        double x = _scrollController.OffsetX;
        double y = _scrollController.OffsetY;
        double viewportWidth = _scrollController.ViewportSize.Width;
        double viewportHeight = _scrollController.ViewportSize.Height;
        double rowHeight = RowHeight > 0 && !double.IsInfinity(RowHeight) ? RowHeight : 32;
        double headerHeight = ShowsColumnHeaders ? Math.Max(0, HeaderHeight) : 0;

        if (row >= 0 && viewportHeight > 0)
        {
            double top = headerHeight + row * rowHeight;
            double bottom = top + rowHeight;
            if (top < y) y = top;
            else if (bottom > y + viewportHeight) y = bottom - viewportHeight;
        }
        if (column >= 0 && column < _columnOffsets.Count && viewportWidth > 0)
        {
            double left = _columnOffsets[column];
            double right = left + _columnWidths[column];
            if (left < x) x = left;
            else if (right > x + viewportWidth) x = right - viewportWidth;
        }
        if (Math.Abs(x - _scrollController.OffsetX) > 0.5 || Math.Abs(y - _scrollController.OffsetY) > 0.5)
            _scrollController.JumpTo(x, y);
    }

    void HandleCellClick(int rowIndex, int columnIndex, PointerEvent e)
    {
        if (!IsEnabled || e.DefaultPrevented) return;
        if (_isEditing && rowIndex == _editRow && columnIndex == _editColumn) return;
        if (_isEditing && !CommitEdit()) return;
        if (rowIndex < 0 || rowIndex >= RowCount || columnIndex < 0 || columnIndex >= _columns.Count) return;

        FocusGrid(e.CurrentTarget);
        _currentRow = rowIndex;
        _currentColumn = columnIndex;
        ChangeSelection(() => SelectionGestures.PointerPressed(_selection, rowIndex, e.Modifiers, PrimitiveSelectionMode));

        var column = _columns[columnIndex];
        if (column is DataGridCheckBoxColumn && CanEditCell(rowIndex, columnIndex))
            ToggleCheckBoxCell(rowIndex, columnIndex);
        else
            Invalidate();
    }

    static void FocusGrid(Element? target)
    {
        for (var element = target; element is not null; element = element.Parent)
        {
            if (!element.IsFocusable) continue;
            if (string.Equals(element.GetAttribute("role"), "grid", StringComparison.OrdinalIgnoreCase))
            {
                element.Focus(new FocusOptions { Origin = FocusOrigin.Pointer });
                return;
            }
        }
    }
}

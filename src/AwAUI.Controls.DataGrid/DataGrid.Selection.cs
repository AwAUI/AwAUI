using System;
using System.Collections.Generic;
using AWSelectionMode = AwAUI.Controls.Primitives.SelectionMode;

namespace AwAUI.Controls.DataGrid;

public sealed partial class DataGrid
{
    object? _requestedSelectionItem;
    int _requestedSelectionIndex = -1;
    bool _hasRequestedSelectionItem;
    bool _hasRequestedSelectionIndex;

    /// <summary>
    /// When true, <see cref="SelectedItem"/> / <see cref="SelectedIndex"/> are applied on every build, including an
    /// empty selection. Otherwise the first non-null value is applied once and the grid manages the selection itself.
    /// </summary>
    public bool IsSelectionControlled { get; set; }

    /// <summary>
    /// The selected row object, or null. Assigning a non-null object selects that row (once, unless
    /// <see cref="IsSelectionControlled"/> is set); the getter always reports the current selection.
    /// </summary>
    public object? SelectedItem
    {
        get => _hasRequestedSelectionItem ? _requestedSelectionItem : CurrentSelectedItem;
        set
        {
            if (value is null && !IsSelectionControlled) return;
            _requestedSelectionItem = value;
            _hasRequestedSelectionItem = true;
            // SelectedIndex and SelectedItem are two views of the same selection; the item wins when both are supplied.
            _hasRequestedSelectionIndex = false;
            Invalidate();
        }
    }

    /// <summary>
    /// The primary selected row index (display order), or -1. Assigning a non-negative index selects that row;
    /// the getter always reports the current selection.
    /// </summary>
    public int SelectedIndex
    {
        get => _hasRequestedSelectionIndex ? _requestedSelectionIndex : _selection.SelectedIndex;
        set
        {
            if (value < 0 && !IsSelectionControlled) return;
            _requestedSelectionIndex = value;
            _hasRequestedSelectionIndex = true;
            _hasRequestedSelectionItem = false;
            Invalidate();
        }
    }

    /// <summary>The selected row objects, in display order.</summary>
    public IReadOnlyList<object?> SelectedItems => SnapshotSelection();

    /// <summary>The primary selected row index, or -1 (read-only view of <see cref="SelectedIndex"/>).</summary>
    public int CurrentSelectedIndex => _selection.SelectedIndex;

    /// <summary>The primary selected row object, or null (read-only view of <see cref="SelectedItem"/>).</summary>
    public object? CurrentSelectedItem
    {
        get
        {
            int index = _selection.SelectedIndex;
            return index >= 0 && index < _displayItems.Count ? _displayItems[index] : null;
        }
    }

    /// <summary>Reports a change to the primary selected row index.</summary>
    public Action<int>? SelectedIndexChanged { get; set; }

    /// <summary>Reports every selection change caused by pointer or keyboard gestures.</summary>
    public Action<DataGridSelectionChangedEventArgs>? SelectionChanged { get; set; }

    /// <summary>Clears the selection (also usable in uncontrolled mode, where assigning -1 is a no-op).</summary>
    public void ClearSelection() => ChangeSelection(() => _selection.Clear());

    /// <summary>Selects every row in the multi-select modes; does nothing in <see cref="SelectionMode.Single"/>.</summary>
    public void SelectAll()
    {
        if (SelectionMode == SelectionMode.Single || RowCount == 0) return;
        ChangeSelection(() =>
        {
            _selection.SingleSelect = false;
            _selection.SelectAll();
        });
    }

    AWSelectionMode PrimitiveSelectionMode
    {
        get
        {
            switch (SelectionMode)
            {
                case SelectionMode.Single:
                    return AWSelectionMode.Single;
                case SelectionMode.Multiple:
                    return AWSelectionMode.Multiple | AWSelectionMode.Toggle;
                default:
                    return AWSelectionMode.Multiple;
            }
        }
    }

    void ApplySelectionProps()
    {
        if (IsSelectionControlled)
        {
            if (_requestedSelectionItem is not null)
            {
                int index = IndexOfItem(_requestedSelectionItem);
                if (index >= 0) SelectSingle(index);
                else _selection.Clear();
            }
            else if (_requestedSelectionIndex >= 0 && _requestedSelectionIndex < RowCount)
            {
                SelectSingle(_requestedSelectionIndex);
            }
            else if (_requestedSelectionItem is null && _requestedSelectionIndex < 0)
            {
                _selection.Clear();
            }
            if (_hasRequestedSelectionItem || _hasRequestedSelectionIndex)
                _currentRow = _selection.SelectedIndex;
        }
        else
        {
            if (_hasRequestedSelectionItem)
            {
                int index = _requestedSelectionItem is null ? -1 : IndexOfItem(_requestedSelectionItem);
                if (index >= 0) SelectSingle(index);
            }
            else if (_hasRequestedSelectionIndex && _requestedSelectionIndex >= 0 && _requestedSelectionIndex < RowCount)
            {
                SelectSingle(_requestedSelectionIndex);
            }
        }
        _hasRequestedSelectionItem = false;
        _hasRequestedSelectionIndex = false;
    }

    void SelectSingle(int index)
    {
        if (index < 0 || index >= RowCount) return;
        _selection.SingleSelect = SelectionMode == SelectionMode.Single;
        _selection.SelectedIndex = index;
        _currentRow = index;
    }

    int IndexOfItem(object? item)
    {
        for (int i = 0; i < _displayItems.Count; i++)
            if (ReferenceEquals(_displayItems[i], item) || Equals(_displayItems[i], item)) return i;
        return -1;
    }

    IReadOnlyList<object?> SnapshotSelection()
    {
        var items = new List<object?>();
        foreach (var index in _selection.SelectedIndexes)
            if (index >= 0 && index < _displayItems.Count) items.Add(_displayItems[index]);
        return items;
    }

    List<int> SelectionIndexes()
    {
        var indexes = new List<int>();
        foreach (var index in _selection.SelectedIndexes) indexes.Add(index);
        return indexes;
    }

    void ChangeSelection(Action change)
    {
        var beforeIndexes = SelectionIndexes();
        int beforePrimary = _selection.SelectedIndex;
        change();
        var afterIndexes = SelectionIndexes();
        bool changed = beforeIndexes.Count != afterIndexes.Count;
        if (!changed)
        {
            for (int i = 0; i < beforeIndexes.Count; i++)
                if (beforeIndexes[i] != afterIndexes[i]) { changed = true; break; }
        }
        if (!changed) return;

        var before = new HashSet<int>(beforeIndexes);
        var after = new HashSet<int>(afterIndexes);
        var added = new List<object?>();
        var removed = new List<object?>();
        foreach (var index in afterIndexes)
            if (!before.Contains(index) && index >= 0 && index < _displayItems.Count) added.Add(_displayItems[index]);
        foreach (var index in beforeIndexes)
            if (!after.Contains(index) && index >= 0 && index < _displayItems.Count) removed.Add(_displayItems[index]);

        SelectionChanged?.Invoke(new DataGridSelectionChangedEventArgs(SnapshotSelection(), added, removed, _selection.SelectedIndex));
        if (beforePrimary != _selection.SelectedIndex) SelectedIndexChanged?.Invoke(_selection.SelectedIndex);
        Invalidate();
    }

    /// <summary>Re-selects the same row objects after a sort (indices move with the rows, not with positions).</summary>
    void RemapSelectionAfterReorder(List<object?> oldDisplay, List<object?> newDisplay)
    {
        if (oldDisplay.Count == 0 || newDisplay.Count == 0) return;
        var selected = new List<object?>();
        foreach (var index in _selection.SelectedIndexes)
            if (index >= 0 && index < oldDisplay.Count) selected.Add(oldDisplay[index]);
        object? anchor = _selection.AnchorIndex >= 0 && _selection.AnchorIndex < oldDisplay.Count
            ? oldDisplay[_selection.AnchorIndex] : null;
        object? current = _currentRow >= 0 && _currentRow < oldDisplay.Count ? oldDisplay[_currentRow] : null;
        if (selected.Count == 0 && anchor is null && current is null) return;

        _selection.SingleSelect = false;
        _selection.Clear();
        foreach (var item in selected)
        {
            int index = IndexOfItemIn(newDisplay, item);
            if (index >= 0) _selection.Select(index);
        }
        _selection.SingleSelect = SelectionMode == SelectionMode.Single;
        if (anchor is not null)
        {
            int anchorIndex = IndexOfItemIn(newDisplay, anchor);
            if (anchorIndex >= 0) _selection.AnchorIndex = anchorIndex;
        }
        if (current is not null)
        {
            int currentIndex = IndexOfItemIn(newDisplay, current);
            if (currentIndex >= 0) _currentRow = currentIndex;
        }
    }

    static int IndexOfItemIn(List<object?> items, object? item)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item) || Equals(items[i], item)) return i;
        return -1;
    }
}

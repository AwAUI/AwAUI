using System;
using System.Collections.Generic;

namespace AwAUI.Controls.DataGrid;

/// <summary>How the rows of a <see cref="DataGrid"/> react to selection gestures.</summary>
public enum SelectionMode
{
    /// <summary>One row at a time; every gesture replaces the selection.</summary>
    Single = 0,

    /// <summary>Clicking a row toggles it; Shift extends a range and the primary modifier toggles.</summary>
    Multiple = 1,

    /// <summary>Clicking a row selects only it; Shift extends a range and the primary modifier toggles.</summary>
    Extended = 2,
}

/// <summary>The sort direction of a column.</summary>
public enum DataGridSortDirection
{
    /// <summary>Not sorted; the source order is preserved.</summary>
    None = 0,

    /// <summary>Smallest value first.</summary>
    Ascending = 1,

    /// <summary>Largest value first; equal values keep their source order.</summary>
    Descending = 2,
}

/// <summary>Which separator lines are drawn between rows and columns.</summary>
public enum DataGridGridLines
{
    /// <summary>No grid lines.</summary>
    None = 0,

    /// <summary>Horizontal lines between rows.</summary>
    Horizontal = 1,

    /// <summary>Vertical lines between columns.</summary>
    Vertical = 2,

    /// <summary>Both horizontal and vertical lines.</summary>
    All = 3,
}

/// <summary>Which headers a <see cref="DataGrid"/> displays.</summary>
public enum DataGridHeadersVisibility
{
    /// <summary>No headers.</summary>
    None = 0,

    /// <summary>Column headers only.</summary>
    Column = 1,

    /// <summary>Row headers only.</summary>
    Row = 2,

    /// <summary>Both row and column headers.</summary>
    All = 3,
}

/// <summary>Describes a selection change reported by <see cref="DataGrid.SelectionChanged"/>.</summary>
public sealed class DataGridSelectionChangedEventArgs : EventArgs
{
    internal DataGridSelectionChangedEventArgs(
        IReadOnlyList<object?> selectedItems,
        IReadOnlyList<object?> addedItems,
        IReadOnlyList<object?> removedItems,
        int selectedIndex)
    {
        SelectedItems = selectedItems;
        AddedItems = addedItems;
        RemovedItems = removedItems;
        SelectedIndex = selectedIndex;
    }

    /// <summary>The selected items after the change, in row order.</summary>
    public IReadOnlyList<object?> SelectedItems { get; }

    /// <summary>The items added by this change.</summary>
    public IReadOnlyList<object?> AddedItems { get; }

    /// <summary>The items removed by this change.</summary>
    public IReadOnlyList<object?> RemovedItems { get; }

    /// <summary>The primary selected row index, or -1 when nothing is selected.</summary>
    public int SelectedIndex { get; }
}

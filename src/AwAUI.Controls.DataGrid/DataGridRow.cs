using AwAUI.UI;

namespace AwAUI.Controls.DataGrid;

/// <summary>
/// One realized row of a <see cref="DataGrid"/>. The grid creates these only for the rows in and near the viewport;
/// <see cref="RowIndex"/> is the position in the displayed (sorted) row order.
/// </summary>
public sealed partial class DataGridRow : Component
{
    internal DataGrid Grid { get; set; } = null!;

    /// <summary>The displayed row index (0-based).</summary>
    public int RowIndex { get; set; }

    /// <summary>The row object.</summary>
    public object? Item { get; set; }

    /// <summary>The absolute vertical position inside the scroll content, in logical pixels.</summary>
    public double Top { get; set; }

    /// <summary>Whether the row is selected.</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// Changes on every grid build so the coordinator rebuilds realized rows even when the row object is unchanged
    /// (editing and selection state is not part of the row's data).
    /// </summary>
    public long Revision { get; set; }

    protected override Node Build() => Grid.BuildRowNode(this);
}

/// <summary>One cell of a realized <see cref="DataGrid"/> row.</summary>
public sealed partial class DataGridCell : Component
{
    internal DataGrid Grid { get; set; } = null!;

    /// <summary>The displayed row index (0-based).</summary>
    public int RowIndex { get; set; }

    /// <summary>The column index (0-based).</summary>
    public int ColumnIndex { get; set; }

    /// <summary>The column description.</summary>
    public DataGridColumn? Column { get; set; }

    /// <summary>The row object.</summary>
    public object? Item { get; set; }

    /// <summary>The absolute horizontal position inside the scroll content, in logical pixels.</summary>
    public double Left { get; set; }

    /// <summary>The assigned column width, in logical pixels.</summary>
    public double Width { get; set; }

    /// <summary>Whether the row is selected.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Whether this cell is the grid's current cell.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Whether this cell is in edit mode.</summary>
    public bool IsEditing { get; set; }

    /// <summary>Whether the last edit attempt failed and marked this cell invalid.</summary>
    public bool IsInvalid { get; set; }

    /// <summary>Changes on every grid build so the coordinator rebuilds realized cells.</summary>
    public long Revision { get; set; }

    protected override Node Build() => Grid.BuildCellNode(this);
}

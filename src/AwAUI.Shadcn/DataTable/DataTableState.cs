using System;

namespace AwAUI.Shadcn;

/// <summary>Built-in comparison used by <see cref="DataTableFilterFn"/>.</summary>
public enum DataTableFilterKind : byte
{
    /// <summary>Case-insensitive substring match.</summary>
    Contains,
    /// <summary>Case-insensitive whole-text match.</summary>
    Equals,
    /// <summary>Case-insensitive prefix match.</summary>
    StartsWith,
    /// <summary>User-supplied predicate over the cell value.</summary>
    Custom,
}

/// <summary>
/// Per-column filter comparison for <see cref="DataTableColumn{T}.FilterFn"/>.
/// <para>
/// The built-in values are <see cref="Contains"/>, <see cref="Equals"/> and <see cref="StartsWith"/>.
/// <see cref="Custom"/> wraps a delegate that receives the cell value (the column's
/// <c>FilterValue</c> or <c>Value</c>) and the filter text. Use
/// <see cref="DataTableColumn{T}.Filter"/> instead when the predicate needs the whole row.
/// </para>
/// </summary>
public sealed class DataTableFilterFn
{
    DataTableFilterFn(DataTableFilterKind kind, Func<object?, string, bool>? predicate)
    {
        Kind = kind;
        Predicate = predicate;
    }

    /// <summary>Case-insensitive substring match (the default).</summary>
    public static DataTableFilterFn Contains { get; } = new(DataTableFilterKind.Contains, null);

    /// <summary>Case-insensitive whole-text match.</summary>
    public static new DataTableFilterFn Equals { get; } = new(DataTableFilterKind.Equals, null);

    /// <summary>Case-insensitive prefix match.</summary>
    public static DataTableFilterFn StartsWith { get; } = new(DataTableFilterKind.StartsWith, null);

    /// <summary>Creates a custom comparison over the cell value and the filter text.</summary>
    public static DataTableFilterFn Custom(Func<object?, string, bool> predicate) =>
        new(DataTableFilterKind.Custom, predicate ?? throw new ArgumentNullException(nameof(predicate)));

    /// <summary>The built-in comparison in use.</summary>
    public DataTableFilterKind Kind { get; }

    /// <summary>The custom predicate, or null for the built-in comparisons.</summary>
    public Func<object?, string, bool>? Predicate { get; }

    /// <summary>Tests a formatted cell value against the filter text.</summary>
    public bool IsMatch(object? value, string filter)
    {
        string text = DataTableValueComparer.ToText(value);
        return Kind switch
        {
            DataTableFilterKind.Contains => text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0,
            DataTableFilterKind.Equals => string.Equals(text, filter, StringComparison.OrdinalIgnoreCase),
            DataTableFilterKind.StartsWith => text.StartsWith(filter, StringComparison.OrdinalIgnoreCase),
            DataTableFilterKind.Custom => Predicate is not null && Predicate(value, filter),
            _ => false,
        };
    }

    /// <summary>True when the comparison is a custom delegate.</summary>
    public bool IsCustom => Kind == DataTableFilterKind.Custom;
}

/// <summary>One active sort of a <see cref="DataTableModel{T}"/>.</summary>
public readonly struct DataTableSortDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    public DataTableSortDescriptor(string columnKey, DataTableSortDirection direction, int precedence)
    {
        ColumnKey = columnKey;
        Direction = direction;
        Precedence = precedence;
    }

    /// <summary>Key of the sorted column.</summary>
    public string ColumnKey { get; }

    /// <summary>Ascending or descending (never <see cref="DataTableSortDirection.None"/>).</summary>
    public DataTableSortDirection Direction { get; }

    /// <summary>Zero-based precedence; zero is the primary sort.</summary>
    public int Precedence { get; }
}

/// <summary>Scope of the header select-all affordance of <see cref="DataTable{T}"/>.</summary>
public enum DataTableSelectionScope : byte
{
    /// <summary>Select every row that survives the current filters.</summary>
    Global,
    /// <summary>Select only the rows on the current page.</summary>
    Page,
}

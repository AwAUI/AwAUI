using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Horizontal alignment of a data table column.</summary>
public enum DataTableColumnAlign : byte
{
    /// <summary>Leading edge (left in left-to-right writing systems).</summary>
    Start,
    /// <summary>Centered.</summary>
    Center,
    /// <summary>Trailing edge (right in left-to-right writing systems).</summary>
    End,
}

/// <summary>The active sort direction of a data table.</summary>
public enum DataTableSortDirection : byte
{
    /// <summary>Source order.</summary>
    None,
    /// <summary>Smallest value first.</summary>
    Ascending,
    /// <summary>Largest value first.</summary>
    Descending,
}

/// <summary>
/// One column of a <see cref="DataTableModel{T}"/> / <see cref="DataTable{T}"/>.
/// <para>
/// <see cref="Value"/> is the cell accessor and the default source for sorting and filtering.
/// Set <see cref="Cell"/> to render a custom node, <see cref="HeaderRenderer"/> to render a custom header
/// (the header is not passed the row), and <see cref="SortValue"/> when the sort key differs from the displayed value.
/// </para>
/// </summary>
public sealed class DataTableColumn<T>
{
    /// <summary>Stable identifier used by sorting state and by <c>data-column-key</c>.</summary>
    public string Key { get; set; } = "";

    /// <summary>Text shown in the header. Defaults to <see cref="Key"/> when neither header text nor renderer is set.</summary>
    public string? Header { get; set; }

    /// <summary>Custom header content. Takes precedence over <see cref="Header"/>.</summary>
    public Func<Node>? HeaderRenderer { get; set; }

    /// <summary>Custom cell content. Takes precedence over <see cref="Value"/>.</summary>
    public Func<T, Node>? Cell { get; set; }

    /// <summary>Cell value. Rendered as text when <see cref="Cell"/> is null, and used by sorting and filtering.</summary>
    public Func<T, object?>? Value { get; set; }

    bool _enableSorting;

    /// <summary>
    /// When true, the header becomes a button that toggles the sort. Alias of <see cref="EnableSorting"/>;
    /// both names address the same flag.
    /// </summary>
    public bool Sortable
    {
        get => _enableSorting;
        set => _enableSorting = value;
    }

    /// <summary>TanStack-style spelling of <see cref="Sortable"/>; both names address the same flag.</summary>
    public bool EnableSorting
    {
        get => _enableSorting;
        set => _enableSorting = value;
    }

    /// <summary>
    /// Whether the column is rendered. Defaults to true. The effective value can also be changed
    /// per model through <see cref="DataTableModel{T}.SetColumnVisibility"/>.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// When false, the model refuses to hide this column; <see cref="DataTableModel{T}.ToggleColumnVisibility"/>
    /// and <see cref="DataTableModel{T}.SetColumnVisibility"/> become no-ops for hiding it. Defaults to true.
    /// </summary>
    public bool EnableHiding { get; set; } = true;

    /// <summary>
    /// Comparison used when this column participates in a per-column filter. Defaults to
    /// <see cref="DataTableFilterFn.Contains"/> when null and <see cref="Filter"/> is not set.
    /// </summary>
    public DataTableFilterFn? FilterFn { get; set; }

    /// <summary>Arbitrary application metadata for the column (TanStack <c>columnDef.meta</c>).</summary>
    public object? Meta { get; set; }

    /// <summary>Sort key when it differs from <see cref="Value"/>.</summary>
    public Func<T, object?>? SortValue { get; set; }

    /// <summary>Comparator for the sort key. Defaults to <see cref="DataTableValueComparer.Instance"/>.</summary>
    public IComparer<object?>? SortComparer { get; set; }

    /// <summary>Row comparison used instead of the key selector (for multi-field or custom sorting).</summary>
    public Comparison<T>? SortComparison { get; set; }

    /// <summary>Custom filter predicate receiving the row and the global filter text. Takes precedence over <see cref="FilterValue"/>.</summary>
    public Func<T, string, bool>? Filter { get; set; }

    /// <summary>Value matched against the global filter. Defaults to <see cref="Value"/>.</summary>
    public Func<T, object?>? FilterValue { get; set; }

    /// <summary>Text alignment of the header and cells.</summary>
    public DataTableColumnAlign Align { get; set; }

    /// <summary>CSS grid track size for the column, for example <c>12rem</c> or <c>25%</c>. Defaults to <c>minmax(0, 1fr)</c>.</summary>
    public string? Width { get; set; }

    /// <summary>Extra classes for the column's cells.</summary>
    public string? Class { get; set; }

    /// <summary>Extra classes for the header cell.</summary>
    public string? HeaderClass { get; set; }
}

/// <summary>
/// Default value comparer for data tables: numbers compare numerically, strings compare ordinally with
/// digit runs compared by value (<c>item2</c> before <c>item10</c>), and other comparable values use
/// <see cref="IComparable"/>. Null sorts before every non-null value.
/// </summary>
public sealed class DataTableValueComparer : IComparer<object?>
{
    /// <summary>Shared instance.</summary>
    public static readonly DataTableValueComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (IsNumeric(x) && IsNumeric(y))
        {
            try
            {
                return Convert.ToDecimal(x, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDecimal(y, CultureInfo.InvariantCulture));
            }
            catch (InvalidCastException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }

        if (x is string sx && y is string sy) return CompareNatural(sx, sy);

        if (x is IComparable comparable)
        {
            try
            {
                return comparable.CompareTo(y);
            }
            catch (ArgumentException) { }
        }

        return string.CompareOrdinal(ToText(x), ToText(y));
    }

    /// <summary>Ordinal comparison that compares consecutive digit runs by numeric value.</summary>
    public static int CompareNatural(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i];
            char cy = y[j];
            if (cx >= '0' && cx <= '9' && cy >= '0' && cy <= '9')
            {
                int startX = i;
                while (i < x.Length && x[i] >= '0' && x[i] <= '9') i++;
                int startY = j;
                while (j < y.Length && y[j] >= '0' && y[j] <= '9') j++;

                int sigX = startX;
                while (sigX < i - 1 && x[sigX] == '0') sigX++;
                int sigY = startY;
                while (sigY < j - 1 && y[sigY] == '0') sigY++;

                int lengthX = i - sigX;
                int lengthY = j - sigY;
                if (lengthX != lengthY) return lengthX < lengthY ? -1 : 1;
                for (int k = 0; k < lengthX; k++)
                {
                    int difference = x[sigX + k] - y[sigY + k];
                    if (difference != 0) return difference < 0 ? -1 : 1;
                }
                continue;
            }

            if (cx != cy) return cx < cy ? -1 : 1;
            i++;
            j++;
        }

        int remainingX = x.Length - i;
        int remainingY = y.Length - j;
        return remainingX == remainingY ? 0 : remainingX < remainingY ? -1 : 1;
    }

    /// <summary>Formats a cell value for display.</summary>
    public static string ToText(object? value)
    {
        if (value is null) return "";
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";
        return value.ToString() ?? "";
    }

    static bool IsNumeric(object value) =>
        value is byte || value is sbyte || value is short || value is ushort ||
        value is int || value is uint || value is long || value is ulong ||
        value is float || value is double || value is decimal;
}

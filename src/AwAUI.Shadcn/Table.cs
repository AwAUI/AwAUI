using System;
using System.Globalization;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>
/// Responsive native table and its semantic parts.
/// <para>
/// Why this is not a native table layout: AwAUI's layout engine has no table formatting context. The UA
/// stylesheet gives <c>table</c>, <c>thead</c>, <c>tbody</c>, <c>tfoot</c>, <c>th</c>, <c>td</c> and
/// <c>caption</c> no <c>display</c> (their computed value is the CSS initial <c>inline</c>), and
/// <c>LayoutMapper.MapDisplay</c> maps <c>display: table</c> / <c>table-row-group</c> / <c>table-cell</c> /
/// <c>table-caption</c> to a plain block box while <c>table-row</c> becomes a flex row. Nothing shares column
/// widths between rows, there is no <c>colspan</c> / <c>rowspan</c> geometry and <c>caption-side</c> is never
/// applied by layout. Switching to native table display would therefore collapse (or misalign) the table
/// instead of improving it, so the native elements are kept for semantics and the visual table is approximated:
/// every part carries an explicit display utility (<c>block</c> / <c>grid</c> / <c>flex</c>), each
/// <see cref="TableRow"/> is its own grid with <c>auto-cols-fr</c> plus the optional
/// <c>--aw-table-columns</c> track list from <see cref="Table.Columns"/>, and a cell <c>colspan</c> becomes
/// <c>grid-column: span N</c> so the cells of that row line up. A <c>rowspan</c> cannot cross the independent
/// row grids; it is forwarded as an attribute for assistive technology only. <c>caption-bottom</c> is emitted
/// for class parity, but the caption is laid out in DOM order (before the rows), where the shadcn examples put it.
/// </para>
/// </summary>
public sealed partial class Table : Component
{
    const string RootClass = "block w-full caption-bottom text-sm";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    /// <summary>
    /// Grid track list for every row (<c>grid-template-columns</c>), for example <c>"200px 1fr"</c> or
    /// <c>"repeat(3, minmax(0, 1fr))"</c>. A bare integer (<c>"3"</c>) is expanded to
    /// <c>repeat(3, minmax(0, 1fr))</c>. Left unset, rows size their columns with <c>auto-cols-fr</c>.
    /// </summary>
    public string? Columns { get; set; }

    protected override Node Build()
    {
        var table = TableParts.Build("table", "table", RootClass, Class, Children, Rest);
        if (!string.IsNullOrWhiteSpace(Columns))
        {
            table.Style = new StyleValue("--aw-table-columns: " + TableParts.TrackList(Columns!) + "; " + table.Style.Text);
        }
        var container = new Div { Class = "relative w-full overflow-x-auto", Children = [table] };
        container.Attributes["data-slot"] = "table-container";
        return container;
    }
}

public sealed partial class TableHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => TableParts.Build("thead", "table-header", "block [&_tr]:border-b", Class, Children, Rest);
}

public sealed partial class TableBody : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => TableParts.Build("tbody", "table-body", "block [&_tr:last-child]:border-0", Class, Children, Rest);
}

public sealed partial class TableFooter : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => TableParts.Build("tfoot", "table-footer", "block border-t bg-muted/50 font-medium [&>tr]:last:border-b-0", Class, Children, Rest);
}

public sealed partial class TableRow : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    // "grid grid-flow-col auto-cols-fr grid-cols-[var(--aw-table-columns)]" is the engine approximation of a
    // native table row; the rest is the official new-york-v4 class list (plus the project's
    // has-aria-expanded state, which is already covered by TableTests).
    protected override Node Build() => TableParts.Build("tr", "table-row",
        "grid grid-flow-col auto-cols-fr grid-cols-[var(--aw-table-columns)] border-b transition-colors " +
        "hover:bg-muted/50 data-[state=selected]:bg-muted has-aria-expanded:bg-muted/50",
        Class, Children, Rest);
}

public sealed partial class TableHead : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    /// <summary>Number of grid columns this header cell spans (the native <c>colspan</c> attribute).</summary>
    public int? ColSpan { get; set; }

    /// <summary>
    /// Forwarded as the native <c>rowspan</c> attribute. The engine's per-row grids cannot span rows, so this is
    /// semantic metadata for assistive technology; it does not change layout.
    /// </summary>
    public int? RowSpan { get; set; }

    protected override Node Build()
    {
        // "flex items-center" approximates the native table cell's vertical centering (align-middle is a no-op in
        // the block/flex approximation); the remaining classes are the official new-york-v4 list.
        var node = TableParts.Build("th", "table-head",
            "flex h-10 items-center px-2 text-left align-middle font-medium whitespace-nowrap text-foreground " +
            "[&:has([role=checkbox])]:pr-0 [&>[role=checkbox]]:translate-y-[2px]",
            Class, Children, Rest);
        TableParts.ApplySpan(node, ColSpan, RowSpan);
        return node;
    }
}

public sealed partial class TableCell : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    /// <summary>Number of grid columns this cell spans (the native <c>colspan</c> attribute).</summary>
    public int? ColSpan { get; set; }

    /// <summary>
    /// Forwarded as the native <c>rowspan</c> attribute. The engine's per-row grids cannot span rows, so this is
    /// semantic metadata for assistive technology; it does not change layout.
    /// </summary>
    public int? RowSpan { get; set; }

    protected override Node Build()
    {
        // "block" is required because the UA stylesheet gives td no display; the rest is the official
        // new-york-v4 class list.
        var node = TableParts.Build("td", "table-cell",
            "block p-2 align-middle whitespace-nowrap [&:has([role=checkbox])]:pr-0 [&>[role=checkbox]]:translate-y-[2px]",
            Class, Children, Rest);
        TableParts.ApplySpan(node, ColSpan, RowSpan);
        return node;
    }
}

public sealed partial class TableCaption : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => TableParts.Build("caption", "table-caption", "block mt-4 text-sm text-muted-foreground", Class, Children, Rest);
}

static class TableParts
{
    public static ElementNode Build(string tag, string slot, string defaults, string? classes, NodeList? children, AttributeDictionary? rest)
    {
        var node = new TableElement(tag) { Children = children ?? new NodeList() };
        node.Attributes["data-slot"] = slot;
        node.MergeAttributes(rest);
        node.Class = Tailwind.TailwindMerge.Cn(defaults, node.Class.Text, classes);
        return node;
    }

    /// <summary>
    /// Expands a bare column count (<c>"3"</c>) to <c>repeat(3, minmax(0, 1fr))</c>; any other value is used as
    /// the track list verbatim.
    /// </summary>
    public static string TrackList(string columns)
    {
        string value = columns.Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0)
            return "repeat(" + count.ToString(CultureInfo.InvariantCulture) + ", minmax(0, 1fr))";
        return value;
    }

    /// <summary>
    /// Applies <c>colspan</c> / <c>rowspan</c>. A colspan greater than one also gets <c>grid-column: span N</c>
    /// so the row's grid approximation leaves the right number of tracks; rowspan is attribute-only because the
    /// rows are independent grids.
    /// </summary>
    public static void ApplySpan(ElementNode node, int? colSpan, int? rowSpan)
    {
        int? columns = Span(colSpan, node.Attributes["colspan"]);
        if (columns is { } n)
        {
            node.Attributes["colspan"] = n.ToString(CultureInfo.InvariantCulture);
            if (n > 1)
            {
                node.Style = new StyleValue("grid-column: span " + n.ToString(CultureInfo.InvariantCulture) +
                                            "; " + node.Style.Text);
            }
        }

        int? rows = Span(rowSpan, node.Attributes["rowspan"]);
        if (rows is { } r) node.Attributes["rowspan"] = r.ToString(CultureInfo.InvariantCulture);
    }

    static int? Span(int? explicitValue, object? attribute)
    {
        if (explicitValue is { } value) return value > 0 ? value : null;
        if (attribute is null) return null;
        return int.TryParse(Convert.ToString(attribute, CultureInfo.InvariantCulture), NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : null;
    }

    sealed class TableElement(string tag) : ElementNode
    {
        protected override Element CreateElement() => new(tag);
    }
}

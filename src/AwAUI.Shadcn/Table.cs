using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Responsive native table and its semantic parts.</summary>
public sealed partial class Table : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    public string? Columns { get; set; }

    protected override Node Build()
    {
        var table = TableParts.Build("table", "table", "block w-full text-sm", Class, Children, Rest);
        if (!string.IsNullOrWhiteSpace(Columns)) table.Style = new StyleValue("--aw-table-columns: " + Columns + "; " + table.Style.Text);
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

    protected override Node Build() => TableParts.Build("tr", "table-row", "grid grid-flow-col auto-cols-fr grid-cols-(--aw-table-columns) border-b transition-colors hover:bg-muted/50 data-[state=selected]:bg-muted", Class, Children, Rest);
}

public sealed partial class TableHead : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => TableParts.Build("th", "table-head", "flex h-10 items-center px-2 text-left align-middle font-medium whitespace-nowrap text-foreground [&:has([role=checkbox])]:pr-0", Class, Children, Rest);
}

public sealed partial class TableCell : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => TableParts.Build("td", "table-cell", "block px-2 py-2 align-middle whitespace-nowrap [&:has([role=checkbox])]:pr-0", Class, Children, Rest);
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

    sealed class TableElement(string tag) : ElementNode
    {
        protected override Element CreateElement() => new(tag);
    }
}

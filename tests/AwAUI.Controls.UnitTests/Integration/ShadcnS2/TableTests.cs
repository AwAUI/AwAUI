using System;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Table: the responsive container and native semantic tags keep the official data-slots, and the row picks up
/// the muted background when it contains an expanded control (has-aria-expanded, as in the web class list).
/// </summary>
public class TableTests
{
    static TableTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 500, height: 300);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).First();

    [Fact]
    public void Table_renders_a_container_and_the_native_semantic_tags()
    {
        var h = Harness(() => new Table
        {
            Children =
            [
                new TableCaption { Children = [new UIText("Invoices")] },
                new TableHeader
                {
                    Children =
                    [
                        new TableRow { Children = [new TableHead { Children = [new UIText("Invoice")] }] },
                    ],
                },
                new TableBody
                {
                    Children =
                    [
                        new TableRow { Children = [new TableCell { Children = [new UIText("INV-001")] }] },
                    ],
                },
                new TableFooter
                {
                    Children =
                    [
                        new TableRow { Children = [new TableCell { Children = [new UIText("Total")] }] },
                    ],
                },
            ],
        });

        var container = Slot(h, "table-container");
        Assert.Contains("overflow-x-auto", container.Classes);
        var table = Slot(h, "table");
        Assert.Equal("table", table.Tag);
        Assert.Same(container, table.Parent);

        Assert.Equal("caption", Slot(h, "table-caption").Tag);
        Assert.Equal("thead", Slot(h, "table-header").Tag);
        Assert.Equal("tbody", Slot(h, "table-body").Tag);
        Assert.Equal("tfoot", Slot(h, "table-footer").Tag);
        Assert.Equal("tr", Slot(h, "table-row").Tag);
        Assert.Equal("th", Slot(h, "table-head").Tag);
        Assert.Equal("td", Slot(h, "table-cell").Tag);
    }

    [Fact]
    public void Table_row_uses_the_muted_background_when_it_contains_an_expanded_control()
    {
        var expandedControl = new Div();
        expandedControl.Attributes["aria-expanded"] = "true";

        var h = Harness(() => new Table
        {
            Children =
            [
                new TableBody
                {
                    Children =
                    [
                        new TableRow
                        {
                            Rest = new AttributeDictionary { ["id"] = "plain" },
                            Children = [new TableCell { Children = [new UIText("closed")] }],
                        },
                        new TableRow
                        {
                            Rest = new AttributeDictionary { ["id"] = "expanded" },
                            Children = [new TableCell { Children = [expandedControl] }],
                        },
                    ],
                },
            ],
        });

        var plain = h.FindAll(e => A(e, "id") == "plain").Single();
        var expanded = h.FindAll(e => A(e, "id") == "expanded").Single();

        Assert.NotEqual(plain.ComputedStyle?.BackgroundColor, expanded.ComputedStyle?.BackgroundColor);
    }

    static Table SampleTable(string? columns = null) => new()
    {
        Columns = columns,
        Children =
        [
            new TableCaption { Children = [new UIText("Invoices")] },
            new TableHeader
            {
                Children =
                [
                    new TableRow
                    {
                        Children =
                        [
                            new TableHead { Children = [new UIText("Invoice")] },
                            new TableHead { Children = [new UIText("Status")] },
                        ],
                    },
                ],
            },
            new TableBody
            {
                Children =
                [
                    new TableRow
                    {
                        Children =
                        [
                            new TableCell { Children = [new UIText("INV-001")] },
                            new TableCell { Children = [new UIText("Paid")] },
                        ],
                    },
                ],
            },
            new TableFooter
            {
                Children =
                [
                    new TableRow { Children = [new TableCell { Children = [new UIText("Total")] }] },
                ],
            },
        ],
    };

    static void AssertHasClasses(Element element, params string[] expected)
    {
        foreach (string name in expected) Assert.Contains(name, element.Classes);
    }

    [Fact]
    public void Table_container_wraps_the_native_table_with_the_official_classes()
    {
        var h = Harness(() => SampleTable());

        var container = Slot(h, "table-container");
        AssertHasClasses(container, "relative", "w-full", "overflow-x-auto");

        var table = Slot(h, "table");
        Assert.Same(container, table.Parent);
        Assert.Equal("table", table.Tag);
        AssertHasClasses(table, "w-full", "caption-bottom", "text-sm", "block");

        AssertHasClasses(Slot(h, "table-header"), "[&_tr]:border-b");
        AssertHasClasses(Slot(h, "table-body"), "[&_tr:last-child]:border-0");
        AssertHasClasses(Slot(h, "table-footer"), "border-t", "bg-muted/50", "font-medium", "[&>tr]:last:border-b-0");
        AssertHasClasses(Slot(h, "table-row"), "border-b", "transition-colors", "hover:bg-muted/50",
            "data-[state=selected]:bg-muted", "has-aria-expanded:bg-muted/50");
        AssertHasClasses(Slot(h, "table-head"), "h-10", "px-2", "text-left", "align-middle", "font-medium",
            "whitespace-nowrap", "text-foreground");
        AssertHasClasses(Slot(h, "table-cell"), "p-2", "align-middle", "whitespace-nowrap");
    }

    [Fact]
    public void Table_caption_uses_the_official_new_york_v4_classes()
    {
        var h = Harness(() => SampleTable());

        var caption = Slot(h, "table-caption");
        Assert.Equal("caption", caption.Tag);
        Assert.Equal(Slot(h, "table"), caption.Parent);
        Assert.Equal(new[] { "block", "mt-4", "text-sm", "text-muted-foreground" }.OrderBy(x => x),
            caption.Classes.OrderBy(x => x));
    }

    [Fact]
    public void Table_columns_become_the_row_grid_template()
    {
        var h = Harness(() => SampleTable("200px 1fr"));

        var table = Slot(h, "table");
        Assert.Contains("--aw-table-columns: 200px 1fr", A(table, "style"));

        var row = Slot(h, "table-row");
        Assert.Equal("200px 1fr", row.ComputedStyle!.GridTemplateColumns.ToString());
    }

    [Fact]
    public void Table_columns_accepts_a_bare_count_and_sizes_rows_with_fr_tracks()
    {
        var h = Harness(() => SampleTable("2"));

        var table = Slot(h, "table");
        Assert.Contains("--aw-table-columns: repeat(2, minmax(0, 1fr))", A(table, "style"));
        var row = Slot(h, "table-row");
        Assert.Contains("repeat(2", row.ComputedStyle!.GridTemplateColumns.ToString());
    }

    [Fact]
    public void Table_colspan_spans_the_grid_and_keeps_the_native_attribute()
    {
        var h = Harness(() => new Table
        {
            Columns = "3",
            Children =
            [
                new TableBody
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableCell { ColSpan = 2, Children = [new UIText("wide")] },
                                new TableCell
                                {
                                    Rest = new AttributeDictionary { ["colspan"] = "2" },
                                    Children = [new UIText("also wide")],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        var cells = h.FindAll(e => A(e, "data-slot") == "table-cell");
        Assert.Equal("2", A(cells[0], "colspan"));
        Assert.Contains("grid-column: span 2", cells[0].InlineStyle ?? "");
        Assert.Equal(2, cells[0].ComputedStyle!.GridColumnStart.Integer);

        // colspan supplied through Rest is translated the same way.
        Assert.Equal("2", A(cells[1], "colspan"));
        Assert.Contains("grid-column: span 2", cells[1].InlineStyle ?? "");
    }

    [Fact]
    public void Table_rowspan_is_kept_as_semantic_metadata_only()
    {
        var h = Harness(() => new Table
        {
            Children =
            [
                new TableBody
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableCell { RowSpan = 2, Children = [new UIText("tall")] },
                                new TableCell { Children = [new UIText("next")] },
                            ],
                        },
                    ],
                },
            ],
        });

        var cell = h.FindAll(e => A(e, "data-slot") == "table-cell").First();
        Assert.Equal("2", A(cell, "rowspan"));
        // The per-row grids cannot span rows, so no grid placement is emitted.
        Assert.DoesNotContain("grid-row", cell.InlineStyle ?? "");
    }

    [Fact]
    public void Table_renders_headless_with_stacked_rows_and_visible_text()
    {
        var h = Harness(() => SampleTable("1fr 1fr"));

        var caption = Slot(h, "table-caption").GetBoundingClientRect();
        var header = Slot(h, "table-header").GetBoundingClientRect();
        var body = Slot(h, "table-body").GetBoundingClientRect();
        var footer = Slot(h, "table-footer").GetBoundingClientRect();

        Assert.True(caption.Width > 0 && caption.Height > 0, "caption has no box");
        Assert.True(caption.Top < header.Top, "caption should be laid out above the header in DOM order");
        Assert.True(header.Top < body.Top, "header row should sit above the body row");
        Assert.True(body.Top < footer.Top, "footer row should sit below the body row");

        var head = Slot(h, "table-head").GetBoundingClientRect();
        Assert.True(head.Width > 0 && head.Height >= 30, $"header cell box was {head}");

        using var bitmap = h.Render();
        var pixels = bitmap.ToRgbaUnpremultiplied();
        int dark = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] < 128 && pixels[i + 1] < 128 && pixels[i + 2] < 128) dark++;
        Assert.True(dark > 100, $"expected rendered table text, found {dark} dark pixels");
    }
}

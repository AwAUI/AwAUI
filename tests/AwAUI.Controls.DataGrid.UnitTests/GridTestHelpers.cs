using AwAUI.Controls.DataGrid;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;

namespace AwAUI.Controls.DataGrid.UnitTests;

/// <summary>Element queries shared by the data-grid tests.</summary>
internal static class GridTest
{
    public static Element GridRoot(UIHarness h) =>
        h.FindAll(e => string.Equals(e.GetAttribute("role"), "grid", StringComparison.OrdinalIgnoreCase)).Single();

    public static List<Element> Rows(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("part") == "row")
            .OrderBy(e => int.Parse(e.GetAttribute("aria-rowindex")!))
            .ToList();

    public static List<Element> HeaderCells(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("role") == "columnheader")
            .OrderBy(e => int.Parse(e.GetAttribute("aria-colindex") ?? "0"))
            .ToList();

    public static List<Element> Cells(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("role") == "gridcell")
            .OrderBy(e => int.Parse(e.GetAttribute("aria-colindex") ?? "0"))
            .ToList();

    public static List<Element> RowCells(UIHarness h, int rowIndex)
    {
        var row = Rows(h)[rowIndex];
        var cells = new List<Element>();
        var stack = new Stack<Element>(row.Children);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (element.GetAttribute("role") == "gridcell") cells.Add(element);
            for (int i = element.Children.Count - 1; i >= 0; i--) stack.Push(element.Children[i]);
        }
        return cells.OrderBy(e => int.Parse(e.GetAttribute("aria-colindex")!)).ToList();
    }

    public static int RowIndex(Element row) => int.Parse(row.GetAttribute("aria-rowindex")!) - 2;

    public static Element Extent(UIHarness h) => h.FindAll(e => e.GetAttribute("part") == "extent").Single();

    public static List<string> ColumnTexts(UIHarness h, int columnIndex) =>
        Rows(h).Select(row => RowCells(h, RowIndex(row))[columnIndex].TextContent.Trim()).ToList();

    public static List<string> HeaderTexts(UIHarness h) =>
        HeaderCells(h).Select(e => e.TextContent.Trim()).ToList();
}

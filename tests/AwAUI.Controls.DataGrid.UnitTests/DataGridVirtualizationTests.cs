using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;

namespace AwAUI.Controls.DataGrid.UnitTests;

public class DataGridVirtualizationTests
{
    sealed class Row
    {
        public string Name { get; set; } = "";
    }

    static DataGrid Grid(int count, int columns = 2)
    {
        var items = Enumerable.Range(0, count).Select(i => new Row { Name = "row-" + i }).ToList();
        var grid = new DataGrid
        {
            Style = "height:100px;width:400px;",
            ItemsSource = items,
            RowHeight = 20,
            HeaderHeight = 20,
            Overscan = 0,
        };
        for (int i = 0; i < columns; i++)
            grid.Columns.Add(new DataGridTextColumn { Header = "Col" + i, Binding = nameof(Row.Name), Width = DataGridLength.Pixel(100) });
        return grid;
    }

    [Fact]
    public void Only_the_rows_in_the_viewport_are_instantiated()
    {
        var h = new UIHarness(() => Grid(1_000));

        var rows = GridTest.Rows(h);
        Assert.InRange(rows.Count, 1, 10);
        Assert.Equal(0, GridTest.RowIndex(rows[0]));
        Assert.DoesNotContain(rows, r => GridTest.RowIndex(r) == 999);
        // 定高：总高度一次算出（表头 20 + 1000 × 20），不实例化其余行
        Assert.Equal(20 + 20_000, GridTest.Extent(h).GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Scrolling_realizes_the_rows_that_come_into_view()
    {
        var h = new UIHarness(() => Grid(1_000));
        var root = GridTest.GridRoot(h);
        h.Wheel(root.GetBoundingClientRect().Center, -500);

        var rows = GridTest.Rows(h);
        Assert.All(rows, r => Assert.InRange(GridTest.RowIndex(r), 20, 35));
        Assert.Contains(rows, r => GridTest.RowIndex(r) > 25);
        Assert.True(root.ScrollController!.OffsetY > 0);
    }

    [Fact]
    public void Keyboard_navigation_scrolls_to_an_unrealized_row()
    {
        var h = new UIHarness(() => Grid(1_000));
        h.Tab();
        h.Key(AwAUI.Platform.PhysicalKey.End, "End", AwAUI.UI.Shortcuts.PrimaryModifier);

        var rows = GridTest.Rows(h);
        Assert.Contains(rows, r => GridTest.RowIndex(r) == 999);
        var root = GridTest.GridRoot(h);
        Assert.Equal(root.ScrollController!.MaxOffset.Y, root.ScrollController.OffsetY, 1);
    }

    [Fact]
    public void Horizontal_scrolling_clips_the_columns_that_are_rendered()
    {
        var h = new UIHarness(() => Grid(10, columns: 20));
        var firstRowCells = GridTest.RowCells(h, 0);
        Assert.InRange(firstRowCells.Count, 1, 12);
        Assert.DoesNotContain(firstRowCells, c => c.GetAttribute("aria-colindex") == "20");

        var root = GridTest.GridRoot(h);
        root.ScrollController!.JumpTo(new Point(root.ScrollController.MaxOffset.X, 0));
        h.Frame();

        var scrolled = GridTest.RowCells(h, 0);
        Assert.Contains(scrolled, c => c.GetAttribute("aria-colindex") == "20");
        Assert.DoesNotContain(scrolled, c => c.GetAttribute("aria-colindex") == "1");
    }

    [Fact]
    public void Row_and_column_indices_follow_the_window_not_the_source()
    {
        var h = new UIHarness(() => Grid(100));
        var root = GridTest.GridRoot(h);
        root.ScrollController!.JumpTo(new Point(0, 500));
        h.Frame();

        var rows = GridTest.Rows(h);
        Assert.All(rows, r => Assert.True(GridTest.RowIndex(r) >= 20));
        foreach (var row in rows)
            Assert.Equal((GridTest.RowIndex(row) + 2).ToString(), row.GetAttribute("aria-rowindex"));
    }

    [Fact]
    public void Virtualization_can_be_turned_off_to_mount_every_row()
    {
        var grid = Grid(50);
        grid.IsVirtualized = false;
        var h = new UIHarness(() => grid);
        Assert.Equal(50, GridTest.Rows(h).Count);
    }

    [Fact]
    public void Row_headers_are_rendered_when_requested()
    {
        var grid = Grid(3);
        grid.HeadersVisibility = DataGridHeadersVisibility.All;
        var h = new UIHarness(() => grid);
        var rowHeaders = h.FindAll(e => e.GetAttribute("role") == "rowheader");
        Assert.Equal(3, rowHeaders.Count);
        Assert.Equal("1", rowHeaders[0].TextContent.Trim());
        // 数据列从行头之后开始
        Assert.True(GridTest.RowCells(h, 0)[0].GetBoundingClientRect().Left > 0);
    }
}

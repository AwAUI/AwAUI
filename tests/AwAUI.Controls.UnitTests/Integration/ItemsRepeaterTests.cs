using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Variable-height virtualization in <see cref="ItemsRepeater"/>, plus the fixed-height regression path.</summary>
public class ItemsRepeaterTests
{
    static List<Element> Rows(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("part") == "item")
            .OrderBy(e => int.Parse(e.GetAttribute("aria-posinset")!))
            .ToList();

    static int RowHeight(int index) => 10 + index % 4 * 10;

    [Fact]
    public void Horizontal_mode_virtualizes_on_the_x_axis_and_scrolls_to_the_end()
    {
        var items = Enumerable.Range(0, 1_000).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            Orientation = Orientation.Horizontal,
            ItemWidth = 20,
            EstimatedItemWidth = 20,
            Overscan = 0,
            ColumnGap = 2,
            ItemTemplate = _ => new Div { Style = "height:20px" },
        }, height: 600);

        var extent = h.FindAll(e => e.GetAttribute("part") == "extent").Single();
        Assert.Equal(1_000 * 20 + 999 * 2, extent.GetBoundingClientRect().Width, 1);
        var rows = Rows(h);
        Assert.InRange(rows.Count, 1, 30);
        Assert.Equal(0, rows[0].GetBoundingClientRect().Left - extent.GetBoundingClientRect().Left, 1);
        Assert.Equal(rows[0].GetBoundingClientRect().Height, extent.GetBoundingClientRect().Height, 1);

        var viewport = extent.Parent!;
        viewport.ScrollController!.JumpTo(new Point(viewport.ScrollController.MaxOffset.X, 0));
        h.Frame();
        Assert.NotNull(Rows(h).SingleOrDefault(r => r.GetAttribute("aria-posinset") == "1000"));
        Assert.True(viewport.ScrollController.Offset.X > 19_000, $"offset {viewport.ScrollController.Offset.X}");
    }

    [Fact]
    public void Horizontal_variable_width_items_are_measured_by_their_content_width()
    {
        var items = Enumerable.Range(0, 20).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            Orientation = Orientation.Horizontal,
            ItemWidth = null,
            EstimatedItemWidth = 10,
            Overscan = 0,
            ItemTemplate = _ => new Div { Style = "width:20px;height:20px" },
        }, height: 600);

        var rows = Rows(h);
        Assert.True(rows.Count >= 10, $"realized {rows.Count}");
        Assert.All(rows, r => Assert.Equal(20, r.GetBoundingClientRect().Width, 1));
        var extent = h.FindAll(e => e.GetAttribute("part") == "extent").Single();
        Assert.Equal(400, extent.GetBoundingClientRect().Width, 1);
    }

    [Fact]
    public void Variable_height_rows_are_measured_and_stacked_by_their_content()
    {
        var items = Enumerable.Range(0, 20).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = null,
            EstimatedItemHeight = 10,
            Overscan = 0,
            ItemTemplate = value => new Div { Style = "height:" + RowHeight((int)value!) + "px" },
        }, height: 600);

        var rows = Rows(h);
        Assert.True(rows.Count >= 4, $"realized {rows.Count} rows");

        // 行高来自内容测量，后一行紧接前一行
        for (int i = 0; i < rows.Count; i++)
        {
            int index = int.Parse(rows[i].GetAttribute("aria-posinset")!) - 1;
            Assert.Equal(RowHeight(index), rows[i].GetBoundingClientRect().Height, 3);
            if (i > 0)
                Assert.Equal(rows[i - 1].GetBoundingClientRect().Bottom, rows[i].GetBoundingClientRect().Top, 3);
        }

        // 总尺寸反映测量结果（初始估计 10 像素/行会被实测值替换）
        var extent = h.FindAll(e => e.GetAttribute("part") == "extent").Single();
        Assert.True(extent.GetBoundingClientRect().Height > rows.Count * 10,
            $"extent {extent.GetBoundingClientRect().Height} should exceed the estimate-only minimum");
    }

    [Fact]
    public void Variable_height_repeater_reaches_the_end_with_measured_positions()
    {
        var items = Enumerable.Range(0, 200).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = null,
            EstimatedItemHeight = 20,
            Overscan = 1,
            ItemTemplate = value => new Div { Style = "height:" + RowHeight((int)value!) + "px" },
        }, height: 600);

        var list = h.FindAll(e => e.Role == Role.List).Single();
        var center = list.GetBoundingClientRect().Center;
        // 内容尺寸随测量增长，滚动多次直到末尾
        for (int i = 0; i < 40; i++) h.Wheel(center, -2_000);

        var rows = Rows(h);
        Assert.True(rows.Any(r => r.GetAttribute("aria-posinset") == "200"),
            $"realized: {string.Join(",", rows.Select(r => r.GetAttribute("aria-posinset")))} extent={h.FindAll(e => e.GetAttribute("part") == "extent").Single().GetBoundingClientRect().Height}");
        foreach (var row in rows)
        {
            int index = int.Parse(row.GetAttribute("aria-posinset")!) - 1;
            Assert.Equal(RowHeight(index), row.GetBoundingClientRect().Height, 3);
        }
    }

    [Fact]
    public void Fixed_height_rows_keep_their_configured_height()
    {
        var items = Enumerable.Range(0, 50).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = 30,
            Overscan = 0,
            ItemTemplate = _ => new Div { Style = "height:100px" },
        }, height: 600);

        var rows = Rows(h);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(30, row.GetBoundingClientRect().Height, 3));
    }

    [Fact]
    public void Lanes_lay_realized_items_out_in_a_virtualized_grid()
    {
        var items = Enumerable.Range(0, 1_000).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = 20,
            Lanes = 4,
            Overscan = 0,
            ItemTemplate = _ => new Div(),
        }, height: 600);

        var rows = Rows(h);
        Assert.InRange(rows.Count, 40, 52); // 240px viewport / 20px rows = 12 rows × 4 lanes
        var first = rows.Single(r => r.GetAttribute("aria-posinset") == "1");
        var second = rows.Single(r => r.GetAttribute("aria-posinset") == "2");
        var nextRow = rows.Single(r => r.GetAttribute("aria-posinset") == "5");

        var box = first.GetBoundingClientRect();
        Assert.Equal(200, box.Width, 1);
        Assert.Equal(box.Top, second.GetBoundingClientRect().Top, 1);
        Assert.Equal(box.Left, nextRow.GetBoundingClientRect().Left, 1);
        Assert.Equal(box.Top + 20, nextRow.GetBoundingClientRect().Top, 1);
        Assert.Equal(box.Left + 200, second.GetBoundingClientRect().Left, 1);

        var extent = h.FindAll(e => e.GetAttribute("part") == "extent").Single();
        Assert.Equal(5_000, extent.GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Grid_row_and_column_gaps_are_applied()
    {
        var items = Enumerable.Range(0, 20).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = 20,
            Lanes = 4,
            RowGap = 6,
            ColumnGap = 8,
            Overscan = 0,
            ItemTemplate = _ => new Div(),
        }, height: 600);

        var rows = Rows(h);
        var first = rows.Single(r => r.GetAttribute("aria-posinset") == "1");
        var second = rows.Single(r => r.GetAttribute("aria-posinset") == "2");
        var nextRow = rows.Single(r => r.GetAttribute("aria-posinset") == "5");
        var box = first.GetBoundingClientRect();

        Assert.Equal(194, box.Width, 1); // (800 − 3 × 8) / 4
        Assert.Equal(box.Left + 202, second.GetBoundingClientRect().Left, 1); // column width + gap
        Assert.Equal(box.Top + 26, nextRow.GetBoundingClientRect().Top, 1);   // row height + gap

        var extent = h.FindAll(e => e.GetAttribute("part") == "extent").Single();
        Assert.Equal(5 * 20 + 4 * 6, extent.GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Variable_height_grid_rows_take_the_tallest_item_in_each_row()
    {
        var items = Enumerable.Range(0, 3).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = null,
            EstimatedItemHeight = 10,
            Lanes = 3,
            Overscan = 0,
            ItemTemplate = value => new Div { Style = "height:" + (10 + (int)value! * 10) + "px" },
        }, height: 600);

        var rows = Rows(h);
        Assert.Equal(3, rows.Count);
        var top = rows[0].GetBoundingClientRect().Top;
        Assert.All(rows, row => Assert.Equal(top, row.GetBoundingClientRect().Top, 1));
        Assert.Equal(10, rows[0].GetBoundingClientRect().Height, 3);
        Assert.Equal(30, rows[2].GetBoundingClientRect().Height, 3);

        var extent = h.FindAll(e => e.GetAttribute("part") == "extent").Single();
        Assert.Equal(30, extent.GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Scrolling_realizes_later_grid_rows()
    {
        var items = Enumerable.Range(0, 1_000).ToList();
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = 20,
            Lanes = 3,
            Overscan = 1,
            ItemTemplate = _ => new Div(),
        }, height: 600);

        var list = h.FindAll(e => e.Role == Role.List).Single();
        h.Wheel(list.GetBoundingClientRect().Center, -2_000);

        var rows = Rows(h);
        Assert.Contains(rows, r => int.Parse(r.GetAttribute("aria-posinset")!) > 150);
        Assert.All(rows, row => Assert.InRange(row.GetBoundingClientRect().Width, 266, 267));
    }

    [Fact]
    public void Changing_lanes_recomputes_the_grid()
    {
        var items = Enumerable.Range(0, 40).ToList();
        var lanes = new State<int>(2);
        var h = new UIHarness(() => new ItemsRepeater
        {
            ItemsSource = items,
            ItemHeight = 20,
            Lanes = lanes.Value,
            Overscan = 0,
            ItemTemplate = _ => new Div(),
        }, height: 600);

        Assert.Equal(400, Rows(h)[0].GetBoundingClientRect().Width, 1);
        Assert.Equal(400, h.FindAll(e => e.GetAttribute("part") == "extent").Single().GetBoundingClientRect().Height, 1);

        lanes.Value = 4;
        h.Frame();

        Assert.Equal(200, Rows(h)[0].GetBoundingClientRect().Width, 1);
        Assert.Equal(200, h.FindAll(e => e.GetAttribute("part") == "extent").Single().GetBoundingClientRect().Height, 1);
    }
}

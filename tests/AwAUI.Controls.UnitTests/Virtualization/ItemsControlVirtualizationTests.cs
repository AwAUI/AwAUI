using System.Collections;
using System.Collections.ObjectModel;
using AwAUI.Controls.Primitives;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Virtualization;

/// <summary>Virtualized <see cref="ItemsControl"/>: realized range, scrolling, collection changes and variable-height measurement.</summary>
public class ItemsControlVirtualizationTests
{
    static Node RowNode(object? _)
    {
        var row = new Div { Style = "height:20px" };
        row.Attributes["part"] = "content";
        return row;
    }

    static List<Element> Rows(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("part") == "item")
            .OrderBy(e => int.Parse(e.GetAttribute("data-index")!))
            .ToList();

    static Element Extent(UIHarness h) => h.FindAll(e => e.GetAttribute("part") == "extent").Single();

    static Element Viewport(UIHarness h) => Extent(h).Parent!;

    static int RowIndex(Element row) => int.Parse(row.GetAttribute("data-index")!);

    static ItemsControl Items(int count, bool virtualized = true, double? itemHeight = 20, int overscan = 0,
        Func<object?, Node>? template = null) => new()
    {
        Style = "height:200px;",
        ItemsSource = Enumerable.Range(0, count).ToList(),
        IsVirtualized = virtualized,
        ItemHeight = itemHeight,
        EstimatedItemHeight = itemHeight ?? 10,
        Overscan = overscan,
        ItemTemplate = template ?? RowNode,
    };

    [Fact]
    public void Only_the_rows_in_the_viewport_are_mounted()
    {
        var h = new UIHarness(() => Items(1_000), height: 600);

        var rows = Rows(h);
        Assert.InRange(rows.Count, 1, 30);
        Assert.Equal(0, RowIndex(rows[0]));
        Assert.Equal("1", rows[0].GetAttribute("aria-posinset"));
        Assert.Equal("1000", rows[0].GetAttribute("aria-setsize"));
        Assert.DoesNotContain(rows, r => RowIndex(r) == 999);
        // 定高：一次算出总尺寸，不挂载其余 990 行
        Assert.Equal(20_000, Extent(h).GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Horizontal_virtualization_scrolls_on_the_x_axis()
    {
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "width:200px;height:100px;",
            ItemsSource = Enumerable.Range(0, 1_000).ToList(),
            IsVirtualized = true,
            Orientation = Orientation.Horizontal,
            ItemWidth = 20,
            EstimatedItemWidth = 20,
            Overscan = 0,
            ItemTemplate = RowNode,
        }, height: 600);

        Assert.Equal(20_000, Extent(h).GetBoundingClientRect().Width, 1);
        var rows = Rows(h);
        Assert.Equal(0, RowIndex(rows[0]));
        Assert.DoesNotContain(rows, r => RowIndex(r) == 999);

        var viewport = Viewport(h);
        // 物理向右的滚轮把 LTR 行内偏移推大
        h.Pipeline.SmoothScrolling = false;
        h.WheelX(viewport.GetBoundingClientRect().Center, -40);
        Assert.Equal(40, viewport.ScrollController!.Offset.X, 0.5);

        viewport.ScrollController!.JumpTo(new Point(viewport.ScrollController.MaxOffset.X, 0));
        h.Frame();
        Assert.Contains(Rows(h), r => RowIndex(r) == 999);
        Assert.True(viewport.ScrollController.Offset.X > 19_000, $"offset {viewport.ScrollController.Offset.X}");
    }

    [Fact]
    public void Horizontal_virtualization_mirrors_in_rtl_and_scrolls_toward_the_inline_end()
    {
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "width:200px;height:100px;",
            ItemsSource = Enumerable.Range(0, 1_000).ToList(),
            IsVirtualized = true,
            Orientation = Orientation.Horizontal,
            ItemWidth = 20,
            EstimatedItemWidth = 20,
            Overscan = 0,
            ItemTemplate = RowNode,
        }, height: 600);
        // 方向从根元素继承到虚拟化视口与各项
        h.Root.RootElement.SetAttributeOverride("dir", "rtl");
        h.Frame();

        var viewport = Viewport(h);
        var c = viewport.ScrollController!;
        var vp = viewport.GetBoundingClientRect();
        Assert.Equal(0, c.Offset.X, 0.5);
        Assert.Equal(vp.Right, Extent(h).GetBoundingClientRect().Right, 0.5);
        var first = Rows(h).Single(r => RowIndex(r) == 0);
        Assert.Equal(vp.Right, first.GetBoundingClientRect().Right, 0.5);

        Assert.Equal(AwAUI.Css.Values.CssDirection.Rtl, viewport.ComputedStyle?.Direction);
        Assert.True(c.MaxOffset.X > 0, $"max {c.MaxOffset.X}");

        // 物理向左的滚轮在 RTL 下朝行内末尾推进（偏移从右边缘量起、增大）
        h.Pipeline.SmoothScrolling = false;
        h.WheelX(vp.Center, +40);
        Assert.Equal(40, c.Offset.X, 0.5);
        Assert.Equal(vp.Right + 40, Extent(h).GetBoundingClientRect().Right, 0.5);

        // 逻辑末尾：最后一项出现在左边缘
        c.JumpTo(c.MaxOffset.X, 0);
        h.Frame();
        var last = Rows(h).SingleOrDefault(r => RowIndex(r) == 999);
        Assert.NotNull(last);
        Assert.Equal(vp.Left, last!.GetBoundingClientRect().Left, 0.5);
    }

    [Fact]
    public void Horizontal_variable_width_items_are_measured_by_their_content_width()
    {
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "width:200px;",
            ItemsSource = Enumerable.Range(0, 10).ToList(),
            IsVirtualized = true,
            Orientation = Orientation.Horizontal,
            ItemWidth = null,
            EstimatedItemWidth = 10,
            Overscan = 0,
            ItemTemplate = _ => new Div { Style = "width:20px;height:20px" },
        }, height: 600);

        // 视口宽度 200，十项全部实现并测量：总宽 10 × 20
        var rows = Rows(h);
        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.Equal(20, r.GetBoundingClientRect().Width, 1));
        Assert.Equal(200, Extent(h).GetBoundingClientRect().Width, 1);
    }

    [Fact]
    public void Rows_wrap_the_item_template_and_expose_the_flat_index()
    {
        var h = new UIHarness(() => Items(1_000), height: 600);

        var first = Rows(h)[0];
        Assert.Equal("0", first.GetAttribute("data-index"));
        // 行包住 PresentItem 的结果，且扁平挂在 extent 下
        Assert.Same(Extent(h), first.Parent);
        var content = Assert.Single(first.Children);
        Assert.Equal("content", content.GetAttribute("part"));
    }

    [Fact]
    public void Scrolling_to_the_end_realizes_the_last_items()
    {
        var h = new UIHarness(() => Items(1_000), height: 600);
        var viewport = Viewport(h);

        viewport.ScrollController.JumpTo(new Point(0, viewport.ScrollController.MaxOffset.Y));
        h.Frame();

        var last = Rows(h).SingleOrDefault(r => RowIndex(r) == 999);
        Assert.NotNull(last);
        Assert.Equal("1000", last!.GetAttribute("aria-setsize"));
        Assert.True(viewport.ScrollController.Offset.Y > 19_000, $"offset {viewport.ScrollController.Offset.Y}");
    }

    [Fact]
    public void Collection_add_remove_and_reset_while_scrolled_rebuild_the_range()
    {
        var source = new ObservableCollection<int>(Enumerable.Range(0, 1_000));
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "height:200px;",
            ItemsSource = source,
            IsVirtualized = true,
            ItemHeight = 20,
            EstimatedItemHeight = 20,
            Overscan = 0,
            ItemTemplate = RowNode,
        }, height: 600);
        var viewport = Viewport(h);
        viewport.ScrollController.JumpTo(new Point(0, 10_000));
        h.Frame();
        Assert.Contains(Rows(h), r => RowIndex(r) > 400);

        // Add：项数增长，滚到新的末尾后最后一项被实现
        source.Add(1_000);
        h.Frame();
        Assert.All(Rows(h), r => Assert.Equal("1001", r.GetAttribute("aria-setsize")));
        viewport.ScrollController.JumpTo(new Point(0, viewport.ScrollController.MaxOffset.Y));
        h.Frame();
        Assert.Contains(Rows(h), r => RowIndex(r) == 1_000);

        // Remove：项数减少，仍在滚动位置附近的范围内
        source.RemoveAt(0);
        h.Frame();
        Assert.All(Rows(h), r => Assert.Equal("1000", r.GetAttribute("aria-setsize")));
        Assert.Contains(Rows(h), r => RowIndex(r) > 400);

        // Reset：清空后重建，范围与总尺寸都由新的集合决定
        source.Clear();
        h.Frame();
        Assert.Empty(Rows(h));
        Assert.Equal(0, Extent(h).GetBoundingClientRect().Height, 1);

        foreach (var i in Enumerable.Range(0, 50)) source.Add(i);
        h.Frame();
        Assert.All(Rows(h), r => Assert.Equal("50", r.GetAttribute("aria-setsize")));
        Assert.InRange(Rows(h).Count, 1, 30);
        Assert.Equal(50 * 20, Extent(h).GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Variable_height_rows_are_measured_and_survive_a_source_reset()
    {
        static int Height(int index) => 10 + index % 4 * 10;
        var source = new State<IEnumerable?>(Enumerable.Range(0, 30).ToList());
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "height:300px;",
            ItemsSource = source.Value,
            IsVirtualized = true,
            ItemHeight = null,
            EstimatedItemHeight = 10,
            Overscan = 0,
            ItemTemplate = value => new Div { Style = "height:" + Height((int)value!) + "px" },
        }, height: 600);

        AssertMeasuredRows(h, Height);
        double before = Extent(h).GetBoundingClientRect().Height;
        // 实测值替换了估计值：总尺寸大于「全部按 10 像素估计」的下限
        Assert.True(before > 30 * 10, $"extent {before}");

        // 等价的新序列按位置复用 ItemRow；测量缓存按索引保留，不会退回估计值
        source.Value = Enumerable.Range(0, 30).ToList();
        h.Frame();

        AssertMeasuredRows(h, Height);
        Assert.Equal(before, Extent(h).GetBoundingClientRect().Height, 3);
    }

    static void AssertMeasuredRows(UIHarness h, Func<int, int> height)
    {
        var rows = Rows(h);
        Assert.True(rows.Count >= 5, $"realized {rows.Count} rows");
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(height(RowIndex(rows[i])), rows[i].GetBoundingClientRect().Height, 3);
            if (i > 0)
                Assert.Equal(rows[i - 1].GetBoundingClientRect().Bottom, rows[i].GetBoundingClientRect().Top, 3);
        }
    }

    [Fact]
    public void Virtualized_mode_ignores_ItemsPanel()
    {
        int panels = 0;
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "height:200px;",
            ItemsSource = Enumerable.Range(0, 1_000).ToList(),
            IsVirtualized = true,
            ItemHeight = 20,
            EstimatedItemHeight = 20,
            Overscan = 0,
            ItemTemplate = RowNode,
            ItemsPanel = items =>
            {
                panels++;
                return new StackPanel { Children = items };
            },
        }, height: 600);

        // 虚拟化时 ItemsPanel 不参与行结构：行是 extent 下的扁平子节点
        Assert.Equal(0, panels);
        var extent = Extent(h);
        var rows = Rows(h);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Same(extent, r.Parent));
    }

    [Fact]
    public void Virtualization_is_off_by_default()
    {
        var h = new UIHarness(() => new ItemsControl
        {
            Style = "height:100px;",
            ItemsSource = Enumerable.Range(0, 50).ToList(),
            ItemTemplate = RowNode,
        }, height: 600);

        // 默认路径直接挂载模板节点，不插入行包装或 extent
        var contents = h.FindAll(e => e.GetAttribute("part") == "content");
        Assert.Equal(50, contents.Count);
        Assert.Empty(h.FindAll(e => e.GetAttribute("part") == "extent"));
        Assert.All(contents, content => Assert.Equal("aw-items-control", content.Parent!.GetAttribute("class")));
    }
}

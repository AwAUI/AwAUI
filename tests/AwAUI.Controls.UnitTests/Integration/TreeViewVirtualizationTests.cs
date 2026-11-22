using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Virtualized <see cref="TreeView"/>: realized range, scrolling, keyboard navigation, expansion and measurement.</summary>
public class TreeViewVirtualizationTests
{
    static List<Element> Items(UIHarness h) =>
        h.FindAll(e => e.Role == Role.TreeItem)
            .OrderBy(e => e.GetBoundingClientRect().Top)
            .ToList();

    static Element Extent(UIHarness h) => h.FindAll(e => e.GetAttribute("part") == "extent").Single();

    static int Position(Element item) => int.Parse(item.GetAttribute("aria-posinset")!);

    static List<TreeViewItem> Flat(int count) =>
        Enumerable.Range(0, count).Select(i => new TreeViewItem(i, "Item " + i)).ToList();

    static TreeView Tree(IReadOnlyList<TreeViewItem> items, double? itemHeight = 20, int overscan = 0,
        IReadOnlyCollection<object>? expanded = null) => new()
    {
        Style = "height:100px;",
        Items = items,
        IsVirtualized = true,
        ItemHeight = itemHeight,
        EstimatedItemHeight = itemHeight ?? 10,
        Overscan = overscan,
        DefaultExpandedKeys = expanded,
        ItemTemplate = _ => new Div { Style = "height:20px" },
    };

    [Fact]
    public void Only_the_visible_rows_are_mounted()
    {
        var h = new UIHarness(() => Tree(Flat(1_000)), height: 600);

        var items = Items(h);
        Assert.InRange(items.Count, 1, 8);
        Assert.Equal(1, Position(items[0]));
        Assert.DoesNotContain(items, i => Position(i) == 1_000);
        Assert.Equal(20_000, Extent(h).GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Scrolling_realizes_the_rows_that_come_into_view()
    {
        var h = new UIHarness(() => Tree(Flat(1_000)), height: 600);
        var tree = h.FindAll(e => e.Role == Role.Tree).Single();

        h.Wheel(tree.GetBoundingClientRect().Center, -500);

        // 第 1 行仍作为 Tab 停靠保持挂载
        Assert.All(Items(h).Where(i => Position(i) != 1), i => Assert.InRange(Position(i), 21, 31));
        Assert.True(tree.ScrollController.Offset.Y > 0, $"offset {tree.ScrollController.Offset.Y}");
    }

    [Fact]
    public void Keyboard_navigation_scrolls_to_and_focuses_an_unmounted_row()
    {
        var h = new UIHarness(() => Tree(Flat(1_000)), height: 600);
        h.Tab();
        Assert.True(Items(h)[0].IsFocused);

        h.Key(PhysicalKey.End, "End");

        var last = Items(h).SingleOrDefault(i => Position(i) == 1_000);
        Assert.NotNull(last);
        Assert.True(last!.IsFocused);
        var tree = h.FindAll(e => e.Role == Role.Tree).Single();
        Assert.Equal(tree.ScrollController.MaxOffset.Y, tree.ScrollController.Offset.Y, 1);
    }

    [Fact]
    public void Expanding_and_collapsing_changes_the_virtualized_range()
    {
        var items = new List<TreeViewItem>
        {
            new("root", "Root", [new TreeViewItem("a", "A"), new TreeViewItem("b", "B")]),
        };
        var h = new UIHarness(() => Tree(items), height: 600);
        Assert.Single(Items(h));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(3, Items(h).Count);
        // 子项按层级缩进（虚拟化时以每行的内边距代替嵌套 group）
        var child = Items(h).First(i => i.GetAttribute("aria-level") == "2");
        var parent = Items(h).Single(i => i.GetAttribute("aria-level") == "1");
        float ChildRowX() => child.Children.Single(c => c.GetAttribute("part") == "row").GetBoundingClientRect().X;
        float ParentRowX() => parent.Children.Single(c => c.GetAttribute("part") == "row").GetBoundingClientRect().X;
        Assert.Equal(18, ChildRowX() - ParentRowX(), 1);

        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft"); // 回到父项（折叠由父项上的左键完成）
        Assert.True(parent.IsFocused);
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.Single(Items(h));
    }

    [Fact]
    public void Variable_height_rows_are_measured_and_stacked()
    {
        static int Height(int index) => 10 + index % 4 * 10;
        var items = Enumerable.Range(0, 30).Select(i => new TreeViewItem(i, "Item " + i)).ToList();
        var h = new UIHarness(() => new TreeView
        {
            Style = "height:300px;",
            Items = items,
            IsVirtualized = true,
            ItemHeight = null,
            EstimatedItemHeight = 10,
            Overscan = 0,
            ItemTemplate = item => new Div { Style = "height:" + Height((int)item.Key) + "px" },
        }, height: 600);

        var rows = Items(h);
        Assert.True(rows.Count >= 5, $"realized {rows.Count} rows");
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Height(Position(rows[i]) - 1), rows[i].GetBoundingClientRect().Height, 3);
            if (i > 0)
                Assert.Equal(rows[i - 1].GetBoundingClientRect().Bottom, rows[i].GetBoundingClientRect().Top, 3);
        }
        Assert.True(Extent(h).GetBoundingClientRect().Height > rows.Count * 10);
    }

    [Fact]
    public void Virtualization_is_off_by_default()
    {
        var h = new UIHarness(() => new TreeView
        {
            Style = "height:100px;",
            Items = Flat(50),
            ItemTemplate = _ => new Div { Style = "height:20px" },
        }, height: 600);

        Assert.Equal(50, Items(h).Count);
    }
}

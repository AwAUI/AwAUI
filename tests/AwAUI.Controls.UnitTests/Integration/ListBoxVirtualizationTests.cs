using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Virtualized <see cref="ListBox"/>: realized range, scrolling, keyboard navigation and variable-height measurement.</summary>
public class ListBoxVirtualizationTests
{
    static List<Element> Rows(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("part") == "item")
            .OrderBy(e => int.Parse(e.GetAttribute("aria-posinset")!))
            .ToList();

    static Element Extent(UIHarness h) => h.FindAll(e => e.GetAttribute("part") == "extent").Single();

    static int RowIndex(Element row) => int.Parse(row.GetAttribute("aria-posinset")!) - 1;

    static ListBox List(int count, bool virtualized = true, double? itemHeight = 20, int overscan = 0, int? selectedIndex = null) => new()
    {
        Style = "height:100px;",
        ItemsSource = Enumerable.Range(0, count).ToList(),
        IsVirtualized = virtualized,
        ItemHeight = itemHeight,
        EstimatedItemHeight = itemHeight ?? 10,
        Overscan = overscan,
        SelectedIndex = selectedIndex,
        ItemTemplate = _ => new Div { Style = "height:20px" },
    };

    [Fact]
    public void Only_the_rows_in_the_viewport_are_mounted()
    {
        var h = new UIHarness(() => List(1_000), height: 600);

        var rows = Rows(h);
        Assert.InRange(rows.Count, 1, 8);
        Assert.Equal(0, RowIndex(rows[0]));
        Assert.DoesNotContain(rows, r => RowIndex(r) == 999);
        // 定高：一次算出总尺寸，不挂载其余 990 行
        Assert.Equal(20_000, Extent(h).GetBoundingClientRect().Height, 1);
    }

    [Fact]
    public void Scrolling_realizes_the_rows_that_come_into_view()
    {
        var h = new UIHarness(() => List(1_000), height: 600);
        var list = h.FindAll(e => e.Role == Role.ListBox).Single();

        h.Wheel(list.GetBoundingClientRect().Center, -500);

        var rows = Rows(h);
        // 第 0 行仍作为 Tab 停靠保持挂载；进入视口的是滚动后的新范围
        Assert.All(rows.Where(r => RowIndex(r) != 0), r => Assert.InRange(RowIndex(r), 20, 30));
        Assert.Contains(rows, r => RowIndex(r) >= 25);
        Assert.True(list.ScrollController.Offset.Y > 0, $"offset {list.ScrollController.Offset.Y}");
    }

    [Fact]
    public void Keyboard_navigation_scrolls_to_and_focuses_an_unmounted_row()
    {
        var h = new UIHarness(() => List(1_000), height: 600);
        h.Tab();
        Assert.True(Rows(h)[0].IsFocused);

        h.Key(PhysicalKey.End, "End");

        var last = Rows(h).SingleOrDefault(r => RowIndex(r) == 999);
        Assert.NotNull(last);
        Assert.True(last!.IsFocused);
        var list = h.FindAll(e => e.Role == Role.ListBox).Single();
        Assert.Equal(list.ScrollController.MaxOffset.Y, list.ScrollController.Offset.Y, 1);
    }

    [Fact]
    public void A_controlled_selection_outside_the_viewport_stays_mounted_as_the_tab_stop()
    {
        var h = new UIHarness(() => List(1_000, selectedIndex: 900), height: 600);

        var tabStops = Rows(h).Where(r => r.GetAttribute("tabindex") == "0").ToList();
        var tabStop = Assert.Single(tabStops);
        Assert.Equal(900, RowIndex(tabStop));
        // 视口内的行仍在，选中的行额外实现（不在渲染范围内也保持挂载）
        Assert.Contains(Rows(h), r => RowIndex(r) == 0);
    }

    [Fact]
    public void Variable_height_rows_are_measured_and_stacked()
    {
        static int Height(int index) => 10 + index % 4 * 10;
        var h = new UIHarness(() => new ListBox
        {
            Style = "height:300px;",
            ItemsSource = Enumerable.Range(0, 30).ToList(),
            IsVirtualized = true,
            ItemHeight = null,
            EstimatedItemHeight = 10,
            Overscan = 0,
            ItemTemplate = value => new Div { Style = "height:" + Height((int)value!) + "px" },
        }, height: 600);

        var rows = Rows(h);
        Assert.True(rows.Count >= 5, $"realized {rows.Count} rows");
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Height(RowIndex(rows[i])), rows[i].GetBoundingClientRect().Height, 3);
            if (i > 0)
                Assert.Equal(rows[i - 1].GetBoundingClientRect().Bottom, rows[i].GetBoundingClientRect().Top, 3);
        }
        // 实测值替换了估计值：总尺寸大于「全部按 10 像素估计」的下限
        Assert.True(Extent(h).GetBoundingClientRect().Height > rows.Count * 10);
    }

    [Fact]
    public void Virtualization_is_off_by_default()
    {
        var h = new UIHarness(() => new ListBox
        {
            Style = "height:100px;",
            ItemsSource = Enumerable.Range(0, 50).ToList(),
            ItemTemplate = _ => new Div { Style = "height:20px" },
        }, height: 600);

        Assert.Equal(50, Rows(h).Count);
    }
}

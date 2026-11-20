using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// 第七批留下的 aspect-ratio 后续事项：替换元素标记、宽高都 auto 时 min/max 经比例传递、非替换元素的自动最小尺寸；以及 flex 的 order。
/// 期望值都在 headless Chromium（Playwright chromium_headless_shell-1243）中用等价的 HTML 量过（替换元素用给定 width/height 的 SVG 图片）。
/// </summary>
public class AspectRatioReplacedTests
{
    /// <summary>类似图片：固定的固有尺寸、替换元素、按固有比例。</summary>
    static LayoutNode Img(float w, float h, LayoutStyle? style = null) =>
        new((style ?? Block) with { IsReplaced = true, AspectRatio = w / h }) { Measure = new FixedMeasure(w, h) };

    [Fact]
    public void Percentage_min_width_scales_replaced_element_keeping_ratio()
    {
        // <img 50×25 style="display:block; min-width:10%"> 在 1000px 容器中：min-width = 100 → 100×50（第七批时得到 1000×500）
        var i = Img(50, 25, Block with { MinWidth = Dimension.Percent(10) });
        Run(N(Block, i), 1000);
        Box(i, 0, 0, 100, 50);
    }

    [Fact]
    public void Max_height_is_transferred_to_the_width_of_replaced_element()
    {
        // 100×50，max-height: 20px → 宽 40（Chrome 40×20）
        var i = Img(100, 50, Block with { MaxHeight = 20 });
        Run(N(Block, i), 300);
        Box(i, 0, 0, 40, 20);
    }

    [Fact]
    public void Transferred_min_is_capped_by_max_width_then_height_clamped()
    {
        // min-height:100 → 传递的最小宽度 200，被 max-width:150 截到 150；高度 75 再被 min-height 提到 100 → 150×100（Chrome 相同）
        var i = Img(100, 50, Block with { MinHeight = 100, MaxWidth = 150 });
        // 宽度确定时比例被打破：width:60、max-height:20 → 60×20
        var j = Img(100, 50, Block with { Width = 60, MaxHeight = 20 });
        Run(N(Block, i, j), 300);
        Box(i, 0, 0, 150, 100);
        Box(j, 0, 100, 60, 20);
    }

    [Fact]
    public void Block_level_replaced_element_is_not_stretched()
    {
        // display:block 的 100×50 图片在 300px 容器中保持 100×50（不靠 InlineFlex 规避）
        var i = Img(100, 50);
        Run(N(Block, i), 300);
        Box(i, 0, 0, 100, 50);
    }

    [Fact]
    public void Absolutely_positioned_replaced_element_with_all_insets_keeps_natural_size()
    {
        // left/right/top/bottom: 0 → 100×50 在 (0,0)；再加 margin:auto 时在 300×200 中居中 → (100, 75)
        var i = Img(100, 50, Block with { Position = Position.Absolute, Inset = new(LengthPercentageAuto.Px(0)) });
        var j = Img(100, 50, Block with { Position = Position.Absolute, Inset = new(LengthPercentageAuto.Px(0)), Margin = new(LengthPercentageAuto.Auto) });
        Run(N(Block with { Position = Position.Relative, Height = 200 }, i, j), 300);
        Box(i, 0, 0, 100, 50);
        Box(j, 100, 75, 100, 50);
    }

    [Fact]
    public void Replaced_grid_item_aligns_to_start_by_default()
    {
        var i = Img(100, 50);
        var g = N(Block with
        {
            Display = Display.Grid, GridTemplateColumns = GridTrackList.Of(200), GridTemplateRows = GridTrackList.Of(100),
        }, i);
        Run(N(Block, g), 300);
        Box(i, 0, 0, 100, 50);
    }

    [Fact]
    public void Non_replaced_ratio_height_is_at_least_the_content_height()
    {
        // width:100px; aspect-ratio:4 内含 100px 高的子元素 → 100×100（CSS Sizing 4 自动最小尺寸；第七批时 100×25）
        var a = N(Block with { Width = 100, AspectRatio = 4 }, N(Block with { Height = 100 }));
        // 没有内容 → 25；滚动容器、min-height:0 时没有自动最小尺寸 → 25；max-height 仍然限制 → 60
        var empty = N(Block with { Width = 100, AspectRatio = 4 });
        var hidden = N(Block with { Width = 100, AspectRatio = 4, OverflowX = Overflow.Hidden, OverflowY = Overflow.Hidden }, N(Block with { Height = 100 }));
        var min0 = N(Block with { Width = 100, AspectRatio = 4, MinHeight = 0 }, N(Block with { Height = 100 }));
        var maxH = N(Block with { Width = 100, AspectRatio = 4, MaxHeight = 60 }, N(Block with { Height = 100 }));
        Run(N(Block, a, empty, hidden, min0, maxH), 300);
        T.Size(a, 100, 100);
        T.Size(empty, 100, 25);
        T.Size(hidden, 100, 25);
        T.Size(min0, 100, 25);
        T.Size(maxH, 100, 60);
    }

    [Fact]
    public void Ratio_auto_minimum_for_flex_container_and_text_leaf()
    {
        // display:flex; width:100px; aspect-ratio:4 内含 100px 高的项目 → 高 100（Chrome 相同）
        var f = N(Row with { Width = 100, AspectRatio = 4 }, N(Block with { Width = 10, Height = 100 }));
        // 非替换的文本叶子（测量尺寸的比值与样式比值不同）也有自动最小尺寸：width:50px; aspect-ratio:5、三行文本 → 50×60
        // （headless Chromium 实测 50×60）。评审前叶子不施加它，得 50×10、内容溢出
        var t = Text("aaa bbb ccc", Block with { Width = 50, AspectRatio = 5 });
        // 同样的叶子包在块容器里：50×60
        var wrapped = N(Block with { Width = 50, AspectRatio = 5 }, Text("aaa bbb ccc"));
        Run(N(Block, f, t, wrapped), 300);
        T.Size(f, 100, 100);
        T.Size(t, 50, 60);
        T.Size(wrapped, 50, 60);
    }

    [Fact]
    public void Only_is_replaced_decides_the_ratio_auto_minimum_of_a_leaf()
    {
        // 只有 IsReplaced 决定叶子是否按替换元素对待（过渡期「测量比值等于样式比值」的猜测已删除）：
        // 标记的图片（100×50，width:50px）高度按比例 25，不被撑回固有高度；未标记的同样叶子按非替换元素，
        // 自动最小高度是内容高度 50
        var flagged = new LayoutNode(Block with { AspectRatio = 2, Width = 50, IsReplaced = true }) { Measure = new FixedMeasure(100, 50) };
        var unflagged = new LayoutNode(Block with { AspectRatio = 2, Width = 50 }) { Measure = new FixedMeasure(100, 50) };
        Run(N(Block, flagged, unflagged), 300);
        T.Size(flagged, 50, 25);
        T.Size(unflagged, 50, 50);
    }

    [Fact]
    public void Replaced_leaf_has_no_ratio_auto_minimum()
    {
        // 替换元素高度由比例决定，不受内容高度（测量函数的固有高度）影响
        var i = new LayoutNode(Block with { IsReplaced = true, AspectRatio = 4, Width = 100 }) { Measure = new FixedMeasure(10, 500) };
        Run(N(Block, i), 300);
        T.Size(i, 100, 25);
    }

    [Fact]
    public void Scaled_replaced_element_does_not_report_overflow()
    {
        // 100×50 的图片以 width:50px 显示：内容按 50×25 绘制，可滚动溢出就是自身
        var i = Img(100, 50, Block with { Width = 50 });
        Run(N(Block, i), 300);
        T.Size(i, 50, 25);
        T.Eq(50, i.ScrollSize.Width);
        T.Eq(25, i.ScrollSize.Height);
    }

    [Fact]
    public void Flex_order_reorders_items_stably()
    {
        // a、b(order -1)、c(order -1) → b c a（Chrome：b 0、c 10、a 20）
        var a = Text("a"); var b = Text("b", Block with { Order = -1 }); var c = Text("c", Block with { Order = -1 });
        Run(N(Block, N(Row, a, b, c)), 300);
        T.Eq(0, b.X);
        T.Eq(10, c.X);
        T.Eq(20, a.X);
    }

    [Fact]
    public void Order_sort_is_stable_for_many_items()
    {
        var items = Enumerable.Range(0, 100).Select(i => N(Block with { Width = 1, Height = 1, Order = i % 3 == 0 ? 1 : 0 })).ToArray();
        Run(N(Block, N(Row, items)), 300);
        // order 0 的项目（i % 3 != 0）按文档顺序排在前面，然后是 order 1
        float x = 0;
        foreach (var i in Enumerable.Range(0, 100).Where(i => i % 3 != 0).Concat(Enumerable.Range(0, 100).Where(i => i % 3 == 0)))
        {
            T.Eq(x, items[i].X);
            x++;
        }
    }
}

/// <summary>第九批：比值推出的轴按另一轴经 min/max 钳制后的使用值计算（响应式图片 <c>max-width: 100%; height: auto</c>）。</summary>
public class AspectRatioClampTests
{
    [Fact]
    public void Height_from_ratio_uses_width_after_max_width_clamp()
    {
        // <img width=300 height=150 style="display:block; max-width:100%; height:auto"> 在 120px 容器中：120×60（Chrome 相同）
        var i = new LayoutNode(Block with { IsReplaced = true, AspectRatio = 2, Width = 300, MaxWidth = Dimension.Percent(100) }) { Measure = new FixedMeasure(300, 150) };
        Run(N(Block, i), 120);
        Box(i, 0, 0, 120, 60);
        // 非替换元素同理：width 400、max-width 200、aspect-ratio 4 → 200×50
        var d = N(Block with { Width = 400, MaxWidth = 200, AspectRatio = 4 });
        Run(N(Block, d), 1000);
        Box(d, 0, 0, 200, 50);
        // 宽度由高度推出时按钳制后的高度：height 400、max-height 100、ratio 2 → 200×100
        var e = N(Block with { Height = 400, MaxHeight = 100, AspectRatio = 2 });
        Run(N(Block with { Display = Display.Flex, AlignItems = AlignItems.FlexStart }, e), 1000);
        Box(e, 0, 0, 200, 100);
    }
}

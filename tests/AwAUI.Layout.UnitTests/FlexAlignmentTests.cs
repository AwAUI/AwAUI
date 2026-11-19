using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>justify-content、align-items/self、auto 外边距、基线对齐。</summary>
public class FlexAlignmentTests
{
    static (LayoutNode root, LayoutNode a, LayoutNode b, LayoutNode c) Three(JustifyContent j)
    {
        var a = N(Block with { Width = 50, Height = 10 });
        var b = N(Block with { Width = 50, Height = 10 });
        var c = N(Block with { Width = 50, Height = 10 });
        var root = N(Row with { Width = 300, Height = 10, JustifyContent = j }, a, b, c);
        Run(root);
        return (root, a, b, c);
    }

    // 三个 50 宽项目，自由空间 150
    [Theory]
    [InlineData(JustifyContent.Normal, 0, 50, 100)]
    [InlineData(JustifyContent.FlexStart, 0, 50, 100)]
    [InlineData(JustifyContent.Start, 0, 50, 100)]
    [InlineData(JustifyContent.Stretch, 0, 50, 100)]
    [InlineData(JustifyContent.FlexEnd, 150, 200, 250)]
    [InlineData(JustifyContent.End, 150, 200, 250)]
    [InlineData(JustifyContent.Center, 75, 125, 175)]          // 150/2
    [InlineData(JustifyContent.SpaceBetween, 0, 125, 250)]     // 间隔 75
    [InlineData(JustifyContent.SpaceAround, 25, 125, 225)]     // 每项 50，两侧各 25
    [InlineData(JustifyContent.SpaceEvenly, 37.5f, 125, 212.5f)] // 150/4
    public void Justify_content(JustifyContent j, float x1, float x2, float x3)
    {
        var (_, a, b, c) = Three(j);
        T.Eq(x1, a.X);
        T.Eq(x2, b.X);
        T.Eq(x3, c.X);
    }

    [Fact]
    public void Space_between_with_single_item_falls_back_to_start_and_space_around_to_center()
    {
        var a = N(Block with { Width = 50, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, JustifyContent = JustifyContent.SpaceBetween }, a));
        T.Eq(0, a.X);
        var b = N(Block with { Width = 50, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, JustifyContent = JustifyContent.SpaceAround }, b));
        T.Eq(125, b.X);
    }

    [Fact]
    public void Negative_free_space_with_space_around_centers()
    {
        // 两个 200 宽不可收缩项目在 300 中：自由空间 -100 → 按 center：起点 -50
        var a = N(Block with { Width = 200, Height = 10, FlexShrink = 0 });
        var b = N(Block with { Width = 200, Height = 10, FlexShrink = 0 });
        Run(N(Row with { Width = 300, Height = 10, JustifyContent = JustifyContent.SpaceAround }, a, b));
        T.Eq(-50, a.X);
        T.Eq(150, b.X);
    }

    [Fact]
    public void Auto_margins_absorb_free_space_before_justify_content()
    {
        // 左 auto：推到最右 250；两侧 auto：居中 125；justify-content 被忽略
        var a = N(Block with { Width = 50, Height = 10, Margin = Margin(0, 0, 0, Auto) });
        Run(N(Row with { Width = 300, Height = 10, JustifyContent = JustifyContent.Center }, a));
        T.Eq(250, a.X);
        T.Eq(250, a.Margin.Left);

        var b = N(Block with { Width = 50, Height = 10, Margin = Margin(0, Auto, 0, Auto) });
        Run(N(Row with { Width = 300, Height = 10 }, b));
        T.Eq(125, b.X);
    }

    [Fact]
    public void Auto_margin_between_items_pushes_apart()
    {
        // 第一个项目 margin-right:auto 吃掉全部 200 自由空间
        var a = N(Block with { Width = 50, Height = 10, Margin = Margin(0, Auto, 0, 0) });
        var b = N(Block with { Width = 50, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10 }, a, b));
        T.Eq(0, a.X);
        T.Eq(250, b.X);
    }

    [Theory]
    [InlineData(AlignItems.FlexStart, 0, 20)]
    [InlineData(AlignItems.Start, 0, 20)]
    [InlineData(AlignItems.FlexEnd, 80, 20)]
    [InlineData(AlignItems.End, 80, 20)]
    [InlineData(AlignItems.Center, 40, 20)]
    [InlineData(AlignItems.Stretch, 0, 100)]
    [InlineData(AlignItems.Normal, 0, 100)]
    public void Align_items_with_auto_height(AlignItems align, float y, float h)
    {
        // 文本项目自然高 20；容器 100 高
        var t = Text("abc");
        Run(N(Row with { Width = 300, Height = 100, AlignItems = align }, t));
        T.Eq(y, t.Y);
        T.Eq(h, t.Height);
    }

    [Fact]
    public void Stretch_does_not_override_explicit_cross_size()
    {
        var a = N(Block with { Width = 10, Height = 20 });
        Run(N(Row with { Width = 300, Height = 100 }, a));
        Box(a, 0, 0, 10, 20);
    }

    [Fact]
    public void Stretch_is_clamped_by_max_cross_size()
    {
        var a = N(Block with { Width = 10, MaxHeight = 50 });
        Run(N(Row with { Width = 300, Height = 100 }, a));
        Box(a, 0, 0, 10, 50);
    }

    [Fact]
    public void Align_self_overrides_align_items()
    {
        var a = N(Block with { Width = 10, Height = 20, AlignSelf = AlignSelf.FlexEnd });
        var b = N(Block with { Width = 10, Height = 20, AlignSelf = AlignSelf.Center });
        var c = N(Block with { Width = 10, AlignSelf = AlignSelf.Stretch });
        var d = N(Block with { Width = 10, Height = 20 });
        Run(N(Row with { Width = 300, Height = 100, AlignItems = AlignItems.FlexStart }, a, b, c, d));
        T.Eq(80, a.Y);
        T.Eq(40, b.Y);
        T.Eq(100, c.Height);
        T.Eq(0, d.Y);
    }

    [Fact]
    public void Cross_axis_auto_margins()
    {
        // margin-top:auto → 贴底 80；上下 auto → 居中 40；auto 外边距也阻止拉伸
        var a = N(Block with { Width = 10, Height = 20, Margin = Margin(Auto, 0, 0, 0) });
        var b = N(Block with { Width = 10, Height = 20, Margin = Margin(Auto, 0, Auto, 0) });
        var c = Text("abc", Block with { Margin = Margin(0, 0, Auto, 0) });
        Run(N(Row with { Width = 300, Height = 100 }, a, b, c));
        T.Eq(80, a.Y);
        T.Eq(40, b.Y);
        T.Eq(0, c.Y);
        T.Eq(20, c.Height);
    }

    [Fact]
    public void Baseline_alignment_single_line()
    {
        // A：padding-top 10 → 基线 10+16=26；B：基线 16。共享基线 26 → B 下移 10
        // 行高 = max(上方 26) + max(下方：A 30-26=4，B 20-16=4) = 30
        var a = Text("aaa", Block with { Padding = new(10, 0, 0, 0) });
        var b = Text("bbb");
        var root = N(Row with { Width = 300, AlignItems = AlignItems.Baseline }, a, b);
        Run(root);
        Box(a, 0, 0, 30, 30);
        Box(b, 30, 10, 30, 20);
        T.Size(root, 300, 30);
        // 容器自身的基线是共享基线
        T.Eq(26, root.Baseline);
    }

    [Fact]
    public void Baseline_alignment_uses_nested_container_baseline()
    {
        // B 是 column 容器，padding-top 20，第一个子文本基线 16 → B 的基线 36；A 的基线 16
        // → A 下移 20
        var a = Text("aaa");
        var inner = Text("bbb");
        var b = N(Column with { Padding = new(20, 0, 0, 0) }, inner);
        var root = N(Row with { Width = 300, AlignItems = AlignItems.Baseline }, a, b);
        Run(root);
        T.Eq(20, a.Y);
        T.Eq(0, b.Y);
        T.Eq(36, b.Baseline);
    }

    [Fact]
    public void Item_without_baseline_synthesizes_from_border_box_bottom()
    {
        // 空盒高 40 没有基线 → 以底边 40 合成；文本基线 16 → 文本下移 24
        var a = N(Block with { Width = 10, Height = 40 });
        var t = Text("aaa");
        Run(N(Row with { Width = 300, AlignItems = AlignItems.Baseline }, a, t));
        T.Eq(0, a.Y);
        T.Eq(24, t.Y);
    }
}

using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>简化块布局与盒模型：堆叠、撑满、auto 外边距、box-sizing、百分比、aspect-ratio、固有尺寸关键字。</summary>
public class BlockAndBoxTests
{
    [Fact]
    public void Block_children_stack_vertically_without_margin_collapsing()
    {
        // 外边距不折叠：c1 y=10；c2 y = 10+50+10 + 10 = 80；根高 = 10+50+10+10+30+10 = 120
        var c1 = N(Block with { Height = 50, Margin = Margin(10, 0, 10, 0) });
        var c2 = N(Block with { Height = 30, Margin = Margin(10, 0, 10, 0) });
        var root = N(Block, c1, c2);
        Run(root, 500);
        Box(root, 0, 0, 500, 120);
        Box(c1, 0, 10, 500, 50);
        Box(c2, 0, 80, 500, 30);
    }

    [Fact]
    public void Auto_width_fills_containing_block_minus_margins()
    {
        var c = N(Block with { Height = 10, Margin = Margin(0, 30, 0, 20) });
        Run(N(Block, c), 500);
        Box(c, 20, 0, 450, 10);
    }

    [Fact]
    public void Horizontal_auto_margins_center_or_push()
    {
        var centered = N(Block with { Width = 100, Height = 10, Margin = Margin(0, Auto, 0, Auto) });
        var right = N(Block with { Width = 100, Height = 10, Margin = Margin(0, 0, 0, Auto) });
        Run(N(Block, centered, right), 500);
        T.Eq(200, centered.X);
        T.Eq(200, centered.Margin.Left);
        T.Eq(400, right.X);
    }

    [Fact]
    public void Rtl_block_aligns_fixed_width_children_to_the_right()
    {
        var c = N(Block with { Width = 100, Height = 10, Margin = Margin(0, 10, 0, 0) });
        Run(N(Block with { Direction = Direction.Rtl }, c), 500);
        T.Eq(390, c.X);
    }

    [Fact]
    public void Max_width_limits_stretched_width()
    {
        var c = N(Block with { Height = 10, MaxWidth = 300 });
        Run(N(Block, c), 500);
        Box(c, 0, 0, 300, 10);
    }

    [Fact]
    public void Root_padding_and_border_offset_children()
    {
        var c = N(Block with { Height = 10 });
        var root = N(Block with { Padding = new(5, 10), Border = new(2f) }, c);
        Run(root, 500);
        // 根宽 500（border-box 撑满）；子节点 x = 2+10，宽 = 500 - 24
        Box(c, 12, 7, 476, 10);
        T.Size(root, 500, 24);
    }

    [Fact]
    public void Content_box_vs_border_box()
    {
        // content-box：100 + 2×10 padding + 2×5 border = 130；border-box：100，内容 70
        var a = N(Block with { Width = 100, Height = 50, Padding = new(10), Border = new(5f) });
        var b = N(Block with { Width = 100, Height = 50, Padding = new(10), Border = new(5f), BoxSizing = BoxSizing.BorderBox });
        Run(N(Block, a, b), 500);
        T.Size(a, 130, 80);
        T.Size(b, 100, 50);
        Assert.Equal(new Rect(15, 15, 70, 20), b.ContentBox);
        Assert.Equal(new Rect(5, 5, 90, 40), b.PaddingBox);
    }

    [Fact]
    public void Border_box_size_cannot_be_smaller_than_padding_and_border()
    {
        var a = N(Block with { Width = 10, Height = 10, Padding = new(20), BoxSizing = BoxSizing.BorderBox });
        Run(N(Block, a), 500);
        T.Size(a, 40, 40);
    }

    [Fact]
    public void Percentages_resolve_against_containing_block_width_for_padding_and_margin()
    {
        // 包含块宽 400：width 50% = 200；padding 10% = 40（上下也相对宽度）；margin-top 5% = 20
        var c = N(Block with
        {
            Width = Dimension.Percent(50),
            Padding = new(LengthPercentage.Percent(10)),
            Margin = new(LengthPercentageAuto.Percent(5), 0, 0, 0),
        });
        Run(N(Block with { Width = 400, Height = 300 }, c), 1000);
        Box(c, 0, 20, 280, 80);
        T.Eq(40, c.Padding.Top);
    }

    [Fact]
    public void Percentage_height_needs_definite_containing_block_height()
    {
        var a = N(Block with { Height = Dimension.Percent(50) });
        Run(N(Block with { Height = 300 }, a), 500);
        T.Eq(150, a.Height);

        // 包含块高度 auto → 百分比高度按 auto（内容高 0）
        var b = N(Block with { Height = Dimension.Percent(50) });
        Run(N(Block, b), 500);
        T.Eq(0, b.Height);
    }

    [Fact]
    public void Calc_dimensions_resolve_with_percent_basis()
    {
        // calc(100% - 30px) 在 400 宽的包含块中 → 370
        var c = N(Block with { Width = Dimension.Of(new CalcPercentMinus(100, 30)), Height = 10 });
        Run(N(Block with { Width = 400 }, c), 1000);
        T.Eq(370, c.Width);
    }

    sealed class CalcPercentMinus : LayoutCalc
    {
        readonly float _percent, _px;
        public CalcPercentMinus(float percent, float px) { _percent = percent; _px = px; }
        public override float Resolve(float basis) => basis * _percent / 100 - _px;
    }

    [Fact]
    public void Min_and_max_size_clamp_with_min_winning()
    {
        var a = N(Block with { Width = 500, MaxWidth = 200, Height = 10 });
        var b = N(Block with { Width = 50, MinWidth = 100, Height = 10 });
        var c = N(Block with { Width = 50, MinWidth = 300, MaxWidth = 200, Height = 10 });
        Run(N(Block, a, b, c), 1000);
        T.Eq(200, a.Width);
        T.Eq(100, b.Width);
        T.Eq(300, c.Width);
    }

    [Fact]
    public void Aspect_ratio_derives_height_from_width()
    {
        // 撑满的宽 400，aspect-ratio 2 → 高 200；height 指定时宽由比例推出
        var a = N(Block with { AspectRatio = 2 });
        var b = N(Block with { Height = 50, AspectRatio = 2, Margin = Margin(0, Auto, 0, Auto) });
        Run(N(Block, a, b), 400);
        T.Size(a, 400, 200);
        T.Size(b, 100, 50);
    }

    [Fact]
    public void Text_leaf_wraps_at_block_width()
    {
        // "aaa bbb ccc ddd"：100 宽时 "aaa bbb"(70) / "ccc ddd"(70) 两行
        var t = Text("aaa bbb ccc ddd");
        Run(N(Block with { Width = 100 }, t), 500);
        Box(t, 0, 0, 100, 40);
    }

    [Theory]
    [InlineData(DimensionKind.MinContent, 30, 80)]  // 最长单词 30，四行
    [InlineData(DimensionKind.MaxContent, 150, 20)] // 单行 150
    [InlineData(DimensionKind.FitContent, 100, 40)] // min(150, max(30, 100))
    public void Intrinsic_size_keywords(DimensionKind kind, float w, float h)
    {
        var width = kind switch
        {
            DimensionKind.MinContent => Dimension.MinContent,
            DimensionKind.MaxContent => Dimension.MaxContent,
            _ => Dimension.FitContent,
        };
        var t = Text("aaa bbb ccc ddd");
        var box = N(Block with { Width = width, Padding = new(0, 5) }, t);
        Run(N(Block with { Width = 110 }, box), 1000);
        // 盒宽 = 内容固有宽 + padding 10（fit-content 的可用空间 110 已扣除 padding → 内容 100）
        T.Size(box, w + 10, h);
        T.Size(t, w, h);
    }

    [Fact]
    public void Block_intrinsic_width_is_max_of_children_contributions()
    {
        // max-content：子节点贡献 = 文本 70 + 外边距 20 与 固定 50 中的较大者 → 90
        var t = Text("aaa bbb", Block with { Margin = Margin(0, 10, 0, 10) });
        var f = N(Block with { Width = 50, Height = 5 });
        var box = N(Block with { Width = Dimension.MaxContent }, t, f);
        Run(N(Block, box), 1000);
        T.Eq(90, box.Width);
        Box(t, 10, 0, 70, 20);
    }

    [Fact]
    public void Inline_flex_uses_fit_content_width_in_block()
    {
        // 文本 70 + 20：max-content 90 ≤ 500 → 90
        var a = Text("aaa bbb");
        var b = Text("cc");
        var flex = N(Row with { Display = Display.InlineFlex }, a, b);
        Run(N(Block, flex), 500);
        Box(flex, 0, 0, 90, 20);
        Box(b, 70, 0, 20, 20);
    }

    [Fact]
    public void Inline_flex_in_narrow_space_shrinks_to_available_with_min_content_floor()
    {
        // 可用 60：max-content 90 > 60，min-content（单行）= 30+20 = 50 → 宽 max(50,60) = 60
        // 收缩 30：按基准 70:20 → 46.67 / 13.33；b 的自动最小 20 违例 → 冻结 20，a = 40
        // a 在 40 宽时断成两行 → 高 40；b 被拉伸到 40
        var a = Text("aaa bbb");
        var b = Text("cc");
        var flex = N(Row with { Display = Display.InlineFlex }, a, b);
        Run(N(Block, flex), 60);
        Box(flex, 0, 0, 60, 40);
        Box(a, 0, 0, 40, 40);
        Box(b, 40, 0, 20, 40);
    }
}

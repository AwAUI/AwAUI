using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>Flex 主轴尺寸：基准尺寸、伸展、收缩、min/max 冻结循环、自动最小尺寸、gap。</summary>
public class FlexSizingTests
{
    static LayoutStyle Item(float grow = 0, float shrink = 1) => Block with { FlexGrow = grow, FlexShrink = shrink };

    [Fact]
    public void Grow_distributes_by_factor_from_zero_basis()
    {
        // 300 宽，basis 0：自由空间 300 按 1:2 分 → 100、200。交叉轴默认 stretch → 高 100
        var a = N(Item(1) with { FlexBasis = 0 });
        var b = N(Item(2) with { FlexBasis = 0 });
        var root = N(Row with { Width = 300, Height = 100 }, a, b);
        Run(root);
        Box(a, 0, 0, 100, 100);
        Box(b, 100, 0, 200, 100);
    }

    [Fact]
    public void Grow_adds_free_space_on_top_of_auto_basis()
    {
        // 基准 50 与 100（来自 width），自由空间 300-150=150 平分 → 125、175
        var a = N(Item(1) with { Width = 50 });
        var b = N(Item(1) with { Width = 100 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 125, 10);
        Box(b, 125, 0, 175, 10);
    }

    [Fact]
    public void Grow_max_violation_freezes_and_redistributes()
    {
        // 第一轮各得 150；A 超过 max 50（违例 -100）→ 总违例为负，冻结 A；第二轮 B = 300 - 50 = 250
        var a = N(Item(1) with { FlexBasis = 0, MaxWidth = 50 });
        var b = N(Item(1) with { FlexBasis = 0 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 50, 10);
        Box(b, 50, 0, 250, 10);
    }

    [Fact]
    public void Grow_min_violation_freezes_and_redistributes()
    {
        // 第一轮各得 100；C 的 min 160 违例 +60，总违例为正 → 冻结 C=160；A、B 平分剩余 140 → 70
        var a = N(Item(1) with { FlexBasis = 0 });
        var b = N(Item(1) with { FlexBasis = 0 });
        var c = N(Item(1) with { FlexBasis = 0, MinWidth = 160 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b, c);
        Run(root);
        Box(a, 0, 0, 70, 10);
        Box(b, 70, 0, 70, 10);
        Box(c, 140, 0, 160, 10);
    }

    [Fact]
    public void Sum_of_grow_factors_below_one_leaves_free_space()
    {
        // grow 合计 0.5 < 1：只分配初始自由空间的 50%：(300-0)*0.25 = 75 各一份
        var a = N(Item(0.25f) with { FlexBasis = 0 });
        var b = N(Item(0.25f) with { FlexBasis = 0 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 75, 10);
        Box(b, 75, 0, 75, 10);
    }

    [Fact]
    public void Shrink_is_weighted_by_base_size()
    {
        // 溢出 100；缩放收缩因子 1×100 与 1×300 → A 收缩 25、B 收缩 75 → 75、225
        var a = N(Item() with { Width = 100 });
        var b = N(Item() with { Width = 300 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 75, 10);
        Box(b, 75, 0, 225, 10);
    }

    [Fact]
    public void Shrink_min_violation_freezes()
    {
        // 各收缩 50 → 150；A 的 min 180 违例 → 冻结 180；B = 300 - 180 = 120
        var a = N(Item() with { Width = 200, MinWidth = 180 });
        var b = N(Item() with { Width = 200 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 180, 10);
        Box(b, 180, 0, 120, 10);
    }

    [Fact]
    public void Flex_shrink_zero_overflows()
    {
        var a = N(Item(shrink: 0) with { Width = 200 });
        var b = N(Item() with { Width = 200 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 200, 10);
        Box(b, 200, 0, 100, 10);
    }

    [Fact]
    public void Shrink_factor_weights()
    {
        // 溢出 100；缩放因子 1×200 与 3×200 → A 收缩 25、B 收缩 75
        var a = N(Item(shrink: 1) with { Width = 200 });
        var b = N(Item(shrink: 3) with { Width = 200 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 175, 10);
        Box(b, 175, 0, 125, 10);
    }

    [Fact]
    public void Automatic_minimum_size_prevents_shrinking_below_min_content()
    {
        // 单个 150px 的单词：min-content 150。容器 100，项目不能收缩到内容以下（min-width:auto）
        var t = Text("aaaaaaaaaaaaaaa");
        var root = N(Row with { Width = 100, Height = 50, AlignItems = AlignItems.FlexStart }, t);
        Run(root);
        Box(t, 0, 0, 150, 20);
    }

    [Fact]
    public void Min_width_zero_disables_automatic_minimum()
    {
        var t = Text("aaaaaaaaaaaaaaa", Block with { MinWidth = 0 });
        var root = N(Row with { Width = 100, Height = 50, AlignItems = AlignItems.FlexStart }, t);
        Run(root);
        T.Size(t, 100, 20);
    }

    [Fact]
    public void Scroll_container_has_no_automatic_minimum()
    {
        var t = Text("aaaaaaaaaaaaaaa", Block with { OverflowX = Overflow.Hidden });
        var root = N(Row with { Width = 100, Height = 50, AlignItems = AlignItems.FlexStart }, t);
        Run(root);
        T.Size(t, 100, 20);
    }

    [Fact]
    public void Automatic_minimum_is_capped_by_specified_width()
    {
        // min-width:auto = min(内容 150, 指定宽度 120) = 120；容器 100 → 收缩到 120 为止
        var t = Text("aaaaaaaaaaaaaaa", Block with { Width = 120 });
        var root = N(Row with { Width = 100, Height = 50, AlignItems = AlignItems.FlexStart }, t);
        Run(root);
        T.Size(t, 120, 20);
    }

    [Fact]
    public void Text_items_shrink_from_max_content_and_wrap()
    {
        // "aaa bbb ccc" max-content 110、min-content 30。两个合计 220 > 200，按基准等比收缩 10 → 100
        // 宽 100 时断成两行（aaa bbb / ccc）→ 高 40
        var a = Text("aaa bbb ccc");
        var b = Text("aaa bbb ccc");
        var root = N(Row with { Width = 200, AlignItems = AlignItems.FlexStart }, a, b);
        Run(root);
        Box(a, 0, 0, 100, 40);
        Box(b, 100, 0, 100, 40);
        T.Size(root, 200, 40);
    }

    [Fact]
    public void Flex_basis_length_overrides_content()
    {
        // basis 50：项目宽 50（大于 min-content 30），文本按 50 断行 → 两行
        var t = Text("aaa bbb", Block with { FlexBasis = 50, FlexShrink = 0 });
        var root = N(Row with { Width = 300, AlignItems = AlignItems.FlexStart }, t);
        Run(root);
        T.Size(t, 50, 40);
    }

    [Fact]
    public void Flex_basis_auto_uses_width_and_content_ignores_it()
    {
        // basis auto → 用 width 80；basis content → 用 max-content 70
        var a = Text("aaa bbb", Block with { Width = 80 });
        var b = Text("aaa bbb", Block with { Width = 80, FlexBasis = Dimension.Content });
        var root = N(Row with { Width = 300, AlignItems = AlignItems.FlexStart }, a, b);
        Run(root);
        Box(a, 0, 0, 80, 20);
        Box(b, 80, 0, 70, 20);
    }

    [Fact]
    public void Percentage_flex_basis_resolves_against_container_inner_main_size()
    {
        // 容器内容盒宽 400（padding 不计入），25% → 100
        var a = N(Item() with { FlexBasis = Dimension.Percent(25) });
        var root = N(Row with { Width = 400, Height = 10, Padding = new(0, 20) }, a);
        Run(root);
        Box(a, 20, 0, 100, 10);
    }

    [Fact]
    public void Flex_basis_respects_box_sizing()
    {
        // content-box：basis 100 + padding 20 → 边框盒 120；border-box：basis 100 → 100
        var a = N(Item(shrink: 0) with { FlexBasis = 100, Padding = new(0, 10) });
        var b = N(Item(shrink: 0) with { FlexBasis = 100, Padding = new(0, 10), BoxSizing = BoxSizing.BorderBox });
        var root = N(Row with { Width = 400, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 120, 10);
        Box(b, 120, 0, 100, 10);
    }

    [Fact]
    public void Column_gap_is_subtracted_before_flexing()
    {
        // 三个 grow 项目，gap 10：(300-20)/3 = 93.33
        var a = N(Item(1) with { FlexBasis = 0 });
        var b = N(Item(1) with { FlexBasis = 0 });
        var c = N(Item(1) with { FlexBasis = 0 });
        var root = N(Row with { Width = 300, Height = 10, ColumnGap = 10 }, a, b, c);
        Run(root);
        Box(a, 0, 0, 93.333f, 10);
        Box(b, 103.333f, 0, 93.333f, 10);
        Box(c, 206.667f, 0, 93.333f, 10);
    }

    [Fact]
    public void Percentage_gap_resolves_against_container_content_box()
    {
        // column-gap 10% × 300 = 30 → (300-60)/3 = 80
        var a = N(Item(1) with { FlexBasis = 0 });
        var b = N(Item(1) with { FlexBasis = 0 });
        var c = N(Item(1) with { FlexBasis = 0 });
        var root = N(Row with { Width = 300, Height = 10, ColumnGap = LengthPercentage.Percent(10) }, a, b, c);
        Run(root);
        Box(a, 0, 0, 80, 10);
        Box(b, 110, 0, 80, 10);
        Box(c, 220, 0, 80, 10);
    }

    [Fact]
    public void Item_margins_take_main_space()
    {
        // 外边距 10 两侧：自由空间 300 - 40 = 260 平分 → 130；x = 10 和 160
        var a = N(Item(1) with { FlexBasis = 0, Margin = Margin(0, 10, 0, 10) });
        var b = N(Item(1) with { FlexBasis = 0, Margin = Margin(0, 10, 0, 10) });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 10, 0, 130, 10);
        Box(b, 160, 0, 130, 10);
        T.Eq(10, a.Margin.Left);
    }

    [Fact]
    public void Container_padding_and_border_offset_items()
    {
        var a = N(Item(1));
        var root = N(Row with { Width = 300, Height = 100, Padding = new(10), Border = new(5f), BoxSizing = BoxSizing.BorderBox }, a);
        Run(root);
        // 内容盒：x 15..285（270 宽），y 15..85（70 高）
        Box(a, 15, 15, 270, 70);
        T.Eq(5, root.Border.Left);
        T.Eq(10, root.Padding.Top);
    }

    [Fact]
    public void Nested_flex_containers()
    {
        // 外层 row 400×100：B 固定 100，A grow → 300。A 是 column：两个文本各 20 高，第二个 grow → 80
        var t1 = Text("aaa");
        var t2 = Text("bbb", Block with { FlexGrow = 1 });
        var a = N(Column with { FlexGrow = 1 }, t1, t2);
        var b = N(Block with { Width = 100 });
        var root = N(Row with { Width = 400, Height = 100 }, a, b);
        Run(root);
        Box(a, 0, 0, 300, 100);
        Box(b, 300, 0, 100, 100);
        Box(t1, 0, 0, 300, 20);
        Box(t2, 0, 20, 300, 80);
    }

    [Fact]
    public void Row_container_height_from_tallest_item()
    {
        var a = N(Block with { Width = 10, Height = 30 });
        var b = N(Block with { Width = 10, Height = 50 });
        var root = N(Row with { Width = 300 }, a, b);
        Run(root);
        T.Size(root, 300, 50);
        // a 的 height 已指定，不被拉伸
        Box(a, 0, 0, 10, 30);
    }
}

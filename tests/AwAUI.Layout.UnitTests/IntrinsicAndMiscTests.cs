using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>flex 容器的固有尺寸，以及 flex 项目上的百分比、相对定位、嵌套基线等组合情形。</summary>
public class IntrinsicAndMiscTests
{
    [Fact]
    public void Row_min_content_is_sum_of_item_min_contents_when_nowrap()
    {
        // "aaa bbb" min-content 30，"cc" 20 → 单行 50
        var row = N(Row with { Width = Dimension.MinContent }, Text("aaa bbb"), Text("cc"));
        Run(N(Block, row));
        T.Eq(50, row.Width);
    }

    [Fact]
    public void Row_min_content_is_largest_item_when_wrapping()
    {
        var row = N(Row with { Width = Dimension.MinContent, FlexWrap = FlexWrap.Wrap }, Text("aaa bbb"), Text("cc"));
        Run(N(Block, row));
        T.Eq(30, row.Width);
    }

    [Fact]
    public void Row_max_content_includes_gaps_margins_and_padding()
    {
        // 70 + 10(gap) + 20 + 5(右外边距) + 2×4(padding) = 113
        var row = N(Row with { Width = Dimension.MaxContent, ColumnGap = 10, Padding = new(0, 4) },
            Text("aaa bbb"), Text("cc", Block with { Margin = Margin(0, 5, 0, 0) }));
        Run(N(Block, row));
        T.Eq(113, row.Width);
    }

    [Fact]
    public void Column_flex_shrink_to_fit_width_is_widest_item()
    {
        // 绝对定位的 column 容器：宽 = max(70, 20) = 70；高 = 20 + 20
        var col = N(Column with { Position = Position.Absolute, Inset = Inset(top: 0, left: 0) }, Text("aaa bbb"), Text("cc"));
        Run(N(Block with { Position = Position.Relative, Width = 500, Height = 500 }, col));
        T.Size(col, 70, 40);
    }

    [Fact]
    public void Column_main_axis_auto_margin_pushes_to_end()
    {
        var a = N(Block with { Height = 20 });
        var b = N(Block with { Height = 30, Margin = Margin(Auto, 0, 0, 0) });
        Run(N(Column with { Width = 100, Height = 200 }, a, b));
        T.Eq(0, a.Y);
        T.Eq(170, b.Y);
    }

    [Fact]
    public void Flex_item_percent_padding_and_margin_use_container_inner_width()
    {
        // 容器内容盒 400 宽：padding-top 10% = 40；margin-left 5% = 20
        var a = N(Block with { Width = 100, Padding = new(LengthPercentage.Percent(10), 0, 0, 0), Margin = new(0, 0, 0, LengthPercentageAuto.Percent(5)) });
        Run(N(Row with { Width = 400, AlignItems = AlignItems.FlexStart }, a));
        Box(a, 20, 0, 100, 40);
    }

    [Fact]
    public void Flex_item_percent_height_resolves_against_definite_container_height()
    {
        var a = N(Block with { Width = 10, Height = Dimension.Percent(50) });
        Run(N(Row with { Width = 400, Height = 200 }, a));
        T.Eq(100, a.Height);
    }

    [Fact]
    public void Stretched_item_gives_children_a_definite_height()
    {
        // 行容器高 200，项目被拉伸 → 项目内 50% 高的子节点 = 100
        var inner = N(Block with { Height = Dimension.Percent(50) });
        var item = N(Block with { Width = 10 }, inner);
        Run(N(Row with { Width = 400, Height = 200 }, item));
        T.Eq(200, item.Height);
        T.Eq(100, inner.Height);
    }

    [Fact]
    public void Relative_offset_on_flex_item()
    {
        var a = N(Block with { Width = 50, Height = 10, Position = Position.Relative, Inset = Inset(top: 3, left: 4) });
        var b = N(Block with { Width = 50, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10 }, a, b));
        Box(a, 4, 3, 50, 10);
        Box(b, 50, 0, 50, 10);
    }

    [Fact]
    public void Nested_container_baseline_comes_from_its_baseline_aligned_items()
    {
        // B 是 row 容器：第一项（盒）不参与基线对齐，第二项（文本，margin-top 10）参与 → B 的基线 = 10 + 16 = 26
        // 外层按基线对齐：A 的基线 16 → A 下移 10
        var a = Text("aaa");
        var box = N(Block with { Width = 10, Height = 40 });
        var t = Text("bbb", Block with { AlignSelf = AlignSelf.Baseline, Margin = Margin(10, 0, 0, 0) });
        var b = N(Row with { AlignItems = AlignItems.FlexStart }, box, t);
        Run(N(Row with { Width = 300, AlignItems = AlignItems.Baseline }, a, b));
        T.Eq(26, b.Baseline);
        T.Eq(10, a.Y);
        T.Eq(0, b.Y);
    }

    [Fact]
    public void Absolute_child_inside_display_contents_uses_box_parent_static_position()
    {
        // contents 节点被展开：绝对定位节点的静态位置由 row 容器（居中）决定，坐标相对盒父节点（容器）
        var abs = N(Block with { Position = Position.Absolute, Width = 20, Height = 20 });
        var contents = N(Block with { Display = Display.Contents }, abs);
        var root = N(Row with { Position = Position.Relative, Width = 100, Height = 100, JustifyContent = JustifyContent.Center, AlignItems = AlignItems.Center }, contents);
        Run(root);
        Box(abs, 40, 40, 20, 20);
    }

    [Fact]
    public void Wrap_with_max_width_breaks_lines_in_shrink_to_fit_context()
    {
        // inline-flex 换行容器，max-width 100：每行最多两个 40 宽项目
        var items = new LayoutNode[3];
        for (int i = 0; i < 3; i++) items[i] = N(Block with { Width = 40, Height = 10 });
        var flex = N(Row with { Display = Display.InlineFlex, FlexWrap = FlexWrap.Wrap, MaxWidth = 100 }, items);
        Run(N(Block, flex), 500);
        T.Size(flex, 100, 20);
        Box(items[2], 0, 10, 40, 10);
    }

    [Fact]
    public void Content_box_and_scroll_size_for_leaf()
    {
        var t = Text("aaa", Block with { Padding = new(5), Border = new(1f) });
        Run(N(Block with { Width = 100 }, t));
        // 边框盒 100 × (20 + 10 + 2)；内容盒 (6, 6, 88, 20)
        T.Size(t, 100, 32);
        Assert.Equal(new Rect(6, 6, 88, 20), t.ContentBox);
        T.Eq(16 + 6, t.Baseline);
    }
}

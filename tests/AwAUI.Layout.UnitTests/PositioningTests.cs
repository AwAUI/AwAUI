using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>相对定位与绝对/固定定位。</summary>
public class PositioningTests
{
    // 包含块：border-box 400×300，border 5，padding 10 → 内边距盒 (5,5) 起 390×290
    static LayoutNode Container(params LayoutNode[] children) =>
        N(Block with
        {
            Position = Position.Relative,
            Width = 400,
            Height = 300,
            BoxSizing = BoxSizing.BorderBox,
            Border = new(5f),
            Padding = new(10),
        }, children);

    static LayoutStyle Abs => Block with { Position = Position.Absolute };

    [Fact]
    public void Relative_offset_does_not_affect_siblings()
    {
        var a = N(Block with { Height = 10, Position = Position.Relative, Inset = Inset(top: 5, left: 7) });
        var b = N(Block with { Height = 10 });
        Run(N(Block, a, b), 100);
        Box(a, 7, 5, 100, 10);
        Box(b, 0, 10, 100, 10);
    }

    [Fact]
    public void Relative_right_and_bottom_offsets_are_negated()
    {
        var a = N(Block with { Height = 10, Position = Position.Relative, Inset = Inset(bottom: 5, right: 7) });
        Run(N(Block, a), 100);
        Box(a, -7, -5, 100, 10);
    }

    [Fact]
    public void Absolute_left_top()
    {
        var a = N(Abs with { Width = 50, Height = 50, Inset = Inset(top: 20, left: 10) });
        Run(Container(a));
        // 相对内边距盒：x = 5 + 10，y = 5 + 20
        Box(a, 15, 25, 50, 50);
    }

    [Fact]
    public void Absolute_right_bottom()
    {
        var a = N(Abs with { Width = 50, Height = 50, Inset = Inset(right: 10, bottom: 20) });
        Run(Container(a));
        // x = 5 + 390 - 10 - 50 = 335；y = 5 + 290 - 20 - 50 = 225
        Box(a, 335, 225, 50, 50);
    }

    [Fact]
    public void Absolute_left_and_right_stretch_auto_width()
    {
        var a = N(Abs with { Height = 10, Inset = Inset(top: 0, left: 10, right: 20) });
        Run(Container(a));
        Box(a, 15, 5, 360, 10);
    }

    [Fact]
    public void Absolute_all_insets_stretch_both_axes_with_margins()
    {
        var a = N(Abs with { Inset = new(LengthPercentageAuto.Px(0)), Margin = Margin(10) });
        Run(Container(a));
        Box(a, 15, 15, 370, 270);
    }

    [Fact]
    public void Absolute_auto_margins_center_when_insets_and_size_are_fixed()
    {
        // 水平：390 - 0 - 0 - 100 = 290 → 各 145；垂直：290 - 50 = 240 → 各 120
        var a = N(Abs with { Width = 100, Height = 50, Inset = new(LengthPercentageAuto.Px(0)), Margin = Margin(Auto) });
        Run(Container(a));
        Box(a, 150, 125, 100, 50);
    }

    [Fact]
    public void Absolute_over_constrained_ignores_right_in_ltr_and_left_in_rtl()
    {
        var a = N(Abs with { Width = 100, Height = 10, Inset = Inset(top: 0, left: 10, right: 10) });
        Run(Container(a));
        T.Eq(15, a.X);

        var b = N(Abs with { Width = 100, Height = 10, Inset = Inset(top: 0, left: 10, right: 10) });
        var root = Container(b);
        root.Style = root.Style with { Direction = Direction.Rtl };
        Run(root);
        T.Eq(5 + 390 - 10 - 100, b.X);
    }

    [Fact]
    public void Absolute_percentages_resolve_against_padding_box()
    {
        // 50% × 390 = 195；高 10% × 290 = 29；left 10% × 390 = 39
        var a = N(Abs with { Width = Dimension.Percent(50), Height = Dimension.Percent(10), Inset = new(LengthPercentageAuto.Percent(0), LengthPercentageAuto.Auto, LengthPercentageAuto.Auto, LengthPercentageAuto.Percent(10)) });
        Run(Container(a));
        Box(a, 44, 5, 195, 29);
    }

    [Fact]
    public void Absolute_auto_size_shrinks_to_fit_content()
    {
        // 文本 max-content 150 能放下 → 宽 150 高 20
        var t = Text("aaa bbb ccc ddd", Abs with { Inset = Inset(top: 0, left: 0) });
        Run(Container(t));
        Box(t, 5, 5, 150, 20);
    }

    [Fact]
    public void Absolute_shrink_to_fit_wraps_in_narrow_containing_block()
    {
        // 可用宽 = 390 - left 300 = 90：fit-content = min(150, max(30, 90)) → 文本按 90 断行：
        // "aaa bbb"(70) / "ccc ddd"(70) → 宽 70 高 40
        var t = Text("aaa bbb ccc ddd", Abs with { Inset = Inset(top: 0, left: 300) });
        Run(Container(t));
        Box(t, 305, 5, 70, 40);
    }

    [Fact]
    public void Absolute_static_position_in_block_flow()
    {
        // 无 inset：静态位置在内容盒内当前流的位置（前一个子节点之后）
        var before = N(Block with { Height = 40 });
        var a = N(Abs with { Width = 20, Height = 20 });
        var after = N(Block with { Height = 10 });
        Run(Container(before, a, after));
        Box(a, 15, 55, 20, 20);
        // 不占流内空间
        T.Eq(55, after.Y);
    }

    [Fact]
    public void Absolute_static_position_in_rtl_block_aligns_right()
    {
        var a = N(Abs with { Width = 20, Height = 20 });
        var root = Container(a);
        root.Style = root.Style with { Direction = Direction.Rtl };
        Run(root);
        // 内容盒右边界 = 400 - 15 = 385
        Box(a, 365, 15, 20, 20);
    }

    [Fact]
    public void Absolute_static_position_in_flex_container_uses_alignment()
    {
        // 视作唯一的 flex 项目：justify-content center、align-items center → (300-50)/2, (100-20)/2
        var a = N(Abs with { Width = 50, Height = 20 });
        var root = N(Row with { Position = Position.Relative, Width = 300, Height = 100, JustifyContent = JustifyContent.Center, AlignItems = AlignItems.Center }, a);
        Run(root);
        Box(a, 125, 40, 50, 20);

        // flex-end + 只指定 top：水平仍取静态位置
        var b = N(Abs with { Width = 50, Height = 20, Inset = Inset(top: 5) });
        Run(N(Row with { Position = Position.Relative, Width = 300, Height = 100, JustifyContent = JustifyContent.FlexEnd }, b));
        Box(b, 250, 5, 50, 20);
    }

    [Fact]
    public void Absolute_child_of_static_parent_uses_nearest_positioned_ancestor()
    {
        // 中间节点（static）位于 (15, 15)，绝对定位节点相对包含块内边距盒 left 10 top 10 → 包含块坐标 (15, 15)
        // 结果坐标相对其父节点（中间节点）：(15-15, 15-15)
        var a = N(Abs with { Width = 10, Height = 10, Inset = Inset(top: 10, left: 10) });
        var middle = N(Block with { Height = 50 }, a);
        Run(Container(middle));
        Box(middle, 15, 15, 370, 50);
        Box(a, 0, 0, 10, 10);
    }

    [Fact]
    public void Absolute_static_position_through_static_ancestors()
    {
        // 中间节点在 (15,15)，其内部先有 30 高的子节点 → 绝对定位节点的静态位置在中间节点内 (0, 30)
        var spacer = N(Block with { Height = 30 });
        var a = N(Abs with { Width = 10, Height = 10 });
        var middle = N(Block, spacer, a);
        Run(Container(middle));
        Box(a, 0, 30, 10, 10);
    }

    [Fact]
    public void Absolute_without_positioned_ancestor_uses_viewport()
    {
        // 根不是已定位的 → 包含块为视口 800×600；根有 margin 20，坐标仍相对根边框盒
        var a = N(Abs with { Width = 10, Height = 10, Inset = Inset(bottom: 0, right: 0) });
        var root = N(Block with { Margin = Margin(20) }, a);
        Run(root, 800, 600);
        Box(root, 20, 20, 760, 0);
        // 视口坐标 (790, 590) → 相对根 (770, 570)
        Box(a, 770, 570, 10, 10);
    }

    [Fact]
    public void Viewport_height_change_moves_viewport_anchored_boxes()
    {
        // 只有视口高度变化：流内布局不变，但相对视口底边定位的节点必须移动
        var a = N(Abs with { Width = 10, Height = 10, Inset = Inset(bottom: 0, left: 0) });
        var root = N(Block, a);
        var e = Run(root, 800, 600);
        T.Eq(590, a.Y);
        e.Compute(root, new Size(800, 400));
        T.Eq(390, a.Y);
    }

    [Fact]
    public void Fixed_ignores_positioned_ancestors()
    {
        var f = N(Block with { Position = Position.Fixed, Width = 10, Height = 10, Inset = Inset(top: 0, left: 0) });
        var container = Container(f);
        container.Style = container.Style with { Margin = Margin(50, 0, 0, 50) };
        var root = N(Block, container);
        Run(root, 800, 600);
        // 容器在 (50,50)；fixed 节点在视口 (0,0) → 相对容器 (-50,-50)
        Box(f, -50, -50, 10, 10);
    }

    [Fact]
    public void Fixed_inside_absolute_inside_root_is_laid_out_after_its_parent()
    {
        var f = N(Block with { Position = Position.Fixed, Width = 10, Height = 10, Inset = Inset(top: 5, left: 5) });
        var a = N(Abs with { Width = 100, Height = 100, Inset = Inset(top: 100, left: 200) }, f);
        var root = N(Block, a);
        Run(root, 800, 600);
        Box(a, 200, 100, 100, 100);
        Box(f, -195, -95, 10, 10);
    }

    [Fact]
    public void Absolute_does_not_contribute_to_parent_size_but_to_scroll_size()
    {
        var a = N(Abs with { Width = 50, Height = 50, Inset = Inset(top: 500, left: 0) });
        var root = N(Block with { Position = Position.Relative, Height = 100 }, a);
        Run(root, 300);
        T.Size(root, 300, 100);
        T.Eq(550, root.ScrollSize.Height);
    }

    [Fact]
    public void Fixed_descendants_do_not_contribute_to_root_scroll_size()
    {
        // 300 px 高的视口里，top:250px; height:100px 的 fixed 元素超出视口底边 50 px：
        // fixed 不随视口滚动，不计入根的可滚动溢出（浏览器同样如此），根不可滚动
        var f = N(Block with { Position = Position.Fixed, Width = 10, Height = 100, Inset = Inset(top: 250, left: 0) });
        var inner = N(Block with { Height = 20 }, N(Block with { Position = Position.Fixed, Width = 10, Height = 100, Inset = Inset(top: 280, left: 0) }));
        var root = N(Block with { Height = 300 }, f, inner);
        Run(root, 300, 300);
        Box(f, 0, 250, 10, 100);
        T.Eq(300, root.ScrollSize.Height);
    }

    [Fact]
    public void Absolute_descendants_still_contribute_to_root_scroll_size()
    {
        // 对照：同样位置的 absolute 元素仍计入根的可滚动溢出
        var a = N(Abs with { Width = 10, Height = 100, Inset = Inset(top: 250, left: 0) });
        var f = N(Block with { Position = Position.Fixed, Width = 10, Height = 500, Inset = Inset(top: 0, left: 0) });
        var root = N(Block with { Height = 300 }, a, f);
        Run(root, 300, 300);
        T.Eq(350, root.ScrollSize.Height);
    }
}

using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// <c>position: sticky</c> 的布局行为：与 static 相同地参与常规流（占据静态空间、不影响兄弟），
/// 布局引擎不施加 inset 偏移——视觉偏移由 UI 层在几何阶段按滚动偏移计算（design/08）。
/// </summary>
public class StickyPositionTests
{
    [Fact]
    public void Sticky_occupies_its_static_space_and_does_not_apply_insets()
    {
        var a = N(Block with { Height = 10, Position = Position.Sticky, Inset = Inset(top: 5, left: 7) });
        var b = N(Block with { Height = 10 });
        Run(N(Block, a, b), 100);
        // 与 static 相同：位置不受 inset 影响，兄弟仍从 a 的末尾排下去
        Box(a, 0, 0, 100, 10);
        Box(b, 0, 10, 100, 10);
    }

    [Fact]
    public void Sticky_is_in_flow_but_establishes_a_containing_block_for_absolute_descendants()
    {
        var abs = N(Block with { Position = Position.Absolute, Width = 20, Height = 20, Inset = Inset(top: 5, left: 6) });
        var sticky = N(Block with { Position = Position.Sticky, Height = 50, Inset = Inset(top: 3) }, abs);
        var below = N(Block with { Height = 10 });
        Run(N(Block, sticky, below), 100);
        // sticky 留在常规流中（下面的块紧接其后）
        Box(sticky, 0, 0, 100, 50);
        Box(below, 0, 50, 100, 10);
        // 绝对定位后代相对 sticky 的包含块（内边距盒，无边框 / 内边距时为边框盒）
        Box(abs, 6, 5, 20, 20);
    }

    [Fact]
    public void Sticky_without_insets_equals_static_for_siblings()
    {
        var sticky = N(Block with { Height = 10, Position = Position.Sticky });
        var sibling = N(Block with { Height = 10 });
        Run(N(Block, sticky, sibling), 80);
        Box(sticky, 0, 0, 80, 10);
        Box(sibling, 0, 10, 80, 10);
    }
}

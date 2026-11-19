using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>病态输入：深层嵌套的收缩适应上下文不能导致指数级重算；非有限样式值不能产生 NaN/无穷结果。</summary>
public class StressTests
{
    [Theory]
    [InlineData(Display.InlineFlex, FlexDirection.Row)]
    [InlineData(Display.InlineFlex, FlexDirection.Column)]
    [InlineData(Display.Flex, FlexDirection.Row)]
    public void Deeply_nested_intrinsic_sizing_is_not_exponential(Display display, FlexDirection dir)
    {
        // 40 层嵌套，每层都是收缩适应（inline-flex）或 row 方向的 flex 项目（每层都要问子节点 min/max-content）
        const int Depth = 40;
        var leaf = Text("aaa bbb ccc", out var m);
        LayoutNode cur = leaf;
        for (int i = 0; i < Depth; i++)
            cur = N(Block with { Display = display, FlexDirection = dir, Padding = new(1), AlignItems = AlignItems.FlexStart }, cur);
        var root = N(Block with { Width = 60 }, cur);
        var e = new LayoutEngine();
        e.Compute(root, new Size(500, 500));
        // 每个节点只有常数个不同的约束键；指数级重算会达到 4^40
        Assert.True(e.AlgorithmRuns < Depth * 40, "algorithm runs: " + e.AlgorithmRuns);
        Assert.True(m.Calls < 10, "measure calls: " + m.Calls);
        Assert.False(float.IsNaN(leaf.Width));
    }

    [Fact]
    public void Non_finite_style_values_are_sanitized()
    {
        var a = N(Block with
        {
            Width = float.PositiveInfinity,
            Height = float.NaN,
            Padding = new(float.PositiveInfinity),
            Margin = new(float.NaN),
            Border = new(float.NegativeInfinity),
            FlexGrow = float.NaN,
            FlexShrink = float.PositiveInfinity,
            FlexBasis = float.NegativeInfinity,
            AspectRatio = float.NaN,
            Position = Position.Relative,
            Inset = new(LengthPercentageAuto.Px(float.PositiveInfinity)),
        });
        var b = N(Block with { Width = 10, Height = 10 });
        var root = N(Row with { Width = 100, ColumnGap = float.PositiveInfinity, RowGap = float.NaN }, a, b);
        Run(root);
        foreach (var n in new[] { root, a, b })
        {
            foreach (var v in new[] { n.X, n.Y, n.Width, n.Height, n.ScrollSize.Width, n.ScrollSize.Height })
                Assert.False(float.IsNaN(v) || float.IsInfinity(v), n + " has non-finite value");
        }
    }
}

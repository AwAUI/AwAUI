using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>评审中发现的问题的回归测试。</summary>
public class ReviewRegressionTests
{
    static LayoutNode Chain(int depth, LayoutStyle style, LayoutNode? leaf = null)
    {
        // 自底向上构建，避免 AddChild 的祖先检查让构建本身变成平方级
        var cur = leaf ?? new LayoutNode(style);
        for (int i = 0; i < depth; i++)
        {
            var p = new LayoutNode(style);
            p.AddChild(cur);
            cur = p;
        }
        return cur;
    }

    [Fact]
    public void Block_baseline_skips_leading_children_without_baseline_when_measuring()
    {
        // A 是块：首个子块（高 10，无内容）没有基线，第二个子块（高 20）内含文本（基线 16）→ A 的基线 = 10 + 16 = 26。
        // 基线对齐在求尺寸（ComputeSize）阶段就要得到 26：之前只问第一个子节点，得到 NaN 后以边框盒底边 30 合成，
        // 导致共享基线 30、文本 y = 14（浏览器中 A 与文本基线对齐，文本 y = 26 - 16 = 10）。
        var inner = Text("x");
        var a = N(Block with { Width = 50 }, N(Block with { Height = 10 }), N(Block with { Height = 20 }, inner));
        var t = Text("y");
        var root = N(Row with { Width = 300, AlignItems = AlignItems.Baseline }, a, t);
        Run(root);
        T.Eq(26, a.Baseline);
        Box(a, 0, 0, 50, 30);
        Box(t, 50, 10, 10, 20);
        T.Size(root, 300, 30);
    }

    [Fact]
    public void Huge_flex_factors_do_not_overflow()
    {
        // 两个 flex-grow 为 3e38 的项目：因子之和溢出为 +∞ 时 free × grow / sum 为 ∞/∞ = NaN（之前得到宽 0 或 NaN）。
        // 因子相同 → 平分 300
        var a = N(Block with { FlexGrow = 3e38f, FlexBasis = 0 });
        var b = N(Block with { FlexGrow = 3e38f, FlexBasis = 0 });
        var root = N(Row with { Width = 300, Height = 10 }, a, b);
        Run(root);
        Box(a, 0, 0, 150, 10);
        Box(b, 150, 0, 150, 10);

        // 收缩：flex-shrink 3e38 × 基准 400 溢出；两个项目相同 → 各收缩一半
        var c = N(Block with { FlexShrink = 3e38f, Width = 400 });
        var d = N(Block with { FlexShrink = 3e38f, Width = 400 });
        var root2 = N(Row with { Width = 300, Height = 10 }, c, d);
        Run(root2);
        Box(c, 0, 0, 150, 10);
        Box(d, 150, 0, 150, 10);
    }

    [Fact]
    public void Huge_finite_lengths_never_produce_nan_or_infinity()
    {
        // 有限但极大的长度（攻击者可控的 CSS，如 width: 1e38px）：求和溢出为 ∞ 后 ∞ - ∞ = NaN。
        // 长度被钳制到 ±1e9 后所有结果都是有限值
        var a = N(Block with { Width = 3e38f, Height = 3e38f, Padding = new(3e38f), Border = new(3e38f) });
        var b = N(Block with { Width = 3e38f, Margin = new(3e38f), FlexBasis = 3e38f });
        var c = N(Block with { Width = Dimension.Percent(3e38f), AspectRatio = 1e-30f });
        var t = new LayoutNode { Measure = new FixedMeasure(3e38f, 3e38f) };
        var root = N(Row with { Width = 300, ColumnGap = 3e38f, FlexWrap = FlexWrap.Wrap }, a, b, c, t);
        var e = new LayoutEngine();
        e.Compute(root, new Size(3e38f, 3e38f));
        LayoutEngine.RoundToPixels(root, 2);
        foreach (var n in new[] { root, a, b, c, t })
        {
            foreach (var v in new[] { n.X, n.Y, n.Width, n.Height, n.ScrollSize.Width, n.ScrollSize.Height,
                         n.SnappedBorderBox.X, n.SnappedBorderBox.Width })
                Assert.False(float.IsNaN(v) || float.IsInfinity(v), n + " has non-finite value");
        }
    }

    [Fact]
    public void Very_deep_display_none_subtree_does_not_overflow_the_stack()
    {
        // display:none 子树不经过 ComputeNode（没有栈深度检查），之前递归清零会在深链上栈溢出、直接终止进程
        var hidden = Chain(100_000, Block);
        hidden.Style = Block with { Display = Display.None };
        var sibling = N(Block with { Height = 10 });
        var root = N(Block, hidden, sibling);
        Run(root, 100);
        Box(sibling, 0, 0, 100, 10);
        T.Size(hidden, 0, 0);
    }

    [Fact]
    public void Very_deep_display_contents_chain_throws_instead_of_crashing()
    {
        // 嵌套的 display:contents 由 CollectChildren 递归展开，同样不经过 ComputeNode 的检查
        var deep = Chain(100_000, Block with { Display = Display.Contents }, N(Block with { Height = 10 }));
        var root = N(Block, deep);
        Assert.Throws<InsufficientExecutionStackException>(() => Run(root, 100));
    }

    [Fact]
    public void Very_deep_tree_throws_and_engine_stays_usable()
    {
        // 原有的 Deep_trees_throw_instead_of_crashing 只有 200 层，从未真正触发栈检查
        var deep = Chain(100_000, Block);
        var e = new LayoutEngine();
        Assert.Throws<InsufficientExecutionStackException>(() => e.Compute(deep, new Size(100, 100)));

        // 异常之后同一个引擎仍能正常布局其他树
        var a = N(Block with { Height = 10 });
        var root = N(Block, a);
        e.Compute(root, new Size(100, 100));
        Box(a, 0, 0, 100, 10);
    }

    [Fact]
    public void Exception_during_relayout_does_not_leave_a_stale_final_layout_cache()
    {
        // 第一次布局成功（宽 100）；第二次换视口时测量函数抛异常。节点的完整布局缓存不能仍然声称「宽 100 时已布局」，
        // 否则下一次以 100 布局会命中缓存，而子孙的位置已被中途的那次布局改写
        var thrower = new ThrowingMeasure();
        var leaf = new LayoutNode { Measure = thrower };
        var a = N(Block with { Height = 10 });
        var mid = N(Block, a, leaf);
        var root = N(Block, mid);
        var e = new LayoutEngine();
        e.Compute(root, new Size(100, 100));
        Box(a, 0, 0, 100, 10);

        thrower.Throw = true;
        Assert.Throws<InvalidOperationException>(() => e.Compute(root, new Size(200, 100)));
        // 中途 a 已按 200 宽布局
        T.Eq(200, a.Width);

        thrower.Throw = false;
        e.Compute(root, new Size(100, 100));
        Box(a, 0, 0, 100, 10);
        T.Size(mid, 100, 30);
    }

    sealed class ThrowingMeasure : LayoutMeasure
    {
        public bool Throw;
        public override Size Measure(LayoutNode node, in MeasureConstraints constraints)
        {
            if (Throw) throw new InvalidOperationException("measure failed");
            return new Size(10, 20);
        }
    }

    [Fact]
    public void Column_item_base_size_is_measured_at_its_clamped_fit_content_width()
    {
        // column 容器宽 300、align-items: flex-start；文本 max-content 150，但 max-width 70。
        // §9.2 3E：基准尺寸按「以 fit-content 作交叉尺寸」布局求得，fit-content 受项目自身 min/max-width 约束 →
        // 宽 70 时断成两行（"aaa bbb" / "ccc ddd"）→ 高 40。之前基准尺寸按 300 宽测量（一行，高 20），项目最终 70×20，文本溢出
        var t = Text("aaa bbb ccc ddd", Block with { MaxWidth = 70 });
        var after = N(Block with { Height = 5 });
        var root = N(Column with { Width = 300, AlignItems = AlignItems.FlexStart }, t, after);
        Run(root);
        Box(t, 0, 0, 70, 40);
        Box(after, 0, 40, 0, 5);
        T.Size(root, 300, 45);
    }

    [Fact]
    public void Column_item_min_width_widens_base_size_measurement()
    {
        // 同理 min-width：容器只有 100 宽（align-items: center），文本 max-content 150。
        // 不考虑 min-width 时 fit-content = min(150, max(30, 100)) = 100 → 两行；min-width 150 → 宽 150 → 一行，高 20。
        // 居中：x = (100 - 150) / 2 = -25（溢出时 center 两侧等量溢出）
        var t = Text("aaa bbb ccc ddd", Block with { MinWidth = 150 });
        var root = N(Column with { Width = 100, AlignItems = AlignItems.Center }, t);
        Run(root);
        Box(t, -25, 0, 150, 20);
    }

    [Fact]
    public void Infinite_leaf_baseline_is_treated_as_missing()
    {
        // 测量函数返回 ∞ 基线：之前基线对齐中 ∞ - ∞ 得到 NaN 坐标。按「没有基线」处理 → 以边框盒底边合成
        var a = new LayoutNode { Measure = new BadBaseline() };
        var b = Text("x");
        var root = N(Row with { Width = 100, AlignItems = AlignItems.Baseline }, a, b);
        Run(root);
        // a 合成基线 = 高 20；b 的基线 16 → b 下移 4
        Box(a, 0, 0, 10, 20);
        Box(b, 10, 4, 10, 20);
    }

    sealed class BadBaseline : LayoutMeasure
    {
        public override Size Measure(LayoutNode node, in MeasureConstraints constraints) => new(10, 20);
        public override float GetBaseline(LayoutNode node, Size contentSize) => float.PositiveInfinity;
    }

    [Fact]
    public void Absolute_with_auto_horizontal_insets_shrinks_into_space_after_static_position()
    {
        // css-position-3 §4.1：left/right 都是 auto 时，inset 修正后的包含块从静态位置开始。
        // 包含块 400 宽，静态位置 x = 300 → 可用宽 100：fit-content = min(150, max(30, 100)) = 100
        // → "aaa bbb"(70) / "ccc ddd" → 70×40。之前可用宽取整个包含块 400，文本一行 150 宽，溢出包含块右缘
        var abs = Text("aaa bbb ccc ddd", Block with { Position = Position.Absolute });
        var b = N(Block with { Padding = new(0, 0, 0, 300) }, abs);
        var root = N(Block with { Position = Position.Relative, Width = 400, Height = 100 }, b);
        Run(root);
        Box(abs, 300, 0, 70, 40);
    }

    [Fact]
    public void Absolute_with_auto_horizontal_insets_in_rtl_uses_space_before_static_position()
    {
        // rtl：静态位置靠右（内容盒 0..100 的右缘），修正后的包含块是 [0, 100] → 同样 70×40，x = 100 - 70 = 30
        var abs = Text("aaa bbb ccc ddd", Block with { Position = Position.Absolute });
        var b = N(Block with { Padding = new(0, 300, 0, 0), Direction = Direction.Rtl }, abs);
        var root = N(Block with { Position = Position.Relative, Width = 400, Height = 100, Direction = Direction.Rtl }, b);
        Run(root);
        Box(abs, 30, 0, 70, 40);
    }

    [Fact]
    public void Percentages_on_items_of_shrink_to_fit_row_resolve_against_final_width()
    {
        // 绝对定位（收缩适应）的 row 容器：求固有宽度时项目的百分比内外边距按 0 → 项目贡献 100 → 容器宽 100。
        // 真正布局时百分比按最终内容盒宽 100 解析（浏览器行为）：margin-left 10% = 10，padding-left 10% = 10，
        // content-box 宽 100 + 10 → 边框盒 110（flex-shrink: 0，溢出容器）。之前布局时仍按 0 解析
        var item = N(Block with
        {
            Width = 100, Height = 10, FlexShrink = 0,
            Margin = new(0, 0, 0, LengthPercentageAuto.Percent(10)),
            Padding = new(0, 0, 0, LengthPercentage.Percent(10)),
        });
        var flex = N(Row with { Position = Position.Absolute }, item);
        var root = N(Block with { Position = Position.Relative, Width = 500, Height = 100 }, flex);
        Run(root);
        T.Size(flex, 100, 10);
        Box(item, 10, 0, 110, 10);
        T.Eq(10, item.Padding.Left);
        T.Eq(10, item.Margin.Left);
    }

    [Fact]
    public void Percentage_width_item_in_shrink_to_fit_row()
    {
        // 文本 max-content 70；B 的 width: 50% 在求固有宽度时按 auto（内容为空 → 0）→ 容器宽 70。
        // 布局时 B = 50% × 70 = 35；基准 70 + 35 = 105 > 70，按「收缩因子 × 基准」分摊溢出 35：
        // 文本收缩 35 × 70/105 = 23.33 → 46.67（≥ min-content 30）；B 收缩 11.67 → 23.33（自动最小 = min(0, 35) = 0）
        // 文本在 46.67 宽时断成两行 → 高 40
        var t = Text("aaa bbb");
        var b = N(Block with { Width = Dimension.Percent(50), Height = 10 });
        var flex = N(Row with { Position = Position.Absolute, AlignItems = AlignItems.FlexStart }, t, b);
        var root = N(Block with { Position = Position.Relative, Width = 500, Height = 100 }, flex);
        Run(root);
        T.Size(flex, 70, 40);
        Box(t, 0, 0, 46.667f, 40);
        Box(b, 46.667f, 0, 23.333f, 10);
    }
}

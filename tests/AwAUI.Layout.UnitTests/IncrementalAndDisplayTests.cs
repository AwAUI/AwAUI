using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>display:none/contents、缓存与增量重排、像素对齐、滚动尺寸。</summary>
public class IncrementalAndDisplayTests
{
    [Fact]
    public void Display_none_subtree_produces_zero_boxes_and_is_skipped()
    {
        var inner = N(Block with { Width = 10, Height = 10 });
        var hidden = N(Block with { Display = Display.None, Width = 100, Height = 100 }, inner);
        var b = N(Block with { Width = 50, Height = 10 });
        var root = N(Row with { Width = 300, Height = 10, JustifyContent = JustifyContent.SpaceBetween }, hidden, b);
        Run(root);
        Box(hidden, 0, 0, 0, 0);
        Box(inner, 0, 0, 0, 0);
        // 只有一个项目参与 space-between → 位于起点
        T.Eq(0, b.X);
    }

    [Fact]
    public void Toggling_display_none_restores_layout()
    {
        var inner = N(Block with { Width = 10, Height = 10 });
        var box = N(Block with { Height = 30 }, inner);
        var root = N(Block, box);
        var e = Run(root, 100);
        Box(inner, 0, 0, 10, 10);

        box.Style = box.Style with { Display = Display.None };
        e.Compute(root, new Size(100, 100));
        Box(inner, 0, 0, 0, 0);
        T.Eq(0, root.Height);

        box.Style = box.Style with { Display = Display.Block };
        e.Compute(root, new Size(100, 100));
        Box(box, 0, 0, 100, 30);
        Box(inner, 0, 0, 10, 10);
    }

    [Fact]
    public void Display_contents_hoists_children_into_parent()
    {
        // contents 节点的两个子节点直接成为 row 容器的 flex 项目
        var a = N(Block with { Width = 50, Height = 10 });
        var b = N(Block with { Width = 60, Height = 10 });
        var contents = N(Block with { Display = Display.Contents, Width = 999, Padding = new(50) }, a, b);
        var c = N(Block with { Width = 70, Height = 10 });
        var root = N(Row with { Width = 300, Height = 10 }, contents, c);
        Run(root);
        Box(contents, 0, 0, 0, 0);
        Box(a, 0, 0, 50, 10);
        Box(b, 50, 0, 60, 10);
        Box(c, 110, 0, 70, 10);
    }

    [Fact]
    public void Clean_relayout_with_same_viewport_does_not_measure()
    {
        var t1 = Text("aaa bbb ccc", out var m1);
        var t2 = Text("ddd eee", out var m2);
        var root = N(Column, N(Row, t1, t2), N(Block, Text("x")));
        var e = Run(root, 500, 500);
        Assert.True(m1.Calls > 0);
        int c1 = m1.Calls, c2 = m2.Calls;

        e.Compute(root, new Size(500, 500));
        e.Compute(root, new Size(500, 500));
        Assert.Equal(c1, m1.Calls);
        Assert.Equal(c2, m2.Calls);
        Assert.False(root.IsDirty);
    }

    [Fact]
    public void Dirty_leaf_only_remeasures_itself()
    {
        // 改变 t1 的内容：只有 t1 重新测量；兄弟节点 t2 与其他子树命中缓存
        var t1 = Text("aaa bbb", out var m1);
        var t2 = Text("ccc ddd", out var m2);
        var t3 = Text("eee", out var m3);
        var row = N(Row with { ColumnGap = 10 }, t1, t2);
        var root = N(Column with { Width = 400 }, row, N(Block, t3));
        var e = Run(root);
        int c2 = m2.Calls, c3 = m3.Calls;
        Box(t2, 80, 0, 70, 20);

        t1.Measure = new FakeText("aaaaa bbb");
        e.Compute(root, new Size(1000, 1000));
        Assert.Equal(c2, m2.Calls);
        Assert.Equal(c3, m3.Calls);
        Box(t1, 0, 0, 90, 20);
        Box(t2, 100, 0, 70, 20);
    }

    [Fact]
    public void Mark_dirty_propagates_to_root_and_relayout_picks_up_changes()
    {
        var a = N(Block with { Height = 10 });
        var b = N(Block with { Height = 10 });
        var root = N(Block, N(Block, a), b);
        var e = Run(root, 100);
        Assert.False(a.IsDirty);
        T.Eq(10, b.Y);

        a.Style = a.Style with { Height = 25 };
        Assert.True(a.IsDirty);
        Assert.True(a.Parent!.IsDirty);
        Assert.True(root.IsDirty);
        Assert.False(b.IsDirty);
        e.Compute(root, new Size(100, 100));
        T.Eq(25, b.Y);
        T.Eq(35, root.Height);
    }

    [Fact]
    public void Setting_equal_style_does_not_dirty()
    {
        var a = N(Block with { Height = 10 });
        var root = N(Block, a);
        Run(root, 100);
        a.Style = Block with { Height = 10 };
        Assert.False(a.IsDirty);
        Assert.False(root.IsDirty);
    }

    [Fact]
    public void Structural_changes_relayout()
    {
        var a = N(Block with { Height = 10 });
        var root = N(Block, a);
        var e = Run(root, 100);
        var b = N(Block with { Height = 20 });
        root.InsertChild(0, b);
        e.Compute(root, new Size(100, 100));
        T.Eq(20, a.Y);
        root.RemoveChild(b);
        e.Compute(root, new Size(100, 100));
        T.Eq(0, a.Y);
        T.Eq(10, root.Height);
    }

    [Fact]
    public void Viewport_change_relayouts_and_rewraps()
    {
        var t = Text("aaa bbb ccc ddd");
        var root = N(Block, t);
        var e = Run(root, 200);
        T.Eq(20, t.Height);
        e.Compute(root, new Size(100, 100));
        T.Eq(40, t.Height);
        e.Compute(root, new Size(200, 100));
        T.Eq(20, t.Height);
    }

    [Fact]
    public void Reusing_cached_subtree_after_sibling_change_keeps_positions()
    {
        // 第一行高度变化后，第二行（干净）整体下移，其内部坐标保持
        var t = Text("aaa");
        var row2 = N(Row with { Padding = new(5) }, t);
        var first = N(Block with { Height = 10 });
        var root = N(Block, first, row2);
        var e = Run(root, 300);
        Box(t, 5, 5, 30, 20);
        first.Style = first.Style with { Height = 50 };
        e.Compute(root, new Size(300, 300));
        T.Eq(50, row2.Y);
        Box(t, 5, 5, 30, 20);
    }

    [Fact]
    public void Scroll_size_covers_overflowing_children_and_end_padding()
    {
        // 子节点 500 宽、200 高超出 100×100 的容器（padding 10）→ 可滚动区域 = 10+500+10 × 10+200+10
        var child = N(Block with { Width = 500, Height = 200 });
        var root = N(Block with { Width = 100, Height = 100, Padding = new(10), BoxSizing = BoxSizing.BorderBox, OverflowX = Overflow.Auto, OverflowY = Overflow.Auto }, child);
        Run(root);
        T.Eq(520, root.ScrollSize.Width);
        T.Eq(220, root.ScrollSize.Height);
    }

    [Fact]
    public void Pixel_snapping_rounds_edges_and_keeps_boxes_seamless()
    {
        // 三个 33.333 宽：绝对边 0、33.33、66.67、100 → 对齐后 0、33、67、100 → 宽 33、34、33
        var a = N(Block with { FlexGrow = 1, FlexBasis = 0 });
        var b = N(Block with { FlexGrow = 1, FlexBasis = 0 });
        var c = N(Block with { FlexGrow = 1, FlexBasis = 0 });
        var root = N(Row with { Width = 100, Height = 10 }, a, b, c);
        Run(root);
        LayoutEngine.RoundToPixels(root, 1);
        Assert.Equal(new Rect(0, 0, 33, 10), a.SnappedBorderBox);
        Assert.Equal(new Rect(33, 0, 34, 10), b.SnappedBorderBox);
        Assert.Equal(new Rect(67, 0, 33, 10), c.SnappedBorderBox);
        // 未对齐的结果不变
        T.Eq(33.333f, b.X);
    }

    [Fact]
    public void Pixel_snapping_with_device_scale()
    {
        // 缩放 2：边对齐到 0.5 逻辑像素。父 x = 10.3 → 10.5；子绝对 x = 10.3 + 0.3 = 10.6 → 10.5，相对父为 0
        var child = N(Block with { Width = 5.2f, Height = 1 });
        var parent = N(Block with { Width = 20, Position = Position.Relative, Inset = Inset(left: 10.3f) }, child);
        child.Style = child.Style with { Position = Position.Relative, Inset = Inset(left: 0.3f) };
        var root = N(Block, parent);
        Run(root, 100);
        LayoutEngine.RoundToPixels(root, 2);
        T.Eq(10.5f, parent.SnappedBorderBox.X);
        T.Eq(0, child.SnappedBorderBox.X);
        // 右边 10.6 + 5.2 = 15.8 → 16.0；宽 = 16 - 10.5 = 5.5
        T.Eq(5.5f, child.SnappedBorderBox.Width);
    }

    [Fact]
    public void Deep_trees_throw_instead_of_crashing()
    {
        // 极深的树：EnsureSufficientExecutionStack 应抛出可捕获异常（或在栈足够时正常完成）
        var root = new LayoutNode();
        var cur = root;
        for (int i = 0; i < 200; i++)
        {
            var n = new LayoutNode();
            cur.AddChild(n);
            cur = n;
        }
        Run(root, 100);
        T.Eq(100, cur.Width);
    }

    [Fact]
    public void Cannot_add_node_with_parent_or_ancestor()
    {
        var a = new LayoutNode();
        var b = new LayoutNode();
        a.AddChild(b);
        Assert.Throws<InvalidOperationException>(() => new LayoutNode().AddChild(b));
        Assert.Throws<InvalidOperationException>(() => b.AddChild(a));
    }

    [Fact]
    public void Measure_returning_garbage_is_sanitized()
    {
        var n = new LayoutNode { Measure = new GarbageMeasure() };
        var root = N(Row with { Width = 100 }, n);
        Run(root);
        Assert.False(float.IsNaN(n.Width));
        Assert.False(float.IsNaN(root.Height));
    }

    sealed class GarbageMeasure : LayoutMeasure
    {
        public override Size Measure(LayoutNode node, in MeasureConstraints constraints) => new(float.NaN, float.PositiveInfinity);
    }
}

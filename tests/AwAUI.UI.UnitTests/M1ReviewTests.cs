using AwAUI.Css.Values;
using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>M1 验收的独立评审：样式失效范围、百分比解析、绘制 / 命中顺序与资源回收的回归测试。</summary>
public class M1ReviewTests
{
    static Color C(string hex) => Color.Parse(hex);

    static Color ColorOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.Color);

    [Fact]
    public void Hover_restyles_subsequent_siblings_and_their_descendants()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div { Id = "a", Class = "a" },
                new Div { Id = "b", Class = "b" },
                new Div { Id = "c", Class = "c", Children = [new Text("t") { Id = "t", Class = "t" }] },
            ],
        }, "div { color: #000000; } .a { height: 20px; } .b, .c { height: 20px; } .a:hover ~ .c .t { color: #ff0000; } .a:hover + .b { color: #00ff00; }");
        Assert.Equal(C("#000000"), ColorOf(h.El("t")));
        h.Move(5, 5);
        h.Frame();
        Assert.Equal(C("#00ff00"), ColorOf(h.El("b")));
        Assert.Equal(C("#ff0000"), ColorOf(h.El("t")));
        h.Move(5, 250);
        h.Frame();
        Assert.Equal(C("#000000"), ColorOf(h.El("b")));
        Assert.Equal(C("#000000"), ColorOf(h.El("t")));
    }

    [Fact]
    public void Ancestor_attribute_and_negated_class_changes_restyle_descendants()
    {
        var on = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Class = on.Value ? "x" : "",
                Attributes = { ["data-mode"] = on.Value ? "dark" : "light" },
                Children = [new Div { Children = [new Text("x") { Id = "t" }, new Text("y") { Id = "u", Class = "u" }] }],
            },
        }, "div, text { color: #000000; } [data-mode=dark] text { color: #ffffff; } :not(.x) > div > .u { background-color: #ff0000; }");
        Assert.Equal(C("#000000"), ColorOf(h.El("t")));
        Assert.Equal(C("#ff0000"), h.El("u").ComputedStyle!.ResolveColor(h.El("u").ComputedStyle!.BackgroundColor));
        on.Value = true;
        h.Frame();
        Assert.Equal(C("#ffffff"), ColorOf(h.El("t")));
        Assert.True(h.El("u").ComputedStyle!.ResolveColor(h.El("u").ComputedStyle!.BackgroundColor).IsTransparent);
    }

    [Fact]
    public void Removing_a_sibling_restyles_the_new_last_child_and_adjacent_sibling()
    {
        var items = new State<string[]>(new[] { "a", "b", "c" });
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Children = [.. items.Value.Select(i => (Node)new Div { Key = i, Id = i, Class = "item " + i })],
            },
        }, ".item:last-child { color: #0000ff; } .a + .c { color: #ff0000; }");
        Assert.Equal(C("#0000ff"), ColorOf(h.El("c")));
        items.Value = new[] { "a", "c" };
        h.Frame();
        Assert.Equal(C("#ff0000"), ColorOf(h.El("c")));
        items.Value = new[] { "a" };
        h.Frame();
        Assert.Equal(C("#0000ff"), ColorOf(h.El("a")));
    }

    [Fact]
    public void Root_font_size_change_reaches_rem_users_below_an_explicit_font_size()
    {
        var big = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Class = big.Value ? "big" : "",
                Children = [new Div { Style = "font-size: 10px", Children = [new Div { Id = "r", Style = "width: 2rem; height: 1px" }] }],
            },
        }, "root { font-size: 16px; }");
        Assert.Equal(32, h.Box("r").Width);
        h.Pipeline.AddStyleSheet("root { font-size: 20px; }", "later.css");
        h.Frame();
        Assert.Equal(40, h.Box("r").Width);
    }

    [Fact]
    public void Vertical_percentage_padding_and_margin_resolve_against_containing_block_width()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "outer", Style = "width: 200px; height: 50px; display: block",
            Children = [new Div { Id = "inner", Style = "padding-top: 10%; margin-top: 5%; height: 0; box-sizing: content-box" }],
        });
        // 200px 的 10% = 20px（纵向百分比内外边距也以包含块宽度为基准）
        Assert.Equal(10, h.Box("inner").Y);
        Assert.Equal(20, h.Box("inner").Height);
    }

    [Fact]
    public void Absolute_percentage_insets_resolve_against_padding_box()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "position: relative; width: 200px; height: 100px; border: 5px solid #000000; padding: 10px; box-sizing: content-box",
            Children = [new Div { Id = "abs", Style = "position: absolute; left: 50%; top: 50%; width: 10px; height: 10px" }],
        });
        // 内边距盒 220×120，原点在边框内缘 (5,5)
        Assert.Equal(new Rect(5 + 110, 5 + 60, 10, 10), h.Box("abs"));
    }

    [Fact]
    public void Text_inherits_font_size_percentage_and_line_height_from_ancestors()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "font-size: 20px; line-height: 1.5",
            Children = [new Div { Style = "font-size: 50%", Children = [new Text("ab") { Id = "t" }] }],
        }, null);
        var t = h.El("t");
        Assert.Equal(10f, t.ComputedStyle!.FontSize);
        // 行高 1.5 继承为数值，按子元素字号计算：15px
        Assert.Equal(15, h.Box("t").Height);
    }

    [Fact]
    public void Hit_test_follows_paint_order_for_negative_z_index_and_positioned_siblings()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "parent", Style = "height: 100px; background-color: #ffffff",
            Children =
            [
                new Div { Id = "neg", Style = "position: absolute; left: 0; top: 0; width: 50px; height: 50px; z-index: -1; background-color: #ff0000" },
                new Div { Id = "rel", Style = "position: relative; width: 50px; height: 50px; left: 25px; background-color: #00ff00" },
                new Div { Id = "flow", Style = "width: 50px; height: 50px; margin-top: -25px; background-color: #0000ff" },
            ],
        });
        // z 为负的元素画在父元素背景之后：命中父元素
        Assert.Equal("parent", h.Pipeline.HitTest(new Point(5, 5))?.Id);
        // 定位元素画在常规流之后：重叠处命中 rel
        Assert.Equal("rel", h.Pipeline.HitTest(new Point(30, 30))?.Id);
        Assert.Equal("flow", h.Pipeline.HitTest(new Point(10, 40))?.Id);
        var bmp = h.Render();
        ColorAssert.Near(C("#ffffff"), bmp.GetPixel(5, 5));
        ColorAssert.Near(C("#00ff00"), bmp.GetPixel(30, 30));
        ColorAssert.Near(C("#0000ff"), bmp.GetPixel(10, 40));
    }

    [Fact]
    public void Removed_subtrees_release_layout_nodes_hit_entries_and_hover()
    {
        var show = new State<bool>(true);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "host", Style = "height: 100px",
                Children = show.Value ? [new Div { Id = "child", Style = "height: 50px", Children = ["abc"] }] : [],
            },
        });
        h.Move(5, 5);
        h.Frame();
        var (weak, layout) = Probe(h);
        show.Value = false;
        h.Frame();
        Assert.True(layout.TryGetTarget(out var node) && node.Parent is null);
        node = null;
        Assert.Equal("host", h.Pipeline.HitTest(new Point(5, 5))?.Id);
        Assert.Equal("host", h.Root.Input.HoveredElement?.Id);
        for (int i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "removed element is still reachable");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference, WeakReference<Layout.LayoutNode>) Probe(PipelineHarness h)
    {
        var child = h.El("child");
        Assert.Same(child, h.Root.Input.HoveredElement);
        return (new WeakReference(child), new WeakReference<Layout.LayoutNode>(child.Render!.Layout!));
    }

    [Fact]
    public void Idle_host_does_not_request_frames_after_hover_without_rules()
    {
        var h = new PipelineHarness(new Div { Style = "height: 100px" });
        h.Frame();
        int before = h.FrameRequests;
        h.Move(5, 5);
        h.Frame();
        h.Move(6, 6);
        Assert.False(h.Root.NeedsFrame);
        Assert.True(h.FrameRequests - before <= 1);
    }

    [Theory]
    // 一条 CSS 规则：不裁剪虚线时这一帧约 6.6 s、分配 828 MB
    [InlineData("width: 1000000px; height: 1000000px; border: 1px dotted #000000")]
    // 布局把尺寸限制在 10⁹：float 弧长不再前进，曾在绘制时死循环
    [InlineData("width: 1000000000000px; height: 1000000000000px; border: 1px dashed #000000; outline: 2px dotted #ff0000")]
    // 3σ 外扩让整数坐标溢出（ArgumentOutOfRangeException），较小的巨大模糊则分配数百 MB 的蒙版
    [InlineData("width: 300px; height: 300px; box-shadow: 0 0 100000px 50000px #000000")]
    [InlineData("width: 300px; height: 300px; box-shadow: 0 0 2000px #000000, inset 0 0 3000px 20px #ff0000")]
    public void Hostile_css_renders_in_bounded_time_and_memory(string css)
    {
        var h = new PipelineHarness(new Div { Style = css }, scale: 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        h.Render();
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 96_000_000, $"allocated {allocated / 1e6:F1} MB");
    }

    [Fact]
    public void Fixed_element_is_a_stacking_context_so_its_children_z_index_stays_inside_it()
    {
        // fixed（z-index: auto）建立层叠上下文：其子元素的 z-index: 5 不能越过后面 z-index: 1 的兄弟
        var h = new PipelineHarness(new Div
        {
            Style = "height: 100px",
            Children =
            [
                new Div
                {
                    Style = "position: fixed; left: 0; top: 0; width: 60px; height: 60px",
                    Children = [new Div { Id = "inner", Style = "position: relative; z-index: 5; width: 60px; height: 60px; background-color: #ff0000" }],
                },
                new Div { Id = "over", Style = "position: absolute; z-index: 1; left: 0; top: 0; width: 60px; height: 60px; background-color: #00ff00" },
            ],
        });
        Assert.Equal("over", h.Pipeline.HitTest(new Point(30, 30))?.Id);
        ColorAssert.Near(C("#00ff00"), h.Render().GetPixel(30, 30));
    }
}

using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.UI.UnitTests;

/// <summary>绘制阶段（DisplayList 内容、层叠顺序、裁剪、像素）与命中测试。</summary>
public class PipelinePaintTests
{
    static Color C(string hex) => Color.Parse(hex);

    /// <summary>把 DisplayList 摘要成文本行（操作码 + 纯色），便于断言顺序。</summary>
    static List<string> Ops(DisplayList list)
    {
        var result = new List<string>();
        FrameWalker.Walk(list, (DrawOp op, Point o, ref DisplayListReader r) =>
        {
            switch (op)
            {
                case DrawOp.FillRect:
                {
                    var c = r.Read<FillRectCmd>();
                    result.Add("FillRect " + FrameWalker.Offset(c.Rect, o) + " " + (c.Paint.IsSolid ? c.Paint.Color.ToString() : "brush"));
                    break;
                }
                case DrawOp.FillRoundRect:
                {
                    var c = r.Read<FillRoundRectCmd>();
                    result.Add("FillRoundRect " + FrameWalker.Offset(c.Shape.Rect, o) + " " + (c.Paint.IsSolid ? c.Paint.Color.ToString() : "brush"));
                    break;
                }
                case DrawOp.PushLayer:
                    result.Add("PushLayer " + r.Read<PushLayerCmd>().Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    result.Add(op.ToString());
                    break;
            }
        });
        return result;
    }

    [Fact]
    public void Background_border_radius_shadow_and_opacity_are_recorded()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "box", Class = "box",
        }, ".box { width: 100px; height: 50px; background-color: #ff0000; border: 2px solid #0000ff; border-radius: 8px;"
           + " box-shadow: 0 2px 4px #00000080; opacity: 0.5; }");
        var ops = Ops(h.Pipeline.Frame);
        int layer = ops.IndexOf("PushLayer 0.5");
        int shadow = ops.IndexOf("DrawBoxShadow");
        int bg = ops.FindIndex(o => o.StartsWith("FillRoundRect [0, 0, 100x50] #ff0000ff", StringComparison.Ordinal));
        int border = ops.IndexOf("FillPath");
        Assert.True(layer >= 0 && shadow > layer && bg > shadow && border > bg, string.Join("\n", ops));
        Assert.Contains("PopLayer", ops);
    }

    [Fact]
    public void Visibility_hidden_skips_painting_but_children_can_be_visible()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "visibility: hidden; background-color: #ff0000; height: 40px",
            Children = [new Div { Style = "visibility: visible; background-color: #00ff00; height: 10px" }],
        });
        var ops = Ops(h.Pipeline.Frame);
        Assert.DoesNotContain(ops, o => o.Contains("#ff0000ff"));
        Assert.Contains(ops, o => o.Contains("#00ff00ff"));
    }

    [Fact]
    public void Z_index_orders_positioned_elements_and_pixels_match()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "rel", Style = "position: relative; height: 100px",
            Children =
            [
                new Div { Id = "top", Style = "position: absolute; left: 10px; top: 10px; width: 50px; height: 50px; background-color: #ff0000; z-index: 2" },
                new Div { Id = "mid", Style = "position: absolute; left: 30px; top: 30px; width: 50px; height: 50px; background-color: #00ff00; z-index: 1" },
                new Div { Id = "under", Style = "position: absolute; left: 0; top: 0; width: 100px; height: 100px; background-color: #0000ff; z-index: -1" },
                new Div { Id = "flow", Style = "width: 20px; height: 100px; background-color: #ffff00" },
            ],
        });
        var ops = Ops(h.Pipeline.Frame).Where(o => o.StartsWith("Fill", StringComparison.Ordinal)).Select(o => o.Substring(o.LastIndexOf('#'))).Skip(1).ToList(); // 第一项是根元素的画布背景
        // 负 z → 常规流 → z=1 → z=2
        Assert.Equal(new[] { "#0000ffff", "#ffff00ff", "#00ff00ff", "#ff0000ff" }, ops);

        using var bmp = h.Render();
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(40, 40));   // top 覆盖 mid
        ColorAssert.Near(C("#00ff00"), bmp.GetPixel(70, 70));
        ColorAssert.Near(C("#ffff00"), bmp.GetPixel(5, 80));    // 常规流在负 z 之上
        ColorAssert.Near(C("#0000ff"), bmp.GetPixel(90, 5));

        Assert.Equal("top", h.Pipeline.HitTest(new Point(40, 40))!.Id);
        Assert.Equal("mid", h.Pipeline.HitTest(new Point(70, 70))!.Id);
        Assert.Equal("flow", h.Pipeline.HitTest(new Point(5, 80))!.Id);
        // 负 z 的元素画在 z 为 auto 的定位父元素之下：父元素（没有背景）仍然在上面接收命中，与浏览器一致
        Assert.Equal("rel", h.Pipeline.HitTest(new Point(90, 5))!.Id);
    }

    [Fact]
    public void Overflow_hidden_clips_painting_and_hit_testing_with_rounded_corners()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "clip", Style = "width: 60px; height: 60px; overflow: hidden; border-radius: 30px; background-color: #0000ff",
            Children = [new Div { Id = "child", Style = "width: 200px; height: 200px; background-color: #ff0000" }],
        });
        using var bmp = h.Render();
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(30, 30));
        ColorAssert.Near(Color.White, bmp.GetPixel(100, 30));  // 裁剪掉
        ColorAssert.Near(Color.White, bmp.GetPixel(2, 2));     // 圆角之外
        Assert.Equal("child", h.Pipeline.HitTest(new Point(30, 30))!.Id);
        Assert.NotEqual("child", h.Pipeline.HitTest(new Point(100, 30))?.Id);
        // 圆角外的点不命中裁剪元素本身
        Assert.Equal(h.Root.RootElement, h.Pipeline.HitTest(new Point(2, 2)));
    }

    [Fact]
    public void Absolute_child_escapes_clip_of_non_containing_ancestor()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "position: relative; width: 200px; height: 200px",
            Children =
            [
                new Div
                {
                    Style = "width: 50px; height: 50px; overflow: hidden",
                    Children = [new Div { Id = "abs", Style = "position: absolute; left: 100px; top: 100px; width: 20px; height: 20px; background-color: #ff0000" }],
                },
            ],
        });
        using var bmp = h.Render();
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(110, 110));
        Assert.Equal("abs", h.Pipeline.HitTest(new Point(110, 110))!.Id);
    }

    [Fact]
    public void Pointer_events_none_and_display_none_are_not_hit()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "base", Style = "position: relative; height: 100px",
            Children =
            [
                new Div { Id = "ghost", Style = "position: absolute; inset: 0; pointer-events: none" },
                new Div { Id = "gone", Style = "position: absolute; inset: 0; display: none" },
            ],
        });
        Assert.Equal("base", h.Pipeline.HitTest(new Point(50, 50))!.Id);
    }

    [Fact]
    public void Text_is_painted_with_inherited_color_at_snapped_origin()
    {
        var h = new PipelineHarness(new Text("ab") { Style = "color: #ff0000; font-size: 20px; padding-left: 10px" }, scale: 2);
        bool found = false;
        FrameWalker.Walk(h.Pipeline.Frame, (DrawOp op, Point o, ref DisplayListReader r) =>
        {
            if (op != DrawOp.DrawGlyphRun) return;
            var c = r.Read<DrawGlyphRunCmd>();
            Assert.Equal(C("#ff0000"), c.Paint.Color);
            Assert.Equal(10f, c.Origin.X + o.X);
            found = true;
        });
        Assert.True(found);
        using var bmp = h.Render();
        // Ahem 方块字形：第一个字符覆盖 x ∈ [10, 30)，上升 16px
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(40, 20));
        ColorAssert.Near(Color.White, bmp.GetPixel(10, 20));
    }

    [Fact]
    public void Linear_gradient_background_runs_top_to_bottom_by_default()
    {
        var h = new PipelineHarness(new Div { Style = "height: 100px; background-image: linear-gradient(#ff0000, #0000ff)" });
        using var bmp = h.Render();
        var top = bmp.GetPixel(50, 0);
        var bottom = bmp.GetPixel(50, 99);
        Assert.True(top.R > 0.95f && top.B < 0.05f, top.ToString());
        Assert.True(bottom.B > 0.95f && bottom.R < 0.05f, bottom.ToString());
        var mid = bmp.GetPixel(50, 50);
        Assert.InRange(mid.R, 0.4f, 0.6f);
    }

    [Fact]
    public void Off_screen_subtrees_are_culled()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div { Style = "height: 50px; background-color: #ff0000" },
                new Div { Style = "height: 1000px" },
                new Div { Style = "height: 50px; background-color: #00ff00" },
            ],
        }, height: 300);
        var ops = Ops(h.Pipeline.Frame);
        Assert.Contains(ops, o => o.Contains("#ff0000ff"));
        Assert.DoesNotContain(ops, o => o.Contains("#00ff00ff"));
    }

    [Fact]
    public void Focus_visible_outline_is_painted_after_keyboard_focus()
    {
        var h = new PipelineHarness(new Button { Id = "b", Children = ["Go"] }, "button { outline-color: #ff00ff; }");
        int Paths() => Ops(h.Pipeline.Frame).Count(o => o == "FillPath");
        int before = Paths();
        h.Root.Press(Platform.PhysicalKey.Tab);
        h.Frame();
        Assert.True(h.El("b").States.HasFlag(PseudoStates.FocusVisible));
        Assert.Equal(before + 1, Paths()); // 轮廓圆环
        using var bmp = h.Render();
        // UA 样式：2px 轮廓，外偏移 2px；按钮从 (0,0) 开始，轮廓在 x ∈ [-4, -2)，看按钮下方
        var b = h.Box("b");
        ColorAssert.Near(C("#ff00ff"), bmp.GetPixel(10, (int)(b.Bottom + 3)), 0.05f);
    }
}

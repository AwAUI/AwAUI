using System.Numerics;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Render;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 第九批：CSS 变换、filter、backdrop-filter、clip-path、mix-blend-mode 与 pointer-events 在帧管线中的绘制、命中测试、
/// 几何查询与增量绘制（损伤、缓存）。
/// </summary>
public class TransformEffectsTests
{
    const string Base = "root { background-color: #ffffff; } .box { position: absolute; left: 100px; top: 100px; width: 100px; height: 50px; background-color: #ff0000; }";

    static Color C(string hex) => Color.Parse(hex);

    static PipelineHarness Box(string extraCss, float scale = 1, Node? inner = null) =>
        new(new Div { Children = [new Div { Id = "box", Class = "box", Children = inner is null ? [] : [inner] }] }, Base + extraCss, 400, 300, scale);

    // ------------------------------------------------------------ 变换：几何与命中测试

    [Fact]
    public void Rotation_hit_tests_through_the_inverse_transform()
    {
        var h = Box(".box { transform: rotate(45deg); }");
        var box = h.El("box");
        // 中心不变
        Assert.Same(box, h.Pipeline.HitTest(new Point(150, 125)));
        // 未变换盒子的角（旋转后在形状之外）
        Assert.NotSame(box, h.Pipeline.HitTest(new Point(101, 101)));
        // 沿旋转后的 x 轴离中心 45 px：伸出原盒子的下边（原盒子半高只有 25 px），但在旋转后的形状内；60 px 则在外
        Assert.Same(box, h.Pipeline.HitTest(new Point(150 + 45 * 0.7071f, 125 + 45 * 0.7071f)));
        Assert.NotSame(box, h.Pipeline.HitTest(new Point(150 + 60 * 0.7071f, 125 + 60 * 0.7071f)));
    }

    [Fact]
    public void Transformed_bounds_include_translate_scale_and_origin()
    {
        var h = Box(".box { transform: translate(10px, 20px) scale(2); transform-origin: 0 0; }");
        var r = h.Pipeline.GetTransformedBounds(h.El("box"));
        Assert.Equal(new Rect(110, 120, 200, 100), r);
        // 布局矩形不变
        Assert.Equal(new Rect(100, 100, 100, 50), h.El("box").BoundingRect);
        var m = h.Pipeline.GetElementTransform(h.El("box"));
        Assert.True(h.Pipeline.TryMapToElement(h.El("box"), new Point(110 + 20, 120 + 10), out var local));
        Assert.Equal(new Point(110, 105), local);
        Assert.Equal(2, m.M11);
    }

    [Fact]
    public void Individual_transform_properties_compose_in_css_order()
    {
        // translate → rotate → scale（CSS Transforms 2）：先缩放，再旋转 90°，再平移
        var h = Box(".box { translate: 50px 0; rotate: 90deg; scale: 2 1; }");
        var r = h.Pipeline.GetTransformedBounds(h.El("box"));
        // 中心 (150, 125) 平移到 (200, 125)；200×50 旋转 90° 成为 50×200
        Assert.Equal(175, r.X, 3);
        Assert.Equal(25, r.Y, 3);
        Assert.Equal(50, r.Width, 3);
        Assert.Equal(200, r.Height, 3);
    }

    [Fact]
    public void Transform_box_content_box_uses_the_content_box_for_the_origin()
    {
        var h = Box(".box { padding: 10px 0 0 20px; transform-box: content-box; transform-origin: 0 0; transform: scale(2); }");
        // 内容盒原点 (120, 110)：边框盒的左上角 (100, 100) 映射到 (80, 90)
        var r = h.Pipeline.GetTransformedBounds(h.El("box"));
        Assert.Equal(80, r.X, 3);
        Assert.Equal(90, r.Y, 3);
    }

    [Fact]
    public void Nested_transforms_and_scroll_offsets_hit_test_correctly()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "scroller", Class = "scroller",
            Children = [new Div { Class = "spacer" }, new Div { Id = "outer", Class = "outer", Children = [new Div { Id = "inner", Class = "inner" }] }],
        }, """
           .scroller { position: absolute; left: 0; top: 0; width: 300px; height: 200px; overflow: auto; }
           .spacer { height: 150px; }
           .outer { margin-left: 50px; width: 100px; height: 100px; background-color: #00ff00; transform: scale(0.5); transform-origin: 0 0; }
           .inner { margin-left: 40px; margin-top: 40px; width: 20px; height: 20px; background-color: #0000ff; transform: translateX(100px); }
           .scroller::after { content: ""; display: block; height: 300px; }
           """, 400, 300);
        var inner = h.El("inner");
        // 未滚动：outer 在 (50, 150)，缩放 0.5；inner 布局在 (90, 190)，平移 100 后在 (190, 190)，再经 outer 的缩放：(50 + 140·0.5, 150 + 40·0.5) = (120, 170)
        var b = h.Pipeline.GetTransformedBounds(inner);
        Assert.Equal(new Rect(120, 170, 10, 10), b);
        Assert.Same(inner, h.Pipeline.HitTest(new Point(125, 175)));
        h.El("scroller").ScrollController.JumpTo(new Point(0, 60));
        h.Frame();
        b = h.Pipeline.GetTransformedBounds(inner);
        Assert.Equal(new Rect(120, 110, 10, 10), b);
        Assert.Same(inner, h.Pipeline.HitTest(new Point(125, 115)));
        Assert.NotSame(inner, h.Pipeline.HitTest(new Point(125, 175)));
    }

    [Fact]
    public void Singular_transform_and_hidden_backface_are_not_painted_or_hit()
    {
        var h = Box(".box { transform: scale(0); }");
        Assert.NotSame(h.El("box"), h.Pipeline.HitTest(new Point(150, 125)));
        using (var bmp = h.Render()) ColorAssert.Near(Color.White, bmp.GetPixel(150, 125));
        var h2 = Box(".box { transform: rotateY(180deg); backface-visibility: hidden; }");
        Assert.NotSame(h2.El("box"), h2.Pipeline.HitTest(new Point(150, 125)));
        using (var bmp = h2.Render()) ColorAssert.Near(Color.White, bmp.GetPixel(150, 125));
        // 背面可见时 rotateY(180deg) 是水平翻转
        var h3 = Box(".box { transform: rotateY(180deg); }");
        Assert.Same(h3.El("box"), h3.Pipeline.HitTest(new Point(150, 125)));
    }

    [Fact]
    public void Three_d_rotation_is_flattened_orthographically()
    {
        var h = Box(".box { transform: rotateX(60deg); }");
        var r = h.Pipeline.GetTransformedBounds(h.El("box"));
        // 高度 50 · cos 60° = 25，绕中心
        Assert.Equal(25, r.Height, 2);
        Assert.Equal(112.5f, r.Y, 2);
        Assert.Equal(100, r.Width, 2);
    }

    [Fact]
    public void Transform_paints_rotated_pixels_and_keeps_text_unlayered()
    {
        var h = Box(".box { transform: rotate(90deg); } text { font-size: 10px; color: #000000; }", inner: new Text("ab"));
        using var bmp = h.Render();
        // 旋转 90° 后盒子是 50×100、中心 (150, 125)：(150, 80) 在内，(110, 125) 在外
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(150, 82));
        ColorAssert.Near(Color.White, bmp.GetPixel(110, 125));
        // 变换不需要离屏图层
        Assert.DoesNotContain(DrawOp.PushLayer, FrameWalker.Ops(h.Pipeline.Frame));
        Assert.Contains(DrawOp.Transform, FrameWalker.Ops(h.Pipeline.Frame));
    }

    [Fact]
    public void Transform_creates_stacking_context_and_paints_above_later_static_siblings_in_tree_order()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Div { Id = "a", Class = "a" }, new Div { Id = "b", Class = "b" }],
        }, """
           .a { width: 100px; height: 100px; background-color: #ff0000; transform: translateY(50px); }
           .b { width: 100px; height: 100px; background-color: #0000ff; }
           """, 200, 300);
        using var bmp = h.Render();
        // 变换的元素按 z = 0 的定位元素绘制：盖在后面的常规流兄弟之上（与浏览器一致）
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(50, 120));
        Assert.Equal("a", h.Pipeline.HitTest(new Point(50, 120))?.Id);
    }

    // ------------------------------------------------------------ pointer-events

    [Fact]
    public void Pointer_events_none_falls_through_transformed_and_clipped_elements()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div { Id = "under", Class = "under" },
                new Div { Id = "over", Class = "over", Children = [new Div { Id = "child", Class = "child" }] },
            ],
        }, """
           .under { position: absolute; left: 0; top: 0; width: 200px; height: 200px; background-color: #cccccc; }
           .over { position: absolute; left: 0; top: 0; width: 200px; height: 200px; transform: rotate(10deg); pointer-events: none; background-color: #ff000080; }
           .child { width: 50px; height: 50px; pointer-events: auto; background-color: #0000ff; }
           """, 300, 300);
        Assert.Equal("under", h.Pipeline.HitTest(new Point(150, 150))?.Id);
        // 子元素 pointer-events: auto 仍可命中（继承的值被它覆盖）
        var childBounds = h.Pipeline.GetTransformedBounds(h.El("child"));
        Assert.Equal("child", h.Pipeline.HitTest(new Point(childBounds.X + childBounds.Width / 2, childBounds.Y + childBounds.Height / 2))?.Id);
        // DevTools 的拾取忽略 pointer-events
        Assert.Equal("over", h.Pipeline.HitTest(new Point(150, 150), ignorePointerEvents: true)?.Id);
    }

    [Fact]
    public void Pointer_events_values_other_than_none_are_hit()
    {
        // CssPointerEvents 目前只有 auto / none（SVG 的 visiblePainted 等关键字尚未被解析器接受，见报告的共享文件请求）；
        // 命中测试把 none 以外的任何值都当作 auto
        foreach (CssPointerEvents value in Enum.GetValues(typeof(CssPointerEvents)))
        {
            var h = Box(".box { pointer-events: " + (value == CssPointerEvents.None ? "none" : "auto") + "; }");
            Assert.Equal(value != CssPointerEvents.None, ReferenceEquals(h.El("box"), h.Pipeline.HitTest(new Point(150, 125))));
        }
    }

    // ------------------------------------------------------------ clip-path

    [Fact]
    public void Clip_path_circle_clips_paint_and_hit_testing()
    {
        var h = Box(".box { width: 100px; height: 100px; clip-path: circle(50%); }");
        using var bmp = h.Render();
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(150, 150));
        ColorAssert.Near(Color.White, bmp.GetPixel(102, 102));
        Assert.Same(h.El("box"), h.Pipeline.HitTest(new Point(150, 150)));
        Assert.NotSame(h.El("box"), h.Pipeline.HitTest(new Point(103, 103)));
        // 边缘抗锯齿：圆周（45° 方向，离圆心 50 px）上的像素是中间值
        var edge = bmp.GetPixel(185, 185);
        Assert.True(edge.G > 0.05f && edge.G < 0.95f, edge.ToString());
    }

    [Theory]
    [InlineData("inset(10px 20px round 5px)", 125, 115, 102, 102)]
    [InlineData("ellipse(30% 20% at 50% 50%)", 150, 125, 105, 105)]
    [InlineData("polygon(50% 0, 100% 100%, 0 100%)", 150, 140, 102, 102)]
    [InlineData("polygon(evenodd, 0 0, 100% 0, 100% 100%, 0 100%, 25% 25%, 75% 25%, 75% 75%, 25% 75%)", 102, 102, 150, 125)]
    [InlineData("padding-box", 110, 110, 101, 101)]
    public void Clip_path_basic_shapes(string clip, float inX, float inY, float outX, float outY)
    {
        var h = Box(".box { border: 4px solid #0000ff; clip-path: " + clip + "; }");
        Assert.Same(h.El("box"), h.Pipeline.HitTest(new Point(inX, inY)));
        Assert.NotSame(h.El("box"), h.Pipeline.HitTest(new Point(outX, outY)));
        using var bmp = h.Render();
        ColorAssert.Near(Color.White, bmp.GetPixel((int)outX, (int)outY), 0.05f);
    }

    [Fact]
    public void Clip_path_clips_descendants_and_box_shadow()
    {
        var h = Box(".box { box-shadow: 0 0 0 10px #00ff00; clip-path: inset(0); } .c { position: absolute; left: 80px; top: 0; width: 60px; height: 20px; background-color: #0000ff; }",
            inner: new Div { Id = "c", Class = "c" });
        using var bmp = h.Render();
        ColorAssert.Near(Color.White, bmp.GetPixel(95, 125));       // 阴影被裁掉
        ColorAssert.Near(C("#0000ff"), bmp.GetPixel(190, 110));      // 子元素在盒内的部分
        ColorAssert.Near(Color.White, bmp.GetPixel(220, 110));       // 伸出盒外的部分被裁掉
        Assert.NotSame(h.El("c"), h.Pipeline.HitTest(new Point(220, 110)));
    }

    // ------------------------------------------------------------ filter / backdrop-filter / mix-blend-mode

    [Fact]
    public void Filter_blur_spreads_beyond_the_box_and_extends_ink_and_damage()
    {
        var h = Box(".box { filter: blur(4px); }");
        using var bmp = h.Render();
        var outside = bmp.GetPixel(96, 125);
        Assert.True(outside.G < 0.98f, "blurred edge should tint pixels outside the box: " + outside);
        var center = bmp.GetPixel(150, 125);
        ColorAssert.Near(C("#ff0000"), center, 0.05f);
        Assert.Contains(DrawOp.PushLayer, FrameWalker.Ops(h.Pipeline.Frame));
        // 改变颜色：损伤覆盖模糊的范围
        h.El("box").SetStyleOverride("background-color", "#0000ff");
        h.Frame();
        var d = h.Pipeline.LastDamage!.Value;
        Assert.True(d.Left <= 88 && d.Right >= 212 && d.Top <= 88 && d.Bottom >= 162, d.ToString());
    }

    [Fact]
    public void Filter_color_functions_and_drop_shadow()
    {
        var h = Box(".box { filter: grayscale(1); }");
        using (var bmp = h.Render())
        {
            var p = bmp.GetPixel(150, 125);
            Assert.True(Math.Abs(p.R - p.G) < 0.02f && Math.Abs(p.G - p.B) < 0.02f, p.ToString());
        }
        var h2 = Box(".box { filter: drop-shadow(6px 6px 0 #0000ff); }");
        using (var bmp = h2.Render())
        {
            ColorAssert.Near(C("#0000ff"), bmp.GetPixel(203, 153), 0.05f);
            ColorAssert.Near(C("#ff0000"), bmp.GetPixel(195, 145), 0.05f);
        }
        // currentColor 的投影
        var h3 = Box(".box { color: #00ff00; filter: drop-shadow(6px 6px 0); }");
        using (var bmp = h3.Render()) ColorAssert.Near(C("#00ff00"), bmp.GetPixel(203, 153), 0.05f);
        // url() 引用：整个滤镜不生效（Filter Effects 1），仍建立层叠上下文
        var h4 = Box(".box { filter: url(#missing) grayscale(1); }");
        using (var bmp = h4.Render()) ColorAssert.Near(C("#ff0000"), bmp.GetPixel(150, 125));
    }

    [Fact]
    public void Backdrop_filter_blurs_what_is_painted_below()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Div { Class = "stripes" }, new Div { Id = "glass", Class = "glass" }],
        }, """
           .stripes { position: absolute; left: 0; top: 0; width: 300px; height: 200px;
                      background-image: repeating-linear-gradient(90deg, #000000 0px, #000000 4px, #ffffff 4px, #ffffff 8px); }
           .glass { position: absolute; left: 50px; top: 50px; width: 100px; height: 60px; border-radius: 10px; backdrop-filter: blur(6px); }
           """, 300, 200);
        using var bmp = h.Render();
        // 玻璃外：清晰的条纹
        ColorAssert.Near(Color.Black, bmp.GetPixel(41, 30), 0.02f);
        // 玻璃内：模糊成灰色
        var p = bmp.GetPixel(100, 80);
        Assert.InRange(p.R, 0.35f, 0.65f);
        Assert.Contains(DrawOp.DrawBackdrop, FrameWalker.Ops(h.Pipeline.Frame));
    }

    [Fact]
    public void Backdrop_damage_expands_to_the_whole_read_region()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Div { Id = "dot", Class = "dot" }, new Div { Class = "glass" }],
        }, """
           .dot { position: absolute; left: 60px; top: 60px; width: 4px; height: 4px; background-color: #000000; }
           .glass { position: absolute; left: 50px; top: 50px; width: 100px; height: 60px; backdrop-filter: blur(3px); }
           """, 300, 200);
        h.El("dot").SetStyleOverride("background-color", "#ff0000");
        h.Frame();
        var d = h.Pipeline.LastDamage!.Value;
        // 点只有 4×4，但它在玻璃的读取范围内：损伤扩大到整块玻璃（含模糊范围）
        Assert.True(d.Left <= 50 - 9 && d.Top <= 50 - 9 && d.Right >= 150 + 9 && d.Bottom >= 110 + 9, d.ToString());
    }

    [Fact]
    public void Mix_blend_mode_multiply_blends_with_content_below()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Div { Class = "a" }, new Div { Class = "b" }],
        }, """
           .a { position: absolute; left: 0; top: 0; width: 100px; height: 100px; background-color: #ffff00; }
           .b { position: absolute; left: 50px; top: 0; width: 100px; height: 100px; background-color: #00ffff; mix-blend-mode: multiply; }
           """, 200, 120);
        using var bmp = h.Render();
        ColorAssert.Near(C("#00ff00"), bmp.GetPixel(75, 50));
        ColorAssert.Near(C("#00ffff"), bmp.GetPixel(125, 50));
    }

    // ------------------------------------------------------------ 增量绘制：变换与不透明度不重新布局

    [Fact]
    public void Transform_and_opacity_changes_do_not_relayout_and_reuse_the_element_list()
    {
        // 已经有变换（层叠上下文、按绝对坐标录制的单元）：之后的变化不改变层叠结构与录制方式
        var h = Box(".box { transform: rotate(1deg); } text { font-size: 10px; }", inner: new Text("label"));
        var box = h.El("box");
        h.Frame();
        box.SetStyleOverride("transform", "translateX(30px) rotate(5deg)");
        h.Frame();
        var t = h.Pipeline.LastTimings;
        Assert.False(t.LayoutRan, t.ToString());
        Assert.Equal(0, t.ParagraphsBuilt);
        // 变换在录制单元之外：元素自己的 DisplayList 复用（只有外层重新合成）
        Assert.DoesNotContain(box, h.Pipeline.LastRecordedElements);
        var d = h.Pipeline.LastDamage!.Value;
        // 损伤覆盖旧位置与新位置
        Assert.True(d.Left <= 100 && d.Right >= 230, d.ToString());

        foreach (var (prop, value) in new[] { ("opacity", "0.5"), ("opacity", "0.8"), ("filter", "blur(2px)"), ("filter", "grayscale(1)"), ("transform", "scale(1.5)") })
        {
            box.SetStyleOverride(prop, value);
            h.Frame();
            Assert.False(h.Pipeline.LastTimings.LayoutRan, prop);
            Assert.Equal(0, h.Pipeline.LastTimings.ParagraphsBuilt);
        }
        // opacity / filter 的变化（层叠结构不变时）同样只重新合成
        box.SetStyleOverride("opacity", "0.6");
        h.Frame();
        Assert.DoesNotContain(box, h.Pipeline.LastRecordedElements);
    }

    [Fact]
    public void Incremental_transform_animation_matches_full_repaint()
    {
        var h = Box(".box { border-radius: 8px; } .x { position: absolute; left: 0; top: 0; width: 400px; height: 300px; background-color: #e0f2fe; }",
            scale: 1.5f);
        var reference = Box(".box { border-radius: 8px; }", scale: 1.5f);
        reference.Pipeline.IncrementalPaint = false;
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(h.Pipeline.Viewport, h.Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, 1.5);
        target.Render(h.Pipeline.Frame, new RenderOptions(Color.White));
        long rendered = h.Pipeline.FrameVersion;
        string[] steps = ["rotate(10deg)", "rotate(20deg) scale(1.2)", "translate(40px, -10px) rotate(-30deg)", "skewX(20deg)", "none", "scale(0.5)", "rotate(170deg)"];
        foreach (var step in steps)
        {
            foreach (var x in new[] { h, reference })
            {
                x.El("box").SetStyleOverride("transform", step);
                x.Frame();
            }
            Assert.Equal(rendered + 1, h.Pipeline.FrameVersion);
            var d = h.Pipeline.LastDamage!.Value;
            var dp = PixelRect.RoundOut(new Rect(d.X * 1.5f - 1, d.Y * 1.5f - 1, d.Width * 1.5f + 2, d.Height * 1.5f + 2));
            target.Render(h.Pipeline.Frame, new RenderOptions(Color.White, dp));
            rendered = h.Pipeline.FrameVersion;
            using var expected = reference.Render();
            Assert.True(target.Bitmap.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(expected.ToRgbaUnpremultiplied()), "mismatch after " + step);
        }
    }

    // ------------------------------------------------------------ 输入相关的计算值

    [Fact]
    public void User_select_and_touch_action_used_values()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "outer", Class = "outer",
            Children = [new Div { Id = "mid", Class = "mid", Children = [new Text("t") { Id = "t" }] }, new Input { Id = "in" }],
        }, ".outer { user-select: none; touch-action: pan-y; } .mid { touch-action: pan-x pan-y; }", 300, 200);
        Assert.Equal(CssUserSelect.None, h.Pipeline.GetUsedUserSelect(h.El("t")));
        Assert.Equal(CssUserSelect.Contain, h.Pipeline.GetUsedUserSelect(h.El("in")));
        Assert.Equal(CssTouchAction.PanY, h.Pipeline.GetEffectiveTouchAction(h.El("t")));
        var h2 = new PipelineHarness(new Div { Children = [new Text("x") { Id = "t" }] }, "", 100, 100);
        Assert.Equal(CssUserSelect.Text, h2.Pipeline.GetUsedUserSelect(h2.El("t")));
        Assert.Equal(CssTouchAction.Auto, h2.Pipeline.GetEffectiveTouchAction(h2.El("t")));
    }

    // ------------------------------------------------------------ 黄金图像

    const string GoldenCss = """
        root { background-color: #f1f5f9; }
        .page { position: relative; width: 360px; height: 260px; }
        .stripes { position: absolute; left: 0; top: 0; width: 360px; height: 260px;
                   background-image: repeating-linear-gradient(135deg, #fde68a 0px, #fde68a 10px, #93c5fd 10px, #93c5fd 20px); }
        .card { position: absolute; width: 90px; height: 60px; border-radius: 10px; background-color: #4f46e5; color: #ffffff; font-size: 10px; padding: 6px; }
        .rot { left: 20px; top: 20px; transform: rotate(-12deg); }
        .skew { left: 140px; top: 20px; transform: skewX(-15deg) scale(0.9); background-color: #059669; }
        .three { left: 250px; top: 20px; transform: rotateY(50deg) rotateX(20deg); background-color: #db2777; }
        .circle { left: 20px; top: 110px; clip-path: circle(40% at 50% 50%); background-color: #ea580c; }
        .poly { left: 130px; top: 110px; clip-path: polygon(50% 0, 100% 50%, 50% 100%, 0 50%); background-color: #0891b2; }
        .blur { left: 250px; top: 110px; filter: blur(3px); }
        .shadow { left: 20px; top: 190px; filter: drop-shadow(4px 4px 3px rgba(0, 0, 0, 0.6)); background-color: #fbbf24; color: #111827; }
        .gray { left: 140px; top: 190px; filter: grayscale(1) contrast(1.4); background-image: linear-gradient(to right, #ef4444, #3b82f6); }
        .glass { left: 240px; top: 180px; width: 100px; background-color: rgba(255, 255, 255, 0.25); backdrop-filter: blur(5px) saturate(1.8); border: 1px solid #ffffff; }
        """;

    static Node GoldenScene() => new Div
    {
        Class = "page",
        Children =
        [
            new Div { Class = "stripes" },
            new Div { Class = "card rot", Children = [new Text("rotate")] },
            new Div { Class = "card skew", Children = [new Text("skew")] },
            new Div { Class = "card three", Children = [new Text("3d")] },
            new Div { Class = "card circle" },
            new Div { Class = "card poly" },
            new Div { Class = "card blur", Children = [new Text("blur")] },
            new Div { Class = "card shadow", Children = [new Text("shadow")] },
            new Div { Class = "card gray", Children = [new Text("gray")] },
            new Div { Class = "card glass", Children = [new Text("glass")] },
        ],
    };

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Effects_page_matches_golden(float scale)
    {
        var h = new PipelineHarness(GoldenScene(), GoldenCss, 360, 260, scale);
        using var bmp = h.Render();
        AssertGolden(scale == 1 ? "ui-effects" : "ui-effects@2x", bmp);
    }

    /// <summary>UI 层效果的黄金图像放在 tests/TestFiles/Golden/UI（第九批起的约定）。</summary>
    static void AssertGolden(string name, Bitmap actual, int tolerance = 2)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "tests", "TestFiles", "Golden", "UI");
        var path = Path.Combine(dir, name + ".png");
        var outDir = Path.Combine(root.FullName, "tests", "TestFiles", "Output");
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            PngCodec.Save(actual, path);
            return;
        }
        Directory.CreateDirectory(outDir);
        if (!File.Exists(path))
        {
            PngCodec.Save(actual, Path.Combine(outDir, name + ".actual.png"));
            Assert.Fail($"Golden image '{name}.png' is missing. Review {outDir} and run with AWAUI_UPDATE_GOLDEN=1.");
        }
        using var expected = PngCodec.Load(path);
        Assert.Equal(expected.Size, actual.Size);
        var e = expected.ToRgbaUnpremultiplied();
        var a = actual.ToRgbaUnpremultiplied();
        int bad = 0, maxDiff = 0;
        for (int i = 0; i < e.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(e[i + c] - a[i + c]));
            maxDiff = Math.Max(maxDiff, d);
            if (d > tolerance) bad++;
        }
        if (bad > 0)
        {
            PngCodec.Save(actual, Path.Combine(outDir, name + ".actual.png"));
            Assert.Fail($"'{name}': {bad} pixels differ (max channel diff {maxDiff}). See {outDir}.");
        }
    }
}

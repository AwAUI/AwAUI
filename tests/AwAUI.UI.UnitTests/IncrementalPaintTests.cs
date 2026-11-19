using AwAUI.Graphics;
using AwAUI.Render;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 增量绘制（元素级 DisplayList 缓存 + 损伤区域）：只重录变化的元素、只移动的子树在合成时平移、
/// 局部光栅化的结果与整帧重绘逐字节相同，以及层叠上下文的裁剪逃逸（M1 评审遗留）。
/// </summary>
public partial class IncrementalPaintTests
{
    const string ListCss = """
        .list { display: flex; flex-direction: column; gap: 2px; padding: 4px; }
        .row { display: flex; height: 20px; padding: 2px; border: 1px solid #d0d7de; border-radius: 4px; background-color: #ffffff; }
        .row:hover { background-color: #ffeeaa; }
        .row.sel { background-color: #aaccff; }
        .cell { width: 40px; color: #333333; font-size: 10px; }
        .spacer { background-color: #22aa22; }
        """;

    sealed partial class ListApp : Component
    {
        public readonly State<int> Count = new(30);
        public readonly State<int> Selected = new(-1);
        public readonly State<float> Spacer = new(10);
        public readonly State<string> Label = new("x");

        protected override Node Build()
        {
            var rows = new NodeList();
            for (int i = 0; i < Count.Value; i++)
            {
                rows.Add(new Div
                {
                    Key = i,
                    Id = "row-" + i,
                    Class = i == Selected.Value ? "row sel" : "row",
                    Children =
                    [
                        new Text("#" + i) { Class = "cell" },
                        new Text(i == 3 ? Label.Value : "item") { Class = "cell" },
                    ],
                });
            }
            return new Div
            {
                Id = "app",
                Children =
                [
                    new Div { Id = "spacer", Class = "spacer", Style = "height: " + Spacer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px" },
                    new Div { Id = "list", Class = "list", Children = rows },
                ],
            };
        }
    }

    static List<string?> Recorded(PipelineHarness h) => h.Pipeline.LastRecordedElements.Select(e => e.Id).ToList();

    [Fact]
    public void Hover_rerecords_only_the_row_and_its_ancestors()
    {
        var app = new ListApp();
        var h = new PipelineHarness(app, ListCss, 300, 400);
        Assert.True(h.Pipeline.LastTimings.RecordedElements > 10);
        var row = h.Box("row-5");
        h.Move(row.X + 5, row.Y + 5);
        h.Frame();
        var t = h.Pipeline.LastTimings;
        var recorded = Recorded(h);
        Assert.Contains("row-5", recorded);
        Assert.Contains("list", recorded);
        Assert.Contains("app", recorded);
        Assert.DoesNotContain("row-4", recorded);
        Assert.DoesNotContain("row-6", recorded);
        Assert.DoesNotContain("spacer", recorded);
        // 行、列表、app 三个单元；行里的两个文本没有变化，复用
        Assert.Equal(3, t.RecordedElements);
        Assert.True(t.ReusedElements >= 10, t.ToString());
        // 损伤区域只覆盖这一行（含 1px 抗锯齿余量），而不是整个视口
        var d = h.Pipeline.LastDamage!.Value;
        Assert.True(d.Height <= row.Height + 4, d.ToString());
        Assert.True(d.Top >= row.Top - 2 && d.Bottom <= row.Bottom + 2, d + " vs " + row);
    }

    [Fact]
    public void Layout_only_move_translates_cached_subtrees_instead_of_rerecording()
    {
        var app = new ListApp();
        var h = new PipelineHarness(app, ListCss, 300, 800);
        var before = h.Box("row-7");
        app.Spacer.Value = 33;
        h.Frame();
        var after = h.Box("row-7");
        Assert.Equal(before.Y + 23, after.Y);
        // spacer 自身变了；整个列表只是向下移动：列表与各行都不重录，只有 app 以新的偏移重新合成列表的缓存
        Assert.Equal(new string?[] { "spacer", "app" }, Recorded(h));
        Assert.Equal(2, h.Pipeline.LastTimings.RecordedElements);
    }

    [Fact]
    public void Text_change_rerecords_that_paragraph_only()
    {
        var app = new ListApp();
        var h = new PipelineHarness(app, ListCss, 300, 800);
        app.Label.Value = "y";
        h.Frame();
        var t = h.Pipeline.LastTimings;
        // 文本元素、所在行、列表、app
        Assert.Equal(4, t.RecordedElements);
        Assert.Contains("row-3", Recorded(h));
    }

    [Fact]
    public void Nothing_changed_reuses_the_whole_frame()
    {
        var h = new PipelineHarness(new ListApp(), ListCss, 300, 400);
        long v = h.Pipeline.FrameVersion;
        h.Root.RunFrame();
        Assert.Equal(v, h.Pipeline.FrameVersion);
        Assert.False(h.Pipeline.LastTimings.Repainted);
    }

    /// <summary>每一步之后：增量录制 + 只按损伤区域局部光栅化的累积结果，必须与关闭增量绘制、整帧重绘的结果逐字节相同。</summary>
    [Theory]
    [InlineData(1f, false, 0)]
    [InlineData(2f, false, 0)]
    [InlineData(1.5f, false, 0)]
    // 模糊阴影：软件光栅结果与裁剪区域无关（第七批起降采样网格按绝对坐标对齐），局部光栅同样逐字节一致
    [InlineData(1f, true, 0)]
    [InlineData(1.5f, true, 0)]
    [InlineData(2f, true, 0)]
    public void Incremental_frames_with_partial_raster_match_full_repaint(float scale, bool shadow, int tolerance)
    {
        var app = new ListApp();
        var css = ListCss + (shadow ? ".row:nth-child(4) { box-shadow: 0 2px 6px #00000055; }" : "") + """
            .row:nth-child(6) { opacity: 0.5; }
            .row:nth-child(8) { position: relative; z-index: 2; left: 10px; }
            """;
        var h = new PipelineHarness(app, css, 300, 260, scale);
        var refApp = new ListApp();
        var reference = new PipelineHarness(refApp, css, 300, 260, scale);
        reference.Pipeline.IncrementalPaint = false;
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(h.Pipeline.Viewport, h.Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, scale);
        long rendered = -1;

        void Present()
        {
            var p = h.Pipeline;
            PixelRect? damage = null;
            if (rendered >= 0 && p.FrameVersion == rendered + 1 && p.LastDamage is { } d)
            {
                int x0 = Math.Max(0, (int)Math.Floor(d.Left * scale) - 1), y0 = Math.Max(0, (int)Math.Floor(d.Top * scale) - 1);
                int x1 = (int)Math.Ceiling(d.Right * scale) + 1, y1 = (int)Math.Ceiling(d.Bottom * scale) + 1;
                damage = d.Width <= 0 ? new PixelRect(0, 0, 0, 0) : new PixelRect(x0, y0, x1 - x0, y1 - y0);
            }
            if (p.FrameVersion != rendered && damage is not { IsEmpty: true }) target.Render(p.Frame, new RenderOptions(Color.White, damage));
            rendered = p.FrameVersion;
        }

        void Step(string what, Action<ListApp> change, Action<PipelineHarness>? input = null)
        {
            change(app);
            change(refApp);
            input?.Invoke(h);
            input?.Invoke(reference);
            h.Frame();
            reference.Frame();
            Present();
            using var expected = reference.Render();
            var a = target.Bitmap.ToRgbaUnpremultiplied();
            var b = expected.ToRgbaUnpremultiplied();
            if (!a.AsSpan().SequenceEqual(b))
            {
                int diff = 0;
                var where = new System.Text.StringBuilder();
                for (int i = 0; i < a.Length; i++)
                {
                    if (Math.Abs(a[i] - b[i]) <= tolerance) continue;
                    if (diff++ < 6) where.Append($" ({i / 4 % px.Width},{i / 4 / px.Width}) {a[i]}≠{b[i]}");
                }
                if (diff > 0) Assert.Fail(what + ": " + diff + " bytes differ:" + where + " damage " + h.Pipeline.LastDamage);
            }
        }

        Present();
        Step("mount", _ => { });
        Step("select 2", a => a.Selected.Value = 2);
        Step("hover row 5", _ => { }, x => { var r = x.Box("row-5"); x.Move(r.X + 3, r.Y + 3); });
        Step("hover row 7 (z-indexed neighbour)", _ => { }, x => { var r = x.Box("row-7"); x.Move(r.X + 3, r.Y + 3); });
        Step("spacer grows (layout-only move)", a => a.Spacer.Value = 27);
        Step("label change", a => a.Label.Value = "longer label");
        Step("remove rows (content scrolls into view from below)", a => a.Count.Value = 12);
        Step("spacer shrinks (rows move up)", a => a.Spacer.Value = 0);
        Step("add rows back", a => a.Count.Value = 40);
        Step("move pointer away", _ => { }, x => x.Move(299, 259));
        Step("select 7 and grow spacer", a => { a.Selected.Value = 7; a.Spacer.Value = 41.5f; });
    }

    [Fact]
    public void Elements_moving_into_the_viewport_are_recorded()
    {
        var app = new ListApp();
        var h = new PipelineHarness(app, ListCss, 300, 120);
        // 视口只容纳前几行；把 spacer 缩到 0 让更多的行进入视口
        app.Spacer.Value = 0;
        h.Frame();
        app.Count.Value = 4;
        h.Frame();
        app.Count.Value = 30;
        app.Spacer.Value = 5;
        h.Frame();
        using var bmp = h.Render();
        // row-4 在 y ≈ 5 + 4 + 4 * 28 处：其背景（白）与边框可见
        var r4 = h.Box("row-4");
        Assert.True(r4.Bottom < 120);
        ColorAssert.Near(Color.Parse("#d0d7de"), bmp.GetPixel((int)r4.X + 20, (int)r4.Y), 0.05f);
    }

    [Fact]
    public void Unit_nesting_is_bounded_for_deep_trees()
    {
        Node node = new Div { Style = "width: 4px; height: 4px; background-color: #ff0000" };
        for (int i = 0; i < 300; i++) node = new Div { Style = "padding-left: 0.1px", Children = [node] };
        var h = new PipelineHarness(node);
        Assert.True(FrameWalker.Depth(h.Pipeline.Frame) <= 26, FrameWalker.Depth(h.Pipeline.Frame).ToString());
        using var bmp = h.Render();
        ColorAssert.Near(Color.Parse("#ff0000"), bmp.GetPixel(30, 1));
    }

    /// <summary>
    /// M1 评审遗留：不定位的 opacity 层叠上下文位于 overflow:hidden 祖先内，其中绝对定位后代的包含块在该祖先之外——
    /// 浏览器不裁剪它，之前的实现会裁掉。
    /// </summary>
    [Fact]
    public void Absolute_descendant_of_opacity_context_escapes_clip_between_it_and_its_containing_block()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "cb", Style = "position: relative; width: 200px; height: 200px",
            Children =
            [
                new Div
                {
                    Id = "clipper", Style = "overflow: hidden; width: 50px; height: 50px; background-color: #0000ff",
                    Children =
                    [
                        new Div
                        {
                            Id = "fade", Style = "opacity: 0.99; width: 40px; height: 40px",
                            Children =
                            [
                                new Div { Id = "abs", Style = "position: absolute; left: 100px; top: 100px; width: 30px; height: 30px; background-color: #ff0000" },
                                new Div { Id = "inflow", Style = "width: 200px; height: 10px; background-color: #00ff00" },
                            ],
                        },
                    ],
                },
            ],
        }, defaultSheet: false);
        using var bmp = h.Render();
        // 绝对定位的方块（包含块是 #cb）不受 #clipper 裁剪
        var red = bmp.GetPixel(110, 110);
        Assert.True(red.R > 0.9f && red.G < 0.1f && red.B < 0.1f, red.ToString());
        Assert.Equal("abs", h.Pipeline.HitTest(new Point(110, 110))?.Id);
        // 常规流的内容仍被裁剪在 50px 之内
        ColorAssert.Near(Color.White, bmp.GetPixel(120, 5));
        var green = bmp.GetPixel(45, 5);
        Assert.True(green.G > 0.9f && green.R < 0.1f, green.ToString());
        Assert.Equal("clipper", h.Pipeline.HitTest(new Point(45, 45))?.Id);
        Assert.Equal("cb", h.Pipeline.HitTest(new Point(120, 5))?.Id);
    }

    [Fact]
    public void Absolute_descendant_whose_containing_block_is_inside_the_clipper_is_still_clipped()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "overflow: hidden; position: relative; width: 50px; height: 50px",
            Children =
            [
                new Div
                {
                    Style = "opacity: 0.99",
                    Children = [new Div { Id = "abs", Style = "position: absolute; left: 30px; top: 30px; width: 40px; height: 40px; background-color: #ff0000" }],
                },
            ],
        }, defaultSheet: false);
        using var bmp = h.Render();
        Assert.True(bmp.GetPixel(45, 45).R > 0.9f);
        ColorAssert.Near(Color.White, bmp.GetPixel(60, 60));
        Assert.Null(h.Pipeline.HitTest(new Point(60, 60))?.Id);
    }

    [Fact]
    public void Escaping_context_keeps_working_after_incremental_updates()
    {
        var color = new State<string>("#ff0000");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Style = "position: relative; width: 200px; height: 200px",
                Children =
                [
                    new Div
                    {
                        Style = "overflow: hidden; width: 50px; height: 50px",
                        Children =
                        [
                            new Div
                            {
                                Style = "opacity: 0.99",
                                Children = [new Div { Style = "position: absolute; left: 100px; top: 100px; width: 30px; height: 30px; background-color: " + color.Value }],
                            },
                        ],
                    },
                ],
            },
        }, defaultSheet: false);
        color.Value = "#0000ff";
        h.Frame();
        using var bmp = h.Render();
        Assert.True(bmp.GetPixel(110, 110).B > 0.9f);
    }
}

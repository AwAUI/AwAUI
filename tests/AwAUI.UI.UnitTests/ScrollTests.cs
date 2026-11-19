using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>滚动的端到端测试共用的场景与辅助（无头平台 + 软件后端）。</summary>
internal static partial class ScrollSupport
{
    public const string Css = """
        :root { font-size: 13px; background-color: #f8fafc; }
        .scroller { position: absolute; left: 20px; top: 30px; width: 200px; height: 150px; overflow: auto;
                    background-color: #ffffff; border: 2px solid #94a3b8; }
        .row { height: 30px; border-bottom: 1px solid #e2e8f0; background-color: #eef2ff; }
        .row:nth-child(2n) { background-color: #fde68a; }
        .wide { width: 600px; }
        .inner { height: 80px; overflow: auto; margin: 10px; background-color: #dcfce7; }
        .inner-row { height: 25px; background-color: #bbf7d0; border-bottom: 1px solid #16a34a; }
        """;

    public sealed partial class ListApp : Component
    {
        public int Rows { get; set; } = 40;
        public ScrollController? Controller { get; set; }
        public bool Nested { get; set; }
        public string? ScrollerStyle { get; set; }
        public Action<ScrollEvent>? OnScrolled { get; set; }
        public Action<PointerEvent>? OnRowClick { get; set; }
        public Action<PointerEvent>? OnRowCancel { get; set; }
        public Action<WheelEvent>? OnScrollerWheel { get; set; }

        protected override Node Build()
        {
            var rows = new NodeList();
            if (Nested)
            {
                var inner = new NodeList();
                for (int i = 0; i < 10; i++) inner.Add(new Div { Key = i, Id = "inner-row-" + i, Class = "inner-row", Children = ["inner " + i] });
                rows.Add(new Div { Key = "inner", Id = "inner", Class = "inner", Children = inner });
            }
            for (int i = 0; i < Rows; i++)
                rows.Add(new Div { Key = "r" + i, Id = "row-" + i, Class = "row", TabIndex = 0, OnClick = OnRowClick, OnPointerCancel = OnRowCancel, Children = ["Row " + i] });
            return new Div
            {
                Id = "scroller", Class = "scroller", Style = ScrollerStyle, ScrollController = Controller, OnScroll = OnScrolled, OnWheel = OnScrollerWheel,
                Children = rows,
            };
        }
    }

    public static UIHost CreateHost(HeadlessPlatform platform, Node root, float w = 300, float h = 240, string css = Css) =>
        AwApp.CreateHost(platform, new WindowOptions { Size = new Size(w, h) }, new SoftwareRenderBackend(), new TestTextEngine(), root,
            AwAUI.Css.StyleSheet.Parse(css));

    public static (UIHost Host, HeadlessWindow Window, ManualUIClock Clock) Create(Node root, float w = 300, float h = 240, double scale = 1,
        string css = Css, bool smooth = true)
    {
        var platform = new HeadlessPlatform { DefaultScale = scale };
        var host = CreateHost(platform, root, w, h, css);
        var clock = new ManualUIClock();
        host.Root.Clock = clock;
        host.Pipeline.SmoothScrolling = smooth;
        host.RunFrame();
        return (host, (HeadlessWindow)host.Window, clock);
    }

    /// <summary>推进时钟并执行一帧（帧时钟的确定性替身）。</summary>
    public static void Step(UIHost host, ManualUIClock clock, double ms = 16)
    {
        clock.Advance(TimeSpan.FromMilliseconds(ms));
        host.RunFrame();
    }

    /// <summary>反复推进直到不再需要动画帧（上限 <paramref name="maxFrames"/> 帧）。</summary>
    public static int Settle(UIHost host, ManualUIClock clock, int maxFrames = 600, double ms = 16)
    {
        int n = 0;
        do
        {
            Step(host, clock, ms);
            n++;
        } while (host.Pipeline.NeedsAnimationFrame && n < maxFrames);
        return n;
    }

    public static void Pointer(HeadlessWindow w, RawPointerEventType type, Point p, PointerType device = PointerType.Mouse, long id = 1,
        PointerButtons changed = PointerButtons.None, PointerButtons buttons = PointerButtons.None, double ms = 0) =>
        w.SendPointer(new RawPointerEvent(type, device, id, p, changed, buttons, KeyModifiers.None, TimeSpan.FromMilliseconds(ms)));

    /// <summary>当前帧整帧光栅化（不走损伤矩形）的像素。</summary>
    public static byte[] FullRaster(UIHost host)
    {
        var px = PixelSize.FromSize(host.Pipeline.Viewport, host.Pipeline.Scale);
        using var target = (BitmapRenderTarget)new SoftwareRenderBackend().CreateOffscreenTarget(px, host.Pipeline.Scale);
        target.Render(host.PresentedFrame, new RenderOptions(host.ClearColor));
        return target.Bitmap.ToRgbaUnpremultiplied();
    }

    public static byte[] Pixels(HeadlessWindow w) => w.Framebuffer!.ToRgbaUnpremultiplied();
}

/// <summary>滚动容器的几何、绘制、命中测试与损伤（design/21「滚动」）。</summary>
public class ScrollGeometryTests
{
    [Fact]
    public void Scroll_container_reports_metrics_and_wheel_moves_content_and_hit_testing()
    {
        var app = new ScrollSupport.ListApp { Rows = 40 };
        var (host, window, clock) = ScrollSupport.Create(app, smooth: false);
        var scroller = host.Root.RootElement.ById("scroller");
        var c = scroller.ScrollController;
        Assert.Equal(new Size(196, 146), c.ViewportSize);
        // 40 行 × 30 像素（border-box，含下边框）
        Assert.Equal(40 * 30, c.ContentSize.Height, 0.5f);
        Assert.Equal(40 * 30 - 146, c.MaxOffset.Y, 0.5f);
        var row0 = host.Root.RootElement.ById("row-0");
        var top0 = row0.BoundingRect.Y;
        Assert.Equal(32, top0);

        var inside = new Point(100, 100);
        Assert.Equal("row-2", host.Pipeline.HitTest(inside)?.Id);
        window.WheelNotches(inside, 1); // 100 像素
        host.RunFrame();
        Assert.Equal(100, c.Offset.Y);
        Assert.Equal(top0 - 100, row0.BoundingRect.Y);
        // 命中测试按滚动后的位置：y=100 处现在是第 (100-32+100)/30 = 5 行
        Assert.Equal("row-5", host.Pipeline.HitTest(inside)?.Id);
        Assert.True(host.Pipeline.LastFrameScrolled);
        Assert.False(host.LastFrame.Pipeline.LayoutRan);

        // 损伤只是滚动容器的视口（不是整个窗口）
        var damage = host.LastDamage!.Value;
        Assert.True(damage.Width <= 202 && damage.Height <= 152, damage.ToString());
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));

        // 滚到底：钳制
        for (int i = 0; i < 20; i++) window.WheelNotches(inside, 1);
        host.RunFrame();
        Assert.Equal(c.MaxOffset.Y, c.Offset.Y);
        var last = host.Root.RootElement.ById("row-39");
        Assert.Equal(scroller.BoundingRect.Bottom - 2, last.BoundingRect.Bottom, 0.5f);
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));
    }

    [Fact]
    public void Partial_raster_after_scrolling_matches_a_non_incremental_full_render()
    {
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var a = ScrollSupport.Create(new ScrollSupport.ListApp { Rows = 60, Nested = true }, scale: scale, smooth: false);
            var b = ScrollSupport.Create(new ScrollSupport.ListApp { Rows = 60, Nested = true }, scale: scale, smooth: false);
            b.Host.Pipeline.IncrementalPaint = false;
            b.Host.PartialRaster = false;
            var ca = a.Host.Root.RootElement.ById("scroller").ScrollController;
            var cb = b.Host.Root.RootElement.ById("scroller").ScrollController;
            var ia = a.Host.Root.RootElement.ById("inner").ScrollController;
            var ib = b.Host.Root.RootElement.ById("inner").ScrollController;
            var steps = new (float Outer, float Inner)[] { (13.3f, 0), (40, 7), (0, 30), (333, 30), (17.5f, 0), (2000, 999) };
            foreach (var (outer, inner) in steps)
            {
                ca.JumpTo(new Point(0, outer));
                ia.JumpTo(new Point(0, inner));
                cb.JumpTo(new Point(0, outer));
                ib.JumpTo(new Point(0, inner));
                a.Host.RunFrame();
                b.Host.RunFrame();
                Assert.Equal(ca.Offset, cb.Offset);
                Assert.NotNull(a.Host.LastDamage);
                Assert.True(ScrollSupport.Pixels(a.Window).AsSpan().SequenceEqual(ScrollSupport.Pixels(b.Window)),
                    $"scale {scale}, outer {outer}, inner {inner}");
                for (int y = 5; y < 240; y += 11)
                for (int x = 5; x < 300; x += 13)
                    Assert.Equal(b.Host.Pipeline.HitTest(new Point(x, y))?.Id, a.Host.Pipeline.HitTest(new Point(x, y))?.Id);
            }
        }
    }

    [Fact]
    public void Scroll_position_survives_rebuilds_and_is_clamped_when_content_shrinks()
    {
        var rows = new State<int>(40);
        var scrolled = new List<Point>();
        var app = new Lambda
        {
            Render = () => new ScrollSupport.ListApp { Rows = rows.Value, OnScrolled = e => scrolled.Add(e.Offset) },
        };
        var (host, window, clock) = ScrollSupport.Create(app, smooth: false);
        var scroller = host.Root.RootElement.ById("scroller");
        scroller.ScrollController.JumpTo(new Point(0, 500));
        host.RunFrame();
        Assert.Equal(500, scroller.ScrollController.Offset.Y);
        Assert.Equal(new[] { new Point(0, 500) }, scrolled);
        rows.Value = 39; // 重建：位置保留
        host.RunFrame();
        Assert.Same(scroller, host.Root.RootElement.ById("scroller"));
        Assert.Equal(500, scroller.ScrollController.Offset.Y);
        rows.Value = 10; // 内容变小：钳制到 300 − 146
        host.RunFrame();
        Assert.Equal(10 * 30 - 146, scroller.ScrollController.Offset.Y, 0.5f);
        Assert.Equal(2, scrolled.Count);
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));
    }

    [Fact]
    public void Absolute_descendant_with_containing_block_outside_does_not_scroll_and_fixed_never_scrolls()
    {
        const string css = ScrollSupport.Css + """
            .scroller { position: static; margin: 10px; }
            .pin { position: absolute; left: 240px; top: 10px; width: 20px; height: 20px; background-color: red; }
            .fix { position: fixed; left: 270px; top: 10px; width: 20px; height: 20px; background-color: blue; }
            .rel { position: relative; height: 40px; }
            .child { position: absolute; left: 5px; top: 5px; width: 10px; height: 10px; background-color: green; }
            """;
        var app = new Lambda
        {
            Render = () => new Div
            {
                Id = "scroller", Class = "scroller",
                Children =
                [
                    new Div { Id = "pin", Class = "pin" },
                    new Div { Id = "fix", Class = "fix" },
                    new Div { Class = "rel", Children = [new Div { Id = "child", Class = "child" }] },
                    new Div { Class = "row" }, new Div { Class = "row" }, new Div { Class = "row" }, new Div { Class = "row" },
                    new Div { Class = "row" }, new Div { Class = "row" }, new Div { Class = "row" }, new Div { Class = "row" },
                ],
            },
        };
        var (host, window, _) = ScrollSupport.Create(app, css: css, smooth: false);
        var root = host.Root.RootElement;
        var pin = root.ById("pin").BoundingRect;
        var fix = root.ById("fix").BoundingRect;
        var child = root.ById("child").BoundingRect;
        root.ById("scroller").ScrollController.JumpTo(new Point(0, 50));
        host.RunFrame();
        Assert.Equal(pin, root.ById("pin").BoundingRect);
        Assert.Equal(fix, root.ById("fix").BoundingRect);
        Assert.Equal(child.Y - 50, root.ById("child").BoundingRect.Y);
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));
    }

    [Fact]
    public void Root_viewport_scrolls_when_content_overflows_the_window()
    {
        const string css = ":root { background-color: #fff; } .tall { height: 1000px; background-color: #e0f2fe; } .fix { position: fixed; top: 0; left: 0; width: 30px; height: 30px; background-color: red; }";
        var app = new Lambda { Render = () => new Div { Children = [new Div { Id = "tall", Class = "tall" }, new Div { Id = "fix", Class = "fix" }] } };
        var (host, window, _) = ScrollSupport.Create(app, 200, 300, css: css, smooth: false);
        var rootCtl = host.Root.RootElement.ScrollController;
        Assert.Equal(new Size(200, 300), rootCtl.ViewportSize);
        Assert.Equal(1000, rootCtl.ContentSize.Height);
        window.WheelNotches(new Point(100, 100), 2);
        host.RunFrame();
        Assert.Equal(200, rootCtl.Offset.Y);
        Assert.Equal(-200, host.Root.RootElement.ById("tall").BoundingRect.Y);
        Assert.Equal(0, host.Root.RootElement.ById("fix").BoundingRect.Y);
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));
    }
}

/// <summary>
/// 概览截图：设置环境变量 AWAUI_SCROLL_SHEET=路径 时写出一张 PNG（经典滚动条：滚动过的列表、横纵两向的嵌套容器；
/// 覆盖式滚动条：滚动中显示的指示条）。不设置时只检查能画出、局部光栅与整帧一致。
/// </summary>
public partial class ScrollScreenshotTests
{
    const string SheetCss = """
        :root { font-size: 13px; background-color: #e2e8f0; }
        .wrap { display: flex; gap: 16px; padding: 16px; }
        .list { width: 210px; height: 260px; overflow: auto; background-color: #ffffff; border: 1px solid #94a3b8; border-radius: 6px; }
        .item { display: flex; align-items: center; height: 34px; padding: 0 10px; border-bottom: 1px solid #e2e8f0; }
        .item:nth-child(2n) { background-color: #f8fafc; }
        .badge { width: 22px; height: 22px; border-radius: 11px; margin-right: 10px; background-color: #6366f1; }
        .grid { width: 210px; height: 260px; overflow: auto; background-color: #fff7ed; border: 1px solid #fb923c; border-radius: 6px; }
        .canvas { width: 520px; height: 640px; background-color: #ffedd5; }
        .tile { position: relative; left: 12px; width: 480px; height: 60px; margin-top: 14px; background-color: #fdba74; border-radius: 8px; }
        """;

    sealed partial class Sheet : Component
    {
        protected override Node Build()
        {
            var items = new NodeList();
            for (int i = 0; i < 40; i++)
                items.Add(new Div { Key = i, Class = "item", Children = [new Div { Class = "badge" }, new Text("List item " + (i + 1))] });
            var tiles = new NodeList();
            for (int i = 0; i < 9; i++) tiles.Add(new Div { Key = i, Class = "tile", Children = [new Text("  tile " + (i + 1))] });
            return new Div
            {
                Class = "wrap",
                Children =
                [
                    new Div { Id = "list", Class = "list", Children = items },
                    new Div { Id = "grid", Class = "grid", Children = [new Div { Class = "canvas", Children = tiles }] },
                ],
            };
        }
    }

    [Fact]
    public void Scrolled_lists_with_visible_scrollbars()
    {
        var classic = new HeadlessPlatform { DefaultScale = 2, DefaultScrollbarStyle = ScrollbarStyle.Classic };
        var host = ScrollSupport.CreateHost(classic, new Sheet(), 470, 300, SheetCss);
        host.Root.Clock = new ManualUIClock();
        host.Pipeline.SmoothScrolling = false;
        host.RunFrame();
        var window = (HeadlessWindow)host.Window;
        var root = host.Root.RootElement;
        window.WheelNotches(root.ById("list").BoundingRect.Center, 2.6f);
        window.WheelNotches(root.ById("grid").BoundingRect.Center, 1.2f);
        window.WheelNotches(root.ById("grid").BoundingRect.Center, 0.9f, modifiers: KeyModifiers.Shift);
        host.RunFrame();
        Assert.Equal(260, root.ById("list").ScrollController.Offset.Y, 0.5f);
        Assert.Equal(90, root.ById("grid").ScrollController.Offset.X, 0.01f);
        Assert.Equal(120, root.ById("grid").ScrollController.Offset.Y, 0.01f);
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));

        var overlay = new HeadlessPlatform { DefaultScale = 2 };
        var host2 = ScrollSupport.CreateHost(overlay, new Sheet(), 470, 300, SheetCss);
        host2.Root.Clock = new ManualUIClock();
        host2.Pipeline.SmoothScrolling = false;
        host2.RunFrame();
        var window2 = (HeadlessWindow)host2.Window;
        window2.WheelNotches(host2.Root.RootElement.ById("list").BoundingRect.Center, 4.4f);
        window2.TrackpadScroll(host2.Root.RootElement.ById("grid").BoundingRect.Center, 200, 150, ScrollPhase.Began);
        host2.RunFrame();
        Assert.Equal(ScrollSupport.FullRaster(host2), ScrollSupport.Pixels(window2));

        var path = Environment.GetEnvironmentVariable("AWAUI_SCROLL_SHEET");
        if (string.IsNullOrEmpty(path)) return;
        var a = window.Framebuffer!.ToRgbaUnpremultiplied();
        var b = window2.Framebuffer!.ToRgbaUnpremultiplied();
        var size = window.Framebuffer!.Size;
        var both = new byte[a.Length + b.Length];
        a.CopyTo(both, 0);
        b.CopyTo(both, a.Length);
        using var sheet = Bitmap.FromRgbaUnpremultiplied(both, new PixelSize(size.Width, size.Height * 2));
        File.WriteAllBytes(path, AwAUI.Imaging.PngCodec.Encode(sheet));
    }
}

/// <summary>
/// 滚动一帧的开销（只输出，不断言时间）：2000 行的列表在 overflow:auto 容器里，每帧滚动 3 像素（触控板式）。
/// 断言的是工作量：滚动帧不重新布局、只重录容器（及新露出的行），损伤是容器视口。用 -p:Optimize=true 测性能数字。
/// </summary>
public class ScrollFrameCostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const string Css = """
        :root { font-size: 13px; }
        .scroller { height: 560px; overflow: auto; margin: 10px; border: 1px solid #ccc; }
        .row { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-bottom: 1px solid #e2e8f0; }
        .row:nth-child(2n) { background-color: #f8fafc; }
        .idx { width: 56px; color: #64748b; }
        .name { flex: 1; }
        """;

    [Fact]
    public void Scrolling_a_2000_row_list_records_only_the_viewport()
    {
        var rows = new NodeList();
        for (int i = 0; i < 2000; i++)
            rows.Add(new Div { Key = i, Class = "row", Children = [new Text("#" + i) { Class = "idx" }, new Text("Item number " + i + " — the quick brown fox") { Class = "name" }, new Button { Children = ["Select"] }] });
        var platform = new HeadlessPlatform { DefaultScale = 2 };
        var host = ScrollSupport.CreateHost(platform, new Div { Id = "scroller", Class = "scroller", Children = rows }, 800, 600, Css);
        host.Root.Clock = new ManualUIClock();
        host.RunFrame();
        var window = (HeadlessWindow)host.Window;
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        var p = new Point(300, 300);
        var pipeline = new List<double>();
        var raster = new List<double>();
        var total = new List<double>();
        int recorded = 0, reused = 0;
        // 预热
        for (int i = 0; i < 20; i++)
        {
            window.TrackpadScroll(p, 3, 0, ScrollPhase.Changed);
            host.RunFrame();
        }
        for (int i = 0; i < 200; i++)
        {
            window.TrackpadScroll(p, 3, 0, ScrollPhase.Changed);
            host.RunFrame();
            var f = host.LastFrame;
            Assert.False(f.Pipeline.LayoutRan);
            pipeline.Add(f.Pipeline.StyleMs + f.Pipeline.LayoutMs + f.Pipeline.PaintMs);
            raster.Add(f.RasterMs);
            total.Add(f.TotalMs);
            recorded += f.Pipeline.RecordedElements;
            reused += f.Pipeline.ReusedElements;
            var d = host.LastDamage!.Value;
            Assert.True(d.Height <= 2 * 562 + 4, d.ToString());
        }
        Assert.Equal(20 * 3 + 200 * 3, c.Offset.Y, 1);
        // 平均每帧重录的单元：容器本身、外层单元与新露出的少数几行（不是整个列表）
        Assert.True(recorded / 200.0 < 40, $"recorded {recorded / 200.0:F1} per frame");
        static double Median(List<double> v)
        {
            v.Sort();
            return v[v.Count / 2];
        }
        output.WriteLine(FormattableString.Invariant(
            $"2000 rows @2x, 3 px per frame: pipeline median {Median(pipeline):F3} ms, raster median {Median(raster):F3} ms, total median {Median(total):F3} ms; recorded {recorded / 200.0:F1} / reused {reused / 200.0:F1} units per frame"));
    }
}

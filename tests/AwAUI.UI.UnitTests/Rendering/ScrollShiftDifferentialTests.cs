using System.Diagnostics;
using AwAUI.Css;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 纯滚动帧的像素复用（design/18「滚动：位块搬移已有像素 + 重画新露出的条带」）。
/// 只改滚动偏移的帧走快路径；无法证明安全的情形（圆角裁剪、覆盖层、fixed / sticky 后代）必须退回整视口重画。
/// 每个用例都把窗口里累积的像素与「同一帧整帧重画」逐字节比较。
/// </summary>
public partial class ScrollShiftDifferentialTests(Xunit.Abstractions.ITestOutputHelper output)
{
    readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    const string BaseCss = """
        :root { font-size: 13px; background-color: #f8fafc; }
        .scroller { position: absolute; left: 20px; top: 30px; width: 200px; height: 150px; overflow: auto;
                    background-color: #ffffff; border: 2px solid #94a3b8; }
        .wrap { position: absolute; left: 16px; top: 24px; width: 186px; height: 128px; overflow: hidden; background-color: #cbd5e1; }
        .wrap.rounded { border-radius: 14px; }
        .scroller.inset { position: relative; left: 6px; top: 4px; }
        .row { height: 30px; border-bottom: 1px solid #e2e8f0; background-color: #eef2ff; }
        .row:nth-child(2n) { background-color: #fde68a; }
        .row.wide { width: 600px; }
        .overlay { position: absolute; left: 70px; top: 80px; width: 70px; height: 40px; background-color: #111827; z-index: 4; }
        .fixed { position: fixed; left: 8px; top: 8px; width: 22px; height: 18px; background-color: #7c3aed; z-index: 6; }
        .sticky { position: sticky; top: 4px; height: 12px; background-color: #0ea5e9; }
        """;

    /// <summary>固定内容：若干行（可加宽以产生横向溢出）、可选覆盖层与特殊子元素。</summary>
    sealed partial class RowsApp : Component
    {
        public int Rows { get; set; } = 40;
        public bool Wide { get; set; }
        public bool Overlay { get; set; }
        public bool FixedChild { get; set; }
        public bool StickyChild { get; set; }

        protected override Node Build()
        {
            var rows = new NodeList();
            if (FixedChild) rows.Add(new Div { Key = "fixed", Class = "fixed" });
            if (StickyChild) rows.Add(new Div { Key = "sticky", Class = "sticky" });
            for (int i = 0; i < Rows; i++)
                rows.Add(new Div { Key = i, Id = "row-" + i, Class = Wide ? "row wide" : "row", Children = ["Row " + i] });
            var scroller = new Div { Id = "scroller", Class = "scroller", Children = rows };
            if (!Overlay) return scroller;
            return new Div { Children = [scroller, new Div { Id = "overlay", Class = "overlay" }] };
        }
    }

    sealed partial class WrappedList : Component
    {
        public bool Rounded { get; set; }
        public int Rows { get; set; } = 40;

        protected override Node Build()
        {
            var rows = new NodeList();
            for (int i = 0; i < Rows; i++)
                rows.Add(new Div { Key = i, Class = "row", Children = ["Row " + i] });
            return new Div
            {
                Class = Rounded ? "wrap rounded" : "wrap",
                Children = [new Div { Id = "scroller", Class = "scroller inset", Children = rows }],
            };
        }
    }

    static (UIHost Host, HeadlessWindow Window) Start(Node root, float scale, string? extraCss = null)
    {
        var platform = new HeadlessPlatform { DefaultScale = scale };
        var host = AwApp.CreateHost(platform, new WindowOptions { Size = new Size(320, 260) }, new SoftwareRenderBackend(),
            new TestTextEngine(), root, StyleSheet.Parse(BaseCss + (extraCss ?? "")));
        host.Root.Clock = new ManualUIClock();
        host.Pipeline.SmoothScrolling = false;
        host.RunFrame();
        return (host, (HeadlessWindow)host.Window);
    }

    static void Expect(UIHost host, HeadlessWindow window, bool fast)
    {
        Assert.False(host.LastFrame.Pipeline.LayoutRan);
        if (fast) Assert.NotNull(host.Pipeline.LastScrollShift);
        else Assert.Null(host.Pipeline.LastScrollShift);
        var expected = FullRaster(host);
        var actual = Pixels(window);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            int diff = 0;
            for (int i = 0; i < actual.Length; i += 4)
                if (!actual.AsSpan(i, 4).SequenceEqual(expected.AsSpan(i, 4))) diff++;
            Assert.Fail($"frame differs from a full redraw in {diff} pixels (fast={(host.Pipeline.LastScrollShift is not null)}, damage={host.LastDamage})");
        }
    }

    static ScrollController Scroll(UIHost host) => host.Root.RootElement.ById("scroller").ScrollController!;

    [Theory]
    [InlineData(1f, 0f, 3f)]
    [InlineData(2f, 0f, 3f)]
    [InlineData(1f, 3f, 0f)]
    [InlineData(2f, 5f, 0f)]
    public void Vertical_and_horizontal_scrolling_reuse_pixels_and_match_a_full_redraw(float scale, float dx, float dy)
    {
        bool horizontal = dx != 0;
        var (host, window) = Start(new RowsApp { Wide = horizontal }, scale);
        var c = Scroll(host);
        Assert.True(c.MaxOffsetY > 0);
        if (horizontal) Assert.True(c.MaxOffsetX > 0);
        // 第一帧滚动会显示覆盖式滚动条（不透明度变化），之后是纯滚动帧
        for (int i = 0; i < 8; i++)
        {
            window.TrackpadScroll(new Point(100, 100), dy, dx, ScrollPhase.Changed);
            host.RunFrame();
            Expect(host, window, fast: i >= 1);
        }
        Assert.Equal(8 * Math.Abs(horizontal ? dx : dy), horizontal ? c.OffsetX : c.Offset.Y, 1);
    }

    [Fact]
    public void Nested_axis_aligned_clip_still_uses_the_fast_path()
    {
        var (host, window) = Start(new WrappedList(), 1f);
        var c = Scroll(host);
        for (int i = 0; i < 6; i++)
        {
            window.TrackpadScroll(new Point(60, 70), 4, 0, ScrollPhase.Changed);
            host.RunFrame();
            Expect(host, window, fast: i >= 1);
        }
        Assert.True(c.Offset.Y > 0);
    }

    [Fact]
    public void Rounded_ancestor_clip_falls_back_to_a_full_redraw()
    {
        var (host, window) = Start(new WrappedList { Rounded = true }, 2f);
        for (int i = 0; i < 4; i++)
        {
            window.TrackpadScroll(new Point(60, 70), 4, 0, ScrollPhase.Changed);
            host.RunFrame();
            Expect(host, window, fast: false);
        }
        // 退回时损伤必须是整个滚动视口（不是新露出的条带）
        Assert.True(host.LastDamage!.Value.Height >= 146);
    }

    [Fact]
    public void Content_painted_above_the_scroller_falls_back_to_a_full_redraw()
    {
        var (host, window) = Start(new RowsApp { Overlay = true }, 1f);
        for (int i = 0; i < 4; i++)
        {
            // 指针避开覆盖层（覆盖层会命中并吞掉滚轮），但仍落在滚动视口里
            window.TrackpadScroll(new Point(45, 60), 3, 0, ScrollPhase.Changed);
            host.RunFrame();
            Expect(host, window, fast: false);
        }
        Assert.True(host.LastDamage!.Value.Height >= 146);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fixed_or_sticky_descendants_fall_back_to_a_full_redraw(bool fixedChild)
    {
        var (host, window) = Start(new RowsApp { FixedChild = fixedChild, StickyChild = !fixedChild }, 1f);
        for (int i = 0; i < 4; i++)
        {
            window.TrackpadScroll(new Point(100, 100), 3, 0, ScrollPhase.Changed);
            host.RunFrame();
            Expect(host, window, fast: false);
        }
        Assert.True(host.LastDamage!.Value.Height >= 146);
    }

    [Fact]
    public void Fast_path_paints_the_scrollbar_thumb_at_its_new_position()
    {
        var (host, window) = Start(new RowsApp(), 2f);
        var c = Scroll(host);
        for (int i = 0; i < 10; i++)
        {
            window.TrackpadScroll(new Point(100, 100), 3, 0, ScrollPhase.Changed);
            host.RunFrame();
            Expect(host, window, fast: i >= 1);
        }
        Assert.True(c.Offset.Y >= 30);
        // 滚动条条带一定在损伤里（覆盖式滑块的位置变了）
        var d = host.LastDamage!.Value;
        var vp = host.Root.RootElement.ById("scroller").BoundingRect;
        Assert.True(d.Right >= vp.Right - 1, $"damage {d} should reach the scrollbar band at {vp.Right}");
    }

    // ------------------------------------------------------------ 2000 行列表的确定性测量

    const string CostCss = """
        :root { font-size: 13px; }
        .scroller { height: 560px; overflow: auto; margin: 10px; background-color: #ffffff; border: 1px solid #ccc; }
        .row { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-bottom: 1px solid #e2e8f0; }
        .row:nth-child(2n) { background-color: #f8fafc; }
        .idx { width: 56px; color: #64748b; }
        .name { flex: 1; }
        """;

    static PipelineHarness MakeLargeList(float scale)
    {
        var rows = new NodeList();
        for (int i = 0; i < 2000; i++)
            rows.Add(new Div
            {
                Key = i,
                Class = "row",
                Children = [new Text("#" + i) { Class = "idx" }, new Text("Item number " + i + " — the quick brown fox") { Class = "name" }],
            });
        return new PipelineHarness(new Div { Id = "scroller", Class = "scroller", Children = rows }, CostCss, 800, 600, scale);
    }

    static PixelRect? DamageRect(PipelineHarness h)
    {
        var p = h.Pipeline;
        if (p.LastDamage is not { } d) return null;
        if (d.Width <= 0 || d.Height <= 0) return new PixelRect(0, 0, 0, 0);
        double scale = p.Scale;
        int x0 = Math.Max(0, (int)Math.Floor(d.Left * scale) - 1), y0 = Math.Max(0, (int)Math.Floor(d.Top * scale) - 1);
        int x1 = (int)Math.Ceiling(d.Right * scale) + 1, y1 = (int)Math.Ceiling(d.Bottom * scale) + 1;
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    [Fact]
    public void Two_thousand_row_pure_scroll_measures_rasterized_pixels_and_blits()
    {
        const int Frames = 18;
        var on = MakeLargeList(2f);
        var off = MakeLargeList(2f);
        off.Pipeline.ScrollShiftOptimization = false;
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(on.Pipeline.Viewport, on.Pipeline.Scale);
        using var onTarget = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, on.Pipeline.Scale);
        using var offTarget = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, off.Pipeline.Scale);
        long onVersion = -1, offVersion = -1;

        void Present(PipelineHarness h, BitmapRenderTarget target, ref long rendered, bool shift, List<double> ms)
        {
            var p = h.Pipeline;
            var damage = rendered >= 0 ? DamageRect(h) : null;
            var sw = Stopwatch.StartNew();
            if (p.FrameVersion != rendered && damage is not { IsEmpty: true })
                target.Render(p.Frame, new RenderOptions(Color.White, damage, shift ? p.LastScrollShift : null, shift ? p.LastScrollDamage : null));
            sw.Stop();
            rendered = p.FrameVersion;
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }

        var onMs = new List<double>();
        var offMs = new List<double>();
        Present(on, onTarget, ref onVersion, shift: true, onMs);
        Present(off, offTarget, ref offVersion, shift: false, offMs);
        var onController = on.El("scroller").ScrollController!;
        var offController = off.El("scroller").ScrollController!;

        long onPixels = 0, offPixels = 0, onCommands = 0, offCommands = 0, onBlits = 0, offBlits = 0;
        int measured = 0;
        for (int i = 0; i < Frames; i++)
        {
            onController.JumpTo(0, onController.OffsetY + 3);
            offController.JumpTo(0, offController.OffsetY + 3);
            on.Root.RunFrame();
            off.Root.RunFrame();
            Present(on, onTarget, ref onVersion, shift: true, onMs);
            Present(off, offTarget, ref offVersion, shift: false, offMs);
            if (i == 0) continue; // 第一帧显示覆盖式滚动条（不透明度变化），两种路径都整视口重画
            var a = onTarget.LastFrameStats;
            var b = offTarget.LastFrameStats;
            if (i >= 2) Assert.NotNull(on.Pipeline.LastScrollShift);
            Assert.Equal(1, a.BlitCalls);
            Assert.Equal(0, b.BlitCalls);
            onPixels += a.RasterizedPixels; offPixels += b.RasterizedPixels;
            onCommands += a.CommandCount; offCommands += b.CommandCount;
            onBlits += a.BlitPixels; offBlits += b.BlitPixels;
            measured++;
        }
        Assert.True(measured >= Frames - 1);
        // 复用像素：着色像素数应下降一个数量级以上；命令流（整段 DisplayList 仍需解释）基本不变
        Assert.True(onPixels * 8 < offPixels,
            FormattableString.Invariant($"rasterized pixels on={onPixels} off={offPixels} per frame on={onPixels / (double)measured:F0} off={offPixels / (double)measured:F0}"));
        Assert.True(onBlits > 0);
        Assert.True(offBlits == 0);

        static double Median(List<double> v)
        {
            var s = new List<double>(v);
            s.Sort();
            return s[s.Count / 2];
        }
        _output.WriteLine(FormattableString.Invariant($"2000 rows @2x, 3 logical px per frame, {measured} pure-scroll frames: before (no shift) {offPixels / (double)measured:F0} rasterized px/frame, {offCommands / (double)measured:F0} commands/frame, median raster {Median(offMs):F2} ms; after (shift) {onPixels / (double)measured:F0} rasterized px/frame, {onCommands / (double)measured:F0} commands/frame, {onBlits / (double)measured:F0} blit px/frame, median raster {Median(onMs):F2} ms"));
    }
}

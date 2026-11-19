using AwAUI.Headless;
using AwAUI.Platform;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 复审补充：只平移滚动容器子树的几何遍历与完整几何遍历的结果一致（嵌套、定位、fixed、非整数缩放），
/// 以及各种边界条件（动画中内容变小、程序滚动被另一轴的用户输入打断、模拟从边界出发）。
/// </summary>
public partial class ScrollReviewTests
{
    const string GeoCss = """
        :root { font-size: 12px; background-color: #f1f5f9; }
        .outer { margin: 7px; height: 160px; overflow: auto; border: 3px solid #64748b; padding: 5px; background-color: #fff; }
        .pos { position: relative; margin: 4px; }
        .inner { height: 60px; overflow: auto; border: 2px solid #16a34a; padding: 3px; background-color: #f0fdf4; }
        .row { height: 21px; border-bottom: 1px solid #cbd5e1; background-color: #e0e7ff; }
        .wide { width: 400px; }
        .absout { position: absolute; left: 150px; top: 20px; width: 18px; height: 18px; background-color: #f97316; }
        .absin { position: absolute; right: 2px; top: 2px; width: 10px; height: 10px; background-color: #0ea5e9; }
        .fix { position: fixed; left: 2px; bottom: 2px; width: 14px; height: 14px; background-color: #dc2626; }
        .tall { height: 400px; background-color: #fef9c3; }
        """;

    sealed partial class GeoScene : Component
    {
        protected override Node Build()
        {
            var inner = new NodeList();
            for (int i = 0; i < 9; i++) inner.Add(new Div { Key = i, Id = "i" + i, Class = i % 3 == 0 ? "row wide" : "row", Children = [new Div { Class = "absin", Id = "ia" + i }] });
            var outer = new NodeList
            {
                new Div { Key = "absout", Id = "absout", Class = "absout" },
                new Div { Key = "pos", Id = "pos", Class = "pos", Children = [new Div { Id = "inner", Class = "inner", Children = inner }, new Div { Id = "posabs", Class = "absin" }] },
                new Div { Key = "fix", Id = "fix", Class = "fix" },
            };
            for (int i = 0; i < 20; i++) outer.Add(new Div { Key = "r" + i, Id = "r" + i, Class = i % 4 == 1 ? "row wide" : "row" });
            return new Div { Children = [new Div { Id = "outer", Class = "outer", Children = outer }, new Div { Class = "tall" }] };
        }
    }

    static Dictionary<Element, Rect> Snapshot(Element root)
    {
        var map = new Dictionary<Element, Rect>();
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            map[e] = e.BoundingRect;
            foreach (var c in e.Children) stack.Push(c);
        }
        return map;
    }

    public static IEnumerable<object[]> GeoSeeds()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("AWAUI_SCROLL_DIFF_SEEDS"), out var v) && v > 0 ? v : 9;
        for (int i = 0; i < n; i++) yield return new object[] { i, i % 3 == 0 ? 1.0 : i % 3 == 1 ? 1.5 : 1.25 };
    }

    /// <summary>
    /// 只平移子树的几何（滚动帧不重新布局）与强制完整布局 + 完整几何遍历的结果逐元素相同，像素也相同。
    /// （差分测试的参照宿主同样走「只平移」的路径，看不出两条路径的分歧。）
    /// </summary>
    [Theory]
    [MemberData(nameof(GeoSeeds))]
    public void Scroll_only_geometry_matches_a_full_geometry_walk(int seed, double scale)
    {
        var rnd = new Random(seed);
        var (host, window, clock) = Create(new GeoScene(), 260, 220, scale, GeoCss, smooth: rnd.Next(2) == 0);
        var root = host.Root.RootElement;
        for (int step = 0; step < 40; step++)
        {
            var p = new Point(rnd.Next(0, 260), rnd.Next(0, 220));
            switch (rnd.Next(5))
            {
                case 0:
                    window.WheelNotches(p, (float)Math.Round(rnd.NextDouble() * 3 - 1.2, 2), modifiers: rnd.Next(4) == 0 ? KeyModifiers.Shift : KeyModifiers.None);
                    break;
                case 1:
                    window.TrackpadScroll(p, (float)Math.Round(rnd.NextDouble() * 50 - 20, 1), (float)Math.Round(rnd.NextDouble() * 20 - 8, 1), ScrollPhase.Changed);
                    break;
                case 2:
                {
                    var id = rnd.Next(3) switch { 0 => "outer", 1 => "inner", _ => (string?)null };
                    var c = id is null ? root.ScrollController : root.ById(id).ScrollController;
                    c.JumpTo(new Point((float)Math.Round(rnd.NextDouble() * 200, 2), (float)Math.Round(rnd.NextDouble() * 400, 2)));
                    break;
                }
                default:
                    clock.Advance(TimeSpan.FromMilliseconds(rnd.Next(1, 200)));
                    break;
            }
            Step(host, clock);
            var before = Snapshot(root);
            var pixels = Pixels(window);
            host.Pipeline.InvalidateAll();
            root.Render!.Layout!.MarkDirty();
            host.RunFrame();
            Assert.True(host.LastFrame.Pipeline.LayoutRan);
            var after = Snapshot(root);
            foreach (var kv in before)
                if (after[kv.Key] != kv.Value) Assert.Fail($"seed {seed} step {step}: {kv.Key} scroll-only {kv.Value} vs full {after[kv.Key]}");
            Assert.True(pixels.AsSpan().SequenceEqual(Pixels(window)), $"seed {seed} step {step}: pixels differ after a full relayout");
        }
    }

    [Fact]
    public void Hit_testing_at_fractional_scale_matches_the_scrolled_bounding_rects()
    {
        var (host, window, _) = Create(new GeoScene(), 260, 220, 1.5, GeoCss, smooth: false);
        var root = host.Root.RootElement;
        root.ById("outer").ScrollController.JumpTo(new Point(13.37f, 41.3f));
        root.ById("inner").ScrollController.JumpTo(new Point(7.7f, 19.1f));
        root.ScrollController.JumpTo(new Point(0, 11.1f));
        host.RunFrame();
        var outer = root.ById("outer");
        int checkedRows = 0;
        foreach (var id in new[] { "r1", "r2", "r3", "r4", "r5", "r6", "r7", "i1", "i2", "i3", "i4" })
        {
            var r = root.ById(id).BoundingRect;
            // 行的可见部分中点（在外层视口之内）
            var vp = host.Pipeline.ScrollViewportRect(id[0] == 'i' ? root.ById("inner") : outer);
            var vis = Rect.FromLTRB(Math.Max(r.Left, vp.Left), Math.Max(r.Top, vp.Top), Math.Min(r.Right, vp.Right), Math.Min(r.Bottom, vp.Bottom));
            var ov = host.Pipeline.ScrollViewportRect(outer);
            vis = Rect.FromLTRB(Math.Max(vis.Left, ov.Left), Math.Max(vis.Top, ov.Top), Math.Min(vis.Right, ov.Right - 13), Math.Min(vis.Bottom, ov.Bottom - 13));
            // 避开视口右侧与底部的覆盖式滚动条（滚动后可见，命中的是滚动容器本身）
            vis = Rect.FromLTRB(vis.Left, vis.Top, Math.Min(vis.Right, vp.Right - 13), Math.Min(vis.Bottom, vp.Bottom - 13));
            if (vis.Width <= 2 || vis.Height <= 2) continue;
            var hit = host.Pipeline.HitTest(new Point(vis.Left + 20, vis.Center.Y));
            Assert.True(hit is not null && (ReferenceEquals(hit, root.ById(id)) || root.ById(id).IsInclusiveAncestorOf(hit)), $"{id} at {vis}: {hit}");
            checkedRows++;
        }
        Assert.True(checkedRows >= 6, checkedRows.ToString());
    }

    [Fact]
    public void Content_shrinking_during_a_smooth_scroll_never_shows_offsets_past_the_end()
    {
        var rows = new State<int>(40);
        var app = new Lambda { Render = () => new ListApp { Rows = rows.Value } };
        var (host, window, clock) = Create(app, smooth: true);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        _ = c.ScrollTo(new Point(0, 1000));
        Step(host, clock, 30);
        rows.Value = 12; // 最大偏移 12·30 − 146 = 214
        for (int i = 0; i < 40; i++)
        {
            Step(host, clock);
            Assert.True(c.Offset.Y <= c.MaxOffset.Y + 0.01f, $"frame {i}: offset {c.Offset.Y} > max {c.MaxOffset.Y}");
        }
    }

    [Fact]
    public void ScrollTo_interrupted_by_wheel_on_the_other_axis_stops_the_programmatic_animation()
    {
        var (host, window, clock) = Create(new ListApp { Rows = 40, ScrollerStyle = "" }, smooth: true,
            css: ScrollSupport.Css + " .row { width: 500px; }");
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        var task = c.ScrollTo(new Point(0, 800));
        Step(host, clock, 50);
        float y = c.Offset.Y;
        Assert.InRange(y, 1, 799);
        window.WheelNotches(new Point(100, 100), 1, modifiers: KeyModifiers.Shift); // 横向滚轮
        Step(host, clock);
        Assert.True(task.IsCompleted);
        Settle(host, clock);
        // 任务已经报告完成：纵向的程序动画不能在它之后继续推进
        Assert.Equal(y, c.Offset.Y, 0.01f);
        Assert.True(c.Offset.X > 0);
    }

    [Fact]
    public void Clamped_fling_starting_at_the_leading_edge_moves_into_the_content()
    {
        var physics = new ClampingPhysics();
        var sim = physics.CreateFling(new ScrollMetrics(0, 0, 1000, 300), 2000);
        Assert.NotNull(sim);
        Assert.False(sim!.IsDone(0));
        Assert.True(sim.Velocity(0) > 0);
        Assert.True(sim.Position(0.1) > 0);
        var end = physics.CreateFling(new ScrollMetrics(1000, 0, 1000, 300), -2000);
        Assert.NotNull(end);
        Assert.False(end!.IsDone(0));
        Assert.True(end.Position(0.1) < 1000);
    }

    /// <summary>强制完整布局与几何遍历，断言每个元素的矩形不变（只平移子树的结果与完整遍历一致）。</summary>
    static void AssertMatchesFullWalk(UIHost host, string what)
    {
        var root = host.Root.RootElement;
        var before = Snapshot(root);
        host.Pipeline.InvalidateAll();
        root.Render!.Layout!.MarkDirty();
        host.RunFrame();
        var after = Snapshot(root);
        foreach (var kv in before)
            if (after[kv.Key] != kv.Value) Assert.Fail($"{what}: {kv.Key} scroll-only {kv.Value} vs full {after[kv.Key]}");
    }

    [Fact]
    public void Replacing_the_controller_with_an_explicitly_positioned_one_applies_its_offset_without_relayout()
    {
        var a = new ScrollController();
        var b = new ScrollController();
        b.JumpTo(new Point(0, 0)); // 显式的位置（附着前设置）：不接过 a 的位置
        var useB = new State<bool>(false);
        var app = new Lambda { Render = () => new ListApp { Rows = 40, Controller = useB.Value ? b : a } };
        var (host, window, _) = Create(app, smooth: false);
        var row0 = host.Root.RootElement.ById("row-0");
        float top = row0.BoundingRect.Y;
        a.JumpTo(new Point(0, 100));
        host.RunFrame();
        Assert.Equal(top - 100, row0.BoundingRect.Y);
        useB.Value = true;
        host.RunFrame();
        Assert.False(host.LastFrame.Pipeline.LayoutRan);
        Assert.Same(b, host.Root.RootElement.ById("scroller").ScrollController);
        Assert.Equal(0, b.Offset.Y);
        Assert.Equal(top, row0.BoundingRect.Y);
        Assert.Equal(40 * 30 - 146, b.MaxOffset.Y, 0.5f);
        // 新控制器立即可以用滚轮滚动（有度量）
        window.WheelNotches(new Point(100, 100), 1);
        host.RunFrame();
        Assert.Equal(100, b.Offset.Y);
        Assert.Equal(top - 100, row0.BoundingRect.Y);
        AssertMatchesFullWalk(host, "after wheel");
    }

    [Fact]
    public void Moving_a_controller_between_two_live_scrollers_keeps_both_geometries_consistent()
    {
        const string css = ScrollSupport.Css + """
            .s { width: 120px; height: 100px; overflow: auto; border: 1px solid #000; }
            .wrap { display: flex; gap: 10px; }
            """;
        var shared = new ScrollController();
        var swap = new State<bool>(false);
        Node List(string id, ScrollController? c)
        {
            var rows = new NodeList();
            for (int i = 0; i < 20; i++) rows.Add(new Div { Key = i, Id = id + i, Class = "row" });
            return new Div { Id = id, Class = "s", ScrollController = c, Children = rows };
        }
        var app = new Lambda
        {
            Render = () => new Div { Class = "wrap", Children = [List("a", swap.Value ? null : shared), List("b", swap.Value ? shared : null)] },
        };
        var (host, window, _) = Create(app, smooth: false, css: css);
        var root = host.Root.RootElement;
        float a0 = root.ById("a0").BoundingRect.Y, b0 = root.ById("b0").BoundingRect.Y;
        root.ById("b").ScrollController.JumpTo(new Point(0, 40));
        shared.JumpTo(new Point(0, 120));
        host.RunFrame();
        Assert.Equal(a0 - 120, root.ById("a0").BoundingRect.Y);
        Assert.Equal(b0 - 40, root.ById("b0").BoundingRect.Y);
        swap.Value = true;
        host.RunFrame();
        Assert.Same(shared, root.ById("b").ScrollController);
        // a 换回自己的控制器（新建，位置 0），b 用 shared 的位置
        Assert.Equal(root.ById("a").ScrollController.Offset.Y, a0 - root.ById("a0").BoundingRect.Y);
        Assert.Equal(shared.Offset.Y, b0 - root.ById("b0").BoundingRect.Y);
        AssertMatchesFullWalk(host, "after swap");
        Assert.True(Pixels(window).AsSpan().SequenceEqual(FullRaster(host)));
    }

    /// <summary>CSS：只有一个轴为 clip、另一个轴为 visible 时两轴都不变成 auto / hidden，元素不是滚动容器（滚轮交给外层）。</summary>
    [Fact]
    public void Overflow_clip_with_visible_on_the_other_axis_is_not_a_scroll_container()
    {
        const string css = ScrollSupport.Css + " .c { height: 100px; overflow-x: clip; } .h { height: 400px; background-color: #ccc; }";
        var app = new Lambda { Render = () => new Div { Children = [new Div { Id = "c", Class = "c", Children = [new Div { Class = "h" }] }, new Div { Class = "h" }] } };
        var (host, window, _) = Create(app, 200, 300, css: css, smooth: false);
        var c = host.Root.RootElement.ById("c");
        Assert.Equal(0, RenderPipeline.ScrollAxes(c));
        window.WheelNotches(new Point(50, 50), 1);
        host.RunFrame();
        Assert.Equal(0, c.ScrollController.Offset.Y);
        Assert.Equal(100, host.Root.RootElement.ScrollController.Offset.Y);
    }

    /// <summary>
    /// 没有时钟（<see cref="UIRoot.Clock"/> 为 null，无窗口的确定性渲染）时滚动系统的时间按帧推进，不读墙上时间：
    /// 同样的输入与帧序列给出同样的覆盖式滚动条，与两帧之间实际过了多久无关（否则差分测试在高负载下偶发失败）。
    /// </summary>
    [Fact]
    public void Without_a_clock_overlay_scrollbar_timing_follows_frames_not_wall_time()
    {
        const string css = ".tall { height: 600px; background-color: #e0f2fe; }";
        PipelineHarness Make()
        {
            var h = new PipelineHarness(new Div { Children = [new Div { Class = "tall" }] }, css, 220, 180);
            Assert.Null(h.Root.Clock);
            h.Frame();
            return h;
        }
        var a = Make();
        var b = Make();
        foreach (var h in new[] { a, b })
        {
            h.Move(217, 50); // 视口右缘：覆盖式滚动条的轨道
            h.Frame();
            Assert.Equal(1, h.Root.RootElement.ScrollController._scrollbarOpacity);
            h.Move(50, 50);
            h.Frame();
        }
        a.Root.RunFrame();
        Thread.Sleep(1600); // 墙上时间超过保持 + 淡出时长：只在 b 的这一帧之前
        b.Root.RunFrame();
        Assert.Equal(a.Root.RootElement.ScrollController._scrollbarOpacity, b.Root.RootElement.ScrollController._scrollbarOpacity);
        Assert.Equal(1, b.Root.RootElement.ScrollController._scrollbarOpacity);
        // 按帧推进：保持 1 s + 淡出 0.3 s 之后（约 80 帧）隐藏
        for (int i = 0; i < 90 && b.Pipeline.NeedsAnimationFrame; i++) b.Root.RunFrame();
        Assert.Equal(0, b.Root.RootElement.ScrollController._scrollbarOpacity);
        Assert.False(b.Pipeline.NeedsAnimationFrame);
    }
}

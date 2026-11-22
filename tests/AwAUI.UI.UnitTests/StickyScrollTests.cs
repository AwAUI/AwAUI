using AwAUI.Headless;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.Render;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// <c>position: sticky</c> 的端到端行为（design/08）：静态空间、滚动偏移下的视觉位置、包含块约束、裁剪与命中测试。
/// 几何在滚动时由 <see cref="RenderPipeline"/> 的纯滚动遍历更新，不重新布局。
/// </summary>
public class StickyScrollTests
{
    const string Css = ScrollSupport.Css + """
        .body { height: 500px; }
        .spacer { height: 100px; }
        .sticky { position: sticky; top: 10px; height: 20px; background-color: #ef4444; }
        .low { top: 140px; }
        .bottom-sticky { position: sticky; bottom: 10px; height: 20px; background-color: #3b82f6; }
        .high-spacer { height: 400px; }
        .tail { height: 300px; }
        """;

    static Node Scroller(params Node[] children) => new Div { Id = "scroller", Class = "scroller", Children = new NodeList(children) };

    static Node App(Node scroller) => new Lambda { Render = () => scroller };

    static (UIHost Host, HeadlessWindow Window, ManualUIClock Clock) Make(string css, Node tree, float w = 320, float h = 240)
        => ScrollSupport.Create(App(tree), w: w, h: h, css: css, smooth: false);

    static void Scroll(UIHost host, float x, float y)
    {
        host.Root.RootElement.ById("scroller").ScrollController.JumpTo(new Point(x, y));
        host.RunFrame();
    }

    static float Top(UIHost host, string id) => host.Root.RootElement.ById(id).BoundingRect.Y;

    // ------------------------------------------------------------ 纵向

    [Fact]
    public void Top_sticky_scrolls_normally_then_sticks_and_leaves_with_the_containing_block()
    {
        var tree = Scroller(
            new Div
            {
                Id = "body", Class = "body",
                Children =
                [
                    new Div { Id = "spacer", Class = "spacer" },
                    new Div { Id = "sticky", Class = "sticky" },
                ],
            },
            new Div { Id = "tail", Class = "tail" });
        var (host, window, _) = Make(Css, tree);

        // 滚动容器边框 2：视口 (22,32) 196×146，粘性行 top:10 → 粘附线 y = 42；静态位置 32+100 = 132
        Assert.Equal(132, Top(host, "sticky"), 0.5f);
        Scroll(host, 0, 50);
        Assert.Equal(82, Top(host, "sticky"), 0.5f);   // 阈值前随内容滚动
        Scroll(host, 0, 90);
        Assert.Equal(42, Top(host, "sticky"), 0.5f);   // 到达 top inset
        // 增量绘制与整帧光栅化一致：粘性盒画在视觉位置，并被滚动容器裁剪
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));
        Scroll(host, 0, 300);
        Assert.Equal(42, Top(host, "sticky"), 0.5f);   // 粘住
        Assert.Equal(ScrollSupport.FullRaster(host), ScrollSupport.Pixels(window));
        // 包含块的末端 532；滚动 500 后它在 32：粘性盒被它的结束边带走（32 − 20 = 12）
        Scroll(host, 0, 500);
        Assert.Equal(12, Top(host, "sticky"), 0.5f);
        Scroll(host, 0, 550);
        Assert.Equal(-38, Top(host, "sticky"), 0.5f);
        Assert.False(host.LastFrame.Pipeline.LayoutRan);
    }

    [Fact]
    public void Bottom_sticky_sticks_at_the_end_inset_and_resumes_normal_flow()
    {
        var tree = Scroller(
            new Div
            {
                Id = "body", Class = "body",
                Children =
                [
                    new Div { Class = "high-spacer" },
                    new Div { Id = "sticky", Class = "bottom-sticky" },
                ],
            },
            new Div { Class = "tail" });
        var (host, _, _) = Make(Css, tree);

        // 静态 y = 32 + 400 = 432；视口底 178，bottom:10 → 粘附线 178 − 10 − 20 = 148
        Assert.Equal(148, Top(host, "sticky"), 0.5f);
        Scroll(host, 0, 200);
        Assert.Equal(148, Top(host, "sticky"), 0.5f);
        Scroll(host, 0, 284);
        Assert.Equal(148, Top(host, "sticky"), 0.5f);
        Scroll(host, 0, 350);
        Assert.Equal(82, Top(host, "sticky"), 0.5f);   // 阈值后随内容滚出
    }

    // ------------------------------------------------------------ 纵向 + 横向 inset

    [Fact]
    public void Simultaneous_top_and_left_insets_offset_both_axes()
    {
        const string css = ScrollSupport.Css + """
            .hbody { height: 500px; }
            .hspace { height: 100px; }
            .hrow { position: sticky; top: 10px; left: 10px; width: 40px; height: 20px; background-color: #22c55e; }
            """;
        var tree = Scroller(new Div
        {
            Id = "body", Class = "hbody",
            Children =
            [
                new Div { Class = "hspace" },
                new Div { Id = "sticky", Class = "hrow" },
            ],
        }, new Div { Class = "wide" });
        var (host, _, _) = Make(css, tree);

        // 静态 (22, 132)；视口 (22,32,196,146)。left inset 使盒一开始就内缩到 x = 32（内容左缘与视口重合），
        // 纵向仍随内容滚动，直到 top inset 的粘附线 y = 42
        var r = host.Root.RootElement.ById("sticky").BoundingRect;
        Assert.Equal(32, r.X, 0.5f);
        Assert.Equal(132, r.Y, 0.5f);
        Scroll(host, 0, 90);
        r = host.Root.RootElement.ById("sticky").BoundingRect;
        Assert.Equal(32, r.X, 0.5f);
        Assert.Equal(42, r.Y, 0.5f);
        Scroll(host, 90, 0);
        r = host.Root.RootElement.ById("sticky").BoundingRect;
        Assert.Equal(32, r.X, 0.5f);
        Assert.Equal(132, r.Y, 0.5f);
    }

    [Fact]
    public void Left_sticky_honours_the_scrollport_and_containing_block_end_edge()
    {
        const string css = ScrollSupport.Css + """
            .hbody { display: flex; width: 600px; height: 150px; }
            .hspacer { width: 100px; height: 20px; flex: none; }
            .hl { position: sticky; left: 10px; width: 20px; height: 20px; flex: none; background-color: #22c55e; }
            .wide { width: 800px; height: 10px; }
            """;
        var tree = Scroller(
            new Div
            {
                Id = "body", Class = "hbody",
                Children =
                [
                    new Div { Class = "hspacer" },
                    new Div { Id = "sticky", Class = "hl" },
                ],
            },
            new Div { Class = "wide" });
        var (host, _, _) = Make(css, tree);
        Assert.Equal(122, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
        Scroll(host, 50, 0);
        Assert.Equal(72, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
        Scroll(host, 90, 0);
        Assert.Equal(32, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
        Scroll(host, 200, 0);
        Assert.Equal(32, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
        // 包含块右端 622：滚动 590 后它在 32，粘性盒随之移出（32 − 20 = 12）
        Scroll(host, 590, 0);
        Assert.Equal(12, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
    }

    [Fact]
    public void Right_sticky_resolves_against_the_right_scrollport_edge()
    {
        const string css = ScrollSupport.Css + """
            .hbody { display: flex; width: 600px; height: 150px; }
            .hspacer { width: 400px; height: 20px; flex: none; }
            .hr { position: sticky; right: 10px; width: 20px; height: 20px; flex: none; background-color: #3b82f6; }
            .wide { width: 800px; height: 10px; }
            """;
        var tree = Scroller(
            new Div
            {
                Id = "body", Class = "hbody",
                Children =
                [
                    new Div { Class = "hspacer" },
                    new Div { Id = "sticky", Class = "hr" },
                ],
            },
            new Div { Class = "wide" });
        var (host, _, _) = Make(css, tree);
        // 静态 x = 422；视口右 218，right:10 → 粘附线 218 − 10 − 20 = 188
        Assert.Equal(188, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
        Scroll(host, 234, 0);
        Assert.Equal(188, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
        Scroll(host, 300, 0);
        Assert.Equal(122, host.Root.RootElement.ById("sticky").BoundingRect.X, 0.5f);
    }

    // ------------------------------------------------------------ 命中测试 / 裁剪 / 回归

    [Fact]
    public void Hit_testing_follows_the_visual_sticky_position()
    {
        var tree = Scroller(
            new Div
            {
                Id = "body", Class = "body",
                Children =
                [
                    new Div { Id = "spacer", Class = "spacer" },
                    new Div { Id = "sticky", Class = "sticky" },
                ],
            },
            new Div { Class = "tail" });
        var (host, _, _) = Make(Css, tree);
        // 静止时 y=50 仍属于 spacer
        Assert.Equal("spacer", host.Pipeline.HitTest(new Point(50, 50))?.Id);
        Scroll(host, 0, 90);
        // 粘住后视觉位置在 42..62：命中粘性元素，而不是它静态时所在的区域
        Assert.Equal("sticky", host.Pipeline.HitTest(new Point(50, 50))?.Id);
        Assert.NotEqual("sticky", host.Pipeline.HitTest(new Point(50, 140))?.Id);
    }

    [Fact]
    public void The_scrollport_clips_the_sticky_element_for_hit_testing()
    {
        const string css = ScrollSupport.Css + """
            .body { height: 500px; }
            .spacer { height: 100px; }
            .sticky { position: sticky; top: 140px; height: 20px; background-color: #ef4444; }
            """;
        var tree = Scroller(new Div
        {
            Id = "body", Class = "body",
            Children =
            [
                new Div { Class = "spacer" },
                new Div { Id = "sticky", Class = "sticky" },
            ],
        });
        var (host, _, _) = Make(css, tree);
        // 静态 132 → 粘附线 32+140 = 172，盒 172..192；视口底 178
        Assert.Equal(172, Top(host, "sticky"), 0.5f);
        Assert.Equal("sticky", host.Pipeline.HitTest(new Point(50, 175))?.Id);
        // 视口外的部分被裁剪，不能命中
        Assert.NotEqual("sticky", host.Pipeline.HitTest(new Point(50, 185))?.Id);
    }

    [Fact]
    public void Sticky_without_insets_and_static_siblings_are_unaffected()
    {
        const string css = ScrollSupport.Css + """
            .body { height: 500px; }
            .no-inset { position: sticky; height: 20px; }
            .plain { height: 20px; }
            """;
        var tree = Scroller(new Div
        {
            Id = "body", Class = "body",
            Children =
            [
                new Div { Id = "plain", Class = "plain" },
                new Div { Id = "no-inset", Class = "no-inset" },
            ],
        });
        var (host, _, _) = Make(css, tree);
        Assert.Equal(Position.Sticky, RenderBox.Get(host.Root.RootElement.ById("no-inset")).Layout!.Style.Position);
        Assert.Equal(32, Top(host, "plain"), 0.5f);
        Assert.Equal(52, Top(host, "no-inset"), 0.5f);
        Scroll(host, 0, 100);
        // 没有 inset 的 sticky 与 static 一样随内容滚动
        Assert.Equal(-68, Top(host, "plain"), 0.5f);
        Assert.Equal(-48, Top(host, "no-inset"), 0.5f);
    }

    [Fact]
    public void Nested_scroll_containers_resolve_against_the_nearest_scrollport()
    {
        const string css = ScrollSupport.Css + """
            .outer-body { height: 600px; }
            .inner { height: 80px; overflow: auto; margin: 0; }
            .inner-body { height: 300px; }
            .inner-space { height: 100px; }
            .inner-sticky { position: sticky; top: 5px; height: 20px; background-color: #a855f7; }
            """;
        var tree = Scroller(new Div
        {
            Id = "outer-body", Class = "outer-body",
            Children =
            [
                new Div
                {
                    Id = "inner", Class = "inner",
                    Children =
                    [
                        new Div
                        {
                            Id = "inner-body", Class = "inner-body",
                            Children =
                            [
                                new Div { Class = "inner-space" },
                                new Div { Id = "sticky", Class = "inner-sticky" },
                            ],
                        },
                    ],
                },
            ],
        });
        var (host, _, _) = Make(css, tree);
        var inner = host.Root.RootElement.ById("inner");
        host.Root.RootElement.ById("inner").ScrollController.JumpTo(new Point(0, 120));
        host.RunFrame();
        // 内层视口顶部 = inner 边框盒顶部；top:5 相对它，而不受外层滚动影响
        Assert.Equal(inner.BoundingRect.Y + 5, Top(host, "sticky"), 0.5f);
        Scroll(host, 0, 40);
        Assert.Equal(inner.BoundingRect.Y + 5, Top(host, "sticky"), 0.5f);
    }
}

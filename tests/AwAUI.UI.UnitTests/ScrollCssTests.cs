using AwAUI.Graphics;
using AwAUI.Platform;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 滚动相关 CSS 属性的接入（第九批）：<c>overscroll-behavior</c>、<c>scrollbar-width</c>、<c>scrollbar-color</c>、
/// <c>scroll-behavior</c>（程序滚动的默认动画）、<c>scroll-margin</c> / <c>scroll-padding</c>（滚动到可见）。控制器上显式设置的属性优先。
/// </summary>
public class ScrollCssTests
{
    [Fact]
    public void Scroll_behavior_auto_makes_programmatic_scrolls_instant()
    {
        var ctl = new ScrollController();
        var (host, _, clock) = Create(new ListApp { Controller = ctl, ScrollerStyle = "scroll-behavior: auto" });
        var task = ctl.ScrollTo(new Point(0, 300));
        Assert.True(task.IsCompleted);
        host.RunFrame();
        Assert.Equal(300, ctl.Offset.Y);
        // 显式给出的动画优先于 CSS
        var t2 = ctl.ScrollTo(new Point(0, 0), ScrollAnimation.Default);
        Assert.False(t2.IsCompleted);
        Settle(host, clock);
        Assert.Equal(0, ctl.Offset.Y);

        // 默认样式表：smooth
        var ctl2 = new ScrollController();
        var (host2, _, _) = Create(new ListApp { Controller = ctl2 });
        Assert.False(ctl2.ScrollTo(new Point(0, 300)).IsCompleted);
        host2.RunFrame();
    }

    [Fact]
    public void Scroll_margin_and_padding_adjust_scroll_into_view()
    {
        var ctl = new ScrollController();
        var (host, _, clock) = Create(new ListApp { Controller = ctl, ScrollerStyle = "scroll-behavior: auto; scroll-padding-top: 20px" },
            css: ScrollSupport.Css + " #row-10 { scroll-margin-top: 5px }");
        var root = host.Root.RootElement;
        var vp = root.ById("scroller").BoundingRect;
        ctl.ScrollIntoView(root.ById("row-10"), ScrollAlignment.Start);
        Settle(host, clock);
        // 视口上边（内边距盒，边框 2px）+ scroll-padding 20 + scroll-margin 5
        Assert.Equal(vp.Top + 2 + 20 + 5, root.ById("row-10").BoundingRect.Top, 0.5f);
    }

    [Fact]
    public void Scrollbar_width_and_color_come_from_css_unless_controller_sets_them()
    {
        var ctl = new ScrollController();
        var (host, _, _) = Create(new ListApp { Controller = ctl, ScrollerStyle = "scrollbar-width: none" });
        host.Pipeline.ScrollbarStyle = ScrollbarStyle.Classic;
        host.RunFrame();
        var scroller = host.Root.RootElement.ById("scroller");
        Assert.Equal(default, host.Pipeline.ScrollbarGeometryFor(scroller));
        Assert.Equal(ScrollbarWidth.None, ScrollCss.Width(ctl));
        ctl.ScrollbarWidth = ScrollbarWidth.Thin;
        Assert.Equal(ScrollbarWidth.Thin, ScrollCss.Width(ctl));

        var ctl2 = new ScrollController();
        var (host2, _, _) = Create(new ListApp { Controller = ctl2, ScrollerStyle = "scrollbar-color: #ff0000 #00ff00" });
        Assert.Equal(Color.Parse("#ff0000"), ScrollCss.ThumbColor(ctl2));
        Assert.Equal(Color.Parse("#00ff00"), ScrollCss.TrackColor(ctl2));
        ctl2.ScrollbarThumbColor = Color.Parse("#0000ff");
        Assert.Equal(Color.Parse("#0000ff"), ScrollCss.ThumbColor(ctl2));
        // 经典滚动条按 CSS 颜色绘制：轨道是绿色
        host2.Pipeline.ScrollbarStyle = ScrollbarStyle.Classic;
        host2.RunFrame();
        var g = host2.Pipeline.ScrollbarGeometryFor(host2.Root.RootElement.ById("scroller"));
        Assert.True(g.TrackY.Height > 0);
        var px = ((AwAUI.Headless.HeadlessWindow)host2.Window).Framebuffer!.GetPixel((int)g.TrackY.Center.X, (int)g.TrackY.Bottom - 2);
        Assert.True(px.G > 0.9f && px.R < 0.1f, px.ToString());
    }

    [Fact]
    public void Overscroll_behavior_contain_from_css_stops_scroll_chaining()
    {
        var outer = new ScrollController();
        var app = new ListApp { Controller = outer, Nested = true };
        var (host, window, _) = Create(app, css: ScrollSupport.Css + " .inner { overscroll-behavior: contain }", smooth: false);
        var inner = host.Root.RootElement.ById("inner").ScrollController;
        Assert.Equal(OverscrollBehavior.Contain, ScrollCss.Overscroll(inner, 1));
        Assert.Equal(OverscrollBehavior.Auto, ScrollCss.Overscroll(outer, 1));
        var p = new Point(inner.Element!.BoundingRect.Center.X, inner.Element.BoundingRect.Center.Y);
        // 内层滚到底之后继续滚：被截断，外层不动
        for (int i = 0; i < 5; i++)
        {
            window.WheelNotches(p, 1);
            host.RunFrame();
        }
        Assert.Equal(inner.MaxOffset.Y, inner.Offset.Y);
        Assert.Equal(0, outer.Offset.Y);
    }
}

/// <summary>经典滚动条占布局空间（第九批）：overflow: scroll / scrollbar-gutter: stable 总是预留，overflow: auto 溢出时预留（同一帧内再布局）。</summary>
public class ScrollbarGutterTests
{
    static PipelineHarness Harness(string scrollerStyle, int rows, bool classic = true)
    {
        var children = new NodeList();
        for (int i = 0; i < rows; i++) children.Add(new Div { Id = "r" + i, Style = "height: 20px" });
        var h = new PipelineHarness(new Div { Id = "s", Style = "width: 100px; height: 100px; border: 0; padding: 0; " + scrollerStyle, Children = children },
            deferMount: true);
        if (classic) h.Pipeline.ScrollbarStyle = ScrollbarStyle.Classic;
        h.MountNow();
        h.Frame();
        return h;
    }

    [Fact]
    public void Overflow_scroll_and_stable_gutter_reserve_space_for_classic_scrollbars()
    {
        Assert.Equal(88, Harness("overflow-y: scroll", 1).Box("r0").Width);
        Assert.Equal(88, Harness("overflow-y: hidden; scrollbar-gutter: stable", 1).Box("r0").Width);
        var both = Harness("overflow-y: auto; scrollbar-gutter: stable both-edges", 1);
        Assert.Equal(new Rect(12, 0, 76, 20), both.Box("r0"));
        Assert.Equal(92, Harness("overflow-y: scroll; scrollbar-width: thin", 1).Box("r0").Width);
        Assert.Equal(100, Harness("overflow-y: scroll; scrollbar-width: none", 1).Box("r0").Width);
        // 覆盖式滚动条不占空间
        Assert.Equal(100, Harness("overflow-y: scroll", 1, classic: false).Box("r0").Width);
    }

    [Fact]
    public void Overflow_auto_reserves_space_only_when_content_overflows_and_follows_style_switches()
    {
        Assert.Equal(100, Harness("overflow-y: auto", 2).Box("r0").Width);
        var h = Harness("overflow-y: auto", 10);
        // 同一帧内：溢出 → 预留 12px → 再布局
        Assert.Equal(88, h.Box("r0").Width);
        var g = h.Pipeline.ScrollbarGeometryFor(h.El("s"));
        Assert.Equal(new Rect(88, 0, 12, 100), g.TrackY);
        // 切换到覆盖式：不再占空间
        h.Pipeline.ScrollbarStyle = ScrollbarStyle.Overlay;
        h.Frame();
        Assert.Equal(100, h.Box("r0").Width);
        h.Pipeline.ScrollbarStyle = ScrollbarStyle.Classic;
        h.Frame();
        Assert.Equal(88, h.Box("r0").Width);
    }
}

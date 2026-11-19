using AwAUI.Platform;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>第八批集成：fixed 不计入视口的可滚动溢出、触控板手势的超时兜底、UIRoot.NeedsFrame 包含滚动动画。</summary>
public class ScrollIntegrationTests
{
    [Fact]
    public void Fixed_element_overflowing_the_window_does_not_make_the_root_viewport_scrollable()
    {
        // 300 px 高的窗口里 top:250px; height:100px 的 fixed 元素：fixed 不随视口滚动，不计入根的可滚动溢出（浏览器同样如此）
        const string css = ":root { background-color: #fff; } .fix { position: fixed; top: 250px; left: 0; width: 30px; height: 100px; background-color: red; }";
        var app = new Lambda { Render = () => new Div { Children = [new Div { Style = "height: 100px" }, new Div { Id = "fix", Class = "fix" }] } };
        var (host, window, _) = Create(app, 200, 300, css: css, smooth: false);
        var rootCtl = host.Root.RootElement.ScrollController;
        Assert.Equal(new Size(200, 300), rootCtl.ViewportSize);
        Assert.True(rootCtl.ContentSize.Height <= 300, rootCtl.ContentSize.ToString());
        Assert.Equal(0, rootCtl.MaxOffset.Y);
        window.WheelNotches(new Point(100, 100), 2);
        host.RunFrame();
        Assert.Equal(0, rootCtl.Offset.Y);
        Assert.Equal(250, host.Root.RootElement.ById("fix").BoundingRect.Y);
    }

    [Fact]
    public void Absolute_element_overflowing_the_window_still_makes_the_root_viewport_scrollable()
    {
        const string css = ":root { background-color: #fff; } .abs { position: absolute; top: 250px; left: 0; width: 30px; height: 100px; background-color: red; }";
        var app = new Lambda { Render = () => new Div { Children = [new Div { Style = "height: 100px" }, new Div { Class = "abs" }] } };
        var (host, _, _) = Create(app, 200, 300, css: css, smooth: false);
        Assert.Equal(50, host.Root.RootElement.ScrollController.MaxOffset.Y);
    }

    [Fact]
    public void Trackpad_gesture_without_an_end_phase_times_out()
    {
        // 窗口在手势中途失去焦点、平台丢了 Ended / Cancelled：最后一个事件之后 GestureTimeoutSeconds 仍没有新事件就结束手势
        var app = new ListApp();
        var (host, window, clock) = Create(app);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        int ends = 0, requests = 0;
        c.ScrollEnd += () => ends++;
        host.Root.FrameRequested += _ => requests++;
        var over = new Point(100, 100);
        window.TrackpadScroll(over, 20, 0, ScrollPhase.Began);
        Step(host, clock);
        for (int i = 0; i < 10; i++)
        {
            // 事件间隔（50 ms）小于超时：手势一直进行
            window.TrackpadScroll(over, 5, 0, ScrollPhase.Changed);
            Step(host, clock, 50);
            Assert.True(c.IsScrolling);
        }
        Assert.Equal(70, c.Offset.Y);
        Assert.Equal(0, ends);
        // 手势进行中不为超时逐帧出帧
        Assert.False(host.Pipeline.NeedsAnimationFrame);

        // 之后再也没有事件：超时前仍在滚动
        clock.Advance(TimeSpan.FromMilliseconds(ScrollManager.GestureTimeoutSeconds * 1000 - 100));
        host.RunFrame();
        Assert.True(c.IsScrolling);
        // 超时：时钟定时器唤醒宿主出一帧，手势结束、ScrollEnd 触发
        requests = 0;
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(requests > 0);
        host.RunFrame();
        Assert.False(c.IsScrolling);
        Assert.Equal(1, ends);
        Assert.Equal(70, c.Offset.Y);
        Settle(host, clock);
        Assert.Equal(1, ends);

        // 锁定仍在：迟到的 Changed 事件继续滚动同一个容器，开始新的一段「滚动中」，同样会超时结束
        window.TrackpadScroll(over, 10, 0, ScrollPhase.Changed);
        host.RunFrame();
        Assert.True(c.IsScrolling);
        Assert.Equal(80, c.Offset.Y);
        clock.Advance(TimeSpan.FromMilliseconds(ScrollManager.GestureTimeoutSeconds * 1000 + 50));
        host.RunFrame();
        Assert.False(c.IsScrolling);
        Assert.Equal(2, ends);
    }

    [Fact]
    public void Ended_gesture_is_not_affected_by_the_timeout()
    {
        var app = new ListApp();
        var (host, window, clock) = Create(app);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        int ends = 0;
        c.ScrollEnd += () => ends++;
        var over = new Point(100, 100);
        window.TrackpadScroll(over, 20, 0, ScrollPhase.Began);
        Step(host, clock);
        window.TrackpadScroll(over, 0, 0, ScrollPhase.Ended);
        Settle(host, clock);
        Assert.False(c.IsScrolling);
        Assert.Equal(1, ends);
        clock.Advance(TimeSpan.FromSeconds(1));
        Settle(host, clock);
        Assert.Equal(1, ends);
    }

    [Fact]
    public void Ui_root_needs_frames_while_a_scroll_animation_runs_and_then_stops_requesting_them()
    {
        // UIHost 以外的宿主（这里没有时钟、只靠 FrameRequested / NeedsFrame 驱动）也能把平滑滚动推进到底，且不会无限出帧
        var h = new PipelineHarness(new Div { Children = [new Div { Style = "height: 2000px" }] }, width: 200, height: 300);
        h.Frame();
        var ctl = h.Root.RootElement.ScrollController;
        Assert.False(h.Root.NeedsFrame);
        var task = ctl.ScrollTo(new Point(0, 500));
        Assert.True(h.Root.NeedsFrame);
        int frames = 0, requests = h.FrameRequests;
        while (h.Root.NeedsFrame && frames < 1000)
        {
            h.Root.RunFrame();
            frames++;
        }
        Assert.True(frames < 1000, "frames never stopped");
        Assert.True(frames > 1, frames.ToString());
        Assert.True(h.FrameRequests > requests);
        Assert.True(task.IsCompleted);
        Assert.Equal(500, ctl.Offset.Y, 0.5f);
        Assert.False(h.Pipeline.NeedsAnimationFrame);
        Assert.False(h.Root.NeedsFrame);
    }
}

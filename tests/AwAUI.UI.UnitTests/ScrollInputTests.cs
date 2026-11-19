using AwAUI.Headless;
using AwAUI.Platform;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>滚轮、触控板、键盘、焦点、滚动条与触摸拖动的端到端行为（design/21「滚轮、触控板、键盘」「嵌套滚动」）。</summary>
public class ScrollInputTests
{
    static readonly Point Inside = new(100, 100);

    [Fact]
    public void Wheel_animates_smoothly_accumulates_and_reports_scroll_once_per_frame_then_scroll_end()
    {
        var events = new List<Point>();
        var app = new ListApp { OnScrolled = e => events.Add(e.Offset) };
        var (host, window, clock) = Create(app);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        int ends = 0;
        c.ScrollEnd += () => ends++;
        window.WheelNotches(Inside, 1);
        Assert.True(c.IsScrolling);
        Step(host, clock);
        float first = c.Offset.Y;
        Assert.InRange(first, 1, 99);
        // 动画中的第二格叠加到目标上：共 200
        window.WheelNotches(Inside, 1);
        Settle(host, clock);
        Assert.Equal(200, c.Offset.Y, 3);
        Assert.False(c.IsScrolling);
        Assert.Equal(1, ends);
        Assert.Equal(200, events[events.Count - 1].Y, 3);
        // 每帧最多一次
        Assert.True(events.Count <= 20, events.Count.ToString());
        for (int i = 1; i < events.Count; i++) Assert.True(events[i].Y > events[i - 1].Y);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Wheel_chains_from_the_inner_scroller_to_the_outer_one_unless_overscroll_behavior_contains_it()
    {
        var app = new ListApp { Nested = true };
        var (host, window, clock) = Create(app, smooth: false);
        var root = host.Root.RootElement;
        var outer = root.ById("scroller").ScrollController;
        var inner = root.ById("inner").ScrollController;
        var over = root.ById("inner").BoundingRect.Center;
        Assert.Equal(250 - 80, inner.MaxOffset.Y, 0.5f);
        window.WheelNotches(over, 1);
        host.RunFrame();
        Assert.Equal(100, inner.Offset.Y);
        Assert.Equal(0, outer.Offset.Y);
        window.WheelNotches(over, 1);
        host.RunFrame();
        Assert.Equal(170, inner.Offset.Y);
        Assert.Equal(0, outer.Offset.Y);
        // 内层滚到头：交给外层
        window.WheelNotches(over, 1);
        host.RunFrame();
        Assert.Equal(170, inner.Offset.Y);
        Assert.Equal(100, outer.Offset.Y);
        // 向上滚：指针下现在是普通行，外层先滚
        window.WheelNotches(new Point(100, 60), -1);
        host.RunFrame();
        Assert.Equal(0, outer.Offset.Y);
        Assert.Equal(170, inner.Offset.Y);

        // overscroll-behavior: contain —— 内层滚到头也不传给外层
        inner.OverscrollBehaviorY = OverscrollBehavior.Contain;
        over = root.ById("inner").BoundingRect.Center;
        window.WheelNotches(over, 1);
        host.RunFrame();
        Assert.Equal(0, outer.Offset.Y);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Shift_wheel_scrolls_horizontally_and_horizontal_wheel_does_not_scroll_a_vertical_only_scroller()
    {
        const string css = ScrollSupport.Css + ".row { width: 600px; }";
        var (host, window, _) = Create(new ListApp(), css: css, smooth: false);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        Assert.Equal(600 - 196, c.MaxOffset.X, 0.5f);
        window.WheelNotches(Inside, 1, modifiers: KeyModifiers.Shift);
        host.RunFrame();
        Assert.Equal(new Point(100, 0), c.Offset);
        window.WheelNotches(Inside, 0, right: -1);
        host.RunFrame();
        Assert.Equal(new Point(0, 0), c.Offset);

        var (host2, window2, _) = Create(new ListApp(), smooth: false);
        var c2 = host2.Root.RootElement.ById("scroller").ScrollController;
        window2.WheelNotches(Inside, 0, right: 1);
        host2.RunFrame();
        Assert.Equal(new Point(0, 0), c2.Offset);
    }

    [Fact]
    public void Trackpad_gesture_follows_deltas_directly_stays_latched_and_ends_after_momentum()
    {
        var app = new ListApp { Nested = true };
        var (host, window, clock) = Create(app);
        var root = host.Root.RootElement;
        var outer = root.ById("scroller").ScrollController;
        var inner = root.ById("inner").ScrollController;
        var over = root.ById("inner").BoundingRect.Center;
        int ends = 0;
        inner.ScrollEnd += () => ends++;
        window.TrackpadScroll(over, 0, 0, ScrollPhase.MayBegin);
        window.TrackpadScroll(over, 30, 0, ScrollPhase.Began);
        host.RunFrame();
        // 直接跟随：没有平滑
        Assert.Equal(30, inner.Offset.Y);
        Assert.True(inner.IsScrolling);
        for (int i = 0; i < 10; i++)
        {
            window.TrackpadScroll(over, 30, 0, ScrollPhase.Changed);
            Step(host, clock, 8);
        }
        // 锁定在内层：滚到头之后的增量不传给外层
        Assert.Equal(170, inner.Offset.Y);
        Assert.Equal(0, outer.Offset.Y);
        window.TrackpadScroll(over, 0, 0, ScrollPhase.Ended);
        Step(host, clock, 8);
        window.TrackpadScroll(over, -10, 0, ScrollPhase.None, ScrollPhase.Began);
        Step(host, clock, 8);
        Assert.Equal(160, inner.Offset.Y);
        window.TrackpadScroll(over, -5, 0, ScrollPhase.None, ScrollPhase.Changed);
        Step(host, clock, 8);
        Assert.Equal(155, inner.Offset.Y);
        Assert.Equal(0, ends);
        window.TrackpadScroll(over, 0, 0, ScrollPhase.None, ScrollPhase.Ended);
        Settle(host, clock);
        Assert.False(inner.IsScrolling);
        Assert.Equal(1, ends);
        Assert.Equal(155, inner.Offset.Y);
        // 惯性结束后解除锁定：新的手势从指针处重新选择
        window.TrackpadScroll(new Point(100, 170), 40, 0, ScrollPhase.Began);
        host.RunFrame();
        Assert.Equal(40, outer.Offset.Y);
    }

    [Fact]
    public void Wheel_event_reaches_elements_and_prevent_default_cancels_scrolling()
    {
        var seen = new List<float>();
        bool prevent = true;
        var app = new ListApp { OnScrollerWheel = e => { seen.Add(e.DeltaY); if (prevent) e.PreventDefault(); } };
        var (host, window, _) = Create(app, smooth: false);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        window.WheelNotches(Inside, 1);
        host.RunFrame();
        Assert.Equal(new[] { 100f }, seen); // DOM 约定：正值向下
        Assert.Equal(0, c.Offset.Y);
        prevent = false;
        window.WheelNotches(Inside, 1);
        host.RunFrame();
        Assert.Equal(100, c.Offset.Y);
    }

    [Fact]
    public void Keyboard_scrolls_the_focused_elements_scroller_when_the_key_is_not_consumed()
    {
        var app = new ListApp();
        var (host, window, clock) = Create(app, smooth: false);
        var root = host.Root.RootElement;
        var c = root.ById("scroller").ScrollController;
        // 点击一行（指针聚焦不滚动）
        var row1 = root.ById("row-1").BoundingRect.Center;
        Pointer(window, RawPointerEventType.Down, row1, changed: PointerButtons.Left, buttons: PointerButtons.Left);
        Pointer(window, RawPointerEventType.Up, row1, changed: PointerButtons.Left);
        host.RunFrame();
        Assert.Equal("row-1", host.Root.FocusManager.FocusedElement?.Id);
        Assert.Equal(0, c.Offset.Y);
        window.TypeKey(PhysicalKey.ArrowDown);
        host.RunFrame();
        Assert.Equal(40, c.Offset.Y);
        window.TypeKey(PhysicalKey.PageDown);
        host.RunFrame();
        Assert.Equal(40 + 146 * 0.875f, c.Offset.Y, 2);
        window.TypeKey(PhysicalKey.End);
        host.RunFrame();
        Assert.Equal(c.MaxOffset.Y, c.Offset.Y);
        window.TypeKey(PhysicalKey.Space, KeyModifiers.Shift);
        host.RunFrame();
        Assert.Equal(c.MaxOffset.Y - 146 * 0.875f, c.Offset.Y, 2);
        window.TypeKey(PhysicalKey.Home);
        host.RunFrame();
        Assert.Equal(0, c.Offset.Y);
        window.TypeKey(PhysicalKey.Space);
        host.RunFrame();
        Assert.Equal(146 * 0.875f, c.Offset.Y, 2);
    }

    [Fact]
    public void Keydown_handler_preventing_default_and_inputs_keep_their_keys()
    {
        bool prevent = false;
        var app = new Lambda
        {
            Render = () => new Div
            {
                Id = "scroller", Class = "scroller",
                OnKeyDown = e => { if (prevent) e.PreventDefault(); },
                Children = [new Input { Id = "field" }, new Div { Id = "row-0", Class = "row", TabIndex = 0 }, .. Enumerable.Range(1, 20).Select(i => (Node)new Div { Class = "row" })],
            },
        };
        var (host, window, _) = Create(app, smooth: false);
        var root = host.Root.RootElement;
        var c = root.ById("scroller").ScrollController;
        root.ById("field").Focus();
        host.RunFrame();
        window.TypeKey(PhysicalKey.End);
        window.TypeKey(PhysicalKey.Space);
        host.RunFrame();
        Assert.Equal(0, c.Offset.Y);
        root.ById("row-0").Focus();
        prevent = true;
        window.TypeKey(PhysicalKey.ArrowDown);
        host.RunFrame();
        Assert.Equal(0, c.Offset.Y);
        prevent = false;
        window.TypeKey(PhysicalKey.ArrowDown);
        host.RunFrame();
        Assert.Equal(40, c.Offset.Y);
    }

    [Fact]
    public void Focus_navigation_scrolls_the_focused_element_into_view()
    {
        var app = new ListApp { Rows = 30 };
        var (host, window, clock) = Create(app, smooth: false);
        var root = host.Root.RootElement;
        var c = root.ById("scroller").ScrollController;
        var vp = root.ById("scroller").BoundingRect;
        for (int i = 0; i < 8; i++) window.TypeKey(PhysicalKey.Tab);
        host.RunFrame();
        Assert.Equal("row-7", host.Root.FocusManager.FocusedElement?.Id);
        var r = root.ById("row-7").BoundingRect;
        // 最小滚动量：行的下边缘贴着视口下边缘（内边距盒 = 边框盒内缩 2）
        Assert.Equal(vp.Bottom - 2, r.Bottom, 0.5f);
        Assert.Equal(8 * 30 - 146, c.Offset.Y, 0.5f);
        // 程序聚焦一个上方的行：滚回去，行的上边缘贴着视口上边缘
        root.ById("row-1").Focus();
        host.RunFrame();
        Assert.Equal(vp.Top + 2, root.ById("row-1").BoundingRect.Top, 0.5f);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void ScrollTo_animates_and_completes_its_task_and_jump_interrupts_it()
    {
        var ctl = new ScrollController();
        var app = new ListApp { Controller = ctl };
        var (host, window, clock) = Create(app);
        Assert.Same(ctl, host.Root.RootElement.ById("scroller").ScrollController);
        var task = ctl.ScrollTo(new Point(0, 600));
        Assert.False(task.IsCompleted);
        Step(host, clock, 150);
        Assert.InRange(ctl.Offset.Y, 100, 500); // 缓入缓出的中点附近
        Settle(host, clock);
        Assert.True(task.IsCompleted);
        Assert.Equal(600, ctl.Offset.Y);
        // 超出范围的目标被钳制
        var t2 = ctl.ScrollTo(new Point(0, 99999), new ScrollAnimation { Duration = TimeSpan.FromMilliseconds(100), Easing = ScrollEasing.Linear });
        Step(host, clock, 50);
        Assert.Equal(600 + (ctl.MaxOffset.Y - 600) / 2, ctl.Offset.Y, 1);
        ctl.JumpTo(new Point(0, 10));
        Assert.True(t2.IsCompleted);
        host.RunFrame();
        Assert.Equal(10, ctl.Offset.Y);
        Assert.Equal(32 - 10, host.Root.RootElement.ById("row-0").BoundingRect.Y, 0.5f);
        Assert.True(ctl.ScrollTo(new Point(0, 10)).IsCompleted);
    }

    [Fact]
    public void ScrollIntoView_aligns_the_element_after_layout()
    {
        var ctl = new ScrollController();
        var (host, window, clock) = Create(new ListApp { Controller = ctl });
        var root = host.Root.RootElement;
        var vp = root.ById("scroller").BoundingRect;
        var t = ctl.ScrollIntoView(root.ById("row-20"));
        int frames = Settle(host, clock);
        Assert.True(t.IsCompleted, $"{t.Status} {ctl} frames {frames}");
        Assert.Equal(vp.Bottom - 2, root.ById("row-20").BoundingRect.Bottom, 0.5f);
        ctl.ScrollIntoView(root.ById("row-10"), ScrollAlignment.Start);
        Settle(host, clock);
        Assert.Equal(vp.Top + 2, root.ById("row-10").BoundingRect.Top, 0.5f);
        ctl.ScrollIntoView(root.ById("row-10"), ScrollAlignment.Center);
        Settle(host, clock);
        Assert.Equal(vp.Center.Y, root.ById("row-10").BoundingRect.Center.Y, 0.5f);
        // 不是后代：立即完成
        Assert.True(ctl.ScrollIntoView(root).IsCompleted);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Classic_scrollbar_thumb_drag_and_track_click_scroll_the_content()
    {
        var platform = new HeadlessPlatform { DefaultScrollbarStyle = ScrollbarStyle.Classic };
        var host = CreateHost(platform, new ListApp());
        host.Root.Clock = new ManualUIClock();
        host.Pipeline.SmoothScrolling = false;
        Assert.Equal(ScrollbarStyle.Classic, host.Pipeline.ScrollbarStyle);
        host.RunFrame();
        var window = (HeadlessWindow)host.Window;
        var root = host.Root.RootElement;
        var scroller = root.ById("scroller");
        var c = scroller.ScrollController;
        var b = scroller.BoundingRect;
        // 纵向轨道：内边距盒右侧 12 像素；滑块长度 = 146 · 146 / 1200
        float trackX = b.Right - 2 - 6;
        float thumbLen = Math.Max(20, 146f * 146 / 1200);
        var thumbCenter = new Point(trackX, b.Top + 2 + thumbLen / 2);
        Assert.Same(scroller, host.Pipeline.HitTest(thumbCenter));
        // 经典滚动条常驻：轨道像素是浅灰（不是行的背景色）
        var px = window.Framebuffer!.GetPixel((int)trackX, (int)(b.Top + 140));
        Assert.Equal(Color.FromRgb(0xF1, 0xF1, 0xF1), px);
        int clicks = 0;
        Pointer(window, RawPointerEventType.Down, thumbCenter, changed: PointerButtons.Left, buttons: PointerButtons.Left);
        Assert.True(c.IsScrolling);
        Pointer(window, RawPointerEventType.Move, thumbCenter + new Point(0, 40), buttons: PointerButtons.Left);
        host.RunFrame();
        float expected = 40 * c.MaxOffset.Y / (146 - thumbLen);
        Assert.Equal(expected, c.Offset.Y, 1);
        // 拖到轨道之外：钳制
        Pointer(window, RawPointerEventType.Move, thumbCenter + new Point(30, 500), buttons: PointerButtons.Left);
        Pointer(window, RawPointerEventType.Up, thumbCenter + new Point(30, 500), changed: PointerButtons.Left);
        host.RunFrame();
        Assert.Equal(c.MaxOffset.Y, c.Offset.Y);
        Assert.False(c.IsScrolling);
        Assert.Equal(0, clicks);
        // 点击滑块上方的轨道：向上翻一页
        Pointer(window, RawPointerEventType.Down, new Point(trackX, b.Top + 10), changed: PointerButtons.Left, buttons: PointerButtons.Left);
        Pointer(window, RawPointerEventType.Up, new Point(trackX, b.Top + 10), changed: PointerButtons.Left);
        host.RunFrame();
        Assert.Equal(c.MaxOffset.Y - 146 * 0.875f, c.Offset.Y, 1);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Overlay_scrollbar_appears_while_scrolling_and_fades_out()
    {
        var (host, window, clock) = Create(new ListApp(), smooth: false);
        var b = host.Root.RootElement.ById("scroller").BoundingRect;
        var probe = new Point(b.Right - 2 - 4, b.Top + 2 + 20);
        var before = window.Framebuffer!.GetPixel((int)probe.X, (int)probe.Y);
        // 覆盖式滚动条隐藏时不参与命中
        Assert.Equal("row-0", host.Pipeline.HitTest(probe)?.Id);
        window.WheelNotches(Inside, 0.3f);
        host.RunFrame();
        var during = window.Framebuffer!.GetPixel((int)probe.X, (int)probe.Y);
        Assert.NotEqual(before, during);
        Assert.Equal("scroller", host.Pipeline.HitTest(probe)?.Id);
        Assert.Equal(FullRaster(host), Pixels(window));
        // 1 秒后开始淡出，0.3 秒淡完
        for (int i = 0; i < 100; i++) Step(host, clock, 20);
        Assert.False(host.Pipeline.NeedsAnimationFrame);
        var after = window.Framebuffer!.GetPixel((int)probe.X, (int)probe.Y);
        Assert.NotEqual(during, after);
        Assert.Equal("row-1", host.Pipeline.HitTest(probe)?.Id);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Touch_drag_scrolls_cancels_the_click_and_flings()
    {
        int clicks = 0, cancels = 0;
        var app = new ListApp { OnRowClick = _ => clicks++, OnRowCancel = _ => cancels++ };
        var (host, window, clock) = Create(app);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        // 轻点：仍然是点击
        Pointer(window, RawPointerEventType.Down, Inside, PointerType.Touch, 5, PointerButtons.Left, PointerButtons.Left, 0);
        Pointer(window, RawPointerEventType.Move, Inside + new Point(2, -3), PointerType.Touch, 5, buttons: PointerButtons.Left, ms: 30);
        Pointer(window, RawPointerEventType.Up, Inside + new Point(2, -3), PointerType.Touch, 5, PointerButtons.Left, ms: 60);
        Assert.Equal(1, clicks);
        Assert.Equal(0, c.Offset.Y);

        // 拖动：越过触摸容差后变成滚动，按下的行收到 pointercancel，抬起不产生 click
        double t = 1000;
        var p = Inside;
        Pointer(window, RawPointerEventType.Down, p, PointerType.Touch, 6, PointerButtons.Left, PointerButtons.Left, t);
        for (int i = 0; i < 10; i++)
        {
            t += 10;
            p += new Point(0, -12);
            Pointer(window, RawPointerEventType.Move, p, PointerType.Touch, 6, buttons: PointerButtons.Left, ms: t);
        }
        Assert.Equal(1, cancels);
        Assert.True(c.IsScrolling);
        // 第一次越过容差（12 > 8）只确定滚动，之后 9 次移动各 12 像素
        Assert.Equal(108, c.Offset.Y, 1);
        clock.Advance(TimeSpan.FromMilliseconds(t));
        Pointer(window, RawPointerEventType.Up, p, PointerType.Touch, 6, PointerButtons.Left, ms: t + 5);
        Assert.Equal(1, clicks);
        // 松手速度约 1200 px/s：惯性继续向下滚动
        Settle(host, clock);
        Assert.True(c.Offset.Y > 150, c.Offset.Y.ToString());
        Assert.False(c.IsScrolling);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Bouncing_physics_overscrolls_while_dragging_and_springs_back()
    {
        var ctl = new ScrollController { Physics = new BouncingPhysics() };
        var (host, window, clock) = Create(new ListApp { Controller = ctl });
        double t = 0;
        var p = Inside;
        Pointer(window, RawPointerEventType.Down, p, PointerType.Touch, 7, PointerButtons.Left, PointerButtons.Left, t);
        for (int i = 0; i < 8; i++)
        {
            t += 16;
            p += new Point(0, 20); // 手指向下：越过顶部
            Pointer(window, RawPointerEventType.Move, p, PointerType.Touch, 7, buttons: PointerButtons.Left, ms: t);
            Step(host, clock, 16);
        }
        Assert.True(ctl.Offset.Y < 0);
        // 橡皮筋阻尼：越界距离小于手指移动的距离
        Assert.True(ctl.Offset.Y > -140, ctl.Offset.Y.ToString());
        var row0 = host.Root.RootElement.ById("row-0").BoundingRect;
        Assert.True(row0.Top > 32);
        Assert.Equal(FullRaster(host), Pixels(window));
        // 停住再松手：弹簧回到 0
        t += 100;
        Pointer(window, RawPointerEventType.Move, p, PointerType.Touch, 7, buttons: PointerButtons.Left, ms: t);
        Pointer(window, RawPointerEventType.Up, p, PointerType.Touch, 7, PointerButtons.Left, ms: t + 1);
        Settle(host, clock);
        Assert.Equal(0, ctl.Offset.Y);
        Assert.Equal(32, host.Root.RootElement.ById("row-0").BoundingRect.Top);
        Assert.Equal(FullRaster(host), Pixels(window));
    }

    [Fact]
    public void Controller_reassignment_keeps_the_position_and_detaches_cleanly()
    {
        var which = new State<int>(0);
        var a = new ScrollController();
        var app = new Lambda { Render = () => new ListApp { Controller = which.Value == 0 ? null : a } };
        var (host, _, _) = Create(app, smooth: false);
        var el = host.Root.RootElement.ById("scroller");
        el.ScrollController.JumpTo(new Point(0, 300));
        host.RunFrame();
        which.Value = 1; // 换成一个没用过的控制器：接过当前位置
        host.RunFrame();
        Assert.Same(a, el.ScrollController);
        Assert.Equal(300, a.Offset.Y);
        Assert.Same(el, a.Element);
        a.JumpTo(new Point(0, 120));
        host.RunFrame();
        which.Value = 0; // 不再指定：元素换回自己的控制器，位置保留
        host.RunFrame();
        Assert.NotSame(a, el.ScrollController);
        Assert.Null(a.Element);
        Assert.Equal(120, el.ScrollController.Offset.Y);
    }
}

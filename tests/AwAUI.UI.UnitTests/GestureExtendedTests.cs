using System;
using System.Collections.Generic;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 第九批补全的手势识别器：LongPress / DoubleTap / Swipe / Pinch / Rotate 的确定性测试，
/// 以及第二根手指与单指 Tap / Pan 竞技场共存的回归测试（design/09、21）。
/// </summary>
public class GestureExtendedTests
{
    static UIRoot Mount(Node node, bool clock = true)
    {
        var root = new UIRoot();
        if (clock) root.Clock = new ManualUIClock();
        root.Mount(new Lambda { Render = () => node });
        return root;
    }

    static RawPointerEvent Event(RawPointerEventType type, long id, float x, float y, double ms) =>
        new(type, PointerType.Touch, id, new Point(x, y),
            type is RawPointerEventType.Down or RawPointerEventType.Up ? PointerButtons.Left : PointerButtons.None,
            type is RawPointerEventType.Up or RawPointerEventType.Cancel ? PointerButtons.None : PointerButtons.Left,
            KeyModifiers.None, TimeSpan.FromMilliseconds(ms));

    static RawPointerEvent Down(long id, float x, float y, double ms = 0) => Event(RawPointerEventType.Down, id, x, y, ms);
    static RawPointerEvent Move(long id, float x, float y, double ms = 16) => Event(RawPointerEventType.Move, id, x, y, ms);
    static RawPointerEvent Up(long id, float x, float y, double ms = 32) => Event(RawPointerEventType.Up, id, x, y, ms);
    static RawPointerEvent Cancel(long id, float x, float y, double ms = 32) => Event(RawPointerEventType.Cancel, id, x, y, ms);

    static void Near(float expected, float actual, float tolerance = 0.02f) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"期望 {expected}，实际 {actual}");

    // ------------------------------------------------------------ LongPress

    [Fact]
    public void Long_press_starts_after_duration_then_moves_and_ends()
    {
        var events = new List<LongPressEvent>();
        var root = Mount(new Div { Id = "lp", OnLongPress = events.Add });
        var element = root.RootElement.ById("lp");
        var longPress = new LongPressGestureRecognizer();
        element.AddGestureRecognizer(longPress);
        var clock = (ManualUIClock)root.Clock!;

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        Assert.Empty(events);

        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Empty(events);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var started = Assert.Single(events);
        Assert.Equal(LongPressPhase.Started, started.Phase);
        Assert.Equal(new Point(10, 10), started.Position);
        Assert.Equal(PointerType.Touch, started.PointerType);
        Assert.Equal(GestureState.Began, longPress.State);

        root.Input.DispatchPointer(Move(1, 12, 11, 520), element);
        var moved = events[^1];
        Assert.Equal(LongPressPhase.Moved, moved.Phase);
        Assert.Equal(new Point(12, 11), moved.Position);

        root.Input.DispatchPointer(Up(1, 12, 11, 600), element);
        Assert.Equal(LongPressPhase.Ended, events[^1].Phase);
        Assert.Equal(GestureState.Ended, longPress.State);
    }

    [Fact]
    public void Long_press_fails_when_moving_beyond_slop_before_the_duration()
    {
        var events = new List<LongPressEvent>();
        var root = Mount(new Div { Id = "lp", OnLongPress = events.Add });
        var element = root.RootElement.ById("lp");
        var longPress = new LongPressGestureRecognizer();
        element.AddGestureRecognizer(longPress);

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        root.Input.DispatchPointer(Move(1, 40, 10, 100), element);

        Assert.Empty(events);
        Assert.Equal(GestureState.Failed, longPress.State);
    }

    [Fact]
    public void Long_press_without_clock_uses_event_timestamps()
    {
        var events = new List<LongPressEvent>();
        var root = Mount(new Div { Id = "lp", OnLongPress = events.Add }, clock: false);
        var element = root.RootElement.ById("lp");
        element.AddGestureRecognizer(new LongPressGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 2, 1, 600), element);
        root.Input.DispatchPointer(Up(1, 2, 1, 700), element);

        Assert.Equal(2, events.Count);
        Assert.Equal(LongPressPhase.Started, events[0].Phase);
        Assert.Equal(LongPressPhase.Ended, events[1].Phase);
    }

    [Fact]
    public void Long_press_cancel_reports_cancelled_and_touch_action_none_does_not_block_it()
    {
        var events = new List<LongPressEvent>();
        var root = Mount(new Div { Id = "lp", OnLongPress = events.Add });
        var element = root.RootElement.ById("lp");
        var longPress = new LongPressGestureRecognizer();
        element.AddGestureRecognizer(longPress);
        var clock = (ManualUIClock)root.Clock!;

        Assert.True(longPress.AllowsTouchAction(CssTouchAction.None));

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(LongPressPhase.Started, Assert.Single(events).Phase);

        root.Input.DispatchPointer(Cancel(1, 10, 10, 520), element);
        Assert.Equal(LongPressPhase.Cancelled, events[^1].Phase);
        Assert.Equal(GestureState.Cancelled, longPress.State);
    }

    [Fact]
    public void Long_press_can_be_bound_through_attribute_expansion()
    {
        int count = 0;
        var node = new Div { Id = "lp" };
        node.MergeAttributes(new Dictionary<string, object?>
        {
            ["onlongpress"] = new Action<LongPressEvent>(_ => count++),
        });
        var root = Mount(node);
        var element = root.RootElement.ById("lp");
        element.AddGestureRecognizer(new LongPressGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        ((ManualUIClock)root.Clock!).Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, count);
    }

    // ------------------------------------------------------------ DoubleTap

    [Fact]
    public void Double_tap_fires_on_the_second_press_within_interval_and_distance()
    {
        var events = new List<DoubleTapEvent>();
        var root = Mount(new Div { Id = "dt", OnDoubleTap = events.Add });
        var element = root.RootElement.ById("dt");
        var doubleTap = new DoubleTapGestureRecognizer();
        element.AddGestureRecognizer(doubleTap);

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        root.Input.DispatchPointer(Up(1, 10, 10, 40), element);
        Assert.Empty(events);

        root.Input.DispatchPointer(Down(1, 12, 11, 100), element);
        root.Input.DispatchPointer(Up(1, 12, 11, 140), element);

        var e = Assert.Single(events);
        Assert.Equal(new Point(12, 11), e.Position);
        Assert.Equal(PointerType.Touch, e.PointerType);
        Assert.Equal(TimeSpan.FromMilliseconds(140), e.Timestamp);
        Assert.Equal(GestureState.Ended, doubleTap.State);
    }

    [Fact]
    public void Double_tap_times_out_and_a_late_second_press_starts_a_new_sequence()
    {
        var events = new List<DoubleTapEvent>();
        var root = Mount(new Div { Id = "dt", OnDoubleTap = events.Add });
        var element = root.RootElement.ById("dt");
        var doubleTap = new DoubleTapGestureRecognizer();
        element.AddGestureRecognizer(doubleTap);
        var clock = (ManualUIClock)root.Clock!;

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        root.Input.DispatchPointer(Up(1, 10, 10, 40), element);
        clock.Advance(GestureSettings.Default.DoubleTapInterval);

        Assert.Empty(events);
        Assert.Equal(GestureState.Failed, doubleTap.State);

        // 事件时间戳也超过了间隔：这一次按下只开始新的一次点按
        root.Input.DispatchPointer(Down(1, 10, 10, 500), element);
        root.Input.DispatchPointer(Up(1, 10, 10, 540), element);
        Assert.Empty(events);
    }

    [Fact]
    public void Double_tap_rejects_a_second_press_too_far_away()
    {
        var events = new List<DoubleTapEvent>();
        var root = Mount(new Div { Id = "dt", OnDoubleTap = events.Add });
        var element = root.RootElement.ById("dt");
        element.AddGestureRecognizer(new DoubleTapGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        root.Input.DispatchPointer(Up(1, 10, 10, 40), element);
        root.Input.DispatchPointer(Down(1, 200, 10, 100), element);
        root.Input.DispatchPointer(Up(1, 200, 10, 140), element);

        Assert.Empty(events);
    }

    [Fact]
    public void Double_tap_without_clock_uses_event_timestamps()
    {
        var events = new List<DoubleTapEvent>();
        var root = Mount(new Div { Id = "dt", OnDoubleTap = events.Add }, clock: false);
        var element = root.RootElement.ById("dt");
        element.AddGestureRecognizer(new DoubleTapGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        root.Input.DispatchPointer(Up(1, 10, 10, 40), element);
        root.Input.DispatchPointer(Down(1, 10, 10, 100), element);
        root.Input.DispatchPointer(Up(1, 10, 10, 140), element);

        Assert.Single(events);
    }

    // ------------------------------------------------------------ Swipe

    [Fact]
    public void Swipe_reports_direction_velocity_distance_and_position()
    {
        var events = new List<SwipeEvent>();
        var root = Mount(new Div { Id = "s", OnSwipe = events.Add });
        var element = root.RootElement.ById("s");
        var swipe = new SwipeGestureRecognizer();
        element.AddGestureRecognizer(swipe);

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 20, 0, 16), element);
        root.Input.DispatchPointer(Move(1, 40, 0, 32), element);
        root.Input.DispatchPointer(Move(1, 60, 0, 48), element);
        root.Input.DispatchPointer(Up(1, 80, 0, 64), element);

        var e = Assert.Single(events);
        Assert.Equal(SwipeDirection.Right, e.Direction);
        Assert.True(e.Velocity.X > GestureSettings.Default.SwipeMinVelocity);
        Assert.Equal(new Point(80, 0), e.Distance);
        Assert.Equal(new Point(80, 0), e.Position);
        Assert.Equal(PointerType.Touch, e.PointerType);
        Assert.Equal(GestureState.Ended, swipe.State);
    }

    [Fact]
    public void Swipe_requires_the_minimum_velocity()
    {
        var events = new List<SwipeEvent>();
        var root = Mount(new Div { Id = "s", OnSwipe = events.Add });
        var element = root.RootElement.ById("s");
        var swipe = new SwipeGestureRecognizer();
        element.AddGestureRecognizer(swipe);

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 15, 0, 100), element);
        root.Input.DispatchPointer(Move(1, 30, 0, 200), element);
        root.Input.DispatchPointer(Move(1, 45, 0, 300), element);
        root.Input.DispatchPointer(Up(1, 60, 0, 400), element);

        Assert.Empty(events);
        Assert.Equal(GestureState.Failed, swipe.State);
    }

    [Fact]
    public void Swipe_direction_uses_the_dominant_axis()
    {
        var events = new List<SwipeEvent>();
        var root = Mount(new Div { Id = "s", OnSwipe = events.Add });
        var element = root.RootElement.ById("s");
        element.AddGestureRecognizer(new SwipeGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 0, 20, 16), element);
        root.Input.DispatchPointer(Move(1, 0, 40, 32), element);
        root.Input.DispatchPointer(Move(1, 0, 60, 48), element);
        root.Input.DispatchPointer(Up(1, 0, 80, 64), element);

        Assert.Equal(SwipeDirection.Down, Assert.Single(events).Direction);
    }

    [Fact]
    public void Swipe_is_rejected_by_touch_action_none()
    {
        var swipe = new SwipeGestureRecognizer();
        Assert.False(swipe.AllowsTouchAction(CssTouchAction.None));
        Assert.True(swipe.AllowsTouchAction(CssTouchAction.Manipulation));
    }

    // ------------------------------------------------------------ Pinch / Rotate

    [Fact]
    public void Pinch_reports_started_changed_ended_with_midpoint_focus()
    {
        var events = new List<PinchEvent>();
        var root = Mount(new Div { Id = "p", OnPinch = events.Add });
        var element = root.RootElement.ById("p");
        var pinch = new PinchGestureRecognizer();
        element.AddGestureRecognizer(pinch);

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Down(2, 200, 100, 10), element);
        Assert.Empty(events);

        root.Input.DispatchPointer(Move(2, 300, 100, 20), element);
        var started = Assert.Single(events);
        Assert.Equal(GesturePhase.Started, started.Phase);
        Near(2f, started.Scale);
        Near(1f, started.DeltaScale);
        Assert.Equal(new Point(200, 100), started.Focus);
        Assert.Equal(PointerType.Touch, started.PointerType);

        root.Input.DispatchPointer(Move(1, 50, 100, 30), element);
        var changed = events[^1];
        Assert.Equal(GesturePhase.Changed, changed.Phase);
        Near(2.5f, changed.Scale);
        Near(0.5f, changed.DeltaScale);
        Assert.Equal(new Point(175, 100), changed.Focus);

        root.Input.DispatchPointer(Up(2, 300, 100, 40), element);
        Assert.Equal(GesturePhase.Ended, events[^1].Phase);
        Assert.Equal(GestureState.Ended, pinch.State);
    }

    [Fact]
    public void Pinch_below_threshold_does_not_start()
    {
        var events = new List<PinchEvent>();
        var root = Mount(new Div { Id = "p", OnPinch = events.Add });
        var element = root.RootElement.ById("p");
        var pinch = new PinchGestureRecognizer();
        element.AddGestureRecognizer(pinch);

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Down(2, 200, 100, 10), element);
        root.Input.DispatchPointer(Move(2, 201, 100, 20), element);
        root.Input.DispatchPointer(Up(2, 201, 100, 30), element);

        Assert.Empty(events);
        Assert.Equal(GestureState.Failed, pinch.State);
    }

    [Fact]
    public void Pinch_cancel_resets_and_reports_cancelled()
    {
        var events = new List<PinchEvent>();
        var root = Mount(new Div { Id = "p", OnPinch = events.Add });
        var element = root.RootElement.ById("p");
        var pinch = new PinchGestureRecognizer();
        element.AddGestureRecognizer(pinch);

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Down(2, 200, 100, 10), element);
        root.Input.DispatchPointer(Move(2, 300, 100, 20), element);
        Assert.Equal(GesturePhase.Started, events[^1].Phase);

        root.Input.DispatchPointer(Cancel(1, 100, 100, 30), element);
        Assert.Equal(GesturePhase.Cancelled, events[^1].Phase);
        Assert.Equal(GestureState.Cancelled, pinch.State);

        int count = events.Count;
        root.Input.DispatchPointer(Move(2, 400, 100, 40), element);
        Assert.Equal(count, events.Count);
    }

    [Fact]
    public void Pinch_scale_is_independent_of_pointer_move_order()
    {
        var first = RunPinch(moveSecondFirst: false);
        var second = RunPinch(moveSecondFirst: true);
        Near(first, second);
    }

    static float RunPinch(bool moveSecondFirst)
    {
        var events = new List<PinchEvent>();
        var root = Mount(new Div { Id = "p", OnPinch = events.Add });
        var element = root.RootElement.ById("p");
        element.AddGestureRecognizer(new PinchGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Down(2, 200, 100, 10), element);
        if (moveSecondFirst)
        {
            root.Input.DispatchPointer(Move(2, 250, 100, 20), element);
            root.Input.DispatchPointer(Move(1, 50, 100, 30), element);
        }
        else
        {
            root.Input.DispatchPointer(Move(1, 50, 100, 20), element);
            root.Input.DispatchPointer(Move(2, 250, 100, 30), element);
        }
        return events[^1].Scale;
    }

    [Fact]
    public void Rotate_reports_angle_delta_focus_and_phases()
    {
        var events = new List<RotateEvent>();
        var root = Mount(new Div { Id = "r", OnRotate = events.Add });
        var element = root.RootElement.ById("r");
        var rotate = new RotateGestureRecognizer();
        element.AddGestureRecognizer(rotate);

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Down(2, 200, 100, 10), element);

        root.Input.DispatchPointer(Move(2, 100, 200, 20), element);
        var started = Assert.Single(events);
        Assert.Equal(GesturePhase.Started, started.Phase);
        Near(90f, started.AngleDegrees, 0.1f);
        Near(90f, started.DeltaDegrees, 0.1f);
        Assert.Equal(new Point(100, 150), started.Focus);

        root.Input.DispatchPointer(Move(1, 150, 100, 30), element);
        var changed = events[^1];
        Assert.Equal(GesturePhase.Changed, changed.Phase);
        Near(116.565f, changed.AngleDegrees, 0.1f);
        Near(26.565f, changed.DeltaDegrees, 0.1f);
        Assert.Equal(new Point(125, 150), changed.Focus);

        root.Input.DispatchPointer(Up(2, 100, 200, 40), element);
        Assert.Equal(GesturePhase.Ended, events[^1].Phase);
        Assert.Equal(GestureState.Ended, rotate.State);
    }

    [Fact]
    public void Rotate_below_threshold_does_not_start_and_cancel_reports_cancelled()
    {
        var events = new List<RotateEvent>();
        var root = Mount(new Div { Id = "r", OnRotate = events.Add });
        var element = root.RootElement.ById("r");
        var rotate = new RotateGestureRecognizer();
        element.AddGestureRecognizer(rotate);

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Down(2, 300, 100, 10), element);
        // 100 px 的半径上旋转 2°（约 3.5 px）不足以开始
        root.Input.DispatchPointer(Move(2, 300, 103.5f, 20), element);
        Assert.Empty(events);

        root.Input.DispatchPointer(Move(2, 100, 200, 30), element);
        Assert.Equal(GesturePhase.Started, events[^1].Phase);
        root.Input.DispatchPointer(Cancel(2, 100, 200, 40), element);
        Assert.Equal(GesturePhase.Cancelled, events[^1].Phase);
        Assert.Equal(GestureState.Cancelled, rotate.State);
    }

    [Fact]
    public void Pinch_and_rotate_allow_touch_action_values()
    {
        var pinch = new PinchGestureRecognizer();
        var rotate = new RotateGestureRecognizer();
        foreach (var recognizer in new GestureRecognizer[] { pinch, rotate })
        {
            Assert.True(recognizer.AllowsTouchAction(CssTouchAction.Auto));
            Assert.True(recognizer.AllowsTouchAction(CssTouchAction.PinchZoom));
            Assert.True(recognizer.AllowsTouchAction(CssTouchAction.Manipulation));
            Assert.False(recognizer.AllowsTouchAction(CssTouchAction.None));
            Assert.False(recognizer.AllowsTouchAction(CssTouchAction.PanX));
        }
    }

    // ------------------------------------------------------------ 多指与单指竞技场共存

    [Fact]
    public void Second_pointer_does_not_reset_the_first_pointer_tap_arena()
    {
        var taps = new List<TapEvent>();
        var root = Mount(new Div { Id = "t", OnTap = taps.Add });
        var element = root.RootElement.ById("t");
        var tap = new TapGestureRecognizer();
        element.AddGestureRecognizer(tap);

        root.Input.DispatchPointer(Down(1, 10, 10, 0), element);
        root.Input.DispatchPointer(Down(2, 50, 50, 10), element);
        root.Input.DispatchPointer(Up(1, 12, 11, 20), element);
        root.Input.DispatchPointer(Up(2, 50, 50, 30), element);

        var e = Assert.Single(taps);
        Assert.Equal(new Point(12, 11), e.Position);
        Assert.Equal(GestureState.Ended, tap.State);
    }

    [Fact]
    public void Second_pointer_does_not_disturb_a_running_pan()
    {
        var started = new List<PanEvent>();
        var changed = new List<PanEvent>();
        var ended = new List<PanEvent>();
        var pan = new PanGestureRecognizer();
        pan.Started += started.Add;
        pan.Changed += changed.Add;
        pan.Ended += ended.Add;

        var root = Mount(new Div { Id = "p" });
        var element = root.RootElement.ById("p");
        element.AddGestureRecognizer(pan);

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 30, 4, 16), element);
        Assert.Single(started);

        root.Input.DispatchPointer(Down(2, 100, 100, 20), element);
        root.Input.DispatchPointer(Move(1, 60, 6, 32), element);
        root.Input.DispatchPointer(Up(1, 90, 8, 48), element);

        Assert.Single(changed);
        Assert.Single(ended);
        Assert.Equal(GestureState.Ended, pan.State);
    }

    [Fact]
    public void Pinch_and_a_first_finger_tap_can_coexist()
    {
        var taps = new List<TapEvent>();
        var pinches = new List<PinchEvent>();
        var root = Mount(new Div { Id = "mix", OnTap = taps.Add, OnPinch = pinches.Add });
        var element = root.RootElement.ById("mix");
        element.AddGestureRecognizer(new TapGestureRecognizer());
        element.AddGestureRecognizer(new PinchGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 100, 100, 0), element);
        root.Input.DispatchPointer(Move(1, 102, 101, 10), element);
        root.Input.DispatchPointer(Down(2, 200, 100, 20), element);
        root.Input.DispatchPointer(Move(2, 300, 100, 30), element);

        Assert.Single(pinches);
        Assert.Empty(taps);

        root.Input.DispatchPointer(Up(1, 102, 101, 40), element);
        Assert.Single(taps);
    }
}

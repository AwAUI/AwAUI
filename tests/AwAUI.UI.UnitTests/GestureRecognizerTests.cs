using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>内建识别器：点按容差与结束、拖动容差 / 轴向锁定 / 增量 / 速度，以及 <c>touch-action</c> 过滤（design/21）。</summary>
public class GestureRecognizerTests
{
    static UIRoot Mount(Node node)
    {
        var root = new UIRoot { Clock = new ManualUIClock() };
        root.Mount(new Lambda { Render = () => node });
        return root;
    }

    static RawPointerEvent Event(RawPointerEventType type, long id, float x, float y, double ms) =>
        new(type, PointerType.Touch, id, new Point(x, y),
            type is RawPointerEventType.Down or RawPointerEventType.Up ? PointerButtons.Left : PointerButtons.None,
            type == RawPointerEventType.Up ? PointerButtons.None : PointerButtons.Left,
            KeyModifiers.None, TimeSpan.FromMilliseconds(ms));

    static RawPointerEvent Down(long id, float x, float y, double ms = 0) => Event(RawPointerEventType.Down, id, x, y, ms);
    static RawPointerEvent Move(long id, float x, float y, double ms = 16) => Event(RawPointerEventType.Move, id, x, y, ms);
    static RawPointerEvent Up(long id, float x, float y, double ms = 32) => Event(RawPointerEventType.Up, id, x, y, ms);

    // ------------------------------------------------------------ Tap

    [Fact]
    public void Tap_fires_within_slop_with_position_type_and_count_and_click_still_works()
    {
        var taps = new List<TapEvent>();
        int clicks = 0;
        var root = Mount(new Div { Id = "t", OnTap = e => taps.Add(e), OnClick = _ => clicks++ });
        var element = root.RootElement.ById("t");
        element.AddGestureRecognizer(new TapGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 10, 10), element);
        root.Input.DispatchPointer(Up(1, 12, 11), element);

        var tap = Assert.Single(taps);
        Assert.Equal(new Point(12, 11), tap.Position);
        Assert.Equal(PointerType.Touch, tap.PointerType);
        Assert.Equal(1, tap.TapCount);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Tap_does_not_fire_after_moving_beyond_slop()
    {
        var taps = new List<TapEvent>();
        var root = Mount(new Div { Id = "t", OnTap = e => taps.Add(e) });
        var element = root.RootElement.ById("t");
        element.AddGestureRecognizer(new TapGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 10, 10), element);
        root.Input.DispatchPointer(Move(1, 40, 10), element);
        root.Input.DispatchPointer(Up(1, 40, 10), element);

        Assert.Empty(taps);
    }

    [Fact]
    public void Pan_recognizer_keeps_ordinary_click_working()
    {
        int clicks = 0;
        var root = Mount(new Div { Id = "p", OnClick = _ => clicks++ });
        var element = root.RootElement.ById("p");
        element.AddGestureRecognizer(new PanGestureRecognizer());

        root.Input.DispatchPointer(Down(1, 10, 10), element);
        root.Input.DispatchPointer(Up(1, 10, 10), element);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Tap_recognizer_keeps_text_selection_working()
    {
        var harness = new PipelineHarness(new Div
        {
            Children = [new Text("hello") { Id = "a" }],
        });
        harness.Frame();
        var a = harness.El("a");
        a.AddGestureRecognizer(new TapGestureRecognizer());

        var y = a.BoundingRect.Y + a.BoundingRect.Height / 2;
        var start = new Point(a.BoundingRect.X + 2 * 14 + 0.25f, y);
        var end = new Point(a.BoundingRect.X + 4 * 14 + 0.25f, y);
        harness.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, start,
            PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        harness.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, end,
            PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        harness.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, end,
            PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));

        Assert.True(harness.Root.Selection.HasSelection);
    }

    // ------------------------------------------------------------ Pan

    [Fact]
    public void Pan_reports_started_changed_ended_with_axis_lock_and_velocity()
    {
        var started = new List<PanEvent>();
        var changed = new List<PanEvent>();
        var ended = new List<PanEvent>();
        var onPan = new List<PanEvent>();
        var pan = new PanGestureRecognizer();
        pan.Started += started.Add;
        pan.Changed += changed.Add;
        pan.Ended += ended.Add;

        var root = Mount(new Div { Id = "p", OnPan = onPan.Add });
        var element = root.RootElement.ById("p");
        element.AddGestureRecognizer(pan);

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 2, 2, 16), element);   // 容差内
        root.Input.DispatchPointer(Move(1, 30, 4, 32), element);  // 越过容差，横向占优
        root.Input.DispatchPointer(Move(1, 60, 6, 48), element);
        root.Input.DispatchPointer(Up(1, 90, 8, 64), element);

        var begin = Assert.Single(started);
        Assert.Equal(0, begin.Delta.X);
        Assert.Equal(0, begin.Delta.Y);
        Assert.True(begin.Total.X > 0);

        var move = Assert.Single(changed);
        Assert.True(move.Delta.X > 0);
        Assert.Equal(0, move.Delta.Y); // 方向锁定：纵向增量被清零

        var end = Assert.Single(ended);
        Assert.True(end.Velocity.X > 0);
        Assert.Equal(GestureState.Ended, pan.State);

        // OnPan 收到开始 / 变化 / 结束
        Assert.Equal(3, onPan.Count);
    }

    [Fact]
    public void Pan_does_not_start_before_the_slop_is_exceeded()
    {
        bool began = false;
        var pan = new PanGestureRecognizer();
        pan.Started += _ => began = true;
        var root = Mount(new Div { Id = "p" });
        var element = root.RootElement.ById("p");
        element.AddGestureRecognizer(pan);

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 3, 3, 16), element);
        root.Input.DispatchPointer(Up(1, 3, 3, 32), element);

        Assert.False(began);
        Assert.Equal(GestureState.Possible, pan.State); // 赢得竞技场但从未开始拖动
    }

    [Fact]
    public void Horizontal_only_pan_rejects_a_vertical_drag()
    {
        bool began = false;
        var pan = new PanGestureRecognizer { Axis = PanAxis.X };
        pan.Started += _ => began = true;
        var root = Mount(new Div { Id = "p" });
        var element = root.RootElement.ById("p");
        element.AddGestureRecognizer(pan);

        root.Input.DispatchPointer(Down(1, 0, 0, 0), element);
        root.Input.DispatchPointer(Move(1, 2, 30, 16), element);

        Assert.False(began);
        Assert.Equal(GestureState.Failed, pan.State);
    }

    [Fact]
    public void Pan_is_filtered_by_touch_action_none()
    {
        var pan = new PanGestureRecognizer();
        Assert.False(pan.AllowsTouchAction(CssTouchAction.None));
        Assert.True(pan.AllowsTouchAction(CssTouchAction.Auto));
        Assert.True(pan.AllowsTouchAction(CssTouchAction.PanX));
        Assert.True(pan.AllowsTouchAction(CssTouchAction.Manipulation));
    }
}

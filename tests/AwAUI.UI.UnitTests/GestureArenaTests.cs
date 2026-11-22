using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>手势竞技场：入场顺序、显式胜出、唯一幸存者、抬起回退、延后裁决、同时生效与取消（design/21「竞技场规则」）。</summary>
public class GestureArenaTests
{
    static UIRoot Mount(Node node)
    {
        var root = new UIRoot { Clock = new ManualUIClock() };
        root.Mount(new Lambda { Render = () => node });
        return root;
    }

    static RawPointerEvent Down(long id, float x, float y) =>
        new(RawPointerEventType.Down, PointerType.Touch, id, new Point(x, y), PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero);

    static RawPointerEvent Move(long id, float x, float y) =>
        new(RawPointerEventType.Move, PointerType.Touch, id, new Point(x, y), PointerButtons.None, PointerButtons.Left, KeyModifiers.None, TimeSpan.FromMilliseconds(16));

    static RawPointerEvent Up(long id, float x, float y) =>
        new(RawPointerEventType.Up, PointerType.Touch, id, new Point(x, y), PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.FromMilliseconds(32));

    static RawPointerEvent Cancel(long id, float x, float y) =>
        new(RawPointerEventType.Cancel, PointerType.Touch, id, new Point(x, y), PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.FromMilliseconds(32));

    static (UIRoot Root, Element Inner, Element Outer) Nested()
    {
        var root = Mount(new Div
        {
            Id = "outer",
            Children = [new Div { Id = "inner" }],
        });
        return (root, root.RootElement.ById("inner"), root.RootElement.ById("outer"));
    }

    [Fact]
    public void Inner_recognizer_enters_first_and_explicit_accept_rejects_the_rest()
    {
        var order = new List<string>();
        var inner = new ProbeRecognizer();
        var outer = new ProbeRecognizer();
        inner.OnEvent = (e, entry) =>
        {
            order.Add("inner:" + e.Type);
            if (e.Type == "pointermove") entry.Accept();
        };
        outer.OnEvent = (e, _) => order.Add("outer:" + e.Type);

        var (root, innerElement, outerElement) = Nested();
        innerElement.AddGestureRecognizer(inner);
        outerElement.AddGestureRecognizer(outer);

        root.Input.DispatchPointer(Down(1, 10, 10), innerElement);
        root.Input.DispatchPointer(Move(1, 40, 10), innerElement);

        Assert.Equal(new[] { "inner:pointerdown", "outer:pointerdown", "inner:pointermove" }, order);
        Assert.Equal(1, inner.Accepted);
        Assert.Equal(GestureState.Began, inner.State);
        Assert.Equal(1, outer.Rejected);
        Assert.Equal(GestureState.Failed, outer.State);
    }

    [Fact]
    public void Sole_survivor_wins_automatically_on_pointer_down()
    {
        var recognizer = new ProbeRecognizer();
        var (root, inner, _) = Nested();
        inner.AddGestureRecognizer(recognizer);

        root.Input.DispatchPointer(Down(1, 10, 10), inner);

        Assert.Equal(1, recognizer.Accepted);
        Assert.Equal(GestureState.Began, recognizer.State);
    }

    [Fact]
    public void Pointer_up_without_accept_picks_the_first_entered()
    {
        var inner = new ProbeRecognizer();
        var outer = new ProbeRecognizer();
        var (root, innerElement, outerElement) = Nested();
        innerElement.AddGestureRecognizer(inner);
        outerElement.AddGestureRecognizer(outer);

        root.Input.DispatchPointer(Down(1, 10, 10), innerElement);
        root.Input.DispatchPointer(Up(1, 10, 10), innerElement);

        Assert.Equal(1, inner.Accepted);
        Assert.Equal(GestureState.Began, inner.State);
        Assert.Equal(1, outer.Rejected);
        Assert.Equal(GestureState.Failed, outer.State);
    }

    [Fact]
    public void Hold_defers_the_fallback_until_the_clock_advances()
    {
        var inner = new ProbeRecognizer();
        var outer = new ProbeRecognizer();
        inner.OnEvent = (e, entry) =>
        {
            if (e.Type == "pointerdown") entry.Hold();
        };
        var (root, innerElement, outerElement) = Nested();
        innerElement.AddGestureRecognizer(inner);
        outerElement.AddGestureRecognizer(outer);
        var clock = (ManualUIClock)root.Clock!;

        root.Input.DispatchPointer(Down(1, 10, 10), innerElement);
        root.Input.DispatchPointer(Up(1, 10, 10), innerElement);
        Assert.Equal(0, inner.Accepted);
        Assert.Equal(0, outer.Rejected);

        clock.Advance(GestureArena.DefaultHoldTimeout);

        Assert.Equal(1, inner.Accepted);
        Assert.Equal(GestureState.Began, inner.State);
        Assert.Equal(1, outer.Rejected);
        Assert.Equal(GestureState.Failed, outer.State);
    }

    [Fact]
    public void Simultaneous_team_wins_and_loses_together()
    {
        var inner = new ProbeRecognizer();
        var outer = new ProbeRecognizer();
        inner.AllowSimultaneousWith(outer);
        outer.AllowSimultaneousWith(inner);
        inner.OnEvent = (e, entry) =>
        {
            if (e.Type == "pointermove") entry.Accept();
        };
        var (root, innerElement, outerElement) = Nested();
        innerElement.AddGestureRecognizer(inner);
        outerElement.AddGestureRecognizer(outer);

        root.Input.DispatchPointer(Down(1, 10, 10), innerElement);
        root.Input.DispatchPointer(Move(1, 40, 10), innerElement);

        Assert.Equal(1, inner.Accepted);
        Assert.Equal(1, outer.Accepted);
        Assert.Equal(0, outer.Rejected);
        Assert.Equal(GestureState.Began, inner.State);
        Assert.Equal(GestureState.Began, outer.State);
    }

    [Fact]
    public void Cancel_cancels_every_live_recognizer_and_stops_routing()
    {
        var inner = new ProbeRecognizer();
        var outer = new ProbeRecognizer();
        var (root, innerElement, outerElement) = Nested();
        innerElement.AddGestureRecognizer(inner);
        outerElement.AddGestureRecognizer(outer);

        root.Input.DispatchPointer(Down(1, 10, 10), innerElement);
        root.Input.DispatchPointer(Cancel(1, 10, 10), innerElement);

        Assert.Equal(GestureState.Cancelled, inner.State);
        Assert.Equal(GestureState.Cancelled, outer.State);
        Assert.Equal(0, inner.Rejected);
        Assert.Equal(0, outer.Rejected);

        int events = inner.Events.Count;
        root.Input.DispatchPointer(Move(1, 40, 10), innerElement);
        Assert.Equal(events, inner.Events.Count);
    }

    [Fact]
    public void RequireFailureOf_blocks_acceptance_until_the_other_rejects()
    {
        var inner = new ProbeRecognizer();
        var outer = new ProbeRecognizer();
        inner.RequireFailureOf(outer);
        inner.OnEvent = (e, entry) =>
        {
            if (e.Type == "pointermove") entry.Accept();
        };
        outer.OnEvent = (e, entry) =>
        {
            if (e.Type == "pointermove") entry.Reject();
        };
        var (root, innerElement, outerElement) = Nested();
        innerElement.AddGestureRecognizer(inner);
        outerElement.AddGestureRecognizer(outer);

        root.Input.DispatchPointer(Down(1, 10, 10), innerElement);
        root.Input.DispatchPointer(Move(1, 40, 10), innerElement);

        Assert.Equal(1, inner.Accepted);
        Assert.Equal(GestureState.Began, inner.State);
        Assert.Equal(1, outer.Rejected);
        Assert.Equal(GestureState.Failed, outer.State);
    }

    [Fact]
    public void Winning_pan_captures_the_pointer_when_dragging_and_releases_it_on_up()
    {
        var pan = new PanGestureRecognizer();
        var (root, inner, _) = Nested();
        inner.AddGestureRecognizer(pan);

        root.Input.DispatchPointer(Down(1, 10, 10), inner);
        // 仅仅点下（未越过容差）不捕获：普通点击的目标不变
        Assert.False(inner.HasPointerCapture(1));

        root.Input.DispatchPointer(Move(1, 40, 10), inner);
        Assert.True(inner.HasPointerCapture(1));

        root.Input.DispatchPointer(Up(1, 40, 10), inner);
        Assert.False(inner.HasPointerCapture(1));
    }

    /// <summary>可编程的探针识别器：记录事件、统计裁决、按测试给定的逻辑宣告。</summary>
    sealed class ProbeRecognizer : GestureRecognizer
    {
        internal readonly List<string> Events = new();

        internal Action<PointerEvent, ArenaEntry>? OnEvent;

        internal int Accepted;

        internal int Rejected;

        protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
        {
            Events.Add(e.Type);
            if (e.Type == "pointercancel") State = GestureState.Cancelled;
            OnEvent?.Invoke(e, entry);
        }

        protected internal override void OnAccepted(long pointerId)
        {
            Accepted++;
            base.OnAccepted(pointerId);
        }

        protected internal override void OnRejected(long pointerId)
        {
            Rejected++;
            base.OnRejected(pointerId);
        }
    }
}

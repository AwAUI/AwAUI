using System;
using System.Collections.Generic;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 边缘滑动识别器（design/09「可用性约束」）：四条边缘的判定、向内移动胜出、方向错误与容差内抬起不派发，
/// 以及 <c>touch-action: none</c> 不阻止它入场。
/// </summary>
public class EdgeSwipeGestureTests
{
    static RawPointerEvent Event(RawPointerEventType type, long id, float x, float y, double ms) =>
        new(type, PointerType.Touch, id, new Point(x, y),
            type is RawPointerEventType.Down or RawPointerEventType.Up ? PointerButtons.Left : PointerButtons.None,
            type == RawPointerEventType.Up ? PointerButtons.None : PointerButtons.Left,
            KeyModifiers.None, TimeSpan.FromMilliseconds(ms));

    static RawPointerEvent Down(long id, float x, float y, double ms = 0) => Event(RawPointerEventType.Down, id, x, y, ms);
    static RawPointerEvent Move(long id, float x, float y, double ms = 16) => Event(RawPointerEventType.Move, id, x, y, ms);
    static RawPointerEvent Up(long id, float x, float y, double ms = 32) => Event(RawPointerEventType.Up, id, x, y, ms);

    static (UIRoot Root, Element Element, List<EdgeSwipeEvent> Events) Setup(EdgeSwipeGestureRecognizer? recognizer = null)
    {
        var events = new List<EdgeSwipeEvent>();
        // 400x300 视口；边缘带默认 20 逻辑像素。没有时钟。
        var harness = new PipelineHarness(
            new Div { Id = "e", OnEdgeSwipe = events.Add },
            width: 400,
            height: 300);
        var element = harness.El("e");
        element.AddGestureRecognizer(recognizer ?? new EdgeSwipeGestureRecognizer());
        return (harness.Root, element, events);
    }

    [Theory]
    [InlineData(EdgeSwipeEdge.Left, 5f, 150f, 50f, 150f)]
    [InlineData(EdgeSwipeEdge.Right, 395f, 150f, 350f, 150f)]
    [InlineData(EdgeSwipeEdge.Top, 200f, 5f, 200f, 50f)]
    [InlineData(EdgeSwipeEdge.Bottom, 200f, 295f, 200f, 250f)]
    public void Edge_swipe_wins_from_each_edge_and_dispatches_on_up(
        EdgeSwipeEdge expectedEdge, float startX, float startY, float endX, float endY)
    {
        var (root, element, events) = Setup();

        root.Input.DispatchPointer(Down(1, startX, startY, 0), element);
        root.Input.DispatchPointer(Move(1, endX, endY, 30), element);
        Assert.Empty(events); // 抬起时才派发

        root.Input.DispatchPointer(Up(1, endX, endY, 60), element);

        var e = Assert.Single(events);
        Assert.Equal(expectedEdge, e.Edge);
        Assert.Equal(new Point(endX, endY), e.Position);
        Assert.Equal(new Point(endX - startX, endY - startY), e.Distance);
        Assert.Equal(PointerType.Touch, e.PointerType);
    }

    [Fact]
    public void A_press_in_the_middle_is_not_accepted()
    {
        var (root, element, events) = Setup();

        root.Input.DispatchPointer(Down(1, 200, 150, 0), element);
        root.Input.DispatchPointer(Move(1, 260, 150, 30), element);
        root.Input.DispatchPointer(Up(1, 260, 150, 60), element);

        Assert.Empty(events);
    }

    [Fact]
    public void Dragging_outward_fails_and_does_not_dispatch()
    {
        var recognizer = new EdgeSwipeGestureRecognizer();
        var (root, element, events) = Setup(recognizer);

        root.Input.DispatchPointer(Down(1, 10, 150, 0), element);
        root.Input.DispatchPointer(Move(1, -10, 150, 30), element);

        Assert.Equal(GestureState.Failed, recognizer.State);

        root.Input.DispatchPointer(Up(1, -10, 150, 60), element);
        Assert.Empty(events);
    }

    [Fact]
    public void Releasing_without_moving_inward_does_not_dispatch()
    {
        var (root, element, events) = Setup();

        root.Input.DispatchPointer(Down(1, 5, 150, 0), element);
        root.Input.DispatchPointer(Move(1, 10, 150, 30), element); // 2 px，未超过容差
        root.Input.DispatchPointer(Up(1, 10, 150, 60), element);

        Assert.Empty(events);
    }

    [Fact]
    public void Edge_restriction_rejects_the_other_edges()
    {
        var recognizer = new EdgeSwipeGestureRecognizer { Edge = EdgeSwipeEdge.Right };
        var (root, element, events) = Setup(recognizer);

        root.Input.DispatchPointer(Down(1, 5, 150, 0), element);   // 左边缘：不在配置范围内
        root.Input.DispatchPointer(Move(1, 50, 150, 30), element);
        root.Input.DispatchPointer(Up(1, 50, 150, 60), element);

        Assert.Empty(events);
    }

    [Fact]
    public void Touch_action_none_does_not_block_edge_swipe()
    {
        var recognizer = new EdgeSwipeGestureRecognizer();
        Assert.True(recognizer.AllowsTouchAction(CssTouchAction.None));
        Assert.True(recognizer.AllowsTouchAction(CssTouchAction.Auto));
    }

    [Fact]
    public void Edge_width_controls_the_band()
    {
        var recognizer = new EdgeSwipeGestureRecognizer { EdgeWidth = 40f };
        var (root, element, events) = Setup(recognizer);

        root.Input.DispatchPointer(Down(1, 30, 150, 0), element); // 默认 20 之外、40 之内
        root.Input.DispatchPointer(Move(1, 80, 150, 30), element);
        root.Input.DispatchPointer(Up(1, 80, 150, 60), element);

        var e = Assert.Single(events);
        Assert.Equal(EdgeSwipeEdge.Left, e.Edge);
    }
}

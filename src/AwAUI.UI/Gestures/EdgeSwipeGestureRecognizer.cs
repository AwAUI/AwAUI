using System.Numerics;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 边缘滑动识别器（design/09「可用性约束」的系统返回手势，<c>edgeswipe</c>）：按下位置落在屏幕边缘带内、
/// 且向视口内侧移动超过触摸容差后胜出，抬起时派发 <see cref="EdgeSwipeEvent"/>（冒泡，<c>OnEdgeSwipe</c>）。
/// <list type="bullet">
/// <item>边缘带按视口坐标计算：距离对应边缘不超过 <see cref="EdgeWidth"/>（默认 20 逻辑像素）。</item>
/// <item>默认四个边缘都接受，<see cref="Edge"/> 可限制为某一条（例如只做左边缘返回）；角上按左 / 右 / 上 / 下的顺序判定。</item>
/// <item>向内移动超过容差即胜出；向外拖动（或方向与边缘的向内方向相反）失败；抬起时才派发。</item>
/// <item>边缘手势与平台返回手势同类，<c>touch-action: none</c> 不阻止它入场。</item>
/// <item>不需要时钟：只按指针事件推进。</item>
/// </list>
/// </summary>
public sealed class EdgeSwipeGestureRecognizer : GestureRecognizer
{
    readonly VelocityTracker _tracker = new();
    Point _start;
    PointerType _pointerType = PointerType.Touch;
    EdgeSwipeEdge _edge = EdgeSwipeEdge.Left;
    bool _pointerDown;
    bool _began;

    /// <summary>边缘带宽度（逻辑像素，默认 20）：按下位置距边缘不超过它时进入竞技场。</summary>
    public float EdgeWidth { get; set; } = 20f;

    /// <summary>只接受这一条边缘；null（默认）接受四个边缘，由按下位置判定。</summary>
    public EdgeSwipeEdge? Edge { get; set; }

    /// <summary>只接受主按钮（触摸 / 笔的按下，或鼠标左键）。</summary>
    protected internal override bool AcceptsPointer(in PointerEvent down) =>
        ((down.Button & PointerButtons.Left) != 0 || down.Button == PointerButtons.None)
        && TryEdgeOf(down.Position, out _);

    /// <summary>边缘手势是系统返回手势：<c>touch-action</c> 不阻止它入场。</summary>
    internal override bool AllowsTouchAction(CssTouchAction action) => true;

    /// <summary>赢得竞技场不等于已开始：必须向内移动超过容差。</summary>
    protected internal override void OnAccepted(long pointerId) { }

    internal override void Reset()
    {
        base.Reset();
        _tracker.Reset();
        _pointerDown = false;
        _began = false;
    }

    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
    {
        switch (e.Type)
        {
            case "pointerdown":
                _pointerDown = true;
                _began = false;
                _start = e.Position;
                _pointerType = e.PointerType;
                _tracker.Reset();
                _tracker.AddSample(e.Timestamp, e.Position);
                if (!TryEdgeOf(e.Position, out _edge))
                {
                    // 视口在按下后变化等情况下兜底
                    State = GestureState.Failed;
                    entry.Reject();
                }
                break;

            case "pointermove":
                if (!_pointerDown) break;
                _tracker.AddSample(e.Timestamp, e.Position);
                if (State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                if (_began)
                {
                    State = GestureState.Changed;
                    break;
                }
                var total = e.Position - _start;
                float inward = InwardDistance(_edge, total);
                float slop = Settings.SlopFor(e.PointerType);
                if (inward > slop)
                {
                    _began = true;
                    State = GestureState.Began;
                    entry.Accept();
                }
                else if (inward <= 0f && total.Length > slop)
                {
                    // 方向不对（向外拖动或沿边缘平行移动）：失败
                    State = GestureState.Failed;
                    entry.Reject();
                }
                break;

            case "pointerup":
                if (!_pointerDown) break;
                _pointerDown = false;
                _tracker.AddSample(e.Timestamp, e.Position);
                if (!_began || State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended)
                {
                    if (State is not (GestureState.Failed or GestureState.Cancelled or GestureState.Ended))
                        State = GestureState.Failed;
                    break;
                }
                State = GestureState.Ended;
                Dispatch(e, e.Position - _start, _tracker.GetVelocity());
                break;

            case "pointercancel":
                _pointerDown = false;
                _began = false;
                State = GestureState.Cancelled;
                break;
        }
    }

    /// <summary>
    /// 按下位置是否落在（配置的）边缘带内。视口为零（没有帧管线）时没有边缘可言，返回 false。
    /// 角上按左、右、上、下的顺序判定，因此左上角算左边缘。
    /// </summary>
    bool TryEdgeOf(Point position, out EdgeSwipeEdge edge)
    {
        edge = EdgeSwipeEdge.Left;
        var viewport = Target?.Root?.ViewportSize ?? default;
        if (viewport.Width <= 0 || viewport.Height <= 0) return false;
        float width = EdgeWidth > 0 ? EdgeWidth : 0;
        EdgeSwipeEdge detected;
        if (position.X <= width) detected = EdgeSwipeEdge.Left;
        else if (position.X >= viewport.Width - width) detected = EdgeSwipeEdge.Right;
        else if (position.Y <= width) detected = EdgeSwipeEdge.Top;
        else if (position.Y >= viewport.Height - width) detected = EdgeSwipeEdge.Bottom;
        else return false;
        if (Edge is { } only && only != detected) return false;
        edge = detected;
        return true;
    }

    /// <summary>位移在边缘「向内」方向上的投影。</summary>
    static float InwardDistance(EdgeSwipeEdge edge, Point total) => edge switch
    {
        EdgeSwipeEdge.Left => total.X,
        EdgeSwipeEdge.Right => -total.X,
        EdgeSwipeEdge.Top => total.Y,
        _ => -total.Y,
    };

    void Dispatch(in PointerEvent e, Point distance, Vector2 velocity)
    {
        if (Target?.Root is not { } root) return;
        var edgeSwipe = new EdgeSwipeEvent
        {
            Edge = _edge,
            Position = e.Position,
            Distance = distance,
            Velocity = new Point(velocity.X, velocity.Y),
            PointerType = _pointerType,
            Timestamp = e.Timestamp,
        };
        root.Input.DispatchGesture(Target, edgeSwipe, EventKind.EdgeSwipe);
    }
}

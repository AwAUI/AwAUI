using System;
using System.Numerics;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 滑动识别器（design/21）：按下到抬起的位移超过容差，且抬起速度（<see cref="VelocityTracker"/>）超过
/// <see cref="MinVelocity"/>（默认 <see cref="GestureSettings.SwipeMinVelocity"/>）时胜出，派发 <see cref="SwipeEvent"/>
/// （冒泡，<c>OnSwipe</c>）。方向按位移的主导轴判定。
/// </summary>
public sealed class SwipeGestureRecognizer : GestureRecognizer
{
    readonly VelocityTracker _tracker = new();
    Point _start;
    PointerType _pointerType = PointerType.Touch;
    bool _pointerDown;
    bool _accepted;

    /// <summary>抬起时需要超过的最小速度（逻辑像素每秒）；null 时取 <see cref="GestureSettings.SwipeMinVelocity"/>。</summary>
    public float? MinVelocity { get; set; }

    /// <summary>判定为滑动所需的最小位移（逻辑像素）；null 时按 <see cref="GestureSettings.SlopFor"/> 选取。</summary>
    public float? MaxDistance { get; set; }

    /// <summary>只接受主按钮（触摸 / 笔的按下，或鼠标左键）。</summary>
    protected internal override bool AcceptsPointer(in PointerEvent down) =>
        (down.Button & PointerButtons.Left) != 0 || down.Button == PointerButtons.None;

    /// <summary>与拖动一样，<c>touch-action: none</c> 不允许滑动入场。</summary>
    internal override bool AllowsTouchAction(CssTouchAction action) => (action & CssTouchAction.None) == 0;

    protected internal override void OnAccepted(long pointerId)
    {
        _accepted = true;
        base.OnAccepted(pointerId);
    }

    internal override void Reset()
    {
        base.Reset();
        _tracker.Reset();
        _pointerDown = false;
        _accepted = false;
    }

    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
    {
        switch (e.Type)
        {
            case "pointerdown":
                _pointerDown = true;
                _accepted = false;
                _start = e.Position;
                _pointerType = e.PointerType;
                _tracker.Reset();
                _tracker.AddSample(e.Timestamp, e.Position);
                break;

            case "pointermove":
                if (!_pointerDown) break;
                _tracker.AddSample(e.Timestamp, e.Position);
                if (State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                if (_accepted) break;
                if (!ExceedsSlop(e.Position, e.PointerType)) break;
                // 移动中速度已足够时先赢下竞技场；抬起时再按最终速度确认
                if (Velocity().Length() >= MinVelocityValue())
                {
                    _accepted = true;
                    State = GestureState.Began;
                    entry.Accept();
                }
                break;

            case "pointerup":
            {
                if (!_pointerDown) break;
                _pointerDown = false;
                _tracker.AddSample(e.Timestamp, e.Position);
                if (State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                var distance = e.Position - _start;
                var velocity = Velocity();
                if (ExceedsSlop(e.Position, e.PointerType) && velocity.Length() >= MinVelocityValue())
                {
                    State = GestureState.Ended;
                    if (!_accepted) entry.Accept();
                    Dispatch(e, distance, velocity);
                }
                else
                {
                    State = GestureState.Failed;
                    entry.Reject();
                }
                break;
            }

            case "pointercancel":
                _pointerDown = false;
                State = GestureState.Cancelled;
                break;
        }
    }

    float MinVelocityValue() => MinVelocity ?? Settings.SwipeMinVelocity;

    Vector2 Velocity() => _tracker.GetVelocity();

    bool ExceedsSlop(Point position, PointerType type)
    {
        float slop = MaxDistance ?? Settings.SlopFor(type);
        var d = position - _start;
        return d.X * d.X + d.Y * d.Y > slop * slop;
    }

    void Dispatch(in PointerEvent e, Point distance, Vector2 velocity)
    {
        if (Target?.Root is not { } root) return;
        var swipe = new SwipeEvent
        {
            Direction = DirectionOf(distance),
            Velocity = new Point(velocity.X, velocity.Y),
            Distance = distance,
            Position = e.Position,
            PointerType = _pointerType,
            Timestamp = e.Timestamp,
        };
        root.Input.DispatchGesture(Target, swipe, EventKind.Swipe);
    }

    /// <summary>方向按位移的主导轴判定（水平位移更大时取左右，否则取上下）。</summary>
    static SwipeDirection DirectionOf(Point distance)
    {
        if (Math.Abs(distance.X) >= Math.Abs(distance.Y))
            return distance.X >= 0 ? SwipeDirection.Right : SwipeDirection.Left;
        return distance.Y >= 0 ? SwipeDirection.Down : SwipeDirection.Up;
    }
}

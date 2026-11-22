using System;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>拖动识别器允许的轴向。</summary>
public enum PanAxis : byte
{
    /// <summary>两个方向都可拖动（默认）。</summary>
    Both,

    /// <summary>只允许水平拖动。</summary>
    X,

    /// <summary>只允许垂直拖动。</summary>
    Y,
}

/// <summary>
/// 拖动识别器：移动超过触摸容差后胜出，随后按增量派发 <see cref="PanEvent"/>（冒泡，<c>OnPan</c>），
/// 并通过 <see cref="Started"/> / <see cref="Changed"/> / <see cref="Ended"/> / <see cref="Cancelled"/> 报告阶段。
/// <list type="bullet">
/// <item>容差取自 <see cref="MaxDistance"/>，为 null 时按 <see cref="GestureSettings.SlopFor"/> 选取。</item>
/// <item>方向锁定：两轴位移相差超过 <see cref="AxisLockRatio"/> 倍时只保留主导轴；<see cref="Axis"/> 可强制单轴。</item>
/// <item>速度由 <see cref="VelocityTracker"/> 估算（逻辑像素每秒）。</item>
/// <item>胜出时捕获指针，拖动离开元素后仍收到 move。</item>
/// </list>
/// </summary>
public sealed class PanGestureRecognizer : GestureRecognizer
{
    readonly VelocityTracker _tracker = new();
    Point _start;
    Point _last;
    bool _began;
    bool _lockX;
    bool _lockY;

    /// <summary>开始拖动所需的最大位移（逻辑像素）；null 时按 <see cref="GestureSettings.SlopFor"/> 选取。</summary>
    public float? MaxDistance { get; set; }

    /// <summary>允许的拖动轴向（默认 <see cref="PanAxis.Both"/>）。</summary>
    public PanAxis Axis { get; set; } = PanAxis.Both;

    /// <summary>方向锁定比；null 时取 <see cref="GestureSettings.AxisLockRatio"/>。主导轴的位移超过另一轴的这个倍数时锁定。</summary>
    public float? AxisLockRatio { get; set; }

    /// <summary>是否按主导轴锁定（默认 true）。为 false 时两轴增量都保留。</summary>
    public bool LockAxis { get; set; } = true;

    /// <summary>开始拖动（超过容差、方向确定）。</summary>
    public event Action<PanEvent>? Started;

    /// <summary>拖动中，<see cref="PanEvent.Delta"/> 为相对上一次的位移。</summary>
    public event Action<PanEvent>? Changed;

    /// <summary>抬起结束。</summary>
    public event Action<PanEvent>? Ended;

    /// <summary>被取消（<c>pointercancel</c>）。</summary>
    public event Action<PanEvent>? Cancelled;

    protected internal override bool AcceptsPointer(in PointerEvent down) =>
        (down.Button & PointerButtons.Left) != 0 || down.Button == PointerButtons.None;

    internal override bool AllowsTouchAction(CssTouchAction action) => (action & CssTouchAction.None) == 0;

    /// <summary>胜出只表示赢得竞技场；真正开始拖动要等移动超过容差，所以不推进状态。</summary>
    protected internal override void OnAccepted(long pointerId) { }

    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
    {
        switch (e.Type)
        {
            case "pointerdown":
                _start = _last = e.Position;
                _began = false;
                _lockX = _lockY = false;
                _tracker.Reset();
                _tracker.AddSample(e.Timestamp, e.Position);
                break;

            case "pointermove":
                _tracker.AddSample(e.Timestamp, e.Position);
                if (State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                if (!_began)
                {
                    var total = e.Position - _start;
                    float slop = MaxDistance ?? Settings.SlopFor(e.PointerType);
                    if (total.X * total.X + total.Y * total.Y <= slop * slop)
                    {
                        _last = e.Position;
                        break;
                    }
                    if (!ResolveAxis(total, entry)) break;
                    entry.Accept();
                    _began = true;
                    State = GestureState.Began;
                    _last = e.Position;
                    // 拖动开始才捕获指针：普通点击（未越过容差）不改变 click 的目标
                    Target?.SetPointerCapture(e.PointerId);
                    Dispatch(PanPhase.Started, e, Point.Zero, LockedTotal(e.Position), _tracker.GetVelocity());
                    break;
                }
                {
                    var delta = e.Position - _last;
                    _last = e.Position;
                    State = GestureState.Changed;
                    Dispatch(PanPhase.Changed, e, new Point(_lockX ? 0 : delta.X, _lockY ? 0 : delta.Y),
                        LockedTotal(e.Position), _tracker.GetVelocity());
                }
                break;

            case "pointerup":
                if (!_began || State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                _tracker.AddSample(e.Timestamp, e.Position);
                State = GestureState.Ended;
                Dispatch(PanPhase.Ended, e, Point.Zero, LockedTotal(e.Position), _tracker.GetVelocity());
                Target?.ReleasePointerCapture(e.PointerId);
                break;

            case "pointercancel":
                if (_began && State is not (GestureState.Failed or GestureState.Cancelled or GestureState.Ended))
                {
                    State = GestureState.Cancelled;
                    Dispatch(PanPhase.Cancelled, e, Point.Zero, LockedTotal(e.Position), default);
                }
                else
                {
                    State = GestureState.Cancelled;
                }
                Target?.ReleasePointerCapture(e.PointerId);
                break;
        }
    }

    /// <summary>根据 <see cref="Axis"/> 与 <c>touch-action</c> 决定是否入场；返回 false 表示退出竞技场。</summary>
    bool ResolveAxis(Point total, ArenaEntry entry)
    {
        bool allowX = Axis != PanAxis.Y;
        bool allowY = Axis != PanAxis.X;
        var action = TouchAction;
        if (action is not (CssTouchAction.Auto or CssTouchAction.Manipulation))
        {
            bool touchX = (action & (CssTouchAction.PanX | CssTouchAction.PanLeft | CssTouchAction.PanRight)) != 0;
            bool touchY = (action & (CssTouchAction.PanY | CssTouchAction.PanUp | CssTouchAction.PanDown)) != 0;
            allowX &= touchX;
            allowY &= touchY;
        }
        if (!allowX && !allowY)
        {
            State = GestureState.Failed;
            entry.Reject();
            return false;
        }
        // 只允许单轴时，主导方向必须是允许的轴，否则退出（例如 touch-action: pan-x 下的竖直滑动）
        if (allowX != allowY)
        {
            float absX = Math.Abs(total.X), absY = Math.Abs(total.Y);
            if ((!allowX && absX > absY) || (!allowY && absY > absX))
            {
                State = GestureState.Failed;
                entry.Reject();
                return false;
            }
        }
        _lockX = !allowX;
        _lockY = !allowY;
        if (LockAxis && allowX && allowY)
        {
            float ratio = AxisLockRatio ?? Settings.AxisLockRatio;
            _lockX = Math.Abs(total.Y) > ratio * Math.Abs(total.X);
            _lockY = Math.Abs(total.X) > ratio * Math.Abs(total.Y);
        }
        return true;
    }

    Point LockedTotal(Point position)
    {
        var total = position - _start;
        return new Point(_lockX ? 0 : total.X, _lockY ? 0 : total.Y);
    }

    void Dispatch(PanPhase phase, in PointerEvent e, Point delta, Point total, Point velocity)
    {
        var pan = new PanEvent
        {
            Position = e.Position,
            Delta = delta,
            Total = total,
            Velocity = velocity,
            Timestamp = e.Timestamp,
        };
        switch (phase)
        {
            case PanPhase.Started: Started?.Invoke(pan); break;
            case PanPhase.Changed: Changed?.Invoke(pan); break;
            case PanPhase.Ended: Ended?.Invoke(pan); break;
            case PanPhase.Cancelled: Cancelled?.Invoke(pan); break;
        }
        // 高层 pan 事件（OnPan）：每个阶段都派发，阶段由上面的回调区分（PanEvent 没有 Phase）
        if (Target?.Root is { } root)
            root.Input.DispatchGesture(Target, pan, EventKind.Pan);
    }

    enum PanPhase : byte
    {
        Started,
        Changed,
        Ended,
        Cancelled,
    }
}

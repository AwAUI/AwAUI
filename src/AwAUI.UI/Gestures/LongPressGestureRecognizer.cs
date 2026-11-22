using System;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 长按识别器（design/21）：按住超过 <see cref="Duration"/>（默认 <see cref="GestureSettings.LongPressDuration"/>）且位移未超出容差时胜出，
/// 派发 <see cref="LongPressEvent"/>（冒泡，<c>OnLongPress</c>）。
/// <list type="bullet">
/// <item>开始后指针移动派发 <see cref="LongPressPhase.Moved"/>，抬起 <see cref="LongPressPhase.Ended"/>，取消 <see cref="LongPressPhase.Cancelled"/>。</item>
/// <item>超时用 <see cref="UIRoot.Clock"/> 定时器；没有时钟时在每次移动 / 抬起时按事件时间戳判定，不会永久卡住。</item>
/// <item>长按不是 CSS 默认手势，<c>touch-action: none</c> 不会阻止它入场。</item>
/// </list>
/// </summary>
public sealed class LongPressGestureRecognizer : GestureRecognizer
{
    IDisposable? _timer;
    ArenaEntry _entry;
    Point _start;
    Point _last;
    TimeSpan _downTimestamp;
    TimeSpan _lastTimestamp;
    PointerType _pointerType = PointerType.Touch;
    bool _pointerDown;
    bool _began;
    bool _activating;

    /// <summary>长按所需时长；null 时取 <see cref="GestureSettings.LongPressDuration"/>。</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>允许的最大位移（逻辑像素）；null 时按 <see cref="GestureSettings.SlopFor"/> 选取。</summary>
    public float? MaxDistance { get; set; }

    /// <summary>只接受主按钮（触摸 / 笔的按下，或鼠标左键）。</summary>
    protected internal override bool AcceptsPointer(in PointerEvent down) =>
        (down.Button & PointerButtons.Left) != 0 || down.Button == PointerButtons.None;

    /// <summary>长按与 <c>touch-action</c> 无关（它不是 CSS 默认手势）。</summary>
    internal override bool AllowsTouchAction(CssTouchAction action) => true;

    /// <summary>赢得竞技场不等于开始长按：必须等时长到达后由定时器 / 时间戳判定。</summary>
    protected internal override void OnAccepted(long pointerId)
    {
        if (State == GestureState.Possible) State = GestureState.Began;
        // 因 RequireFailureOf 被推迟到时长之后才获准时，补上已到期的长按
        if (State == GestureState.Began && _pointerDown && !_began)
        {
            var duration = Duration ?? Settings.LongPressDuration;
            if (_lastTimestamp - _downTimestamp >= duration) Activate(_lastTimestamp);
        }
    }

    protected internal override void OnRejected(long pointerId)
    {
        CancelTimer();
        _pointerDown = false;
        base.OnRejected(pointerId);
    }

    internal override void Reset()
    {
        base.Reset();
        CancelTimer();
        _pointerDown = false;
        _began = false;
    }

    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
    {
        switch (e.Type)
        {
            case "pointerdown":
                _entry = entry;
                _pointerDown = true;
                _began = false;
                _start = _last = e.Position;
                _downTimestamp = _lastTimestamp = e.Timestamp;
                _pointerType = e.PointerType;
                ScheduleTimer();
                break;

            case "pointermove":
                if (!_pointerDown || State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                _last = e.Position;
                _lastTimestamp = e.Timestamp;
                if (!_began)
                {
                    if (OutsideSlop(e.Position, e.PointerType))
                    {
                        Fail(entry);
                        break;
                    }
                    // 没有时钟时的回退：按住够久就在移动事件上开始
                    MaybeExpire(e.Timestamp);
                    break;
                }
                State = GestureState.Changed;
                Dispatch(LongPressPhase.Moved, e.Position, e.Timestamp);
                break;

            case "pointerup":
                if (_pointerDown)
                {
                    _last = e.Position;
                    _lastTimestamp = e.Timestamp;
                    if (!_began)
                    {
                        if (!OutsideSlop(e.Position, e.PointerType)) MaybeExpire(e.Timestamp);
                        if (!_began) State = GestureState.Failed;
                    }
                    if (_began)
                    {
                        State = GestureState.Ended;
                        Dispatch(LongPressPhase.Ended, e.Position, e.Timestamp);
                    }
                }
                _pointerDown = false;
                CancelTimer();
                break;

            case "pointercancel":
                _pointerDown = false;
                CancelTimer();
                if (_began) Dispatch(LongPressPhase.Cancelled, _last, e.Timestamp);
                State = GestureState.Cancelled;
                break;
        }
    }

    void ScheduleTimer()
    {
        var clock = Target?.Root?.Clock;
        if (clock is null) return;
        CancelTimer();
        var duration = Duration ?? Settings.LongPressDuration;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        _timer = clock.Schedule(duration, () =>
        {
            _timer = null;
            if (!_pointerDown) return;
            Activate(_downTimestamp + duration);
            if (!_began) CancelTimer();
        });
    }

    void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>没有时钟时的回退：事件时间戳距按下已超过时长则开始。</summary>
    void MaybeExpire(TimeSpan timestamp)
    {
        if (_began) return;
        var duration = Duration ?? Settings.LongPressDuration;
        if (timestamp - _downTimestamp < duration) return;
        Activate(timestamp);
    }

    void Activate(TimeSpan timestamp)
    {
        if (_activating || _began || !_pointerDown) return;
        if (State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) return;
        if (OutsideSlop(_last, _pointerType))
        {
            State = GestureState.Failed;
            _entry.Reject();
            return;
        }
        _activating = true;
        try
        {
            if (State != GestureState.Began) _entry.Accept();
            // RequireFailureOf 仍阻止胜出时保持可能（状态不变），等对方失败后再由竞技场裁决
            if (State != GestureState.Began) return;
            _began = true;
            State = GestureState.Began;
            Dispatch(LongPressPhase.Started, _last, timestamp);
        }
        finally
        {
            _activating = false;
        }
    }

    void Fail(ArenaEntry entry)
    {
        State = GestureState.Failed;
        _pointerDown = false;
        CancelTimer();
        entry.Reject();
    }

    bool OutsideSlop(Point position, PointerType type)
    {
        float slop = MaxDistance ?? Settings.SlopFor(type);
        var d = position - _start;
        return d.X * d.X + d.Y * d.Y > slop * slop;
    }

    void Dispatch(LongPressPhase phase, Point position, TimeSpan timestamp)
    {
        if (Target?.Root is not { } root) return;
        var e = new LongPressEvent
        {
            Position = position,
            Phase = phase,
            PointerType = _pointerType,
            Timestamp = timestamp,
        };
        root.Input.DispatchGesture(Target, e, EventKind.LongPress);
    }
}

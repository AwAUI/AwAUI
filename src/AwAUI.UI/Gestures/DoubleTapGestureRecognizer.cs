using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 双击识别器（design/21）：第一次点按抬起后，在 <see cref="Interval"/>（默认 <see cref="GestureSettings.DoubleTapInterval"/>）与
/// <see cref="MaxDistance"/>（默认 <see cref="GestureSettings.DoubleTapDistance"/>）内再次按下并抬起时胜出，
/// 派发 <see cref="DoubleTapEvent"/>（冒泡，<c>OnDoubleTap</c>）。
/// <list type="bullet">
/// <item>每次按下都通过 <see cref="ArenaEntry.Hold"/> 延后裁决，等第二次点按或超时；超时后一次点按判为失败。</item>
/// <item>超时用 <see cref="UIRoot.Clock"/> 定时器；没有时钟时在下一次按下时按事件时间戳判定（不会永久卡住）。</item>
/// <item>不改变单击 <see cref="TapGestureRecognizer"/> 的行为；需要「单击等双击失败」时由使用方调用 <see cref="GestureRecognizer.RequireFailureOf"/>。</item>
/// </list>
/// </summary>
public sealed class DoubleTapGestureRecognizer : GestureRecognizer
{
    IDisposable? _timer;
    ArenaEntry _firstEntry;
    bool _hasFirstEntry;
    bool _hasFirst;
    bool _awaitingSecond;
    bool _pointerDown;
    Point _downPosition;
    Point _firstUpPosition;
    TimeSpan _firstUpTimestamp;
    PointerType _pointerType = PointerType.Touch;

    /// <summary>两次点按的最大间隔；null 时取 <see cref="GestureSettings.DoubleTapInterval"/>。</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>两次按下位置之间的最大距离（逻辑像素）；null 时取 <see cref="GestureSettings.DoubleTapDistance"/>。</summary>
    public float? MaxDistance { get; set; }

    /// <summary>只接受主按钮（触摸 / 笔的按下，或鼠标左键）。</summary>
    protected internal override bool AcceptsPointer(in PointerEvent down) =>
        (down.Button & PointerButtons.Left) != 0 || down.Button == PointerButtons.None;

    internal override void Reset()
    {
        base.Reset();
        _pointerDown = false;
        _awaitingSecond = false;
        // 第一次点按的待定状态保留：紧接着的按下可能就是第二次点按
    }

    protected internal override void OnRejected(long pointerId)
    {
        // 相邻竞技场（第一次点按）的裁决结果不应打断第二次点按
        CancelTimer();
        _hasFirst = false;
        base.OnRejected(pointerId);
    }

    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
    {
        switch (e.Type)
        {
            case "pointerdown":
            {
                _pointerDown = true;
                _downPosition = e.Position;
                _pointerType = e.PointerType;
                var interval = Interval ?? Settings.DoubleTapInterval;
                float maxDistance = MaxDistance ?? Settings.DoubleTapDistance;
                bool second = _hasFirst
                    && e.Timestamp - _firstUpTimestamp <= interval
                    && InRange(e.Position, _firstUpPosition, maxDistance);
                if (second)
                {
                    _awaitingSecond = true;
                    _hasFirst = false;
                    CancelTimer();
                    // 第一次点按的竞技场仍在延后：释放它，避免残留
                    if (_hasFirstEntry)
                    {
                        _firstEntry.Release();
                        _hasFirstEntry = false;
                    }
                }
                else
                {
                    CancelTimer();
                    _hasFirst = false;
                    _awaitingSecond = false;
                }
                // 延后裁决：等第二次点按或超时
                entry.Hold();
                break;
            }

            case "pointermove":
                if (!_pointerDown) break;
                if (!InRange(e.Position, _downPosition, MaxDistance ?? Settings.SlopFor(e.PointerType)))
                    Fail(entry);
                break;

            case "pointerup":
                if (!_pointerDown) break;
                _pointerDown = false;
                if (!InRange(e.Position, _downPosition, MaxDistance ?? Settings.SlopFor(e.PointerType)))
                {
                    Fail(entry);
                    break;
                }
                if (_awaitingSecond)
                {
                    _awaitingSecond = false;
                    _hasFirst = false;
                    CancelTimer();
                    entry.Accept();
                    State = GestureState.Ended;
                    Dispatch(e);
                }
                else
                {
                    _hasFirst = true;
                    _firstUpTimestamp = e.Timestamp;
                    _firstUpPosition = e.Position;
                    _pointerType = e.PointerType;
                    _firstEntry = entry;
                    _hasFirstEntry = true;
                    ScheduleTimer();
                }
                break;

            case "pointercancel":
                _pointerDown = false;
                _hasFirst = false;
                _awaitingSecond = false;
                CancelTimer();
                if (_hasFirstEntry)
                {
                    _firstEntry.Reject();
                    _hasFirstEntry = false;
                }
                State = GestureState.Cancelled;
                break;
        }
    }

    void ScheduleTimer()
    {
        var clock = Target?.Root?.Clock;
        if (clock is null) return;
        CancelTimer();
        var interval = Interval ?? Settings.DoubleTapInterval;
        if (interval < TimeSpan.Zero) interval = TimeSpan.Zero;
        _timer = clock.Schedule(interval, () =>
        {
            _timer = null;
            if (!_hasFirst) return;
            _hasFirst = false;
            State = GestureState.Failed;
            if (_hasFirstEntry)
            {
                var entry = _firstEntry;
                _hasFirstEntry = false;
                // 让同一竞技场里的其它识别器（例如要求双击失败的单击）继续裁决
                entry.Reject();
            }
        });
    }

    void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    void Fail(ArenaEntry entry)
    {
        State = GestureState.Failed;
        _pointerDown = false;
        _hasFirst = false;
        _awaitingSecond = false;
        CancelTimer();
        if (_hasFirstEntry)
        {
            _firstEntry.Reject();
            _hasFirstEntry = false;
        }
        entry.Reject();
    }

    static bool InRange(Point a, Point b, float distance)
    {
        var d = a - b;
        return d.X * d.X + d.Y * d.Y <= distance * distance;
    }

    void Dispatch(in PointerEvent e)
    {
        if (Target?.Root is not { } root) return;
        var tap = new DoubleTapEvent
        {
            Position = e.Position,
            PointerType = _pointerType,
            Timestamp = e.Timestamp,
        };
        root.Input.DispatchGesture(Target, tap, EventKind.DoubleTap);
    }
}

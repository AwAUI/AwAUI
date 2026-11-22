using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 点按识别器：按下后在触摸容差内抬起即触发 <see cref="TapEvent"/>（冒泡，<c>OnTap</c>）。
/// 容差取自 <see cref="MaxDistance"/>，为 null 时按本次序列的手势设置（默认为 <see cref="GestureSettings.Default"/>）与指针类型选取。
/// <para>目前是单次点按；多击计数（双击、三击）尚未实现（见 design/21 的 DoubleTap 行）。</para>
/// </summary>
public sealed class TapGestureRecognizer : GestureRecognizer
{
    Point _start;
    PointerType _pointerType = PointerType.Touch;

    /// <summary>允许的最大位移（逻辑像素）；null 时按 <see cref="GestureSettings.SlopFor"/> 选取。</summary>
    public float? MaxDistance { get; set; }

    /// <summary>写入 <see cref="TapEvent.TapCount"/> 的点击次数（默认 1）。</summary>
    public int TapCount { get; set; } = 1;

    /// <summary>只接受主按钮（触摸 / 笔的按下，或鼠标左键）。</summary>
    protected internal override bool AcceptsPointer(in PointerEvent down) =>
        (down.Button & PointerButtons.Left) != 0 || down.Button == PointerButtons.None;

    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry)
    {
        switch (e.Type)
        {
            case "pointerdown":
                _start = e.Position;
                _pointerType = e.PointerType;
                break;

            case "pointermove":
                if (State is GestureState.Failed or GestureState.Cancelled or GestureState.Ended) break;
                if (OutsideSlop(e.Position, e.PointerType))
                {
                    State = GestureState.Failed;
                    entry.Reject();
                }
                break;

            case "pointerup":
                if (State is not (GestureState.Began or GestureState.Changed)) break;
                if (OutsideSlop(e.Position, e.PointerType))
                {
                    State = GestureState.Failed;
                    entry.Reject();
                    break;
                }
                State = GestureState.Ended;
                Dispatch(e);
                break;

            case "pointercancel":
                State = GestureState.Cancelled;
                break;
        }
    }

    bool OutsideSlop(Point position, PointerType type)
    {
        float slop = MaxDistance ?? Settings.SlopFor(type);
        var d = position - _start;
        return d.X * d.X + d.Y * d.Y > slop * slop;
    }

    void Dispatch(in PointerEvent e)
    {
        if (Target?.Root is not { } root) return;
        var tap = new TapEvent
        {
            Position = e.Position,
            PointerType = _pointerType,
            TapCount = TapCount,
            Timestamp = e.Timestamp,
        };
        root.Input.DispatchGesture(Target, tap, EventKind.Tap);
    }
}

using System;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 捏合识别器（design/21）：观察同时落在命中路径上的两个指针，两指距离相对初始值变化超过
/// <see cref="ScaleThreshold"/>（默认 <see cref="GestureSettings.PinchScaleThreshold"/>）后开始，
/// 派发 <see cref="PinchEvent"/>（冒泡，<c>OnPinch</c>）；两指中点为 <see cref="PinchEvent.Focus"/>。
/// <para>
/// 这是多指识别器（<see cref="UsesAllPointers"/>），不进入单指针竞技场；第二根手指不会重置同一元素上的 Tap / Pan 竞技场。
/// <c>touch-action: none</c> 禁止捏合，<c>pinch-zoom</c> / <c>manipulation</c>（以及 <c>auto</c>）允许。
/// </para>
/// </summary>
public sealed class PinchGestureRecognizer : GestureRecognizer
{
    bool _tracking;
    bool _began;
    Point _first;
    Point _second;
    float _startDistance;
    float _lastScale = 1f;

    /// <summary>判为捏合所需的缩放比变化；null 时取 <see cref="GestureSettings.PinchScaleThreshold"/>。</summary>
    public float? ScaleThreshold { get; set; }

    internal override bool UsesAllPointers => true;

    /// <summary><c>touch-action: none</c> 禁止捏合；只有 <c>auto</c>、<c>pinch-zoom</c> 或 <c>manipulation</c> 允许。</summary>
    internal override bool AllowsTouchAction(CssTouchAction action) =>
        action == CssTouchAction.Auto || (action & (CssTouchAction.PinchZoom | CssTouchAction.Manipulation)) != 0;

    /// <summary>多指识别器不处理单指针事件。</summary>
    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry) { }

    internal override void Reset()
    {
        base.Reset();
        _tracking = false;
        _began = false;
        _startDistance = 0f;
        _lastScale = 1f;
    }

    internal override void HandleMultiPointer(in MultiPointerEvent e)
    {
        switch (e.Type)
        {
            case "pointerdown":
                if (_tracking) return; // 第三根手指：保持当前两指
                _tracking = true;
                _began = false;
                _first = e.First.Position;
                _second = e.Second.Position;
                _startDistance = Distance(_first, _second);
                _lastScale = 1f;
                State = GestureState.Possible;
                break;

            case "pointermove":
                if (!_tracking) return;
                _first = e.First.Position;
                _second = e.Second.Position;
                float distance = Distance(_first, _second);
                if (_startDistance <= 1e-4f)
                {
                    // 两指从同一点出发：以第一次分开的距离为基准
                    _startDistance = distance;
                    return;
                }
                float scale = distance / _startDistance;
                if (!_began)
                {
                    if (Math.Abs(scale - 1f) < Threshold())
                    {
                        _lastScale = scale;
                        return;
                    }
                    _began = true;
                    _lastScale = scale;
                    State = GestureState.Began;
                    Dispatch(GesturePhase.Started, scale, scale - 1f, e.Focus, e.Changed.PointerType, e.Timestamp);
                    return;
                }
                float delta = scale - _lastScale;
                _lastScale = scale;
                State = GestureState.Changed;
                Dispatch(GesturePhase.Changed, scale, delta, e.Focus, e.Changed.PointerType, e.Timestamp);
                break;

            case "pointerup":
                if (!_tracking) return;
                _first = e.First.Position;
                _second = e.Second.Position;
                if (_began)
                {
                    State = GestureState.Ended;
                    Dispatch(GesturePhase.Ended, _lastScale, 0f, e.Focus, e.Changed.PointerType, e.Timestamp);
                }
                else
                {
                    State = GestureState.Failed;
                }
                _tracking = false;
                _began = false;
                break;

            case "pointercancel":
                _first = e.First.Position;
                _second = e.Second.Position;
                if (_tracking && _began)
                    Dispatch(GesturePhase.Cancelled, _lastScale, 0f, e.Focus, e.Changed.PointerType, e.Timestamp);
                _tracking = false;
                _began = false;
                State = GestureState.Cancelled;
                break;
        }
    }

    float Threshold()
    {
        float threshold = ScaleThreshold ?? Settings.PinchScaleThreshold;
        return threshold < 0f ? 0f : threshold;
    }

    static float Distance(Point a, Point b)
    {
        var d = b - a;
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y);
    }

    void Dispatch(GesturePhase phase, float scale, float deltaScale, Point focus, PointerType pointerType, TimeSpan timestamp)
    {
        if (Target?.Root is not { } root) return;
        var e = new PinchEvent
        {
            Scale = scale,
            DeltaScale = deltaScale,
            Focus = focus,
            Phase = phase,
            PointerType = pointerType,
            Timestamp = timestamp,
        };
        root.Input.DispatchGesture(Target, e, EventKind.Pinch);
    }
}

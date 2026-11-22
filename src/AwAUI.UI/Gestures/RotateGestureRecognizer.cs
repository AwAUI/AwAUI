using System;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 旋转识别器（design/21）：观察同时落在命中路径上的两个指针，两指连线角度相对初始值变化超过
/// <see cref="AngleThreshold"/>（度，默认 <see cref="GestureSettings.RotateAngleThreshold"/>）后开始，
/// 派发 <see cref="RotateEvent"/>（冒泡，<c>OnRotate</c>）；两指中点为 <see cref="RotateEvent.Focus"/>。
/// <para>
/// 这是多指识别器（<see cref="UsesAllPointers"/>），不进入单指针竞技场；<c>touch-action: none</c> 禁止，
/// <c>pinch-zoom</c> / <c>manipulation</c>（以及 <c>auto</c>）允许。
/// </para>
/// </summary>
public sealed class RotateGestureRecognizer : GestureRecognizer
{
    bool _tracking;
    bool _began;
    Point _first;
    Point _second;
    float _lastAngle;
    float _accumulatedDegrees;

    /// <summary>判为旋转所需的角度变化（度）；null 时取 <see cref="GestureSettings.RotateAngleThreshold"/>。</summary>
    public float? AngleThreshold { get; set; }

    internal override bool UsesAllPointers => true;

    /// <summary><c>touch-action: none</c> 禁止旋转；只有 <c>auto</c>、<c>pinch-zoom</c> 或 <c>manipulation</c> 允许。</summary>
    internal override bool AllowsTouchAction(CssTouchAction action) =>
        action == CssTouchAction.Auto || (action & (CssTouchAction.PinchZoom | CssTouchAction.Manipulation)) != 0;

    /// <summary>多指识别器不处理单指针事件。</summary>
    protected internal override void HandleEvent(in PointerEvent e, ArenaEntry entry) { }

    internal override void Reset()
    {
        base.Reset();
        _tracking = false;
        _began = false;
        _lastAngle = 0f;
        _accumulatedDegrees = 0f;
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
                _lastAngle = Angle(_first, _second);
                _accumulatedDegrees = 0f;
                State = GestureState.Possible;
                break;

            case "pointermove":
                if (!_tracking) return;
                _first = e.First.Position;
                _second = e.Second.Position;
                float angle = Angle(_first, _second);
                float delta = NormalizeDegrees((angle - _lastAngle) * (180f / MathF.PI));
                _lastAngle = angle;
                _accumulatedDegrees += delta;
                if (!_began)
                {
                    if (Math.Abs(_accumulatedDegrees) < Threshold())
                        return;
                    _began = true;
                    State = GestureState.Began;
                    Dispatch(GesturePhase.Started, _accumulatedDegrees, _accumulatedDegrees, e.Focus, e.Changed.PointerType, e.Timestamp);
                    return;
                }
                State = GestureState.Changed;
                Dispatch(GesturePhase.Changed, _accumulatedDegrees, delta, e.Focus, e.Changed.PointerType, e.Timestamp);
                break;

            case "pointerup":
                if (!_tracking) return;
                _first = e.First.Position;
                _second = e.Second.Position;
                if (_began)
                {
                    State = GestureState.Ended;
                    Dispatch(GesturePhase.Ended, _accumulatedDegrees, 0f, e.Focus, e.Changed.PointerType, e.Timestamp);
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
                    Dispatch(GesturePhase.Cancelled, _accumulatedDegrees, 0f, e.Focus, e.Changed.PointerType, e.Timestamp);
                _tracking = false;
                _began = false;
                State = GestureState.Cancelled;
                break;
        }
    }

    float Threshold()
    {
        float threshold = AngleThreshold ?? Settings.RotateAngleThreshold;
        return threshold < 0f ? 0f : threshold;
    }

    static float Angle(Point a, Point b) => MathF.Atan2(b.Y - a.Y, b.X - a.X);

    /// <summary>把角度增量折算到 (-180, 180] 度，避免两指连线跨越 ±π 时出现 360 度跳变。</summary>
    static float NormalizeDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        else if (degrees <= -180f) degrees += 360f;
        return degrees;
    }

    void Dispatch(GesturePhase phase, float angleDegrees, float deltaDegrees, Point focus, PointerType pointerType, TimeSpan timestamp)
    {
        if (Target?.Root is not { } root) return;
        var e = new RotateEvent
        {
            AngleDegrees = angleDegrees,
            DeltaDegrees = deltaDegrees,
            Focus = focus,
            Phase = phase,
            PointerType = pointerType,
            Timestamp = timestamp,
        };
        root.Input.DispatchGesture(Target, e, EventKind.Rotate);
    }
}

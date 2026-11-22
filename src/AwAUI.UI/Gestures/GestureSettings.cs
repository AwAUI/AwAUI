using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 手势参数（design/21「参数」）。<see cref="Default"/> 是全局默认值；在组件里用 <see cref="Context.Provide{T}"/>
/// 提供另一份 <see cref="GestureSettings"/> 可以按子树覆盖，识别器在每次按下时取最近的设置（只影响该次指针序列）。
/// 滚动的拖动识别用到触摸容差与惯性速度。
/// </summary>
public sealed class GestureSettings
{
    /// <summary>默认参数。</summary>
    public static GestureSettings Default { get; } = new();

    /// <summary>触摸 / 笔的移动容差（逻辑像素）：按下后移动超过它才可能成为拖动或滚动。</summary>
    public float TouchSlop { get; init; } = 8f;

    /// <summary>鼠标的移动容差（逻辑像素）。</summary>
    public float MouseSlop { get; init; } = 2f;

    /// <summary>某一轴的位移超过另一轴的这个倍数时锁定到该轴（方向锁定）。</summary>
    public float AxisLockRatio { get; init; } = 2f;

    /// <summary>长按的判定时长（默认 500 ms）：按住超过它且位移未超出容差即触发长按。</summary>
    public TimeSpan LongPressDuration { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>双击允许的最大间隔（默认 300 ms）。</summary>
    public TimeSpan DoubleTapInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>双击两次按下位置之间的最大距离（逻辑像素，默认 40）。</summary>
    public float DoubleTapDistance { get; init; } = 40f;

    /// <summary>滑动（Swipe）在抬起时需要超过的最小速度（逻辑像素每秒，默认 500）。</summary>
    public float SwipeMinVelocity { get; init; } = 500f;

    /// <summary>捏合（Pinch）的缩放比变化阈值（默认 0.05，即 5%）超过后判为捏合。</summary>
    public float PinchScaleThreshold { get; init; } = 0.05f;

    /// <summary>旋转（Rotate）的角度变化阈值（度，默认 5）。</summary>
    public float RotateAngleThreshold { get; init; } = 5f;

    /// <summary>指针类型对应的移动容差。</summary>
    public float SlopFor(PointerType type) => type == PointerType.Mouse ? MouseSlop : TouchSlop;
}

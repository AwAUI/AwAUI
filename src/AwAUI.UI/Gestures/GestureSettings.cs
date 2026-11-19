using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 手势参数（design/21「参数」）。目前是全局默认值（平台设置与按子树覆盖尚未接入）；滚动的拖动识别用到触摸容差与惯性速度。
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

    /// <summary>指针类型对应的移动容差。</summary>
    public float SlopFor(PointerType type) => type == PointerType.Mouse ? MouseSlop : TouchSlop;
}

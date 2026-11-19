using AwAUI.Platform;

namespace AwAUI.MacOS;

/// <summary>
/// <c>scrollWheel:</c> 的纯逻辑（与 AppKit 无关，放在 Input/ 目录里以便 <c>tests/AwAUI.Platform.UnitTests</c> 以源码链接方式测试）：
/// NSEventPhase → <see cref="ScrollPhase"/>，增量换算。
/// </summary>
internal static class MacWheel
{
    /// <summary>非精确滚轮每「行」的逻辑像素（WebKit 的 pixelsPerLineStep；系统给出的行数已含加速）。</summary>
    public const float PixelsPerLine = 40f;

    // NSEventPhase 位
    const ulong PhaseBegan = 1, PhaseStationary = 2, PhaseChanged = 4, PhaseEnded = 8, PhaseCancelled = 16, PhaseMayBegin = 32;

    /// <summary>NSEventPhase（<c>phase</c> / <c>momentumPhase</c>）→ 框架相位。Stationary（手指停在板上）按 Changed（增量为 0）处理。</summary>
    public static ScrollPhase MapPhase(ulong phase)
    {
        if ((phase & PhaseMayBegin) != 0) return ScrollPhase.MayBegin;
        if ((phase & PhaseBegan) != 0) return ScrollPhase.Began;
        if ((phase & PhaseCancelled) != 0) return ScrollPhase.Cancelled;
        if ((phase & PhaseEnded) != 0) return ScrollPhase.Ended;
        if ((phase & (PhaseChanged | PhaseStationary)) != 0) return ScrollPhase.Changed;
        return ScrollPhase.None;
    }

    /// <summary>
    /// <c>scrollingDeltaX / Y</c> → 逻辑像素。精确设备（<c>hasPreciseScrollingDeltas</c>）的增量已是点（= 逻辑像素）；
    /// 普通滚轮的增量是行（可为小数，含系统加速），乘以 <see cref="PixelsPerLine"/>。AppKit 的符号与框架约定相同（dy &gt; 0 向上滚动）。
    /// </summary>
    public static void Translate(double scrollingDeltaX, double scrollingDeltaY, bool precise, out float dx, out float dy, out WheelDeltaMode mode)
    {
        float k = precise ? 1f : PixelsPerLine;
        dx = WheelDeltas.Sanitize((float)scrollingDeltaX * k);
        dy = WheelDeltas.Sanitize((float)scrollingDeltaY * k);
        mode = precise ? WheelDeltaMode.Pixel : WheelDeltaMode.Line;
    }
}

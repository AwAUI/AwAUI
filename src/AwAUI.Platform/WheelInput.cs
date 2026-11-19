using System;

namespace AwAUI.Platform;

/// <summary>滚轮设备报告增量的单位（W3C <c>WheelEvent.deltaMode</c>）。<see cref="RawPointerEvent"/> 中的增量总是已换算成逻辑像素。</summary>
public enum WheelDeltaMode : byte
{
    /// <summary>按像素（触控板、高分辨率滚轮、浏览器的像素模式）。</summary>
    Pixel,

    /// <summary>按行（普通分格滚轮）：平台按系统设置的「每格行数」与 <see cref="WheelDeltas.PixelsPerLine"/> 换算。</summary>
    Line,

    /// <summary>按页（Windows 的「一次滚动一屏」、浏览器的 deltaMode = 2）：增量按窗口客户区尺寸换算，框架按滚动容器的页尺寸重新换算。</summary>
    Page,
}

/// <summary>
/// 触控板滚动的相位（macOS <c>NSEventPhase</c> 的子集）。手势相位描述手指在板上的过程；
/// 惯性相位（<see cref="RawPointerEvent.MomentumPhase"/>）描述手指离开之后系统生成的惯性滚动，只用 Began / Changed / Ended。
/// </summary>
public enum ScrollPhase : byte
{
    /// <summary>没有相位信息（普通滚轮），或不在该过程中。</summary>
    None,

    /// <summary>手指放上触控板但还没有移动（可用来停止正在进行的滚动动画）。</summary>
    MayBegin,

    Began,
    Changed,
    Ended,

    /// <summary>手势被系统取消。</summary>
    Cancelled,
}

/// <summary>滚动条样式（design/21「滚动条」）：平台偏好，框架按它与指针类型选择。</summary>
public enum ScrollbarStyle : byte
{
    /// <summary>覆盖式：不占布局空间、滚动时出现、停止后自动隐藏的细指示条（macOS 默认、触摸设备）。</summary>
    Overlay,

    /// <summary>经典式：常驻、可拖动滑块、点击轨道翻页（Windows、Linux 桌面）。</summary>
    Classic,
}

/// <summary>滚轮增量换算的公共约定与辅助（各平台后端共用，纯计算）。</summary>
public static class WheelDeltas
{
    /// <summary>
    /// 一「行」对应的逻辑像素（Win32、X11 的分格滚轮）：每格 3 行时为 100 逻辑像素，与 Chromium 一致。
    /// macOS 的非精确滚轮沿用 WebKit 的每行 40 像素（系统给出的行数已含加速）。
    /// </summary>
    public const float PixelsPerLine = 100f / 3f;

    /// <summary>没有系统设置时每格滚动的行数（Windows 默认值）。</summary>
    public const int DefaultLinesPerNotch = 3;

    /// <summary>单个事件的增量上限（逻辑像素）：防御异常的平台值。</summary>
    public const float MaxDelta = 10_000_000f;

    /// <summary>非有限值按 0，超出 ±<see cref="MaxDelta"/> 的值钳制。</summary>
    public static float Sanitize(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 0;
        return Math.Max(-MaxDelta, Math.Min(MaxDelta, v));
    }

    /// <summary>
    /// 分格滚轮：<paramref name="notches"/> 格（可为小数，高分辨率滚轮）按 <paramref name="linesPerNotch"/> 行换算成逻辑像素。
    /// 行数不合法（≤ 0）时用 <see cref="DefaultLinesPerNotch"/>，过大时钳制到 100 行。
    /// </summary>
    public static float NotchesToPixels(float notches, int linesPerNotch)
    {
        if (linesPerNotch <= 0) linesPerNotch = DefaultLinesPerNotch;
        if (linesPerNotch > 100) linesPerNotch = 100;
        return Sanitize(notches * linesPerNotch * PixelsPerLine);
    }
}

using System;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 本目录（Input/）只含与 Win32 API 无关的纯逻辑：由测试以源码链接方式在任何系统上编译运行（含 net48）。

/// <summary>
/// WM_MOUSEWHEEL / WM_MOUSEHWHEEL 的增量换算（纯逻辑）：
/// <list type="bullet">
/// <item>按行：每 <see cref="WheelDelta"/>（120）单位为一格，按系统设置 SPI_GETWHEELSCROLLLINES（纵向）/ SPI_GETWHEELSCROLLCHARS（横向）
/// 的行数与 <see cref="WheelDeltas.PixelsPerLine"/> 换算成像素。不足 120 的增量（高分辨率滚轮、模拟滚轮的精确触控板）按比例换算，
/// 并标为精确（不再做平滑动画）。</item>
/// <item>按页（行数设置为 WHEEL_PAGESCROLL）：增量先累加，满 120 才发出一页（按客户区尺寸，<see cref="WheelDeltaMode.Page"/>），余数保留；方向改变时清零。</item>
/// <item>行数为 0（系统设置为不滚动）时不发出事件。</item>
/// </list>
/// 符号：WM_MOUSEWHEEL 正值为向上滚动（与框架约定相同）；WM_MOUSEHWHEEL 正值为向右滚动，框架约定正值向左，因此取反。
/// </summary>
internal sealed class Win32WheelTranslator
{
    public const int WheelDelta = 120;

    /// <summary>SPI_GETWHEELSCROLLLINES 返回它表示「一次滚动一屏」。</summary>
    public const uint WheelPageScroll = uint.MaxValue;

    int _pageAccumV, _pageAccumH;

    /// <summary>纵向每格滚动的行数（SPI_GETWHEELSCROLLLINES，默认 3）。</summary>
    public uint LinesPerNotch { get; set; } = WheelDeltas.DefaultLinesPerNotch;

    /// <summary>横向每格滚动的字符数（SPI_GETWHEELSCROLLCHARS，默认 3），按行同样换算。</summary>
    public uint CharsPerNotch { get; set; } = WheelDeltas.DefaultLinesPerNotch;

    /// <summary>
    /// 换算一条滚轮消息。<paramref name="rawDelta"/> 是 GET_WHEEL_DELTA_WPARAM 的值。返回 false 表示不发出事件（设置为不滚动，或按页累加未满一格）。
    /// </summary>
    public bool Translate(bool horizontal, int rawDelta, Size clientSize, out float dx, out float dy, out WheelDeltaMode mode, out bool precise)
    {
        dx = dy = 0;
        mode = WheelDeltaMode.Line;
        precise = false;
        if (rawDelta == 0) return false;
        uint setting = horizontal ? CharsPerNotch : LinesPerNotch;
        if (setting == 0) return false;
        float pixels;
        if (setting == WheelPageScroll)
        {
            ref int acc = ref horizontal ? ref _pageAccumH : ref _pageAccumV;
            if (acc != 0 && Math.Sign(acc) != Math.Sign(rawDelta)) acc = 0;
            acc += rawDelta;
            int pages = acc / WheelDelta;
            if (pages == 0) return false;
            acc -= pages * WheelDelta;
            mode = WheelDeltaMode.Page;
            pixels = pages * (horizontal ? clientSize.Width : clientSize.Height);
        }
        else
        {
            _pageAccumV = _pageAccumH = 0;
            precise = rawDelta % WheelDelta != 0;
            if (precise) mode = WheelDeltaMode.Pixel;
            pixels = WheelDeltas.NotchesToPixels(rawDelta / (float)WheelDelta, (int)Math.Min(setting, 100));
        }
        if (horizontal) dx = -pixels;
        else dy = pixels;
        return true;
    }
}

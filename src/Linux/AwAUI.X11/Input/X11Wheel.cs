using AwAUI.Platform;

namespace AwAUI.X11;

// 本目录（Input/）只含与 Xlib 无关的纯逻辑：由测试以源码链接方式在任何系统上编译运行（含 net48）。

/// <summary>
/// 核心协议的滚轮：按钮 4 / 5 / 6 / 7 = 向上 / 向下 / 向左 / 向右一格（只有按下有意义）。X11 没有统一的「每格行数」系统设置，
/// 按 <see cref="WheelDeltas.DefaultLinesPerNotch"/> 行换算（每格 100 逻辑像素，与 Win32 默认一致）。
/// <para>
/// XInput2 的平滑滚动（滚动类 valuator，触控板与高分辨率滚轮的连续增量）没有实现：需要 XI2 事件选择、设备层级与 valuator 增量的跟踪，
/// 并在收到 XI2 滚动时屏蔽重复的核心按钮事件。目前触控板在 X11 上按「分格滚轮」处理（框架做平滑动画）。
/// </para>
/// </summary>
internal static class X11Wheel
{
    /// <summary>按钮号 → 滚动增量（逻辑像素；框架约定：dy &gt; 0 向上，dx &gt; 0 向左）。不是滚轮按钮时返回 false。</summary>
    public static bool TryGetDelta(uint button, int linesPerNotch, out float dx, out float dy)
    {
        dx = dy = 0;
        float step = WheelDeltas.NotchesToPixels(1, linesPerNotch);
        switch (button)
        {
            case 4: dy = step; return true;
            case 5: dy = -step; return true;
            case 6: dx = step; return true;
            case 7: dx = -step; return true;
            default: return false;
        }
    }

    /// <summary>是否为滚轮按钮（4–7）。</summary>
    public static bool IsWheelButton(uint button) => button >= 4 && button <= 7;
}

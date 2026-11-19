using System;

namespace AwAUI.Browser;

// 本目录（Translation/）只含纯 C# 的翻译逻辑：不依赖 JS 互操作，由 tests/AwAUI.Browser.Tests 以源码链接方式
// 在普通 .NET 上测试（含 net48），所以只能使用 netstandard2.0 可用的 API。

/// <summary>
/// JS 胶水（wwwroot/awaui-browser.js）与 C# 之间约定的数值编码。两边必须保持一致，改动时同时修改 JS 中的同名常量。
/// 跨边界只传数字与短字符串，避免逐事件分配 JS 对象代理。
/// </summary>
internal static class DomProtocol
{
    // 指针事件种类（DOM 事件名 → 数值）
    public const int PointerDown = 0;
    public const int PointerUp = 1;
    public const int PointerMove = 2;
    public const int PointerEnter = 3;
    public const int PointerLeave = 4;
    public const int PointerCancel = 5;

    // PointerEvent.pointerType
    public const int DeviceMouse = 0;
    public const int DeviceTouch = 1;
    public const int DevicePen = 2;

    // 修饰键位（JS 端按 shiftKey/ctrlKey/altKey/metaKey 打包）
    public const int ModShift = 1;
    public const int ModControl = 2;
    public const int ModAlt = 4;
    public const int ModMeta = 8;

    /// <summary>KeyboardEvent.getModifierState("AltGraph")：只用于键盘策略（<see cref="KeyboardPolicy"/>），不映射到 KeyModifiers。</summary>
    public const int ModAltGraph = 16;

    // WheelEvent.deltaMode
    public const int DeltaPixel = 0;
    public const int DeltaLine = 1;
    public const int DeltaPage = 2;

    // 输入法组合事件
    public const int CompositionStart = 0;
    public const int CompositionUpdate = 1;
    public const int CompositionEnd = 2;

    /// <summary>非精确滚轮每「行」对应的逻辑像素（与 macOS 后端的按行换算一致；Win32 / X11 按每格 100 像素，见 <see cref="AwAUI.Platform.WheelDeltas"/>）。</summary>
    public const float WheelLineStep = 40f;

    /// <summary>来自页面的数值都可能是 NaN / ±∞（例如脚本合成的事件）；坐标与滚动量限制在这个幅度内。</summary>
    public const float MaxCoordinate = 1e7f;

    public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>非有限值取 <paramref name="fallback"/>，其余限制到 [min, max]。</summary>
    public static double Sanitize(double v, double min, double max, double fallback)
    {
        if (!IsFinite(v)) return fallback;
        return v < min ? min : v > max ? max : v;
    }

    /// <summary>
    /// DOMHighResTimeStamp（毫秒，相对 performance.timeOrigin）→ TimeSpan。
    /// 按 tick 精确换算（TimeSpan.FromMilliseconds 在 .NET Framework 上会舍入到整毫秒）；非法值取 0。
    /// </summary>
    public static TimeSpan ToTimeSpan(double milliseconds)
    {
        // 约 29 万年：远小于 TimeSpan.MaxValue，避免乘法溢出
        const double MaxMs = 9e12;
        if (!IsFinite(milliseconds) || milliseconds <= 0) return TimeSpan.Zero;
        if (milliseconds > MaxMs) milliseconds = MaxMs;
        return TimeSpan.FromTicks((long)(milliseconds * TimeSpan.TicksPerMillisecond));
    }
}

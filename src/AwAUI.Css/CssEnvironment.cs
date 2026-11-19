using System;
using AwAUI.Css.Media;

namespace AwAUI.Css;

/// <summary>
/// 样式计算所处的环境：媒体查询特性、视口（含 small/large/dynamic 视口）、<c>env()</c> 变量（安全区）与默认字体大小。
/// 由平台层维护；修改后需通知 <see cref="Cascade.StyleEngine.EnvironmentChanged"/>，以便重新求值媒体查询并重算样式。
/// </summary>
public sealed class CssEnvironment
{
    public CssEnvironment() : this(new MediaEnvironment()) { }

    public CssEnvironment(MediaEnvironment media) => Media = media ?? throw new ArgumentNullException(nameof(media));

    /// <summary>媒体查询求值所用的环境。视口宽高即 <see cref="MediaEnvironment.Width"/> / <see cref="MediaEnvironment.Height"/>。</summary>
    public MediaEnvironment Media { get; }

    /// <summary>当前（动态）视口宽度，<c>vw</c>/<c>dvw</c> 的基准。</summary>
    public double ViewportWidth => Media.Width;

    /// <summary>当前（动态）视口高度，<c>vh</c>/<c>dvh</c> 的基准。</summary>
    public double ViewportHeight => Media.Height;

    /// <summary>最小视口宽度（移动端工具栏全部展开时），<c>svw</c> 的基准；未设置时等于当前视口。</summary>
    public double? SmallViewportWidthOverride { get; set; }

    /// <summary>最小视口高度，<c>svh</c> 的基准；未设置时等于当前视口。</summary>
    public double? SmallViewportHeightOverride { get; set; }

    /// <summary>最大视口宽度（工具栏全部收起时），<c>lvw</c> 的基准；未设置时等于当前视口。</summary>
    public double? LargeViewportWidthOverride { get; set; }

    /// <summary>最大视口高度，<c>lvh</c> 的基准；未设置时等于当前视口。</summary>
    public double? LargeViewportHeightOverride { get; set; }

    public double SmallViewportWidth => SmallViewportWidthOverride ?? ViewportWidth;
    public double SmallViewportHeight => SmallViewportHeightOverride ?? ViewportHeight;
    public double LargeViewportWidth => LargeViewportWidthOverride ?? ViewportWidth;
    public double LargeViewportHeight => LargeViewportHeightOverride ?? ViewportHeight;

    /// <summary><c>env(safe-area-inset-top)</c>（px）。</summary>
    public double SafeAreaInsetTop { get; set; }

    /// <summary><c>env(safe-area-inset-right)</c>（px）。</summary>
    public double SafeAreaInsetRight { get; set; }

    /// <summary><c>env(safe-area-inset-bottom)</c>（px）。</summary>
    public double SafeAreaInsetBottom { get; set; }

    /// <summary><c>env(safe-area-inset-left)</c>（px）。</summary>
    public double SafeAreaInsetLeft { get; set; }

    /// <summary><c>medium</c> 字体大小，即 <c>font-size</c> 的初始值与根元素 <c>rem</c> 的基准（px）。CSS 规范与浏览器默认为 16。</summary>
    public double DefaultFontSize => 16;

    /// <summary>按名称取 <c>env()</c> 变量（px）；未知名称返回 false。</summary>
    public bool TryGetEnvironmentVariable(string name, out double value)
    {
        switch (name)
        {
            case "safe-area-inset-top": value = SafeAreaInsetTop; return true;
            case "safe-area-inset-right": value = SafeAreaInsetRight; return true;
            case "safe-area-inset-bottom": value = SafeAreaInsetBottom; return true;
            case "safe-area-inset-left": value = SafeAreaInsetLeft; return true;
            case "safe-area-max-inset-top": value = SafeAreaInsetTop; return true;
            case "safe-area-max-inset-right": value = SafeAreaInsetRight; return true;
            case "safe-area-max-inset-bottom": value = SafeAreaInsetBottom; return true;
            case "safe-area-max-inset-left": value = SafeAreaInsetLeft; return true;
            default: value = 0; return false;
        }
    }
}

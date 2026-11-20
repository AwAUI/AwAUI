using System;
using AwAUI.Css;
using AwAUI.Css.Values;

namespace AwAUI.UI;

/// <summary>
/// 滚动容器的 CSS 属性（<c>overscroll-behavior</c>、<c>scrollbar-width</c>、<c>scrollbar-color</c>、<c>scroll-behavior</c>、
/// <c>scroll-margin</c> / <c>scroll-padding</c>）到滚动系统的使用值。<see cref="ScrollController"/> 上显式设置的属性优先：
/// 控制器的属性不是默认值（<see cref="OverscrollBehavior.Auto"/>、<see cref="ScrollbarWidth.Auto"/>、null）时用它，否则用计算样式。
/// </summary>
internal static class ScrollCss
{
    static ComputedStyle? StyleOf(ScrollController c) => c._element?.Render?.Style;

    /// <summary><c>overscroll-behavior-x</c>（<paramref name="axis"/> 0）或 <c>-y</c>（1）。</summary>
    public static OverscrollBehavior Overscroll(ScrollController c, int axis)
    {
        var own = axis == 0 ? c.OverscrollBehaviorX : c.OverscrollBehaviorY;
        if (own != OverscrollBehavior.Auto || StyleOf(c) is not { } s) return own;
        return (axis == 0 ? s.OverscrollBehaviorX : s.OverscrollBehaviorY) switch
        {
            CssOverscrollBehavior.Contain => OverscrollBehavior.Contain,
            CssOverscrollBehavior.None => OverscrollBehavior.None,
            _ => OverscrollBehavior.Auto,
        };
    }

    /// <summary><c>scrollbar-width</c>。</summary>
    public static ScrollbarWidth Width(ScrollController c)
    {
        if (c.ScrollbarWidth != ScrollbarWidth.Auto || StyleOf(c) is not { } s) return c.ScrollbarWidth;
        return s.ScrollbarWidth switch
        {
            CssScrollbarWidth.Thin => ScrollbarWidth.Thin,
            CssScrollbarWidth.None => ScrollbarWidth.None,
            _ => ScrollbarWidth.Auto,
        };
    }

    /// <summary><c>scrollbar-color</c> 的滑块颜色（继承属性；auto 时为 null，用平台默认）。</summary>
    public static Color? ThumbColor(ScrollController c)
    {
        if (c.ScrollbarThumbColor is { } own) return own;
        if (StyleOf(c) is not { } s || s.ScrollbarColor.IsAuto) return null;
        return s.ResolveColor(s.ScrollbarColor.Thumb);
    }

    /// <summary><c>scrollbar-color</c> 的轨道颜色（只用于经典样式）。</summary>
    public static Color? TrackColor(ScrollController c)
    {
        if (c.ScrollbarTrackColor is { } own) return own;
        if (StyleOf(c) is not { } s || s.ScrollbarColor.IsAuto) return null;
        return s.ResolveColor(s.ScrollbarColor.Track);
    }

    /// <summary>
    /// 程序滚动（<see cref="ScrollController.ScrollTo"/> / <see cref="ScrollController.ScrollIntoView"/> 不给动画时）的动画：
    /// 滚动容器的 <c>scroll-behavior: smooth</c> 为平滑，<c>auto</c> 为立即（CSSOM View §7）。默认样式表把它设为 smooth，
    /// 所以没有作者样式时与此前「默认平滑」的行为相同。
    /// </summary>
    public static ScrollAnimation DefaultAnimation(ScrollController c) =>
        StyleOf(c) is { ScrollBehavior: CssScrollBehavior.Auto } ? ScrollAnimation.Instant : ScrollAnimation.Default;

    /// <summary>
    /// 「滚动到可见」的目标矩形：元素边框盒按 <c>scroll-margin</c> 外扩（CSS Scroll Snap 1 §4.2，作用于 <c>scrollIntoView</c>）。
    /// </summary>
    public static Rect WithScrollMargin(Element target, Rect rect)
    {
        if (target.Render?.Style is not { } s) return rect;
        float t = Finite(s.ScrollMarginTop), r = Finite(s.ScrollMarginRight), b = Finite(s.ScrollMarginBottom), l = Finite(s.ScrollMarginLeft);
        if (t == 0 && r == 0 && b == 0 && l == 0) return rect;
        return Rect.FromLTRB(rect.Left - l, rect.Top - t, Math.Max(rect.Left - l, rect.Right + r), Math.Max(rect.Top - t, rect.Bottom + b));
    }

    /// <summary>滚动容器的视口按 <c>scroll-padding</c> 内缩（百分比相对视口的对应尺寸；auto 为 0）。</summary>
    public static Rect WithScrollPadding(Element scroller, Rect viewport)
    {
        if (scroller.Render?.Style is not { } s) return viewport;
        float t = Pad(s.ScrollPaddingTop, viewport.Height), r = Pad(s.ScrollPaddingRight, viewport.Width);
        float b = Pad(s.ScrollPaddingBottom, viewport.Height), l = Pad(s.ScrollPaddingLeft, viewport.Width);
        if (t == 0 && r == 0 && b == 0 && l == 0) return viewport;
        float left = viewport.Left + l, top = viewport.Top + t;
        return Rect.FromLTRB(left, top, Math.Max(left, viewport.Right - r), Math.Max(top, viewport.Bottom - b));
    }

    static float Pad(SizeValue v, float basis) => v.Kind == SizeKind.LengthPercentage ? Math.Max(0, Finite(v.Value.Resolve(basis))) : 0;

    static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : Math.Max(-1e6f, Math.Min(1e6f, v));
}

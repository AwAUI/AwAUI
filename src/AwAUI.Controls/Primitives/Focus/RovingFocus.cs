using System;
using AwAUI.Layout;
using AwAUI.Platform;

namespace AwAUI.Controls.Primitives;

/// <summary>排列方向（<c>data-orientation</c>）。</summary>
public enum Orientation : byte
{
    Horizontal,
    Vertical,
}

/// <summary>按键对应的焦点移动意图。</summary>
public enum FocusIntent : byte
{
    None,
    First,
    Last,
    Previous,
    Next,

    /// <summary>向前翻一页（PageUp，仅在设置了页大小时）。</summary>
    PageBackward,

    /// <summary>向后翻一页（PageDown，仅在设置了页大小时）。</summary>
    PageForward,
}

/// <summary>漫游焦点的选项（Radix <c>RovingFocusGroup</c> 的 <c>orientation / dir / loop</c>）。</summary>
public sealed class RovingFocusOptions
{
    public static readonly RovingFocusOptions Default = new();

    /// <summary>只响应该方向的方向键；null 表示两个方向都响应。</summary>
    public Orientation? Orientation { get; init; }

    /// <summary>RTL 下左右方向键互换。</summary>
    public Direction Direction { get; init; } = Direction.Ltr;

    /// <summary>到达一端后是否回绕（Radix 默认 false）。</summary>
    public bool Loop { get; init; }

    /// <summary>
    /// PageUp / PageDown 一次移动的项数；0（默认）时与 Radix 相同，PageUp / PageDown 等同 Home / End。
    /// </summary>
    public int PageSize { get; init; }
}

/// <summary>
/// 漫游焦点的导航逻辑（Radix <c>RovingFocusGroup</c>）：一组项只占一个 Tab 位，方向键在可聚焦项之间移动，跳过禁用项。
/// 纯函数，项以索引表示，可聚焦性由回调给出；最坏情况（全部禁用）扫描一遍，不会无限循环。
/// </summary>
public static class RovingFocus
{
    /// <summary>把按键（DOM <c>key</c> 名）映射为移动意图；不处理的键返回 <see cref="FocusIntent.None"/>。</summary>
    public static FocusIntent GetFocusIntent(string? key, Orientation? orientation, Direction direction, int pageSize = 0)
    {
        if (key is null) return FocusIntent.None;
        if (direction == Direction.Rtl)
        {
            if (key == "ArrowLeft") key = "ArrowRight";
            else if (key == "ArrowRight") key = "ArrowLeft";
        }
        if (orientation == Orientation.Vertical && (key == "ArrowLeft" || key == "ArrowRight")) return FocusIntent.None;
        if (orientation == Orientation.Horizontal && (key == "ArrowUp" || key == "ArrowDown")) return FocusIntent.None;
        return key switch
        {
            "ArrowLeft" or "ArrowUp" => FocusIntent.Previous,
            "ArrowRight" or "ArrowDown" => FocusIntent.Next,
            "Home" => FocusIntent.First,
            "End" => FocusIntent.Last,
            "PageUp" => pageSize > 0 ? FocusIntent.PageBackward : FocusIntent.First,
            "PageDown" => pageSize > 0 ? FocusIntent.PageForward : FocusIntent.Last,
            _ => FocusIntent.None,
        };
    }

    /// <summary>
    /// 按意图求目标索引。<paramref name="currentIndex"/> 为 -1（或不在范围内）时，Next 从第一项开始、Previous 从最后一项开始。
    /// 没有可去的项时返回 -1。
    /// </summary>
    public static int Navigate(FocusIntent intent, int currentIndex, int count, Func<int, bool>? isFocusable, bool loop, int pageSize = 0)
    {
        if (count <= 0) return -1;
        if (currentIndex < 0 || currentIndex >= count) currentIndex = -1;
        switch (intent)
        {
            case FocusIntent.First:
                return Scan(0, +1, count, count, isFocusable);
            case FocusIntent.Last:
                return Scan(count - 1, -1, count, count, isFocusable);
            case FocusIntent.Next:
            {
                if (currentIndex < 0) return Scan(0, +1, count, count, isFocusable);
                if (loop) return Scan((currentIndex + 1) % count, +1, count, count - 1, isFocusable, wrap: true);
                return Scan(currentIndex + 1, +1, count, count - currentIndex - 1, isFocusable);
            }
            case FocusIntent.Previous:
            {
                if (currentIndex < 0) return Scan(count - 1, -1, count, count, isFocusable);
                if (loop) return Scan((currentIndex - 1 + count) % count, -1, count, count - 1, isFocusable, wrap: true);
                return Scan(currentIndex - 1, -1, count, currentIndex, isFocusable);
            }
            case FocusIntent.PageForward:
            case FocusIntent.PageBackward:
            {
                int step = Math.Max(1, pageSize);
                int dir = intent == FocusIntent.PageForward ? 1 : -1;
                long target = currentIndex < 0 ? (dir > 0 ? 0 : count - 1) : currentIndex + (long)dir * step;
                int t = (int)Math.Max(0, Math.Min(count - 1, target));
                // 目标不可聚焦：先沿翻页方向找，找不到再回头（不越过当前项）
                int found = Scan(t, dir, count, dir > 0 ? count - t : t + 1, isFocusable);
                if (found >= 0) return found;
                int limit = currentIndex < 0 ? count : Math.Abs(t - currentIndex);
                return Scan(t - dir, -dir, count, limit, isFocusable);
            }
            default:
                return -1;
        }
    }

    /// <summary>
    /// 处理一次按键：返回应获得焦点的索引；按键不归漫游焦点处理（含按住任何修饰键时，与 Radix 相同）返回 -1。
    /// 返回值等于 <paramref name="currentIndex"/> 表示按键已处理但焦点不动（调用方仍应 <c>PreventDefault</c>）。
    /// </summary>
    public static int HandleKeyDown(string? key, KeyModifiers modifiers, int currentIndex, int count, Func<int, bool>? isFocusable, RovingFocusOptions? options = null)
    {
        options ??= RovingFocusOptions.Default;
        var intent = GetFocusIntent(key, options.Orientation, options.Direction, options.PageSize);
        if (intent == FocusIntent.None) return -1;
        if ((modifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)) != 0) return -1;
        int target = Navigate(intent, currentIndex, count, isFocusable, options.Loop, options.PageSize);
        return target < 0 ? currentIndex : target;
    }

    static int Scan(int start, int step, int count, int limit, Func<int, bool>? isFocusable, bool wrap = false)
    {
        int i = start;
        for (int n = 0; n < limit; n++)
        {
            if (i < 0 || i >= count)
            {
                if (!wrap) return -1;
                i = (i % count + count) % count;
            }
            if (isFocusable is null || isFocusable(i)) return i;
            i += step;
        }
        return -1;
    }
}

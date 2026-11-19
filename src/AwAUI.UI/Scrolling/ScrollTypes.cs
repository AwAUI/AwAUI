using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 滚动事件（<see cref="ScrollController.Scrolled"/>、元素的 <c>OnScroll</c>）：每帧最多一次，带本帧结束时的偏移与相对上次事件的变化。不冒泡。
/// </summary>
public sealed class ScrollEvent : Event
{
    public ScrollEvent() : base("scroll", bubbles: false) { }

    /// <summary>当前滚动偏移（逻辑像素；越界回弹期间可能超出 [0, MaxOffset]）。</summary>
    public Point Offset { get; internal set; }

    /// <summary>相对上一次 scroll 事件的偏移变化。</summary>
    public Point Delta { get; internal set; }

    /// <summary>发生滚动的控制器。</summary>
    public ScrollController? Controller { get; internal set; }
}

/// <summary>
/// 滚轮事件（DOM <c>wheel</c>）：派发到指针下的元素并冒泡；处理器调用 <see cref="Event.PreventDefault"/> 时不滚动。
/// 增量按 DOM 约定：正值 = 向下 / 向右滚动（与 <see cref="RawPointerEvent.WheelDeltaY"/> 的平台约定相反），逻辑像素。
/// </summary>
public sealed class WheelEvent : Event
{
    public WheelEvent() : base("wheel") { }

    /// <summary>客户区坐标（逻辑像素）。</summary>
    public Point Position { get; internal set; }

    /// <summary>横向增量（正值 = 向右滚动；Shift + 纵向滚轮已换成横向）。</summary>
    public float DeltaX { get; internal set; }

    /// <summary>纵向增量（正值 = 向下滚动）。</summary>
    public float DeltaY { get; internal set; }

    /// <summary>设备报告增量的单位（增量本身已是逻辑像素）。</summary>
    public WheelDeltaMode DeltaMode { get; internal set; }

    /// <summary>精确设备（触控板）：滚动直接跟随，不做平滑动画。</summary>
    public bool IsPrecise { get; internal set; }

    /// <summary>触控板手势相位。</summary>
    public ScrollPhase ScrollPhase { get; internal set; }

    /// <summary>系统惯性相位。</summary>
    public ScrollPhase MomentumPhase { get; internal set; }

    public KeyModifiers Modifiers { get; internal set; }
}

/// <summary><see cref="ScrollController.ScrollIntoView"/> 的对齐方式（CSS <c>scrollIntoView</c> 的 block / inline 选项）。</summary>
public enum ScrollAlignment : byte
{
    /// <summary>已经完整可见则不动，否则以最小的滚动量使其可见（默认）。</summary>
    Nearest,

    /// <summary>对齐到视口起始边。</summary>
    Start,

    /// <summary>居中。</summary>
    Center,

    /// <summary>对齐到视口末端边。</summary>
    End,
}

/// <summary>滚动到边界后是否把滚动链传给祖先、是否显示越界效果（CSS <c>overscroll-behavior</c>）。</summary>
public enum OverscrollBehavior : byte
{
    /// <summary>滚到头后把剩余的滚动交给最近的可滚动祖先；允许越界效果。</summary>
    Auto,

    /// <summary>不传给祖先；允许本容器的越界效果。</summary>
    Contain,

    /// <summary>不传给祖先，也不显示越界效果。</summary>
    None,
}

/// <summary>滚动条宽度（CSS <c>scrollbar-width</c>）。</summary>
public enum ScrollbarWidth : byte
{
    Auto,
    Thin,

    /// <summary>不显示滚动条（仍可滚动）。</summary>
    None,
}

/// <summary>程序滚动的动画参数。</summary>
public sealed class ScrollAnimation
{
    /// <summary>默认的平滑滚动：300 ms，缓入缓出。</summary>
    public static readonly ScrollAnimation Default = new();

    /// <summary>不做动画（立即跳到目标，等价于 <see cref="ScrollController.JumpTo"/>，但仍返回任务）。</summary>
    public static readonly ScrollAnimation Instant = new() { Duration = TimeSpan.Zero };

    /// <summary>时长；≤ 0 表示立即跳到目标。</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(300);

    public ScrollEasing Easing { get; init; } = ScrollEasing.EaseInOut;
}

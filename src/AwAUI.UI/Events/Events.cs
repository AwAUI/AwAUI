using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>事件处理器槽位。元素与描述节点用同一张表保存处理器，<c>*Capture</c> 为捕获阶段的处理器。</summary>
internal enum EventKind : byte
{
    Click,
    PointerDown,
    PointerUp,
    PointerMove,
    PointerEnter,
    PointerLeave,
    PointerCancel,
    KeyDown,
    KeyUp,
    Focus,
    Blur,
    FocusIn,
    FocusOut,
    Input,
    Change,
    Submit,
    Tap,
    Pan,
    LongPress,
    DoubleTap,
    Swipe,
    Pinch,
    Rotate,
    EdgeSwipe,
    ClickCapture,
    PointerDownCapture,
    PointerUpCapture,
    PointerMoveCapture,
    KeyDownCapture,
    KeyUpCapture,
    Scroll,
    Wheel,
    // CSS 动画与过渡（Animation/RenderPipeline.Animation.cs 派发）
    AnimationStart,
    AnimationIteration,
    AnimationEnd,
    AnimationCancel,
    TransitionRun,
    TransitionStart,
    TransitionEnd,
    TransitionCancel,
    Count,
}

/// <summary>事件传播阶段（DOM 语义）。</summary>
public enum EventPhase : byte
{
    None,
    Capturing,
    AtTarget,
    Bubbling,
}

/// <summary>
/// 事件基类。派发按 DOM 模型进行：捕获（根 → 目标的父）→ 目标（先捕获处理器后冒泡处理器）→ 冒泡（目标的父 → 根）。
/// 路径在派发开始时确定，处理器中对树的修改不影响本次派发。
/// </summary>
public class Event
{
    public Event(string type, bool bubbles = true)
    {
        Type = type;
        Bubbles = bubbles;
    }

    /// <summary>事件名（DOM 拼写，如 <c>click</c>、<c>pointerdown</c>）。</summary>
    public string Type { get; }

    /// <summary>是否冒泡。</summary>
    public bool Bubbles { get; }

    /// <summary>派发目标。</summary>
    public Element? Target { get; internal set; }

    /// <summary>当前正在执行处理器的元素。</summary>
    public Element? CurrentTarget { get; internal set; }

    public EventPhase Phase { get; internal set; }

    public TimeSpan Timestamp { get; internal set; }

    public bool DefaultPrevented { get; private set; }

    public bool IsPropagationStopped { get; private set; }

    public bool IsImmediatePropagationStopped { get; private set; }

    /// <summary>阻止框架的默认行为（如按下时聚焦、Tab 移动焦点、回车激活按钮）。</summary>
    public void PreventDefault() => DefaultPrevented = true;

    /// <summary>
    /// 不再传播到其他元素。DOM 语义：目标上的捕获处理器（<c>*Capture</c>）与冒泡处理器是两个监听器、分两轮调用，
    /// 因此在目标的捕获处理器中调用后，目标的冒泡处理器也不再执行。
    /// </summary>
    public void StopPropagation() => IsPropagationStopped = true;

    /// <summary>立即停止：当前元素上的其余处理器也不再执行。</summary>
    public void StopImmediatePropagation()
    {
        IsPropagationStopped = true;
        IsImmediatePropagationStopped = true;
    }

    internal void ResetForRedispatch()
    {
        IsPropagationStopped = false;
        IsImmediatePropagationStopped = false;
        Phase = EventPhase.None;
        CurrentTarget = null;
    }
}

/// <summary>统一的指针事件（鼠标、触摸、笔）。<c>click</c> 也是指针事件；键盘激活产生的 click 的 <see cref="PointerId"/> 为 -1。</summary>
public sealed class PointerEvent : Event
{
    public PointerEvent(string type, bool bubbles = true) : base(type, bubbles) { }

    /// <summary>客户区坐标（逻辑像素）。</summary>
    public Point Position { get; internal set; }

    public long PointerId { get; internal set; }

    public PointerType PointerType { get; internal set; }

    /// <summary>引起本事件的按键（按下/抬起）；移动事件为 <see cref="PointerButtons.None"/>。</summary>
    public PointerButtons Button { get; internal set; }

    /// <summary>当前按住的全部按键。</summary>
    public PointerButtons Buttons { get; internal set; }

    public KeyModifiers Modifiers { get; internal set; }

    public float Pressure { get; internal set; }

    /// <summary>是否为该类设备的主指针。</summary>
    public bool IsPrimary { get; internal set; } = true;

    /// <summary>由键盘或无障碍动作合成的激活（非真实指针）。</summary>
    public bool IsSynthetic { get; internal set; }
}

/// <summary>键盘事件。</summary>
public sealed class KeyEvent : Event
{
    public KeyEvent(string type) : base(type) { }

    /// <summary>
    /// 逻辑键（W3C <c>KeyboardEvent.key</c> 拼写，按当前布局）：可打印键为其字符（含 Shift / AltGr 的效果，不含 Ctrl），
    /// 其余为键名（<c>Enter</c>、<c>Tab</c>、<c>ArrowLeft</c>、死键 <c>Dead</c> ...）。平台未提供时按 US 布局推断。
    /// </summary>
    public string Key { get; internal set; } = "";

    /// <summary>与布局无关的物理键。</summary>
    public PhysicalKey PhysicalKey { get; internal set; }

    /// <summary>按键位置：左 / 右修饰键、小键盘。</summary>
    public KeyLocation Location { get; internal set; }

    public KeyModifiers Modifiers { get; internal set; }

    /// <summary>该按键产生的文本（若平台提供）。</summary>
    public string? Text { get; internal set; }

    public bool IsRepeat { get; internal set; }

    /// <summary>平台的主快捷键修饰键是否按下：macOS 为 Cmd（Meta），其他平台为 Ctrl。</summary>
    public bool HasPrimaryModifier => Shortcuts.HasPrimaryModifier(Modifiers);

    /// <summary>与快捷键描述匹配（<c>"Mod+S"</c>、<c>"Ctrl+Shift+Z"</c>），见 <see cref="KeyGesture"/>。</summary>
    public bool Matches(KeyGesture gesture) => gesture.Matches(this);
}

/// <summary>焦点从何而来，用于 <c>:focus-visible</c> 判定。</summary>
public enum FocusOrigin : byte
{
    Programmatic,
    Keyboard,
    Pointer,
}

/// <summary>焦点事件：<c>focus</c>/<c>blur</c> 不冒泡，<c>focusin</c>/<c>focusout</c> 冒泡。</summary>
public sealed class FocusEvent : Event
{
    public FocusEvent(string type, bool bubbles) : base(type, bubbles) { }

    /// <summary>失去焦点的一方（focus/focusin）或获得焦点的一方（blur/focusout）。</summary>
    public Element? RelatedTarget { get; internal set; }

    public FocusOrigin Origin { get; internal set; }
}

/// <summary>输入框内容变化（<c>input</c>、<c>change</c>）。</summary>
public sealed class InputEvent : Event
{
    public InputEvent(string type, string value) : base(type) => Value = value;

    /// <summary>变化后的完整文本。</summary>
    public string Value { get; }
}

/// <summary>输入框提交（回车）。</summary>
public sealed class SubmitEvent : Event
{
    public SubmitEvent(string value) : base("submit") => Value = value;

    public string Value { get; }
}

/// <summary>点按手势事件（design/21）。手势识别器在后续里程碑实现，目前只声明类型。</summary>
public sealed class TapEvent : Event
{
    public TapEvent() : base("tap") { }

    public Point Position { get; internal set; }

    public PointerType PointerType { get; internal set; }

    public int TapCount { get; internal set; } = 1;
}

/// <summary>拖动手势事件（design/21）。手势识别器在后续里程碑实现，目前只声明类型。</summary>
public sealed class PanEvent : Event
{
    public PanEvent() : base("pan") { }

    public Point Position { get; internal set; }

    /// <summary>相对上一次的位移。</summary>
    public Point Delta { get; internal set; }

    /// <summary>自开始的累计位移。</summary>
    public Point Total { get; internal set; }

    /// <summary>速度（逻辑像素每秒）。</summary>
    public Point Velocity { get; internal set; }
}

/// <summary>长按的阶段。</summary>
public enum LongPressPhase : byte
{
    /// <summary>按住超过时长，长按开始。</summary>
    Started,

    /// <summary>长按开始后指针移动。</summary>
    Moved,

    /// <summary>抬起结束。</summary>
    Ended,

    /// <summary>被取消（<c>pointercancel</c> 或竞技场裁决）。</summary>
    Cancelled,
}

/// <summary>长按手势事件（design/21，<c>longpress</c>，冒泡）。</summary>
public sealed class LongPressEvent : Event
{
    public LongPressEvent() : base("longpress") { }

    /// <summary>当前位置（开始 / 移动 / 结束）；取消时为最后一次已知位置。</summary>
    public Point Position { get; internal set; }

    /// <summary>本事件的阶段。</summary>
    public new LongPressPhase Phase { get; internal set; }

    /// <summary>引起手势的指针类型。</summary>
    public PointerType PointerType { get; internal set; }
}

/// <summary>双击手势事件（design/21，<c>doubletap</c>，冒泡）。</summary>
public sealed class DoubleTapEvent : Event
{
    public DoubleTapEvent() : base("doubletap") { }

    /// <summary>第二次抬起的客户区坐标（逻辑像素）。</summary>
    public Point Position { get; internal set; }

    /// <summary>引起手势的指针类型。</summary>
    public PointerType PointerType { get; internal set; }
}

/// <summary>滑动方向（按位移的主导轴判定）。</summary>
public enum SwipeDirection : byte
{
    /// <summary>向左。</summary>
    Left,

    /// <summary>向右。</summary>
    Right,

    /// <summary>向上（逻辑坐标 y 减小）。</summary>
    Up,

    /// <summary>向下（逻辑坐标 y 增大）。</summary>
    Down,
}

/// <summary>滑动手势事件（design/21，<c>swipe</c>，冒泡）。</summary>
public sealed class SwipeEvent : Event
{
    public SwipeEvent() : base("swipe") { }

    /// <summary>滑动方向（按位移的主导轴判定）。</summary>
    public SwipeDirection Direction { get; internal set; }

    /// <summary>抬起时的速度（逻辑像素每秒）。</summary>
    public Point Velocity { get; internal set; }

    /// <summary>自按下到抬起的累计位移（逻辑像素）。</summary>
    public Point Distance { get; internal set; }

    /// <summary>抬起位置（客户区坐标，逻辑像素）。</summary>
    public Point Position { get; internal set; }

    /// <summary>引起手势的指针类型。</summary>
    public PointerType PointerType { get; internal set; }
}

/// <summary>屏幕边缘（边缘滑动手势的起点）。</summary>
public enum EdgeSwipeEdge : byte
{
    /// <summary>左边缘（客户区 x 靠近 0）。</summary>
    Left,

    /// <summary>右边缘（客户区 x 靠近视口宽度）。</summary>
    Right,

    /// <summary>上边缘（客户区 y 靠近 0）。</summary>
    Top,

    /// <summary>下边缘（客户区 y 靠近视口高度）。</summary>
    Bottom,
}

/// <summary>边缘滑动手势事件（<c>edgeswipe</c>，冒泡）：从屏幕边缘向内拖动后抬起时派发（design/09）。</summary>
public sealed class EdgeSwipeEvent : Event
{
    public EdgeSwipeEvent() : base("edgeswipe") { }

    /// <summary>手势起始的边缘。</summary>
    public EdgeSwipeEdge Edge { get; internal set; }

    /// <summary>抬起位置（客户区坐标，逻辑像素）。</summary>
    public Point Position { get; internal set; }

    /// <summary>自按下到抬起的累计位移（逻辑像素）。</summary>
    public Point Distance { get; internal set; }

    /// <summary>抬起时的速度（逻辑像素每秒）。</summary>
    public Point Velocity { get; internal set; }

    /// <summary>引起手势的指针类型。</summary>
    public PointerType PointerType { get; internal set; }
}

/// <summary>多指手势（捏合 / 旋转）的阶段。</summary>
public enum GesturePhase : byte
{
    /// <summary>超过阈值，手势开始。</summary>
    Started,

    /// <summary>识别中，本次事件带来了变化。</summary>
    Changed,

    /// <summary>正常结束（一指或两指抬起）。</summary>
    Ended,

    /// <summary>被取消（<c>pointercancel</c>）。</summary>
    Cancelled,
}

/// <summary>捏合手势事件（design/21，<c>pinch</c>，冒泡）。</summary>
public sealed class PinchEvent : Event
{
    public PinchEvent() : base("pinch") { }

    /// <summary>相对手势开始时的缩放比（两指距离之比）。</summary>
    public float Scale { get; internal set; }

    /// <summary>本帧（本次事件）的缩放比增量。</summary>
    public float DeltaScale { get; internal set; }

    /// <summary>两指中点（客户区坐标，逻辑像素）。</summary>
    public Point Focus { get; internal set; }

    /// <summary>本事件的阶段。</summary>
    public new GesturePhase Phase { get; internal set; }

    /// <summary>引起变化的指针类型。</summary>
    public PointerType PointerType { get; internal set; }
}

/// <summary>旋转手势事件（design/21，<c>rotate</c>，冒泡）。</summary>
public sealed class RotateEvent : Event
{
    public RotateEvent() : base("rotate") { }

    /// <summary>相对手势开始时的角度（度，顺时针为正）。</summary>
    public float AngleDegrees { get; internal set; }

    /// <summary>本帧（本次事件）的角度增量（度）。</summary>
    public float DeltaDegrees { get; internal set; }

    /// <summary>两指中点（客户区坐标，逻辑像素）。</summary>
    public Point Focus { get; internal set; }

    /// <summary>本事件的阶段。</summary>
    public new GesturePhase Phase { get; internal set; }

    /// <summary>引起变化的指针类型。</summary>
    public PointerType PointerType { get; internal set; }
}

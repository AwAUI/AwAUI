using System;

namespace AwAUI.Platform;

public enum PointerType : byte { Mouse, Touch, Pen }

public enum RawPointerEventType : byte { Down, Up, Move, Enter, Leave, Cancel, Wheel }

[Flags]
public enum PointerButtons : byte { None = 0, Left = 1, Right = 2, Middle = 4, X1 = 8, X2 = 16 }

/// <summary>
/// 修饰键。<see cref="AltGraph"/> 是「第三层」键（多数欧洲布局的右 Alt；Windows 上系统把它模拟成 Ctrl+Alt）：
/// 平台识别出 AltGr 时报告 <see cref="AltGraph"/> 而不报告由它模拟出来的 <see cref="Control"/> 与 <see cref="Alt"/>，
/// 所以 AltGr 输入的字符（德语 AltGr+Q = @）不会被当成 Ctrl+Alt 快捷键。
/// </summary>
[Flags]
public enum KeyModifiers : byte { None = 0, Shift = 1, Control = 2, Alt = 4, Meta = 8, AltGraph = 16 }

/// <summary>
/// 原始指针事件（W3C Pointer Events 模型）：鼠标、触摸、笔统一。
/// 平台层须原样上交全部采样，重采样与手势识别在上层完成（design/09、21）。
/// <para>
/// 滚轮事件（<see cref="RawPointerEventType.Wheel"/>）另带：增量（逻辑像素，正值 = 内容向右 / 向下移动，即向左 / 向上滚动，
/// 与 DOM 的 deltaX / deltaY 符号相反）、设备报告增量的单位 <see cref="WheelDeltaMode"/>、是否为精确设备 <see cref="PreciseWheel"/>，
/// 以及触控板的手势相位 <see cref="ScrollPhase"/> 与系统惯性相位 <see cref="MomentumPhase"/>。用 <see cref="CreateWheel"/> 构造。
/// </para>
/// </summary>
public readonly struct RawPointerEvent
{
    public RawPointerEvent(RawPointerEventType type, PointerType device, long pointerId, Point position,
        PointerButtons changedButton, PointerButtons buttons, KeyModifiers modifiers, TimeSpan timestamp,
        float pressure = 0.5f, float wheelDeltaX = 0, float wheelDeltaY = 0, bool preciseWheel = false)
        : this(type, device, pointerId, position, changedButton, buttons, modifiers, timestamp, pressure, wheelDeltaX, wheelDeltaY, preciseWheel,
            preciseWheel ? WheelDeltaMode.Pixel : WheelDeltaMode.Line, ScrollPhase.None, ScrollPhase.None)
    {
    }

    RawPointerEvent(RawPointerEventType type, PointerType device, long pointerId, Point position,
        PointerButtons changedButton, PointerButtons buttons, KeyModifiers modifiers, TimeSpan timestamp,
        float pressure, float wheelDeltaX, float wheelDeltaY, bool preciseWheel, WheelDeltaMode mode, ScrollPhase phase, ScrollPhase momentum)
    {
        Type = type; Device = device; PointerId = pointerId; Position = position; ChangedButton = changedButton;
        Buttons = buttons; Modifiers = modifiers; Timestamp = timestamp; Pressure = pressure;
        WheelDeltaX = WheelDeltas.Sanitize(wheelDeltaX); WheelDeltaY = WheelDeltas.Sanitize(wheelDeltaY); PreciseWheel = preciseWheel;
        WheelDeltaMode = mode; ScrollPhase = phase; MomentumPhase = momentum;
    }

    /// <summary>
    /// 构造滚轮 / 触控板滚动事件。
    /// </summary>
    /// <param name="position">指针位置（客户区逻辑像素）。</param>
    /// <param name="deltaX">横向增量（逻辑像素；正值 = 向左滚动）。按行 / 按页的设备由平台先按系统设置换算成像素（见 <see cref="WheelDeltas"/>）。</param>
    /// <param name="deltaY">纵向增量（逻辑像素；正值 = 向上滚动）。</param>
    /// <param name="mode">设备报告增量的单位（换算之前）：<see cref="WheelDeltaMode.Line"/> 是普通的分格滚轮，
    /// <see cref="WheelDeltaMode.Page"/> 时增量按「窗口客户区的一页」换算，框架再按滚动容器自己的页尺寸重新换算。</param>
    /// <param name="precise">精确设备（触控板、自由滚动的高分辨率滚轮）：框架直接跟随增量，不做平滑动画。</param>
    /// <param name="phase">触控板手势相位（手指在板上）；平台没有相位信息时为 <see cref="ScrollPhase.None"/>。</param>
    /// <param name="momentumPhase">系统惯性相位（手指离开之后的惯性滚动）；有它时框架不叠加自己的惯性。</param>
    public static RawPointerEvent CreateWheel(Point position, float deltaX, float deltaY, WheelDeltaMode mode, bool precise,
        ScrollPhase phase, ScrollPhase momentumPhase, PointerButtons buttons, KeyModifiers modifiers, TimeSpan timestamp,
        PointerType device = PointerType.Mouse, long pointerId = 0) =>
        new(RawPointerEventType.Wheel, device, pointerId, position, PointerButtons.None, buttons, modifiers, timestamp, 0f,
            deltaX, deltaY, precise, mode, phase, momentumPhase);

    public RawPointerEventType Type { get; }
    public PointerType Device { get; }
    public long PointerId { get; }

    /// <summary>客户区坐标（逻辑像素）。</summary>
    public Point Position { get; }
    public PointerButtons ChangedButton { get; }
    public PointerButtons Buttons { get; }
    public KeyModifiers Modifiers { get; }
    public TimeSpan Timestamp { get; }
    public float Pressure { get; }

    /// <summary>滚动量（逻辑像素；正值 = 向左 / 向上滚动）。非有限值按 0 处理。</summary>
    public float WheelDeltaX { get; }
    public float WheelDeltaY { get; }

    /// <summary>来自触控板等精确设备（带相位，不应再做平滑）。</summary>
    public bool PreciseWheel { get; }

    /// <summary>设备报告增量的单位（增量本身已换算成逻辑像素）。</summary>
    public WheelDeltaMode WheelDeltaMode { get; }

    /// <summary>触控板手势相位（手指在板上）；没有相位信息的设备为 <see cref="ScrollPhase.None"/>。</summary>
    public ScrollPhase ScrollPhase { get; }

    /// <summary>系统惯性相位（<see cref="ScrollPhase.Began"/> / <see cref="ScrollPhase.Changed"/> / <see cref="ScrollPhase.Ended"/>）；不在惯性中为 None。</summary>
    public ScrollPhase MomentumPhase { get; }
}

public enum RawKeyEventType : byte { Down, Up }

/// <summary>按键在键盘上的位置（W3C <c>KeyboardEvent.location</c>）：区分左右修饰键与小键盘。</summary>
public enum KeyLocation : byte { Standard = 0, Left = 1, Right = 2, Numpad = 3 }

/// <summary>
/// 原始按键事件。
/// <list type="bullet">
/// <item><see cref="Key"/>：与布局无关的物理键（按 US 布局的位置命名）。</item>
/// <item><see cref="LogicalKey"/>：按当前布局的逻辑键，W3C <c>KeyboardEvent.key</c> 拼写——产生字符的键是该字符（含 Shift / AltGr 的效果，
/// 但不含 Ctrl / Alt / Meta：Ctrl+C 的逻辑键是 <c>"c"</c>），其余为键名（<c>"Enter"</c>、<c>"ArrowLeft"</c>、死键为 <c>"Dead"</c>）。
/// 平台不知道时为 null，框架按物理键推断。</item>
/// <item><see cref="Location"/>：左 / 右 / 小键盘，由物理键决定。</item>
/// </list>
/// 文本输入一律经 <see cref="IWindowEvents.OnTextInput"/> 或输入法（<see cref="TextInputMethod"/>）传递，不应从按键推断字符；
/// 被输入法处理（组合中）的按下不交给框架。
/// </summary>
public readonly struct RawKeyEvent
{
    public RawKeyEvent(RawKeyEventType type, PhysicalKey key, KeyModifiers modifiers, bool isRepeat, int nativeCode, TimeSpan timestamp)
        : this(type, key, modifiers, isRepeat, nativeCode, timestamp, null)
    {
    }

    /// <param name="logicalKey">按当前布局的逻辑键（W3C <c>KeyboardEvent.key</c>）；未知时为 null。超过 <see cref="KeyValues.MaxLength"/> 的值按未知处理。</param>
    public RawKeyEvent(RawKeyEventType type, PhysicalKey key, KeyModifiers modifiers, bool isRepeat, int nativeCode, TimeSpan timestamp,
        string? logicalKey)
    {
        Type = type; Key = key; Modifiers = modifiers; IsRepeat = isRepeat; NativeCode = nativeCode; Timestamp = timestamp;
        LogicalKey = logicalKey is { Length: > 0 } && logicalKey.Length <= KeyValues.MaxLength ? logicalKey : null;
    }

    public RawKeyEventType Type { get; }

    /// <summary>物理键。</summary>
    public PhysicalKey Key { get; }
    public KeyModifiers Modifiers { get; }
    public bool IsRepeat { get; }
    public int NativeCode { get; }
    public TimeSpan Timestamp { get; }

    /// <summary>按当前布局的逻辑键（W3C <c>KeyboardEvent.key</c>）；平台未提供时为 null。</summary>
    public string? LogicalKey { get; }

    /// <summary>按键位置（由 <see cref="Key"/> 决定）。</summary>
    public KeyLocation Location => PhysicalKeys.GetLocation(Key);
}

/// <summary>
/// 物理键（按 US 布局的位置命名，与 W3C UI Events KeyboardEvent.code 对应）。
/// 新成员只追加在末尾，已有成员的数值不变。
/// </summary>
public enum PhysicalKey : ushort
{
    Unknown = 0,
    KeyA, KeyB, KeyC, KeyD, KeyE, KeyF, KeyG, KeyH, KeyI, KeyJ, KeyK, KeyL, KeyM,
    KeyN, KeyO, KeyP, KeyQ, KeyR, KeyS, KeyT, KeyU, KeyV, KeyW, KeyX, KeyY, KeyZ,
    Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,
    Enter, Escape, Backspace, Tab, Space, Minus, Equal, BracketLeft, BracketRight, Backslash,
    Semicolon, Quote, Backquote, Comma, Period, Slash, CapsLock,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Insert, Delete, Home, End, PageUp, PageDown, ArrowLeft, ArrowRight, ArrowUp, ArrowDown,
    ShiftLeft, ShiftRight, ControlLeft, ControlRight, AltLeft, AltRight, MetaLeft, MetaRight, ContextMenu,

    // 小键盘（NumLock 关闭时这些键产生导航键名，但物理键不变）
    NumLock, Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadAdd, NumpadSubtract, NumpadMultiply, NumpadDivide, NumpadDecimal, NumpadEnter, NumpadEqual, NumpadComma,

    // ISO / JIS 布局多出的键：IntlBackslash 是 ISO 键盘左 Shift 右边的键（德语 &lt; &gt; |）
    IntlBackslash, IntlRo, IntlYen,

    PrintScreen, ScrollLock, Pause,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
}

/// <summary><see cref="PhysicalKey"/> 的分类辅助。</summary>
public static class PhysicalKeys
{
    /// <summary>已定义的最后一个成员（解析与校验用）。</summary>
    public const PhysicalKey Last = PhysicalKey.F24;

    public static KeyLocation GetLocation(PhysicalKey key) => key switch
    {
        PhysicalKey.ShiftLeft or PhysicalKey.ControlLeft or PhysicalKey.AltLeft or PhysicalKey.MetaLeft => KeyLocation.Left,
        PhysicalKey.ShiftRight or PhysicalKey.ControlRight or PhysicalKey.AltRight or PhysicalKey.MetaRight => KeyLocation.Right,
        _ => IsNumpad(key) ? KeyLocation.Numpad : KeyLocation.Standard,
    };

    /// <summary>小键盘上的键（不含 NumLock 本身，与 W3C 一致：NumLock 的 location 为 standard）。</summary>
    public static bool IsNumpad(PhysicalKey key) => key >= PhysicalKey.Numpad0 && key <= PhysicalKey.NumpadComma;

    /// <summary>修饰键与锁定键（单独按下不算「键盘交互」，也不是快捷键的主键）。</summary>
    public static bool IsModifier(PhysicalKey key) =>
        key is PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight or PhysicalKey.ControlLeft or PhysicalKey.ControlRight
            or PhysicalKey.AltLeft or PhysicalKey.AltRight or PhysicalKey.MetaLeft or PhysicalKey.MetaRight
            or PhysicalKey.CapsLock or PhysicalKey.NumLock or PhysicalKey.ScrollLock;
}

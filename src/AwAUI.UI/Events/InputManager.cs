using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>命中测试的接入点：布局集成后由它把客户区坐标映射到最内层元素。</summary>
public abstract class HitTester
{
    public abstract Element? HitTest(Point position);
}

/// <summary>
/// 输入派发（design/05「事件」）：DOM 式的捕获 → 目标 → 冒泡；指针捕获；悬停跟踪（沿祖先链维护 <c>:hover</c>，
/// 派发不冒泡的 <c>pointerenter</c> / <c>pointerleave</c>）；按下态（<c>:active</c>）；点击合成；键盘派发与默认动作
/// （Tab 移动焦点、按钮的回车/空格激活、输入框的提交与删除）。
/// <para>
/// 命中测试尚未接入：派发方法接受调用方给出的命中结果；也可设置 <see cref="HitTester"/> 后调用不带命中结果的重载。
/// </para>
/// </summary>
public sealed partial class InputManager
{
    readonly UIRoot _root;
    readonly Dictionary<long, Element> _captures = new();
    readonly Dictionary<long, Element> _pressTargets = new();
    readonly List<Element> _path = new();
    Element? _hover;
    Element? _active;
    long _activePointer = long.MinValue;
    ButtonElement? _spacePressed;

    internal InputManager(UIRoot root) => _root = root;

    /// <summary>命中测试（布局集成后设置）。</summary>
    public HitTester? HitTester { get; set; }

    /// <summary>滚动系统（帧管线挂接时设置）：滚轮、键盘滚动、触摸拖动滚动与滚动条操作（design/21）。</summary>
    internal ScrollManager? Scrolling { get; set; }

    bool _focusingFromPointer;

    /// <summary>当前悬停的最内层元素。</summary>
    public Element? HoveredElement => _hover;

    /// <summary>当前按下（<c>:active</c>）的最内层元素。</summary>
    public Element? ActiveElement => _active;

    // ------------------------------------------------------------ 指针

    /// <summary>用 <see cref="HitTester"/> 命中后派发。</summary>
    public void DispatchPointer(in RawPointerEvent raw) => DispatchPointer(raw, HitTester?.HitTest(raw.Position));

    /// <summary>派发原始指针事件；<paramref name="hit"/> 为该位置命中的最内层元素（null 表示没有命中任何元素）。</summary>
    public void DispatchPointer(in RawPointerEvent raw, Element? hit)
    {
        _root.BeginDispatch();
        try
        {
            if (hit is not null && !ReferenceEquals(hit.Root, _root)) hit = null;
            if (hit is not null && hit.IsTextNode) hit = hit.Parent;
            // 滚动系统先看：滚动条上的按下与拖动、已经变成滚动的触摸拖动都不再派发给元素
            if (Scrolling is { } scrolling && raw.Type != RawPointerEventType.Wheel && scrolling.PreviewPointer(raw, hit)) return;
            _captures.TryGetValue(raw.PointerId, out var captured);
            var target = captured ?? hit;
            switch (raw.Type)
            {
                case RawPointerEventType.Enter:
                case RawPointerEventType.Move:
                    if (raw.Device != PointerType.Touch || raw.Buttons != PointerButtons.None) UpdateHover(target, raw);
                    if (target is not null) DispatchPointerEvent(target, "pointermove", EventKind.PointerMove, EventKind.PointerMoveCapture, raw);
                    TextPointerMove(raw);
                    break;

                case RawPointerEventType.Down:
                {
                    _root.FocusManager.LastInteractionWasKeyboard = false;
                    if (raw.Device == PointerType.Touch) UpdateHover(target, raw);
                    if (target is null)
                    {
                        _root.FocusManager.ClearFocus();
                        break;
                    }
                    bool proceed = DispatchPointerEvent(target, "pointerdown", EventKind.PointerDown, EventKind.PointerDownCapture, raw);
                    if (!target.IsAttached) break;
                    if (_active is null)
                    {
                        _activePointer = raw.PointerId;
                        SetActive(target);
                    }
                    _pressTargets[raw.PointerId] = target;
                    if (proceed)
                    {
                        var focusable = FindFocusable(target);
                        if (focusable is not null)
                        {
                            _focusingFromPointer = true;
                            try
                            {
                                _root.FocusManager.Focus(focusable, FocusOrigin.Pointer);
                            }
                            finally
                            {
                                _focusingFromPointer = false;
                            }
                        }
                        else if (!IsInsideDisabled(target)) _root.FocusManager.ClearFocus();
                        if (focusable is InputElement input && ReferenceEquals(_root.FocusManager.FocusedElement, input)) TextPointerDown(input, raw);
                    }
                    break;
                }

                case RawPointerEventType.Up:
                {
                    if (target is not null) DispatchPointerEvent(target, "pointerup", EventKind.PointerUp, EventKind.PointerUpCapture, raw);
                    if (raw.PointerId == _activePointer) ClearActive();
                    if (_pressTargets.TryGetValue(raw.PointerId, out var press))
                    {
                        _pressTargets.Remove(raw.PointerId);
                        if (raw.ChangedButton == PointerButtons.Left && target is not null && press.IsAttached && target.IsAttached)
                        {
                            var clickTarget = CommonAncestor(press, target);
                            if (clickTarget is not null && !IsInsideDisabled(clickTarget))
                                DispatchPointerEvent(clickTarget, "click", EventKind.Click, EventKind.ClickCapture, raw);
                        }
                    }
                    _captures.Remove(raw.PointerId);
                    TextPointerUp(raw.PointerId);
                    if (raw.Device == PointerType.Touch) UpdateHover(null, raw);
                    break;
                }

                case RawPointerEventType.Cancel:
                    if (target is not null) DispatchPointerEvent(target, "pointercancel", EventKind.PointerCancel, null, raw);
                    if (raw.PointerId == _activePointer) ClearActive();
                    _pressTargets.Remove(raw.PointerId);
                    _captures.Remove(raw.PointerId);
                    TextPointerUp(raw.PointerId);
                    if (raw.Device == PointerType.Touch) UpdateHover(null, raw);
                    break;

                case RawPointerEventType.Leave:
                    if (captured is null) UpdateHover(null, raw);
                    break;

                case RawPointerEventType.Wheel:
                    // 先派发可取消的 wheel，再由滚动系统滚动（design/21）
                    Scrolling?.OnWheel(raw, target);
                    break;
            }
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    /// <summary>
    /// 按下变成了滚动（触摸拖动越过容差）：向按下的目标派发 <c>pointercancel</c>，清除按下态与指针捕获，之后的抬起不再合成 click。
    /// </summary>
    internal void CancelPointerForScroll(in RawPointerEvent raw)
    {
        _captures.TryGetValue(raw.PointerId, out var captured);
        _pressTargets.TryGetValue(raw.PointerId, out var press);
        var target = captured ?? press;
        if (target is { IsAttached: true }) DispatchPointerEvent(target, "pointercancel", EventKind.PointerCancel, null, raw);
        if (raw.PointerId == _activePointer) ClearActive();
        _pressTargets.Remove(raw.PointerId);
        _captures.Remove(raw.PointerId);
        TextPointerUp(raw.PointerId);
    }

    /// <summary>焦点进入元素（非指针聚焦）：下一帧布局之后把它滚动到可见。</summary>
    void RevealFocus(Element element)
    {
        if (!_focusingFromPointer) Scrolling?.RevealFocused(element);
    }

    /// <summary>测试与无障碍用的便捷方法：在 <paramref name="target"/> 上模拟一次鼠标左键按下 + 抬起。</summary>
    public void Click(Element target, Point position = default)
    {
        var down = new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, position, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero);
        var up = new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, position, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero);
        DispatchPointer(down, target);
        DispatchPointer(up, target);
    }

    /// <summary>激活元素（与键盘回车/空格、无障碍「点按」动作相同）：派发合成的 <c>click</c>。禁用时不派发。</summary>
    public bool Activate(Element target)
    {
        _root.BeginDispatch();
        try
        {
            if (!ReferenceEquals(target.Root, _root) || IsInsideDisabled(target)) return false;
            var e = new PointerEvent("click") { PointerId = -1, IsSynthetic = true, Button = PointerButtons.Left };
            return Dispatch(target, e, EventKind.Click, EventKind.ClickCapture);
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    bool DispatchPointerEvent(Element target, string type, EventKind kind, EventKind? capture, in RawPointerEvent raw)
    {
        var e = CreatePointerEvent(type, raw, bubbles: true);
        return Dispatch(target, e, kind, capture);
    }

    static PointerEvent CreatePointerEvent(string type, in RawPointerEvent raw, bool bubbles) => new(type, bubbles)
    {
        Position = raw.Position,
        PointerId = raw.PointerId,
        PointerType = raw.Device,
        Button = raw.ChangedButton,
        Buttons = raw.Buttons,
        Modifiers = raw.Modifiers,
        Pressure = raw.Pressure,
        Timestamp = raw.Timestamp,
    };

    /// <summary>
    /// 更新悬停：离开的元素（由内向外）清除 <c>:hover</c> 并收到 <c>pointerleave</c>，进入的元素（由外向内）设置 <c>:hover</c> 并收到
    /// <c>pointerenter</c>。两条链的公共祖先不变。
    /// </summary>
    public void UpdateHover(Element? hit, in RawPointerEvent raw)
    {
        if (hit is not null && !ReferenceEquals(hit.Root, _root)) hit = null;
        var old = _hover;
        if (ReferenceEquals(old, hit)) return;
        var lca = CommonAncestor(old, hit);
        _hover = hit;
        for (var e = old; e is not null && !ReferenceEquals(e, lca); e = e.Parent)
        {
            e.SetState(PseudoStates.Hover, false);
            Dispatch(e, CreatePointerEvent("pointerleave", raw, bubbles: false), EventKind.PointerLeave, null);
        }
        var entering = new List<Element>();
        for (var e = hit; e is not null && !ReferenceEquals(e, lca); e = e.Parent) entering.Add(e);
        for (int i = entering.Count - 1; i >= 0; i--)
        {
            var e = entering[i];
            e.SetState(PseudoStates.Hover, true);
            Dispatch(e, CreatePointerEvent("pointerenter", raw, bubbles: false), EventKind.PointerEnter, null);
        }
    }

    void SetActive(Element target)
    {
        _active = target;
        for (var e = target; e is not null; e = e.Parent) e.SetState(PseudoStates.Active, true);
    }

    void ClearActive()
    {
        for (var e = _active; e is not null; e = e.Parent) e.SetState(PseudoStates.Active, false);
        _active = null;
        _activePointer = long.MinValue;
    }

    static Element? FindFocusable(Element target)
    {
        for (var e = target; e is not null; e = e.Parent)
        {
            if (e.IsFocusable) return e;
            if (e.IsDisabled) return null;
        }
        return null;
    }

    static bool IsInsideDisabled(Element target)
    {
        for (var e = target; e is not null; e = e.Parent)
            if (e.IsDisabled && e.CanBeDisabled) return true;
        return false;
    }

    // ------------------------------------------------------------ 指针捕获

    internal void SetCapture(long pointerId, Element element)
    {
        if (!ReferenceEquals(element.Root, _root)) return;
        _captures[pointerId] = element;
    }

    internal void ReleaseCapture(long pointerId, Element element)
    {
        if (_captures.TryGetValue(pointerId, out var e) && ReferenceEquals(e, element)) _captures.Remove(pointerId);
    }

    internal Element? GetCapture(long pointerId) => _captures.TryGetValue(pointerId, out var e) ? e : null;

    // ------------------------------------------------------------ 键盘与文本

    /// <summary>
    /// 派发按键到焦点元素（没有焦点时到根元素）。未被 <see cref="Event.PreventDefault"/> 时执行默认动作：
    /// Tab / Shift+Tab 移动焦点；按钮上回车（按下时）与空格（抬起时）激活；输入框上的编辑键、剪贴板快捷键与回车提交
    /// （见 <see cref="InputElement"/>）。<paramref name="text"/> 是旧式调用方给出的按键文本，只用于推断逻辑键。
    /// </summary>
    public void DispatchKey(in RawKeyEvent raw, string? text = null)
    {
        _root.BeginDispatch();
        try
        {
            if (!PhysicalKeys.IsModifier(raw.Key)) _root.FocusManager.LastInteractionWasKeyboard = true;
            var target = _root.FocusManager.FocusedElement ?? _root.RootElement;
            bool down = raw.Type == RawKeyEventType.Down;
            var e = new KeyEvent(down ? "keydown" : "keyup")
            {
                Key = KeyNames.Get(raw, text),
                PhysicalKey = raw.Key,
                Modifiers = raw.Modifiers,
                Location = raw.Location,
                Text = text,
                IsRepeat = raw.IsRepeat,
                Timestamp = raw.Timestamp,
            };
            bool proceed = down
                ? Dispatch(target, e, EventKind.KeyDown, EventKind.KeyDownCapture)
                : Dispatch(target, e, EventKind.KeyUp, EventKind.KeyUpCapture);
            if (proceed) DefaultKeyAction(target, raw, e.Key, down);
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    void DefaultKeyAction(Element target, in RawKeyEvent raw, string logicalKey, bool down)
    {
        const KeyModifiers commandMods = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        if (!target.IsAttached) return;
        if (down)
        {
            if (target is InputElement input && EditingKeyAction(input, raw, logicalKey)) return;
            switch (raw.Key)
            {
                case PhysicalKey.Tab when (raw.Modifiers & commandMods) == 0:
                    _root.FocusManager.MoveFocus(forward: (raw.Modifiers & KeyModifiers.Shift) == 0);
                    break;
                case PhysicalKey.Enter or PhysicalKey.NumpadEnter when target is ButtonElement b:
                    if (!b.IsDisabled) Activate(b);
                    break;
                case PhysicalKey.Space when target is ButtonElement b && !raw.IsRepeat:
                    if (!b.IsDisabled)
                    {
                        _spacePressed = b;
                        b.SetState(PseudoStates.Active, true);
                    }
                    break;
                case PhysicalKey.Space when target is ButtonElement:
                    break;
                default:
                    // 没有被处理的导航键滚动焦点所在的滚动容器
                    Scrolling?.OnKey(target, raw);
                    break;
            }
        }
        else if (raw.Key == PhysicalKey.Space && target is ButtonElement sb && ReferenceEquals(_spacePressed, sb))
        {
            _spacePressed = null;
            if (_active is null || !sb.IsInclusiveAncestorOf(_active)) sb.SetState(PseudoStates.Active, false);
            if (!sb.IsDisabled) Activate(sb);
        }
    }

    /// <summary>
    /// 文本输入（平台的 <c>OnTextInput</c>，即没有输入法客户端时的提交文本）：替换焦点输入框的选区（组合中则替换预编辑文本）并派发 <c>input</c>。
    /// </summary>
    public void DispatchTextInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _root.BeginDispatch();
        try
        {
            if (_root.FocusManager.FocusedElement is InputElement input && !input.IsDisabled && !input.ReadOnly)
                CommitText(input, text);
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    /// <summary>焦点离开 <paramref name="element"/>：取消其上尚未抬起的空格激活（与浏览器一致，之后的抬起不再触发 click）；输入框提交进行中的组合并断开输入法客户端。</summary>
    internal void OnFocusLeaving(Element element)
    {
        if (element is InputElement input) OnInputFocusLeaving(input);
        var pressed = _spacePressed;
        if (pressed is null || !ReferenceEquals(pressed, element)) return;
        _spacePressed = null;
        if (_active is null || !pressed.IsInclusiveAncestorOf(_active)) pressed.SetState(PseudoStates.Active, false);
    }

    // ------------------------------------------------------------ 通用派发

    /// <summary>
    /// 以 DOM 语义派发自定义事件：捕获阶段调用 <paramref name="captureKind"/> 处理器，目标与冒泡阶段调用 <paramref name="kind"/> 处理器。
    /// 返回 false 表示默认动作被阻止。
    /// </summary>
    internal bool Dispatch<T>(Element target, T e, EventKind kind, EventKind? captureKind) where T : Event
    {
        var path = _path.Count == 0 ? _path : new List<Element>();
        try
        {
            for (var el = target; el is not null; el = el.Parent) path.Add(el);
            e.Target = target;
            if (captureKind is { } ck)
            {
                e.Phase = EventPhase.Capturing;
                for (int i = path.Count - 1; i >= 1 && !e.IsPropagationStopped; i--) Invoke(path[i], ck, e);
            }
            if (!e.IsPropagationStopped)
            {
                e.Phase = EventPhase.AtTarget;
                // DOM：目标上的捕获监听器与非捕获监听器分两轮调用，每轮之前都检查 stop propagation 标志
                if (captureKind is { } ck2) Invoke(target, ck2, e);
                if (!e.IsPropagationStopped) Invoke(target, kind, e);
            }
            if (e.Bubbles)
            {
                e.Phase = EventPhase.Bubbling;
                for (int i = 1; i < path.Count && !e.IsPropagationStopped; i++) Invoke(path[i], kind, e);
            }
            e.Phase = EventPhase.None;
            e.CurrentTarget = null;
            return !e.DefaultPrevented;
        }
        finally
        {
            path.Clear();
        }
    }

    void Invoke<T>(Element el, EventKind kind, T e) where T : Event
    {
        if (el.GetHandler(kind) is not Action<T> handler) return;
        e.CurrentTarget = el;
        try
        {
            handler(e);
        }
        catch (Exception ex)
        {
            _root.ReportError(ex, el.Owner, ErrorPhase.EventHandler);
        }
    }

    /// <summary>
    /// 向 <paramref name="target"/> 派发 <c>click</c>（冒泡）并返回默认动作是否未被阻止。供测试与无障碍动作使用。
    /// </summary>
    public bool DispatchClick(Element target)
    {
        _root.BeginDispatch();
        try
        {
            return Dispatch(target, new PointerEvent("click"), EventKind.Click, EventKind.ClickCapture);
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    /// <summary>两个元素最近的公共祖先（含自身）；任一为 null 或不在同一棵树时为 null。</summary>
    public static Element? CommonAncestor(Element? a, Element? b)
    {
        if (a is null || b is null) return null;
        int da = Depth(a), db = Depth(b);
        while (da > db) { a = a!.Parent; da--; }
        while (db > da) { b = b!.Parent; db--; }
        while (!ReferenceEquals(a, b))
        {
            a = a!.Parent;
            b = b!.Parent;
        }
        return a;
    }

    static int Depth(Element e)
    {
        int d = 0;
        for (var p = e.Parent; p is not null; p = p.Parent) d++;
        return d;
    }

    // ------------------------------------------------------------ 树变更

    internal void OnSubtreeRemoved(Element parent, Element removed)
    {
        if (_hover is not null && removed.IsInclusiveAncestorOf(_hover))
        {
            for (var e = _hover; e is not null; e = e.Parent) e.SetState(PseudoStates.Hover, false);
            _hover = parent; // 指针仍在父元素之上
        }
        if (_active is not null && removed.IsInclusiveAncestorOf(_active))
        {
            for (var e = _active; e is not null; e = e.Parent) e.SetState(PseudoStates.Active, false);
            _active = parent;
        }
        if (_spacePressed is not null && removed.IsInclusiveAncestorOf(_spacePressed)) _spacePressed = null;
        RemoveWithin(_captures, removed);
        RemoveWithin(_pressTargets, removed);
        OnTextSubtreeRemoved(removed);
    }

    static void RemoveWithin(Dictionary<long, Element> map, Element removed)
    {
        if (map.Count == 0) return;
        List<long>? keys = null;
        foreach (var kv in map)
            if (removed.IsInclusiveAncestorOf(kv.Value)) (keys ??= new List<long>()).Add(kv.Key);
        if (keys is null) return;
        foreach (var k in keys) map.Remove(k);
    }
}

/// <summary>按键事件的逻辑键（W3C <c>KeyboardEvent.key</c>）：优先取平台给出的逻辑键，否则按旧式按键文本或 US 布局推断。</summary>
internal static class KeyNames
{
    public static string Get(in RawKeyEvent raw, string? text)
    {
        if (raw.LogicalKey is { } logical) return logical;
        if (!string.IsNullOrEmpty(text) && text!.Length <= 2 && !char.IsControl(text[0])) return text;
        return KeyValues.FromPhysicalKey(raw.Key, raw.Modifiers);
    }
}

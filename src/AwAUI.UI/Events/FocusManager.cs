using System;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 焦点管理：唯一的焦点元素、<c>:focus</c> / <c>:focus-visible</c> / <c>:focus-within</c> 维护、Tab 顺序与焦点事件。
/// <para>
/// Tab 顺序：可聚焦且 TabIndex ≥ 0 的元素；TabIndex &gt; 0 的按值升序排在前面（同值按树序），其后是 TabIndex = 0 或默认可聚焦
/// （按钮、输入框）的元素按树序；首尾循环。
/// </para>
/// <para>
/// focus-visible 启发式（与浏览器一致）：键盘导航获得的焦点总是可见；指针获得的焦点不可见（文本输入框除外）；
/// 程序聚焦跟随最近一次交互方式（初始视为键盘）。
/// </para>
/// <para>事件顺序：旧焦点 <c>blur</c> → <c>focusout</c>（输入框内容有变化时其后 <c>change</c>）→ 新焦点 <c>focus</c> → <c>focusin</c>。</para>
/// </summary>
public sealed class FocusManager
{
    readonly UIRoot _root;
    Element? _focused;

    internal FocusManager(UIRoot root) => _root = root;

    /// <summary>当前焦点元素。</summary>
    public Element? FocusedElement => _focused;

    /// <summary>最近一次交互是否来自键盘（决定程序聚焦的 focus-visible）。</summary>
    public bool LastInteractionWasKeyboard { get; set; } = true;

    /// <summary>聚焦 <paramref name="element"/>；不可聚焦（未挂载、禁用、没有 TabIndex 且非默认可聚焦）时返回 false。</summary>
    public bool Focus(Element element, FocusOrigin origin = FocusOrigin.Programmatic)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        // 程序聚焦也会派发事件：处理器中未被错误边界捕获的异常在本调用结束时抛出（与其他派发入口一致）
        _root.BeginDispatch();
        try
        {
            return FocusCore(element, origin);
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    bool FocusCore(Element element, FocusOrigin origin)
    {
        if (!ReferenceEquals(element.Root, _root) || !element.IsFocusable) return false;
        bool visible = origin switch
        {
            FocusOrigin.Keyboard => true,
            FocusOrigin.Pointer => element is InputElement,
            _ => LastInteractionWasKeyboard || element is InputElement,
        };
        if (ReferenceEquals(_focused, element))
        {
            if (origin == FocusOrigin.Keyboard) element.SetState(PseudoStates.FocusVisible, true);
            return true;
        }

        var old = _focused;
        if (old is not null)
        {
            _focused = null;
            old.SetState(PseudoStates.Focus | PseudoStates.FocusVisible, false);
            _root.Input.OnFocusLeaving(old);
            UpdateFocusWithin(old, element);
            FireBlur(old, element, origin);
            // 处理器可能已经移动了焦点或移除了目标
            if (_focused is not null || !element.IsFocusable || !ReferenceEquals(element.Root, _root))
            {
                UpdateFocusWithin(element, null);
                if (_focused is not null) UpdateFocusWithin(null, _focused);
                return ReferenceEquals(_focused, element);
            }
        }
        else
        {
            UpdateFocusWithin(null, element);
        }

        _focused = element;
        element.SetState(PseudoStates.Focus, true);
        element.SetState(PseudoStates.FocusVisible, visible);
        if (element is InputElement input) input.OnFocused();
        _root.Input.OnFocusEntered(element);
        _root.Input.Dispatch(element, new FocusEvent("focus", bubbles: false) { RelatedTarget = old, Origin = origin }, EventKind.Focus, null);
        _root.Input.Dispatch(element, new FocusEvent("focusin", bubbles: true) { RelatedTarget = old, Origin = origin }, EventKind.FocusIn, null);
        return true;
    }

    /// <summary>移除焦点（触发 blur / focusout）。</summary>
    public void ClearFocus()
    {
        var old = _focused;
        if (old is null) return;
        _root.BeginDispatch();
        try
        {
            _focused = null;
            old.SetState(PseudoStates.Focus | PseudoStates.FocusVisible, false);
            _root.Input.OnFocusLeaving(old);
            UpdateFocusWithin(old, null);
            FireBlur(old, null, FocusOrigin.Programmatic);
        }
        finally
        {
            _root.EndDispatch();
        }
    }

    void FireBlur(Element old, Element? next, FocusOrigin origin)
    {
        _root.Input.Dispatch(old, new FocusEvent("blur", bubbles: false) { RelatedTarget = next, Origin = origin }, EventKind.Blur, null);
        _root.Input.Dispatch(old, new FocusEvent("focusout", bubbles: true) { RelatedTarget = next, Origin = origin }, EventKind.FocusOut, null);
        if (old is InputElement input && input.TakeChangedSinceFocus() && old.IsAttached)
            _root.Input.Dispatch(old, new InputEvent("change", input.Value), EventKind.Change, null);
    }

    /// <summary>只切换两条祖先链不共享的部分，避免公共祖先上多余的状态翻转。</summary>
    static void UpdateFocusWithin(Element? oldEl, Element? newEl)
    {
        var lca = InputManager.CommonAncestor(oldEl, newEl);
        for (var e = oldEl; e is not null && !ReferenceEquals(e, lca); e = e.Parent)
            e.SetState(PseudoStates.FocusWithin, false);
        for (var e = newEl; e is not null && !ReferenceEquals(e, lca); e = e.Parent)
            e.SetState(PseudoStates.FocusWithin, true);
    }

    /// <summary>按 Tab 顺序移到下一个（<paramref name="forward"/> 为 false 时上一个）可聚焦元素。没有可聚焦元素时返回 false。</summary>
    public bool MoveFocus(bool forward = true)
    {
        var order = CollectTabOrder(out int insertion);
        if (order.Count == 0) return false;
        int idx = _focused is null ? -1 : order.IndexOf(_focused);
        Element next;
        if (idx >= 0)
        {
            next = order[((idx + (forward ? 1 : -1)) % order.Count + order.Count) % order.Count];
        }
        else if (_focused is not null && insertion >= 0)
        {
            // 当前焦点不在 Tab 顺序中（TabIndex &lt; 0）：从它在树中的位置继续
            next = forward ? order[insertion % order.Count] : order[(insertion - 1 + order.Count) % order.Count];
        }
        else
        {
            next = forward ? order[0] : order[order.Count - 1];
        }
        return Focus(next, FocusOrigin.Keyboard);
    }

    /// <summary>当前的 Tab 顺序。</summary>
    public IReadOnlyList<Element> GetTabOrder() => CollectTabOrder(out _);

    List<Element> CollectTabOrder(out int insertion)
    {
        insertion = -1;
        var positive = new List<Element>();
        var zero = new List<Element>();
        var stack = new Stack<Element>();
        stack.Push(_root.RootElement);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (ReferenceEquals(e, _focused) && !e.IsTabStop) insertion = zero.Count;
            if (e.IsTabStop)
            {
                if ((e.TabIndex ?? 0) > 0) positive.Add(e);
                else zero.Add(e);
            }
            var ch = e.Children;
            for (int i = ch.Count - 1; i >= 0; i--) stack.Push(ch[i]);
        }
        // 稳定排序（按 TabIndex 升序，同值保持树序）
        if (positive.Count > 1)
        {
            var keyed = new (int TabIndex, int Order, Element Element)[positive.Count];
            for (int i = 0; i < keyed.Length; i++) keyed[i] = (positive[i].TabIndex!.Value, i, positive[i]);
            Array.Sort(keyed, (a, b) => a.TabIndex != b.TabIndex ? a.TabIndex.CompareTo(b.TabIndex) : a.Order.CompareTo(b.Order));
            for (int i = 0; i < keyed.Length; i++) positive[i] = keyed[i].Element;
        }
        if (insertion >= 0) insertion += positive.Count;
        positive.AddRange(zero);
        return positive;
    }

    /// <summary>焦点所在的子树被移除：静默清除焦点（不派发事件，处理器所属的组件可能已卸载）。</summary>
    internal void OnSubtreeRemoved(Element parent, Element removed)
    {
        var f = _focused;
        if (f is null || !removed.IsInclusiveAncestorOf(f)) return;
        _focused = null;
        f.SetState(PseudoStates.Focus | PseudoStates.FocusVisible, false);
        for (var e = f; e is not null; e = e.Parent) e.SetState(PseudoStates.FocusWithin, false);
        for (var e = parent; e is not null; e = e.Parent) e.SetState(PseudoStates.FocusWithin, false);
    }
}

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
    Element? _removedFocus;

    internal FocusManager(UIRoot root) => _root = root;

    /// <summary>当前焦点元素。</summary>
    public Element? FocusedElement => _focused;

    /// <summary>最近一次交互是否来自键盘（决定程序聚焦的 focus-visible）。</summary>
    public bool LastInteractionWasKeyboard { get; set; } = true;

    /// <summary>
    /// 焦点变化之后（<c>focus</c> / <c>focusin</c> 派发完毕，或焦点被清除）在根级报告：旧焦点、新焦点与来源。
    /// DismissableLayer 据此判断焦点移出、FocusScope 据此把焦点拉回作用域。焦点元素所在子树被移除时在提交阶段之后报告
    /// （<see cref="FocusChangedEventArgs.ElementRemoved"/>）。处理器中可以再次移动焦点。
    /// </summary>
    public event Action<FocusChangedEventArgs>? FocusChanged;

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

    /// <summary>
    /// 按选项聚焦（见 <see cref="FocusOptions"/>）：<see cref="FocusOptions.PreventScroll"/> 时不滚动到可见；<see cref="FocusOptions.SelectText"/>
    /// 时聚焦输入框后全选；<see cref="FocusOptions.FocusVisible"/> 覆盖 <c>:focus-visible</c> 的启发式。
    /// </summary>
    public bool Focus(Element element, FocusOptions options)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        options ??= FocusOptions.Default;
        _root.BeginDispatch();
        bool savedPrevent = _root.Input.PreventRevealOnFocus;
        try
        {
            if (options.PreventScroll) _root.Input.PreventRevealOnFocus = true;
            bool ok = FocusCore(element, options.Origin);
            if (ok && ReferenceEquals(_focused, element))
            {
                if (options.FocusVisible is bool v) element.SetState(PseudoStates.FocusVisible, v);
                if (options.SelectText && element is InputElement input) input.SelectAll();
            }
            return ok;
        }
        finally
        {
            _root.Input.PreventRevealOnFocus = savedPrevent;
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
        // 处理器可能已经把焦点移走：那次移动自己会报告
        if (ReferenceEquals(_focused, element)) RaiseFocusChanged(old, element, origin, removed: false);
        return true;
    }

    void RaiseFocusChanged(Element? old, Element? next, FocusOrigin origin, bool removed)
    {
        var handler = FocusChanged;
        if (handler is null) return;
        var args = new FocusChangedEventArgs(old, next, origin, removed);
        foreach (Action<FocusChangedEventArgs> h in handler.GetInvocationList())
        {
            try
            {
                h(args);
            }
            catch (Exception ex)
            {
                _root.ReportError(ex, null, ErrorPhase.EventHandler);
            }
        }
    }

    /// <summary>提交阶段之后：报告本帧中因子树移除而被静默清除的焦点。</summary>
    internal void FlushRemovedFocus()
    {
        var removed = _removedFocus;
        if (removed is null) return;
        _removedFocus = null;
        if (_focused is not null) return; // 之后已经有了新的焦点
        _root.BeginDispatch();
        try
        {
            RaiseFocusChanged(removed, null, FocusOrigin.Programmatic, removed: true);
        }
        finally
        {
            _root.EndDispatch();
        }
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
            if (_focused is null) RaiseFocusChanged(old, null, FocusOrigin.Programmatic, removed: false);
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

    /// <summary>当前的 Tab 顺序（整棵树；跳过 <c>display: none</c> 的子树与 <c>visibility</c> 不为 visible 的元素）。</summary>
    public IReadOnlyList<Element> GetTabOrder() => CollectTabOrder(out _);

    /// <summary>
    /// <paramref name="scope"/> 子树内（不含 <paramref name="scope"/> 自身）的 Tab 顺序：只含可 Tab 到达且被渲染、可见的元素，
    /// 正 TabIndex 在前（升序，同值按树序），其后按树序。供 FocusScope 计算首尾与回绕。
    /// </summary>
    public IReadOnlyList<Element> GetTabOrder(Element scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (!ReferenceEquals(scope.Root, _root)) return Array.Empty<Element>();
        return CollectTabOrder(scope, includeScope: false, out _);
    }

    List<Element> CollectTabOrder(out int insertion) => CollectTabOrder(_root.RootElement, includeScope: true, out insertion);

    /// <summary>
    /// Tab 顺序的候选是否被渲染：有帧管线且已有计算样式时跳过 <c>display: none</c>（整棵子树）与不可见的元素；
    /// 还没有计算样式（本帧新插入）的元素保守地保留。
    /// </summary>
    static bool SkipSubtree(Element e) => e.Render?.Style is { } s && s.Display == AwAUI.Css.Values.CssDisplay.None;

    static bool Invisible(Element e) => e.Render?.Style is { } s && s.Visibility != AwAUI.Css.Values.CssVisibility.Visible;

    List<Element> CollectTabOrder(Element top, bool includeScope, out int insertion)
    {
        insertion = -1;
        var positive = new List<Element>();
        var zero = new List<Element>();
        var stack = new Stack<Element>();
        if (includeScope) stack.Push(top);
        else
        {
            if (SkipSubtree(top)) return positive;
            var ch0 = top.Children;
            for (int i = ch0.Count - 1; i >= 0; i--) stack.Push(ch0[i]);
        }
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (SkipSubtree(e)) continue;
            if (ReferenceEquals(e, _focused) && !e.IsTabStop) insertion = zero.Count;
            if (e.IsTabStop && !Invisible(e))
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
        _removedFocus = f;
        f.SetState(PseudoStates.Focus | PseudoStates.FocusVisible, false);
        for (var e = f; e is not null; e = e.Parent) e.SetState(PseudoStates.FocusWithin, false);
        for (var e = parent; e is not null; e = e.Parent) e.SetState(PseudoStates.FocusWithin, false);
    }
}

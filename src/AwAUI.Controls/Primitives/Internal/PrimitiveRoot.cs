using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 每个 <see cref="UIRoot"/> 一份的原语状态：可关闭层的栈与焦点作用域的栈（Radix 中它们是 document 级的上下文）。
/// 有层时订阅根级的预览输入与焦点变化，并把它们分派给层栈：Escape 只交给最上层，外部指针按下 / 焦点移出交给每个「目标在其外部」的层。
/// </summary>
internal sealed class PrimitiveRoot
{
    static readonly ConditionalWeakTable<UIRoot, PrimitiveRoot> s_roots = new();

    readonly UIRoot _root;
    bool _subscribed;

    PrimitiveRoot(UIRoot root) => _root = root;

    public static PrimitiveRoot For(UIRoot root) => s_roots.GetValue(root, r => new PrimitiveRoot(r));

    public UIRoot Root => _root;

    public DismissableLayerStack<Element> Layers { get; } = new();

    public FocusScopeStack<Element> FocusScopes { get; } = new();

    /// <summary>焦点作用域模型 → 组件（判断组件树中的嵌套关系）。</summary>
    public Dictionary<FocusScopeModel<Element>, Component> FocusScopeOwners { get; } = new();

    /// <summary>
    /// 登记焦点作用域：栈中已有组件树中嵌套在 <paramref name="owner"/> 之内的作用域（嵌套的先挂载）时放在它们之下，否则放在栈顶。
    /// </summary>
    public void AddFocusScope(FocusScopeModel<Element> model, Component owner)
    {
        FocusScopeOwners[model] = owner;
        foreach (var existing in FocusScopes.Scopes)
        {
            if (ReferenceEquals(existing, model) || !FocusScopeOwners.TryGetValue(existing, out var c)) continue;
            for (var p = c.ParentComponent; p is not null; p = p.ParentComponent)
            {
                if (!ReferenceEquals(p, owner)) continue;
                FocusScopes.AddBelow(model, existing);
                return;
            }
        }
        FocusScopes.Add(model);
    }

    public void RemoveFocusScope(FocusScopeModel<Element> model)
    {
        FocusScopeOwners.Remove(model);
        FocusScopes.Remove(model);
    }

    public void AddLayer(DismissableLayer<Element> layer)
    {
        Layers.Push(layer);
        if (_subscribed) return;
        _subscribed = true;
        _root.Input.PreviewKeyDown += OnPreviewKeyDown;
        _root.Input.PreviewPointerDown += OnPreviewPointerDown;
        _root.FocusManager.FocusChanged += OnFocusChanged;
    }

    public void RemoveLayer(DismissableLayer<Element> layer)
    {
        Layers.Remove(layer);
        if (Layers.Layers.Count > 0 || !_subscribed) return;
        _subscribed = false;
        _root.Input.PreviewKeyDown -= OnPreviewKeyDown;
        _root.Input.PreviewPointerDown -= OnPreviewPointerDown;
        _root.FocusManager.FocusChanged -= OnFocusChanged;
    }

    void OnPreviewKeyDown(KeyEvent e)
    {
        if (e.Key != "Escape" || e.DefaultPrevented) return;
        Layers.DispatchEscape(e, out bool dismissed);
        // 与 Radix 相同：关闭了层就阻止默认动作（例如全屏退出），但事件照常派发
        if (dismissed) e.PreventDefault();
    }

    void OnPreviewPointerDown(PointerEvent e)
    {
        // 没有命中（或被模态层屏蔽）时目标视为根元素，即「外部」
        var target = e.Target ?? _root.RootElement;
        Layers.DispatchPointerDown(target, e);
    }

    void OnFocusChanged(FocusChangedEventArgs e)
    {
        if (e.NewFocus is { } target) Layers.DispatchFocusIn(target, e);
    }
}

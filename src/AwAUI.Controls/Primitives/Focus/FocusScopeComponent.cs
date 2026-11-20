using System;
using System.Collections.Generic;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary><see cref="FocusScope.OnMountAutoFocus"/> / <see cref="FocusScope.OnUnmountAutoFocus"/> 的事件：<see cref="PreventDefault"/> 阻止自动聚焦 / 恢复焦点。</summary>
public sealed class FocusScopeAutoFocusEvent
{
    internal FocusScopeAutoFocusEvent(Element container) => Container = container;

    /// <summary>作用域的容器元素。</summary>
    public Element Container { get; }

    public bool DefaultPrevented { get; private set; }

    public void PreventDefault() => DefaultPrevented = true;
}

/// <summary>
/// 焦点作用域组件（Radix <c>FocusScope</c>）：子节点原样渲染，第一个元素是容器。
/// <list type="bullet">
/// <item>挂载后（本帧布局完成时）自动聚焦第一个可见的非链接可 Tab 元素（输入框全选），没有则聚焦容器（容器应有 <c>TabIndex = -1</c>）；焦点已在内部时不移动。</item>
/// <item><see cref="Loop"/>：在两端按 Tab / Shift+Tab 回绕；<see cref="Trapped"/>：焦点不能离开（移到外部时拉回最后一次在内部的元素，焦点元素被移除时交给容器）。</item>
/// <item>卸载后把焦点还给挂载前的焦点元素。</item>
/// </list>
/// 嵌套的作用域只有最内层（最后挂载的）生效。
/// </summary>
public sealed partial class FocusScope : Component
{
    FocusScopeModel<Element>? _model;
    PrimitiveRoot? _services;
    UIRoot? _attachedRoot;
    FocusScopeModel<Element>? _pendingUnmountModel;
    UIRoot? _pendingUnmountRoot;
    int _activationVersion;

    public NodeList? Children { get; set; }

    /// <summary>Whether this scope participates in focus handling, independently of the lifetime of its content. Deactivation removes it from focus handling immediately; focus restoration waits for unmount.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Tab 在两端回绕。</summary>
    public bool Loop { get; set; }

    /// <summary>焦点不能离开作用域。</summary>
    public bool Trapped { get; set; }

    /// <summary>挂载后自动聚焦之前调用；<c>PreventDefault</c> 阻止自动聚焦。</summary>
    public Action<FocusScopeAutoFocusEvent>? OnMountAutoFocus { get; set; }

    /// <summary>卸载后恢复焦点之前调用（在随后的布局完成时）；<c>PreventDefault</c> 阻止恢复。</summary>
    public Action<FocusScopeAutoFocusEvent>? OnUnmountAutoFocus { get; set; }

    /// <summary>纯逻辑的模型（测试与诊断）。挂载之前为 null。</summary>
    public FocusScopeModel<Element>? Model => _model;

    protected override Node Build()
    {
        if (_model is not null)
        {
            _model.Loop = Loop;
            _model.Trapped = Trapped;
        }
        UseEffect(() =>
        {
            if (Active) Activate();
            else Deactivate(unmount: false);
            return null;
        }, Active);
        return new Fragment(Children ?? new NodeList());
    }

    protected override void OnMount()
    {
        if (Active) Activate();
    }

    void Activate()
    {
        if (_model is not null) return;
        _pendingUnmountModel = null;
        _pendingUnmountRoot = null;
        var root = Root!;
        var container = ContainerOf(this);
        if (container is null) return;
        _model = new FocusScopeModel<Element>(container) { Loop = Loop, Trapped = Trapped };
        _services = PrimitiveRoot.For(root);
        _services.AddFocusScope(_model, this);
        // 挂载时（提交阶段，任何自动聚焦之前）的焦点：嵌套作用域同时挂载时，外层恢复焦点以它为准
        var focusBefore = root.FocusManager.FocusedElement;
        _attachedRoot = root;
        root.FocusManager.FocusChanged += OnFocusChanged;
        root.Input.PreviewKeyDown += OnPreviewKeyDown;
        var model = _model;
        _activationVersion++;
        root.RequestAfterLayout(() => AutoFocusOnMount(root, model, focusBefore));
    }

    protected override void OnUnmount() => Deactivate(unmount: true);

    void Deactivate(bool unmount)
    {
        var root = _attachedRoot;
        var model = _model;
        if (root is not null && model is not null)
        {
            root.FocusManager.FocusChanged -= OnFocusChanged;
            root.Input.PreviewKeyDown -= OnPreviewKeyDown;
            _services!.RemoveFocusScope(model);
            _attachedRoot = null;
            _model = null;
            _services = null;
            _activationVersion++;

            if (!unmount)
            {
                _pendingUnmountModel = model;
                _pendingUnmountRoot = root;
                return;
            }

            ScheduleFocusRestore(root, model, OnUnmountAutoFocus);
            return;
        }

        if (!unmount || _pendingUnmountModel is not { } pendingModel || _pendingUnmountRoot is not { } pendingRoot) return;
        _pendingUnmountModel = null;
        _pendingUnmountRoot = null;
        _activationVersion++;
        ScheduleFocusRestore(pendingRoot, pendingModel, OnUnmountAutoFocus);
    }

    void ScheduleFocusRestore(UIRoot root, FocusScopeModel<Element> model, Action<FocusScopeAutoFocusEvent>? callback)
    {
        int version = _activationVersion;
        // Restore focus only after the scope is actually removed from the component tree.
        root.RequestAfterLayout(() =>
        {
            if (version != _activationVersion) return;
            var e = new FocusScopeAutoFocusEvent(model.Container);
            callback?.Invoke(e);
            var target = model.Unmount(e.DefaultPrevented);
            if (target is not null && target.IsAttached && ReferenceEquals(target.Root, root))
                target.Focus(new FocusOptions { SelectText = true });
        });
    }

    void AutoFocusOnMount(UIRoot root, FocusScopeModel<Element> model, Element? focusBefore)
    {
        if (!ReferenceEquals(_model, model) || !model.Container.IsAttached) return;
        if (model.IsPaused)
        {
            // 嵌套在内的作用域已经生效（同时挂载）：只记下挂载前的焦点，不抢焦点
            model.Mount(focusBefore, false, Array.Empty<FocusCandidate<Element>>(), preventAutoFocus: true);
            return;
        }
        var focus = root.FocusManager.FocusedElement;
        bool inside = focus is not null && model.Container.IsInclusiveAncestorOf(focus);
        var e = new FocusScopeAutoFocusEvent(model.Container);
        if (!inside) OnMountAutoFocus?.Invoke(e);
        var candidates = new List<FocusCandidate<Element>>();
        foreach (var el in root.FocusManager.GetTabOrder(model.Container))
            candidates.Add(new FocusCandidate<Element>(el, el.IsVisible, IsLink(el)));
        var target = model.Mount(focus, inside, candidates, e.DefaultPrevented);
        if (target is null) return;
        // 第一个候选（或容器）不可聚焦时依次尝试后面的（Radix 的 focusFirst）
        if (target.Focus(new FocusOptions { SelectText = true })) return;
        foreach (var c in candidates)
            if (c.IsVisible && !c.IsLink && c.Target.Focus(new FocusOptions { SelectText = true })) return;
        model.Container.Focus(new FocusOptions { PreventScroll = true });
    }

    /// <summary>
    /// 作用域的容器：子节点产出的第一个元素；它是 <see cref="PopperContent"/> 的包装元素时取其中的内容元素
    /// （与 Radix 相同，焦点作用域的 ref 落在内容元素上，包装元素对它透明）。
    /// </summary>
    internal static Element? ContainerOf(Component c)
    {
        var e = DismissableLayer.FirstElement(c);
        if (e is not null && e.GetAttribute("data-aw-popper-content-wrapper") is not null && e.ElementChildren.Count > 0) return e.ElementChildren[0];
        return e;
    }

    static bool IsLink(Element e) => e.Role == Role.Link || string.Equals(e.Tag, "a", StringComparison.OrdinalIgnoreCase);

    void OnPreviewKeyDown(KeyEvent e)
    {
        var model = _model;
        var root = _attachedRoot;
        if (model is null || root is null || model.IsPaused || e.DefaultPrevented) return;
        if (e.Key != "Tab" || (e.Modifiers & (KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Meta)) != 0) return;
        var focused = root.FocusManager.FocusedElement;
        if (focused is null || !model.Container.IsInclusiveAncestorOf(focused)) return;
        var tabbables = root.FocusManager.GetTabOrder(model.Container);
        var result = model.HandleTab(focused, (e.Modifiers & KeyModifiers.Shift) != 0, tabbables);
        if (result.PreventDefault) e.PreventDefault();
        if (result.HasTarget) result.Target.Focus(new FocusOptions { SelectText = true, Origin = FocusOrigin.Keyboard });
    }

    void OnFocusChanged(FocusChangedEventArgs e)
    {
        var model = _model;
        if (model is null || model.IsPaused) return;
        if (e.NewFocus is { } next)
        {
            bool inside = model.Container.IsInclusiveAncestorOf(next);
            var pull = model.OnFocusIn(next, inside);
            if (pull is not null && !ReferenceEquals(pull, next) && pull.IsAttached) pull.Focus(new FocusOptions { SelectText = true });
            return;
        }
        // 焦点所在的元素被移除（焦点落回根上）：困住时交给容器
        if (e.ElementRemoved && model.Container.IsAttached && ReferenceEquals(e.OldFocus, model.LastFocusedInside))
            model.OnFocusedElementRemoved()?.Focus(new FocusOptions { PreventScroll = true });
    }
}

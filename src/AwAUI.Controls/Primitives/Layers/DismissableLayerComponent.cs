using System;
using System.Collections.Generic;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 可关闭层组件（Radix <c>DismissableLayer</c>）：子节点原样渲染（相当于 <c>asChild</c>），子节点产出的元素就是层的「内部」。
/// 按 Escape、在外部按下指针、焦点移到外部时依次调用对应回调与 <see cref="OnInteractOutside"/>，没有被 <c>PreventDefault</c> 时调用 <see cref="OnDismiss"/>。
/// <para>
/// 嵌套：组件树中的祖先层（即使经 <see cref="Portal"/> 挂在别处）是本层的逻辑父层——在子层内按下指针不算父层的外部；Escape 只交给最上层（最后打开的）。
/// <see cref="DisableOutsidePointerEvents"/>（模态）时，层之外（且不在更高的覆盖层中）的内容不接收指针（<see cref="UIRoot.DisableOutsidePointerEvents"/>）。
/// </para>
/// </summary>
public sealed partial class DismissableLayer : Component
{
    DismissableLayer<Element>? _layer;
    PrimitiveRoot? _services;
    IDisposable? _pointerBlock;
    Element? _blockedFor;
    bool _registered;

    /// <summary>层的内容（通常是一个元素）。</summary>
    public NodeList? Children { get; set; }

    /// <summary>Whether the layer participates in dismissal and blocks outside input. Closed panels can remain mounted while animating.</summary>
    public bool Active { get; set; } = true;

    /// <summary>屏蔽层外的指针事件（模态）。</summary>
    public bool DisableOutsidePointerEvents { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }

    /// <summary>外部指针按下或焦点移出（在具体回调之后）。</summary>
    public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }

    /// <summary>应当关闭。</summary>
    public Action<DismissReason>? OnDismiss { get; set; }

    /// <summary>纯逻辑的层对象（测试与诊断）。挂载之前为 null。</summary>
    public DismissableLayer<Element>? Layer => _layer;

    protected override Node Build()
    {
        _layer ??= new DismissableLayer<Element>(Contains)
        {
            OnEscapeKeyDown = e => OnEscapeKeyDown?.Invoke(e),
            OnPointerDownOutside = e => OnPointerDownOutside?.Invoke(e),
            OnFocusOutside = e => OnFocusOutside?.Invoke(e),
            OnInteractOutside = e => OnInteractOutside?.Invoke(e),
            OnDismiss = r => OnDismiss?.Invoke(r),
            Name = "DismissableLayer",
        };
        _layer.Parent = TryUse<DismissableLayerScope>(out var parent) ? parent.Layer : null;
        _layer.DisableOutsidePointerEvents = DisableOutsidePointerEvents;
        UseEffect(() =>
        {
            SyncRegistration();
            return null;
        }, Active, DisableOutsidePointerEvents);
        return Provide(new DismissableLayerScope(_layer), new Fragment(Children ?? new NodeList()));
    }

    protected override void OnMount()
    {
        _services = PrimitiveRoot.For(Root!);
        SyncRegistration();
    }

    protected override void OnUnmount()
    {
        _pointerBlock?.Dispose();
        _pointerBlock = null;
        _blockedFor = null;
        if (_registered) _services!.RemoveLayer(_layer!);
        _registered = false;
        _services = null;
    }

    void SyncRegistration()
    {
        if (_services is null) return;
        if (Active != _registered)
        {
            if (Active) _services.AddLayer(_layer!);
            else _services.RemoveLayer(_layer!);
            _registered = Active;
        }
        UpdatePointerBlock();
    }

    void UpdatePointerBlock()
    {
        var element = FirstElement(this);
        bool want = Active && DisableOutsidePointerEvents && element is not null && Root is not null;
        if (want && _pointerBlock is not null && ReferenceEquals(_blockedFor, element)) return;
        _pointerBlock?.Dispose();
        _pointerBlock = null;
        _blockedFor = null;
        if (!want) return;
        _pointerBlock = Root!.DisableOutsidePointerEvents(element!);
        _blockedFor = element;
    }

    bool Contains(Element target)
    {
        foreach (var e in GetRootElements())
            if (e.IsInclusiveAncestorOf(target)) return true;
        return false;
    }

    /// <summary>组件产出的第一个非文本元素。</summary>
    internal static Element? FirstElement(Component c)
    {
        foreach (var e in c.GetRootElements())
            if (!e.IsTextNode) return e;
        return null;
    }
}

/// <summary>向子树提供最近的可关闭层（嵌套层据此找到逻辑父层）。</summary>
internal sealed class DismissableLayerScope
{
    public DismissableLayerScope(DismissableLayer<Element> layer) => Layer = layer;

    public DismissableLayer<Element> Layer { get; }

    public override bool Equals(object? obj) => obj is DismissableLayerScope s && ReferenceEquals(s.Layer, Layer);

    public override int GetHashCode() => Layer.GetHashCode();
}

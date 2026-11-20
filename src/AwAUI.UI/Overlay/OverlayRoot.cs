using System;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 覆盖层的层级常量（<see cref="Portal.ZOrder"/>）：层按（层级，打开顺序）排列，后者在上。同一层级内后打开的在上（与 Radix 把 Portal 依次追加到 body 相同）；
/// 提示（Toast）与工具提示总在普通浮层与模态之上。层内的 <c>z-index</c> 只在覆盖层这个层叠上下文中比较。
/// </summary>
public static class OverlayZOrder
{
    /// <summary>普通浮层与模态对话框（Popover、Menu、Select、Dialog ...）。</summary>
    public const int Default = 0;

    /// <summary>提示队列（Toast 视口）。</summary>
    public const int Toast = 1000;

    /// <summary>工具提示。</summary>
    public const int Tooltip = 2000;
}

/// <summary>
/// 窗口的覆盖层（design/23 第 1 层 <c>Portal / OverlayLayer</c>）：位于根元素的最后一个子元素位置的覆盖层根（标签 <c>aw-overlay</c>），
/// 其下每个 <see cref="Portal"/> 一层（标签 <c>aw-portal</c>）。覆盖层根是铺满视口的 <c>position: fixed</c> 盒，
/// <c>z-index</c> 极大因而总画在常规内容之上；它自身 <c>pointer-events: none</c>（不挡住下面的内容），
/// 层是 <c>display: contents</c> 且 <c>pointer-events: auto</c>，因此 Portal 中的内容像挂在 body 下一样排版、命中，
/// 并继承根元素上的 CSS 变量与字体。
/// <para>
/// 层按 <see cref="Portal.ZOrder"/> 与打开顺序排列；没有层时覆盖层根不在树上（不影响常规内容的元素树）。
/// 树序上覆盖层在全部常规内容之后，所以 Tab 顺序与 <see cref="Element.CompareDocumentPosition"/> 都与浏览器中 Portal 到 body 末尾一致。
/// 根上还定义了层级令牌 <c>--aw-z-popup / --aw-z-modal / --aw-z-toast / --aw-z-tooltip</c>，供样式表在覆盖层内排序。
/// </para>
/// </summary>
public sealed class OverlayRoot
{
    /// <summary>覆盖层根的标签。</summary>
    public const string ElementTag = "aw-overlay";

    /// <summary>层元素的标签。</summary>
    public const string LayerTag = "aw-portal";

    const string RootStyle =
        "display: block; position: fixed; left: 0; top: 0; right: 0; bottom: 0; z-index: 2147483000; pointer-events: none; "
        + "--aw-z-popup: 50; --aw-z-modal: 50; --aw-z-toast: 100; --aw-z-tooltip: 110";

    const string LayerStyle = "display: contents; pointer-events: auto";

    readonly UIRoot _root;
    readonly Element _element;
    readonly List<(Element Layer, int ZOrder)> _layers = new();

    internal OverlayRoot(UIRoot root)
    {
        _root = root;
        _element = new Element(ElementTag);
        _element.SetInlineStyle(RootStyle);
    }

    /// <summary>覆盖层根元素（有层时是根元素的最后一个子元素）。</summary>
    public Element Element => _element;

    /// <summary>覆盖层根当前是否挂在根元素上（至少有一层）。</summary>
    public bool IsAttached => ReferenceEquals(_element._parent, _root.RootElement);

    /// <summary>各层元素，自下而上。</summary>
    public IReadOnlyList<Element> Layers => _element.Children;

    /// <summary>元素所在的层（覆盖层根的直接子元素）；不在覆盖层中时为 null。</summary>
    public Element? LayerOf(Element? element)
    {
        if (element is null) return null;
        for (var e = element; e is not null; e = e._parent)
            if (ReferenceEquals(e._parent, _element)) return e;
        return null;
    }

    /// <summary>层的下标（自下而上，0 起）；不是层时为 -1。</summary>
    public int IndexOfLayer(Element? layer) => layer is null || !ReferenceEquals(layer._parent, _element) ? -1 : layer.IndexInParent;

    /// <summary>新建一层：插在层级不大于 <paramref name="zOrder"/> 的最后一层之上。</summary>
    internal Element CreateLayer(int zOrder, Component? owner)
    {
        var layer = new Element(LayerTag) { _owner = owner };
        layer.SetInlineStyle(LayerStyle);
        int index = _layers.Count;
        while (index > 0 && _layers[index - 1].ZOrder > zOrder) index--;
        _layers.Insert(index, (layer, zOrder));
        EnsureAttached();
        _element.InsertChildDirect(layer, index);
        return layer;
    }

    /// <summary>移除一层（Portal 卸载）；最后一层移除时覆盖层根离开树。</summary>
    internal void RemoveLayer(Element layer)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            if (!ReferenceEquals(_layers[i].Layer, layer)) continue;
            _layers.RemoveAt(i);
            break;
        }
        _element.RemoveChildDirect(layer);
        if (_layers.Count == 0 && IsAttached) _root.RootElement.RemoveChildDirect(_element);
    }

    void EnsureAttached()
    {
        if (IsAttached) return;
        _root.RootElement.InsertChildDirect(_element);
    }

    /// <summary>
    /// <paramref name="target"/> 是否位于 <paramref name="boundary"/> 之内，或位于比 <paramref name="boundary"/> 所在层更高的覆盖层中
    /// （例如模态对话框中打开的下拉菜单）。<paramref name="boundary"/> 不在覆盖层中时，任何覆盖层都算更高。
    /// </summary>
    internal bool IsWithinOrAbove(Element target, Element boundary)
    {
        if (boundary.IsInclusiveAncestorOf(target)) return true;
        var targetLayer = LayerOf(target);
        if (targetLayer is null) return false;
        return targetLayer.IndexInParent > IndexOfLayer(LayerOf(boundary));
    }
}

/// <summary>可释放的登记（滚动锁定、指针屏蔽）。</summary>
internal sealed class RootRegistration : IDisposable
{
    readonly List<RootRegistration> _owner;
    readonly Action? _onEmpty;

    public RootRegistration(List<RootRegistration> owner, Element? subtree, Action? onEmpty)
    {
        _owner = owner;
        Subtree = subtree;
        _onEmpty = onEmpty;
    }

    public Element? Subtree { get; }

    public bool Disposed { get; private set; }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        _owner.Remove(this);
        if (_owner.Count == 0) _onEmpty?.Invoke();
    }
}

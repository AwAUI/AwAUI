using System;
using System.Collections.Generic;
using AwAUI.Layout;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>漫游焦点组的共享状态（项登记、当前 Tab 位与版本号）。</summary>
internal sealed class RovingGroupScope
{
    public RovingGroupScope()
    {
        Items = new ItemCollection<object>();
        Model = new RovingFocusGroup<object>(Items);
    }

    public ItemCollection<object> Items { get; }

    public RovingFocusGroup<object> Model { get; }

    /// <summary>项的登记或当前 Tab 位变化时加一（项读取它，因此随之重建 TabIndex）。</summary>
    public readonly State<int> Version = new(0);

    public void Bump() => Version.Value = Version.Peek() + 1;

    /// <summary>按树序重排已登记的项。</summary>
    public void SortByDocumentOrder()
    {
        Items.Sort((a, b) =>
        {
            var ea = (a.Tag as Component) is { } ca ? DismissableLayer.FirstElement(ca) : null;
            var eb = (b.Tag as Component) is { } cb ? DismissableLayer.FirstElement(cb) : null;
            if (ea is null || eb is null) return 0;
            return Element.CompareTreeOrder(ea, eb);
        });
    }

    public Element? ElementOf(object key) =>
        Items.TryGet(key, out var item) && item.Tag is Component c ? DismissableLayer.FirstElement(c) : null;
}

/// <summary>
/// 漫游焦点组（Radix <c>RovingFocusGroup</c>）：组内的 <see cref="RovingFocusGroupItem"/> 只占一个 Tab 位（当前项 <c>tabindex=0</c>，其余 -1），
/// 方向键在可聚焦项之间移动（跳过 <see cref="RovingFocusGroupItem.Focusable"/> 为 false 的项），支持 <see cref="Orientation"/>、<see cref="Loop"/>、
/// <see cref="Dir"/>（RTL 下左右互换）与 Home / End。项按树序排列（<see cref="Element.CompareDocumentPosition"/>）。
/// 渲染一个 <see cref="Div"/>（<c>data-orientation</c>），或在 <see cref="AsChild"/> 时把属性合并到唯一的子元素上。
/// </summary>
public sealed partial class RovingFocusGroup : Component
{
    readonly RovingGroupScope _scope = new();

    public Orientation? Orientation { get; set; }

    public Direction Dir { get; set; } = Direction.Ltr;

    public bool Loop { get; set; }

    public bool AsChild { get; set; }

    public NodeList? Children { get; set; }

    public ClassValue Class { get; set; }

    public StyleValue Style { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Attributes { get; set; }

    protected override Node Build()
    {
        _scope.Model.Options = new RovingFocusOptions { Orientation = Orientation, Direction = Dir, Loop = Loop };
        var props = new Div();
        if (Orientation is { } o) props.Attributes["data-orientation"] = o == Primitives.Orientation.Horizontal ? "horizontal" : "vertical";
        if (Orientation is { } ao) props.Attributes["aria-orientation"] = ao == Primitives.Orientation.Horizontal ? "horizontal" : "vertical";
        PartProps.Apply(props, Class, Style, Attributes);
        Node body = AsChild ? Slot.Merge(props, Slot.SingleChild(Children)) : WithChildren(props, Children);
        return Provide(_scope, body);
    }

    static Div WithChildren(Div d, NodeList? children)
    {
        d.Children = children ?? new NodeList();
        return d;
    }
}

/// <summary>
/// 漫游焦点组中的一项（Radix <c>RovingFocusGroupItem</c>）：渲染子元素（<see cref="AsChild"/>，例如按钮）或一个 <see cref="Div"/>，
/// 设置其 <c>TabIndex</c>（当前 Tab 位为 0，其余 -1），处理方向键并在获得焦点时成为当前 Tab 位。
/// </summary>
public sealed partial class RovingFocusGroupItem : Component
{
    readonly object _autoKey = new();
    RovingGroupScope? _scope;

    /// <summary>项的标识（默认是组件实例自身）。</summary>
    public object? Value { get; set; }

    /// <summary>是否参与漫游焦点（禁用项为 false）。</summary>
    public bool Focusable { get; set; } = true;

    /// <summary>初始时作为 Tab 位（例如选中的项）。</summary>
    public bool Active { get; set; }

    public bool AsChild { get; set; }

    public NodeList? Children { get; set; }

    public ClassValue Class { get; set; }

    public StyleValue Style { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Attributes { get; set; }

    object ItemKey => Value ?? _autoKey;

    protected override Node Build()
    {
        var scope = Use<RovingGroupScope>();
        _scope = scope;
        _ = scope.Version.Value; // 登记依赖：Tab 位变化时重建
        var key = ItemKey;
        var props = new Div
        {
            TabIndex = scope.Items.Contains(key) ? scope.Model.GetTabIndex(key) : (Active ? 0 : -1),
            OnKeyDown = OnKeyDown,
            OnFocus = _ =>
            {
                scope.Model.OnItemFocused(ItemKey);
                scope.Bump();
            },
        };
        props.Attributes["data-aw-collection-item"] = "";
        if (!Focusable) props.Attributes["data-disabled"] = "";
        PartProps.Apply(props, Class, Style, Attributes);
        // 登记（键或可聚焦性变化时重新登记）；清理函数在卸载或下次登记前注销
        UseEffect(() =>
        {
            var k = ItemKey;
            Register(scope, k);
            return () =>
            {
                scope.Items.Unregister(k);
                scope.Bump();
            };
        }, key, Focusable);
        if (AsChild) return Slot.Merge(props, Slot.SingleChild(Children));
        props.Children = Children ?? new NodeList();
        return props;
    }

    void OnKeyDown(KeyEvent e)
    {
        var scope = _scope;
        if (scope is null || e.DefaultPrevented) return;
        if (e.Target is not null && DismissableLayer.FirstElement(this) is { } own && !ReferenceEquals(e.Target, own)) return;
        if (!scope.Model.HandleKeyDown(e.Key, e.Modifiers, ItemKey, out var target)) return;
        e.PreventDefault();
        scope.Bump();
        if (!ReferenceEquals(target, ItemKey)) scope.ElementOf(target)?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
    }

    void Register(RovingGroupScope scope, object key)
    {
        var item = scope.Items.Register(key, null, !Focusable);
        item.Tag = this;
        if (Active) scope.Model.OnItemFocused(key);
        scope.SortByDocumentOrder();
        scope.Bump();
    }
}

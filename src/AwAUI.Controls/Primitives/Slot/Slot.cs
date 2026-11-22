using System;
using System.Collections.Generic;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 能接收槽位属性的组件的基类（<see cref="Slot"/> 的子节点是组件时）：<see cref="MergeSlotProps"/> 返回把槽位属性合并到自身根部件后的
/// 新组件节点（不修改自身，规则同 <see cref="Slot.Merge"/>；通常是复制 Props 并把槽位的类、属性与处理器并入自己的展开属性）。
/// 其他组件作为 <c>AsChild</c> 的子节点时，Slot 退回为渲染自己的元素并包住它。
/// </summary>
public abstract partial class SlottableComponent : Component
{
    /// <summary>返回合并了 <paramref name="slotProps"/> 的新节点。</summary>
    public abstract Node MergeSlotProps(ElementNode slotProps);
}

/// <summary>
/// 槽位（Radix <c>Slot</c>，实现 <c>AsChild</c>）：把一组「槽位属性」（以一个 <see cref="ElementNode"/> 作为载体给出：类、样式、属性、
/// 事件处理器、角色、TabIndex ...）合并到唯一的子元素上，渲染子元素自己的类型。合并规则与 Radix 的 <c>mergeProps</c> 相同：
/// <list type="bullet">
/// <item>事件处理器：两者都有时先调用子元素的，再调用槽位的（槽位的处理器自己检查 <see cref="Event.DefaultPrevented"/>，见 <see cref="EventComposition.Compose{T}"/>）。</item>
/// <item><c>class</c>：拼接，槽位在前；<c>style</c>：拼接，子元素的声明在后（同名以子元素为准）。</item>
/// <item>其余属性（<c>id</c>、角色、<c>aria-label</c>、TabIndex、<c>data-* / aria-*</c>、Ref、Key、滚动控制器）：子元素给出的优先；布尔的 <c>Disabled</c> 取或。</item>
/// </list>
/// 不修改两个输入节点（描述对象可能被缓存复用）：返回子元素的一份拷贝。内建元素（<see cref="Div"/>、<see cref="Span"/>、<see cref="UIText"/>、
/// <see cref="UIButton"/>、<see cref="Input"/>、<see cref="Image"/>）直接合并；组件需派生自 <see cref="SlottableComponent"/>；
/// 其他节点（文本、<c>Svg</c> 与未知的元素类型、不可合并的组件）退回为渲染槽位载体本身并把子节点放进去
/// （<c>Svg</c> 节点的文档类型在 AwAUI.Svg 中，本程序集不引用它）。
/// </summary>
public sealed partial class Slot : Component
{
    /// <summary>槽位属性的载体（它的元素类型只在退回时使用）。</summary>
    public ElementNode? Props { get; set; }

    /// <summary>唯一的子节点（多个时只取第一个非空节点）。</summary>
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var child = SingleChild(Children);
        if (Props is null) return child ?? new Fragment();
        return Merge(Props, child);
    }

    /// <summary>取唯一的子节点：跳过 null；只有一个子节点的片段展开。没有子节点时返回 null。</summary>
    public static Node? SingleChild(NodeList? children)
    {
        if (children is null || children.Count == 0) return null;
        var n = children[0];
        int guard = 0;
        while (n is Fragment f && f.Children.Count == 1 && guard++ < 64) n = f.Children[0];
        return n;
    }

    /// <summary>把 <paramref name="slotProps"/> 合并到 <paramref name="child"/> 上（规则见类说明）。<paramref name="child"/> 为 null 时返回空片段。</summary>
    public static Node Merge(ElementNode slotProps, Node? child)
    {
        if (slotProps is null) throw new ArgumentNullException(nameof(slotProps));
        int guard = 0;
        while (child is Fragment f && f.Children.Count == 1 && guard++ < 64) child = f.Children[0];
        switch (child)
        {
            case null:
                return new Fragment();
            case ElementNode element when CloneKnown(element) is { } copy:
                CopyCommon(element, copy);
                MergeInto(slotProps, copy);
                return copy;
            case SlottableComponent slottable:
                return slottable.MergeSlotProps(slotProps);
            default:
            {
                // 无法合并：渲染槽位载体本身，子节点放进去（结构多一层，但属性与处理器都还在）
                var host = CloneKnown(slotProps) ?? new Div();
                CopyCommon(slotProps, host);
                host.Children = new NodeList { child };
                return host;
            }
        }
    }

    /// <summary>新建同类型的内建元素节点并拷贝类型特有的属性；未知类型返回 null。</summary>
    static ElementNode? CloneKnown(ElementNode n) => n switch
    {
        Div => new Div(),
        Span => new Span(),
        UIText t => new UIText { Content = t.Content },
        UIButton b => new UIButton { Disabled = b.Disabled },
        Input i => new Input
        {
            Value = i.Value,
            Placeholder = i.Placeholder,
            InputType = i.InputType,
            Disabled = i.Disabled,
            ReadOnly = i.ReadOnly,
            OnInput = i.OnInput,
            OnChange = i.OnChange,
            OnSubmit = i.OnSubmit,
        },
        Image img => new Image { Source = img.Source, Alt = img.Alt, Width = img.Width, Height = img.Height },
        _ => null,
    };

    /// <summary>拷贝所有元素共有的属性（子节点列表共享：描述对象交给框架后不再修改）。</summary>
    static void CopyCommon(ElementNode from, ElementNode to)
    {
        to.Key = from.Key;
        to.Id = from.Id;
        to.Class = from.Class;
        to.Style = from.Style;
        to.Children = from.Children;
        to.Ref = from.Ref;
        to.Role = from.Role;
        to.AriaLabel = from.AriaLabel;
        to.TabIndex = from.TabIndex;
        to.__Scope = from.__Scope;
        to.ScrollController = from.ScrollController;
        foreach (var kv in from.Attributes) to.Attributes[kv.Key] = kv.Value;
        foreach (var h in EventSlots.All) h.Copy(from, to);
    }

    /// <summary>把槽位属性合并进 <paramref name="target"/>（子元素的拷贝）。</summary>
    static void MergeInto(ElementNode slot, ElementNode target)
    {
        if (!slot.Class.IsEmpty) target.Class = target.Class.IsEmpty ? slot.Class : new ClassValue(slot.Class.Text + " " + target.Class.Text);
        if (!slot.Style.IsEmpty)
        {
            string s = slot.Style.Text!.Trim().TrimEnd(';');
            target.Style = target.Style.IsEmpty ? new StyleValue(s) : new StyleValue(s + "; " + target.Style.Text);
        }
        target.Key ??= slot.Key;
        target.Id ??= slot.Id;
        target.Ref ??= slot.Ref;
        if (target.Role == Role.None) target.Role = slot.Role;
        target.AriaLabel ??= slot.AriaLabel;
        target.TabIndex ??= slot.TabIndex;
        target.__Scope ??= slot.__Scope;
        target.ScrollController ??= slot.ScrollController;
        foreach (var kv in slot.Attributes)
            if (!target.Attributes.Contains(kv.Key)) target.Attributes[kv.Key] = kv.Value;
        foreach (var h in EventSlots.All) h.MergeSlot(slot, target);
        switch (target)
        {
            case UIButton tb when slot is UIButton sb:
                tb.Disabled |= sb.Disabled;
                break;
            case Input ti when slot is Input si:
                ti.Disabled |= si.Disabled;
                ti.ReadOnly |= si.ReadOnly;
                ti.OnInput = EventComposition.Chain(ti.OnInput, si.OnInput);
                ti.OnChange = EventComposition.Chain(ti.OnChange, si.OnChange);
                ti.OnSubmit = EventComposition.Chain(ti.OnSubmit, si.OnSubmit);
                break;
        }
    }
}

/// <summary>事件处理器的组合（Radix 的 <c>composeEventHandlers</c> 与 Slot 的处理器合并）。</summary>
public static class EventComposition
{
    /// <summary>
    /// 组件内部处理器与调用方处理器的组合（Radix <c>composeEventHandlers</c>）：先调用 <paramref name="external"/>，
    /// 事件没有被 <see cref="Event.PreventDefault"/> 时再调用 <paramref name="internalHandler"/>。
    /// </summary>
    public static Action<T>? Compose<T>(Action<T>? external, Action<T>? internalHandler) where T : Event
    {
        if (external is null) return internalHandler;
        if (internalHandler is null) return external;
        return e =>
        {
            external(e);
            if (!e.DefaultPrevented) internalHandler(e);
        };
    }

    /// <summary>依次调用两个处理器（Slot：子元素的在前，槽位的在后）。</summary>
    public static Action<T>? Chain<T>(Action<T>? first, Action<T>? second) where T : Event
    {
        if (first is null) return second;
        if (second is null) return first;
        return e =>
        {
            first(e);
            second(e);
        };
    }
}

/// <summary>内建元素节点上的每个事件处理器属性（只用公开的属性访问，不用反射）。</summary>
internal abstract class EventSlots
{
    public static readonly EventSlots[] All =
    {
        new Slot<PointerEvent>("onclick", n => n.OnClick, (n, h) => n.OnClick = h),
        new Slot<PointerEvent>("onpointerdown", n => n.OnPointerDown, (n, h) => n.OnPointerDown = h),
        new Slot<PointerEvent>("onpointerup", n => n.OnPointerUp, (n, h) => n.OnPointerUp = h),
        new Slot<PointerEvent>("onpointermove", n => n.OnPointerMove, (n, h) => n.OnPointerMove = h),
        new Slot<PointerEvent>("onpointerenter", n => n.OnPointerEnter, (n, h) => n.OnPointerEnter = h),
        new Slot<PointerEvent>("onpointerleave", n => n.OnPointerLeave, (n, h) => n.OnPointerLeave = h),
        new Slot<PointerEvent>("onpointercancel", n => n.OnPointerCancel, (n, h) => n.OnPointerCancel = h),
        new Slot<KeyEvent>("onkeydown", n => n.OnKeyDown, (n, h) => n.OnKeyDown = h),
        new Slot<KeyEvent>("onkeyup", n => n.OnKeyUp, (n, h) => n.OnKeyUp = h),
        new Slot<FocusEvent>("onfocus", n => n.OnFocus, (n, h) => n.OnFocus = h),
        new Slot<FocusEvent>("onblur", n => n.OnBlur, (n, h) => n.OnBlur = h),
        new Slot<FocusEvent>("onfocusin", n => n.OnFocusIn, (n, h) => n.OnFocusIn = h),
        new Slot<FocusEvent>("onfocusout", n => n.OnFocusOut, (n, h) => n.OnFocusOut = h),
        new Slot<TapEvent>("ontap", n => n.OnTap, (n, h) => n.OnTap = h),
        new Slot<PanEvent>("onpan", n => n.OnPan, (n, h) => n.OnPan = h),
        new Slot<LongPressEvent>("onlongpress", n => n.OnLongPress, (n, h) => n.OnLongPress = h),
        new Slot<DoubleTapEvent>("ondoubletap", n => n.OnDoubleTap, (n, h) => n.OnDoubleTap = h),
        new Slot<SwipeEvent>("onswipe", n => n.OnSwipe, (n, h) => n.OnSwipe = h),
        new Slot<PinchEvent>("onpinch", n => n.OnPinch, (n, h) => n.OnPinch = h),
        new Slot<RotateEvent>("onrotate", n => n.OnRotate, (n, h) => n.OnRotate = h),
        new Slot<EdgeSwipeEvent>("onedgeswipe", n => n.OnEdgeSwipe, (n, h) => n.OnEdgeSwipe = h),
        new Slot<ScrollEvent>("onscroll", n => n.OnScroll, (n, h) => n.OnScroll = h),
        new Slot<WheelEvent>("onwheel", n => n.OnWheel, (n, h) => n.OnWheel = h),
        new Slot<PointerEvent>("onclickcapture", n => n.OnClickCapture, (n, h) => n.OnClickCapture = h),
        new Slot<PointerEvent>("onpointerdowncapture", n => n.OnPointerDownCapture, (n, h) => n.OnPointerDownCapture = h),
        new Slot<PointerEvent>("onpointerupcapture", n => n.OnPointerUpCapture, (n, h) => n.OnPointerUpCapture = h),
        new Slot<PointerEvent>("onpointermovecapture", n => n.OnPointerMoveCapture, (n, h) => n.OnPointerMoveCapture = h),
        new Slot<KeyEvent>("onkeydowncapture", n => n.OnKeyDownCapture, (n, h) => n.OnKeyDownCapture = h),
        new Slot<KeyEvent>("onkeyupcapture", n => n.OnKeyUpCapture, (n, h) => n.OnKeyUpCapture = h),
    };

    static readonly Dictionary<string, EventSlots> s_byName = CreateIndex();

    static Dictionary<string, EventSlots> CreateIndex()
    {
        var d = new Dictionary<string, EventSlots>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in All) d[s.Name] = s;
        return d;
    }

    protected EventSlots(string name) => Name = name;

    /// <summary>属性展开中的名字（<c>onclick</c> ...）。</summary>
    public string Name { get; }

    public static EventSlots? Find(string name) => s_byName.TryGetValue(name, out var s) ? s : null;

    public abstract void Copy(ElementNode from, ElementNode to);

    /// <summary>Slot：子元素的处理器在前，槽位的在后。</summary>
    public abstract void MergeSlot(ElementNode slot, ElementNode target);

    /// <summary>调用方给出的处理器（<see cref="Action{T}"/> 或无参 <see cref="Action"/>）与部件内部处理器组合：调用方在前，未阻止默认动作时再执行内部的。</summary>
    public abstract bool ComposeExternal(ElementNode target, object handler);

    sealed class Slot<T> : EventSlots where T : Event
    {
        readonly Func<ElementNode, Action<T>?> _get;
        readonly Action<ElementNode, Action<T>?> _set;

        public Slot(string name, Func<ElementNode, Action<T>?> get, Action<ElementNode, Action<T>?> set) : base(name)
        {
            _get = get;
            _set = set;
        }

        public override void Copy(ElementNode from, ElementNode to)
        {
            var h = _get(from);
            if (h is not null) _set(to, h);
        }

        public override void MergeSlot(ElementNode slot, ElementNode target)
        {
            var s = _get(slot);
            if (s is not null) _set(target, EventComposition.Chain(_get(target), s));
        }

        public override bool ComposeExternal(ElementNode target, object handler)
        {
            Action<T>? external = handler switch
            {
                Action<T> typed => typed,
                Action plain => _ => plain(),
                _ => null,
            };
            if (external is null) return false;
            _set(target, EventComposition.Compose(external, _get(target)));
            return true;
        }
    }
}

using System;
using System.ComponentModel;

namespace AwAUI.UI;

/// <summary>
/// 内建元素描述的基类（<see cref="Div"/>、<see cref="Text"/>、<see cref="Image"/> ...）。
/// 每个派生类型提供 <see cref="CreateElement"/> 与手写的 <see cref="Diff"/>：只把真正变化的字段写到存活元素上，
/// 并由元素的写入方法打出精确的脏标记。
/// </summary>
public abstract class ElementNode : Node
{
    NodeList? _children;
    AttributeMap? _attributes;
    internal Delegate?[]? _handlers;

    public string? Id { get; set; }

    /// <summary>class 列表；可由字符串隐式转换，条件类名用 <see cref="ClassValue.Cx(string?, (string Name, bool On)[])"/>。</summary>
    public ClassValue Class { get; set; }

    /// <summary>内联样式（目前保存原始声明文本）。</summary>
    public StyleValue Style { get; set; }

    /// <summary>子节点。读取时按需创建，因此 <c>Children = [ ... ]</c> 与 <c>Children = { ... }</c> 两种写法都可用。</summary>
    public NodeList Children
    {
        get => _children ??= new NodeList();
        set => _children = value;
    }

    internal NodeList? ChildrenOrNull => _children;

    /// <summary>挂载后指向存活元素，卸载时清空。</summary>
    public Ref<Element>? Ref { get; set; }

    // ---- 无障碍

    public Role Role { get; set; }

    public string? AriaLabel { get; set; }

    /// <summary>Tab 顺序：null 为元素默认（按钮、输入框可聚焦，其余不可）；0 按树序参与；正数优先且升序；负数只能程序聚焦。</summary>
    public int? TabIndex { get; set; }

    /// <summary>自定义属性（<c>data-*</c>、<c>aria-*</c> ...），供 CSS 属性选择器使用。</summary>
    public AttributeMap Attributes => _attributes ??= new AttributeMap();

    internal AttributeMap? AttributesOrNull => _attributes;

    /// <summary>作用域样式标记（由 <c>.awaui</c> 编译器 / 作用域 CSS 生成器写入）。</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? __Scope { get; set; }

    // ---- 事件（冒泡阶段）

    public Action<PointerEvent>? OnClick { get => Get<PointerEvent>(EventKind.Click); set => Set(EventKind.Click, value); }

    public Action<PointerEvent>? OnPointerDown { get => Get<PointerEvent>(EventKind.PointerDown); set => Set(EventKind.PointerDown, value); }

    public Action<PointerEvent>? OnPointerUp { get => Get<PointerEvent>(EventKind.PointerUp); set => Set(EventKind.PointerUp, value); }

    public Action<PointerEvent>? OnPointerMove { get => Get<PointerEvent>(EventKind.PointerMove); set => Set(EventKind.PointerMove, value); }

    /// <summary>指针进入本元素或其后代（不冒泡）。</summary>
    public Action<PointerEvent>? OnPointerEnter { get => Get<PointerEvent>(EventKind.PointerEnter); set => Set(EventKind.PointerEnter, value); }

    /// <summary>指针离开本元素及其全部后代（不冒泡）。</summary>
    public Action<PointerEvent>? OnPointerLeave { get => Get<PointerEvent>(EventKind.PointerLeave); set => Set(EventKind.PointerLeave, value); }

    public Action<PointerEvent>? OnPointerCancel { get => Get<PointerEvent>(EventKind.PointerCancel); set => Set(EventKind.PointerCancel, value); }

    public Action<KeyEvent>? OnKeyDown { get => Get<KeyEvent>(EventKind.KeyDown); set => Set(EventKind.KeyDown, value); }

    public Action<KeyEvent>? OnKeyUp { get => Get<KeyEvent>(EventKind.KeyUp); set => Set(EventKind.KeyUp, value); }

    /// <summary>本元素获得焦点（不冒泡）。</summary>
    public Action<FocusEvent>? OnFocus { get => Get<FocusEvent>(EventKind.Focus); set => Set(EventKind.Focus, value); }

    /// <summary>本元素失去焦点（不冒泡）。</summary>
    public Action<FocusEvent>? OnBlur { get => Get<FocusEvent>(EventKind.Blur); set => Set(EventKind.Blur, value); }

    /// <summary>本元素或后代获得焦点（冒泡）。</summary>
    public Action<FocusEvent>? OnFocusIn { get => Get<FocusEvent>(EventKind.FocusIn); set => Set(EventKind.FocusIn, value); }

    /// <summary>本元素或后代失去焦点（冒泡）。</summary>
    public Action<FocusEvent>? OnFocusOut { get => Get<FocusEvent>(EventKind.FocusOut); set => Set(EventKind.FocusOut, value); }

    /// <summary>点按手势（识别器尚未实现，目前不会触发）。</summary>
    public Action<TapEvent>? OnTap { get => Get<TapEvent>(EventKind.Tap); set => Set(EventKind.Tap, value); }

    /// <summary>拖动手势（识别器尚未实现，目前不会触发）。</summary>
    public Action<PanEvent>? OnPan { get => Get<PanEvent>(EventKind.Pan); set => Set(EventKind.Pan, value); }

    /// <summary>本元素（滚动容器）的滚动偏移变化（每帧最多一次，不冒泡）；等价于订阅 <see cref="UI.ScrollController.Scrolled"/>。</summary>
    public Action<ScrollEvent>? OnScroll { get => Get<ScrollEvent>(EventKind.Scroll); set => Set(EventKind.Scroll, value); }

    /// <summary>滚轮 / 触控板滚动（冒泡）；调用 <see cref="Event.PreventDefault"/> 阻止默认的滚动。</summary>
    public Action<WheelEvent>? OnWheel { get => Get<WheelEvent>(EventKind.Wheel); set => Set(EventKind.Wheel, value); }

    /// <summary>
    /// 滚动容器的控制器（design/21）：在组件中持有它以读取偏移或命令式滚动。不指定时元素自己创建（<see cref="Element.ScrollController"/>）。
    /// 换成一个还没用过的新控制器时，它接过当前的滚动位置。
    /// </summary>
    public ScrollController? ScrollController { get; set; }

    // ---- 事件（捕获阶段）

    public Action<PointerEvent>? OnClickCapture { get => Get<PointerEvent>(EventKind.ClickCapture); set => Set(EventKind.ClickCapture, value); }

    public Action<PointerEvent>? OnPointerDownCapture { get => Get<PointerEvent>(EventKind.PointerDownCapture); set => Set(EventKind.PointerDownCapture, value); }

    public Action<PointerEvent>? OnPointerUpCapture { get => Get<PointerEvent>(EventKind.PointerUpCapture); set => Set(EventKind.PointerUpCapture, value); }

    public Action<PointerEvent>? OnPointerMoveCapture { get => Get<PointerEvent>(EventKind.PointerMoveCapture); set => Set(EventKind.PointerMoveCapture, value); }

    public Action<KeyEvent>? OnKeyDownCapture { get => Get<KeyEvent>(EventKind.KeyDownCapture); set => Set(EventKind.KeyDownCapture, value); }

    public Action<KeyEvent>? OnKeyUpCapture { get => Get<KeyEvent>(EventKind.KeyUpCapture); set => Set(EventKind.KeyUpCapture, value); }

    private protected Action<T>? Get<T>(EventKind kind) where T : Event => _handlers?[(int)kind] as Action<T>;

    private protected void Set(EventKind kind, Delegate? handler)
    {
        if (_handlers is null)
        {
            if (handler is null) return;
            _handlers = new Delegate?[(int)EventKind.Count];
        }
        _handlers[(int)kind] = handler;
    }

    /// <summary>创建对应的存活元素（字段由随后的 <see cref="Diff"/> 写入）。</summary>
    protected internal abstract Element CreateElement();

    /// <summary>
    /// 把本描述与上一次描述 <paramref name="oldNode"/>（首次挂载时为 null）的差异写到 <paramref name="element"/>。
    /// 只写变化的字段；元素的写入方法再与自身当前值比较，因此脏标记是精确的。派生类重写时应调用基类。
    /// 子节点不在这里处理，由协调器负责。
    /// </summary>
    protected internal virtual void Diff(ElementNode? oldNode, Element element)
    {
        if (oldNode is null || !string.Equals(oldNode.Id, Id, StringComparison.Ordinal)) element.SetId(Id);
        if (oldNode is null || oldNode.Class != Class) element.SetClasses(Class);
        if (oldNode is null || oldNode.Style != Style) element.SetInlineStyle(Style.Text);
        if (oldNode is null || !string.Equals(oldNode.__Scope, __Scope, StringComparison.Ordinal)) element.SetScope(__Scope);
        if (oldNode is null || oldNode.Role != Role) element.SetRole(Role);
        if (oldNode is null || !string.Equals(oldNode.AriaLabel, AriaLabel, StringComparison.Ordinal)) element.SetAriaLabel(AriaLabel);
        if (oldNode is null || oldNode.TabIndex != TabIndex) element.SetTabIndex(TabIndex);
        if (oldNode is null || !AttributeMap.ContentEquals(oldNode._attributes, _attributes)) element.SetAttributes(_attributes);
        if (oldNode is null || !ReferenceEquals(oldNode.ScrollController, ScrollController)) element.SetScrollController(ScrollController);
        // 事件处理器每次重建都是新委托：只更新引用，不打任何脏标记
        element.SetHandlers(_handlers);
        var oldRef = oldNode?.Ref;
        if (!ReferenceEquals(oldRef, Ref))
        {
            if (oldRef is not null && ReferenceEquals(oldRef.Current, element)) oldRef.Current = null;
            if (Ref is not null) Ref.Current = element;
        }
    }
}

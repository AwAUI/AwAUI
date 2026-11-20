using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

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

    // ---- CSS 动画与过渡事件（冒泡；在一帧的绘制之后派发）

    /// <summary>CSS 动画进入活动阶段（<c>animationstart</c>）。</summary>
    public Action<AnimationEvent>? OnAnimationStart { get => Get<AnimationEvent>(EventKind.AnimationStart); set => Set(EventKind.AnimationStart, value); }

    /// <summary>CSS 动画开始新的一次迭代（<c>animationiteration</c>）。</summary>
    public Action<AnimationEvent>? OnAnimationIteration { get => Get<AnimationEvent>(EventKind.AnimationIteration); set => Set(EventKind.AnimationIteration, value); }

    /// <summary>CSS 动画结束（<c>animationend</c>）；没有时钟时有限的动画在开始的同一帧结束。</summary>
    public Action<AnimationEvent>? OnAnimationEnd { get => Get<AnimationEvent>(EventKind.AnimationEnd); set => Set(EventKind.AnimationEnd, value); }

    /// <summary>CSS 动画在结束前被取消（<c>animationcancel</c>：从 <c>animation-name</c> 移除、元素变为 <c>display: none</c>）。</summary>
    public Action<AnimationEvent>? OnAnimationCancel { get => Get<AnimationEvent>(EventKind.AnimationCancel); set => Set(EventKind.AnimationCancel, value); }

    /// <summary>过渡被创建（<c>transitionrun</c>，含延迟阶段）。</summary>
    public Action<TransitionEvent>? OnTransitionRun { get => Get<TransitionEvent>(EventKind.TransitionRun); set => Set(EventKind.TransitionRun, value); }

    /// <summary>过渡的延迟结束、开始变化（<c>transitionstart</c>）。</summary>
    public Action<TransitionEvent>? OnTransitionStart { get => Get<TransitionEvent>(EventKind.TransitionStart); set => Set(EventKind.TransitionStart, value); }

    /// <summary>过渡完成（<c>transitionend</c>）。</summary>
    public Action<TransitionEvent>? OnTransitionEnd { get => Get<TransitionEvent>(EventKind.TransitionEnd); set => Set(EventKind.TransitionEnd, value); }

    /// <summary>过渡在完成前被取消（<c>transitioncancel</c>）。</summary>
    public Action<TransitionEvent>? OnTransitionCancel { get => Get<TransitionEvent>(EventKind.TransitionCancel); set => Set(EventKind.TransitionCancel, value); }

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

    // ---- 属性展开（@attributes）

    /// <summary>
    /// 属性展开（<c>.awaui</c> 的 <c>@attributes="..."</c>，design/17）：把属性表合并到本节点上。生成的代码在写完标签上显式的属性之后调用它，规则：
    /// <list type="bullet">
    /// <item><c>class</c>：拼接（显式的类名在前，展开的在后；值可以是 <see cref="string"/> 或 <see cref="ClassValue"/>）。</item>
    /// <item><c>style</c>：拼接，展开的声明在前、显式的在后，因此同名声明以显式的为准。</item>
    /// <item>其余属性<b>显式写出的优先</b>：<c>id</c>、<c>role</c>（<see cref="Role"/> 或 ARIA 角色名）、<c>tabindex</c>、<c>aria-label</c>、
    /// 事件处理器（<c>onclick</c> 等，值为 <see cref="Action{T}"/> 或无参的 <see cref="Action"/>）与元素自己的属性（如 <c>disabled</c>、<c>placeholder</c>）
    /// 只在节点上还没有设置时写入；其余名称（<c>data-*</c>、<c>aria-*</c> ...）只在 <see cref="Attributes"/> 中还没有同名属性时写入。</item>
    /// <item>写入 <see cref="Attributes"/> 的值：字符串原样；<see cref="bool"/> 对 <c>aria-*</c> / <c>data-*</c> 写成 <c>"true"</c> / <c>"false"</c>，
    /// 对其他名称按 HTML 布尔属性处理（true 为空串，false 不写）；数值按不变区域性格式化；null 值跳过。</item>
    /// </list>
    /// 同一个表里出现重复的名称时先出现的生效（<see cref="AttributeDictionary"/> 本身不会有重复）。事件处理器的类型不符时抛 <see cref="ArgumentException"/>。
    /// </summary>
    public void MergeAttributes(IEnumerable<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes)
        {
            if (kv.Key is null || kv.Value is null) continue;
            if (!MergeAttribute(kv.Key, kv.Value)) MergeCustomAttribute(kv.Key, kv.Value);
        }
    }

    /// <summary>属性展开：字符串值的属性表（规则同 <see cref="MergeAttributes(IEnumerable{KeyValuePair{string, object}})"/>）。</summary>
    public void MergeAttributes(IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes)
        {
            if (kv.Key is null || kv.Value is null) continue;
            if (!MergeAttribute(kv.Key, kv.Value)) MergeCustomAttribute(kv.Key, kv.Value);
        }
    }

    /// <summary>
    /// 属性展开的扩展点：把名为 <paramref name="name"/> 的展开值合并到本节点的强类型成员上（显式设置过的成员保持不变），
    /// 返回 true 表示已处理；返回 false 时作为自定义属性写入 <see cref="Attributes"/>。派生类先处理自己的属性，再调用基类。
    /// </summary>
    protected virtual bool MergeAttribute(string name, object value)
    {
        switch (name.ToLowerInvariant())
        {
            case "class":
            {
                string text = value is ClassValue cv ? cv.Text : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (text.Length > 0) Class = Class.IsEmpty ? new ClassValue(text) : new ClassValue(Class.Text + " " + text);
                return true;
            }
            case "style":
            {
                string text = (value is StyleValue sv ? sv.Text : Convert.ToString(value, CultureInfo.InvariantCulture))?.Trim().TrimEnd(';').Trim() ?? "";
                if (text.Length > 0) Style = Style.IsEmpty ? new StyleValue(text) : new StyleValue(text + "; " + Style.Text);
                return true;
            }
            case "id":
                Id ??= AttributeText(name, value);
                return true;
            case "aria-label":
                AriaLabel ??= AttributeText(name, value);
                return true;
            case "role":
                if (Role == Role.None) Role = value is Role r ? r : ParseRole(AttributeText(name, value));
                return true;
            case "tabindex":
                if (TabIndex is null)
                {
                    if (value is int i) TabIndex = i;
                    else if (int.TryParse(AttributeText(name, value)?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)) TabIndex = n;
                }
                return true;
            case "onclick": return MergeHandler<PointerEvent>(EventKind.Click, name, value);
            case "onpointerdown": return MergeHandler<PointerEvent>(EventKind.PointerDown, name, value);
            case "onpointerup": return MergeHandler<PointerEvent>(EventKind.PointerUp, name, value);
            case "onpointermove": return MergeHandler<PointerEvent>(EventKind.PointerMove, name, value);
            case "onpointerenter": return MergeHandler<PointerEvent>(EventKind.PointerEnter, name, value);
            case "onpointerleave": return MergeHandler<PointerEvent>(EventKind.PointerLeave, name, value);
            case "onpointercancel": return MergeHandler<PointerEvent>(EventKind.PointerCancel, name, value);
            case "onkeydown": return MergeHandler<KeyEvent>(EventKind.KeyDown, name, value);
            case "onkeyup": return MergeHandler<KeyEvent>(EventKind.KeyUp, name, value);
            case "onfocus": return MergeHandler<FocusEvent>(EventKind.Focus, name, value);
            case "onblur": return MergeHandler<FocusEvent>(EventKind.Blur, name, value);
            case "onfocusin": return MergeHandler<FocusEvent>(EventKind.FocusIn, name, value);
            case "onfocusout": return MergeHandler<FocusEvent>(EventKind.FocusOut, name, value);
            case "ontap": return MergeHandler<TapEvent>(EventKind.Tap, name, value);
            case "onpan": return MergeHandler<PanEvent>(EventKind.Pan, name, value);
            case "onscroll": return MergeHandler<ScrollEvent>(EventKind.Scroll, name, value);
            case "onwheel": return MergeHandler<WheelEvent>(EventKind.Wheel, name, value);
            case "onanimationstart": return MergeHandler<AnimationEvent>(EventKind.AnimationStart, name, value);
            case "onanimationiteration": return MergeHandler<AnimationEvent>(EventKind.AnimationIteration, name, value);
            case "onanimationend": return MergeHandler<AnimationEvent>(EventKind.AnimationEnd, name, value);
            case "onanimationcancel": return MergeHandler<AnimationEvent>(EventKind.AnimationCancel, name, value);
            case "ontransitionrun": return MergeHandler<TransitionEvent>(EventKind.TransitionRun, name, value);
            case "ontransitionstart": return MergeHandler<TransitionEvent>(EventKind.TransitionStart, name, value);
            case "ontransitionend": return MergeHandler<TransitionEvent>(EventKind.TransitionEnd, name, value);
            case "ontransitioncancel": return MergeHandler<TransitionEvent>(EventKind.TransitionCancel, name, value);
            default: return false;
        }
    }

    /// <summary>属性展开中的事件处理器：节点上还没有该事件的处理器时写入。</summary>
    private protected bool MergeHandler<T>(EventKind kind, string name, object value) where T : Event
    {
        if (_handlers?[(int)kind] is not null) return true;
        switch (value)
        {
            case Action<T> typed: Set(kind, typed); return true;
            case Action plain: Set(kind, new Action<T>(_ => plain())); return true;
            default:
                throw new ArgumentException($"属性展开中的 {name} 必须是 Action<{typeof(T).Name}> 或 Action，实际为 {value.GetType().Name}。", nameof(value));
        }
    }

    void MergeCustomAttribute(string name, object value)
    {
        var map = Attributes;
        if (map.Contains(name)) return;
        string? text = AttributeText(name, value);
        if (text is not null) map[name] = text;
    }

    /// <summary>展开值的文本形式：字符串原样；bool 对 aria-* / data-* 为 "true"/"false"，其余为 HTML 布尔属性（true → 空串，false → null）。</summary>
    private protected static string? AttributeText(string name, object value) => value switch
    {
        string s => s,
        bool b when name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) => b ? "true" : "false",
        bool b => b ? "" : null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>展开值作为布尔属性：bool 原样，字符串按 HTML 布尔属性（存在即真，<c>"false"</c> 为假）。</summary>
    private protected static bool AttributeFlag(object value) =>
        value is bool b ? b : !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>ARIA 角色名（大小写不敏感，含别名 img、none、gridcell）→ <see cref="UI.Role"/>；未知为 <see cref="Role.None"/>。</summary>
    static Role ParseRole(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return Role.None;
        // 只接受单个名字：Enum.TryParse 还接受数字与逗号分隔的多个名字（按位或，可能得到另一个角色，如 "button, link" → Link）
        foreach (char c in text!)
            if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z')) return Role.None;
        switch (text.ToLowerInvariant())
        {
            case "img": return Role.Image;
            case "none": return Role.Presentation;
            case "gridcell": return Role.Cell;
        }
        return Enum.TryParse(text, true, out Role role) ? role : Role.None;
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

using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.CodeGen;

/// <summary>内建元素属性在生成代码中的写法。</summary>
internal enum RuntimeAttrKind
{
    /// <summary>字符串属性：单一表达式直接赋值（类型由 C# 检查），字面量与表达式混合时拼接。</summary>
    String,
    /// <summary><c>Class</c>（<c>ClassValue</c>）：同 <see cref="String"/>，单一表达式可以是 <c>string</c> 或 <c>ClassValue</c>。</summary>
    Class,
    /// <summary><c>int?</c>：字面量编译期转换，表达式要求是单一表达式。</summary>
    Int,
    /// <summary><c>float?</c>：字面量编译期转换为 <c>float</c> 常量，表达式要求是单一表达式（类型由 C# 检查，可隐式转换为 <c>float?</c> 即可）。</summary>
    Number,
    /// <summary><c>bool</c>：只写属性名为 true。</summary>
    Bool,
    /// <summary><c>AwAUI.UI.Role</c> 枚举：ARIA 角色名在编译期映射为枚举成员。</summary>
    Role,
    /// <summary>运行时没有对应的强类型成员：作为自定义属性写入 <c>Attributes</c>（CSS 属性选择器与 DevTools 可见）。</summary>
    PassThrough,
}

/// <summary>内建元素的一个属性在运行时（AwAUI.UI）中的落点。</summary>
internal sealed class RuntimeAttr
{
    public RuntimeAttr(string member, RuntimeAttrKind kind, bool unsupported = false)
    {
        Member = member;
        Kind = kind;
        Unsupported = unsupported;
    }

    /// <summary>节点上的 C# 属性名；<see cref="RuntimeAttrKind.PassThrough"/> 时为属性名本身。</summary>
    public string Member { get; }
    public RuntimeAttrKind Kind { get; }

    /// <summary>语言允许、但运行时尚未解释其行为的属性（如 <c>maxlength</c>）：仍透传为自定义属性，同时报 AW4012 警告。</summary>
    public bool Unsupported { get; }
}

/// <summary>一个运行时事件：节点上的处理器属性与事件参数类型。</summary>
internal sealed class RuntimeEvent
{
    public RuntimeEvent(string member, string argsType)
    {
        Member = member;
        ArgsType = argsType;
    }

    /// <summary>处理器属性名，如 <c>OnClick</c>。</summary>
    public string Member { get; }

    /// <summary>事件参数类型的简单名（位于 <c>AwAUI.UI</c>），如 <c>PointerEvent</c>。</summary>
    public string ArgsType { get; }
}

/// <summary>
/// 目标运行时 <c>AwAUI.UI</c> 中实际存在的内建元素描述类型、属性与事件（design/12「元素与事件（M1 实现）」）。
/// 语言层面的元素/属性表（<see cref="Analysis.IntrinsicElements"/>，design/17）比运行时宽：
/// 运行时还没有的元素（<c>canvas</c>，以及 <c>svg</c> 之外的 <c>path</c>）与事件在代码生成时报 AW4012 错误，
/// 没有强类型成员的属性透传为自定义属性（行为尚未实现的报 AW4012 警告）。运行时补齐后只需更新这张表。
/// <para>
/// <c>svg</c> 映射到 <c>AwAUI.UI.Svg</c>：宿主属性（<see cref="IsSvgHostAttribute"/>：<c>id class style role tabindex aria-* data-*</c> 等）
/// 与指令属性落在元素节点上，其余属性与整个子树在编译期序列化为 SVG 文本（见 <c>CSharpEmitter</c>）。
/// </para>
/// </summary>
internal static class RuntimeElements
{
    public const string UI = "global::AwAUI.UI.";

    static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["div"] = "Div", ["span"] = "Span", ["text"] = "Text", ["img"] = "Image", ["input"] = "Input", ["textarea"] = "TextArea", ["svg"] = "Svg",
    };

    static readonly Dictionary<string, RuntimeAttr> Global = new(StringComparer.Ordinal)
    {
        ["id"] = new("Id", RuntimeAttrKind.String),
        ["class"] = new("Class", RuntimeAttrKind.Class),
        ["style"] = new("Style", RuntimeAttrKind.String),
        ["role"] = new("Role", RuntimeAttrKind.Role),
        ["tabindex"] = new("TabIndex", RuntimeAttrKind.Int),
        ["aria-label"] = new("AriaLabel", RuntimeAttrKind.String),
        // 纯信息性属性：透传即是完整语义
        ["title"] = new("title", RuntimeAttrKind.PassThrough),
        ["lang"] = new("lang", RuntimeAttrKind.PassThrough),
        ["dir"] = new("dir", RuntimeAttrKind.PassThrough),
        // 有行为但运行时尚未实现
        ["hidden"] = new("hidden", RuntimeAttrKind.PassThrough, unsupported: true),
        ["draggable"] = new("draggable", RuntimeAttrKind.PassThrough, unsupported: true),
    };

    static readonly Dictionary<string, Dictionary<string, RuntimeAttr>> PerElement = new(StringComparer.Ordinal)
    {
        ["img"] = Table(
            new("src", new("Source", RuntimeAttrKind.String)),
            new("alt", new("Alt", RuntimeAttrKind.String)),
            new("width", new("Width", RuntimeAttrKind.Number)),
            new("height", new("Height", RuntimeAttrKind.Number)),
            Unsupported("loading")),
        ["textarea"] = Table(
            new("value", new("Value", RuntimeAttrKind.String)),
            new("placeholder", new("Placeholder", RuntimeAttrKind.String)),
            new("disabled", new("Disabled", RuntimeAttrKind.Bool)),
            new("readonly", new("ReadOnly", RuntimeAttrKind.Bool)),
            new("name", new("name", RuntimeAttrKind.PassThrough)),
            Unsupported("required"), Unsupported("autofocus"), Unsupported("spellcheck"),
            Unsupported("maxlength"), Unsupported("minlength"), Unsupported("rows"), Unsupported("cols"), Unsupported("wrap")),
        ["input"] = Table(
            new("value", new("Value", RuntimeAttrKind.String)),
            new("placeholder", new("Placeholder", RuntimeAttrKind.String)),
            new("type", new("InputType", RuntimeAttrKind.String)),
            new("disabled", new("Disabled", RuntimeAttrKind.Bool)),
            new("readonly", new("ReadOnly", RuntimeAttrKind.Bool)),
            new("name", new("name", RuntimeAttrKind.PassThrough)),
            Unsupported("autocomplete"), Unsupported("pattern"), Unsupported("min"), Unsupported("max"), Unsupported("step"),
            Unsupported("inputmode"), Unsupported("required"), Unsupported("autofocus"), Unsupported("checked"),
            Unsupported("spellcheck"), Unsupported("maxlength"), Unsupported("minlength")),
    };

    /// <summary>全部内建元素共有的事件（<c>ElementNode</c> 上的处理器属性）。</summary>
    static readonly Dictionary<string, RuntimeEvent> GlobalEvents = new(StringComparer.Ordinal)
    {
        ["click"] = new("OnClick", "PointerEvent"),
        ["pointerdown"] = new("OnPointerDown", "PointerEvent"),
        ["pointerup"] = new("OnPointerUp", "PointerEvent"),
        ["pointermove"] = new("OnPointerMove", "PointerEvent"),
        ["pointerenter"] = new("OnPointerEnter", "PointerEvent"),
        ["pointerleave"] = new("OnPointerLeave", "PointerEvent"),
        ["pointercancel"] = new("OnPointerCancel", "PointerEvent"),
        ["keydown"] = new("OnKeyDown", "KeyEvent"),
        ["keyup"] = new("OnKeyUp", "KeyEvent"),
        ["focus"] = new("OnFocus", "FocusEvent"),
        ["blur"] = new("OnBlur", "FocusEvent"),
        ["focusin"] = new("OnFocusIn", "FocusEvent"),
        ["focusout"] = new("OnFocusOut", "FocusEvent"),
        ["tap"] = new("OnTap", "TapEvent"),
        ["pan"] = new("OnPan", "PanEvent"),
        ["longpress"] = new("OnLongPress", "LongPressEvent"),
        ["doubletap"] = new("OnDoubleTap", "DoubleTapEvent"),
        ["swipe"] = new("OnSwipe", "SwipeEvent"),
        ["pinch"] = new("OnPinch", "PinchEvent"),
        ["rotate"] = new("OnRotate", "RotateEvent"),
        ["edgeswipe"] = new("OnEdgeSwipe", "EdgeSwipeEvent"),
        ["scroll"] = new("OnScroll", "ScrollEvent"),
        ["wheel"] = new("OnWheel", "WheelEvent"),
    };

    static readonly Dictionary<string, RuntimeEvent> InputEvents = new(StringComparer.Ordinal)
    {
        ["input"] = new("OnInput", "InputEvent"),
        ["change"] = new("OnChange", "InputEvent"),
        ["submit"] = new("OnSubmit", "SubmitEvent"),
    };

    /// <summary>运行时中派生自 <c>AwAUI.UI.Event</c> 的事件参数类型（简单名）。</summary>
    static readonly HashSet<string> EventArgTypes = new(StringComparer.Ordinal)
    {
        "Event", "PointerEvent", "KeyEvent", "FocusEvent", "InputEvent", "SubmitEvent", "TapEvent", "PanEvent",
        "LongPressEvent", "DoubleTapEvent", "SwipeEvent", "PinchEvent", "RotateEvent", "EdgeSwipeEvent", "ScrollEvent", "WheelEvent",
    };

    /// <summary>ARIA 角色名 → <c>AwAUI.UI.Role</c> 成员名。</summary>
    static readonly Dictionary<string, string> Roles = BuildRoles();

    static Dictionary<string, string> BuildRoles()
    {
        var names = new[]
        {
            "Generic", "Button", "Link", "Text", "Image", "TextBox", "CheckBox", "Radio", "Switch", "Slider",
            "ProgressBar", "ComboBox", "ListBox", "Option", "List", "ListItem", "Tree", "TreeItem", "Grid", "Row", "Cell",
            "Tab", "TabList", "TabPanel", "Menu", "MenuBar", "MenuItem", "Dialog", "AlertDialog", "Alert", "Status",
            "Tooltip", "Toolbar", "Separator", "Group", "Region", "Navigation", "Heading", "Presentation",
        };
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in names) map[n.ToLowerInvariant()] = n;
        // ARIA 的别名
        map["img"] = "Image";
        map["none"] = "Presentation";
        map["gridcell"] = "Cell";
        return map;
    }

    /// <summary>
    /// 内联 <c>svg</c> 根元素上落在元素节点（而不是 SVG 文档）上的属性：通用属性表中的属性（<c>id class style role tabindex aria-label
    /// title lang dir hidden draggable</c>）与 <c>aria-* / data-*</c>。<c>class</c> 与 <c>style</c> 因此是宿主元素的 CSS（如图标的 <c>color</c>），
    /// 不是文档内部的样式。
    /// </summary>
    public static bool IsSvgHostAttribute(string name) =>
        Global.ContainsKey(name)
        || ((name.StartsWith("aria-", StringComparison.Ordinal) || name.StartsWith("data-", StringComparison.Ordinal)) && name.Length > 5);

    /// <summary>内建元素对应的运行时类型（<c>global::</c> 全限定）；运行时还没有该元素时返回 null。</summary>
    public static string? TypeOf(string element) => Types.TryGetValue(element, out var t) ? UI + t : null;

    /// <summary>
    /// 查找内建元素属性的落点。<c>aria-*</c>（<c>aria-label</c> 除外）与 <c>data-*</c> 透传；
    /// 语言表中没有的属性返回 null（已由 AW4003 报告）。
    /// </summary>
    public static RuntimeAttr? Attribute(string element, string name)
    {
        if (PerElement.TryGetValue(element, out var table) && table.TryGetValue(name, out var a)) return a;
        if (Global.TryGetValue(name, out a)) return a;
        if ((name.StartsWith("aria-", StringComparison.Ordinal) || name.StartsWith("data-", StringComparison.Ordinal)) && name.Length > 5)
            return new RuntimeAttr(name, RuntimeAttrKind.PassThrough);
        return null;
    }

    /// <summary>内建元素（<paramref name="element"/> 为 null 时按通用 <c>ElementNode</c>）上的运行时事件；不存在返回 null。</summary>
    public static RuntimeEvent? Event(string? element, string name)
    {
        if (GlobalEvents.TryGetValue(name, out var e)) return e;
        if (element is "input" or "textarea" && InputEvents.TryGetValue(name, out e)) return e;
        return null;
    }

    /// <summary>不限元素：任何运行时事件（用于组件与大写的元素描述类型）。</summary>
    public static RuntimeEvent? AnyEvent(string name) =>
        GlobalEvents.TryGetValue(name, out var e) || InputEvents.TryGetValue(name, out e) ? e : null;

    /// <summary>
    /// 从处理器属性的类型全名（<c>global::System.Action&lt;global::AwAUI.UI.PointerEvent&gt;</c>）中取出运行时事件参数类型的简单名；
    /// 不是这种形状时返回 null。
    /// </summary>
    public static string? EventArgsOfHandlerType(string? typeName)
    {
        if (typeName is null) return null;
        const string prefix = "global::System.Action<global::AwAUI.UI.";
        string t = typeName.EndsWith("?", StringComparison.Ordinal) ? typeName.Substring(0, typeName.Length - 1) : typeName;
        if (!t.StartsWith(prefix, StringComparison.Ordinal) || !t.EndsWith(">", StringComparison.Ordinal)) return null;
        string inner = t.Substring(prefix.Length, t.Length - prefix.Length - 1);
        return EventArgTypes.Contains(inner) ? inner : null;
    }

    /// <summary>ARIA 角色名 → <c>Role</c> 成员名（大小写不敏感）；未知返回 null。</summary>
    public static string? RoleMember(string role) => Roles.TryGetValue(role.Trim().ToLowerInvariant(), out var m) ? m : null;

    public static IEnumerable<string> RoleNames => Roles.Keys;

    static KeyValuePair<string, RuntimeAttr> Unsupported(string name) => new(name, new RuntimeAttr(name, RuntimeAttrKind.PassThrough, unsupported: true));

    static Dictionary<string, RuntimeAttr> Table(params KeyValuePair<string, RuntimeAttr>[] items)
    {
        var d = new Dictionary<string, RuntimeAttr>(StringComparer.Ordinal);
        foreach (var kv in items) d[kv.Key] = kv.Value;
        return d;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Platform;

namespace AwAUI.MacOS;

/// <summary>需要投递的 NSAccessibility 通知（design/10「平台桥」）。</summary>
internal enum MacAccessibilityNotification
{
    /// <summary>焦点元素变化。</summary>
    FocusedUIElementChanged,

    /// <summary>值变化。</summary>
    ValueChanged,

    /// <summary>选中子元素变化。</summary>
    SelectedChildrenChanged,

    /// <summary>布局变化（边界 / 树的形状）。</summary>
    LayoutChanged,

    /// <summary>播报请求（实时区域）；系统不支持时退化为值变化。</summary>
    AnnouncementRequested,
}

/// <summary>平台无关的 AX 值种类。</summary>
internal enum MacAccessibilityValueKind
{
    /// <summary>没有值。</summary>
    None,

    /// <summary>文本值。</summary>
    Text,

    /// <summary>数值（滑块、进度条、数值框）。</summary>
    Number,

    /// <summary>布尔值（复选框、单选、开关）。</summary>
    Boolean,
}

/// <summary>
/// 格式化后的 AX 值：平台桥据此选择 NSString / NSNumber。纯数据，便于单元测试。
/// </summary>
internal readonly struct MacAccessibilityValue
{
    MacAccessibilityValue(MacAccessibilityValueKind kind, string? text, double number, bool boolean)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Boolean = boolean;
    }

    public MacAccessibilityValueKind Kind { get; }

    /// <summary>文本值（<see cref="MacAccessibilityValueKind.Text"/> 时）。</summary>
    public string? Text { get; }

    /// <summary>数值（<see cref="MacAccessibilityValueKind.Number"/> 时）。</summary>
    public double Number { get; }

    /// <summary>布尔值（<see cref="MacAccessibilityValueKind.Boolean"/> 时）。</summary>
    public bool Boolean { get; }

    public static MacAccessibilityValue None => new(MacAccessibilityValueKind.None, null, 0, false);

    public static MacAccessibilityValue FromText(string text) =>
        new(MacAccessibilityValueKind.Text, text ?? "", 0, false);

    public static MacAccessibilityValue FromNumber(double number) =>
        new(MacAccessibilityValueKind.Number, null, number, false);

    public static MacAccessibilityValue FromBoolean(bool value) =>
        new(MacAccessibilityValueKind.Boolean, null, 0, value);
}

/// <summary>Cocoa 屏幕坐标中的矩形（原点在主显示器左下，y 向上）。</summary>
internal readonly struct MacRect
{
    public MacRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
}

/// <summary>
/// macOS 无障碍桥中与 AppKit 无关的纯逻辑：ARIA → NSAccessibility 角色 / 子角色映射、
/// Cocoa 屏幕矩形换算、值格式化、动作与通知名选择。
/// 放在 Input/ 目录里以便 <c>tests/AwAUI.Platform.UnitTests</c> 以源码链接方式测试（只用 netstandard2.0 可用的 API）。
/// </summary>
internal static class MacAccessibility
{
    // ---------------------------------------------------------------- 角色与子角色

    /// <summary>ARIA 角色（或显式 <c>role</c>）→ NSAccessibility 角色；未知角色映射到 <c>AXGroup</c>。</summary>
    public static string Role(string? ariaRole)
    {
        switch (Normalize(ariaRole))
        {
            case "button":
            case "pushbutton":
            case "push-button":
                return "AXButton";
            case "checkbox":
            case "check-box":
                return "AXCheckBox";
            case "radio":
            case "radiobutton":
            case "radio-button":
                return "AXRadioButton";
            case "switch":
                return "AXCheckBox";
            case "statictext":
            case "static-text":
            case "text":
                return "AXStaticText";
            case "textbox":
            case "textfield":
            case "text-field":
            case "searchbox":
            case "search":
                return "AXTextField";
            case "textarea":
            case "text-area":
                return "AXTextArea";
            case "link":
                return "AXLink";
            case "heading":
                return "AXHeading";
            case "list":
                return "AXList";
            case "listitem":
            case "list-item":
            case "listrow":
            case "list-row":
            case "option":
                return "AXRow";
            case "table":
                return "AXTable";
            case "row":
                return "AXRow";
            case "cell":
            case "gridcell":
            case "grid-cell":
            case "columnheader":
            case "rowheader":
                return "AXCell";
            case "combobox":
            case "combo-box":
                return "AXComboBox";
            case "popupbutton":
            case "popup-button":
            case "pop-up-button":
            case "menubutton":
            case "menu-button":
                return "AXPopUpButton";
            case "slider":
            case "rangeslider":
            case "range-slider":
                return "AXSlider";
            case "progressbar":
            case "progress-bar":
            case "progressindicator":
            case "progress-indicator":
                return "AXProgressIndicator";
            case "tablist":
            case "tab-list":
            case "tabgroup":
            case "tab-group":
                return "AXTabGroup";
            case "tab":
                return "AXTab";
            case "scrollarea":
            case "scroll-area":
            case "scrollview":
            case "scroll-view":
            case "scroll":
                return "AXScrollArea";
            case "separator":
            case "splitter":
            case "hr":
                return "AXSeparator";
            case "image":
            case "img":
                return "AXImage";
            case "dialog":
            case "alertdialog":
            case "alert-dialog":
                return "AXGroup";
            case "spinbutton":
            case "spin-button":
            case "number":
                return "AXIncrementor";
            case "menuitem":
            case "menu-item":
            case "menuitemcheckbox":
            case "menuitemradio":
                return "AXMenuItem";
            case "group":
            case "root":
            case "generic":
            case "navigation":
            case "region":
            case "article":
            case "complementary":
            case "banner":
            case "contentinfo":
            case "form":
            case "main":
            case "menu":
            case "rowgroup":
                return "AXGroup";
            default:
                return "AXGroup";
        }
    }

    /// <summary>ARIA 角色 → NSAccessibility 子角色；没有合适的子角色时返回 null。</summary>
    public static string? Subrole(string? ariaRole)
    {
        switch (Normalize(ariaRole))
        {
            case "dialog":
            case "alertdialog":
            case "alert-dialog":
                return "AXDialog";
            case "searchbox":
            case "search":
                return "AXSearchField";
            case "switch":
                return "AXSwitch";
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- 动作

    /// <summary>平台无关动作 → NSAccessibility 动作名；无法用动作表达的（SetValue / Focus）返回 null。</summary>
    public static string? ActionName(AccessibilityActionKind action)
    {
        switch (action)
        {
            case AccessibilityActionKind.Invoke:
            case AccessibilityActionKind.Toggle:
                return "AXPress";
            case AccessibilityActionKind.Increment:
                return "AXIncrement";
            case AccessibilityActionKind.Decrement:
                return "AXDecrement";
            case AccessibilityActionKind.ScrollIntoView:
                return "AXScrollToVisible";
            case AccessibilityActionKind.Expand:
                return "AXShowMenu";
            case AccessibilityActionKind.Collapse:
                return "AXCancel";
            default:
                return null;
        }
    }

    /// <summary>NSAccessibility 动作名 → 平台无关动作；未知动作返回 null。</summary>
    public static AccessibilityActionKind? ActionKind(string? actionName)
    {
        switch (actionName)
        {
            case "AXPress":
            case "AXConfirm":
            case "AXPick":
                return AccessibilityActionKind.Invoke;
            case "AXIncrement":
                return AccessibilityActionKind.Increment;
            case "AXDecrement":
                return AccessibilityActionKind.Decrement;
            case "AXScrollToVisible":
                return AccessibilityActionKind.ScrollIntoView;
            case "AXShowMenu":
                return AccessibilityActionKind.Expand;
            case "AXCancel":
                return AccessibilityActionKind.Collapse;
            default:
                return null;
        }
    }

    /// <summary>
    /// 节点可用的 NSAccessibility 动作名（按快照里的动作列表）。按钮额外暴露 <c>AXConfirm</c>，
    /// 可展开 / 折叠的节点分别暴露 <c>AXShowMenu</c> / <c>AXCancel</c>。结果去重并保持稳定顺序。
    /// </summary>
    public static IReadOnlyList<string> ActionNamesFor(IReadOnlyList<AccessibilityActionKind>? actions, string? ariaRole)
    {
        var names = new List<string>(5);
        if (actions is null) return names;
        string role = Normalize(ariaRole);

        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var name = ActionName(action);
            if (name is not null && !names.Contains(name)) names.Add(name);

            if (action == AccessibilityActionKind.Invoke && (role == "button" || role == "pushbutton" || role == "push-button"))
                AddOnce(names, "AXConfirm");
        }

        return names;
    }

    // ---------------------------------------------------------------- 通知

    /// <summary>通知种类 → NSAccessibility 通知常量名。</summary>
    public static string NotificationName(MacAccessibilityNotification notification)
    {
        switch (notification)
        {
            case MacAccessibilityNotification.FocusedUIElementChanged:
                return "NSAccessibilityFocusedUIElementChangedNotification";
            case MacAccessibilityNotification.ValueChanged:
                return "NSAccessibilityValueChangedNotification";
            case MacAccessibilityNotification.SelectedChildrenChanged:
                return "NSAccessibilitySelectedChildrenChangedNotification";
            case MacAccessibilityNotification.LayoutChanged:
                return "NSAccessibilityLayoutChangedNotification";
            case MacAccessibilityNotification.AnnouncementRequested:
                return "NSAccessibilityAnnouncementRequestedNotification";
            default:
                return "NSAccessibilityLayoutChangedNotification";
        }
    }

    // ---------------------------------------------------------------- 几何

    /// <summary>
    /// 客户区矩形（原点在内容区左上、y 向下）→ 屏幕 Cocoa 矩形（原点在主显示器左下、y 向上）。
    /// <paramref name="contentHeight"/> 是内容区高度；<paramref name="windowCocoaX"/> /
    /// <paramref name="windowCocoaY"/> 是窗口 frame 在 Cocoa 坐标中的左下角（标题栏不计入内容区，位于内容区之上）。
    /// </summary>
    public static MacRect ToScreenCocoa(Rect clientRect, double contentHeight, double windowCocoaX, double windowCocoaY)
    {
        double x = windowCocoaX + clientRect.X;
        double top = windowCocoaY + contentHeight - clientRect.Y;
        return new MacRect(x, top - clientRect.Height, clientRect.Width, clientRect.Height);
    }

    /// <summary>框架坐标（y 向下）中的 y → Cocoa 屏幕坐标 y。</summary>
    public static double ToCocoaY(double y, double contentHeight, double windowCocoaY) =>
        windowCocoaY + contentHeight - y;

    // ---------------------------------------------------------------- 值格式化

    /// <summary>角色 → AX 值：文本 / 数值 / 布尔。复选框优先使用 <c>checked</c> / <c>mixed</c> 状态。</summary>
    public static MacAccessibilityValue FormatValue(string? ariaRole, string? value, IReadOnlyList<string>? states)
    {
        string role = Normalize(ariaRole);
        bool hasText = value is not null && value.Length > 0;

        if (IsCheckable(role))
        {
            if (ContainsState(states, "mixed")) return MacAccessibilityValue.FromText("mixed");
            return MacAccessibilityValue.FromBoolean(ContainsState(states, "checked"));
        }

        if (IsNumeric(role))
        {
            if (hasText && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return MacAccessibilityValue.FromNumber(number);
            return hasText ? MacAccessibilityValue.FromText(value!) : MacAccessibilityValue.None;
        }

        if (IsTextual(role)) return value is null ? MacAccessibilityValue.None : MacAccessibilityValue.FromText(value);
        return hasText ? MacAccessibilityValue.FromText(value!) : MacAccessibilityValue.None;
    }

    /// <summary>角色是否具有 <c>AXMinValue</c> / <c>AXMaxValue</c>；有则给出默认区间。</summary>
    public static bool TryGetRange(string? ariaRole, out double min, out double max)
    {
        string role = Normalize(ariaRole);
        if (role == "slider" || role == "rangeslider" || role == "range-slider" ||
            role == "spinbutton" || role == "spin-button" || role == "number" ||
            role == "progressbar" || role == "progress-bar")
        {
            min = 0;
            max = 100;
            return true;
        }
        min = 0;
        max = 0;
        return false;
    }

    /// <summary>按边界宽高比给出方向：宽于高为水平，否则垂直。</summary>
    public static string Orientation(Rect bounds) =>
        bounds.Width >= bounds.Height ? "AXHorizontalOrientation" : "AXVerticalOrientation";

    // ---------------------------------------------------------------- 状态与角色分类

    /// <summary>状态列表是否包含某个标记（快照里的标记均为小写、Ordinal 比较）。</summary>
    public static bool ContainsState(IReadOnlyList<string>? states, string name)
    {
        if (states is null) return false;
        for (int i = 0; i < states.Count; i++)
            if (string.Equals(states[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>复选框 / 单选 / 开关（值用布尔表达）。</summary>
    public static bool IsCheckable(string? ariaRole)
    {
        string role = Normalize(ariaRole);
        return role == "checkbox" || role == "check-box" || role == "radio" ||
            role == "radiobutton" || role == "radio-button" || role == "switch";
    }

    /// <summary>滑块 / 进度条 / 数值框（值用数值表达、有区间）。</summary>
    public static bool IsNumeric(string? ariaRole)
    {
        string role = Normalize(ariaRole);
        return role == "slider" || role == "rangeslider" || role == "range-slider" ||
            role == "spinbutton" || role == "spin-button" || role == "number" ||
            role == "progressbar" || role == "progress-bar";
    }

    /// <summary>可编辑文本角色（值即使为空串也应报告）。</summary>
    public static bool IsTextual(string? ariaRole)
    {
        string role = Normalize(ariaRole);
        return role == "textbox" || role == "textfield" || role == "text-field" ||
            role == "textarea" || role == "text-area" || role == "searchbox" || role == "search" ||
            role == "combobox" || role == "combo-box";
    }

    static void AddOnce(List<string> names, string name)
    {
        if (!names.Contains(name)) names.Add(name);
    }

    static string Normalize(string? role)
    {
        if (role is null) return "";
        return role.Trim().ToLowerInvariant();
    }
}

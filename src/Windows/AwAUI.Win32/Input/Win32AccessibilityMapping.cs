using System;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 纯逻辑：ARIA 角色 / 状态 / 动作 → UI Automation 控件类型、模式位掩码与属性值。
// 不引用任何 Win32 互操作，测试直接传入假快照验证。

/// <summary>语义快照到 UIA 的映射规则。</summary>
internal static class Win32AccessibilityMapping
{
    static readonly string[] s_itemRoles =
    {
        "listitem", "option", "tab", "treeitem", "menuitem", "menuitemcheckbox", "menuitemradio", "radio", "row",
    };

    static readonly string[] s_containerRoles =
    {
        "list", "listbox", "menu", "menubar", "tablist", "tree", "grid", "treegrid", "table", "radiogroup",
    };

    static readonly string[] s_gridRoles = { "table", "grid", "treegrid", "spreadsheet" };

    static readonly string[] s_gridItemRoles = { "cell", "gridcell", "columnheader", "rowheader", "dataitem" };

    static readonly string[] s_valueRoles = { "textbox", "searchbox", "combobox", "edit" };

    static readonly string[] s_rangeRoles = { "slider", "spinbutton", "progressbar" };

    /// <summary>ARIA 角色 → UIA ControlTypeId。</summary>
    public static int ControlType(string role)
    {
        switch (role)
        {
            case "root": return Win32UiaControlTypeIds.Window;
            case "button": case "summary": return Win32UiaControlTypeIds.Button;
            case "link": return Win32UiaControlTypeIds.Hyperlink;
            case "checkbox": case "switch": return Win32UiaControlTypeIds.CheckBox;
            case "radio": return Win32UiaControlTypeIds.RadioButton;
            case "textbox": case "searchbox": return Win32UiaControlTypeIds.Edit;
            case "combobox": return Win32UiaControlTypeIds.ComboBox;
            case "list": case "listbox": return Win32UiaControlTypeIds.List;
            case "listitem": case "option": return Win32UiaControlTypeIds.ListItem;
            case "tab": return Win32UiaControlTypeIds.TabItem;
            case "tablist": return Win32UiaControlTypeIds.Tab;
            case "menu": return Win32UiaControlTypeIds.Menu;
            case "menubar": return Win32UiaControlTypeIds.MenuBar;
            case "menuitem": case "menuitemcheckbox": case "menuitemradio": return Win32UiaControlTypeIds.MenuItem;
            case "tree": return Win32UiaControlTypeIds.Tree;
            case "treeitem": return Win32UiaControlTypeIds.TreeItem;
            case "slider": return Win32UiaControlTypeIds.Slider;
            case "spinbutton": return Win32UiaControlTypeIds.Spinner;
            case "progressbar": return Win32UiaControlTypeIds.ProgressBar;
            case "heading": return Win32UiaControlTypeIds.Text;
            case "text": case "alert": case "status": case "log": case "marquee": case "timer":
                return Win32UiaControlTypeIds.Text;
            case "img": case "image": case "figure": return Win32UiaControlTypeIds.Image;
            case "dialog": case "alertdialog": return Win32UiaControlTypeIds.Window;
            case "table": return Win32UiaControlTypeIds.Table;
            case "grid": return Win32UiaControlTypeIds.DataGrid;
            case "row": case "cell": case "gridcell": case "dataitem": return Win32UiaControlTypeIds.DataItem;
            case "columnheader": case "rowheader": return Win32UiaControlTypeIds.HeaderItem;
            case "separator": case "hr": return Win32UiaControlTypeIds.Separator;
            case "tooltip": return Win32UiaControlTypeIds.ToolTip;
            case "toolbar": return Win32UiaControlTypeIds.ToolBar;
            case "document": return Win32UiaControlTypeIds.Document;
            case "statusbar": return Win32UiaControlTypeIds.StatusBar;
            case "group": case "region": case "form": case "article": case "navigation": case "main":
            case "banner": case "contentinfo": case "complementary": case "aside": case "rowgroup":
                return Win32UiaControlTypeIds.Group;
            case "generic": case "presentation": case "none": return Win32UiaControlTypeIds.Group;
            default: return Win32UiaControlTypeIds.Custom;
        }
    }

    /// <summary>节点可映射的 UIA 控件模式。</summary>
    public static Win32AccessibilityPattern Patterns(AccessibilityNodeSnapshot node)
    {
        if (node is null) return Win32AccessibilityPattern.None;
        var role = node.Role ?? "";
        var patterns = Win32AccessibilityPattern.None;

        if (HasAction(node, AccessibilityActionKind.Invoke) || role == "button") patterns |= Win32AccessibilityPattern.Invoke;
        if (HasAction(node, AccessibilityActionKind.Toggle) || role == "checkbox" || role == "radio" || role == "switch"
            || role == "menuitemcheckbox" || role == "menuitemradio")
            patterns |= Win32AccessibilityPattern.Toggle;
        if (node.Text is not null || Contains(s_valueRoles, role))
            patterns |= Win32AccessibilityPattern.Value;
        if (Contains(s_rangeRoles, role))
            patterns |= Win32AccessibilityPattern.RangeValue;
        if (HasAction(node, AccessibilityActionKind.Expand) || HasAction(node, AccessibilityActionKind.Collapse)
            || HasState(node, "expanded"))
            patterns |= Win32AccessibilityPattern.ExpandCollapse;
        if (HasState(node, "selected") || Contains(s_itemRoles, role))
            patterns |= Win32AccessibilityPattern.SelectionItem;
        if (Contains(s_containerRoles, role))
            patterns |= Win32AccessibilityPattern.Selection;
        if (Contains(s_gridRoles, role))
            patterns |= Win32AccessibilityPattern.Grid;
        if (Contains(s_gridItemRoles, role))
            patterns |= Win32AccessibilityPattern.GridItem;
        if (HasAction(node, AccessibilityActionKind.ScrollIntoView))
            patterns |= Win32AccessibilityPattern.ScrollItem;
        return patterns;
    }

    /// <summary>节点是否暴露指定模式。</summary>
    public static bool HasPattern(AccessibilityNodeSnapshot node, Win32AccessibilityPattern pattern) =>
        (Patterns(node) & pattern) == pattern;

    public static bool HasState(AccessibilityNodeSnapshot node, string state)
    {
        var states = node.States;
        if (states is null) return false;
        for (int i = 0; i < states.Count; i++)
            if (string.Equals(states[i], state, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool HasAction(AccessibilityNodeSnapshot node, AccessibilityActionKind action)
    {
        var actions = node.Actions;
        if (actions is null) return false;
        for (int i = 0; i < actions.Count; i++)
            if (actions[i] == action) return true;
        return false;
    }

    public static bool IsEnabled(AccessibilityNodeSnapshot node) => !HasState(node, "disabled");

    public static bool IsKeyboardFocusable(AccessibilityNodeSnapshot node) =>
        HasAction(node, AccessibilityActionKind.Focus);

    public static bool HasKeyboardFocus(AccessibilityNodeSnapshot node) => HasState(node, "focused");

    public static bool IsRequiredForForm(AccessibilityNodeSnapshot node) => HasState(node, "required");

    public static bool IsReadOnly(AccessibilityNodeSnapshot node) =>
        HasState(node, "readonly") || !HasAction(node, AccessibilityActionKind.SetValue);

    public static bool IsInvalid(AccessibilityNodeSnapshot node) => HasState(node, "invalid");

    public static bool IsSelected(AccessibilityNodeSnapshot node) => HasState(node, "selected");

    public static bool IsControlElement(AccessibilityNodeSnapshot node) => node is not null;

    public static bool IsPassword(AccessibilityNodeSnapshot node) => false;

    /// <summary>UIA ToggleState。</summary>
    public static int ToggleState(AccessibilityNodeSnapshot node)
    {
        if (HasState(node, "mixed")) return Win32UiaConstants.ToggleStateIndeterminate;
        if (HasState(node, "checked") || HasState(node, "pressed")) return Win32UiaConstants.ToggleStateOn;
        return Win32UiaConstants.ToggleStateOff;
    }

    /// <summary>UIA ExpandCollapseState。</summary>
    public static int ExpandCollapseState(AccessibilityNodeSnapshot node)
    {
        if (HasState(node, "expanded")) return Win32UiaConstants.ExpandCollapseStateExpanded;
        if (HasAction(node, AccessibilityActionKind.Expand) || HasAction(node, AccessibilityActionKind.Collapse))
            return Win32UiaConstants.ExpandCollapseStateCollapsed;
        return Win32UiaConstants.ExpandCollapseStateLeafNode;
    }

    /// <summary>UIA LiveSetting。</summary>
    public static int LiveSetting(AccessibilityNodeSnapshot node)
    {
        switch (node.LiveRegion)
        {
            case AccessibilityPoliteness.Polite: return Win32UiaConstants.LiveSettingPolite;
            case AccessibilityPoliteness.Assertive: return Win32UiaConstants.LiveSettingAssertive;
            default: return Win32UiaConstants.LiveSettingOff;
        }
    }

    /// <summary>UIA Level（标题层级；非标题为 0）。</summary>
    public static int Level(AccessibilityNodeSnapshot node) => node.HeadingLevel ?? 0;

    public static string Name(AccessibilityNodeSnapshot node) => node.Name ?? "";

    public static string HelpText(AccessibilityNodeSnapshot node) => node.Description ?? "";

    public static string ClassName(string role) => "AwAUI." + role;

    public static string LocalizedControlType(string role) => role;

    public static string ItemType(string role) => role;

    public static string FrameworkId => "AwAUI";

    /// <summary>可编辑文本或普通值。</summary>
    public static string TextValue(AccessibilityNodeSnapshot node) =>
        node.Text?.Value ?? node.Value ?? "";

    /// <summary>
    /// 从名称里的 Windows 助记符（<c>&amp;F</c>）推导访问键；快照没有单独携带 accesskey，
    /// 因此这是唯一可用的来源。没有助记符时返回空串。
    /// </summary>
    public static string AccessKey(AccessibilityNodeSnapshot node)
    {
        var name = node.Name;
        if (string.IsNullOrEmpty(name)) return "";
        for (int i = 0; i < name!.Length; i++)
        {
            if (name[i] != '&') continue;
            if (i + 1 < name.Length && name[i + 1] == '&') { i++; continue; } // "&&" 是字面量 &
            if (i + 1 < name.Length && !char.IsWhiteSpace(name[i + 1]))
                return char.ToUpperInvariant(name[i + 1]).ToString();
        }
        return "";
    }

    /// <summary>快照没有快捷键信息（ARIA <c>aria-keyshortcuts</c> 未进入快照），返回空串。</summary>
    public static string AcceleratorKey(AccessibilityNodeSnapshot node) => "";

    /// <summary>ItemStatus：把与辅助技术相关的状态拼成可读字符串。</summary>
    public static string ItemStatus(AccessibilityNodeSnapshot node)
    {
        var sb = new StringBuilder();
        Append(node, "mixed", sb);
        Append(node, "checked", sb);
        Append(node, "selected", sb);
        Append(node, "expanded", sb);
        Append(node, "pressed", sb);
        Append(node, "invalid", sb);
        Append(node, "busy", sb);
        Append(node, "required", sb);
        Append(node, "readonly", sb);
        Append(node, "disabled", sb);
        return sb.ToString();
    }

    static void Append(AccessibilityNodeSnapshot node, string state, StringBuilder sb)
    {
        if (!HasState(node, state)) return;
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(state);
    }

    /// <summary>UIA PositionInSet：优先使用 aria-posinset，否则按同级序号推算。</summary>
    public static int PositionInSet(AccessibilityNodeSnapshot node, int siblingIndex, bool isItemRole)
    {
        if (node.PosInSet is int explicitPosition) return explicitPosition;
        return isItemRole ? siblingIndex + 1 : 0;
    }

    /// <summary>UIA SizeOfSet：优先使用 aria-setsize，否则用同级数量。</summary>
    public static int SizeOfSet(AccessibilityNodeSnapshot node, int siblingCount, bool isItemRole)
    {
        if (node.SetSize is int explicitSize) return explicitSize;
        return isItemRole ? siblingCount : 0;
    }

    public static bool IsItemRole(string role) => Contains(s_itemRoles, role);

    public static bool IsSelectionContainerRole(string role) => Contains(s_containerRoles, role);

    public static bool IsGridRole(string role) => Contains(s_gridRoles, role);

    public static bool IsGridItemRole(string role) => Contains(s_gridItemRoles, role);

    static bool Contains(string[] values, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (int i = 0; i < values.Length; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
        return false;
    }
}

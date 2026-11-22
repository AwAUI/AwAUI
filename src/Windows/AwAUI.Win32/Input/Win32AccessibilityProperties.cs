using System;
using System.Globalization;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 纯逻辑：按 UIA 属性 ID 读取节点快照。返回平台无关的 Win32AccessibilityValue，
// 由 Accessibility/ 中的 COM 层转换成 VARIANT / SAFEARRAY。测试直接断言这里的值。

internal enum Win32AccessibilityValueKind
{
    Empty = 0,
    String,
    Int32,
    Double,
    Boolean,
    Rect,
    NodeId,
    NodeIds,
}

/// <summary>一个 UIA 属性值的平台无关表示。</summary>
internal struct Win32AccessibilityValue
{
    Win32AccessibilityValue(Win32AccessibilityValueKind kind)
    {
        Kind = kind;
        StringValue = null;
        IntValue = 0;
        DoubleValue = 0;
        BoolValue = false;
        RectValue = default;
        NodeId = -1;
        NodeIds = null;
    }

    public Win32AccessibilityValueKind Kind { get; private set; }
    public string? StringValue { get; private set; }
    public int IntValue { get; private set; }
    public double DoubleValue { get; private set; }
    public bool BoolValue { get; private set; }
    public Win32AccessibilityRect RectValue { get; private set; }
    public int NodeId { get; private set; }
    public int[]? NodeIds { get; private set; }

    public static Win32AccessibilityValue Empty => new(Win32AccessibilityValueKind.Empty);
    public static Win32AccessibilityValue FromString(string value) =>
        new(Win32AccessibilityValueKind.String) { StringValue = value ?? "" };
    public static Win32AccessibilityValue FromInt(int value) =>
        new(Win32AccessibilityValueKind.Int32) { IntValue = value };
    public static Win32AccessibilityValue FromDouble(double value) =>
        new(Win32AccessibilityValueKind.Double) { DoubleValue = value };
    public static Win32AccessibilityValue FromBool(bool value) =>
        new(Win32AccessibilityValueKind.Boolean) { BoolValue = value };
    public static Win32AccessibilityValue FromRect(Win32AccessibilityRect value) =>
        new(Win32AccessibilityValueKind.Rect) { RectValue = value };
    public static Win32AccessibilityValue FromNode(int nodeId) =>
        new(Win32AccessibilityValueKind.NodeId) { NodeId = nodeId };
    public static Win32AccessibilityValue FromNodes(int[] nodeIds) =>
        new(Win32AccessibilityValueKind.NodeIds) { NodeIds = nodeIds ?? Array.Empty<int>() };
}

/// <summary>按 UIA 属性 ID 读取索引中的节点。</summary>
internal static class Win32AccessibilityPropertyReader
{
    public static Win32AccessibilityValue GetProperty(Win32AccessibilityIndex? index, int nodeId, int propertyId,
        Win32AccessibilityClientGeometry geometry, Rect clientBounds)
    {
        if (index is null || !index.TryGet(nodeId, out var entry) || entry is null)
            return Win32AccessibilityValue.Empty;
        var node = entry.Node;
        var role = node.Role ?? "";
        var patterns = entry.Patterns;

        switch (propertyId)
        {
            // ---------------------------------------------------------- 标识与基本信息
            case Win32UiaPropertyIds.BoundingRectangle:
                return Win32AccessibilityValue.FromRect(
                    Win32AccessibilityGeometry.ToScreenRect(node.Bounds, geometry));
            case Win32UiaPropertyIds.ControlType:
                return Win32AccessibilityValue.FromInt(entry.ControlTypeId);
            case Win32UiaPropertyIds.LocalizedControlType:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.LocalizedControlType(role));
            case Win32UiaPropertyIds.Name:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.Name(node));
            case Win32UiaPropertyIds.AutomationId:
                return Win32AccessibilityValue.FromString(entry.StringId);
            case Win32UiaPropertyIds.ClassName:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.ClassName(role));
            case Win32UiaPropertyIds.HelpText:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.HelpText(node));
            case Win32UiaPropertyIds.ItemType:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.ItemType(role));
            case Win32UiaPropertyIds.AcceleratorKey:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.AcceleratorKey(node));
            case Win32UiaPropertyIds.AccessKey:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.AccessKey(node));
            case Win32UiaPropertyIds.FrameworkId:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.FrameworkId);
            case Win32UiaPropertyIds.ItemStatus:
                return Win32AccessibilityValue.FromString(Win32AccessibilityMapping.ItemStatus(node));
            case Win32UiaPropertyIds.ProcessId:
                return Win32AccessibilityValue.FromInt(Win32AccessibilityEnvironment.ProcessId);

            // ---------------------------------------------------------- 状态
            case Win32UiaPropertyIds.IsEnabled:
                return Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsEnabled(node));
            case Win32UiaPropertyIds.IsKeyboardFocusable:
                return Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsKeyboardFocusable(node));
            case Win32UiaPropertyIds.HasKeyboardFocus:
                return Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.HasKeyboardFocus(node));
            case Win32UiaPropertyIds.IsOffscreen:
                return Win32AccessibilityValue.FromBool(IsOffscreen(node, clientBounds));
            case Win32UiaPropertyIds.IsControlElement:
                return Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsControlElement(node));
            case Win32UiaPropertyIds.IsContentElement:
                return Win32AccessibilityValue.FromBool(true);
            case Win32UiaPropertyIds.IsPassword:
                return Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsPassword(node));
            case Win32UiaPropertyIds.IsRequiredForForm:
                return Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsRequiredForForm(node));
            case Win32UiaPropertyIds.Orientation:
                return Win32AccessibilityValue.FromInt(
                    Win32AccessibilityGeometry.OrientationFor(role, node.Bounds));
            case Win32UiaPropertyIds.LiveSetting:
                return Win32AccessibilityValue.FromInt(Win32AccessibilityMapping.LiveSetting(node));
            case Win32UiaPropertyIds.HeadingLevel:
                return Win32AccessibilityValue.FromInt(Win32AccessibilityMapping.Level(node));
            case Win32UiaPropertyIds.IsDialog:
                return Win32AccessibilityValue.FromBool(role is "dialog" or "alertdialog");

            // ---------------------------------------------------------- 集合位置
            case Win32UiaPropertyIds.PositionInSet:
                return Win32AccessibilityValue.FromInt(Win32AccessibilityMapping.PositionInSet(
                    node, entry.SiblingIndex, Win32AccessibilityMapping.IsItemRole(role)));
            case Win32UiaPropertyIds.SizeOfSet:
                return Win32AccessibilityValue.FromInt(Win32AccessibilityMapping.SizeOfSet(
                    node, index.SiblingCount(nodeId), Win32AccessibilityMapping.IsItemRole(role)));
            case Win32UiaPropertyIds.Level:
                // 树项的 Level 是层级（顶层树项为 1）；其余角色使用标题层级。
                return Win32AccessibilityValue.FromInt(
                    string.Equals(role, "treeitem", StringComparison.Ordinal)
                        ? TreeItemLevel(index, entry)
                        : Win32AccessibilityMapping.Level(node));

            // ---------------------------------------------------------- 关系
            case Win32UiaPropertyIds.LabeledBy:
                return entry.LabelledBy.Length > 0
                    ? Win32AccessibilityValue.FromNode(entry.LabelledBy[0])
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.DescribedBy:
                return entry.DescribedBy.Length > 0
                    ? Win32AccessibilityValue.FromNodes(entry.DescribedBy)
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.ControllerFor:
                return entry.Controls.Length > 0
                    ? Win32AccessibilityValue.FromNodes(entry.Controls)
                    : Win32AccessibilityValue.Empty;

            // ---------------------------------------------------------- Value 模式
            case Win32UiaPropertyIds.ValueValue:
                return (patterns & Win32AccessibilityPattern.Value) != 0
                    ? Win32AccessibilityValue.FromString(Win32AccessibilityMapping.TextValue(node))
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.ValueIsReadOnly:
                return (patterns & Win32AccessibilityPattern.Value) != 0
                    ? Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsReadOnly(node))
                    : Win32AccessibilityValue.Empty;

            // ---------------------------------------------------------- RangeValue 模式
            case Win32UiaPropertyIds.RangeValueValue:
            case Win32UiaPropertyIds.RangeValueMinimum:
            case Win32UiaPropertyIds.RangeValueMaximum:
            case Win32UiaPropertyIds.RangeValueLargeChange:
            case Win32UiaPropertyIds.RangeValueSmallChange:
            case Win32UiaPropertyIds.RangeValueIsReadOnly:
                return (patterns & Win32AccessibilityPattern.RangeValue) != 0
                    ? ReadRangeProperty(node, propertyId)
                    : Win32AccessibilityValue.Empty;

            // ---------------------------------------------------------- Toggle / ExpandCollapse
            case Win32UiaPropertyIds.ToggleToggleState:
                return (patterns & Win32AccessibilityPattern.Toggle) != 0
                    ? Win32AccessibilityValue.FromInt(Win32AccessibilityMapping.ToggleState(node))
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.ExpandCollapseExpandCollapseState:
                return (patterns & Win32AccessibilityPattern.ExpandCollapse) != 0
                    ? Win32AccessibilityValue.FromInt(Win32AccessibilityMapping.ExpandCollapseState(node))
                    : Win32AccessibilityValue.Empty;

            // ---------------------------------------------------------- Selection / SelectionItem
            case Win32UiaPropertyIds.SelectionCanSelectMultiple:
                return Win32AccessibilityValue.FromBool(
                    Win32AccessibilityMapping.HasState(node, "multiselectable"));
            case Win32UiaPropertyIds.SelectionIsSelectionRequired:
                return Win32AccessibilityValue.FromBool(false);
            case Win32UiaPropertyIds.SelectionItemIsSelected:
                return (patterns & Win32AccessibilityPattern.SelectionItem) != 0
                    ? Win32AccessibilityValue.FromBool(Win32AccessibilityMapping.IsSelected(node))
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.SelectionItemSelectionContainer:
                return entry.ParentId >= 0
                    ? Win32AccessibilityValue.FromNode(entry.ParentId)
                    : Win32AccessibilityValue.Empty;

            // ---------------------------------------------------------- Grid / GridItem
            case Win32UiaPropertyIds.GridRowCount:
            case Win32UiaPropertyIds.GridColumnCount:
                // 快照不携带表格的行列布局；返回 0 让客户端以子项为准。
                return (patterns & Win32AccessibilityPattern.Grid) != 0
                    ? Win32AccessibilityValue.FromInt(0)
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.GridItemRow:
                return (patterns & Win32AccessibilityPattern.GridItem) != 0
                    ? Win32AccessibilityValue.FromInt(0)
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.GridItemColumn:
                return (patterns & Win32AccessibilityPattern.GridItem) != 0
                    ? Win32AccessibilityValue.FromInt(entry.SiblingIndex)
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.GridItemRowSpan:
            case Win32UiaPropertyIds.GridItemColumnSpan:
                return (patterns & Win32AccessibilityPattern.GridItem) != 0
                    ? Win32AccessibilityValue.FromInt(1)
                    : Win32AccessibilityValue.Empty;
            case Win32UiaPropertyIds.GridItemContainingGrid:
                return (patterns & Win32AccessibilityPattern.GridItem) != 0 && entry.ParentId >= 0
                    ? Win32AccessibilityValue.FromNode(entry.ParentId)
                    : Win32AccessibilityValue.Empty;

            // ---------------------------------------------------------- 模式可用性
            case Win32UiaPropertyIds.IsInvokePatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.Invoke) != 0);
            case Win32UiaPropertyIds.IsTogglePatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.Toggle) != 0);
            case Win32UiaPropertyIds.IsValuePatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.Value) != 0);
            case Win32UiaPropertyIds.IsRangeValuePatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.RangeValue) != 0);
            case Win32UiaPropertyIds.IsExpandCollapsePatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.ExpandCollapse) != 0);
            case Win32UiaPropertyIds.IsSelectionItemPatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.SelectionItem) != 0);
            case Win32UiaPropertyIds.IsSelectionPatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.Selection) != 0);
            case Win32UiaPropertyIds.IsGridPatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.Grid) != 0);
            case Win32UiaPropertyIds.IsGridItemPatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    (patterns & Win32AccessibilityPattern.GridItem) != 0);
            case Win32UiaPropertyIds.IsTextPatternAvailable:
                return Win32AccessibilityValue.FromBool(false);
            case Win32UiaPropertyIds.IsScrollItemPatternAvailable:
                return Win32AccessibilityValue.FromBool(
                    Win32AccessibilityMapping.HasAction(node, AccessibilityActionKind.ScrollIntoView));
            case Win32UiaPropertyIds.IsScrollPatternAvailable:
            case Win32UiaPropertyIds.IsWindowPatternAvailable:
            case Win32UiaPropertyIds.IsTablePatternAvailable:
            case Win32UiaPropertyIds.IsTableItemPatternAvailable:
            case Win32UiaPropertyIds.IsMultipleViewPatternAvailable:
            case Win32UiaPropertyIds.IsDockPatternAvailable:
                return Win32AccessibilityValue.FromBool(false);

            default:
                return Win32AccessibilityValue.Empty;
        }
    }

    /// <summary>树项层级 = 从根到自身的 treeitem 祖先数量（顶层树项为 1）。</summary>
    static int TreeItemLevel(Win32AccessibilityIndex index, Win32AccessibilityEntry entry)
    {
        int level = 0;
        Win32AccessibilityEntry? current = entry;
        int guard = 0;
        while (current is not null && guard++ < 10_000)
        {
            if (string.Equals(current.Node.Role, "treeitem", StringComparison.Ordinal)) level++;
            current = current.ParentId >= 0 && index.TryGet(current.ParentId, out var parent) ? parent : null;
        }
        return level;
    }

    /// <summary>元素是否完全在客户区之外或标记为隐藏。</summary>
    public static bool IsOffscreen(AccessibilityNodeSnapshot node, Rect clientBounds)
    {
        if (Win32AccessibilityMapping.HasState(node, "hidden")) return true;
        return Win32AccessibilityGeometry.IsOffscreen(node.Bounds, clientBounds);
    }

    /// <summary>UIA RangeValue 的默认范围：快照没有 min/max，使用 0..100、步长 1/10。</summary>
    public static bool TryGetRange(AccessibilityNodeSnapshot node, out double minimum, out double maximum,
        out double value, out double smallChange, out double largeChange, out bool isReadOnly)
    {
        minimum = 0;
        maximum = 100;
        smallChange = 1;
        largeChange = 10;
        value = 0;
        isReadOnly = Win32AccessibilityMapping.IsReadOnly(node);

        var text = node.Text?.Value ?? node.Value;
        if (!string.IsNullOrEmpty(text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            value = parsed;
        }
        if (value < minimum) minimum = value;
        if (value > maximum) maximum = value;
        if (maximum <= minimum) maximum = minimum + 1;
        return true;
    }

    static Win32AccessibilityValue ReadRangeProperty(AccessibilityNodeSnapshot node, int propertyId)
    {
        TryGetRange(node, out double minimum, out double maximum, out double value,
            out double smallChange, out double largeChange, out bool isReadOnly);
        switch (propertyId)
        {
            case Win32UiaPropertyIds.RangeValueValue: return Win32AccessibilityValue.FromDouble(value);
            case Win32UiaPropertyIds.RangeValueMinimum: return Win32AccessibilityValue.FromDouble(minimum);
            case Win32UiaPropertyIds.RangeValueMaximum: return Win32AccessibilityValue.FromDouble(maximum);
            case Win32UiaPropertyIds.RangeValueLargeChange: return Win32AccessibilityValue.FromDouble(largeChange);
            case Win32UiaPropertyIds.RangeValueSmallChange: return Win32AccessibilityValue.FromDouble(smallChange);
            case Win32UiaPropertyIds.RangeValueIsReadOnly: return Win32AccessibilityValue.FromBool(isReadOnly);
            default: return Win32AccessibilityValue.Empty;
        }
    }
}

/// <summary>netstandard2.0 没有 Environment.ProcessId。</summary>
internal static class Win32AccessibilityEnvironment
{
    static int s_processId;

    public static int ProcessId
    {
        get
        {
            if (s_processId == 0)
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                s_processId = process.Id;
            }
            return s_processId;
        }
    }
}

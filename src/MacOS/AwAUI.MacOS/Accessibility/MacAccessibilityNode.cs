using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.MacOS.Accessibility;

/// <summary>
/// 一个 NSAccessibility 代理元素的托管状态：由一版 <see cref="AccessibilityNodeSnapshot"/> 填充，
/// 原生对象本身不含状态，所有属性查询都回到这里。
/// </summary>
internal sealed class MacA11yNode
{
    public MacA11yNode(string id, MacAccessibilityBridge owner, IntPtr handle)
    {
        Id = id;
        Owner = owner;
        Handle = handle;
    }

    /// <summary>所属桥（原生回调经它读取窗口几何与投递动作）。</summary>
    public MacAccessibilityBridge Owner { get; }

    /// <summary>原生代理元素（+1 引用；桥释放时 release）。</summary>
    public IntPtr Handle { get; }

    /// <summary>语义节点的稳定标识。</summary>
    public string Id { get; }

    /// <summary>ARIA 角色名（原始快照值）。</summary>
    public string Role { get; set; } = "";

    /// <summary>映射后的 NSAccessibility 角色。</summary>
    public string AxRole { get; set; } = "AXGroup";

    /// <summary>映射后的 NSAccessibility 子角色。</summary>
    public string? AxSubrole { get; set; }

    /// <summary>可访问名称（AXTitle / AXLabel）。</summary>
    public string? Name { get; set; }

    /// <summary>描述（AXHelp）。</summary>
    public string? Description { get; set; }

    /// <summary>快照里的原始值文本（用于变化比较）。</summary>
    public string? ValueText { get; set; }

    /// <summary>格式化后的值。</summary>
    public MacAccessibilityValue FormattedValue { get; set; } = MacAccessibilityValue.None;

    /// <summary>客户区坐标下的边界。</summary>
    public Rect Bounds { get; set; }

    public bool Disabled { get; set; }
    public bool Focused { get; set; }
    public bool Selected { get; set; }
    public bool Expanded { get; set; }
    public bool Busy { get; set; }
    public bool Required { get; set; }
    public bool Mixed { get; set; }

    /// <summary>值可写（可编辑控件且提供 SetValue 动作）。</summary>
    public bool Writable { get; set; }

    /// <summary>是否有数值区间（AXMinValue / AXMaxValue）。</summary>
    public bool HasRange { get; set; }

    public double MinValue { get; set; }
    public double MaxValue { get; set; }

    /// <summary>AXOrientation；仅滑块有。</summary>
    public string? Orientation { get; set; }

    /// <summary>在父节点子列表中的位置。</summary>
    public int IndexInParent { get; set; }

    /// <summary>AXRowCount；-1 表示不适用。</summary>
    public int RowCount { get; set; } = -1;

    /// <summary>AXColumnCount；-1 表示不适用。</summary>
    public int ColumnCount { get; set; } = -1;

    /// <summary>快照里的可用动作。</summary>
    public IReadOnlyList<AccessibilityActionKind> Actions { get; set; } = Array.Empty<AccessibilityActionKind>();

    /// <summary>实时区域优先级。</summary>
    public AccessibilityPoliteness LiveRegion { get; set; }

    /// <summary>快照里的父节点标识。</summary>
    public string? ParentId { get; set; }

    /// <summary>解析后的父节点；根为 null（其 AXParent 是内容视图）。</summary>
    public MacA11yNode? Parent { get; set; }

    /// <summary>解析后的子节点，按树序。</summary>
    public List<MacA11yNode> Children { get; } = new List<MacA11yNode>();

    /// <summary>是否可用某个动作。</summary>
    public bool HasAction(AccessibilityActionKind action)
    {
        var actions = Actions;
        for (int i = 0; i < actions.Count; i++)
            if (actions[i] == action) return true;
        return false;
    }
}

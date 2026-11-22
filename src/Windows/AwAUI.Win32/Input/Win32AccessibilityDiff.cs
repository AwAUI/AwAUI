using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 纯逻辑：比较两版节点索引，产出结构变化、属性变化、焦点与实时区域通知。
// 桥据此只对真正变化的节点调用 UiaRaise*，避免每帧全量重建/全量事件。

internal sealed class Win32AccessibilityStructureChange
{
    public int ParentId { get; set; }
    public int ChangeType { get; set; }
    public int[] ChildIds { get; set; } = Array.Empty<int>();
}

internal sealed class Win32AccessibilityPropertyChange
{
    public int NodeId { get; set; }
    public int PropertyId { get; set; }
    public int OldNodeId { get; set; } = -1;
    public int NewNodeId { get; set; } = -1;
}

internal sealed class Win32AccessibilityNotification
{
    public int NodeId { get; set; }
    public string Text { get; set; } = "";
    public AccessibilityPoliteness Politeness { get; set; }
}

internal sealed class Win32AccessibilityDiff
{
    public List<Win32AccessibilityStructureChange> Structures { get; } = new();
    public List<Win32AccessibilityPropertyChange> Properties { get; } = new();
    public List<Win32AccessibilityNotification> Notifications { get; } = new();
    public bool FocusChanged { get; set; }
    public int FocusedNodeId { get; set; } = -1;

    /// <summary>比较两版索引。首次快照（<paramref name="previous"/> 为 null）不产生事件。</summary>
    public static Win32AccessibilityDiff Compute(Win32AccessibilityIndex? previous, Win32AccessibilityIndex current)
    {
        var diff = new Win32AccessibilityDiff();
        if (current is null) return diff;
        if (previous is null)
        {
            diff.FocusedNodeId = current.FindFocusedId(-1);
            return diff;
        }

        ComputeGroups(previous, current, diff);
        ComputeProperties(previous, current, diff);
        ComputeFocus(previous, current, diff);
        ComputeNotifications(previous, current, diff);
        return diff;
    }

    static void ComputeGroups(Win32AccessibilityIndex previous, Win32AccessibilityIndex current, Win32AccessibilityDiff diff)
    {
        // 新增：父节点在两版中都存在时才报告（整棵新子树的顶层由父节点的那条事件覆盖）。
        var added = new Dictionary<int, List<int>>();
        foreach (var entry in current.Entries.Values)
        {
            if (previous.TryGet(entry.Id, out _)) continue;
            if (entry.ParentId < 0) continue;
            if (!previous.TryGet(entry.ParentId, out _)) continue;
            AddToGroup(added, entry.ParentId, entry.Id);
        }
        foreach (var pair in added)
            diff.Structures.Add(new Win32AccessibilityStructureChange
            {
                ParentId = pair.Key,
                ChangeType = Win32UiaConstants.StructureChangeChildAdded,
                ChildIds = pair.Value.ToArray(),
            });

        // 移除：父节点在当前的版本中仍存在时才报告。
        var removed = new Dictionary<int, List<int>>();
        foreach (var entry in previous.Entries.Values)
        {
            if (current.TryGet(entry.Id, out _)) continue;
            if (entry.ParentId < 0) continue;
            if (!current.TryGet(entry.ParentId, out _)) continue;
            AddToGroup(removed, entry.ParentId, entry.Id);
        }
        foreach (var pair in removed)
            diff.Structures.Add(new Win32AccessibilityStructureChange
            {
                ParentId = pair.Key,
                ChangeType = Win32UiaConstants.StructureChangeChildRemoved,
                ChildIds = pair.Value.ToArray(),
            });

        // 重排：同一个父节点、子节点集合相同但顺序不同。
        foreach (var currentParent in current.Entries.Values)
        {
            if (!previous.TryGet(currentParent.Id, out var previousParent) || previousParent is null) continue;
            var currentChildren = currentParent.Children;
            var previousChildren = previousParent.Children;
            if (currentChildren.Length != previousChildren.Length || currentChildren.Length < 2) continue;
            if (!SameSet(currentChildren, previousChildren)) continue;
            if (SameOrder(currentChildren, previousChildren)) continue;
            diff.Structures.Add(new Win32AccessibilityStructureChange
            {
                ParentId = currentParent.Id,
                ChangeType = Win32UiaConstants.StructureChangeChildrenReordered,
                ChildIds = (int[])currentChildren.Clone(),
            });
        }
    }

    static void ComputeProperties(Win32AccessibilityIndex previous, Win32AccessibilityIndex current,
        Win32AccessibilityDiff diff)
    {
        var seen = new HashSet<long>();
        foreach (var entry in current.Entries.Values)
        {
            if (!previous.TryGet(entry.Id, out var old) || old is null) continue;
            var before = old.Node;
            var after = entry.Node;
            var role = after.Role ?? "";
            var patterns = entry.Patterns;

            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.Name,
                !string.Equals(before.Name ?? "", after.Name ?? "", StringComparison.Ordinal));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.HelpText,
                !string.Equals(before.Description ?? "", after.Description ?? "", StringComparison.Ordinal));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.AutomationId,
                !string.Equals(old.StringId, entry.StringId, StringComparison.Ordinal));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.BoundingRectangle,
                before.Bounds != after.Bounds);

            bool textChanged = !string.Equals(
                Win32AccessibilityMapping.TextValue(before),
                Win32AccessibilityMapping.TextValue(after), StringComparison.Ordinal);
            if ((patterns & Win32AccessibilityPattern.Value) != 0)
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.ValueValue, textChanged);
            if ((patterns & Win32AccessibilityPattern.RangeValue) != 0)
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.RangeValueValue, textChanged);

            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.IsEnabled,
                StateChanged(before, after, "disabled"));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.HasKeyboardFocus,
                StateChanged(before, after, "focused"));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.IsOffscreen,
                StateChanged(before, after, "hidden"));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.IsRequiredForForm,
                StateChanged(before, after, "required"));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.LiveSetting,
                before.LiveRegion != after.LiveRegion);
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.HeadingLevel,
                before.HeadingLevel != after.HeadingLevel);
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.PositionInSet,
                before.PosInSet != after.PosInSet);
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.SizeOfSet,
                before.SetSize != after.SetSize);

            if ((patterns & Win32AccessibilityPattern.Toggle) != 0)
            {
                bool toggleChanged = StateChanged(before, after, "checked")
                    || StateChanged(before, after, "mixed")
                    || StateChanged(before, after, "pressed");
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.ToggleToggleState, toggleChanged);
            }
            if ((patterns & Win32AccessibilityPattern.ExpandCollapse) != 0)
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.ExpandCollapseExpandCollapseState,
                    StateChanged(before, after, "expanded"));
            if ((patterns & Win32AccessibilityPattern.SelectionItem) != 0)
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.SelectionItemIsSelected,
                    StateChanged(before, after, "selected"));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.SelectionCanSelectMultiple,
                StateChanged(before, after, "multiselectable"));

            if ((patterns & Win32AccessibilityPattern.Value) != 0)
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.ValueIsReadOnly,
                    StateChanged(before, after, "readonly"));
            if ((patterns & Win32AccessibilityPattern.RangeValue) != 0)
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.RangeValueIsReadOnly,
                    StateChanged(before, after, "readonly"));

            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.ControllerFor,
                !SameIds(old.Controls, entry.Controls));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.DescribedBy,
                !SameIds(old.DescribedBy, entry.DescribedBy));
            AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.LabeledBy,
                !SameIds(old.LabelledBy, entry.LabelledBy));

            // 选中容器中的选中项变化也通知 Selection 失效。
            if (Win32AccessibilityMapping.IsSelectionContainerRole(role)
                && !SameIds(previous.SelectedChildren(entry.Id), current.SelectedChildren(entry.Id)))
                AddProperty(diff, seen, entry.Id, Win32UiaPropertyIds.SelectionSelection, true);
        }
    }

    static void ComputeFocus(Win32AccessibilityIndex previous, Win32AccessibilityIndex current,
        Win32AccessibilityDiff diff)
    {
        int before = previous.FindFocusedId(-1);
        int after = current.FindFocusedId(-1);
        diff.FocusedNodeId = after;
        diff.FocusChanged = before != after && after >= 0;
    }

    static void ComputeNotifications(Win32AccessibilityIndex previous, Win32AccessibilityIndex current,
        Win32AccessibilityDiff diff)
    {
        foreach (var entry in current.Entries.Values)
        {
            var node = entry.Node;
            if (node.LiveRegion == AccessibilityPoliteness.Off) continue;
            bool changed;
            if (!previous.TryGet(entry.Id, out var old) || old is null)
            {
                changed = true;
            }
            else
            {
                changed = !string.Equals(old.Node.Name ?? "", node.Name ?? "", StringComparison.Ordinal)
                    || !string.Equals(Win32AccessibilityMapping.TextValue(old.Node),
                        Win32AccessibilityMapping.TextValue(node), StringComparison.Ordinal);
            }
            if (!changed) continue;
            var text = node.Name;
            if (string.IsNullOrEmpty(text)) text = Win32AccessibilityMapping.TextValue(node);
            if (string.IsNullOrEmpty(text)) continue;
            diff.Notifications.Add(new Win32AccessibilityNotification
            {
                NodeId = entry.Id,
                Text = text!,
                Politeness = node.LiveRegion,
            });
        }
    }

    static void AddProperty(Win32AccessibilityDiff diff, HashSet<long> seen, int nodeId, int propertyId, bool changed)
    {
        if (!changed) return;
        long key = ((long)nodeId << 32) | (uint)propertyId;
        if (!seen.Add(key)) return;
        diff.Properties.Add(new Win32AccessibilityPropertyChange { NodeId = nodeId, PropertyId = propertyId });
    }

    static void AddToGroup(Dictionary<int, List<int>> groups, int parentId, int childId)
    {
        if (!groups.TryGetValue(parentId, out var list))
        {
            list = new List<int>();
            groups.Add(parentId, list);
        }
        if (!list.Contains(childId)) list.Add(childId);
    }

    static bool StateChanged(AccessibilityNodeSnapshot before, AccessibilityNodeSnapshot after, string state) =>
        Win32AccessibilityMapping.HasState(before, state) != Win32AccessibilityMapping.HasState(after, state);

    static bool SameSet(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < b.Length; j++)
                if (a[i] == b[j]) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    static bool SameOrder(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static bool SameIds(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

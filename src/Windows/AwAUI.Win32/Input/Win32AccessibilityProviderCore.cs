using System;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 纯逻辑：桥与 COM provider 共用的节点表外观。真正的 COM vtable 与 UIA 调用在 Accessibility/，
// 这里只做「按 int ID 读属性 / 导航 / 动作派发」，测试可直接构造假快照驱动它。

internal sealed class Win32AccessibilityProviderCore
{
    readonly Win32AccessibilityIdMap _ids = new();
    volatile Win32AccessibilityIndex? _index;

    public Win32AccessibilityProviderCore()
    {
        ClientBounds = new Rect(0, 0, float.MaxValue, float.MaxValue);
    }

    /// <summary>最近一版索引；未被更新前为 null。</summary>
    public Win32AccessibilityIndex? Index => _index;

    public long Version => _index?.Version ?? 0;
    public int RootId => _index?.RootId ?? -1;
    public int Count => _index?.Count ?? 0;

    /// <summary>客户区 DIP 边界，用于 IsOffscreen 与命中测试。</summary>
    public Rect ClientBounds { get; set; }

    /// <summary>客户区原点的屏幕物理像素与缩放。</summary>
    public Win32AccessibilityClientGeometry Geometry { get; set; }

    /// <summary>动作处理器：返回动作是否已接受执行。</summary>
    public Func<int, AccessibilityActionKind, string?, bool>? ActionHandler { get; set; }

    /// <summary>接收快照；版本未变化时返回 false 并跳过重建。</summary>
    public bool TryUpdate(AccessibilitySnapshot snapshot)
    {
        return TryUpdate(snapshot, out _);
    }

    /// <summary>接收快照并返回上一版索引（首次为 null）。</summary>
    public bool TryUpdate(AccessibilitySnapshot snapshot, out Win32AccessibilityIndex? previous)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var current = _index;
        int nodeCount = snapshot.Nodes?.Count ?? 0;
        if (current is not null && current.Version == snapshot.Version && current.Count == nodeCount)
        {
            previous = current;
            return false;
        }
        previous = current;
        _index = Win32AccessibilityIndex.Build(snapshot, _ids);
        return true;
    }

    public Win32AccessibilityEntry? GetEntry(int nodeId) => _index?.Get(nodeId);

    public bool TryGetEntry(int nodeId, out Win32AccessibilityEntry? entry)
    {
        var index = _index;
        if (index is not null) return index.TryGet(nodeId, out entry);
        entry = null;
        return false;
    }

    public Win32AccessibilityPattern GetPatterns(int nodeId) =>
        GetEntry(nodeId)?.Patterns ?? Win32AccessibilityPattern.None;

    public bool SupportsPattern(int nodeId, Win32AccessibilityPattern pattern) =>
        (GetPatterns(nodeId) & pattern) == pattern;

    public Win32AccessibilityValue GetProperty(int nodeId, int propertyId) =>
        Win32AccessibilityPropertyReader.GetProperty(_index, nodeId, propertyId, Geometry, ClientBounds);

    public Win32AccessibilityRect GetBoundingRectangle(int nodeId)
    {
        var entry = GetEntry(nodeId);
        return entry is null ? default : Win32AccessibilityGeometry.ToScreenRect(entry.Node.Bounds, Geometry);
    }

    public int[] GetRuntimeId(int nodeId) => new[] { Win32UiaConstants.AppendRuntimeId, nodeId };

    public int FindFocusedId(int fallback) => _index?.FindFocusedId(fallback) ?? fallback;

    public int HitTest(double screenX, double screenY, int fallback) =>
        _index?.HitTest(screenX, screenY, Geometry, fallback) ?? fallback;

    public int[] SelectedChildren(int containerId) =>
        _index?.SelectedChildren(containerId) ?? Array.Empty<int>();

    public int SiblingCount(int nodeId) => _index?.SiblingCount(nodeId) ?? 0;

    /// <summary>按 NavigateDirection 返回目标节点；没有目标时返回 false。</summary>
    public bool TryNavigate(int nodeId, int direction, out int target)
    {
        target = -1;
        var index = _index;
        if (index is null || !index.TryGet(nodeId, out var entry) || entry is null) return false;

        switch (direction)
        {
            case Win32UiaConstants.NavigateParent:
                target = entry.ParentId;
                return target >= 0;
            case Win32UiaConstants.NavigateFirstChild:
                if (entry.Children.Length == 0) return false;
                target = entry.Children[0];
                return true;
            case Win32UiaConstants.NavigateLastChild:
                if (entry.Children.Length == 0) return false;
                target = entry.Children[entry.Children.Length - 1];
                return true;
            case Win32UiaConstants.NavigateNextSibling:
            case Win32UiaConstants.NavigatePreviousSibling:
                if (entry.ParentId < 0 || !index.TryGet(entry.ParentId, out var parent) || parent is null)
                    return false;
                int next = entry.SiblingIndex + (direction == Win32UiaConstants.NavigateNextSibling ? 1 : -1);
                if (next < 0 || next >= parent.Children.Length) return false;
                target = parent.Children[next];
                return true;
            default:
                return false;
        }
    }

    /// <summary>动作派发：处理器在 UI 线程执行并返回是否接受。</summary>
    public bool RequestAction(int nodeId, AccessibilityActionKind action, string? value = null)
    {
        if (GetEntry(nodeId) is null) return false;
        var handler = ActionHandler;
        return handler is not null && handler(nodeId, action, value);
    }
}

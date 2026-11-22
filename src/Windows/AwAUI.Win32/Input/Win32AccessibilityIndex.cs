using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 纯逻辑：把平台无关的语义树快照索引成「稳定 int ID ↔ 节点」的节点表，
// 供 COM provider 在任意 UIA 线程上以 O(1) 导航。字符串 ID 到 int ID 的映射跨版本保留，
// 因此节点重建后 UIA 的运行时 ID 不变。

/// <summary>字符串节点 ID → 稳定 int ID。由桥跨快照长期持有。</summary>
internal sealed class Win32AccessibilityIdMap
{
    readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    int _next;

    public Win32AccessibilityIdMap(int firstId = 1)
    {
        _next = firstId;
    }

    public int Count => _ids.Count;

    public int GetOrAdd(string id)
    {
        id ??= "";
        if (_ids.TryGetValue(id, out int existing)) return existing;
        int created = _next++;
        _ids.Add(id, created);
        return created;
    }

    public bool TryGet(string id, out int value)
    {
        if (id is null) { value = 0; return false; }
        return _ids.TryGetValue(id, out value);
    }
}

/// <summary>索引后的语义节点：快照 + 稳定的父子关系与预计算的模式。</summary>
internal sealed class Win32AccessibilityEntry
{
    public int Id { get; internal set; }
    public string StringId { get; internal set; } = "";
    public AccessibilityNodeSnapshot Node { get; internal set; } = null!;
    public int ParentId { get; internal set; } = -1;
    public int[] Children { get; internal set; } = Array.Empty<int>();
    public int[] LabelledBy { get; internal set; } = Array.Empty<int>();
    public int[] Controls { get; internal set; } = Array.Empty<int>();
    public int[] DescribedBy { get; internal set; } = Array.Empty<int>();
    public int SiblingIndex { get; internal set; }
    public int Depth { get; internal set; }
    public int Order { get; internal set; }
    public Win32AccessibilityPattern Patterns { get; internal set; }
    public int ControlTypeId { get; internal set; }

    public bool IsRoot => ParentId < 0;
}

/// <summary>某一版本快照的节点索引。</summary>
internal sealed class Win32AccessibilityIndex
{
    readonly Dictionary<int, Win32AccessibilityEntry> _entries;

    Win32AccessibilityIndex(long version, int rootId, Dictionary<int, Win32AccessibilityEntry> entries, int count)
    {
        Version = version;
        RootId = rootId;
        _entries = entries;
        Count = count;
    }

    public long Version { get; }
    public int RootId { get; }
    public int Count { get; }
    public IReadOnlyDictionary<int, Win32AccessibilityEntry> Entries => _entries;

    public bool TryGet(int id, out Win32AccessibilityEntry? entry)
    {
        if (_entries.TryGetValue(id, out var found)) { entry = found; return true; }
        entry = null;
        return false;
    }

    public Win32AccessibilityEntry? Get(int id) => _entries.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>按快照构建索引；<paramref name="ids"/> 跨版本保留，保证 int ID 稳定。</summary>
    public static Win32AccessibilityIndex Build(AccessibilitySnapshot snapshot, Win32AccessibilityIdMap ids)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (ids is null) throw new ArgumentNullException(nameof(ids));

        var entries = new Dictionary<int, Win32AccessibilityEntry>();
        var ordered = new List<Win32AccessibilityEntry>();
        var nodes = snapshot.Nodes ?? Array.Empty<AccessibilityNodeSnapshot>();

        // 1) 分配稳定 int ID 并创建条目。
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is null) continue;
            int id = ids.GetOrAdd(node.Id ?? "");
            if (entries.ContainsKey(id)) continue;
            var entry = new Win32AccessibilityEntry
            {
                Id = id,
                StringId = node.Id ?? "",
                Node = node,
                Order = i,
                ControlTypeId = Win32AccessibilityMapping.ControlType(node.Role ?? ""),
                Patterns = Win32AccessibilityMapping.Patterns(node),
            };
            entries.Add(id, entry);
            ordered.Add(entry);
        }

        // 2) 解析父节点。
        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var parentString = entry.Node.ParentId;
            if (parentString is not null && ids.TryGet(parentString, out int parentId)
                && parentId != entry.Id && entries.ContainsKey(parentId))
                entry.ParentId = parentId;
        }

        // 3) 按 ChildIds 建立子列表（同时保留快照中的顺序）。
        var childLists = new Dictionary<int, List<int>>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++) childLists[ordered[i].Id] = new List<int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var childIds = entry.Node.ChildIds;
            if (childIds is null) continue;
            for (int c = 0; c < childIds.Count; c++)
            {
                var childString = childIds[c];
                if (childString is null || !ids.TryGet(childString, out int childId)) continue;
                if (childId == entry.Id || !entries.ContainsKey(childId)) continue;
                var list = childLists[entry.Id];
                if (!list.Contains(childId))
                {
                    list.Add(childId);
                    entries[childId].ParentId = entry.Id;
                }
            }
        }

        // 4) ChildIds 缺失但 ParentId 已指向某节点的，补到父节点末尾（保持快照顺序）。
        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (entry.ParentId < 0 || !entries.ContainsKey(entry.ParentId)) continue;
            var list = childLists[entry.ParentId];
            if (!list.Contains(entry.Id)) list.Add(entry.Id);
        }

        // 5) 选择根：显式 RootId 优先，其次第一个没有父节点的节点。
        int rootId = -1;
        if (snapshot.RootId is not null && ids.TryGet(snapshot.RootId, out int declaredRoot)
            && entries.ContainsKey(declaredRoot))
            rootId = declaredRoot;
        if (rootId < 0)
        {
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].ParentId < 0) { rootId = ordered[i].Id; break; }
        }
        if (rootId < 0 && ordered.Count > 0) rootId = ordered[0].Id;

        if (rootId >= 0 && entries.TryGetValue(rootId, out var rootEntry))
        {
            rootEntry.ParentId = -1;
            for (int i = 0; i < ordered.Count; i++)
                childLists[ordered[i].Id].Remove(rootId);
        }

        // 6) 物化子数组、同级序号与深度。
        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var list = childLists[entry.Id];
            entry.Children = list.ToArray();
            for (int c = 0; c < entry.Children.Length; c++)
            {
                if (entries.TryGetValue(entry.Children[c], out var child)) child.SiblingIndex = c;
            }
        }
        ComputeDepths(entries, rootId);

        // 7) 解析关系 ID。
        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            entry.LabelledBy = ResolveIds(entry.Node.LabelledBy, ids, entries);
            entry.Controls = ResolveIds(entry.Node.Controls, ids, entries);
            entry.DescribedBy = ResolveIds(entry.Node.DescribedBy, ids, entries);
        }

        return new Win32AccessibilityIndex(snapshot.Version, rootId, entries, ordered.Count);
    }

    static int[] ResolveIds(IReadOnlyList<string>? source, Win32AccessibilityIdMap ids,
        Dictionary<int, Win32AccessibilityEntry> entries)
    {
        if (source is null || source.Count == 0) return Array.Empty<int>();
        var result = new List<int>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            var value = source[i];
            if (value is null || !ids.TryGet(value, out int id) || !entries.ContainsKey(id)) continue;
            if (!result.Contains(id)) result.Add(id);
        }
        return result.Count == 0 ? Array.Empty<int>() : result.ToArray();
    }

    static void ComputeDepths(Dictionary<int, Win32AccessibilityEntry> entries, int rootId)
    {
        foreach (var entry in entries.Values) entry.Depth = 0;
        if (rootId < 0 || !entries.TryGetValue(rootId, out var root)) return;
        var queue = new Queue<Win32AccessibilityEntry>();
        root.Depth = 0;
        queue.Enqueue(root);
        int guard = 0;
        while (queue.Count > 0 && guard++ < 100_000)
        {
            var current = queue.Dequeue();
            for (int i = 0; i < current.Children.Length; i++)
            {
                if (!entries.TryGetValue(current.Children[i], out var child)) continue;
                int depth = current.Depth + 1;
                if (depth > child.Depth || child.Depth == 0)
                {
                    child.Depth = depth;
                    queue.Enqueue(child);
                }
            }
        }
    }

    /// <summary>查找持有焦点的节点；没有时返回 <paramref name="fallback"/>。</summary>
    public int FindFocusedId(int fallback)
    {
        Win32AccessibilityEntry? best = null;
        foreach (var entry in _entries.Values)
        {
            if (!Win32AccessibilityMapping.HasState(entry.Node, "focused")) continue;
            if (best is null || entry.Depth > best.Depth) best = entry;
        }
        return best?.Id ?? fallback;
    }

    /// <summary>屏幕物理像素命中测试：返回最深的包含该点的节点，未命中返回回退值。</summary>
    public int HitTest(double screenX, double screenY, Win32AccessibilityClientGeometry geometry, int fallback)
    {
        int bestId = fallback;
        int bestDepth = -1;
        int bestOrder = -1;
        foreach (var entry in _entries.Values)
        {
            if (entry.Node.Bounds.IsEmpty) continue;
            var rect = Win32AccessibilityGeometry.ToScreenRect(entry.Node.Bounds, geometry);
            if (!rect.Contains(screenX, screenY)) continue;
            if (entry.Depth > bestDepth || (entry.Depth == bestDepth && entry.Order > bestOrder))
            {
                bestId = entry.Id;
                bestDepth = entry.Depth;
                bestOrder = entry.Order;
            }
        }
        return bestId;
    }

    /// <summary>容器中已选中的直接子节点。</summary>
    public int[] SelectedChildren(int containerId)
    {
        if (!_entries.TryGetValue(containerId, out var container) || container.Children.Length == 0)
            return Array.Empty<int>();
        var result = new List<int>();
        for (int i = 0; i < container.Children.Length; i++)
        {
            if (!_entries.TryGetValue(container.Children[i], out var child)) continue;
            if (Win32AccessibilityMapping.IsSelected(child.Node)) result.Add(child.Id);
        }
        return result.Count == 0 ? Array.Empty<int>() : result.ToArray();
    }

    /// <summary>父节点的子节点数量（根节点返回 0）。</summary>
    public int SiblingCount(int nodeId)
    {
        if (!_entries.TryGetValue(nodeId, out var entry) || entry.ParentId < 0) return 0;
        return _entries.TryGetValue(entry.ParentId, out var parent) ? parent.Children.Length : 0;
    }
}

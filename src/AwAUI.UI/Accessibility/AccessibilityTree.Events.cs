using System;
using System.Collections.Generic;

namespace AwAUI.UI.Accessibility;

public sealed partial class AccessibilityTree
{
    readonly List<AccessibilityEvent> _recentEvents = new();
    Dictionary<string, NodeRecord>? _previousNodes;
    Dictionary<string, string>? _lastAnnouncements;
    long _selectionVersion = long.MinValue;
    int _recentEventLimit = 64;

    /// <summary>
    /// Recent structured events, oldest first, limited to <see cref="RecentEventLimit"/> entries.
    /// Reading this property builds the semantic tree on demand, so pending changes produce their
    /// events here.
    /// </summary>
    public IReadOnlyList<AccessibilityEvent> RecentEvents
    {
        get
        {
            EnsureBuilt();
            return _recentEvents;
        }
    }

    /// <summary>Raised for every structured event while the semantic tree is rebuilt (on the UI thread).</summary>
    public event Action<AccessibilityEvent>? EventRaised;

    /// <summary>Maximum number of retained recent events; older events are discarded. Defaults to 64.</summary>
    public int RecentEventLimit
    {
        get => _recentEventLimit;
        set => _recentEventLimit = value < 0 ? 0 : value;
    }

    /// <summary>Clears the recent event list without changing the version or the nodes.</summary>
    public void ClearEvents() => _recentEvents.Clear();

    /// <summary>上一版语义树的节点记录；用于差异比较。</summary>
    struct NodeRecord
    {
        public string Role;
        public string? Name;
        public string? Value;
        public string? ParentId;
        public bool Focused;
        public bool Live;
        public AccessibilityLiveRegion LiveRegion;
        public int SelectionStart;
        public int SelectionEnd;
        public string? TextValue;
        public string? AnnounceText;
    }

    NodeRecord Record(AccessibilityNode node)
    {
        var record = new NodeRecord
        {
            Role = node.Role,
            Name = node.Name,
            Value = node.Value,
            ParentId = node.Parent?.Id,
            Focused = (node.State & AccessibilityState.Focused) != 0,
            Live = node.LiveRegion != AccessibilityLiveRegion.Off,
            LiveRegion = node.LiveRegion,
            TextValue = node.TextPattern?.Value,
        };
        if (node.LiveRegion != AccessibilityLiveRegion.Off)
        {
            // 实时区域的播报文本是整棵子树的可见文本（区域节点本身通常没有名称）。
            record.AnnounceText = node.TextPattern?.Value ?? Normalize(CollectVisibleText(node.Element));
        }
        if (node.TextPattern is { } pattern)
        {
            record.SelectionStart = pattern.SelectionStart;
            record.SelectionEnd = pattern.SelectionEnd;
        }
        else if (node.Text is { } text)
        {
            record.SelectionStart = text.SelectionStart;
            record.SelectionEnd = text.SelectionEnd;
        }
        return record;
    }

    /// <summary>在本版语义树构建完成后比较新旧节点并产生事件。</summary>
    void PublishDiffEvents()
    {
        var previous = _previousNodes;
        var current = new Dictionary<string, NodeRecord>(_nodes.Count, StringComparer.Ordinal);
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (!current.ContainsKey(node.Id)) current.Add(node.Id, Record(node));
        }

        if (previous is null)
        {
            // 首次构建只建立基线，不产生「全部都是新增」这类噪声。
            _previousNodes = current;
            return;
        }

        PublishEvents(previous, current);
        _previousNodes = current;
    }

    void PublishEvents(Dictionary<string, NodeRecord> previous, Dictionary<string, NodeRecord> current)
    {
        // 1. 焦点变化：整棵树最多一个焦点节点。
        string? oldFocus = FindFocused(previous);
        string? newFocus = FindFocused(current);
        if (!string.Equals(oldFocus, newFocus, StringComparison.Ordinal))
        {
            Emit(new AccessibilityEvent(AccessibilityEventKind.FocusChanged, _version, newFocus)
            {
                PreviousNodeId = oldFocus,
            });
        }

        // 2. 值、文本与选区变化（按树序，每个节点最多一条）。
        foreach (var node in _nodes)
        {
            if (!previous.TryGetValue(node.Id, out var old)) continue;
            var now = current[node.Id];

            bool valueChanged = !string.Equals(old.Value, now.Value, StringComparison.Ordinal)
                || !string.Equals(old.TextValue, now.TextValue, StringComparison.Ordinal)
                || old.SelectionStart != now.SelectionStart || old.SelectionEnd != now.SelectionEnd;
            if (valueChanged)
            {
                Emit(new AccessibilityEvent(AccessibilityEventKind.ValueChanged, _version, node.Id)
                {
                    PreviousValue = old.Value ?? old.TextValue,
                    Value = now.Value ?? now.TextValue,
                });
            }

            if (!string.Equals(old.Name, now.Name, StringComparison.Ordinal))
            {
                Emit(new AccessibilityEvent(AccessibilityEventKind.NameChanged, _version, node.Id)
                {
                    PreviousName = old.Name,
                    Name = now.Name,
                });
            }
        }

        // 3. 结构变化：增删节点、角色或父子关系变化。合并为一条事件。
        var added = new List<string>();
        var removed = new List<string>();
        bool structureChanged = false;
        foreach (var pair in current)
        {
            if (!previous.TryGetValue(pair.Key, out var old) || !string.Equals(old.Role, pair.Value.Role, StringComparison.Ordinal)
                || !string.Equals(old.ParentId, pair.Value.ParentId, StringComparison.Ordinal))
            {
                added.Add(pair.Key);
                structureChanged = true;
            }
        }
        foreach (var pair in previous)
        {
            if (!current.ContainsKey(pair.Key))
            {
                removed.Add(pair.Key);
                structureChanged = true;
            }
        }
        if (structureChanged)
        {
            Emit(new AccessibilityEvent(AccessibilityEventKind.StructureChanged, _version, null)
            {
                AddedNodeIds = added,
                RemovedNodeIds = removed,
            });
        }

        // 4. 实时区域播报：可读文本变化时产生 Announcement，按节点去重。
        foreach (var node in _nodes)
        {
            if (node.LiveRegion == AccessibilityLiveRegion.Off) continue;
            var now = current[node.Id];
            string? text = now.AnnounceText;
            if (string.IsNullOrEmpty(text)) continue;

            previous.TryGetValue(node.Id, out var old);
            if (!old.Equals(default(NodeRecord)) && string.Equals(old.AnnounceText, text, StringComparison.Ordinal))
                continue;

            _lastAnnouncements ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (_lastAnnouncements.TryGetValue(node.Id, out var last) && string.Equals(last, text, StringComparison.Ordinal))
                continue;

            _lastAnnouncements[node.Id] = text!;
            Emit(new AccessibilityEvent(AccessibilityEventKind.Announcement, _version, node.Id)
            {
                Politeness = node.LiveRegion,
                Text = text,
                Value = text,
            });
        }
    }

    static string? FindFocused(Dictionary<string, NodeRecord> nodes)
    {
        foreach (var pair in nodes)
            if (pair.Value.Focused) return pair.Key;
        return null;
    }

    void Emit(AccessibilityEvent evt)
    {
        _recentEvents.Add(evt);
        int limit = _recentEventLimit;
        if (limit >= 0 && _recentEvents.Count > limit)
            _recentEvents.RemoveRange(0, _recentEvents.Count - limit);
        EventRaised?.Invoke(evt);
    }
}

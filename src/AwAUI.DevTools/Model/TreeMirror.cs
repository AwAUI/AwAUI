using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Json;

namespace AwAUI.DevTools;

/// <summary>面板镜像中的一个节点（design/20 <c>Node</c> 的前端副本）。</summary>
internal sealed class DomNode
{
    public DomNode(long id) => Id = id;

    public readonly long Id;
    public DomNode? Parent;
    public bool IsText;
    public string? Tag;
    public string? ElementId;
    public string[]? Classes;
    public List<KeyValuePair<string, string>>? Attributes;
    public string? Text;
    public bool TextTruncated;
    public string? ComponentName;
    public string[]? PseudoStates;
    public int ChildCount;

    /// <summary>已收到的子节点（服务端镜像中「已展开」）；null 表示还没有请求。</summary>
    public List<DomNode>? Children;

    /// <summary>面板里是否展开（只是界面状态，与是否已加载子节点无关）。</summary>
    public bool Expanded;

    /// <summary>深度（根为 0），由 <see cref="TreeMirror"/> 维护。</summary>
    public int Depth;

    public bool HasChildren => Children is { Count: > 0 } || (Children is null && ChildCount > 0);

    /// <summary>
    /// 是否把唯一的短文本子节点画在同一行（<c>&lt;text class="count"&gt;Count: 0&lt;/text&gt;</c>，同浏览器）：此时该行没有展开箭头。
    /// </summary>
    public DomNode? InlineText =>
        !IsText && Children is { Count: 1 } c && c[0].IsText && (c[0].Text?.Length ?? 0) <= TreeMirror.MaxInlineTextLength ? c[0] : null;

    public override string ToString() => IsText ? "\"" + Text + "\"" : "<" + Tag + (ElementId is null ? "" : "#" + ElementId) + ">";
}

/// <summary>树视图中的一行。</summary>
internal readonly struct TreeRow
{
    public TreeRow(DomNode node, int depth)
    {
        Node = node;
        Depth = depth;
    }

    public readonly DomNode Node;
    public readonly int Depth;
}

/// <summary>
/// 前端的元素树镜像：由 <c>Tree.getDocument</c> / <c>requestChildren</c> / <c>pushNodePath</c> 建立，按 Tree 域事件增量维护（design/20「镜像」）。
/// 对端不可信：重复 / 成环的 id、指向未知节点的事件被忽略；节点总数受 <see cref="MaxNodes"/> 限制；所有遍历都是迭代的。
/// </summary>
internal sealed class TreeMirror
{
    /// <summary>同一行显示的文本子节点的最大长度。</summary>
    public const int MaxInlineTextLength = 80;

    readonly Dictionary<long, DomNode> _nodes = new();
    readonly List<TreeRow> _rows = new();
    int _rowsVersion = -1;

    public DomNode? Root { get; private set; }

    public int Count => _nodes.Count;

    /// <summary>镜像中节点数的上限；超出的节点被丢弃（<see cref="Truncated"/>）。</summary>
    public int MaxNodes { get; set; } = 1_000_000;

    /// <summary>是否因为 <see cref="MaxNodes"/> 丢弃过节点。</summary>
    public bool Truncated { get; private set; }

    /// <summary>结构或展开状态的版本（行列表随之重建）。</summary>
    public int StructureVersion { get; private set; }

    /// <summary>任何变化（含属性、文本、伪状态）的版本。</summary>
    public int Version { get; private set; }

    public bool TryGet(long id, out DomNode node) => _nodes.TryGetValue(id, out node!);

    public DomNode? Get(long id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public void Clear()
    {
        _nodes.Clear();
        Root = null;
        Truncated = false;
        Changed(structure: true);
    }

    void Changed(bool structure)
    {
        Version++;
        if (structure) StructureVersion++;
    }

    /// <summary>界面上展开 / 折叠了节点（行列表需要重建）。</summary>
    public void ExpansionChanged() => Changed(structure: true);

    // ------------------------------------------------------------ 建立

    /// <summary><c>Tree.getDocument</c> 的 <c>root</c>：整棵镜像替换。</summary>
    public bool LoadDocument(JsonValue? root)
    {
        _nodes.Clear();
        Root = null;
        Truncated = false;
        if (root is JsonObject o && Create(o, null) is { } r)
        {
            Root = r;
            AddChildren(r, o);
        }
        Changed(structure: true);
        return Root is not null;
    }

    /// <summary>
    /// 用 <paramref name="children"/> 替换 <paramref name="parentId"/> 的子节点（<c>requestChildren</c> 的结果、<c>pushNodePath</c> 的分组）。
    /// 父节点不在镜像中时返回 false。
    /// </summary>
    public bool SetChildren(long parentId, JsonArray? children)
    {
        if (!_nodes.TryGetValue(parentId, out var parent) || parent.IsText) return false;
        if (parent.Children is { } old)
            foreach (var c in old) Drop(c);
        parent.Children = new List<DomNode>(children?.Count ?? 0);
        if (children is not null)
            foreach (var item in children)
            {
                if (item is not JsonObject co || Create(co, parent) is not { } child) continue;
                parent.Children.Add(child);
                AddChildren(child, co);
            }
        parent.ChildCount = parent.Children.Count;
        Changed(structure: true);
        return true;
    }

    /// <summary>从 JSON 创建一个节点（不含子节点）并登记；id 无效、重复或超出上限时返回 null。</summary>
    DomNode? Create(JsonObject o, DomNode? parent)
    {
        if (o["id"] is not JsonNumber n || !n.TryGetInt64(out long id) || id <= 0) return null;
        if (_nodes.ContainsKey(id)) return null;   // 重复或成环：忽略该子树
        if (_nodes.Count >= MaxNodes)
        {
            Truncated = true;
            return null;
        }
        var node = new DomNode(id) { Parent = parent, Depth = parent is null ? 0 : parent.Depth + 1 };
        Fill(node, o);
        _nodes.Add(id, node);
        return node;
    }

    static void Fill(DomNode node, JsonObject o)
    {
        node.IsText = o.GetStringOrDefault("kind") == "text";
        node.Tag = o.GetStringOrDefault("tag");
        node.ElementId = o.GetStringOrDefault("elementId");
        node.Classes = StringArray(o["classes"]);
        node.Text = o.GetStringOrDefault("text");
        node.TextTruncated = o["textTruncated"] is JsonBoolean { } b && b.GetBoolean();
        node.ComponentName = o.GetStringOrDefault("componentName");
        node.PseudoStates = StringArray(o["pseudoStates"]);
        node.ChildCount = o["childCount"] is JsonNumber cn && cn.TryGetInt64(out long cc) ? (int)Math.Max(0, Math.Min(int.MaxValue, cc)) : 0;
        node.Attributes = null;
        if (o["attributes"] is JsonObject attrs && attrs.Count > 0)
        {
            node.Attributes = new List<KeyValuePair<string, string>>(attrs.Count);
            foreach (var kv in attrs)
                if (kv.Value is JsonString s) node.Attributes.Add(new KeyValuePair<string, string>(kv.Key, s.Value));
        }
    }

    /// <summary>迭代地加入 <paramref name="json"/> 中嵌套的 <c>children</c>（深树不会耗尽栈）。</summary>
    void AddChildren(DomNode node, JsonObject json)
    {
        var stack = new Stack<(DomNode Node, JsonObject Json)>();
        stack.Push((node, json));
        while (stack.Count > 0)
        {
            var (n, j) = stack.Pop();
            if (n.IsText || j["children"] is not JsonArray arr) continue;
            n.Children = new List<DomNode>(arr.Count);
            foreach (var item in arr)
            {
                if (item is not JsonObject co || Create(co, n) is not { } child) continue;
                n.Children.Add(child);
                stack.Push((child, co));
            }
            n.ChildCount = n.Children.Count;
        }
    }

    /// <summary>迭代地从索引中移除节点及其子树。</summary>
    void Drop(DomNode node)
    {
        var stack = new Stack<DomNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            _nodes.Remove(n.Id);
            n.Parent = null;
            if (n.Children is { } c)
                foreach (var ch in c) stack.Push(ch);
        }
    }

    static string[]? StringArray(JsonValue? v)
    {
        if (v is not JsonArray a || a.Count == 0) return null;
        var list = new List<string>(a.Count);
        foreach (var item in a)
            if (item is JsonString s) list.Add(s.Value);
        return list.Count == 0 ? null : list.ToArray();
    }

    // ------------------------------------------------------------ 事件

    /// <summary>应用一个 Tree 域事件；返回 true 表示镜像有变化。未知事件、未知节点被忽略。</summary>
    public bool Apply(string eventName, JsonObject p)
    {
        switch (eventName)
        {
            case "childrenChanged": return ChildrenChanged(p);
            case "childMoved": return ChildMoved(p);
            case "attributesChanged": return AttributesChanged(p);
            case "textChanged":
            {
                if (!TryNode(p, "nodeId", out var n)) return false;
                n.Text = p.GetStringOrDefault("text") ?? "";
                n.TextTruncated = p["textTruncated"] is JsonBoolean b && b.GetBoolean();
                Changed(structure: false);
                return true;
            }
            case "pseudoStateChanged":
            {
                if (!TryNode(p, "nodeId", out var n)) return false;
                n.PseudoStates = StringArray(p["states"]);
                Changed(structure: false);
                return true;
            }
            case "childCountUpdated":
            {
                if (!TryNode(p, "nodeId", out var n) || n.Children is not null) return false;
                n.ChildCount = p["childCount"] is JsonNumber cn && cn.TryGetInt64(out long c) ? (int)Math.Max(0, Math.Min(int.MaxValue, c)) : 0;
                Changed(structure: true);
                return true;
            }
            default:
                return false;
        }
    }

    bool TryNode(JsonObject p, string name, out DomNode node)
    {
        node = null!;
        return p[name] is JsonNumber n && n.TryGetInt64(out long id) && _nodes.TryGetValue(id, out node!);
    }

    bool ChildrenChanged(JsonObject p)
    {
        if (!TryNode(p, "parentId", out var parent) || parent.Children is not { } children) return false;
        if (p["removed"] is JsonArray removed)
            foreach (var r in removed)
            {
                if (r is not JsonNumber rn || !rn.TryGetInt64(out long rid) || !_nodes.TryGetValue(rid, out var child) || child.Parent != parent) continue;
                children.Remove(child);
                Drop(child);
            }
        if (p["inserted"] is JsonArray inserted)
            foreach (var item in inserted)
            {
                if (item is not JsonObject entry || entry["node"] is not JsonObject nj || Create(nj, parent) is not { } child) continue;
                AddChildren(child, nj);
                children.Insert(IndexBefore(children, entry["beforeId"]), child);
            }
        parent.ChildCount = children.Count;
        Changed(structure: true);
        return true;
    }

    bool ChildMoved(JsonObject p)
    {
        if (!TryNode(p, "parentId", out var parent) || parent.Children is not { } children) return false;
        if (!TryNode(p, "nodeId", out var child) || child.Parent != parent) return false;
        children.Remove(child);
        children.Insert(IndexBefore(children, p["beforeId"]), child);
        Changed(structure: true);
        return true;
    }

    static int IndexBefore(List<DomNode> children, JsonValue? before)
    {
        if (before is JsonNumber bn && bn.TryGetInt64(out long beforeId))
            for (int i = 0; i < children.Count; i++)
                if (children[i].Id == beforeId) return i;
        return children.Count;
    }

    bool AttributesChanged(JsonObject p)
    {
        if (!TryNode(p, "nodeId", out var n)) return false;
        if (p["attributes"] is JsonObject attrs)
            foreach (var kv in attrs)
            {
                var list = n.Attributes ??= new List<KeyValuePair<string, string>>();
                int i = list.FindIndex(a => a.Key == kv.Key);
                if (kv.Value is JsonString s)
                {
                    var pair = new KeyValuePair<string, string>(kv.Key, s.Value);
                    if (i >= 0) list[i] = pair;
                    else list.Add(pair);
                }
                else if (i >= 0) list.RemoveAt(i);
            }
        if (p.ContainsKey("classes")) n.Classes = StringArray(p["classes"]);
        if (p.ContainsKey("elementId")) n.ElementId = p.GetStringOrDefault("elementId");
        Changed(structure: false);
        return true;
    }

    // ------------------------------------------------------------ 行

    /// <summary>展开的节点按树序摊平成行（迭代，深树不会耗尽栈）。结构或展开状态变化后重建，否则复用。</summary>
    public IReadOnlyList<TreeRow> Rows
    {
        get
        {
            if (_rowsVersion == StructureVersion) return _rows;
            FlattenCount++;
            _rows.Clear();
            _rowIndex.Clear();
            if (Root is { } root)
            {
                var stack = new Stack<(DomNode, int)>();
                stack.Push((root, 0));
                while (stack.Count > 0)
                {
                    var (n, depth) = stack.Pop();
                    _rowIndex[n.Id] = _rows.Count;
                    _rows.Add(new TreeRow(n, depth));
                    if (!n.Expanded || n.Children is not { } c || n.InlineText is not null) continue;
                    for (int i = c.Count - 1; i >= 0; i--) stack.Push((c[i], depth + 1));
                }
            }
            _rowsVersion = StructureVersion;
            return _rows;
        }
    }

    readonly Dictionary<long, int> _rowIndex = new();

    /// <summary>行列表被重新摊平的次数（测试用：结构事件的处理不应每条事件都摊平一次）。</summary>
    internal int FlattenCount { get; private set; }

    /// <summary>节点所在的行；不可见（祖先折叠）时为 -1。</summary>
    public int RowOf(long id)
    {
        _ = Rows;
        return _rowIndex.TryGetValue(id, out int i) ? i : -1;
    }

    /// <summary>展开节点的所有祖先，使它可见。</summary>
    public void Reveal(DomNode node)
    {
        bool changed = false;
        for (var a = node.Parent; a is not null; a = a.Parent)
            if (!a.Expanded)
            {
                a.Expanded = true;
                changed = true;
            }
        if (changed) ExpansionChanged();
    }

    /// <summary>
    /// 初次加载后的默认展开：广度优先展开已加载的元素，直到可见行数达到 <paramref name="maxRows"/>（浏览器只展开到 body，
    /// 这里的树通常很小，尽量多展开一些）。
    /// </summary>
    public void AutoExpand(int maxRows)
    {
        if (Root is null) return;
        int rows = 1;
        var queue = new Queue<DomNode>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Children is not { Count: > 0 } c || n.InlineText is not null) continue;
            if (rows + c.Count > maxRows) break;
            n.Expanded = true;
            rows += c.Count;
            foreach (var ch in c) queue.Enqueue(ch);
        }
        ExpansionChanged();
    }
}

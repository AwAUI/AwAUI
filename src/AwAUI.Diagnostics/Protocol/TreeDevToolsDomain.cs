using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// Tree 域（design/20 <c>Tree</c>）：元素树的按需浏览、查询与变更推送。数据源是各目标的 <see cref="DevToolsTarget.Tree"/>。
/// </summary>
/// <remarks>
/// <para><b>方法</b>（除 enable / disable 外都在目标的 UI 线程上执行）：</para>
/// <list type="bullet">
/// <item><c>getDocument({ depth? = 2 }) → { root: Node }</c>：depth 为根以下展开的层数，-1 为整棵树（受下文的上限约束）。需要 enable。</item>
/// <item><c>requestChildren({ nodeId, depth? = 1 }) → { children: Node[] }</c>：节点必须已发送给本会话（见「镜像」）。需要 enable。</item>
/// <item><c>pushNodePath({ nodeId }) → { path: NodeId[], children: { parentId, children: Node[] }[] }</c>（design/20 之外，CDP 式）：
/// 把从根到节点的路径上尚未展开的祖先逐个展开，<c>path</c> 从根开始、以该节点结束，<c>children</c> 按从根往下的顺序给出新展开的祖先的子节点。
/// 用于在树中显示由拾取或查询得到的节点。需要 enable 且已 getDocument。</item>
/// <item><c>describeNode({ nodeId }) → { node }</c>（design/20 之外）：单个节点的快照，不改变镜像。</item>
/// <item><c>querySelector({ nodeId, selector }) → { nodeId? }</c>、<c>querySelectorAll({ nodeId, selector }) → { nodeIds, truncated? }</c>：
/// 在节点的后代中匹配（选择器匹配由 UI 层完成）；无法解析的选择器返回 -32005。结果数受 <see cref="DevToolsAgentOptions.MaxTreeNodesPerResponse"/> 限制。</item>
/// <item><c>getNodeAt({ x, y, ignorePointerEvents? }) → { nodeId? }</c>、<c>setAttribute({ nodeId, name, value })</c>、
/// <c>removeAttribute({ nodeId, name })</c>、<c>setClasses({ nodeId, classes })</c>：接缝可选支持，否则 -32004。</item>
/// <item><c>getOuterText({ nodeId, includeStyles?: "none" | "key" | "all" }) → { text }</c>：子树的缩进标记文本（每行一个节点，不写结束标签），
/// 节点数与总长度有上限，超出时以 <c>… (truncated)</c> 结尾。</item>
/// </list>
/// <para><b>镜像</b>：协议层为每个会话、每个目标记住已发送给它的节点（CDP 式）。一个节点「已发送」指它是 getDocument 的根，或者作为某个
/// 已展开节点的子节点被发送过；节点的 <c>children</c> 一旦发送，该节点即「已展开」，它的所有直接子节点都已发送。
/// 结构事件只描述已发送的节点：已展开父节点下的插入 / 移除 / 移动推送 <c>childrenChanged</c> / <c>childMoved</c>；
/// 已发送但未展开的父节点只推送（按帧合并的）<c>childCountUpdated</c>。被移除的子树从镜像中丢弃；被移动的节点保留其子树。
/// 一个已发送的节点被报告插入到另一个已展开的父节点下（跨父节点移动先报告了插入）时，先向该会话推送旧位置的移除再推送插入。
/// describeNode / querySelector 等返回的 id 不进入镜像，但可以直接用于本域与其他域的方法。</para>
/// <para><b>事件</b>（都带 <c>target</c>）：</para>
/// <list type="bullet">
/// <item><c>childrenChanged { parentId, removed: NodeId[], inserted: { node: Node, beforeId?: NodeId }[] }</c>：立即推送，每次通知一条
/// （<c>beforeId</c> 省略表示末尾）。</item>
/// <item><c>childMoved { parentId, nodeId, beforeId? }</c>（design/20 之外）：同一父节点内的移动，前端保留节点的子树。立即推送。</item>
/// <item><c>attributesChanged { nodeId, attributes: Record&lt;string, string | null&gt;, classes?, elementId? }</c>：attributes 只含本帧变化的属性
/// （null 表示被移除）；class 变化时带 <c>classes</c>（完整列表），id 变化时带 <c>elementId</c>（null 表示被移除）。按帧合并。</item>
/// <item><c>textChanged { nodeId, text, textTruncated? }</c>、<c>pseudoStateChanged { nodeId, states }</c>：按帧合并。</item>
/// <item><c>childCountUpdated { nodeId, childCount }</c>（design/20 之外）：未展开节点的子节点数变化，按帧合并。</item>
/// <item><c>documentUpdated {}</c>（design/20 之外）：整棵树被替换，镜像清空，前端需重新 getDocument。立即推送。</item>
/// </list>
/// <para><b>上限</b>：一次响应最多展开 <see cref="DevToolsAgentOptions.MaxTreeNodesPerResponse"/> 个节点、嵌套
/// <see cref="DevToolsAgentOptions.MaxTreeDepthPerResponse"/> 层（广度优先，预算不够的节点只带 childCount）；被请求节点的直接子节点总是完整返回，
/// 以保证「已展开」的含义。遍历都是迭代的，深树不会栈溢出。</para>
/// <para>按帧合并的事件在 <see cref="DevToolsAgent.FlushFrame"/> 时发出；只为至少一个会话已发送的节点排队，节点移除时出队，
/// 因此即使宿主长时间不调用 FlushFrame，排队的数据也不超过已发送节点的规模。停用（或断开）时丢弃该会话的镜像。</para>
/// </remarks>
public sealed class TreeDevToolsDomain : DevToolsDomain
{
    internal enum ChangeKind { Attribute, Text, PseudoStates, ChildCount }

    sealed class MirrorNode
    {
        public MirrorNode(long parentId) => ParentId = parentId;
        public readonly long ParentId;
        /// <summary>已发送的子节点；null 表示未展开。</summary>
        public HashSet<long>? Children;
    }

    /// <summary>一个会话在一个目标上已发送的节点。</summary>
    sealed class Mirror
    {
        public readonly Dictionary<long, MirrorNode> Nodes = new();

        public bool IsTracked(long id) => Nodes.ContainsKey(id);
        public bool IsExpanded(long id) => Nodes.TryGetValue(id, out var n) && n.Children != null;

        public void Track(long parentId, long id)
        {
            if (Nodes.ContainsKey(id)) DropSubtree(id, null);
            Nodes[id] = new MirrorNode(parentId);
            if (Nodes.TryGetValue(parentId, out var p)) (p.Children ??= new HashSet<long>()).Add(id);
        }

        /// <summary>用新的子节点列表替换 <paramref name="id"/> 的子节点（旧的子树全部丢弃）。</summary>
        public void Expand(long id, List<long> children)
        {
            if (!Nodes.TryGetValue(id, out var n)) return;
            if (n.Children is { Count: > 0 } old)
            {
                var copy = new long[old.Count];
                old.CopyTo(copy);
                foreach (var c in copy) DropSubtree(c, null);
            }
            n.Children = new HashSet<long>();
            foreach (var c in children)
                if (c != id) Track(id, c);
        }

        /// <summary>丢弃节点及其已发送的后代（迭代），被丢弃的 id 追加到 <paramref name="dropped"/>。</summary>
        public void DropSubtree(long id, List<long>? dropped)
        {
            if (!Nodes.TryGetValue(id, out var n)) return;
            if (Nodes.TryGetValue(n.ParentId, out var p)) p.Children?.Remove(id);
            var stack = new Stack<long>();
            stack.Push(id);
            while (stack.Count > 0)
            {
                long x = stack.Pop();
                if (!Nodes.TryGetValue(x, out var node)) continue;
                Nodes.Remove(x);
                dropped?.Add(x);
                if (node.Children != null)
                    foreach (var c in node.Children) stack.Push(c);
            }
        }
    }

    sealed class PendingNode
    {
        public HashSet<string>? Attributes;
        public bool Text, PseudoStates, ChildCount;
    }

    /// <summary>一个目标上本帧待合并发出的变化（按首次入队的顺序）。</summary>
    sealed class Pending
    {
        public readonly Dictionary<long, PendingNode> Nodes = new();
        public readonly List<long> Order = new();
    }

    readonly struct Expansion
    {
        public Expansion(long parentId, List<long> children)
        {
            ParentId = parentId; Children = children;
        }
        public readonly long ParentId;
        public readonly List<long> Children;
    }

    readonly object _gate = new();
    readonly Dictionary<DevToolsSession, Dictionary<DevToolsTarget, Mirror>> _mirrors = new();
    readonly Dictionary<DevToolsTarget, Pending> _pending = new();

    internal TreeDevToolsDomain() : base("Tree") { }

    DevToolsAgentOptions Options => Agent!.Options;

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        InspectionProtocol.Add(methods, "getDocument", GetDocument, requiresEnable: true);
        InspectionProtocol.Add(methods, "requestChildren", RequestChildren, requiresEnable: true);
        InspectionProtocol.Add(methods, "pushNodePath", PushNodePath, requiresEnable: true);
        InspectionProtocol.Add(methods, "describeNode", DescribeNode);
        InspectionProtocol.Add(methods, "querySelector", QuerySelector);
        InspectionProtocol.Add(methods, "querySelectorAll", QuerySelectorAll);
        InspectionProtocol.Add(methods, "getNodeAt", GetNodeAt);
        InspectionProtocol.Add(methods, "setAttribute", SetAttribute);
        InspectionProtocol.Add(methods, "removeAttribute", RemoveAttribute);
        InspectionProtocol.Add(methods, "setClasses", SetClasses);
        InspectionProtocol.Add(methods, "getOuterText", GetOuterText);
    }

    // ================================================================ 方法

    static int GetDepth(DevToolsCallContext ctx, int defaultValue, bool allowZero)
    {
        long d = ctx.GetOptionalInt64("depth") ?? defaultValue;
        if (d == -1 || (d >= (allowZero ? 0 : 1) && d <= int.MaxValue)) return (int)d;
        throw DevToolsProtocolException.InvalidParams(
            allowZero ? "Parameter 'depth' must be -1 or a non-negative integer." : "Parameter 'depth' must be -1 or a positive integer.",
            new JsonObject { ["parameter"] = "depth" });
    }

    JsonValue? GetDocument(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        var target = ctx.Target!;
        int depth = GetDepth(ctx, 2, allowZero: true);
        long rootId = tree.RootId;
        var info = rootId == 0 ? null : tree.DescribeNode(rootId);
        if (info is null)
            throw new DevToolsProtocolException(DevToolsErrorCodes.NodeNotFound, "The tree has no root.", new JsonObject { ["nodeId"] = rootId });
        var root = InspectionProtocol.NodeJson(info, Options.MaxNodeTextLength);
        var expansions = new List<Expansion>();
        if (depth != 0)
        {
            var children = ExpandFrom(tree, rootId, depth, expansions);
            if (children is null) throw InspectionProtocol.NodeNotFound(rootId);
            root["children"] = children;
        }

        lock (_gate)
        {
            // 处理期间会话停用了 Tree 或目标被移除：不再建立镜像（否则没有人会清理它）
            if (ctx.Session.IsDomainEnabled(this) && target.Agent == Agent)
            {
                var mirror = new Mirror();
                mirror.Nodes[rootId] = new MirrorNode(0);
                Apply(mirror, expansions);
                if (!_mirrors.TryGetValue(ctx.Session, out var map)) _mirrors[ctx.Session] = map = new Dictionary<DevToolsTarget, Mirror>();
                map[target] = mirror;
            }
        }
        return new JsonObject { ["root"] = root };
    }

    JsonValue? RequestChildren(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        int depth = GetDepth(ctx, 1, allowZero: false);
        if (!tree.ContainsNode(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId);
        lock (_gate)
            if (GetMirror(ctx.Session, ctx.Target!) is not { } m || !m.IsTracked(nodeId))
                throw NotSent(nodeId);

        var expansions = new List<Expansion>();
        var children = ExpandFrom(tree, nodeId, depth, expansions) ?? throw InspectionProtocol.NodeNotFound(nodeId);
        lock (_gate)
            if (GetMirror(ctx.Session, ctx.Target!) is { } m) Apply(m, expansions);
        return new JsonObject { ["children"] = children };
    }

    static DevToolsProtocolException NotSent(long nodeId) => DevToolsProtocolException.InvalidParams(
        $"Node {nodeId} has not been sent to this session; use Tree.getDocument / Tree.pushNodePath first.",
        new JsonObject { ["nodeId"] = nodeId });

    JsonValue? PushNodePath(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        var target = ctx.Target!;
        long nodeId = ctx.GetInt64("nodeId");
        var info = tree.DescribeNode(nodeId) ?? throw InspectionProtocol.NodeNotFound(nodeId);

        // 自下而上收集祖先（迭代，带环检测）
        var path = new List<long> { nodeId };
        var seen = new HashSet<long> { nodeId };
        long cur = info.ParentId;
        while (cur != 0)
        {
            if (!seen.Add(cur)) throw new InvalidOperationException("The inspectable tree reported a parent cycle.");
            path.Add(cur);
            cur = tree.DescribeNode(cur)?.ParentId ?? throw InspectionProtocol.NodeNotFound(nodeId);
        }
        path.Reverse();
        if (path[0] != tree.RootId)
            throw DevToolsProtocolException.InvalidParams($"Node {nodeId} is not attached to the document.", new JsonObject { ["nodeId"] = nodeId });

        int first;
        lock (_gate)
        {
            var m = GetMirror(ctx.Session, target);
            if (m is null || !m.IsTracked(path[0]))
                throw DevToolsProtocolException.InvalidParams("The document has not been requested; call Tree.getDocument first.");
            first = path.Count - 1;
            for (int i = 0; i < path.Count - 1; i++)
                if (!m.IsExpanded(path[i]) || !m.IsTracked(path[i + 1])) { first = i; break; }
        }

        var expansions = new List<Expansion>();
        var groups = new JsonArray();
        for (int i = first; i < path.Count - 1; i++)
        {
            var children = ExpandFrom(tree, path[i], 1, expansions) ?? throw InspectionProtocol.NodeNotFound(path[i]);
            groups.Add(new JsonObject { ["parentId"] = path[i], ["children"] = children });
        }
        lock (_gate)
            if (GetMirror(ctx.Session, target) is { } m) Apply(m, expansions);

        var ids = new JsonArray();
        foreach (var id in path) ids.Add(id);
        return new JsonObject { ["path"] = ids, ["children"] = groups };
    }

    JsonValue? DescribeNode(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        var info = tree.DescribeNode(nodeId) ?? throw InspectionProtocol.NodeNotFound(nodeId);
        return new JsonObject { ["node"] = InspectionProtocol.NodeJson(info, Options.MaxNodeTextLength) };
    }

    JsonValue? QuerySelector(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        string selector = ctx.GetString("selector");
        if (!tree.ContainsNode(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId);
        long found = tree.QuerySelector(nodeId, selector);
        var o = new JsonObject();
        if (found != 0)
        {
            InspectionProtocol.ValidateId(found);
            o["nodeId"] = found;
        }
        return o;
    }

    JsonValue? QuerySelectorAll(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        string selector = ctx.GetString("selector");
        if (!tree.ContainsNode(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId);
        var results = new List<long>();
        tree.QuerySelectorAll(nodeId, selector, results);
        int max = Math.Max(0, Options.MaxTreeNodesPerResponse);
        var ids = new JsonArray();
        for (int i = 0; i < results.Count && i < max; i++)
        {
            InspectionProtocol.ValidateId(results[i]);
            ids.Add(results[i]);
        }
        var o = new JsonObject { ["nodeIds"] = ids };
        if (results.Count > max) o["truncated"] = true;
        return o;
    }

    JsonValue? GetNodeAt(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        double x = ctx.GetFiniteDouble("x"), y = ctx.GetFiniteDouble("y");
        bool ignore = ctx.GetBoolean("ignorePointerEvents", false);
        long found = tree.GetNodeAt(new Point((float)x, (float)y), ignore);
        var o = new JsonObject();
        if (found != 0)
        {
            InspectionProtocol.ValidateId(found);
            o["nodeId"] = found;
        }
        return o;
    }

    static string GetAttributeName(DevToolsCallContext ctx)
    {
        string name = ctx.GetString("name");
        if (name.Length == 0)
            throw DevToolsProtocolException.InvalidParams("Parameter 'name' must not be empty.", new JsonObject { ["parameter"] = "name" });
        return name;
    }

    JsonValue? SetAttribute(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        string name = GetAttributeName(ctx);
        string value = ctx.GetString("value");
        if (!tree.SetAttribute(nodeId, name, value)) throw InspectionProtocol.NodeNotFound(nodeId);
        return null;
    }

    JsonValue? RemoveAttribute(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        string name = GetAttributeName(ctx);
        if (!tree.SetAttribute(nodeId, name, null)) throw InspectionProtocol.NodeNotFound(nodeId);
        return null;
    }

    JsonValue? SetClasses(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        var classes = ctx.GetStringArray("classes");
        if (!tree.SetClasses(nodeId, classes)) throw InspectionProtocol.NodeNotFound(nodeId);
        return null;
    }

    // ---- getOuterText ----

    /// <summary>includeStyles: "key" 输出的计算值。</summary>
    static readonly HashSet<string> s_keyProperties = new(StringComparer.Ordinal)
    {
        "display", "position", "width", "height", "flex-direction", "justify-content", "align-items", "gap",
        "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding-top", "padding-right", "padding-bottom", "padding-left",
        "color", "background-color", "font-size", "font-weight", "overflow-x", "overflow-y", "opacity", "visibility",
    };

    /// <summary>getOuterText 的输出上限（UTF-16 单元）。</summary>
    const int MaxOuterTextLength = 1 << 20;
    /// <summary>getOuterText 缩进的最大层数（更深的行按此缩进，避免输出随深度平方增长）。</summary>
    const int MaxOuterTextIndent = 64;

    JsonValue? GetOuterText(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        string mode = ctx.GetOptionalString("includeStyles") ?? "none";
        if (mode is not ("none" or "key" or "all"))
            throw DevToolsProtocolException.InvalidParams("Parameter 'includeStyles' must be \"none\", \"key\" or \"all\".",
                new JsonObject { ["parameter"] = "includeStyles" });
        StyleInspector? styles = null;
        if (mode != "none") styles = InspectionProtocol.RequireStyles(ctx);
        if (!tree.ContainsNode(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId);

        int maxNodes = Math.Max(1, Options.MaxTreeNodesPerResponse);
        int maxText = Options.MaxNodeTextLength;
        var sb = new StringBuilder();
        var stack = new Stack<(long Id, int Depth)>();
        var visited = new HashSet<long>();
        var children = new List<long>();
        stack.Push((nodeId, 0));
        int count = 0;
        bool truncated = false;
        while (stack.Count > 0)
        {
            var (id, depth) = stack.Pop();
            if (!visited.Add(id)) continue;
            var info = tree.DescribeNode(id);
            if (info is null) continue;
            if (++count > maxNodes || sb.Length > MaxOuterTextLength)
            {
                truncated = true;
                break;
            }
            sb.Append(' ', Math.Min(depth, MaxOuterTextIndent) * 2);
            if (info.Kind == InspectableNodeKind.Text)
            {
                sb.Append('"');
                AppendEscaped(sb, InspectionProtocol.Truncate(info.Text ?? string.Empty, maxText, out bool cut));
                sb.Append(cut ? "…\"" : "\"");
            }
            else
            {
                sb.Append('<').Append(info.Tag ?? "?");
                if (info.ElementId != null) AppendAttribute(sb, "id", info.ElementId);
                if (info.Classes is { Count: > 0 }) AppendAttribute(sb, "class", string.Join(" ", info.Classes));
                if (info.Attributes != null)
                    foreach (var kv in info.Attributes) AppendAttribute(sb, kv.Key ?? string.Empty, kv.Value ?? string.Empty);
                if (styles != null && styles.GetComputedStyle(id) is { } props)
                {
                    var style = new StringBuilder();
                    foreach (var p in props)
                    {
                        if (mode == "key" && !s_keyProperties.Contains(p.Name)) continue;
                        if (style.Length > 0) style.Append("; ");
                        style.Append(p.Name).Append(": ").Append(p.Value);
                    }
                    if (style.Length > 0) AppendAttribute(sb, "style", style.ToString());
                }
                sb.Append('>');
            }
            sb.Append('\n');

            children.Clear();
            if (info.ChildCount > 0 && tree.GetChildren(id, children))
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
        if (truncated) sb.Append("… (truncated)\n");
        return new JsonObject { ["text"] = sb.ToString() };
    }

    static void AppendAttribute(StringBuilder sb, string name, string value)
    {
        sb.Append(' ').Append(name).Append("=\"");
        AppendEscaped(sb, value);
        sb.Append('"');
    }

    static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c < ' ' ? ' ' : c); break;
            }
        }
    }

    // ================================================================ 展开

    /// <summary>
    /// 广度优先地展开 <paramref name="parentId"/>：直接子节点总是完整返回；更深的层按 <paramref name="depth"/>（-1 为不限）、
    /// 节点预算与嵌套层数上限展开。展开过的节点（含没有子节点而得到 <c>children: []</c> 的元素）按顺序记入 <paramref name="expansions"/>。
    /// 节点不存在时返回 null。
    /// </summary>
    JsonArray? ExpandFrom(InspectableTree tree, long parentId, int depth, List<Expansion> expansions)
    {
        var options = Options;
        int budget = Math.Max(0, options.MaxTreeNodesPerResponse);
        int maxLevel = Math.Max(1, options.MaxTreeDepthPerResponse);
        int maxText = options.MaxNodeTextLength;
        var visited = new HashSet<long> { parentId };
        var queue = new Queue<(InspectableNodeInfo Info, JsonObject Json, int Level)>();

        var ids = new List<long>();
        if (!tree.GetChildren(parentId, ids)) return null;
        var top = Describe(tree, parentId, ids, visited, maxText, queue, 1, expansions);
        budget -= top.Count;

        while (queue.Count > 0)
        {
            var (info, json, level) = queue.Dequeue();
            if ((depth != -1 && level >= depth) || level >= maxLevel || info.Kind == InspectableNodeKind.Text) continue;
            if (info.ChildCount <= 0)
            {
                // 叶子元素：带上空的 children，使其「已展开」，之后的插入直接以 childrenChanged 推送
                json["children"] = new JsonArray();
                expansions.Add(new Expansion(info.Id, new List<long>()));
                continue;
            }
            if (info.ChildCount > budget) continue;
            var kids = new List<long>();
            if (!tree.GetChildren(info.Id, kids) || kids.Count > budget) continue;
            var arr = Describe(tree, info.Id, kids, visited, maxText, queue, level + 1, expansions);
            json["children"] = arr;
            budget -= arr.Count;
        }
        return top;
    }

    static JsonArray Describe(InspectableTree tree, long parentId, List<long> ids, HashSet<long> visited, int maxText,
        Queue<(InspectableNodeInfo, JsonObject, int)> queue, int level, List<Expansion> expansions)
    {
        var arr = new JsonArray();
        var sent = new List<long>(ids.Count);
        foreach (var id in ids)
        {
            if (!visited.Add(id)) continue;     // 接缝报告了重复或成环的子节点：跳过
            var info = tree.DescribeNode(id);
            if (info is null) continue;
            var json = InspectionProtocol.NodeJson(info, maxText);
            arr.Add(json);
            sent.Add(id);
            queue.Enqueue((info, json, level));
        }
        expansions.Add(new Expansion(parentId, sent));
        return arr;
    }

    static void Apply(Mirror mirror, List<Expansion> expansions)
    {
        foreach (var e in expansions) mirror.Expand(e.ParentId, e.Children);
    }

    Mirror? GetMirror(DevToolsSession session, DevToolsTarget target) =>
        _mirrors.TryGetValue(session, out var map) && map.TryGetValue(target, out var m) ? m : null;

    /// <summary>节点是否已发送给该会话（Css 域据此筛选 computedStyleInvalidated）。</summary>
    internal bool IsTracked(DevToolsSession session, DevToolsTarget target, long nodeId)
    {
        lock (_gate) return GetMirror(session, target)?.IsTracked(nodeId) == true;
    }

    /// <summary>节点是否已发送给任何会话。</summary>
    internal bool IsTrackedByAny(DevToolsTarget target, long nodeId)
    {
        lock (_gate) return IsTrackedByAnyLocked(target, nodeId);
    }

    bool IsTrackedByAnyLocked(DevToolsTarget target, long nodeId)
    {
        foreach (var map in _mirrors.Values)
            if (map.TryGetValue(target, out var m) && m.IsTracked(nodeId)) return true;
        return false;
    }

    // ================================================================ 会话与目标

    internal override void SessionDisabledFor(DevToolsSession session)
    {
        lock (_gate)
        {
            _mirrors.Remove(session);
            if (_mirrors.Count == 0) _pending.Clear();
        }
    }

    internal void ForgetTarget(DevToolsTarget target)
    {
        lock (_gate)
        {
            foreach (var map in _mirrors.Values) map.Remove(target);
            _pending.Remove(target);
        }
    }

    // ================================================================ 变更通知（来自 InspectableTree）

    internal void OnChildInserted(DevToolsTarget target, long parentId, long childId, long beforeId) =>
        target.RunOnUIThread(() => ChildInserted(target, parentId, childId, beforeId));

    void ChildInserted(DevToolsTarget target, long parentId, long childId, long beforeId)
    {
        var tree = target.Tree;
        if (tree is null || Agent is null) return;
        List<DevToolsSession>? expanded = null;
        bool collapsed = false;
        lock (_gate)
        {
            foreach (var kv in _mirrors)
            {
                if (!kv.Value.TryGetValue(target, out var m) || !m.Nodes.TryGetValue(parentId, out var p)) continue;
                if (p.Children != null) (expanded ??= new List<DevToolsSession>()).Add(kv.Key);
                else collapsed = true;
            }
            if (collapsed) QueueLocked(target, parentId, ChangeKind.ChildCount, null);
        }
        if (expanded is null) return;

        var info = tree.DescribeNode(childId);
        if (info is null) return;   // 已被移除（通知是投递过来的）
        var entry = new JsonObject { ["node"] = InspectionProtocol.NodeJson(info, Options.MaxNodeTextLength) };
        if (beforeId != 0) entry["beforeId"] = beforeId;
        var recipients = new HashSet<DevToolsSession>();
        List<(DevToolsSession Session, long OldParentId)>? stale = null;
        lock (_gate)
        {
            foreach (var s in expanded)
            {
                if (GetMirror(s, target) is not { } m || !m.IsExpanded(parentId)) continue;
                if (m.Nodes.TryGetValue(childId, out var existing))
                {
                    // 已在该父节点下发送过（快照先于这条被投递过来的通知生成）：不重复推送，保留其子树
                    if (existing.ParentId == parentId) continue;
                    // 跨父节点移动先报告了插入（UI 层应先报告移除）：前端的旧位置要先移除，否则它会同时出现在两处
                    if (existing.ParentId != 0) (stale ??= new()).Add((s, existing.ParentId));
                }
                m.Track(parentId, childId);
                recipients.Add(s);
            }
        }
        if (stale != null)
            foreach (var (s, oldParent) in stale)
                Agent.SendEvent(this, s, "childrenChanged",
                    new JsonObject { ["parentId"] = oldParent, ["removed"] = new JsonArray { childId }, ["inserted"] = new JsonArray() }, target.Id);
        if (recipients.Count == 0) return;
        var p2 = new JsonObject { ["parentId"] = parentId, ["removed"] = new JsonArray(), ["inserted"] = new JsonArray { entry } };
        Agent.SendEvent(this, null, "childrenChanged", p2, target.Id, recipients.Contains);
    }

    internal void OnChildRemoved(DevToolsTarget target, long parentId, long childId) =>
        target.RunOnUIThread(() => ChildRemoved(target, parentId, childId));

    void ChildRemoved(DevToolsTarget target, long parentId, long childId)
    {
        if (Agent is null) return;
        HashSet<DevToolsSession>? recipients = null;
        var dropped = new List<long>();
        lock (_gate)
        {
            bool collapsed = false;
            foreach (var kv in _mirrors)
            {
                if (!kv.Value.TryGetValue(target, out var m) || !m.Nodes.TryGetValue(parentId, out var p)) continue;
                if (p.Children != null)
                {
                    // 只丢弃确实挂在该父节点下的节点：若它已被（先报告的插入）挂到别处，别处的副本仍然有效
                    if (p.Children.Contains(childId))
                    {
                        (recipients ??= new HashSet<DevToolsSession>()).Add(kv.Key);
                        m.DropSubtree(childId, dropped);
                    }
                }
                else collapsed = true;
            }
            if (collapsed) QueueLocked(target, parentId, ChangeKind.ChildCount, null);
            if (dropped.Count > 0 && _pending.TryGetValue(target, out var pending))
                foreach (var id in dropped)
                    if (!IsTrackedByAnyLocked(target, id) && pending.Nodes.Remove(id)) { /* Order 中的残留在发出时跳过 */ }
        }
        if (recipients is null) return;
        var p2 = new JsonObject { ["parentId"] = parentId, ["removed"] = new JsonArray { childId }, ["inserted"] = new JsonArray() };
        Agent.SendEvent(this, null, "childrenChanged", p2, target.Id, recipients.Contains);
    }

    internal void OnChildMoved(DevToolsTarget target, long parentId, long childId, long beforeId) =>
        target.RunOnUIThread(() => ChildMoved(target, parentId, childId, beforeId));

    void ChildMoved(DevToolsTarget target, long parentId, long childId, long beforeId)
    {
        if (Agent is null) return;
        HashSet<DevToolsSession>? recipients = null;
        lock (_gate)
        {
            foreach (var kv in _mirrors)
                if (kv.Value.TryGetValue(target, out var m) && m.Nodes.TryGetValue(parentId, out var p) && p.Children?.Contains(childId) == true)
                    (recipients ??= new HashSet<DevToolsSession>()).Add(kv.Key);
        }
        if (recipients is null) return;
        var p2 = new JsonObject { ["parentId"] = parentId, ["nodeId"] = childId };
        if (beforeId != 0) p2["beforeId"] = beforeId;
        Agent.SendEvent(this, null, "childMoved", p2, target.Id, recipients.Contains);
    }

    internal void OnNodeChanged(DevToolsTarget target, long nodeId, ChangeKind kind, string? attribute) =>
        target.RunOnUIThread(() =>
        {
            lock (_gate)
                if (IsTrackedByAnyLocked(target, nodeId)) QueueLocked(target, nodeId, kind, attribute);
        });

    void QueueLocked(DevToolsTarget target, long nodeId, ChangeKind kind, string? attribute)
    {
        if (!_pending.TryGetValue(target, out var pending)) _pending[target] = pending = new Pending();
        if (!pending.Nodes.TryGetValue(nodeId, out var n))
        {
            pending.Nodes[nodeId] = n = new PendingNode();
            pending.Order.Add(nodeId);
            // 节点出队（被移除）后又入队会在 Order 中留下重复项；宿主长时间不 FlushFrame 时压缩，保持 Order 与 Nodes 同阶
            if (pending.Order.Count > 2 * pending.Nodes.Count + 64)
            {
                var seen = new HashSet<long>();
                pending.Order.RemoveAll(id => !pending.Nodes.ContainsKey(id) || !seen.Add(id));
            }
        }
        switch (kind)
        {
            case ChangeKind.Attribute: (n.Attributes ??= new HashSet<string>(StringComparer.Ordinal)).Add(attribute!); break;
            case ChangeKind.Text: n.Text = true; break;
            case ChangeKind.PseudoStates: n.PseudoStates = true; break;
            default: n.ChildCount = true; break;
        }
    }

    internal void OnDocumentUpdated(DevToolsTarget target) =>
        target.RunOnUIThread(() =>
        {
            lock (_gate)
            {
                foreach (var map in _mirrors.Values) map.Remove(target);
                _pending.Remove(target);
            }
            Emit("documentUpdated", null, target.Id);
        });

    /// <summary>帧边界：发出本帧合并的属性 / 文本 / 伪状态 / 子节点数变化（在 UI 线程上）。</summary>
    protected override void OnFrame()
    {
        List<KeyValuePair<DevToolsTarget, Pending>> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = new List<KeyValuePair<DevToolsTarget, Pending>>(_pending);
            _pending.Clear();
        }
        foreach (var kv in batch)
        {
            var target = kv.Key;
            var tree = target.Tree;
            if (tree is null || target.Agent != Agent) continue;
            if (target.EffectiveDispatcher is { } d && !d.CheckAccess())
            {
                var pending = kv.Value;
                d.Post(() => FlushTarget(target, tree, pending));
            }
            else FlushTarget(target, tree, kv.Value);
        }
    }

    void FlushTarget(DevToolsTarget target, InspectableTree tree, Pending pending)
    {
        var agent = Agent;
        if (agent is null) return;
        int maxText = Options.MaxNodeTextLength;
        foreach (long nodeId in pending.Order)
        {
            if (!pending.Nodes.TryGetValue(nodeId, out var change)) continue;   // 排队后被移除，或 Order 中的重复项
            pending.Nodes.Remove(nodeId);
            InspectableNodeInfo? info;
            try { info = tree.DescribeNode(nodeId); }
            catch (Exception) { continue; }
            if (info is null) continue;

            HashSet<DevToolsSession>? recipients = null;
            lock (_gate)
                foreach (var kv in _mirrors)
                    if (kv.Value.TryGetValue(target, out var m) && m.IsTracked(nodeId))
                        (recipients ??= new HashSet<DevToolsSession>()).Add(kv.Key);
            if (recipients is null) continue;
            Func<DevToolsSession, bool> filter = recipients.Contains;

            if (change.Attributes != null)
                agent.SendEvent(this, null, "attributesChanged", AttributesChanged(info, change.Attributes), target.Id, filter);
            if (change.Text)
            {
                var p = new JsonObject { ["nodeId"] = nodeId, ["text"] = InspectionProtocol.Truncate(info.Text ?? string.Empty, maxText, out bool cut) };
                if (cut) p["textTruncated"] = true;
                agent.SendEvent(this, null, "textChanged", p, target.Id, filter);
            }
            if (change.PseudoStates)
                agent.SendEvent(this, null, "pseudoStateChanged",
                    new JsonObject { ["nodeId"] = nodeId, ["states"] = InspectionProtocol.StringArray(info.PseudoStates) }, target.Id, filter);
            if (change.ChildCount)
            {
                // 只发给未展开它的会话；已展开的会话已经通过 childrenChanged 得知
                HashSet<DevToolsSession>? collapsed = null;
                lock (_gate)
                    foreach (var s in recipients)
                        if (GetMirror(s, target) is { } m && !m.IsExpanded(nodeId)) (collapsed ??= new HashSet<DevToolsSession>()).Add(s);
                if (collapsed != null)
                    agent.SendEvent(this, null, "childCountUpdated",
                        new JsonObject { ["nodeId"] = nodeId, ["childCount"] = Math.Max(0, info.ChildCount) }, target.Id, collapsed.Contains);
            }
        }
    }

    static JsonObject AttributesChanged(InspectableNodeInfo info, HashSet<string> names)
    {
        var attrs = new JsonObject();
        bool classesChanged = false, idChanged = false;
        Dictionary<string, string>? lookup = null;
        if (info.Attributes is { Count: > 8 } many)
        {
            lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in many) if (kv.Key != null) lookup[kv.Key] = kv.Value ?? string.Empty;
        }
        foreach (var name in names)
        {
            if (name == "id") { idChanged = true; continue; }
            if (name == "class") { classesChanged = true; continue; }
            string? value = null;
            if (lookup != null) lookup.TryGetValue(name, out value);
            else if (info.Attributes != null)
                foreach (var kv in info.Attributes)
                    if (kv.Key == name) { value = kv.Value ?? string.Empty; break; }
            attrs[name] = value is null ? JsonValue.Null : JsonValue.Create(value);
        }
        var p = new JsonObject { ["nodeId"] = info.Id, ["attributes"] = attrs };
        if (classesChanged) p["classes"] = InspectionProtocol.StringArray(info.Classes);
        if (idChanged) p["elementId"] = info.ElementId is null ? JsonValue.Null : JsonValue.Create(info.ElementId);
        return p;
    }
}

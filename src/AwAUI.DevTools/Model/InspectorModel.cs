using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.Threading;
using AwAUI.UI;

namespace AwAUI.DevTools;

/// <summary>右侧面板的页签。</summary>
internal enum SideTab : byte { Styles, Computed, Events }

/// <summary>事件日志的一条。</summary>
internal sealed class LogEntry
{
    public LogEntry(long seq, string method, string detail)
    {
        Seq = seq;
        Method = method;
        Detail = detail;
    }

    public readonly long Seq;
    public readonly string Method;
    public readonly string Detail;
}

/// <summary>正在编辑的声明：哪一块（规则 id 或内联样式）的第几条，-1 表示新增一条。</summary>
internal sealed class EditState
{
    public EditState(StyleBlock block, int index)
    {
        Block = block;
        Index = index;
    }

    public readonly StyleBlock Block;
    public readonly int Index;

    public bool Matches(StyleBlock b, int index) =>
        index == Index && b.RuleId == Block.RuleId && (b.RuleId is not null || b.NodeId == Block.NodeId);
}

/// <summary>
/// 面板的全部状态与协议交互（只在面板的 UI 线程上使用）。视图组件读取这里的字段与几个 <see cref="State{T}"/> 版本号，
/// 版本号变化时对应的组件重建；与被检查应用的一切交互都经 <see cref="DevToolsClient"/>（协议），不直接接触应用的元素。
/// </summary>
internal sealed class InspectorModel : IDisposable
{
    /// <summary>树视图的行高（逻辑像素，与面板样式表一致）。</summary>
    public const float RowHeight = 20;

    /// <summary>事件日志保留的条数。</summary>
    public const int MaxLogEntries = 300;

    readonly DevToolsClient _client;
    readonly IDispatcher _dispatcher;
    readonly string? _pairingToken;
    readonly Dictionary<string, string> _sheetNames = new(StringComparer.Ordinal);
    readonly LinkedList<LogEntry> _log = new();
    long _logSeq;
    int _stylesToken;
    int _documentToken;
    bool _refreshQueued;
    bool _refreshAfterEdit;
    bool _disposed;

    public InspectorModel(DevToolsClient client, IDispatcher dispatcher, string? targetId, string? pairingToken)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        TargetId = targetId;
        _pairingToken = pairingToken;
        _client.EventReceived += OnEvent;
        _client.Disconnected += OnDisconnected;
    }

    // ------------------------------------------------------------ 视图读取的状态

    public readonly TreeMirror Tree = new();

    /// <summary>树视图（行、选中、滚动）的版本。</summary>
    public readonly State<int> TreeRevision = new(0);

    /// <summary>右侧样式 / 计算值 / 盒模型的版本。</summary>
    public readonly State<int> StylesRevision = new(0);

    /// <summary>工具栏、页签与状态栏的版本。</summary>
    public readonly State<int> ChromeRevision = new(0);

    /// <summary>事件日志的版本。</summary>
    public readonly State<int> LogRevision = new(0);

    public string? TargetId { get; private set; }
    public string? AppName { get; private set; }
    public bool Connected { get; private set; }
    public bool Loaded { get; private set; }
    public string Status { get; private set; } = "Connecting…";
    public bool Picking { get; private set; }
    public SideTab Tab { get; private set; } = SideTab.Styles;
    public bool ShowAllComputed { get; private set; }

    public DomNode? Selected { get; private set; }
    public long HoveredId { get; private set; }

    public StylesSnapshot? Styles { get; private set; }
    public List<ComputedEntry>? Computed { get; private set; }
    public BoxSnapshot? Box { get; private set; }
    public string? StylesError { get; private set; }

    public EditState? Editing { get; private set; }
    /// <summary>编辑框的当前内容（受控输入的值；每次输入更新，不触发重建）。</summary>
    public string EditDraft { get; set; } = "";

    public float TreeScrollTop { get; private set; }
    public float TreeViewportHeight { get; private set; } = 400;
    public float SideScrollTop { get; private set; }
    float _sideViewport = 400, _sideContent;

    public IEnumerable<LogEntry> Log => _log;
    public int LogCount => _log.Count;

    /// <summary>协议请求累计数（测试与性能统计用）。</summary>
    public int RequestCount { get; private set; }

    /// <summary>选中节点变化（面板外的钩子，例如测试）。</summary>
    public event Action<DomNode?>? SelectionChanged;

    // ------------------------------------------------------------ 协议

    void Call(string method, JsonObject? p, Action<DevToolsResponse>? callback)
    {
        if (_disposed) return;
        RequestCount++;
        _client.Call(method, p, TargetId, r =>
        {
            if (_disposed) return;
            callback?.Invoke(r);
        });
    }

    /// <summary>握手、启用 Tree / Css / Overlay 域并加载文档。</summary>
    public void Start()
    {
        Status = "Connecting…";
        _client.Hello("AwAUI.DevTools", _pairingToken, r =>
        {
            if (_disposed) return;
            if (r.IsError)
            {
                SetStatus("Handshake failed: " + r.ErrorMessage);
                return;
            }
            var o = r.ResultObject;
            AppName = (o["app"] as JsonObject)?.GetStringOrDefault("name");
            if (TargetId is null && o["targets"] is JsonArray { Count: > 0 } targets && targets[0] is JsonObject t0)
                TargetId = t0.GetStringOrDefault("id");
            Connected = true;
            Call("Tree.enable", null, null);
            Call("Css.enable", null, null);
            Call("Overlay.enable", null, null);
            LoadStyleSheets();
            LoadDocument();
        });
    }

    void LoadStyleSheets()
    {
        Call("Css.getStyleSheets", null, r =>
        {
            if (r.IsError || r.ResultObject["sheets"] is not JsonArray sheets) return;
            _sheetNames.Clear();
            foreach (var s in sheets)
                if (s is JsonObject so && so.GetStringOrDefault("sheetId") is { } id)
                    _sheetNames[id] = so.GetStringOrDefault("sourceUrl") ?? so.GetStringOrDefault("title") ?? id;
        });
    }

    /// <summary>（重新）加载整棵树：保留之前的选中（若节点仍在）。</summary>
    public void LoadDocument()
    {
        int token = ++_documentToken;
        long keep = Selected?.Id ?? 0;
        Call("Tree.getDocument", new JsonObject { ["depth"] = -1 }, r =>
        {
            if (token != _documentToken) return;
            if (r.IsError)
            {
                SetStatus("Tree.getDocument failed: " + r.ErrorMessage);
                return;
            }
            Tree.LoadDocument(r.ResultObject["root"]);
            Tree.AutoExpand(maxRows: 160);
            Loaded = true;
            TreeScrollTop = 0;
            Selected = null;
            if (keep != 0 && Tree.Get(keep) is { } n) SelectNode(n, scroll: true, highlight: false);
            else ClearStyles();
            SetStatus(null);
            BumpTree();
        });
    }

    void OnEvent(DevToolsEvent e)
    {
        if (_disposed) return;
        if (e.Target is not null && TargetId is not null && e.Target != TargetId) return;
        AddLog(e);
        string m = e.Method;
        int dot = m.IndexOf('.');
        string domain = dot > 0 ? m.Substring(0, dot) : m;
        string name = dot > 0 ? m.Substring(dot + 1) : "";
        switch (domain)
        {
            case "Tree":
                if (name == "documentUpdated")
                {
                    LoadDocument();
                    return;
                }
                int structure = Tree.StructureVersion;
                if (!Tree.Apply(name, e.Params)) return;
                if (Selected is { } sel && Tree.Get(sel.Id) != sel)
                {
                    Selected = null;
                    ClearStyles();
                    SelectionChanged?.Invoke(null);
                }
                // 不在这里夹紧滚动位置：那要摊平整个行列表，一帧里几百条结构事件就是「事件数 × 行数」的工作量。
                // 推迟到树视图下一次重建（每帧至多一次）
                if (Tree.StructureVersion != structure) _clampPending = true;
                BumpTree();
                // 结构或文本变化可能改变选中元素的盒；属性 / 伪状态变化另由 computedStyleInvalidated 报告
                if (name is "childrenChanged" or "childMoved" or "textChanged") ScheduleRefresh();
                ChromeRevision.Value++;
                return;
            case "Overlay":
                if (name == "nodePicked" && e.Params["nodeId"] is JsonNumber pn && pn.TryGetInt64(out long picked))
                {
                    if (Picking) SetPicking(false);
                    RevealAndSelect(picked);
                }
                else if (name == "inspectModeCanceled" && Picking)
                {
                    Picking = false;
                    ChromeRevision.Value++;
                }
                return;
            case "Css":
                if (name == "computedStyleInvalidated")
                {
                    if (Selected is not null && e.Params["nodeIds"] is JsonArray ids)
                    {
                        long styled = StyledId(Selected);
                        foreach (var v in ids)
                            if (v is JsonNumber n && n.TryGetInt64(out long id) && id == styled)
                            {
                                ScheduleRefresh();
                                break;
                            }
                    }
                }
                else if (name.StartsWith("styleSheet", StringComparison.Ordinal))
                {
                    LoadStyleSheets();
                    ScheduleRefresh();
                }
                return;
        }
    }

    void OnDisconnected()
    {
        if (_disposed) return;
        Connected = false;
        Picking = false;
        SetStatus("Disconnected");
    }

    void AddLog(DevToolsEvent e)
    {
        string detail = e.Params.ToJsonString();
        if (detail.Length > 160) detail = detail.Substring(0, 157) + "…";
        _log.AddLast(new LogEntry(++_logSeq, e.Method, detail));
        while (_log.Count > MaxLogEntries) _log.RemoveFirst();
        LogRevision.Value++;
    }

    void SetStatus(string? status)
    {
        Status = status ?? (Connected ? "Connected" : "Disconnected");
        ChromeRevision.Value++;
    }

    void BumpTree() => TreeRevision.Value++;

    void BumpStyles() => StylesRevision.Value++;

    // ------------------------------------------------------------ 树：选中、展开、拾取

    /// <summary>描述一个节点（继承分区的标题）：<c>div#id.class</c>。</summary>
    public string Describe(long id)
    {
        if (Tree.Get(id) is not { } n) return "node " + id.ToString(CultureInfo.InvariantCulture);
        if (n.IsText) return "#text";
        var sb = new StringBuilder(n.Tag ?? "?");
        if (n.ElementId is { } eid) sb.Append('#').Append(eid);
        if (n.Classes is { } cls)
            foreach (var c in cls) sb.Append('.').Append(c);
        return sb.ToString();
    }

    /// <summary>文本节点没有自己的样式与盒：样式面板显示它的父元素。</summary>
    static long StyledId(DomNode n) => n.IsText && n.Parent is { } p ? p.Id : n.Id;

    public void Select(DomNode? node) => SelectNode(node, scroll: false, highlight: true);

    void SelectNode(DomNode? node, bool scroll, bool highlight)
    {
        if (node is not null && Tree.Get(node.Id) != node) node = null;
        CancelEdit();
        bool changed = !ReferenceEquals(Selected, node);
        Selected = node;
        if (node is not null)
        {
            Tree.Reveal(node);
            if (scroll) ScrollRowIntoView(Tree.RowOf(node.Id));
            if (highlight) Highlight(node.Id);
        }
        else if (highlight && HoveredId == 0) Call("Overlay.hideHighlight", null, null);
        BumpTree();
        if (changed)
        {
            SideScrollTop = 0;
            StylesError = null;
            RefreshStyles();
            SelectionChanged?.Invoke(node);
        }
    }

    /// <summary>选中 <paramref name="nodeId"/>：不在镜像中时先 <c>Tree.pushNodePath</c> 展开它的祖先。</summary>
    public void RevealAndSelect(long nodeId)
    {
        if (Tree.Get(nodeId) is { } n)
        {
            SelectNode(n, scroll: true, highlight: true);
            return;
        }
        Call("Tree.pushNodePath", new JsonObject { ["nodeId"] = nodeId }, r =>
        {
            if (r.IsError) return;
            if (r.ResultObject["children"] is JsonArray groups)
                foreach (var g in groups)
                    if (g is JsonObject go && go["parentId"] is JsonNumber pn && pn.TryGetInt64(out long pid))
                        Tree.SetChildren(pid, go["children"] as JsonArray);
            if (Tree.Get(nodeId) is { } found) SelectNode(found, scroll: true, highlight: true);
        });
    }

    /// <summary>展开或折叠；第一次展开时向服务端请求子节点。</summary>
    public void Toggle(DomNode node)
    {
        if (node.Expanded) Collapse(node);
        else Expand(node);
    }

    public void Expand(DomNode node)
    {
        if (node.Expanded || !node.HasChildren) return;
        node.Expanded = true;
        Tree.ExpansionChanged();
        BumpTree();
        if (node.Children is null) RequestChildren(node);
    }

    public void Collapse(DomNode node)
    {
        if (!node.Expanded) return;
        node.Expanded = false;
        Tree.ExpansionChanged();
        ClampTreeScroll();
        BumpTree();
    }

    void RequestChildren(DomNode node)
    {
        Call("Tree.requestChildren", new JsonObject { ["nodeId"] = node.Id, ["depth"] = 2 }, r =>
        {
            if (r.IsError || Tree.Get(node.Id) != node) return;
            Tree.SetChildren(node.Id, r.ResultObject["children"] as JsonArray);
            BumpTree();
            ChromeRevision.Value++;
        });
    }

    /// <summary>键盘：上下移动选中，左右折叠 / 展开（同浏览器的元素面板）。</summary>
    public void MoveSelection(int delta)
    {
        var rows = Tree.Rows;
        if (rows.Count == 0) return;
        int i = Selected is null ? -1 : Tree.RowOf(Selected.Id);
        int j = i < 0 ? 0 : Math.Max(0, Math.Min(rows.Count - 1, i + delta));
        SelectNode(rows[j].Node, scroll: true, highlight: true);
    }

    public void ExpandOrDescend()
    {
        if (Selected is not { } n) return;
        if (n.HasChildren && !n.Expanded && n.InlineText is null) Expand(n);
        else if (n.Expanded && n.Children is { Count: > 0 } c && n.InlineText is null) SelectNode(c[0], scroll: true, highlight: true);
    }

    public void CollapseOrAscend()
    {
        if (Selected is not { } n) return;
        if (n.Expanded && n.InlineText is null && n.HasChildren) Collapse(n);
        else if (n.Parent is { } p) SelectNode(p, scroll: true, highlight: true);
    }

    /// <summary>指针在树的某一行上（0 = 离开）：在应用里高亮它，离开后回到选中的元素。</summary>
    public void Hover(long nodeId)
    {
        if (HoveredId == nodeId) return;
        HoveredId = nodeId;
        if (nodeId != 0) Highlight(nodeId);
        else if (Selected is { } s) Highlight(s.Id);
        else Call("Overlay.hideHighlight", null, null);
    }

    void Highlight(long nodeId)
    {
        if (Tree.Get(nodeId) is { } n) nodeId = StyledId(n);
        Call("Overlay.highlightNode", new JsonObject { ["nodeId"] = nodeId }, null);
    }

    /// <summary>工具栏的「选择元素」：经协议进入 / 退出应用窗口的拾取模式。</summary>
    public void TogglePick() => SetPicking(!Picking);

    void SetPicking(bool on)
    {
        if (Picking == on) return;
        Picking = on;
        Call("Overlay.setInspectMode", new JsonObject { ["mode"] = on ? "pick" : "none" }, null);
        ChromeRevision.Value++;
    }

    public void SetTab(SideTab tab)
    {
        if (Tab == tab) return;
        CancelEdit();
        Tab = tab;
        SideScrollTop = 0;
        ChromeRevision.Value++;
        BumpStyles();
    }

    public void ToggleShowAllComputed()
    {
        ShowAllComputed = !ShowAllComputed;
        BumpStyles();
    }

    // ------------------------------------------------------------ 滚动

    public void SetTreeViewport(float height)
    {
        if (height <= 0 || Math.Abs(height - TreeViewportHeight) < 0.5f) return;
        TreeViewportHeight = height;
        ClampTreeScroll();
        BumpTree();
    }

    public float MaxTreeScroll => Math.Max(0, Tree.Rows.Count * RowHeight - TreeViewportHeight);

    public void ScrollTreeBy(float dy) => ScrollTreeTo(TreeScrollTop + dy);

    public void ScrollTreeTo(float top)
    {
        float t = Math.Max(0, Math.Min(MaxTreeScroll, float.IsNaN(top) ? 0 : top));
        if (Math.Abs(t - TreeScrollTop) < 0.01f) return;
        TreeScrollTop = t;
        BumpTree();
    }

    bool _clampPending;

    /// <summary>结构事件之后推迟的滚动夹紧（树视图重建时调用，行列表此时本来就要摊平）。</summary>
    public void ApplyPendingTreeClamp()
    {
        if (!_clampPending) return;
        _clampPending = false;
        ClampTreeScroll();
    }

    void ClampTreeScroll()
    {
        float t = Math.Min(TreeScrollTop, MaxTreeScroll);
        if (t != TreeScrollTop) TreeScrollTop = t;
    }

    void ScrollRowIntoView(int row)
    {
        if (row < 0) return;
        float top = row * RowHeight, bottom = top + RowHeight;
        if (top < TreeScrollTop) ScrollTreeTo(top);
        else if (bottom > TreeScrollTop + TreeViewportHeight) ScrollTreeTo(bottom - TreeViewportHeight);
    }

    public void SetSideMetrics(float viewport, float content)
    {
        _sideViewport = viewport;
        _sideContent = content;
        float max = Math.Max(0, content - viewport);
        if (SideScrollTop > max)
        {
            SideScrollTop = max;
            BumpStyles();
        }
    }

    public float MaxSideScroll => Math.Max(0, _sideContent - _sideViewport);

    public void ScrollSideBy(float dy)
    {
        float t = Math.Max(0, Math.Min(MaxSideScroll, SideScrollTop + dy));
        if (Math.Abs(t - SideScrollTop) < 0.01f) return;
        SideScrollTop = t;
        BumpStyles();
    }

    // ------------------------------------------------------------ 样式

    void ClearStyles()
    {
        _stylesToken++;
        Styles = null;
        Computed = null;
        Box = null;
        StylesError = null;
        BumpStyles();
    }

    /// <summary>合并到一次刷新（同一轮调度中的多个事件只刷新一次）；编辑中推迟到编辑结束。</summary>
    void ScheduleRefresh()
    {
        if (Selected is null || _refreshQueued) return;
        if (Editing is not null)
        {
            _refreshAfterEdit = true;
            return;
        }
        _refreshQueued = true;
        _dispatcher.Post(() =>
        {
            _refreshQueued = false;
            if (!_disposed) RefreshStyles();
        });
    }

    /// <summary>重新读取选中元素的匹配规则、计算值与盒模型（过期的响应按令牌丢弃）。</summary>
    public void RefreshStyles()
    {
        if (Selected is not { } node)
        {
            ClearStyles();
            return;
        }
        int token = ++_stylesToken;
        long id = StyledId(node);
        var p = new JsonObject { ["nodeId"] = id };
        Call("Css.getMatchedRules", p, r =>
        {
            if (token != _stylesToken) return;
            if (r.IsError)
            {
                Styles = null;
                StylesError = r.ErrorMessage;
            }
            else
            {
                Styles = StylesSnapshot.Parse(id, r.ResultObject, s => _sheetNames.TryGetValue(s, out var name) ? name : null, Describe);
            }
            BumpStyles();
        });
        Call("Css.getComputedStyle", new JsonObject { ["nodeId"] = id }, r =>
        {
            if (token != _stylesToken) return;
            var list = new List<ComputedEntry>();
            if (!r.IsError && r.ResultObject["properties"] is JsonArray props)
                foreach (var item in props)
                {
                    if (item is not JsonObject o) continue;
                    string origin = (o["from"] as JsonObject)?.GetStringOrDefault("origin") ?? "initial";
                    list.Add(new ComputedEntry(o.GetStringOrDefault("name") ?? "", o.GetStringOrDefault("value") ?? "", origin));
                }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            Computed = list;
            BumpStyles();
        });
        Call("Layout.getBoxModel", new JsonObject { ["nodeId"] = id }, r =>
        {
            if (token != _stylesToken) return;
            Box = r.IsError ? null : BoxSnapshot.Parse(r.ResultObject);
            BumpStyles();
        });
    }

    /// <summary>开始编辑第 <paramref name="index"/> 条声明的值（-1：在块末尾新增一条，输入 <c>name: value</c>）。</summary>
    public void BeginEdit(StyleBlock block, int index)
    {
        if (!block.Editable) return;
        StylesError = null;
        Editing = new EditState(block, index);
        // 编辑框的初始文本与显示的值一致（含 !important，同浏览器），否则只改值时会悄悄丢掉 !important
        EditDraft = index >= 0 && index < block.Decls.Count ? DisplayValue(block.Decls[index]) : "";
        BumpStyles();
    }

    public void CancelEdit()
    {
        if (Editing is null) return;
        Editing = null;
        BumpStyles();
        if (_refreshAfterEdit)
        {
            _refreshAfterEdit = false;
            ScheduleRefresh();
        }
    }

    /// <summary>
    /// 提交编辑：值为空表示删除该声明；新增时文本为 <c>name: value</c>。经 <c>Css.setStyleText</c> 替换整个声明块，
    /// 应用在下一帧生效；随后刷新样式。
    /// </summary>
    public void CommitEdit(string? text)
    {
        if (Editing is not { } edit) return;
        Editing = null;
        bool deferredRefresh = _refreshAfterEdit;
        _refreshAfterEdit = false;
        // 没有发出修改时，编辑期间推迟的刷新仍要补上（应用在编辑期间可能变了）
        void NoChange()
        {
            BumpStyles();
            if (deferredRefresh) ScheduleRefresh();
        }
        text = (text ?? "").Trim();
        var block = edit.Block;
        StyleDecl? replacement;
        if (edit.Index >= 0)
        {
            if (edit.Index >= block.Decls.Count)
            {
                NoChange();
                return;
            }
            var old = block.Decls[edit.Index];
            if (text == DisplayValue(old))
            {
                NoChange();
                return;
            }
            replacement = text.Length == 0 ? null : ParseValue(old.Name, text, old.Disabled);
        }
        else
        {
            int colon = text.IndexOf(':');
            if (colon <= 0)
            {
                if (text.Length > 0) StylesError = "Expected \"name: value\".";
                NoChange();
                return;
            }
            string name = text.Substring(0, colon).Trim();
            string value = text.Substring(colon + 1).Trim().TrimEnd(';').Trim();
            if (name.Length == 0 || value.Length == 0)
            {
                NoChange();
                return;
            }
            replacement = ParseValue(name, value, disabled: false);
        }
        ApplyBlockText(block, block.BuildText(edit.Index, replacement));
    }

    /// <summary>声明在样式页里显示的值（编辑框的初始文本）。</summary>
    internal static string DisplayValue(StyleDecl d) => d.Important ? d.Value + " !important" : d.Value;

    static StyleDecl ParseValue(string name, string value, bool disabled)
    {
        bool important = false;
        const string bang = "!important";
        value = value.TrimEnd().TrimEnd(';').TrimEnd();
        if (value.EndsWith(bang, StringComparison.OrdinalIgnoreCase))
        {
            important = true;
            value = value.Substring(0, value.Length - bang.Length).TrimEnd();
        }
        return new StyleDecl { Name = name, Value = value.TrimEnd(';').TrimEnd(), Important = important, Disabled = disabled };
    }

    /// <summary>勾选框：注释掉 / 恢复一条声明。</summary>
    public void ToggleDeclaration(StyleBlock block, int index)
    {
        if (!block.Editable || index < 0 || index >= block.Decls.Count) return;
        CancelEdit();
        var d = block.Decls[index];
        var flipped = new StyleDecl { Name = d.Name, Value = d.Value, Important = d.Important, Disabled = !d.Disabled };
        ApplyBlockText(block, block.BuildText(index, flipped));
    }

    void ApplyBlockText(StyleBlock block, string text)
    {
        var target = block.RuleId is { } ruleId ? new JsonObject { ["ruleId"] = ruleId } : new JsonObject { ["nodeId"] = block.NodeId };
        Call("Css.setStyleText", new JsonObject { ["target"] = target, ["text"] = text }, r =>
        {
            StylesError = r.IsError ? "Edit rejected: " + r.ErrorMessage : null;
            RefreshStyles();
        });
        BumpStyles();
    }

    /// <summary>撤销本面板对应用样式的全部修改（<c>Css.clearOverrides</c>）。</summary>
    public void ClearOverrides()
    {
        CancelEdit();
        Call("Css.clearOverrides", null, _ => RefreshStyles());
    }

    public void Dispose()
    {
        if (_disposed) return;
        // 撤销本会话在应用里留下的高亮与拾取模式（停用 Overlay 时 Agent 也会撤销）
        if (Connected)
        {
            if (Picking) _client.Call("Overlay.setInspectMode", new JsonObject { ["mode"] = "none" }, TargetId, null);
            _client.Call("Overlay.hideHighlight", null, TargetId, null);
            _client.Call("Overlay.disable", null, TargetId, null);
            _client.Call("Css.disable", null, TargetId, null);
            _client.Call("Tree.disable", null, TargetId, null);
        }
        _disposed = true;
        _client.EventReceived -= OnEvent;
        _client.Disconnected -= OnDisconnected;
    }
}

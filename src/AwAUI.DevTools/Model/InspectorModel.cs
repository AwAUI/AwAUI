using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Threading;
using AwAUI.UI;

namespace AwAUI.DevTools;

/// <summary>右侧面板的页签。</summary>
internal enum SideTab : byte { Styles, Computed, Attributes, Events, Stylesheets, Components }

/// <summary>属性查看器正在编辑的字段。</summary>
internal enum AttributeEditKind : byte
{
    /// <summary><c>DomNode.Attributes</c> 里的自定义属性。</summary>
    Attribute,
    /// <summary>元素的 class 列表（用 <c>Tree.setClasses</c> 写回）。</summary>
    Class,
    /// <summary>元素的 id（用 <c>Tree.setAttribute</c> 写回）。</summary>
    Id,
    /// <summary>新增一条属性（输入 <c>name=value</c>）。</summary>
    NewAttribute,
}

/// <summary>属性查看器里的一次编辑：哪一个字段（<see cref="Name"/> 只在自定义属性时有意义）。</summary>
internal sealed class AttributeEditState
{
    public AttributeEditState(AttributeEditKind kind, string? name = null)
    {
        Kind = kind;
        Name = name;
    }

    public readonly AttributeEditKind Kind;
    public readonly string? Name;

    public bool Matches(AttributeEditKind kind, string? name) =>
        Kind == kind && (kind != AttributeEditKind.Attribute || string.Equals(Name, name, StringComparison.Ordinal));
}

/// <summary><c>Tree.search</c> 的一条结果：节点 id、命中原因与短标签。</summary>
internal sealed class SearchResult
{
    public SearchResult(long nodeId, string match, string label)
    {
        NodeId = nodeId;
        Match = match;
        Label = label;
    }

    public readonly long NodeId;
    /// <summary><c>tag</c> / <c>id</c> / <c>class</c> / <c>text</c> / <c>selector</c>。</summary>
    public readonly string Match;
    /// <summary>元素在面板里的短描述（例如 <c>div#app.count</c>）；不在镜像中时由命中原因与查询生成。</summary>
    public readonly string Label;
}

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

    /// <summary>搜索结果在面板里最多列出的条数（协议自身另有响应上限）。</summary>
    public const int MaxSearchResults = 100;

    readonly DevToolsClient _client;
    readonly IDispatcher _dispatcher;
    readonly string? _pairingToken;
    readonly Dictionary<string, string> _sheetNames = new(StringComparer.Ordinal);
    readonly LinkedList<LogEntry> _log = new();
    readonly HashSet<string> _forcedPseudoStates = new(StringComparer.Ordinal);
    long _logSeq;
    int _stylesToken;
    int _documentToken;
    int _searchToken;
    int _sheetTextToken;
    bool _searchScheduled;
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

    /// <summary>搜索结果与结果列表的版本。</summary>
    public readonly State<int> SearchRevision = new(0);

    /// <summary>样式表列表与样式表编辑页的版本。</summary>
    public readonly State<int> SheetsRevision = new(0);

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

    List<ComponentInstance>? _components;
    int _componentsVersion = -1;
    int _componentCount;
    bool _componentsTruncated;

    /// <summary>
    /// 组件实例树：由镜像里已有的 <see cref="DomNode.ComponentName"/> 构建，父实例是最近的组件祖先。
    /// 只在镜像结构变化后重建一次（不是每帧），最多列出 <see cref="ComponentTreeBuilder.MaxInstances"/> 个实例。
    /// </summary>
    public IReadOnlyList<ComponentInstance> Components
    {
        get
        {
            EnsureComponents();
            return _components!;
        }
    }

    /// <summary>构建出的组件实例总数（达到上限时是列出的数量）。</summary>
    public int ComponentCount
    {
        get
        {
            EnsureComponents();
            return _componentCount;
        }
    }

    /// <summary>组件实例数超过上限，列表被截断。</summary>
    public bool ComponentsTruncated
    {
        get
        {
            EnsureComponents();
            return _componentsTruncated;
        }
    }

    /// <summary>组件页里最近一次点击的实例对应的节点 id；0 表示没有。</summary>
    public long SelectedComponentNodeId { get; private set; }

    /// <summary>组件页里最近一次点击的实例名。</summary>
    public string? SelectedComponentName { get; private set; }

    public StylesSnapshot? Styles { get; private set; }
    public List<ComputedEntry>? Computed { get; private set; }
    public BoxSnapshot? Box { get; private set; }
    public string? StylesError { get; private set; }

    /// <summary><c>Css.getStyleSheets</c> 的结果；null 表示尚未加载。</summary>
    public List<StyleSheetEntry>? StyleSheets { get; private set; }

    /// <summary>样式表列表加载失败的原因（可见地显示在样式表页里）。</summary>
    public string? StyleSheetsError { get; private set; }

    /// <summary>当前在样式表页里选中的样式表 id；null 表示没有选中。</summary>
    public string? SelectedSheetId { get; private set; }

    /// <summary>选中样式表的服务端文本（最近一次加载 / 应用成功的版本）。</summary>
    public string SheetText { get; private set; } = "";

    /// <summary>编辑框的当前内容（受控输入的值；每次输入更新，不触发重建）。</summary>
    public string SheetDraft { get; set; } = "";

    /// <summary>编辑框内容与服务端文本不同。</summary>
    public bool SheetDirty => !string.Equals(SheetDraft, SheetText, StringComparison.Ordinal);

    /// <summary>样式表文本正在加载。</summary>
    public bool SheetTextLoading { get; private set; }

    /// <summary>样式表文本加载 / 应用失败的原因（协议错误、只读等）。</summary>
    public string? SheetTextError { get; private set; }

    /// <summary>应用（Apply）请求在途。</summary>
    public bool SheetApplying { get; private set; }

    /// <summary>最近一次 <c>Css.setStyleSheetText</c> 返回的诊断；null 表示还没有应用过。</summary>
    public List<StyleSheetDiagnostic>? SheetDiagnostics { get; private set; }

    /// <summary>外部修改了当前样式表，但本地有未保存的编辑（不覆盖编辑框）。</summary>
    public bool SheetExternalChange { get; private set; }

    /// <summary>被强制的伪状态（见 <see cref="ForceablePseudoStates"/>）。选中变化或会话断开时清空。</summary>
    public IEnumerable<string> ForcedPseudoStates => _forcedPseudoStates;

    /// <summary><c>Css.forcePseudoState</c> 的错误（例如域未启用）。</summary>
    public string? PseudoError { get; private set; }

    /// <summary>搜索框的当前文本（受控值）。</summary>
    public string SearchQuery { get; private set; } = "";

    /// <summary>最近一次 <c>Tree.search</c> 的结果；null 表示没有打开的搜索结果。</summary>
    public List<SearchResult>? SearchResults { get; private set; }

    /// <summary>搜索结果被协议层或面板截断。</summary>
    public bool SearchTruncated { get; private set; }

    /// <summary>搜索请求在途。</summary>
    public bool Searching { get; private set; }

    /// <summary>搜索失败的原因（可见地显示在结果列表里）。</summary>
    public string? SearchError { get; private set; }

    /// <summary>属性查看器里正在编辑的字段；null 表示没有编辑。</summary>
    public AttributeEditState? AttributeEditing { get; private set; }

    /// <summary>属性编辑框的当前内容（受控输入的值）。</summary>
    public string AttributeDraft { get; set; } = "";

    /// <summary>属性编辑失败的原因（只读属性、非法名字等）。</summary>
    public string? AttributesError { get; private set; }

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

    /// <summary>
    /// 加载样式表列表（<c>Css.getStyleSheets</c>），用于匹配规则的来源名与样式表编辑页。
    /// 第一次得到列表时自动选中第一张样式表并加载它的文本；失败时保留旧列表并在样式表页里显示错误。
    /// </summary>
    void LoadStyleSheets()
    {
        Call("Css.getStyleSheets", null, r =>
        {
            if (r.IsError)
            {
                StyleSheetsError = DescribeProtocolError("Loading style sheets", r);
                BumpSheets();
                return;
            }
            if (r.ResultObject["sheets"] is not JsonArray sheets)
            {
                StyleSheetsError = "Css.getStyleSheets returned no sheet list.";
                BumpSheets();
                return;
            }
            StyleSheetsError = null;
            _sheetNames.Clear();
            var list = new List<StyleSheetEntry>(sheets.Count);
            foreach (var s in sheets)
            {
                if (s is not JsonObject so || so.GetStringOrDefault("sheetId") is not { } id) continue;
                var entry = new StyleSheetEntry(id)
                {
                    Origin = so.GetStringOrDefault("origin") ?? "author",
                    SourceUrl = so.GetStringOrDefault("sourceUrl"),
                    Title = so.GetStringOrDefault("title"),
                    Disabled = so["disabled"] is JsonBoolean db && db.GetBoolean(),
                    Editable = so["editable"] is not JsonBoolean eb || eb.GetBoolean(),
                    OwnerNodeId = so["ownerNodeId"] is JsonNumber on && on.TryGetInt64(out long owner) ? owner : 0,
                };
                list.Add(entry);
                _sheetNames[id] = entry.DisplayName;
            }
            StyleSheets = list;
            if (SelectedSheetId is { } selected && list.Find(e => e.SheetId == selected) is null)
            {
                _sheetTextToken++;
                SelectedSheetId = null;
                SheetText = "";
                SheetDraft = "";
                SheetTextError = null;
                SheetDiagnostics = null;
                SheetTextLoading = false;
                SheetExternalChange = false;
            }
            if (SelectedSheetId is null && list.Count > 0)
            {
                // 与浏览器一致：打开样式表页时显示第一张样式表，不必先点一项
                SelectSheetCore(list[0].SheetId);
            }
            BumpSheets();
        });
    }

    /// <summary>重新请求样式表列表（样式表页的 Reload 按钮）。</summary>
    public void ReloadStyleSheets()
    {
        StyleSheetsError = null;
        LoadStyleSheets();
    }

    void BumpSheets() => SheetsRevision.Value++;

    /// <summary>选中一张样式表并请求它的文本（<c>Css.getStyleSheetText</c>）。</summary>
    public void SelectStyleSheet(string? sheetId)
    {
        if (string.IsNullOrEmpty(sheetId) || SelectedSheetId == sheetId) return;
        SelectSheetCore(sheetId!);
    }

    void SelectSheetCore(string sheetId)
    {
        SelectedSheetId = sheetId;
        SheetText = "";
        SheetDraft = "";
        SheetTextError = null;
        SheetDiagnostics = null;
        SheetExternalChange = false;
        SheetTextLoading = true;
        BumpSheets();
        LoadStyleSheetText(sheetId, preserveDraft: false);
    }

    /// <summary>输入框内容变化（不改变控件里的文本，只更新草稿与“未保存”状态）。</summary>
    public void SetSheetDraft(string? text)
    {
        SheetDraft = text ?? "";
        BumpSheets();
    }

    void LoadStyleSheetText(string sheetId, bool preserveDraft)
    {
        int token = ++_sheetTextToken;
        SheetTextLoading = true;
        BumpSheets();
        Call("Css.getStyleSheetText", new JsonObject { ["sheetId"] = sheetId }, r =>
        {
            if (token != _sheetTextToken) return;
            SheetTextLoading = false;
            if (r.IsError)
            {
                SheetTextError = DescribeProtocolError("Loading style sheet", r);
                BumpSheets();
                return;
            }
            string text = r.ResultObject.GetStringOrDefault("text") ?? "";
            SheetText = text;
            if (!preserveDraft) SheetDraft = text;
            SheetTextError = null;
            SheetExternalChange = false;
            BumpSheets();
        });
    }

    /// <summary>
    /// 应用编辑框里的文本（<c>Css.setStyleSheetText</c>）。诊断（severity / message / 行列）保留并显示；
    /// 协议错误时保留用户文本，不清空编辑框。
    /// </summary>
    public void ApplyStyleSheet()
    {
        if (SelectedSheetId is not { } sheetId) return;
        var entry = StyleSheets?.Find(e => e.SheetId == sheetId);
        if (entry is null)
        {
            SheetTextError = "The selected style sheet is no longer available.";
            BumpSheets();
            return;
        }
        if (!entry.Editable)
        {
            SheetTextError = "This style sheet is read-only.";
            BumpSheets();
            return;
        }
        string text = SheetDraft ?? "";
        SheetApplying = true;
        SheetTextError = null;
        SheetDiagnostics = null;
        BumpSheets();
        Call("Css.setStyleSheetText", new JsonObject { ["sheetId"] = sheetId, ["text"] = text }, r =>
        {
            SheetApplying = false;
            if (r.IsError)
            {
                SheetTextError = DescribeProtocolError("Saving style sheet", r);
                BumpSheets();
                return;
            }
            // 服务端已接受文本（即使有诊断）：保留用户文本，更新基线并显示诊断
            SheetText = text;
            SheetExternalChange = false;
            SheetDiagnostics = ParseDiagnostics(r.ResultObject["diagnostics"]);
            BumpSheets();
            LoadStyleSheets();
        });
    }

    static List<StyleSheetDiagnostic> ParseDiagnostics(JsonValue? value)
    {
        var list = new List<StyleSheetDiagnostic>();
        if (value is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            list.Add(new StyleSheetDiagnostic
            {
                Severity = o.GetStringOrDefault("severity") ?? "info",
                Message = o.GetStringOrDefault("message") ?? "",
                Code = o.GetStringOrDefault("code"),
                Line = o["line"] is JsonNumber ln && ln.TryGetInt64(out long line) ? (int)Math.Min(int.MaxValue, Math.Max(0, line)) : 0,
                Column = o["column"] is JsonNumber cn && cn.TryGetInt64(out long column) ? (int)Math.Min(int.MaxValue, Math.Max(0, column)) : 0,
            });
        }
        return list;
    }

    /// <summary>组件实例树被重新构建的次数（测试用：同一结构版本下不应每帧重建）。</summary>
    internal int ComponentsBuildCount { get; private set; }

    void EnsureComponents()
    {
        int version = Tree.StructureVersion;
        if (_components is not null && _componentsVersion == version) return;
        ComponentsBuildCount++;
        _components = new List<ComponentInstance>();
        _componentCount = ComponentTreeBuilder.Build(Tree, ComponentTreeBuilder.MaxInstances, _components, out _componentsTruncated);
        _componentsVersion = version;
    }

    /// <summary>
    /// 点击组件页的实例：在元素树里展开祖先并选中对应节点（复用 <see cref="RevealAndSelect"/>），
    /// 同时记住实例名与节点 id 供组件页显示详情。
    /// </summary>
    public void SelectComponent(ComponentInstance? instance)
    {
        if (instance is null) return;
        SelectedComponentNodeId = instance.Node.Id;
        SelectedComponentName = instance.Name;
        RevealAndSelect(instance.Node.Id);
        BumpTree();
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
            ResetForcedPseudoStates(Selected is null ? 0 : StyledId(Selected), notify: true);
            Selected = null;
            CancelAttributeEdit();
            AttributesError = null;
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
                    CancelAttributeEdit();
                    AttributesError = null;
                    ResetForcedPseudoStates(StyledId(sel), notify: true);
                    ClearStyles();
                    SelectionChanged?.Invoke(null);
                }
                // 不在这里夹紧滚动位置：那要摊平整个行列表，一帧里几百条结构事件就是「事件数 × 行数」的工作量。
                // 推迟到树视图下一次重建（每帧至多一次）
                if (Tree.StructureVersion != structure) _clampPending = true;
                BumpTree();
                // 结构或文本变化可能改变选中元素的盒；属性 / 伪状态变化另由 computedStyleInvalidated 报告
                if (name is "childrenChanged" or "childMoved" or "textChanged") ScheduleRefresh();
                if (name is "attributesChanged" or "pseudoStateChanged") BumpStyles();
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
                    if (name == "styleSheetChanged" && e.Params.GetStringOrDefault("sheetId") is { } changed && changed == SelectedSheetId)
                    {
                        // 自己 Apply 引起的事件：响应会带回新文本与诊断，不能用它覆盖正在编辑的草稿
                        if (!SheetApplying)
                        {
                            if (SheetDirty)
                            {
                                SheetExternalChange = true;
                                BumpSheets();
                            }
                            else LoadStyleSheetText(changed, preserveDraft: false);
                        }
                    }
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
        // 协议层在断开时已经撤销强制伪状态，面板只需复位开关
        ResetForcedPseudoStates(0, notify: false);
        SearchError = null;
        Searching = false;
        AttributeEditing = null;
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

    void BumpSearch() => SearchRevision.Value++;

    static string DescribeProtocolError(string action, DevToolsResponse r) =>
        r.ErrorCode == DevToolsErrorCodes.NotSupported
            ? action + " is read-only: " + r.ErrorMessage
            : action + " failed (" + r.ErrorCode.ToString(CultureInfo.InvariantCulture) + "): " + r.ErrorMessage;

    // ------------------------------------------------------------ 强制伪状态

    public bool IsPseudoStateForced(string state) => _forcedPseudoStates.Contains(state);

    /// <summary>
    /// 切换一个强制伪状态并调用 <c>Css.forcePseudoState</c>（替换整个集合；全部取消时发空数组）。
    /// 失败时恢复之前的集合并显示错误，而不是默默地什么也不做。
    /// </summary>
    public void TogglePseudoState(string state)
    {
        if (Selected is not { } node || string.IsNullOrEmpty(state)) return;
        if (!IsForceable(state)) return;
        CancelEdit();
        CancelAttributeEdit();
        var previous = new HashSet<string>(_forcedPseudoStates, StringComparer.Ordinal);
        if (!_forcedPseudoStates.Remove(state)) _forcedPseudoStates.Add(state);
        PseudoError = null;
        BumpStyles();
        SendForcedPseudoStates(StyledId(node), previous);
    }

    /// <summary>清空全部强制伪状态（<c>Css.forcePseudoState</c> 收到空数组）。</summary>
    public void ClearPseudoStates()
    {
        if (Selected is null) return;
        CancelEdit();
        CancelAttributeEdit();
        var previous = new HashSet<string>(_forcedPseudoStates, StringComparer.Ordinal);
        if (_forcedPseudoStates.Count == 0) return;
        PseudoError = null;
        BumpStyles();
        long id = StyledId(Selected);
        _forcedPseudoStates.Clear();
        SendForcedPseudoStates(id, previous);
    }

    static bool IsForceable(string state)
    {
        foreach (var s in ForceablePseudoStates)
            if (string.Equals(s, state, StringComparison.Ordinal)) return true;
        return false;
    }

    void SendForcedPseudoStates(long nodeId, HashSet<string> previous)
    {
        var states = new JsonArray();
        foreach (var state in ForceablePseudoStates)
            if (_forcedPseudoStates.Contains(state)) states.Add(state);
        Call("Css.forcePseudoState", new JsonObject { ["nodeId"] = nodeId, ["states"] = states }, r =>
        {
            if (!r.IsError) return;
            _forcedPseudoStates.Clear();
            foreach (var s in previous) _forcedPseudoStates.Add(s);
            PseudoError = DescribeProtocolError("Forcing pseudo-states", r);
            BumpStyles();
        });
    }

    void ResetForcedPseudoStates(long nodeId, bool notify)
    {
        if (_forcedPseudoStates.Count == 0)
        {
            PseudoError = null;
            return;
        }
        _forcedPseudoStates.Clear();
        PseudoError = null;
        if (notify && Connected && nodeId != 0)
            Call("Css.forcePseudoState", new JsonObject { ["nodeId"] = nodeId, ["states"] = new JsonArray() }, null);
    }

    // ------------------------------------------------------------ 元素搜索

    /// <summary>搜索框内容变化：空查询立即清空结果；非空查询合并到一次 <c>Tree.search</c>（去抖）。</summary>
    public void SetSearchQuery(string? query)
    {
        query ??= "";
        if (string.Equals(query, SearchQuery, StringComparison.Ordinal) && SearchResults is not null) return;
        SearchQuery = query;
        if (query.Trim().Length == 0)
        {
            _searchToken++;
            SearchResults = null;
            SearchError = null;
            Searching = false;
            SearchTruncated = false;
            BumpSearch();
            return;
        }
        ScheduleSearch();
    }

    /// <summary>回车：立即执行（不等待去抖）。</summary>
    public void SubmitSearch(string? query)
    {
        if (query is not null && !string.Equals(query, SearchQuery, StringComparison.Ordinal)) SearchQuery = query;
        RunSearch(SearchQuery);
    }

    void ScheduleSearch()
    {
        BumpSearch();
        if (_searchScheduled) return;
        _searchScheduled = true;
        _dispatcher.Post(() =>
        {
            _searchScheduled = false;
            if (!_disposed) RunSearch(SearchQuery);
        });
    }

    /// <summary>执行 <c>Tree.search</c>；过期的响应按令牌丢弃。</summary>
    public void RunSearch(string? query)
    {
        if (_disposed) return;
        query = (query ?? "").Trim();
        if (query.Length == 0)
        {
            _searchToken++;
            SearchQuery = "";
            SearchResults = null;
            SearchError = null;
            Searching = false;
            SearchTruncated = false;
            BumpSearch();
            return;
        }
        int token = ++_searchToken;
        Searching = true;
        SearchError = null;
        SearchResults = null;
        SearchTruncated = false;
        BumpSearch();
        Call("Tree.search", new JsonObject { ["query"] = query }, r =>
        {
            if (token != _searchToken) return;
            Searching = false;
            if (r.IsError)
            {
                SearchError = "Search failed: " + r.ErrorMessage;
                BumpSearch();
                return;
            }
            var results = new List<SearchResult>();
            bool truncated = r.ResultObject["truncated"] is JsonBoolean tb && tb.GetBoolean();
            if (r.ResultObject["results"] is JsonArray arr)
                foreach (var item in arr)
                {
                    if (results.Count >= MaxSearchResults)
                    {
                        truncated = true;
                        break;
                    }
                    if (item is not JsonObject o || o["nodeId"] is not JsonNumber n || !n.TryGetInt64(out long id)) continue;
                    string match = o.GetStringOrDefault("match") ?? "";
                    results.Add(new SearchResult(id, match, SearchLabel(id, match, query)));
                }
            SearchResults = results;
            SearchTruncated = truncated;
            BumpSearch();
        });
    }

    /// <summary>结果行的短标签：镜像里有该节点时用 <c>tag#id.class</c>，否则按命中原因与查询生成。</summary>
    string SearchLabel(long id, string match, string query)
    {
        if (Tree.Get(id) is not null) return Describe(id);
        return match switch
        {
            "id" => "#" + query,
            "class" => "." + query,
            "tag" => "<" + query + ">",
            "text" => "\"" + query + "\"",
            "selector" => query,
            _ => "node " + id.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>选中搜索结果：收起列表，展开祖先、选中并滚动到应用里的元素（<c>Tree.scrollIntoView</c>）。</summary>
    public void SelectSearchResult(SearchResult result)
    {
        if (result is null || _disposed) return;
        SearchResults = null;
        SearchTruncated = false;
        SearchError = null;
        BumpSearch();
        RevealAndSelect(result.NodeId);
        Call("Tree.scrollIntoView", new JsonObject { ["nodeId"] = result.NodeId }, null);
    }

    public void ClearSearch() => SetSearchQuery("");

    // ------------------------------------------------------------ 属性与 class 编辑

    public void BeginIdEdit()
    {
        if (Selected is not { } node || node.IsText) return;
        BeginAttributeEditCore(new AttributeEditState(AttributeEditKind.Id, "id"), node.ElementId ?? "");
    }

    public void BeginClassEdit()
    {
        if (Selected is not { } node || node.IsText) return;
        var classes = node.Classes;
        BeginAttributeEditCore(new AttributeEditState(AttributeEditKind.Class, "class"), classes is { Length: > 0 } ? string.Join(" ", classes) : "");
    }

    public void BeginAttributeEdit(string name, string value)
    {
        if (Selected is not { } node || node.IsText) return;
        BeginAttributeEditCore(new AttributeEditState(AttributeEditKind.Attribute, name), value ?? "");
    }

    public void BeginAddAttribute()
    {
        if (Selected is not { } node || node.IsText) return;
        BeginAttributeEditCore(new AttributeEditState(AttributeEditKind.NewAttribute), "");
    }

    void BeginAttributeEditCore(AttributeEditState edit, string draft)
    {
        CancelEdit();
        AttributeEditing = edit;
        AttributeDraft = draft;
        AttributesError = null;
        BumpStyles();
    }

    public void CancelAttributeEdit()
    {
        if (AttributeEditing is null) return;
        AttributeEditing = null;
        BumpStyles();
    }

    /// <summary>
    /// 提交属性编辑：空值删除该属性；class 用 <c>Tree.setClasses</c>，其余用 <c>Tree.setAttribute</c>。
    /// 成功后从 <c>Tree.describeNode</c> 刷新该行；失败（含只读属性的 -32004）显示错误。
    /// </summary>
    public void CommitAttributeEdit(string? text)
    {
        if (AttributeEditing is not { } edit) return;
        if (Selected is not { } node || node.IsText)
        {
            AttributeEditing = null;
            BumpStyles();
            return;
        }
        AttributeEditing = null;
        AttributesError = null;
        long id = node.Id;
        string value = text ?? "";
        switch (edit.Kind)
        {
            case AttributeEditKind.Id:
            {
                string trimmed = value.Trim();
                if (trimmed.Length == 0) SendRemoveAttribute(id, "id");
                else SendSetAttribute(id, "id", trimmed);
                break;
            }
            case AttributeEditKind.Class:
                SendSetClasses(id, ParseClasses(value));
                break;
            case AttributeEditKind.NewAttribute:
            {
                string t = value.Trim();
                int sep = t.IndexOf('=');
                if (sep < 0) sep = t.IndexOf(':');
                if (sep <= 0)
                {
                    AttributesError = "Expected \"name=value\".";
                    BumpStyles();
                    return;
                }
                string name = t.Substring(0, sep).Trim();
                string v = t.Substring(sep + 1).Trim();
                if (name.Length == 0)
                {
                    AttributesError = "Attribute name must not be empty.";
                    BumpStyles();
                    return;
                }
                SendSetAttribute(id, name, v);
                break;
            }
            default:
            {
                string name = edit.Name ?? "";
                if (value.Length == 0) SendRemoveAttribute(id, name);
                else SendSetAttribute(id, name, value);
                break;
            }
        }
        BumpStyles();
    }

    /// <summary>删除一个属性（class 转为空的 class 列表；id 通过 <c>Tree.removeAttribute</c>）。</summary>
    public void RemoveAttribute(string name)
    {
        if (Selected is not { } node || node.IsText || string.IsNullOrEmpty(name)) return;
        CancelEdit();
        AttributeEditing = null;
        AttributesError = null;
        if (name == "class") SendSetClasses(node.Id, new List<string>());
        else SendRemoveAttribute(node.Id, name);
        BumpStyles();
    }

    static List<string> ParseClasses(string? text)
    {
        var list = new List<string>();
        if (text is null) return list;
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool space = i == text.Length || char.IsWhiteSpace(text[i]);
            if (!space && start < 0) start = i;
            else if (space && start >= 0)
            {
                string c = text.Substring(start, i - start);
                if (!list.Contains(c)) list.Add(c);
                start = -1;
            }
        }
        return list;
    }

    void SendSetAttribute(long nodeId, string name, string value) =>
        Call("Tree.setAttribute", new JsonObject { ["nodeId"] = nodeId, ["name"] = name, ["value"] = value },
            r => AfterAttributeRequest(nodeId, r, "attribute"));

    void SendRemoveAttribute(long nodeId, string name) =>
        Call("Tree.removeAttribute", new JsonObject { ["nodeId"] = nodeId, ["name"] = name },
            r => AfterAttributeRequest(nodeId, r, "attribute"));

    void SendSetClasses(long nodeId, List<string> classes)
    {
        var array = new JsonArray();
        foreach (var c in classes) array.Add(c);
        Call("Tree.setClasses", new JsonObject { ["nodeId"] = nodeId, ["classes"] = array },
            r => AfterAttributeRequest(nodeId, r, "class list"));
    }

    void AfterAttributeRequest(long nodeId, DevToolsResponse r, string what)
    {
        if (r.IsError)
        {
            AttributesError = DescribeProtocolError("Editing " + what, r);
            BumpStyles();
            return;
        }
        AttributesError = null;
        RefreshNodeDescription(nodeId);
    }

    /// <summary>编辑成功后用 <c>Tree.describeNode</c> 刷新镜像里的这一行（不改变树结构）。</summary>
    void RefreshNodeDescription(long nodeId)
    {
        Call("Tree.describeNode", new JsonObject { ["nodeId"] = nodeId }, r =>
        {
            if (r.IsError)
            {
                AttributesError = "Refresh failed: " + r.ErrorMessage;
                BumpStyles();
                return;
            }
            if (r.ResultObject["node"] is JsonObject node)
            {
                Tree.UpdateNode(node);
                BumpTree();
                ChromeRevision.Value++;
            }
            BumpStyles();
            RefreshStyles();
        });
    }

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

    /// <summary>协议允许强制的伪状态（与 <see cref="StyleInspector.ForceablePseudoStates"/> 一致）。</summary>
    public static IReadOnlyList<string> ForceablePseudoStates => StyleInspector.ForceablePseudoStates;

    void SelectNode(DomNode? node, bool scroll, bool highlight)
    {
        if (node is not null && Tree.Get(node.Id) != node) node = null;
        bool changed = !ReferenceEquals(Selected, node);
        // 选中变化：旧节点的强制伪状态在应用里撤销，本地开关也随之复位（同浏览器）
        if (changed) ResetForcedPseudoStates(Selected is null ? 0 : StyledId(Selected), notify: true);
        CancelEdit();
        CancelAttributeEdit();
        Selected = node;
        if (changed && SelectedComponentNodeId != 0 && (node is null || node.Id != SelectedComponentNodeId))
        {
            SelectedComponentNodeId = 0;
            SelectedComponentName = null;
        }
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
            AttributesError = null;
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
        // 打开样式表页时刷新列表（选择项由列表回调按需加载文本）
        if (tab == SideTab.Stylesheets) LoadStyleSheets();
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

    /// <summary>撤销本面板对应用样式的全部修改（<c>Css.clearOverrides</c>），并复位伪状态开关。</summary>
    public void ClearOverrides()
    {
        CancelEdit();
        CancelAttributeEdit();
        _forcedPseudoStates.Clear();
        PseudoError = null;
        BumpStyles();
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

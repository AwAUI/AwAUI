using System.Collections.Concurrent;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;
using AwAUI.Threading;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>专用线程上的调度器，模拟真实的 UI 线程。</summary>
sealed class ThreadDispatcher : IDispatcher, IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;

    public ThreadDispatcher()
    {
        _thread = new Thread(() =>
        {
            foreach (var a in _queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Errors.Add(ex); }
            }
        }) { IsBackground = true, Name = "Fake UI thread" };
        _thread.Start();
    }

    public ConcurrentBag<Exception> Errors { get; } = new();
    public bool CheckAccess() => Thread.CurrentThread == _thread;
    public int QueueCount => _queue.Count;
    public void Post(Action action) => _queue.Add(action);

    public Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

sealed class FakeNode
{
    public long Id;
    public string Tag = "div";
    public bool IsText;
    public string? Text;
    public string? ElementId;
    public List<string> Classes = new();
    public List<KeyValuePair<string, string>> Attributes = new();
    public FakeNode? Parent;
    public List<FakeNode> Children = new();
    public List<string> Pseudo = new();
    public Rect Box;
    public long ComponentId;
    public string? ComponentName;
    public SourceLocation? Source;
    public bool NoBox;
    public (float X, float Y)? Scroll;

    public string? GetAttr(string name)
    {
        foreach (var kv in Attributes) if (kv.Key == name) return kv.Value;
        return null;
    }
}

/// <summary>内存元素树：实现全部接缝方法，并在每次调用时检查线程（设置了 <see cref="UIThread"/> 时）。</summary>
class FakeTree : InspectableTree
{
    readonly Dictionary<long, FakeNode> _nodes = new();
    long _next = 100;
    int _threadViolations;

    public FakeTree()
    {
        Root = Create("app");
        Root.ElementId = "root";
    }

    public FakeNode Root { get; private set; }
    public IDispatcher? UIThread { get; set; }
    public int ThreadViolations => Volatile.Read(ref _threadViolations);
    public int DescribeCalls;

    void Check()
    {
        if (UIThread != null && !UIThread.CheckAccess()) Interlocked.Increment(ref _threadViolations);
    }

    public FakeNode Create(string tag)
    {
        var n = new FakeNode { Id = ++_next, Tag = tag, Box = new Rect(0, 0, 100, 20) };
        _nodes[n.Id] = n;
        return n;
    }

    public FakeNode CreateText(string text)
    {
        var n = Create("#text");
        n.IsText = true;
        n.Text = text;
        return n;
    }

    public FakeNode this[long id] => _nodes[id];

    /// <summary>不发通知地追加（构造初始树用）。</summary>
    public FakeNode Add(FakeNode parent, FakeNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
        return child;
    }

    public FakeNode Add(FakeNode parent, string tag, params string[] classes)
    {
        var c = Add(parent, Create(tag));
        c.Classes.AddRange(classes);
        return c;
    }

    // ---- 变更 + 通知 ----

    public FakeNode Insert(FakeNode parent, FakeNode child, FakeNode? before = null)
    {
        child.Parent = parent;
        int i = before is null ? parent.Children.Count : parent.Children.IndexOf(before);
        parent.Children.Insert(i, child);
        var stack = new Stack<FakeNode>();
        stack.Push(child);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            _nodes[x.Id] = x;
            foreach (var c in x.Children) stack.Push(c);
        }
        NotifyChildInserted(parent.Id, child.Id, before?.Id ?? 0);
        return child;
    }

    public void Remove(FakeNode child)
    {
        var parent = child.Parent!;
        parent.Children.Remove(child);
        child.Parent = null;
        Forget(child);
        NotifyChildRemoved(parent.Id, child.Id);
    }

    void Forget(FakeNode n)
    {
        var stack = new Stack<FakeNode>();
        stack.Push(n);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            _nodes.Remove(x.Id);
            foreach (var c in x.Children) stack.Push(c);
        }
    }

    public void Move(FakeNode child, FakeNode? before)
    {
        var parent = child.Parent!;
        parent.Children.Remove(child);
        int i = before is null ? parent.Children.Count : parent.Children.IndexOf(before);
        parent.Children.Insert(i, child);
        NotifyChildMoved(parent.Id, child.Id, before?.Id ?? 0);
    }

    public void SetAttr(FakeNode n, string name, string? value)
    {
        if (name == "id") n.ElementId = value;
        else if (name == "class") { n.Classes.Clear(); if (value != null) n.Classes.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)); }
        else
        {
            n.Attributes.RemoveAll(kv => kv.Key == name);
            if (value != null) n.Attributes.Add(new(name, value));
        }
        NotifyAttributeChanged(n.Id, name);
    }

    public void SetText(FakeNode n, string text)
    {
        n.Text = text;
        NotifyTextChanged(n.Id);
    }

    public void SetPseudo(FakeNode n, params string[] states)
    {
        n.Pseudo = states.ToList();
        NotifyPseudoStatesChanged(n.Id);
    }

    public void ReplaceRoot(FakeNode newRoot)
    {
        Forget(Root);
        _nodes[newRoot.Id] = newRoot;
        Root = newRoot;
        NotifyDocumentUpdated();
    }

    // ---- 接缝 ----

    public override long RootId { get { Check(); return Root.Id; } }

    public override InspectableNodeInfo? DescribeNode(long nodeId)
    {
        Check();
        Interlocked.Increment(ref DescribeCalls);
        if (!_nodes.TryGetValue(nodeId, out var n)) return null;
        return new InspectableNodeInfo(n.Id, n.IsText ? InspectableNodeKind.Text : InspectableNodeKind.Element)
        {
            ParentId = n.Parent?.Id ?? 0,
            Tag = n.IsText ? null : n.Tag,
            ElementId = n.ElementId,
            Classes = n.Classes.ToArray(),
            Attributes = n.Attributes.ToArray(),
            Text = n.Text,
            ComponentId = n.ComponentId,
            ComponentName = n.ComponentName,
            PseudoStates = n.Pseudo.ToArray(),
            ChildCount = n.Children.Count,
            Source = n.Source,
        };
    }

    public override bool ContainsNode(long nodeId)
    {
        Check();
        return _nodes.ContainsKey(nodeId);
    }

    public override bool GetChildren(long nodeId, List<long> children)
    {
        Check();
        if (!_nodes.TryGetValue(nodeId, out var n)) return false;
        foreach (var c in n.Children) children.Add(c.Id);
        return true;
    }

    public override BoxModelInfo? GetBoxModel(long nodeId)
    {
        Check();
        var n = _nodes[nodeId];
        if (n.IsText || n.NoBox) return null;
        var b = n.Box;
        var box = new BoxModelInfo(b.Inflate(5), b, b.Inflate(-1), b.Inflate(-3));
        if (n.Scroll is { } s)
        {
            box.ScrollOffset = new Point(s.X, s.Y);
            box.ScrollSize = new Size(b.Width, b.Height * 10);
        }
        return box;
    }

    /// <summary>极简选择器：<c>*</c>、<c>tag</c>、<c>.cls</c>、<c>#id</c> 及其复合（如 <c>button.primary</c>）。</summary>
    public override void QuerySelectorAll(long scopeNodeId, string selector, List<long> results)
    {
        Check();
        var match = Parse(selector);
        var stack = new Stack<FakeNode>();
        var scope = _nodes[scopeNodeId];
        for (int i = scope.Children.Count - 1; i >= 0; i--) stack.Push(scope.Children[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsText && match(n)) results.Add(n.Id);
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    static Func<FakeNode, bool> Parse(string selector)
    {
        selector = selector.Trim();
        if (selector.Length == 0) throw new FormatException("Empty selector.");
        if (selector == "*") return _ => true;
        string? tag = null, id = null;
        var classes = new List<string>();
        int i = 0;
        string Ident()
        {
            int start = i;
            while (i < selector.Length && (char.IsLetterOrDigit(selector[i]) || selector[i] is '-' or '_')) i++;
            if (i == start) throw new FormatException($"Unexpected '{(i < selector.Length ? selector[i] : ' ')}' in selector at {i}.");
            return selector.Substring(start, i - start);
        }
        if (char.IsLetter(selector[0])) tag = Ident();
        while (i < selector.Length)
        {
            char c = selector[i++];
            if (c == '.') classes.Add(Ident());
            else if (c == '#') id = Ident();
            else throw new FormatException($"Unexpected '{c}' in selector at {i - 1}.");
        }
        return n => (tag is null || n.Tag == tag) && (id is null || n.ElementId == id) && classes.All(n.Classes.Contains);
    }

    public override long GetNodeAt(Point point, bool ignorePointerEvents)
    {
        Check();
        long hit = 0;
        var stack = new Stack<FakeNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsText && !n.NoBox && n.Box.Contains(point) && (ignorePointerEvents || n.GetAttr("pointer-events") != "none")) hit = n.Id;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
        return hit;
    }

    public override bool SetAttribute(long nodeId, string name, string? value)
    {
        Check();
        if (!_nodes.TryGetValue(nodeId, out var n)) return false;
        if (name.Contains(' ')) throw new FormatException("Invalid attribute name.");
        SetAttr(n, name, value);
        return true;
    }

    public override bool SetClasses(long nodeId, IReadOnlyList<string> classes)
    {
        Check();
        if (!_nodes.TryGetValue(nodeId, out var n)) return false;
        n.Classes = classes.ToList();
        NotifyAttributeChanged(nodeId, "class");
        return true;
    }
}

/// <summary>只实现抽象成员的树：可选方法保持默认（不支持）。</summary>
sealed class MinimalTree : InspectableTree
{
    public override long RootId => 1;
    public override InspectableNodeInfo? DescribeNode(long nodeId) =>
        nodeId == 1 ? new InspectableNodeInfo(1, InspectableNodeKind.Element) { Tag = "root" } : null;
    public override bool GetChildren(long nodeId, List<long> children) => nodeId == 1;
    public override BoxModelInfo? GetBoxModel(long nodeId) => null;
    public override void QuerySelectorAll(long scopeNodeId, string selector, List<long> results) { }
}

/// <summary>内存样式数据：每个带 class 的元素命中规则 <c>.{class}</c>；内联样式按节点保存。</summary>
sealed class FakeStyles : StyleInspector
{
    readonly FakeTree _tree;
    public readonly Dictionary<long, List<StyleDeclarationInfo>> Inline = new();
    public readonly Dictionary<string, StyleRuleInfo> Rules = new();
    public readonly Dictionary<string, (StyleSheetInfo Info, string Text)> Sheets = new();
    public readonly List<string> Calls = new();
    public readonly Dictionary<long, IReadOnlyList<string>> Forced = new();
    public bool SupportsOptional = true;
    /// <summary>在 ForcePseudoState 内部（已生效后、返回前）执行，用于制造并发。</summary>
    public Action? DuringForce;

    public FakeStyles(FakeTree tree)
    {
        _tree = tree;
        Sheets["s1"] = (new StyleSheetInfo("s1", StyleOrigin.Author) { SourceUrl = "app.css", Title = "App" }, ".a { color: red; }");
        Sheets["ua"] = (new StyleSheetInfo("ua", StyleOrigin.Default) { Editable = false }, "div { display: block; }");
        var r = new StyleRuleInfo("r1", ".a, .b", StyleOrigin.Author, "s1") { Layer = "base", Media = "(min-width: 100px)", Source = new SourceLocation("app.css", 1, 1) };
        r.Declarations.Add(new StyleDeclarationInfo("color", "red") { Status = StyleDeclarationStatus.Overridden, Range = (5, 16) });
        r.Declarations.Add(new StyleDeclarationInfo("flex-grow", "1") { Status = StyleDeclarationStatus.Inactive, Reason = "Parent is not a flex container" });
        Rules["r1"] = r;
    }

    FakeNode? Node(long id)
    {
        try { return _tree[id]; } catch (KeyNotFoundException) { return null; }
    }

    public override MatchedStylesInfo? GetMatchedRules(long nodeId)
    {
        var n = Node(nodeId);
        if (n is null) return null;
        var m = new MatchedStylesInfo();
        if (Inline.TryGetValue(nodeId, out var inl)) m.Inline.AddRange(inl);
        if (n.Classes.Contains("a")) m.Matched.Add(new StyleRuleMatch(Rules["r1"], ".a", new StyleSpecificity(0, 1, 0)));
        if (n.Parent is { } p)
        {
            var inh = new InheritedStyleInfo(p.Id);
            inh.Matched.Add(new StyleRuleMatch(Rules["r1"], ".b", new StyleSpecificity(0, 1, 0)));
            m.Inherited.Add(inh);
        }
        if (n.ElementId == "root")
        {
            var pe = new PseudoElementStyleInfo("before");
            pe.Matched.Add(new StyleRuleMatch(Rules["r1"], ".a", new StyleSpecificity(1, 0, 1)));
            m.PseudoElements.Add(pe);
        }
        return m;
    }

    public override IReadOnlyList<ComputedStyleProperty>? GetComputedStyle(long nodeId)
    {
        var n = Node(nodeId);
        if (n is null) return null;
        return new[]
        {
            new ComputedStyleProperty("color", "#ff0000") { FromRuleId = "r1", FromOrigin = StyleOrigin.Author },
            new ComputedStyleProperty("display", "block") { FromOrigin = StyleOrigin.Default },
            new ComputedStyleProperty("font-family", "sans-serif") { FromNodeId = n.Parent?.Id ?? 0, FromOrigin = StyleOrigin.Author },
            new ComputedStyleProperty("z-index", "auto"),
        };
    }

    public override IReadOnlyList<StyleSheetInfo> GetStyleSheets() => Sheets.Values.Select(s => s.Info).ToList();

    public override string? GetStyleSheetText(string sheetId) => Sheets.TryGetValue(sheetId, out var s) ? s.Text : null;

    static List<StyleDeclarationInfo> ParseBlock(string text)
    {
        if (text.Contains('{') || text.Contains('}')) throw new FormatException("Unexpected '{' in declaration block.");
        var list = new List<StyleDeclarationInfo>();
        foreach (var raw in text.Split(';'))
        {
            var part = raw.Trim();
            bool disabled = false;
            if (part.StartsWith("/*") && part.EndsWith("*/")) { disabled = true; part = part[2..^2].Trim(); }
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon < 0) { list.Add(new StyleDeclarationInfo(part, "") { Status = StyleDeclarationStatus.Invalid, Reason = "Missing ':'" }); continue; }
            string value = part[(colon + 1)..].Trim();
            bool important = value.EndsWith("!important");
            if (important) value = value[..^10].Trim();
            list.Add(new StyleDeclarationInfo(part[..colon].Trim(), value) { Important = important, Disabled = disabled });
        }
        return list;
    }

    public override IReadOnlyList<StyleDeclarationInfo>? SetStyleText(StyleTextTarget target, string text)
    {
        Calls.Add("setStyleText " + target + " " + text);
        var decls = ParseBlock(text);
        if (target.IsInline)
        {
            if (Node(target.NodeId) is null) return null;
            Inline[target.NodeId] = decls;
            NotifyComputedStyleInvalidated(target.NodeId);
            return decls;
        }
        if (!Rules.TryGetValue(target.RuleId!, out var rule)) return null;
        rule.Declarations.Clear();
        rule.Declarations.AddRange(decls);
        NotifyStyleSheetChanged(rule.SheetId);
        return decls;
    }

    public override IReadOnlyList<StyleDiagnostic>? SetStyleSheetText(string sheetId, string text)
    {
        if (!Sheets.TryGetValue(sheetId, out var s)) return null;
        if (!s.Info.Editable) throw new NotSupportedException("The default style sheet is read-only.");
        Sheets[sheetId] = (s.Info, text);
        NotifyStyleSheetChanged(sheetId);
        var diags = new List<StyleDiagnostic>();
        int at = text.IndexOf("bogus", StringComparison.Ordinal);
        if (at >= 0) diags.Add(new StyleDiagnostic(StyleDiagnosticSeverity.Warning, "Unsupported property 'bogus'.") { Code = "AW2002", Line = 1, Column = at + 1 });
        return diags;
    }

    public override StyleRuleInfo AddRule(string selector, string? text)
    {
        if (!SupportsOptional) return base.AddRule(selector, text);
        if (selector.Contains('!')) throw new FormatException("Invalid selector.");
        var rule = new StyleRuleInfo("r" + (Rules.Count + 1), selector, StyleOrigin.DevTools, "devtools");
        if (text != null) rule.Declarations.AddRange(ParseBlock(text));
        Rules[rule.Id] = rule;
        if (!Sheets.ContainsKey("devtools"))
        {
            var info = new StyleSheetInfo("devtools", StyleOrigin.DevTools) { Title = "DevTools" };
            Sheets["devtools"] = (info, "");
            NotifyStyleSheetAdded(info);
        }
        return rule;
    }

    public void RemoveSheet(string id)
    {
        Sheets.Remove(id);
        NotifyStyleSheetRemoved(id);
    }

    public override bool ForcePseudoState(long nodeId, IReadOnlyList<string> states)
    {
        Calls.Add("force " + nodeId + " [" + string.Join(",", states) + "]");
        if (Node(nodeId) is null) return false;
        if (states.Count == 0) Forced.Remove(nodeId);
        else Forced[nodeId] = states;
        DuringForce?.Invoke();
        return true;
    }

    public override bool ClearOverrides(long? nodeId)
    {
        if (!SupportsOptional) return base.ClearOverrides(nodeId);
        Calls.Add("clear " + (nodeId?.ToString() ?? "all"));
        if (nodeId is { } id)
        {
            if (Node(id) is null) return false;
            Inline.Remove(id);
            Forced.Remove(id);
        }
        else
        {
            Inline.Clear();
            Forced.Clear();
        }
        return true;
    }
}

/// <summary>记录调用的叠加层。</summary>
sealed class FakeOverlay : OverlayHost
{
    readonly FakeTree _tree;
    public readonly List<string> Calls = new();
    public HighlightConfig? LastConfig;
    /// <summary>在 HighlightNode 内部（已生效后、返回前）执行，用于制造并发。</summary>
    public Action? DuringHighlight;
    public IDispatcher? UIThread { get; set; }
    public int ThreadViolations;

    public FakeOverlay(FakeTree tree) => _tree = tree;

    void Check()
    {
        if (UIThread != null && !UIThread.CheckAccess()) Interlocked.Increment(ref ThreadViolations);
    }

    public override bool HighlightNode(long nodeId, HighlightConfig config)
    {
        Check();
        if (!_tree.ContainsNode(nodeId)) return false;
        LastConfig = config;
        lock (Calls) Calls.Add("highlight " + nodeId);
        DuringHighlight?.Invoke();
        return true;
    }

    public override void HideHighlight()
    {
        Check();
        lock (Calls) Calls.Add("hide");
    }

    public override void SetInspectMode(InspectMode mode)
    {
        Check();
        lock (Calls) Calls.Add("mode " + mode);
    }
}

/// <summary>一个挂好接缝的 Agent 与若干进程内客户端。</summary>
sealed class InspectionFixture
{
    public readonly DevToolsAgent Agent;
    public readonly DevToolsTarget Target;
    public readonly FakeTree Tree;
    public readonly FakeStyles Styles;
    public readonly FakeOverlay Overlay;

    // root(app#root) ─┬ header.a (component Header #7)
    //                 │   └ "Title" (text)
    //                 ├ main
    //                 │   ├ button.primary.a [type=submit]
    //                 │   │   └ "OK"
    //                 │   └ span
    //                 └ footer
    public FakeNode Header = null!, Title = null!, Main = null!, Button = null!, Ok = null!, Span = null!, Footer = null!;

    public InspectionFixture(DevToolsAgentOptions? options = null, IDispatcher? targetDispatcher = null)
    {
        Agent = new DevToolsAgent(options);
        Tree = new FakeTree();
        Styles = new FakeStyles(Tree);
        Overlay = new FakeOverlay(Tree);
        Build();
        Target = new DevToolsTarget("w1", "Main", new Size(800, 600))
        {
            Dispatcher = targetDispatcher,
            Tree = Tree,
            Styles = Styles,
            Overlay = Overlay,
        };
        Agent.AddTarget(Target);
    }

    void Build()
    {
        var t = Tree;
        Header = t.Add(t.Root, "header", "a");
        Header.ComponentId = 7;
        Header.ComponentName = "Header";
        Header.Source = new SourceLocation("Header.awaui", 3, 5);
        Title = t.Add(Header, t.CreateText("Title"));
        Main = t.Add(t.Root, "main");
        Main.Box = new Rect(0, 20, 400, 200);
        Main.Scroll = (0, 15);
        Button = t.Add(Main, "button", "primary", "a");
        Button.Attributes.Add(new("type", "submit"));
        Button.Box = new Rect(10, 30, 80, 24);
        Ok = t.Add(Button, t.CreateText("OK"));
        Span = t.Add(Main, "span");
        Span.NoBox = true;
        Footer = t.Add(t.Root, "footer");
    }

    public RpcClient Connect() => Connect(out _);

    public RpcClient Connect(out DevToolsSession session)
    {
        var (a, c) = InProcTransport.CreatePair();
        session = Agent.Attach(a);
        return new RpcClient(c);
    }
}

static class RpcExtensions
{
    public static async Task<JsonValue> OkAsync(this RpcClient client, string method, JsonObject? p = null, string? target = null) =>
        Rpc.Result(await client.CallAsync(method, p, target));

    public static async Task<JsonObject> FailAsync(this RpcClient client, string method, JsonObject? p, int code, string? target = null) =>
        Rpc.Error(await client.CallAsync(method, p, target), code);

    public static long Id(this JsonValue v) => v["id"]!.GetInt64();

    public static long[] Ids(this JsonValue arr) => arr.AsArray().Select(n => n["id"]!.GetInt64()).ToArray();

    public static long[] Longs(this JsonValue arr) => arr.AsArray().Select(n => n.GetInt64()).ToArray();
}

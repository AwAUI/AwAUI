using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Selectors;
using AwAUI.Diagnostics;
using AwAUI.Diagnostics.Inspection;

namespace AwAUI.UI;

/// <summary>
/// DevTools <c>Tree</c> / <c>Layout</c> 域的数据源（design/20）：把 <see cref="UIRoot"/> 的元素树呈现为 <see cref="InspectableTree"/>。
/// <list type="bullet">
/// <item>节点 id 即 <see cref="Element.UniqueId"/>（进程内单调递增、不复用）。<see cref="TextElement.Content"/> 呈现为该元素的第一个
/// 文本子节点，id 为 <see cref="ContentIdBase"/> + 元素 id（不会与元素 id 冲突）。</item>
/// <item>按 id 查找用一张随元素树观察者维护的索引（插入 / 移除时遍历该子树，总计线性）。</item>
/// <item>变更经 <see cref="ElementTreeObserver"/> 在修改发生时同步转发为 <c>Notify*</c>（协调器保证插入 / 移动通知的顺序可增量定位，
/// 见 <see cref="ElementTreeObserver.OnChildMoved(Element, Element, Element)"/>）。</item>
/// </list>
/// 只在 UI 线程上使用。
/// </summary>
internal sealed class ElementInspectableTree : InspectableTree
{
    /// <summary><see cref="TextElement.Content"/> 文本子节点的 id 基数（2^52，加元素 id 后仍不超过 2^53−1）。</summary>
    internal const long ContentIdBase = 1L << 52;

    readonly UIRoot _root;
    readonly RenderPipeline _pipeline;
    readonly Dictionary<long, Element> _nodes = new();
    readonly HashSet<long> _contentNodes = new();
    readonly ObjectRegistry _components = new();
    readonly Observer _observer;
    readonly Stack<Element> _walk = new();
    bool _disposed;

    public ElementInspectableTree(UIRoot root, RenderPipeline pipeline)
    {
        _root = root;
        _pipeline = pipeline;
        Index(root.RootElement);
        _observer = new Observer(this);
        root.AddObserver(_observer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _root.RemoveObserver(_observer);
        _nodes.Clear();
        _contentNodes.Clear();
    }

    // ------------------------------------------------------------ 查找

    /// <summary>按 id 找到挂在树上的元素（<see cref="TextElement.Content"/> 子节点返回 null）。</summary>
    public Element? Find(long id) => _nodes.TryGetValue(id, out var e) && ReferenceEquals(e.Root, _root) ? e : null;

    static long ContentId(Element e) => ContentIdBase + e.UniqueId;

    static bool HasContentNode(Element e) => e is TextElement { Content.Length: > 0 };

    /// <summary><see cref="TextElement.Content"/> 文本子节点所属的元素。</summary>
    TextElement? FindContentOwner(long id) =>
        id > ContentIdBase && _contentNodes.Contains(id) && Find(id - ContentIdBase) is TextElement te && HasContentNode(te) ? te : null;

    void Index(Element top)
    {
        var stack = _walk;
        stack.Clear();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            _nodes[e.UniqueId] = e;
            if (HasContentNode(e)) _contentNodes.Add(ContentId(e));
            var ch = e.Children;
            for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
        }
    }

    void Unindex(Element top)
    {
        var stack = _walk;
        stack.Clear();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            _nodes.Remove(e.UniqueId);
            _contentNodes.Remove(ContentId(e));
            var ch = e.Children;
            for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
        }
    }

    // ------------------------------------------------------------ 读取

    public override long RootId => _root.RootElement.UniqueId;

    public override bool ContainsNode(long nodeId) => Find(nodeId) is not null || FindContentOwner(nodeId) is not null;

    public override InspectableNodeInfo? DescribeNode(long nodeId)
    {
        if (Find(nodeId) is { } e) return Describe(e);
        if (FindContentOwner(nodeId) is { } owner)
            return new InspectableNodeInfo(nodeId, InspectableNodeKind.Text) { ParentId = owner.UniqueId, Text = owner.Content };
        return null;
    }

    InspectableNodeInfo Describe(Element e)
    {
        if (e is TextNodeElement t)
        {
            return new InspectableNodeInfo(e.UniqueId, InspectableNodeKind.Text)
            {
                ParentId = e.Parent?.UniqueId ?? 0,
                Text = t.Text,
                ComponentId = ComponentId(e.Owner),
                ComponentName = e.Owner?.GetType().Name,
            };
        }
        return new InspectableNodeInfo(e.UniqueId, InspectableNodeKind.Element)
        {
            ParentId = e.Parent?.UniqueId ?? 0,
            Tag = e.Tag,
            ElementId = e.Id,
            Classes = e.Classes,
            Attributes = AttributesOf(e),
            ComponentId = ComponentId(e.Owner),
            ComponentName = e.Owner?.GetType().Name,
            PseudoStates = StateNames(e.States),
            ChildCount = e.ChildCount + (HasContentNode(e) ? 1 : 0),
        };
    }

    long ComponentId(Component? c) => c is null ? 0 : _components.GetOrAddId(c);

    /// <summary>反射为属性的内建字段（按显示顺序）；自定义属性同名时以自定义属性为准。</summary>
    static readonly string[] s_reflected = { "style", "role", "aria-label", "tabindex", "src", "alt", "type", "value", "placeholder", "disabled", "readonly" };

    List<KeyValuePair<string, string>> AttributesOf(Element e)
    {
        var list = new List<KeyValuePair<string, string>>();
        var custom = e.Attributes;
        foreach (var kv in custom)
        {
            if (string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key, "class", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(kv);
        }
        foreach (var name in s_reflected)
        {
            if (Contains(custom, name)) continue;
            string? v = name == "style" ? EffectiveInlineStyle(e) : e.GetAttribute(name);
            if (v is not null) list.Add(new KeyValuePair<string, string>(name, v));
        }
        // 作用域样式标记（.awaui 组件的元素）：与浏览器中 Vue 的 data-v-xxxx 一样显示为空值属性
        if (e.Scope is { Length: > 0 } scope && !Contains(custom, scope)) list.Add(new KeyValuePair<string, string>(scope, ""));
        return list;
    }

    static bool Contains(IReadOnlyList<KeyValuePair<string, string>> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Key, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>生效的内联样式文本：DevTools 的修改优先于元素自身的文本。</summary>
    internal static string? EffectiveInlineStyle(Element e) => e.Render?.InlineOverride ?? e.InlineStyle;

    static readonly (PseudoStates Flag, string Name)[] s_stateNames =
    {
        (PseudoStates.Hover, "hover"), (PseudoStates.Active, "active"), (PseudoStates.Focus, "focus"),
        (PseudoStates.FocusVisible, "focus-visible"), (PseudoStates.FocusWithin, "focus-within"), (PseudoStates.Disabled, "disabled"),
        (PseudoStates.Checked, "checked"), (PseudoStates.PlaceholderShown, "placeholder-shown"),
    };

    internal static IReadOnlyList<string> StateNames(PseudoStates states)
    {
        if (states == PseudoStates.None) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var (flag, name) in s_stateNames)
            if ((states & flag) != 0) list.Add(name);
        return list;
    }

    internal static PseudoStates ParseStates(IReadOnlyList<string> names)
    {
        var states = PseudoStates.None;
        foreach (var n in names)
            foreach (var (flag, name) in s_stateNames)
                if (string.Equals(n, name, StringComparison.Ordinal)) states |= flag;
        return states;
    }

    public override bool GetChildren(long nodeId, List<long> children)
    {
        var e = Find(nodeId);
        if (e is null) return FindContentOwner(nodeId) is not null; // 文本子节点没有子节点
        if (HasContentNode(e)) children.Add(ContentId(e));
        var ch = e.Children;
        for (int i = 0; i < ch.Count; i++) children.Add(ch[i].UniqueId);
        return true;
    }

    public override BoxModelInfo? GetBoxModel(long nodeId)
    {
        var e = Find(nodeId);
        if (e is null || e.IsTextNode) return null;
        return BoxModelOf(e);
    }

    /// <summary>元素的四个盒（窗口坐标，逻辑像素）；没有自己的布局盒（display:none、段落内部的 span、尚未布局）时返回 null。</summary>
    internal static BoxModelInfo? BoxModelOf(Element e)
    {
        if (e.Render is not { HasBox: true, Layout: { } node } box || box.Kind == BoxKind.Inline) return null;
        var border = box.Border;
        var m = node.Margin;
        var b = node.Border;
        var p = node.Padding;
        var margin = new Rect(border.X - m.Left, border.Y - m.Top, border.Width + m.Left + m.Right, border.Height + m.Top + m.Bottom);
        var padding = Deflate(border, b.Left, b.Top, b.Right, b.Bottom);
        var content = Deflate(padding, p.Left, p.Top, p.Right, p.Bottom);
        return new BoxModelInfo(margin, border, padding, content);
    }

    static Rect Deflate(Rect r, float l, float t, float right, float bottom) =>
        new(r.X + l, r.Y + t, Math.Max(0, r.Width - l - right), Math.Max(0, r.Height - t - bottom));

    public override void QuerySelectorAll(long scopeNodeId, string selector, List<long> results)
    {
        var list = ParseSelector(selector);
        var scope = Find(scopeNodeId);
        if (scope is null) return;
        // 文档顺序的前序遍历（显式栈）
        var stack = new Stack<Element>();
        var ch0 = scope.Children;
        for (int i = ch0.Count - 1; i >= 0; i--) stack.Push(ch0[i]);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.IsTextNode) continue;
            bool match;
            try
            {
                match = SelectorMatcher.Matches(list, RenderBox.Get(e).GetView(_pipeline));
            }
            catch (InsufficientExecutionStackException)
            {
                match = false;
            }
            if (match) results.Add(e.UniqueId);
            var ch = e.Children;
            for (int i = ch.Count - 1; i >= 0; i--) stack.Push(ch[i]);
        }
    }

    /// <summary>解析选择器列表；无法解析抛 <see cref="FormatException"/>（协议层返回 -32005）。</summary>
    internal static SelectorList ParseSelector(string selector)
    {
        var diagnostics = new List<CssDiagnostic>();
        var list = SelectorParser.Parse(selector, diagnostics);
        if (list is null || list.Selectors.Count == 0)
            throw new FormatException(diagnostics.Count > 0 ? diagnostics[0].Message : "Invalid selector: " + selector);
        foreach (var d in diagnostics)
            if (d.Severity == CssDiagnosticSeverity.Error) throw new FormatException(d.Message);
        return list;
    }

    public override long GetNodeAt(Point point, bool ignorePointerEvents) =>
        _pipeline.HitTest(point, ignorePointerEvents) is { } e && ReferenceEquals(e.Root, _root) ? e.UniqueId : 0;

    // ------------------------------------------------------------ 编辑

    static readonly HashSet<string> s_readOnlyReflected = new(StringComparer.OrdinalIgnoreCase)
    {
        "role", "aria-label", "tabindex", "src", "alt", "type", "value", "placeholder", "disabled", "readonly",
    };

    public override bool SetAttribute(long nodeId, string name, string? value)
    {
        var e = Find(nodeId);
        if (e is null) return FindContentOwner(nodeId) is null ? false : throw new NotSupportedException("Text nodes have no attributes.");
        if (e.IsTextNode) throw new NotSupportedException("Text nodes have no attributes.");
        ValidateAttributeName(name);
        switch (name.ToLowerInvariant())
        {
            case "id":
                e.SetId(string.IsNullOrEmpty(value) ? null : value);
                return true;
            case "class":
                e.SetClasses(new ClassValue(value));
                return true;
            case "style":
                // 与 Css.setStyleText 一样写入 DevTools 的内联修改（组件重建不会冲掉，clearOverrides 还原）
                SetInlineOverride(e, value ?? "");
                return true;
        }
        if (s_readOnlyReflected.Contains(name))
            throw new NotSupportedException("'" + name + "' reflects a built-in element property and cannot be edited from DevTools yet.");
        var map = new AttributeMap(e.Attributes as AttributeMap);
        map[name] = value;
        e.SetAttributes(map);
        return true;
    }

    internal void SetInlineOverride(Element e, string? text)
    {
        var box = RenderBox.Get(e);
        if (string.Equals(box.InlineOverride, text, StringComparison.Ordinal)) return;
        box.InlineOverride = text;
        e.MarkDirty(ElementDirtyFlags.Style);
        NotifyAttributeChanged(e.UniqueId, "style");
    }

    static void ValidateAttributeName(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new FormatException("Attribute name must not be empty.");
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '>' or '<' or '/' or '=')
                throw new FormatException("Invalid attribute name: " + name);
        }
    }

    public override bool SetClasses(long nodeId, IReadOnlyList<string> classes)
    {
        var e = Find(nodeId);
        if (e is null || e.IsTextNode) return false;
        foreach (var c in classes)
            if (string.IsNullOrEmpty(c) || c.IndexOfAny(s_whitespace) >= 0) throw new FormatException("Invalid class name: '" + c + "'.");
        e.SetClasses(new ClassValue(string.Join(" ", classes)));
        return true;
    }

    static readonly char[] s_whitespace = { ' ', '\t', '\n', '\r', '\f' };

    // ------------------------------------------------------------ 变更通知

    sealed class Observer : ElementTreeObserver
    {
        readonly ElementInspectableTree _t;

        public Observer(ElementInspectableTree t) => _t = t;

        public override void OnChildInserted(Element parent, Element child)
        {
            _t.Index(child);
            // 协调器从后往前报告插入：其后的兄弟都已报告过，位于最终位置
            _t.NotifyChildInserted(parent.UniqueId, child.UniqueId, child.NextSibling?.UniqueId ?? 0);
        }

        public override void OnChildRemoved(Element parent, Element child)
        {
            _t.NotifyChildRemoved(parent.UniqueId, child.UniqueId);
            _t.Unindex(child);
        }

        public override void OnChildMoved(Element parent, Element child, Element? before) =>
            _t.NotifyChildMoved(parent.UniqueId, child.UniqueId, before?.UniqueId ?? 0);

        public override void OnIdChanged(Element element, string? oldId) => _t.NotifyAttributeChanged(element.UniqueId, "id");

        public override void OnClassesChanged(Element element) => _t.NotifyAttributeChanged(element.UniqueId, "class");

        public override void OnAttributeChanged(Element element, string name, string? oldValue) => _t.NotifyAttributeChanged(element.UniqueId, name);

        public override void OnInlineStyleChanged(Element element) => _t.NotifyAttributeChanged(element.UniqueId, "style");

        public override void OnPropertyChanged(Element element, string name) => _t.NotifyAttributeChanged(element.UniqueId, name);

        public override void OnPseudoStatesChanged(Element element, PseudoStates oldStates) => _t.NotifyPseudoStatesChanged(element.UniqueId);

        public override void OnTextChanged(Element element)
        {
            if (element.IsTextNode)
            {
                _t.NotifyTextChanged(element.UniqueId);
                return;
            }
            if (element is not TextElement te) return;
            long id = ContentId(te);
            bool had = _t._contentNodes.Contains(id);
            bool has = HasContentNode(te);
            if (had && has) _t.NotifyTextChanged(id);
            else if (has)
            {
                _t._contentNodes.Add(id);
                _t.NotifyChildInserted(te.UniqueId, id, te.FirstChild?.UniqueId ?? 0);
            }
            else if (had)
            {
                _t._contentNodes.Remove(id);
                _t.NotifyChildRemoved(te.UniqueId, id);
            }
        }
    }
}

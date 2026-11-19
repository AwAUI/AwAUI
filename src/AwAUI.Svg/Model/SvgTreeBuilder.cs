using System;
using System.Collections.Generic;
using AwAUI.Svg.Css;
using AwAUI.Svg.Xml;

namespace AwAUI.Svg.Model;

/// <summary>
/// XML 树 → SVG 节点树：剔除外来命名空间的元素、建立 id 表、收集 &lt;style&gt;，并层叠出每个元素的已声明样式。
/// 层叠顺序（低 → 高）：表现属性、&lt;style&gt; 普通声明（按特异性与顺序）、style="" 普通声明、
/// &lt;style&gt; 的 !important、style="" 的 !important。
/// </summary>
internal static class SvgTreeBuilder
{
    const string SvgNamespace = "http://www.w3.org/2000/svg";

    public static SvgNode Build(XmlElement rootXml, SvgParseOptions options, DiagnosticBag diag, SourceLocator locator,
        out Dictionary<string, SvgNode> ids, out List<SvgNode> nodes)
    {
        // 命名空间前缀作用域：前缀 → URI 栈，进入/离开元素时压入/弹出，总代价与属性总数成线性
        var scopes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rootDeclared = EnterScope(rootXml, scopes);
        if (rootXml.Name != "svg" || !IsSvgElement(rootXml, scopes))
        {
            var (line, col) = locator.Locate(rootXml.Offset);
            throw new SvgParseException($"The root element is <{rootXml.Name}>, not <svg>.", line, col);
        }

        ids = new Dictionary<string, SvgNode>(StringComparer.Ordinal);
        nodes = new List<SvgNode>();
        var root = new SvgNode(rootXml, SvgElementKind.Svg, null, 0);
        nodes.Add(root);
        Register(root, ids);

        // 迭代的先序遍历（深度本身已由 XML 解析限制，这里也不递归）
        var stack = new List<Frame> { new(rootXml, root, rootDeclared) };
        while (stack.Count > 0)
        {
            var top = stack[stack.Count - 1];
            var children = top.Xml.Children;
            if (children == null || top.Next >= children.Count)
            {
                ExitScope(top.Declared, scopes);
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            var child = children[top.Next++];
            var declared = EnterScope(child, scopes);
            if (!IsSvgElement(child, scopes))
            {
                ExitScope(declared, scopes);
                continue;
            }
            var n = new SvgNode(child, SvgNode.KindOf(child.Name), top.Node, nodes.Count);
            nodes.Add(n);
            top.Node.AddChild(n);
            Register(n, ids);
            stack.Add(new Frame(child, n, declared));
        }

        // 样式表按 <style> 的文档顺序拼接（nodes 即先序）
        var sheet = new SvgStyleSheet(options.MaxSelectorWork);
        foreach (var n in nodes)
        {
            if (n.Kind != SvgElementKind.Style || n.Xml.Text == null) continue;
            var type = n.Attr("type")?.Trim();
            if (!string.IsNullOrEmpty(type) && !type!.Equals("text/css", StringComparison.OrdinalIgnoreCase)) continue;
            sheet.Add(n.Xml.Text, diag, n.Offset);
        }

        var decls = new List<CssDeclaration>();
        foreach (var n in nodes) Cascade(n, sheet, decls, diag);
        return root;
    }

    static void Register(SvgNode n, Dictionary<string, SvgNode> ids)
    {
        if (n.Id != null && !ids.ContainsKey(n.Id)) ids.Add(n.Id, n);
    }

    sealed class Frame
    {
        public Frame(XmlElement xml, SvgNode node, List<string>? declared) { Xml = xml; Node = node; Declared = declared; }
        public readonly XmlElement Xml;
        public readonly SvgNode Node;
        public readonly List<string>? Declared;
        public int Next;
    }

    static List<string>? EnterScope(XmlElement e, Dictionary<string, List<string>> scopes)
    {
        List<string>? declared = null;
        foreach (var a in e.Attributes)
        {
            if (a.Prefix != "xmlns") continue;
            if (!scopes.TryGetValue(a.Name, out var uris)) scopes[a.Name] = uris = new List<string>();
            uris.Add(a.Value);
            (declared ??= new List<string>()).Add(a.Name);
        }
        return declared;
    }

    static void ExitScope(List<string>? declared, Dictionary<string, List<string>> scopes)
    {
        if (declared == null) return;
        foreach (var prefix in declared)
        {
            var uris = scopes[prefix];
            uris.RemoveAt(uris.Count - 1);
        }
    }

    /// <summary>
    /// 无前缀的元素一律视为 SVG（不解释默认命名空间：Illustrator 等工具会用 DOCTYPE 实体写 xmlns，而实体不展开）。
    /// 带前缀的元素只有前缀绑定到 SVG 命名空间时才算。
    /// </summary>
    static bool IsSvgElement(XmlElement e, Dictionary<string, List<string>> scopes)
    {
        if (e.Prefix == null) return true;
        return scopes.TryGetValue(e.Prefix, out var uris) && uris.Count > 0 && uris[uris.Count - 1] == SvgNamespace;
    }

    static void Cascade(SvgNode node, SvgStyleSheet sheet, List<CssDeclaration> decls, DiagnosticBag diag)
    {
        DeclaredStyle? style = null;
        void Set(SvgProperty p, string v) => (style ??= new DeclaredStyle()).Set(p, v);

        // 1. 表现属性
        string? styleAttr = null;
        foreach (var a in node.Xml.Attributes)
        {
            if (a.Prefix != null) continue;
            if (a.Name == "style") { styleAttr = a.Value; continue; }
            if (SvgProperties.TryGet(a.Name, out var p)) Set(p, a.Value);
        }

        // 2. 样式表普通声明（无效的 CSS 声明在解析时已被丢弃，见 CssDeclarations）
        var matched = sheet.IsEmpty ? null : sheet.Match(node, diag);
        if (matched != null)
            foreach (var r in matched)
                foreach (var d in r.Declarations)
                    if (!d.Important) Set(d.Property, d.Value);

        // 3. style 属性普通声明
        decls.Clear();
        if (!string.IsNullOrEmpty(styleAttr)) CssDeclarations.Parse(styleAttr!, decls, diag, node.Offset);
        foreach (var d in decls) if (!d.Important) Set(d.Property, d.Value);

        // 4. 样式表 !important，5. style 属性 !important
        if (matched != null)
            foreach (var r in matched)
                foreach (var d in r.Declarations)
                    if (d.Important) Set(d.Property, d.Value);
        foreach (var d in decls) if (d.Important) Set(d.Property, d.Value);

        node.Style = style;
    }
}

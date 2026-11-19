using System.Collections.Generic;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Ast;

internal enum AstKind : byte
{
    Rule,
    AtRule,
    Declaration,
    Comment,
    Context,
    AtRoot,
}

/// <summary>上下文节点携带的标记（Tailwind 的 <c>context</c> 对象中用到的键）。</summary>
internal sealed class AstContext
{
    public bool Reference;
    public bool Theme;
    public bool Keyframes;
    public bool SupportsColorMix;
    /// <summary>"user" / "variant"（@scope 处理）。</summary>
    public string? Source;
    public string? Base;
    public string? SourceBase;

    public AstContext Clone() => (AstContext)MemberwiseClone();

    /// <summary>把 <paramref name="other"/> 中设置过的键合并进来（<c>{...a, ...b}</c>）。</summary>
    public AstContext Merge(AstContext other)
    {
        var r = Clone();
        r.Reference |= other.Reference;
        r.Theme |= other.Theme;
        r.Keyframes |= other.Keyframes;
        r.SupportsColorMix |= other.SupportsColorMix;
        if (other.Source != null) r.Source = other.Source;
        if (other.Base != null) r.Base = other.Base;
        if (other.SourceBase != null) r.SourceBase = other.SourceBase;
        return r;
    }
}

/// <summary>
/// 内部 CSS 语法树节点。与 Tailwind 一样用一个可变结构表示所有节点种类，
/// 便于变体实现「原地把节点替换成另一种节点」（JS 中的 <c>Object.assign(node, ...)</c>）。
/// </summary>
internal sealed class AstNode
{
    public AstKind Kind;
    /// <summary>规则的选择器。</summary>
    public string Selector = "";
    /// <summary>at 规则名（含 <c>@</c>）。</summary>
    public string Name = "";
    /// <summary>at 规则参数。</summary>
    public string Params = "";
    /// <summary>声明属性名；注释节点的文本也放在 <see cref="Value"/>。</summary>
    public string Property = "";
    /// <summary>声明值；null 表示「不输出」（Tailwind 的 undefined）。</summary>
    public string? Value;
    public bool Important;
    public List<AstNode> Nodes = s_empty;
    public AstContext? Context;

    static readonly List<AstNode> s_empty = new(0);

    public bool HasChildren => Kind is AstKind.Rule or AstKind.AtRule or AstKind.Context or AstKind.AtRoot;

    public static AstNode Rule(string selector, List<AstNode>? nodes = null) => new() { Kind = AstKind.Rule, Selector = selector, Nodes = nodes ?? new List<AstNode>() };

    public static AstNode AtRule(string name, string @params = "", List<AstNode>? nodes = null) => new() { Kind = AstKind.AtRule, Name = name, Params = @params, Nodes = nodes ?? new List<AstNode>() };

    /// <summary><c>@</c> 开头时解析为 at 规则，否则为样式规则（Tailwind <c>rule()</c>）。</summary>
    public static AstNode RuleOrAtRule(string selector, List<AstNode>? nodes = null)
    {
        if (selector.Length > 0 && selector[0] == '@') return ParseAtRule(selector, nodes ?? new List<AstNode>());
        return Rule(selector, nodes);
    }

    /// <summary>把 <c>@name params</c> 文本拆成 at 规则（Tailwind <c>parseAtRule</c>）。</summary>
    public static AstNode ParseAtRule(string buffer, List<AstNode> nodes)
    {
        string name = buffer;
        string @params = "";
        for (int i = 5; i < buffer.Length; i++)
        {
            char c = buffer[i];
            if (c == ' ' || c == '\t' || c == '(')
            {
                name = buffer.Substring(0, i);
                @params = buffer.Substring(i);
                break;
            }
        }
        return AtRule(name.Trim(), @params.Trim(), nodes);
    }

    public static AstNode Decl(string property, string? value, bool important = false) => new() { Kind = AstKind.Declaration, Property = property, Value = value, Important = important };

    public static AstNode Comment(string value) => new() { Kind = AstKind.Comment, Value = value };

    public static AstNode CreateContext(AstContext context, List<AstNode> nodes) => new() { Kind = AstKind.Context, Context = context, Nodes = nodes };

    public static AstNode AtRoot(List<AstNode> nodes) => new() { Kind = AstKind.AtRoot, Nodes = nodes };

    /// <summary>深拷贝（Tailwind <c>cloneAstNode</c>）。</summary>
    public AstNode Clone()
    {
        var c = (AstNode)MemberwiseClone();
        if (HasChildren)
        {
            var list = new List<AstNode>(Nodes.Count);
            foreach (var n in Nodes) list.Add(n.Clone());
            c.Nodes = list;
        }
        if (Context != null) c.Context = Context.Clone();
        return c;
    }

    /// <summary>把另一节点的全部字段复制到本节点（JS 的 <c>Object.assign</c>）。</summary>
    public void AssignFrom(AstNode other)
    {
        Kind = other.Kind;
        Selector = other.Selector;
        Name = other.Name;
        Params = other.Params;
        Property = other.Property;
        Value = other.Value;
        Important = other.Important;
        Nodes = other.Nodes;
        Context = other.Context;
    }

    public override string ToString() => CssPrinter.Print(new List<AstNode> { this }, false);

    public static List<AstNode>? Children(AstNode n) => n.HasChildren ? n.Nodes : null;

    public static void Walk(List<AstNode> nodes, System.Func<AstNode, WalkContext<AstNode>, WalkAction<AstNode>> enter, System.Func<AstNode, WalkContext<AstNode>, WalkAction<AstNode>>? exit = null)
        => Walker.Walk(nodes, Children, enter, exit);

    public static void CloneInto(List<AstNode> source, List<AstNode> target)
    {
        foreach (var n in source) target.Add(n.Clone());
    }

    public static List<AstNode> CloneList(List<AstNode> source)
    {
        var l = new List<AstNode>(source.Count);
        CloneInto(source, l);
        return l;
    }
}

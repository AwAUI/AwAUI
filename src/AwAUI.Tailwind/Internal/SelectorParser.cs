using System.Collections.Generic;
using System.Text;

namespace AwAUI.Tailwind.Internal;

internal enum SelectorKind : byte
{
    Combinator,
    Complex,
    Compound,
    Function,
    List,
    Selector,
    Value,
}

/// <summary>选择器语法树节点（移植自 Tailwind <c>selector-parser.ts</c>，用于展开嵌套规则）。</summary>
internal sealed class SelectorNode
{
    public SelectorKind Kind;
    public string Value;
    public List<SelectorNode>? Nodes;

    public SelectorNode(SelectorKind kind, string value, List<SelectorNode>? nodes)
    {
        Kind = kind;
        Value = value;
        Nodes = nodes;
    }

    public static SelectorNode Combinator(string v) => new(SelectorKind.Combinator, v, null);
    public static SelectorNode Complex(List<SelectorNode> nodes) => new(SelectorKind.Complex, "", nodes);
    public static SelectorNode Compound(List<SelectorNode> nodes) => new(SelectorKind.Compound, "", nodes);
    public static SelectorNode Function(string v, List<SelectorNode> nodes) => new(SelectorKind.Function, v, nodes);
    public static SelectorNode List(List<SelectorNode> nodes) => new(SelectorKind.List, "", nodes);
    public static SelectorNode Selector(string v) => new(SelectorKind.Selector, v, null);
    public static SelectorNode ValueNode(string v) => new(SelectorKind.Value, v, null);
}

internal static class SelectorParser
{
    /// <summary>是否为通配选择器 <c>*</c> 开头的简单选择器。</summary>
    public static bool IsUniversal(SelectorNode n) => n.Kind == SelectorKind.Selector && n.Value.Length > 0 && n.Value[0] == '*';

    static readonly HashSet<string> s_legacyPseudoElements = new() { ":before", ":after", ":first-line", ":first-letter" };

    public static bool IsPseudoElement(SelectorNode n)
    {
        if (n.Kind != SelectorKind.Selector && n.Kind != SelectorKind.Function) return false;
        return n.Value.StartsWith("::", System.StringComparison.Ordinal) || s_legacyPseudoElements.Contains(n.Value.ToLowerInvariant());
    }

    /// <summary>类型选择器（元素名）。</summary>
    public static bool IsTypeSelector(SelectorNode n)
    {
        if (n.Kind != SelectorKind.Selector || n.Value.Length == 0) return false;
        switch (n.Value[0])
        {
            case '*':
            case '&':
            case '.':
            case '#':
            case ':':
            case '[':
                return false;
            default:
                return true;
        }
    }

    public static string ToCss(List<SelectorNode> nodes, bool compact = false)
    {
        var sb = new StringBuilder();
        Print(nodes, compact, sb);
        return sb.ToString();
    }

    static void Print(List<SelectorNode> nodes, bool compact, StringBuilder sb)
    {
        foreach (var t in nodes)
        {
            switch (t.Kind)
            {
                case SelectorKind.Selector:
                case SelectorKind.Value:
                    sb.Append(t.Value);
                    break;
                case SelectorKind.Combinator:
                    if (compact || t.Value == " ") sb.Append(t.Value);
                    else sb.Append(' ').Append(t.Value).Append(' ');
                    break;
                case SelectorKind.Function:
                    sb.Append(t.Value).Append('(');
                    Print(t.Nodes!, compact, sb);
                    sb.Append(')');
                    break;
                case SelectorKind.Complex:
                case SelectorKind.Compound:
                    Print(t.Nodes!, compact, sb);
                    break;
                case SelectorKind.List:
                    for (int i = 0; i < t.Nodes!.Count; i++)
                    {
                        if (i > 0) sb.Append(compact ? "," : ", ");
                        Print(new List<SelectorNode> { t.Nodes[i] }, compact, sb);
                    }
                    break;
            }
        }
    }

    static bool IsCombinatorChar(char c) => c == '>' || c == '\n' || c == ' ' || c == '+' || c == '\t' || c == '~';

    sealed class StackEntry
    {
        public List<SelectorNode> Target = null!;
        public SelectorNode? CurrentList;
        public bool ContainsCombinator;
    }

    public static List<SelectorNode> Parse(string input)
    {
        if (input.IndexOf('\r') >= 0) input = input.Replace("\r\n", "\n");
        var ast = new List<SelectorNode>();
        var target = ast;
        bool containsCombinator = false;
        var stack = new List<StackEntry>();
        SelectorNode? currentList = null;
        var buffer = new StringBuilder();

        SelectorNode Wrap(List<SelectorNode> d) => d.Count == 1 ? d[0] : containsCombinator ? SelectorNode.Complex(d) : SelectorNode.Compound(d);

        void Push(SelectorNode d)
        {
            SelectorNode? last = target.Count > 0 ? target[target.Count - 1] : null;
            if (last != null && last.Kind == SelectorKind.Compound) last.Nodes!.Add(d);
            else if (last != null && last.Kind != SelectorKind.List && last.Kind != SelectorKind.Combinator)
                target[target.Count - 1] = SelectorNode.Compound(new List<SelectorNode> { last, d });
            else target.Add(d);
        }

        void FlushBuffer()
        {
            if (buffer.Length > 0)
            {
                Push(SelectorNode.Selector(buffer.ToString()));
                buffer.Clear();
            }
        }

        int len = input.Length;
        for (int i = 0; i < len; i++)
        {
            char c = input[i];
            switch (c)
            {
                case ',':
                {
                    FlushBuffer();
                    while (i + 1 < len)
                    {
                        char p = input[i + 1];
                        if (p != '\n' && p != ' ' && p != '\t') break;
                        i++;
                    }
                    if (currentList != null)
                    {
                        currentList.Nodes!.Add(Wrap(target));
                        target = new List<SelectorNode>();
                        containsCombinator = false;
                    }
                    else
                    {
                        var items = new List<SelectorNode>(target);
                        target.Clear();
                        var h = Wrap(items);
                        var list = SelectorNode.List(new List<SelectorNode> { h });
                        target.Add(list);
                        currentList = list;
                        target = new List<SelectorNode>();
                        containsCombinator = false;
                    }
                    break;
                }
                case '>':
                case '\n':
                case ' ':
                case '+':
                case '\t':
                case '~':
                {
                    FlushBuffer();
                    int f = i;
                    int h = i + 1;
                    while (h < len && IsCombinatorChar(input[h])) h++;
                    i = h - 1;
                    string w = input.Substring(f, h - f).Trim();
                    if (w.Length == 0 && (target.Count == 0 || h >= len || input[h] == ',')) break;
                    target.Add(SelectorNode.Combinator(w.Length == 0 ? " " : w));
                    containsCombinator = true;
                    break;
                }
                case '(':
                {
                    var fn = SelectorNode.Function(buffer.ToString(), new List<SelectorNode>());
                    buffer.Clear();
                    string name = fn.Value;
                    if (name != ":not" && name != ":where" && name != ":has" && name != ":is" && name != ":-moz-any" && name != ":-webkit-any")
                    {
                        int start = i + 1;
                        int depth = 0;
                        for (int a = i + 1; a < len; a++)
                        {
                            char p = input[a];
                            if (p == '(')
                            {
                                depth++;
                                continue;
                            }
                            if (p == ')')
                            {
                                if (depth == 0)
                                {
                                    i = a;
                                    break;
                                }
                                depth--;
                            }
                        }
                        int end = i;
                        string inner = input.Substring(start, System.Math.Max(0, end - start));
                        if (name == ":nth-child" || name == ":nth-last-child")
                        {
                            int of = inner.IndexOf("of ", System.StringComparison.Ordinal);
                            if (of != -1)
                            {
                                fn.Nodes!.Add(SelectorNode.ValueNode(inner.Substring(0, of + 3)));
                                fn.Nodes.AddRange(Parse(inner.Substring(of + 3)));
                                Push(fn);
                                break;
                            }
                        }
                        fn.Nodes!.Add(SelectorNode.ValueNode(inner));
                        Push(fn);
                        break;
                    }
                    Push(fn);
                    stack.Add(new StackEntry { Target = target, CurrentList = currentList, ContainsCombinator = containsCombinator });
                    target = fn.Nodes!;
                    containsCombinator = false;
                    currentList = null;
                    break;
                }
                case ')':
                {
                    FlushBuffer();
                    if (currentList != null) currentList.Nodes!.Add(Wrap(target));
                    else if (containsCombinator)
                    {
                        var all = new List<SelectorNode>(target);
                        target.Clear();
                        target.Add(SelectorNode.Complex(all));
                    }
                    StackEntry? f = null;
                    if (stack.Count > 0)
                    {
                        f = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                    target = f?.Target ?? ast;
                    currentList = f?.CurrentList;
                    containsCombinator = f?.ContainsCombinator ?? false;
                    break;
                }
                case '.':
                case ':':
                case '#':
                {
                    if (c == ':' && buffer.Length == 1 && buffer[0] == ':')
                    {
                        buffer.Append(c);
                        break;
                    }
                    FlushBuffer();
                    buffer.Append(c);
                    break;
                }
                case '[':
                {
                    FlushBuffer();
                    int f = i;
                    int depth = 0;
                    for (int w = i + 1; w < len; w++)
                    {
                        char p = input[w];
                        if (p == '[')
                        {
                            depth++;
                            continue;
                        }
                        if (p == ']')
                        {
                            if (depth == 0)
                            {
                                i = w;
                                break;
                            }
                            depth--;
                        }
                    }
                    Push(SelectorNode.Selector(input.Substring(f, i - f + 1)));
                    break;
                }
                case '\'':
                case '"':
                {
                    int f = i;
                    for (int h = i + 1; h < len; h++)
                    {
                        char p = input[h];
                        if (p == '\\') h += 1;
                        else if (p == c)
                        {
                            i = h;
                            break;
                        }
                    }
                    buffer.Append(input, f, i - f + 1);
                    break;
                }
                case '&':
                case '*':
                    FlushBuffer();
                    Push(SelectorNode.Selector(c.ToString()));
                    break;
                case '\\':
                    buffer.Append(c);
                    if (i + 1 < len) buffer.Append(input[i + 1]);
                    i += 1;
                    break;
                default:
                    buffer.Append(c);
                    break;
            }
        }
        FlushBuffer();
        if (currentList != null) currentList.Nodes!.Add(Wrap(target));
        else if (containsCombinator)
        {
            var all = new List<SelectorNode>(target);
            target.Clear();
            target.Add(SelectorNode.Complex(all));
        }
        return ast;
    }

    public static List<SelectorNode>? Children(SelectorNode n) => n.Nodes;

    public static void Walk(List<SelectorNode> nodes, System.Func<SelectorNode, WalkContext<SelectorNode>, WalkAction<SelectorNode>> enter, System.Func<SelectorNode, WalkContext<SelectorNode>, WalkAction<SelectorNode>>? exit = null)
        => Walker.Walk(nodes, Children, enter, exit);
}

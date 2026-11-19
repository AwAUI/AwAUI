using System.Collections.Generic;
using System.Text;

namespace AwAUI.Tailwind.Internal;

internal enum ValueKind : byte
{
    Word,
    Function,
    Separator,
}

/// <summary>CSS 值的轻量语法树节点（词、函数、分隔符），与 Tailwind 的 <c>ValueParser</c> 一致。</summary>
internal sealed class ValueNode
{
    public ValueKind Kind;
    public string Value;
    public List<ValueNode>? Nodes;

    public ValueNode(ValueKind kind, string value, List<ValueNode>? nodes = null)
    {
        Kind = kind;
        Value = value;
        Nodes = nodes;
    }

    public static ValueNode Word(string value) => new(ValueKind.Word, value);

    public static ValueNode Function(string name, List<ValueNode> nodes) => new(ValueKind.Function, name, nodes);

    public static ValueNode Separator(string value) => new(ValueKind.Separator, value);

    public override string ToString() => ValueParser.ToCss(new List<ValueNode> { this });
}

/// <summary>CSS 值解析与打印（移植自 Tailwind <c>value-parser.ts</c>）。</summary>
internal static class ValueParser
{
    static bool IsSeparatorChar(char c) => c == ':' || c == ',' || c == '=' || c == '>' || c == '<' || c == '\n' || c == ' ' || c == '\t';

    public static List<ValueNode> Parse(string input)
    {
        if (input.IndexOf('\r') >= 0) input = input.Replace("\r\n", "\n");
        var ast = new List<ValueNode>();
        var stack = new List<ValueNode>();
        ValueNode? parent = null;
        var buffer = new StringBuilder();
        int len = input.Length;
        for (int i = 0; i < len; i++)
        {
            char c = input[i];
            switch (c)
            {
                case '\\':
                    buffer.Append(c);
                    if (i + 1 < len) buffer.Append(input[i + 1]);
                    i++;
                    break;
                case '/':
                {
                    if (buffer.Length > 0)
                    {
                        Add(ValueNode.Word(buffer.ToString()));
                        buffer.Clear();
                    }
                    Add(ValueNode.Word("/"));
                    break;
                }
                case ':':
                case ',':
                case '=':
                case '>':
                case '<':
                case '\n':
                case ' ':
                case '\t':
                {
                    if (buffer.Length > 0)
                    {
                        Add(ValueNode.Word(buffer.ToString()));
                        buffer.Clear();
                    }
                    int start = i;
                    int end = i + 1;
                    while (end < len && IsSeparatorChar(input[end])) end++;
                    i = end - 1;
                    Add(ValueNode.Separator(input.Substring(start, end - start)));
                    break;
                }
                case '\'':
                case '"':
                {
                    int start = i;
                    for (int j = i + 1; j < len; j++)
                    {
                        char n = input[j];
                        if (n == '\\') j += 1;
                        else if (n == c)
                        {
                            i = j;
                            break;
                        }
                    }
                    buffer.Append(input, start, i - start + 1);
                    break;
                }
                case '(':
                {
                    var fn = ValueNode.Function(buffer.ToString(), new List<ValueNode>());
                    buffer.Clear();
                    Add(fn);
                    stack.Add(fn);
                    parent = fn;
                    break;
                }
                case ')':
                {
                    ValueNode? tail = null;
                    if (stack.Count > 0)
                    {
                        tail = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                    if (buffer.Length > 0)
                    {
                        tail?.Nodes!.Add(ValueNode.Word(buffer.ToString()));
                        buffer.Clear();
                    }
                    parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
                    break;
                }
                default:
                    buffer.Append(c);
                    break;
            }
        }
        if (buffer.Length > 0) ast.Add(ValueNode.Word(buffer.ToString()));
        return ast;

        void Add(ValueNode node)
        {
            if (parent != null) parent.Nodes!.Add(node);
            else ast.Add(node);
        }
    }

    public static string ToCss(List<ValueNode> nodes)
    {
        var sb = new StringBuilder();
        Print(nodes, sb);
        return sb.ToString();
    }

    static void Print(List<ValueNode> nodes, StringBuilder sb)
    {
        foreach (var n in nodes)
        {
            switch (n.Kind)
            {
                case ValueKind.Word:
                case ValueKind.Separator:
                    sb.Append(n.Value);
                    break;
                case ValueKind.Function:
                    sb.Append(n.Value).Append('(');
                    if (n.Nodes != null) Print(n.Nodes, sb);
                    sb.Append(')');
                    break;
            }
        }
    }

    public static List<ValueNode>? Children(ValueNode n) => n.Kind == ValueKind.Function ? n.Nodes : null;

    public static void Walk(List<ValueNode> nodes, System.Func<ValueNode, WalkContext<ValueNode>, WalkAction<ValueNode>> enter, System.Func<ValueNode, WalkContext<ValueNode>, WalkAction<ValueNode>>? exit = null)
        => Walker.Walk(nodes, Children, enter, exit);
}

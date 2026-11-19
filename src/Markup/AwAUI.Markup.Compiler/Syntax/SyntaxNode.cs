using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.Syntax;

/// <summary>语法节点的种类。</summary>
public enum SyntaxKind
{
    Document,
    Directive,
    CodeBlock,
    StyleBlock,
    Comment,
    Text,
    Expression,
    Element,
    Fragment,
    Attribute,
    AttributeValue,
    AttributeLiteral,
    StatementBlock,
    CSharpCode,
    CodeBody,
    If,
    IfBranch,
    Loop,
    Switch,
    SwitchSection,
    SwitchLabel,
    LineText,
}

/// <summary>
/// 语法树节点的基类。每个节点都带有在源文本中的区间；子节点按源顺序排列，区间互不重叠且包含在父节点区间内。
/// 节点不可变，变换（如空白规范化）通过重建产生新树。
/// </summary>
public abstract class SyntaxNode
{
    private protected SyntaxNode(TextSpan span) => Span = span;

    public TextSpan Span { get; }
    public abstract SyntaxKind Kind { get; }

    /// <summary>按源顺序列出直接子节点。</summary>
    public abstract IEnumerable<SyntaxNode> ChildNodes();

    /// <summary>深度优先、先序遍历全部后代节点。</summary>
    public IEnumerable<SyntaxNode> DescendantNodes()
    {
        var stack = new Stack<IEnumerator<SyntaxNode>>();
        stack.Push(ChildNodes().GetEnumerator());
        while (stack.Count > 0)
        {
            var e = stack.Peek();
            if (!e.MoveNext())
            {
                stack.Pop();
                continue;
            }
            yield return e.Current;
            stack.Push(e.Current.ChildNodes().GetEnumerator());
        }
    }

    public override string ToString() => $"{Kind} {Span}";

    private protected static IEnumerable<SyntaxNode> None => Array.Empty<SyntaxNode>();
}

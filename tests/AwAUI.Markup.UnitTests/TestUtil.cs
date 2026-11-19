using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;

namespace AwAUI.Markup.UnitTests;

static class TestUtil
{
    /// <summary>解析并断言没有任何诊断（含检查器的诊断）。</summary>
    public static DocumentSyntax ParseClean(string text)
    {
        var a = MarkupCompiler.Analyze(text);
        Assert.True(a.Diagnostics.Count == 0, "unexpected diagnostics:\n" + string.Join("\n", a.Diagnostics));
        CheckSpans(a.Tree);
        return a.Tree.Root;
    }

    /// <summary>解析、规范化空白并断言没有诊断。</summary>
    public static DocumentSyntax ParseNormalized(string text) => WhitespaceNormalizer.Normalize(ParseClean(text));

    public static IReadOnlyList<MarkupDiagnostic> Diagnose(string text, string? path = null) =>
        MarkupCompiler.Analyze(text, path).Diagnostics;

    /// <summary>规范化后去掉空白文本的顶层节点。</summary>
    public static List<SyntaxNode> Items(DocumentSyntax doc) => doc.Items.ToList();

    public static T Single<T>(DocumentSyntax doc) where T : SyntaxNode =>
        Assert.IsType<T>(Assert.Single(WhitespaceNormalizer.Normalize(doc).Items));

    /// <summary>
    /// 语法树的区间不变式：每个节点的区间在文本范围内；子节点按源顺序、互不重叠、包含在父节点区间内。
    /// </summary>
    public static void CheckSpans(MarkupSyntaxTree tree)
    {
        Assert.Equal(new TextSpan(0, tree.Source.Length), tree.Root.Span);
        CheckNode(tree.Root, tree.Source.Length);
    }

    static void CheckNode(SyntaxNode node, int length)
    {
        Assert.True(node.Span.End <= length, $"{node} exceeds text length {length}");
        int prevEnd = node.Span.Start;
        foreach (var child in node.ChildNodes())
        {
            Assert.True(node.Span.Contains(child.Span), $"{child} not inside parent {node}");
            Assert.True(child.Span.Start >= prevEnd, $"{child} overlaps previous sibling (prev end {prevEnd}) in {node}");
            prevEnd = child.Span.End;
            CheckNode(child, length);
        }
    }

    public static string Dump(SyntaxNode node) => SyntaxPrinter.Print(node);
}

using System.Collections.Generic;
using AwAUI.Markup.Compiler.Parsing;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.Compiler;

/// <summary>一个 <c>.awaui</c> 文件的语法树与语法诊断。</summary>
public sealed class MarkupSyntaxTree
{
    MarkupSyntaxTree(SourceText source, DocumentSyntax root, IReadOnlyList<MarkupDiagnostic> diagnostics)
    {
        Source = source;
        Root = root;
        Diagnostics = diagnostics;
    }

    public SourceText Source { get; }
    /// <summary>未经规范化的完整语法树（保留全部空白文本节点）。</summary>
    public DocumentSyntax Root { get; }
    /// <summary>语法层面的诊断（AW4001、AW4005、AW4006、AW4007、AW4010 的结构部分）。</summary>
    public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }

    /// <summary>解析源文本。任何输入都不会抛异常。</summary>
    public static MarkupSyntaxTree Parse(string text, string? filePath = null) => Parse(new SourceText(text, filePath));

    /// <inheritdoc cref="Parse(string, string?)"/>
    public static MarkupSyntaxTree Parse(SourceText source)
    {
        var diagnostics = new DiagnosticBag(source);
        var root = new MarkupParser(source, diagnostics).ParseDocument();
        return new MarkupSyntaxTree(source, root, diagnostics.Items);
    }
}

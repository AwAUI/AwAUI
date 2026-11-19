using System.Collections.Generic;
using AwAUI.Markup.Compiler.Analysis;
using AwAUI.Markup.Compiler.CodeGen;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;

namespace AwAUI.Markup.Compiler;

/// <summary>前端分析的结果：语法树、空白规范化后的树与全部诊断（按位置排序）。</summary>
public sealed class MarkupAnalysis
{
    internal MarkupAnalysis(MarkupSyntaxTree tree, DocumentSyntax normalizedRoot, IReadOnlyList<MarkupDiagnostic> diagnostics)
    {
        Tree = tree;
        NormalizedRoot = normalizedRoot;
        Diagnostics = diagnostics;
    }

    public MarkupSyntaxTree Tree { get; }
    public DocumentSyntax NormalizedRoot { get; }
    public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }

    public bool HasErrors
    {
        get
        {
            foreach (var d in Diagnostics)
                if (d.IsError) return true;
            return false;
        }
    }
}

/// <summary>完整编译的结果：生成的 C#、全部诊断（前端 + 代码生成，按位置排序）与样式。</summary>
public sealed class MarkupCompilation
{
    internal MarkupCompilation(MarkupAnalysis analysis, string generatedCode, IReadOnlyList<MarkupDiagnostic> diagnostics,
        string className, string ns, string? styleScope, string? scopedStyleSheet, string? globalStyleSheet)
    {
        Analysis = analysis;
        GeneratedCode = generatedCode;
        Diagnostics = diagnostics;
        ClassName = className;
        Namespace = ns;
        StyleScope = styleScope;
        ScopedStyleSheet = scopedStyleSheet;
        GlobalStyleSheet = globalStyleSheet;
    }

    public MarkupAnalysis Analysis { get; }

    /// <summary>生成的 C# 编译单元（partial class + Build() + Props 胶水 + 样式常量）。</summary>
    public string GeneratedCode { get; }

    public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }

    public string ClassName { get; }

    public string Namespace { get; }

    /// <summary>作用域样式的属性名（<c>aw-xxxxxxxx</c>）；没有作用域 <c>&lt;style&gt;</c> 时为 null。</summary>
    public string? StyleScope { get; }

    /// <summary>作用域样式表（选择器已改写）；没有作用域 <c>&lt;style&gt;</c> 时为 null。</summary>
    public string? ScopedStyleSheet { get; }

    /// <summary><c>&lt;style global&gt;</c> 的内容；没有时为 null。</summary>
    public string? GlobalStyleSheet { get; }

    public bool HasErrors
    {
        get
        {
            foreach (var d in Diagnostics)
                if (d.IsError) return true;
            return false;
        }
    }
}

/// <summary>标记编译器的入口：解析 → 检查 → 空白规范化（<see cref="Analyze"/>），再生成 C#（<see cref="Compile(string, string?, CSharpEmitOptions?)"/>）。</summary>
public static class MarkupCompiler
{
    /// <summary>编译一个 <c>.awaui</c> 文件。任何输入都不会抛异常；有错误时仍尽量生成 C#（出错的元素被跳过）。</summary>
    public static MarkupCompilation Compile(string text, string? filePath = null, CSharpEmitOptions? options = null) =>
        Compile(Analyze(text, filePath), options);

    /// <inheritdoc cref="Compile(string, string?, CSharpEmitOptions?)"/>
    public static MarkupCompilation Compile(MarkupAnalysis analysis, CSharpEmitOptions? options = null)
    {
        var emitted = CSharpEmitter.EmitFull(analysis, options);
        var all = new List<MarkupDiagnostic>(analysis.Diagnostics);
        all.AddRange(emitted.Diagnostics);
        return new MarkupCompilation(analysis, emitted.Code, SortByPosition(all), emitted.ClassName, emitted.Namespace,
            emitted.StyleScope, emitted.ScopedCss, emitted.GlobalCss);
    }

    public static MarkupAnalysis Analyze(string text, string? filePath = null)
    {
        var tree = MarkupSyntaxTree.Parse(text, filePath);
        var all = new List<MarkupDiagnostic>(tree.Diagnostics);
        all.AddRange(MarkupValidator.Validate(tree));
        return new MarkupAnalysis(tree, WhitespaceNormalizer.Normalize(tree.Root), SortByPosition(all));
    }

    /// <summary>稳定排序：按位置，同位置保持产生顺序。</summary>
    static List<MarkupDiagnostic> SortByPosition(List<MarkupDiagnostic> all)
    {
        var indexed = new List<KeyValuePair<int, MarkupDiagnostic>>(all.Count);
        for (int i = 0; i < all.Count; i++) indexed.Add(new KeyValuePair<int, MarkupDiagnostic>(i, all[i]));
        indexed.Sort((a, b) =>
        {
            int c = a.Value.Span.Start.CompareTo(b.Value.Span.Start);
            return c != 0 ? c : a.Key.CompareTo(b.Key);
        });
        var sorted = new List<MarkupDiagnostic>(all.Count);
        foreach (var kv in indexed) sorted.Add(kv.Value);
        return sorted;
    }
}

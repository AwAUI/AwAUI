using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css;

/// <summary>
/// 解析后的样式表：规则树 + 诊断。运行时样式表、DevTools 实时编辑与热重载都经由 <see cref="Parse"/>（design/08）。
/// </summary>
public sealed class StyleSheet
{
    StyleSheet(string text, string? sourceName, IReadOnlyList<CssRule> rules, IReadOnlyList<CssDiagnostic> diagnostics)
    {
        Text = text;
        SourceName = sourceName;
        Rules = rules;
        Diagnostics = diagnostics;
    }

    /// <summary>原始文本。</summary>
    public string Text { get; }

    /// <summary>来源名称（文件路径等），可为 null。</summary>
    public string? SourceName { get; }

    /// <summary>顶层规则。被错误恢复丢弃的规则不在其中。</summary>
    public IReadOnlyList<CssRule> Rules { get; }

    /// <summary>解析过程中产生的全部诊断，按出现顺序。</summary>
    public IReadOnlyList<CssDiagnostic> Diagnostics { get; }

    /// <summary>按 CSS Syntax 3「解析样式表」算法解析，永不抛出语法异常。</summary>
    public static StyleSheet Parse(string text, string? sourceName = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var diagnostics = new List<CssDiagnostic>();
        var source = new CssSourceText(text);
        var parser = new CssParser(source, diagnostics);
        var rules = parser.ParseStyleSheet();
        return new StyleSheet(text, sourceName, rules, diagnostics);
    }

    /// <summary>
    /// 解析声明列表（内联样式 <c>Style = "padding: 8px"</c>、DevTools 的 setStyleText）。其中的规则被丢弃并报告。
    /// </summary>
    public static IReadOnlyList<CssDeclaration> ParseDeclarations(string text, List<CssDiagnostic>? diagnostics = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var parser = new CssParser(new CssSourceText(text), diagnostics ?? new List<CssDiagnostic>());
        return parser.ParseDeclarationList();
    }

    /// <summary>先序遍历全部规则（含嵌套规则、分组规则内的规则与关键帧）。</summary>
    public IEnumerable<CssRule> EnumerateRules()
    {
        var stack = new Stack<(IReadOnlyList<CssRule> list, int index)>();
        stack.Push((Rules, 0));
        while (stack.Count > 0)
        {
            var (list, index) = stack.Pop();
            if (index >= list.Count) continue;
            stack.Push((list, index + 1));
            var rule = list[index];
            yield return rule;
            if (rule.ChildRules.Count > 0) stack.Push((rule.ChildRules, 0));
        }
    }
}

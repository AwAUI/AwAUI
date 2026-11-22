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
    /// 增量解析：<paramref name="previous"/> 是修改前文本的解析结果，<paramref name="changedAt"/> 是第一个可能发生变化的
    /// 顶层规则起点（该点之前的内容必须逐字符相同）。前缀规则与诊断直接复用（它们的源码区间在新文本中仍然有效），
    /// 只从 <paramref name="changedAt"/> 开始重新解析；结果与整段 <see cref="Parse"/> 等价，但开销只与变化后的尾部有关。
    /// <para>
    /// 如果前缀并不相同、发生变化的位置落在某个顶层规则内部，或参数越界，则自动退回整段解析。
    /// 返回值是新对象；<paramref name="previous"/> 不被修改。
    /// </para>
    /// </summary>
    public static StyleSheet ParseIncremental(string text, string? sourceName, StyleSheet previous, int changedAt)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (previous is null) throw new ArgumentNullException(nameof(previous));
        if (changedAt < 0 || changedAt > text.Length) throw new ArgumentOutOfRangeException(nameof(changedAt));
        if (changedAt > previous.Text.Length
            || string.CompareOrdinal(previous.Text, 0, text, 0, changedAt) != 0)
            return Parse(text, sourceName);

        var rules = new List<CssRule>(previous.Rules.Count + 4);
        // 与解析器一致：只有 @import、@layer 语句与被忽略的未知 at 规则不禁止后续 @import。
        // 前缀里没有这样的规则（或根本没有规则）时，尾部仍可合法地以 @import 开头。
        bool importAllowed = true;
        foreach (var rule in previous.Rules)
        {
            if (rule.Range.End.Offset <= changedAt)
            {
                rules.Add(rule);
                if (rule is not CssImportRule
                    && rule is not CssLayerStatementRule
                    && rule is not CssUnknownAtRule { IsKnownUnsupported: false })
                    importAllowed = false;
                continue;
            }
            // 变化落在一条顶层规则内部：无法从规则中间继续，退回整段解析
            if (rule.Range.Start.Offset < changedAt) return Parse(text, sourceName);
            break;
        }

        var diagnostics = new List<CssDiagnostic>();
        foreach (var d in previous.Diagnostics)
        {
            if (d.Range.End.Offset <= changedAt) diagnostics.Add(d);
            // 跨过变化点的诊断属于被丢弃的规则（错误恢复）：尾部解析可能不再报告它，退回整段解析
            else if (d.Range.Start.Offset < changedAt) return Parse(text, sourceName);
        }

        var parser = new CssParser(new CssSourceText(text), diagnostics);
        rules.AddRange(parser.ParseStyleSheet(changedAt, importAllowed));
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

using System.Collections.Generic;
using System.Text;
using AwAUI.Markup.Compiler.Parsing;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.Compiler.Transforms;

/// <summary>
/// 空白规范化（design/14「空白」，与 JSX 的规则相同）：对标记中的每个文本节点按行处理 ——
/// 制表符视为空格；除第一行外去掉行首空白，除最后一行外去掉行尾空白；丢弃空行；
/// 其余行之间以一个空格连接。结果为空的文本节点被移除。因此仅由空白和换行组成的文本被丢弃，
/// 行内空白（包括与表达式、元素相邻的同一行空白）保留。
/// <c>@:</c> 单行文本、属性值、代码与样式不受影响。
/// </summary>
public static class WhitespaceNormalizer
{
    /// <summary>返回规范化后的新树；原树不变。</summary>
    public static DocumentSyntax Normalize(DocumentSyntax document) =>
        new(document.Span, VisitList(document.Items, normalizeText: true));

    /// <summary>对一段文本源写法应用规则；返回 null 表示该文本节点应被丢弃。</summary>
    public static string? NormalizeText(string raw)
    {
        var lines = SplitLines(raw);
        int lastNonEmpty = 0; // 与 JSX 一致：没有非空白行时视第 0 行为最后一个非空行
        for (int i = 0; i < lines.Count; i++)
            if (HasContent(lines[i])) lastNonEmpty = i;

        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Replace('\t', ' ');
            if (i > 0) line = line.TrimStart(' ');
            if (i < lines.Count - 1) line = line.TrimEnd(' ');
            if (line.Length == 0) continue;
            sb.Append(line);
            if (i != lastNonEmpty) sb.Append(' ');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    static bool HasContent(string line)
    {
        foreach (char c in line)
            if (c != ' ' && c != '\t') return true;
        return false;
    }

    static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r' || c == '\n')
            {
                lines.Add(s.Substring(start, i - start));
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        lines.Add(s.Substring(start));
        return lines;
    }

    static IReadOnlyList<SyntaxNode> VisitList(IReadOnlyList<SyntaxNode> nodes, bool normalizeText)
    {
        var result = new List<SyntaxNode>(nodes.Count);
        foreach (var n in nodes)
        {
            if (normalizeText && n is TextSyntax t)
            {
                string? text = NormalizeText(t.Text);
                if (text != null) result.Add(new TextSyntax(t.Span, text, Entities.Decode(text)));
                continue;
            }
            result.Add(Visit(n));
        }
        return result;
    }

    static SyntaxNode Visit(SyntaxNode node)
    {
        switch (node)
        {
            case ElementSyntax e:
                return new ElementSyntax(e.Span, e.Name, e.NameSpan, e.TypeArguments, e.Attributes,
                    VisitList(e.Children, true), e.IsSelfClosing, e.StartTagSpan, e.EndTagSpan);
            case FragmentSyntax f:
                return new FragmentSyntax(f.Span, VisitList(f.Children, true), f.StartTagSpan, f.EndTagSpan);
            case StatementBlockSyntax b:
                return new StatementBlockSyntax(b.Span, VisitBody(b.Body));
            case IfSyntax i:
            {
                var branches = new List<IfBranchSyntax>(i.Branches.Count);
                foreach (var br in i.Branches)
                    branches.Add(new IfBranchSyntax(br.Span, br.BranchKind, br.Condition, br.ConditionSpan, VisitBody(br.Body)));
                return new IfSyntax(i.Span, branches);
            }
            case LoopSyntax l:
                return new LoopSyntax(l.Span, l.LoopKind, l.Header, l.HeaderSpan, VisitBody(l.Body));
            case SwitchSyntax s:
            {
                var sections = new List<SwitchSectionSyntax>(s.Sections.Count);
                foreach (var sec in s.Sections)
                    sections.Add(new SwitchSectionSyntax(sec.Span, sec.Labels, VisitList(sec.Nodes, false)));
                return new SwitchSyntax(s.Span, s.Expression, s.ExpressionSpan, sections, s.HasOpenBrace, s.IsClosed);
            }
            case LineTextSyntax lt:
                return new LineTextSyntax(lt.Span, VisitList(lt.Children, false));
            default:
                return node;
        }
    }

    static CodeBodySyntax VisitBody(CodeBodySyntax body) =>
        new(body.Span, VisitList(body.Nodes, false), body.HasOpenBrace, body.IsClosed);
}

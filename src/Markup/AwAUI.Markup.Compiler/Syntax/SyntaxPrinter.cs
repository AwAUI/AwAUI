using System.Text;

namespace AwAUI.Markup.Compiler.Syntax;

/// <summary>
/// 把语法树打印为缩进文本，供调试、快照与等价比较使用。
/// <paramref name="structuralOnly"/> 模式忽略区间与纯书写差异（隐式/显式表达式、引号种类、C# 片段首尾空白），
/// 用于判断「格式化后重新解析的树与原树等价」。
/// </summary>
public static class SyntaxPrinter
{
    public static string Print(SyntaxNode node, bool includeSpans = false, bool structuralOnly = false)
    {
        var sb = new StringBuilder();
        Write(sb, node, 0, includeSpans && !structuralOnly, structuralOnly);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, SyntaxNode node, int indent, bool spans, bool structural)
    {
        sb.Append(' ', indent * 2).Append(node.Kind);
        switch (node)
        {
            case DirectiveSyntax d:
                sb.Append(' ').Append(d.Name).Append(' ').Append(Quote(d.Value));
                break;
            case CodeBlockSyntax c:
                sb.Append(' ').Append(Quote(structural ? NormalizeCode(c.Code) : c.Code));
                if (!c.IsClosed) sb.Append(" (unclosed)");
                break;
            case StyleBlockSyntax st:
                if (st.IsGlobal) sb.Append(" global");
                sb.Append(' ').Append(Quote(st.Content));
                break;
            case CommentSyntax cm:
                if (cm.IsHtml) sb.Append(" html");
                sb.Append(' ').Append(Quote(cm.Content));
                break;
            case TextSyntax t:
                sb.Append(' ').Append(Quote(t.Value));
                break;
            case ExpressionSyntax e:
                if (!structural) sb.Append(e.IsExplicit ? " explicit" : " implicit");
                sb.Append(' ').Append(Quote(e.Code));
                break;
            case ElementSyntax el:
                sb.Append(' ').Append(el.Name);
                if (el.TypeArguments != null) sb.Append('<').Append(el.TypeArguments).Append('>');
                if (el.IsSelfClosing) sb.Append(" />");
                else if (!el.IsClosed) sb.Append(" (unclosed)");
                break;
            case FragmentSyntax f:
                if (!f.IsClosed) sb.Append(" (unclosed)");
                break;
            case AttributeSyntax a:
                sb.Append(' ').Append(a.FullName);
                break;
            case AttributeValueSyntax v:
                if (!structural) sb.Append(' ').Append(v.Quote == '\0' ? "unquoted" : v.Quote.ToString());
                break;
            case AttributeLiteralSyntax l:
                sb.Append(' ').Append(Quote(structural ? l.Value : l.Text));
                break;
            case CSharpCodeSyntax cs:
                sb.Append(' ').Append(Quote(structural ? NormalizeCode(cs.Code) : cs.Code));
                break;
            case CodeBodySyntax b:
                if (!b.IsClosed) sb.Append(" (unclosed)");
                break;
            case IfBranchSyntax ib:
                sb.Append(' ').Append(ib.BranchKind);
                if (ib.Condition != null) sb.Append(' ').Append(Quote(structural ? ib.Condition.Trim() : ib.Condition));
                break;
            case LoopSyntax lp:
                sb.Append(' ').Append(lp.Keyword).Append(' ').Append(Quote(structural ? lp.Header.Trim() : lp.Header));
                break;
            case SwitchSyntax sw:
                sb.Append(' ').Append(Quote(structural ? sw.Expression.Trim() : sw.Expression));
                break;
            case SwitchLabelSyntax sl:
                sb.Append(' ').Append(Quote(structural ? sl.Text.Trim() : sl.Text));
                break;
        }
        if (spans) sb.Append(' ').Append(node.Span);
        sb.Append('\n');
        foreach (var child in node.ChildNodes())
            Write(sb, child, indent + 1, spans, structural);
    }

    /// <summary>结构比较时忽略 C# 的缩进与空行（格式化器会按花括号深度重新缩进代码）。</summary>
    static string NormalizeCode(string code)
    {
        var sb = new StringBuilder();
        foreach (var line in code.Split('\n', '\r'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t);
        }
        return sb.ToString();
    }

    static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }
}

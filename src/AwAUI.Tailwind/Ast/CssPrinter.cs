using System.Collections.Generic;
using System.Text;

namespace AwAUI.Tailwind.Ast;

/// <summary>把内部语法树打印为 CSS 文本：默认与 Tailwind <c>toCss</c> 逐字一致（两空格缩进），也可压缩输出。</summary>
internal static class CssPrinter
{
    public static string Print(List<AstNode> ast, bool minify)
    {
        var sb = new StringBuilder();
        if (minify)
        {
            foreach (var n in ast) PrintMinified(n, sb);
        }
        else
        {
            foreach (var n in ast) PrintPretty(n, 0, sb);
        }
        return sb.ToString();
    }

    static void Indent(StringBuilder sb, int depth)
    {
        for (int i = 0; i < depth; i++) sb.Append("  ");
    }

    static void PrintPretty(AstNode node, int depth, StringBuilder sb)
    {
        switch (node.Kind)
        {
            case AstKind.Declaration:
                Indent(sb, depth);
                sb.Append(node.Property).Append(": ").Append(node.Value);
                if (node.Important) sb.Append(" !important");
                sb.Append(";\n");
                break;
            case AstKind.Rule:
                Indent(sb, depth);
                sb.Append(node.Selector).Append(" {\n");
                foreach (var c in node.Nodes) PrintPretty(c, depth + 1, sb);
                Indent(sb, depth);
                sb.Append("}\n");
                break;
            case AstKind.AtRule:
                Indent(sb, depth);
                if (node.Nodes.Count == 0)
                {
                    sb.Append(node.Name).Append(' ').Append(node.Params).Append(";\n");
                    break;
                }
                sb.Append(node.Name);
                if (node.Params.Length > 0) sb.Append(' ').Append(node.Params).Append(' ');
                else sb.Append(' ');
                sb.Append("{\n");
                foreach (var c in node.Nodes) PrintPretty(c, depth + 1, sb);
                Indent(sb, depth);
                sb.Append("}\n");
                break;
            case AstKind.Comment:
                Indent(sb, depth);
                sb.Append("/*").Append(node.Value).Append("*/\n");
                break;
        }
    }

    static void PrintMinified(AstNode node, StringBuilder sb)
    {
        switch (node.Kind)
        {
            case AstKind.Declaration:
                sb.Append(node.Property).Append(':').Append(node.Value);
                if (node.Important) sb.Append("!important");
                sb.Append(';');
                break;
            case AstKind.Rule:
                sb.Append(node.Selector).Append('{');
                foreach (var c in node.Nodes) PrintMinified(c, sb);
                TrimSemicolon(sb);
                sb.Append('}');
                break;
            case AstKind.AtRule:
                if (node.Nodes.Count == 0)
                {
                    sb.Append(node.Name).Append(' ').Append(node.Params).Append(';');
                    break;
                }
                sb.Append(node.Name);
                if (node.Params.Length > 0) sb.Append(' ').Append(node.Params);
                sb.Append('{');
                foreach (var c in node.Nodes) PrintMinified(c, sb);
                TrimSemicolon(sb);
                sb.Append('}');
                break;
            case AstKind.Comment:
                // 只保留许可证注释（/*! ... */）
                if (node.Value != null && node.Value.Length > 0 && node.Value[0] == '!') sb.Append("/*").Append(node.Value).Append("*/");
                break;
        }
    }

    static void TrimSemicolon(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] == ';') sb.Length -= 1;
    }
}

using System.Collections.Generic;
using System.Text;

namespace AwAUI.Tailwind.Internal;

/// <summary>任意值（方括号内）的校验与解码：下划线转空格、数学运算符补空格（移植自 Tailwind <c>decode-arbitrary-value.ts</c>）。</summary>
internal static class ArbitraryValue
{
    /// <summary>括号是否配对、顶层是否没有 <c>;</c>（Tailwind <c>isValidArbitrary</c>）。</summary>
    public static bool IsValid(string input)
    {
        var stack = new List<char>();
        int len = input.Length;
        for (int i = 0; i < len; i++)
        {
            char c = input[i];
            switch (c)
            {
                case '\\':
                    i += 1;
                    break;
                case '\'':
                case '"':
                    while (++i < len)
                    {
                        char n = input[i];
                        if (n == '\\')
                        {
                            i += 1;
                            continue;
                        }
                        if (n == c) break;
                    }
                    break;
                case '(':
                    stack.Add(')');
                    if (stack.Count > Limits.MaxValueNesting) return false;
                    break;
                case '[':
                    stack.Add(']');
                    if (stack.Count > Limits.MaxValueNesting) return false;
                    break;
                case '{':
                    break;
                case ']':
                case '}':
                case ')':
                    if (stack.Count == 0) return false;
                    if (c == stack[stack.Count - 1]) stack.RemoveAt(stack.Count - 1);
                    else if (c != '}') return false;
                    break;
                case ';':
                    if (stack.Count == 0) return false;
                    break;
            }
        }
        return stack.Count == 0;
    }

    /// <summary>校验并解码；非法时返回 null（Tailwind 中的 <c>Le</c>）。</summary>
    public static string? DecodeChecked(string input) => IsValid(input) ? Decode(input) : null;

    /// <summary>解码任意值：<c>_</c> → 空格（<c>\_</c> 保留下划线；<c>url()</c> 与 <c>var()</c> 的变量名不转换），再给数学函数里的运算符补空格。</summary>
    public static string Decode(string input)
    {
        if (input.IndexOf('(') == -1) return ConvertUnderscores(input, false);
        var ast = ValueParser.Parse(input);
        RecursivelyDecode(ast, 0);
        input = ValueParser.ToCss(ast);
        return DataTypes.AddWhitespaceAroundMathOperators(input);
    }

    public static string ConvertUnderscores(string input, bool skipUnderscoreToSpace)
    {
        if (input.IndexOf('_') < 0) return input;
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\\' && i + 1 < input.Length && input[i + 1] == '_')
            {
                sb.Append('_');
                i += 1;
            }
            else if (c == '_' && !skipUnderscoreToSpace) sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static void RecursivelyDecode(List<ValueNode> ast, int depth)
    {
        if (depth > Limits.MaxValueNesting) return;
        foreach (var node in ast)
        {
            switch (node.Kind)
            {
                case ValueKind.Function:
                {
                    if (node.Value == "url" || node.Value.EndsWith("_url", System.StringComparison.Ordinal))
                    {
                        node.Value = ConvertUnderscores(node.Value, false);
                        break;
                    }
                    if (node.Value == "var" || node.Value.EndsWith("_var", System.StringComparison.Ordinal) || node.Value == "theme" || node.Value.EndsWith("_theme", System.StringComparison.Ordinal))
                    {
                        node.Value = ConvertUnderscores(node.Value, false);
                        for (int i = 0; i < node.Nodes!.Count; i++)
                        {
                            if (i == 0 && node.Nodes[i].Kind == ValueKind.Word)
                            {
                                node.Nodes[i].Value = ConvertUnderscores(node.Nodes[i].Value, true);
                                continue;
                            }
                            RecursivelyDecode(new List<ValueNode> { node.Nodes[i] }, depth + 1);
                        }
                        break;
                    }
                    node.Value = ConvertUnderscores(node.Value, false);
                    RecursivelyDecode(node.Nodes!, depth + 1);
                    break;
                }
                case ValueKind.Separator:
                case ValueKind.Word:
                    node.Value = ConvertUnderscores(node.Value, false);
                    break;
            }
        }
    }
}

/// <summary>敌意输入的上限（候选长度、变体深度、嵌套深度、@apply 深度等）。</summary>
internal static class Limits
{
    /// <summary>单个候选类名的最大长度（字符）。超出的候选直接视为无效。</summary>
    public const int MaxCandidateLength = 1024;

    /// <summary>单个候选的最大变体数。</summary>
    public const int MaxVariants = 32;

    /// <summary>任意值、CSS 值中括号的最大嵌套深度。</summary>
    public const int MaxValueNesting = 128;

    /// <summary>CSS 输入中规则块的最大嵌套深度。</summary>
    public const int MaxBlockNesting = 128;

    /// <summary>@import 的最大递归深度（与 Tailwind 相同）。</summary>
    public const int MaxImportDepth = 100;

    /// <summary>复合变体（group-*、has-*、not-* ...）的最大嵌套层数。</summary>
    public const int MaxCompoundDepth = 16;
}

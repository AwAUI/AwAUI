using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Ast;

/// <summary>
/// 宽松的 CSS 解析器（移植自 Tailwind <c>css-parser.ts</c>）：只区分规则、at 规则、声明与 <c>/*! */</c> 注释，
/// 不理解属性值；支持原生嵌套。输入错误抛出 <see cref="TailwindException"/>；嵌套深度有上限。
/// </summary>
internal static class CssParser
{
    public static List<AstNode> Parse(string input)
    {
        if (input.Length > 0 && input[0] == '﻿') input = " " + input.Substring(1);
        var ast = new List<AstNode>();
        var licenseComments = new List<AstNode>();
        var stack = new List<AstNode?>();
        AstNode? parent = null;
        var buffer = new StringBuilder();
        var closingBracketStack = new StringBuilder();
        int len = input.Length;

        for (int i = 0; i < len; i++)
        {
            char c = input[i];
            if (c == '\r' && i + 1 < len && input[i + 1] == '\n') continue;

            if (c == '\\')
            {
                buffer.Append(c);
                if (i + 1 < len) buffer.Append(input[i + 1]);
                i += 1;
            }
            else if (c == '/' && i + 1 < len && input[i + 1] == '*')
            {
                int start = i;
                int end = -1;
                for (int j = i + 2; j < len; j++)
                {
                    if (input[j] == '*' && j + 1 < len && input[j + 1] == '/')
                    {
                        end = j + 1;
                        break;
                    }
                }
                if (end == -1) end = len - 1;
                i = end;
                string comment = input.Substring(start, end - start + 1);
                if (comment.Length >= 4 && comment[2] == '!') licenseComments.Add(AstNode.Comment(comment.Substring(2, comment.Length - 4)));
            }
            else if (c == '\'' || c == '"')
            {
                int end = ParseString(input, i, c);
                buffer.Append(input, i, end - i + 1);
                i = end;
            }
            else
            {
                if ((c == ' ' || c == '\n' || c == '\t') && i + 1 < len)
                {
                    char p = input[i + 1];
                    if (p == ' ' || p == '\n' || p == '\t' || (p == '\r' && i + 2 < len && input[i + 2] == '\n')) continue;
                }
                if (c == '\n')
                {
                    if (buffer.Length == 0) continue;
                    char last = buffer[buffer.Length - 1];
                    if (last != ' ' && last != '\n' && last != '\t') buffer.Append(' ');
                }
                else if (parent == null && buffer.Length == 0 &&
                         ((c == '-' && At(input, i + 1) == '-' && At(input, i + 2) == '>') ||
                          (c == '<' && At(input, i + 1) == '!' && At(input, i + 2) == '-' && At(input, i + 3) == '-')))
                {
                    // 跳过 HTML 注释标记（与 Tailwind 一致）
                    i += c == '-' ? 2 : 3;
                }
                else if (c == '-' && At(input, i + 1) == '-' && buffer.Length == 0)
                {
                    // 自定义属性：值可以包含 {}、; 等任意内容，需整体读取
                    var closing = new StringBuilder();
                    int start = i;
                    int colonIdx = -1;
                    int valueEnd = 0;
                    var value = new StringBuilder();
                    bool done = false;
                    for (int j = i + 2; j < len; j++)
                    {
                        char p = input[j];
                        if (p == '\\') j += 1;
                        else if (p == '\'' || p == '"') j = ParseString(input, j, p);
                        else if (p == '/' && At(input, j + 1) == '*')
                        {
                            for (int k = j + 2; k < len; k++)
                            {
                                if (input[k] == '*' && At(input, k + 1) == '/')
                                {
                                    j = k + 1;
                                    break;
                                }
                            }
                            continue;
                        }
                        else if (colonIdx == -1 && p == ':') colonIdx = value.Length + j - start;
                        else if (p == ';' && closing.Length == 0)
                        {
                            value.Append(input, start, j - start);
                            i = j;
                            done = true;
                            break;
                        }
                        else if (p == '(') closing.Append(')');
                        else if (p == '[') closing.Append(']');
                        else if (p == '{') closing.Append('}');
                        else if (p == '}' && closing.Length == 0)
                        {
                            i = j - 1;
                            value.Append(input, start, j - start);
                            done = true;
                            break;
                        }
                        else if ((p == ')' || p == ']' || p == '}') && closing.Length > 0 && p == closing[closing.Length - 1])
                        {
                            closing.Length -= 1;
                        }
                        if (closing.Length > Limits.MaxValueNesting) throw new TailwindException("Custom property value is nested too deeply.");
                        if (p != ' ' && p != '\t' && p != '\n' && p != '\r') valueEnd = j + 1 - start;
                    }
                    if (!done)
                    {
                        value.Clear();
                        value.Append(input, start, len - start);
                        i = len;
                    }
                    var decl = ParseDeclaration(value.ToString(), colonIdx, valueEnd);
                    if (decl == null) throw new TailwindException("Invalid custom property, expected a value");
                    if (parent != null) parent.Nodes.Add(decl);
                    else ast.Add(decl);
                    buffer.Clear();
                }
                else if (c == ';' && buffer.Length > 0 && buffer[0] == '@')
                {
                    var node = AstNode.ParseAtRule(buffer.ToString(), new List<AstNode>());
                    if (parent != null) parent.Nodes.Add(node);
                    else ast.Add(node);
                    buffer.Clear();
                }
                else if (c == ';' && (closingBracketStack.Length == 0 || closingBracketStack[closingBracketStack.Length - 1] != ')'))
                {
                    var decl = ParseDeclaration(buffer.ToString());
                    if (decl == null)
                    {
                        if (buffer.Length == 0) continue;
                        throw new TailwindException($"Invalid declaration: `{buffer.ToString().Trim()}`");
                    }
                    if (parent != null) parent.Nodes.Add(decl);
                    else ast.Add(decl);
                    buffer.Clear();
                }
                else if (c == '{' && (closingBracketStack.Length == 0 || closingBracketStack[closingBracketStack.Length - 1] != ')'))
                {
                    closingBracketStack.Append('}');
                    if (closingBracketStack.Length > Limits.MaxBlockNesting) throw new TailwindException("CSS is nested too deeply.");
                    var node = AstNode.RuleOrAtRule(buffer.ToString().Trim());
                    parent?.Nodes.Add(node);
                    stack.Add(parent);
                    parent = node;
                    buffer.Clear();
                }
                else if (c == '}' && (closingBracketStack.Length == 0 || closingBracketStack[closingBracketStack.Length - 1] != ')'))
                {
                    if (closingBracketStack.Length == 0) throw new TailwindException("Missing opening {");
                    closingBracketStack.Length -= 1;
                    if (buffer.Length > 0)
                    {
                        if (buffer[0] == '@')
                        {
                            var node = AstNode.ParseAtRule(buffer.ToString(), new List<AstNode>());
                            if (parent != null) parent.Nodes.Add(node);
                            else ast.Add(node);
                            buffer.Clear();
                        }
                        else
                        {
                            string text = buffer.ToString();
                            int colon = text.IndexOf(':');
                            if (parent != null)
                            {
                                var decl = ParseDeclaration(text, colon);
                                if (decl == null) throw new TailwindException($"Invalid declaration: `{text.Trim()}`");
                                parent.Nodes.Add(decl);
                            }
                        }
                    }
                    AstNode? grand = null;
                    if (stack.Count > 0)
                    {
                        grand = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                    if (grand == null && parent != null) ast.Add(parent);
                    parent = grand;
                    buffer.Clear();
                }
                else if (c == '(')
                {
                    closingBracketStack.Append(')');
                    if (closingBracketStack.Length > Limits.MaxValueNesting + Limits.MaxBlockNesting) throw new TailwindException("CSS is nested too deeply.");
                    buffer.Append('(');
                }
                else if (c == ')')
                {
                    if (closingBracketStack.Length == 0 || closingBracketStack[closingBracketStack.Length - 1] != ')') throw new TailwindException("Missing opening (");
                    closingBracketStack.Length -= 1;
                    buffer.Append(')');
                }
                else
                {
                    if (buffer.Length == 0 && (c == ' ' || c == '\n' || c == '\t')) continue;
                    buffer.Append(c);
                }
            }
        }

        if (buffer.Length > 0 && buffer[0] == '@')
        {
            ast.Add(AstNode.ParseAtRule(buffer.ToString(), new List<AstNode>()));
        }
        else if (buffer.Length > 0 && closingBracketStack.Length == 0)
        {
            var decl = ParseDeclaration(buffer.ToString());
            if (decl != null)
            {
                if (parent != null) parent.Nodes.Add(decl);
                else ast.Add(decl);
            }
        }

        if (closingBracketStack.Length > 0 && parent != null)
        {
            if (parent.Kind == AstKind.Rule) throw new TailwindException($"Missing closing }} at {parent.Selector}");
            if (parent.Kind == AstKind.AtRule) throw new TailwindException($"Missing closing }} at {parent.Name} {parent.Params}");
        }

        if (licenseComments.Count > 0)
        {
            licenseComments.AddRange(ast);
            return licenseComments;
        }
        return ast;
    }

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>返回闭合引号的位置；字符串跨行时报错。</summary>
    static int ParseString(string input, int start, char quote)
    {
        for (int i = start + 1; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\\')
            {
                i += 1;
                continue;
            }
            if (c == quote) return i;
            if (c == ';' && (At(input, i + 1) == '\n' || (At(input, i + 1) == '\r' && At(input, i + 2) == '\n')))
                throw new TailwindException($"Unterminated string: {input.Substring(start, i - start + 1)}{quote}");
            if (c == '\n' || (c == '\r' && At(input, i + 1) == '\n'))
                throw new TailwindException($"Unterminated string: {input.Substring(start, i - start)}{quote}");
        }
        return start;
    }

    /// <summary>解析 <c>prop: value [!important]</c>；没有冒号时返回 null。</summary>
    public static AstNode? ParseDeclaration(string buffer, int colonIdx = -2, int end = -1)
    {
        if (colonIdx == -2) colonIdx = buffer.IndexOf(':');
        if (colonIdx == -1) return null;
        if (end < 0 || end > buffer.Length) end = buffer.Length;
        string property = buffer.Substring(0, colonIdx).Trim();
        string value = TrimValue(buffer.Slice(colonIdx + 1, end));
        int importantIdx = value.Length - 10;
        if (value.EndsWith("!important", StringComparison.Ordinal) && !IsEscaped(value, importantIdx))
            return AstNode.Decl(property, TrimValue(value.Substring(0, importantIdx)), true);
        return AstNode.Decl(property, TrimValue(buffer.Substring(colonIdx + 1)), false);
    }

    static string TrimValue(string s)
    {
        s = s.TrimStart();
        string trimmed = s.TrimEnd();
        if (trimmed.Length < s.Length && IsEscaped(s, trimmed.Length)) return s.Substring(0, trimmed.Length + 1);
        return trimmed;
    }

    static bool IsEscaped(string s, int index)
    {
        int count = 0;
        while (index - 1 - count >= 0 && s[index - 1 - count] == '\\') count++;
        return count % 2 == 1;
    }
}

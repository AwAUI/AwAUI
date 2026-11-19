using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Markup.Compiler.Styles;

/// <summary>
/// 作用域样式的选择器改写（design/12「作用域样式」）：给每条样式规则选择器的<b>最后一个复合选择器</b>附加作用域属性选择器
/// <c>[aw-xxxxxxxx]</c>，使规则只匹配本组件 <c>Build()</c> 直接产出的元素（它们带有同名的空值属性）。
/// <list type="bullet">
/// <item>伪元素之前插入：<c>.a::before</c> → <c>.a[s]::before</c>（含旧式单冒号 <c>:before :after :first-line :first-letter</c>）；
/// <c>x::part(p)</c> 同理作用在宿主元素上。</item>
/// <item>函数式伪类不展开：<c>:is(.a, .b)</c> → <c>:is(.a, .b)[s]</c>，<c>:where()</c>、<c>:not()</c>、<c>:has()</c> 相同，所以特异性只增加一个属性选择器。</item>
/// <item>CSS 嵌套：嵌套规则（含 <c>&amp;</c> 与相对选择器 <c>&gt; .b</c>）同样改写其最后一个复合选择器；<c>&amp;</c> 已带作用域，重复附加无害。</item>
/// <item>条件组规则 <c>@media @supports @layer @container @scope @starting-style @document</c> 的内容递归处理；
/// <c>@keyframes @font-face @property @page @counter-style</c> 等原样保留（其中没有选择器）。</item>
/// </list>
/// 只做插入，其余文本（含注释、错误的规则）原样保留；CSS 的完整解析与诊断仍由运行时的 CSS 引擎负责（AW2xxx）。
/// 实现为单遍扫描 + 显式栈：任意输入线性时间、不递归、不抛异常。
/// </summary>
public static class ScopedCss
{
    /// <summary>由组件的全名（命名空间 + 类名）得到稳定的作用域属性名：<c>aw-</c> + FNV-1a 32 位哈希的 8 位十六进制。</summary>
    public static string ScopeAttributeFor(string componentFullName)
    {
        uint h = 2166136261;
        foreach (char c in componentFullName)
        {
            h ^= c;
            h *= 16777619;
        }
        return "aw-" + h.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>改写 <paramref name="css"/> 中全部样式规则的选择器，附加 <c>[<paramref name="scopeAttribute"/>]</c>。</summary>
    public static string Rewrite(string css, string scopeAttribute)
    {
        if (css is null) throw new ArgumentNullException(nameof(css));
        if (string.IsNullOrEmpty(scopeAttribute)) throw new ArgumentException("作用域属性名不能为空", nameof(scopeAttribute));
        return new Rewriter(css, "[" + scopeAttribute + "]").Run();
    }

    enum Block : byte
    {
        /// <summary>规则列表：顶层或条件组规则内。</summary>
        Rules,
        /// <summary>声明列表：样式规则内，可含嵌套规则。</summary>
        Declarations,
    }

    sealed class Rewriter
    {
        readonly string _s;
        readonly string _scope;
        readonly StringBuilder _out;
        readonly List<Block> _stack = new();
        int _pos;
        int _copied;

        public Rewriter(string s, string scope)
        {
            _s = s;
            _scope = scope;
            _out = new StringBuilder(s.Length + 64);
        }

        Block Current => _stack.Count == 0 ? Block.Rules : _stack[_stack.Count - 1];

        public string Run()
        {
            while (true)
            {
                SkipTrivia();
                if (_pos >= _s.Length) break;
                char c = _s[_pos];
                if (c == '}')
                {
                    if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
                    _pos++;
                    continue;
                }
                if (c == ';')
                {
                    _pos++;
                    continue;
                }
                if (c == '@')
                {
                    AtRule();
                    continue;
                }
                if (Current == Block.Rules) QualifiedRule();
                else DeclarationOrNestedRule();
            }
            _out.Append(_s, _copied, _s.Length - _copied);
            return _out.ToString();
        }

        void AtRule()
        {
            int nameStart = _pos + 1;
            int i = nameStart;
            while (i < _s.Length && IsNameChar(_s[i])) i++;
            string name = _s.Substring(nameStart, i - nameStart).ToLowerInvariant();
            if (name.StartsWith("-", StringComparison.Ordinal))
            {
                // 带厂商前缀的 at 规则（-webkit-keyframes ...）按去掉前缀的名称处理
                int dash = name.IndexOf('-', 1);
                if (dash > 0) name = name.Substring(dash + 1);
            }
            int end = ScanPrelude(i, out char stop);
            _pos = end;
            if (stop != '{')
            {
                if (stop == ';') _pos++;
                return; // 语句式 at 规则（@import、@layer a, b; ...）或在 } 处结束
            }
            _pos++; // {
            switch (name)
            {
                case "media":
                case "supports":
                case "layer":
                case "container":
                case "scope":
                case "starting-style":
                case "document":
                    // 条件组规则：在声明列表中嵌套时内容仍是声明 + 嵌套规则
                    _stack.Add(Current == Block.Declarations ? Block.Declarations : Block.Rules);
                    break;
                default:
                    // @keyframes、@font-face、@page ...：没有需要改写的选择器，原样跳过整个块
                    _pos = SkipBlock(_pos);
                    break;
            }
        }

        void QualifiedRule()
        {
            int start = _pos;
            int end = ScanPrelude(start, out char stop);
            if (stop != '{')
            {
                // 没有块的规则（语法错误）：原样保留，由运行时的 CSS 解析器报告
                _pos = stop == ';' ? end + 1 : end;
                return;
            }
            RewriteSelectorList(start, end);
            _pos = end + 1;
            _stack.Add(Block.Declarations);
        }

        void DeclarationOrNestedRule()
        {
            int start = _pos;
            bool custom = _pos + 1 < _s.Length && _s[_pos] == '-' && _s[_pos + 1] == '-';
            if (custom)
            {
                // 自定义属性的值可以含 {}：平衡地扫描到 ; 或所在块的 }
                _pos = ScanDeclarationValue(_pos);
                return;
            }
            int end = ScanPrelude(start, out char stop);
            if (stop == '{')
            {
                RewriteSelectorList(start, end);
                _pos = end + 1;
                _stack.Add(Block.Declarations);
                return;
            }
            _pos = stop == ';' ? end + 1 : end;
        }

        /// <summary>
        /// 从 <paramref name="from"/> 扫描到顶层的 <c>{</c>、<c>;</c> 或 <c>}</c>（跳过字符串、注释、括号），返回其位置；
        /// <paramref name="stop"/> 为遇到的字符，文本结束时为 <c>\0</c>。
        /// </summary>
        int ScanPrelude(int from, out char stop)
        {
            int depth = 0;
            int i = from;
            while (i < _s.Length)
            {
                char c = _s[i];
                int skipped = SkipAtom(i);
                if (skipped != i)
                {
                    i = skipped;
                    continue;
                }
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                else if (depth == 0 && (c == '{' || c == ';' || c == '}'))
                {
                    stop = c;
                    return i;
                }
                i++;
            }
            stop = '\0';
            return i;
        }

        /// <summary>自定义属性声明：扫描到顶层的 <c>;</c>（含）或不配对的 <c>}</c>（不含）。</summary>
        int ScanDeclarationValue(int from)
        {
            int depth = 0;
            int i = from;
            while (i < _s.Length)
            {
                int skipped = SkipAtom(i);
                if (skipped != i)
                {
                    i = skipped;
                    continue;
                }
                char c = _s[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) return c == '}' ? i : i + 1;
                    depth--;
                }
                else if (c == ';' && depth == 0) return i + 1;
                i++;
            }
            return i;
        }

        /// <summary>从块内（<c>{</c> 之后）跳到配对的 <c>}</c> 之后。</summary>
        int SkipBlock(int from)
        {
            int depth = 0;
            int i = from;
            while (i < _s.Length)
            {
                int skipped = SkipAtom(i);
                if (skipped != i)
                {
                    i = skipped;
                    continue;
                }
                char c = _s[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) return i + 1;
                    depth--;
                }
                i++;
            }
            return i;
        }

        void SkipTrivia()
        {
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (IsWhitespace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '*')
                {
                    _pos = SkipAtom(_pos);
                }
                else if (c == '<' && string.CompareOrdinal(_s, _pos, "<!--", 0, 4) == 0)
                {
                    _pos += 4; // CDO / CDC 在样式表顶层被忽略
                }
                else if (c == '-' && string.CompareOrdinal(_s, _pos, "-->", 0, 3) == 0)
                {
                    _pos += 3;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>若 <paramref name="i"/> 处是字符串、注释或转义，返回其结束位置；否则返回 <paramref name="i"/>。</summary>
        int SkipAtom(int i)
        {
            char c = _s[i];
            if (c == '/' && i + 1 < _s.Length && _s[i + 1] == '*')
            {
                int end = _s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return end < 0 ? _s.Length : end + 2;
            }
            if (c == '"' || c == '\'')
            {
                int j = i + 1;
                while (j < _s.Length)
                {
                    char d = _s[j];
                    if (d == c) return j + 1;
                    if (d == '\\') j += 2;
                    else if (d == '\n' || d == '\r' || d == '\f') return j; // 坏字符串在换行处结束
                    else j++;
                }
                return _s.Length;
            }
            if (c == '\\') return Math.Min(i + 2, _s.Length);
            return i;
        }

        // ------------------------------------------------------------ 选择器

        /// <summary>改写 [start, end) 中逗号分隔的选择器列表：把插入点之前的文本拷贝到输出，再写入作用域选择器。</summary>
        void RewriteSelectorList(int start, int end)
        {
            int depth = 0;
            int itemStart = start;
            int i = start;
            while (i < end)
            {
                int skipped = SkipAtom(i);
                if (skipped != i)
                {
                    i = Math.Min(skipped, end);
                    continue;
                }
                char c = _s[i];
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                else if (c == ',' && depth == 0)
                {
                    RewriteComplex(itemStart, i);
                    itemStart = i + 1;
                }
                i++;
            }
            RewriteComplex(itemStart, end);
        }

        /// <summary>在一个复杂选择器 [start, end) 的最后一个复合选择器中找到插入点并插入作用域。</summary>
        void RewriteComplex(int start, int end)
        {
            // 去掉末尾的空白与注释
            int last = -1; // 最后一个有意义字符之后的位置
            int compoundStart = -1;
            bool pendingBoundary = true;
            int depth = 0;
            int i = start;
            while (i < end)
            {
                char c = _s[i];
                if (c == '/' && i + 1 < end && _s[i + 1] == '*')
                {
                    i = Math.Min(SkipAtom(i), end);
                    if (depth == 0) pendingBoundary = true;
                    continue;
                }
                if (depth == 0 && IsWhitespace(c))
                {
                    pendingBoundary = true;
                    i++;
                    continue;
                }
                if (depth == 0 && (c == '>' || c == '+' || c == '~' || (c == '|' && i + 1 < end && _s[i + 1] == '|')))
                {
                    // 组合符：其后开始新的复合选择器
                    pendingBoundary = true;
                    compoundStart = -1;
                    i += c == '|' ? 2 : 1;
                    continue;
                }
                if (pendingBoundary && depth == 0)
                {
                    compoundStart = i;
                    pendingBoundary = false;
                }
                int skipped = SkipAtom(i);
                if (skipped != i)
                {
                    i = Math.Min(skipped, end);
                    last = i;
                    continue;
                }
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                i++;
                last = i;
            }
            if (compoundStart < 0 || last <= compoundStart) return; // 空选择器或以组合符结尾：保持原样

            int insertAt = FindPseudoElement(compoundStart, last);
            _out.Append(_s, _copied, insertAt - _copied);
            _out.Append(_scope);
            _copied = insertAt;
        }

        /// <summary>复合选择器 [start, end) 中第一个顶层伪元素的位置；没有时返回 <paramref name="end"/>。</summary>
        int FindPseudoElement(int start, int end)
        {
            int depth = 0;
            int i = start;
            while (i < end)
            {
                int skipped = SkipAtom(i);
                if (skipped != i)
                {
                    i = skipped;
                    continue;
                }
                char c = _s[i];
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                else if (c == ':' && depth == 0)
                {
                    if (i + 1 < end && _s[i + 1] == ':') return i;
                    int n = i + 1;
                    while (n < end && IsNameChar(_s[n])) n++;
                    string name = _s.Substring(i + 1, n - i - 1).ToLowerInvariant();
                    if (name == "before" || name == "after" || name == "first-line" || name == "first-letter") return i;
                }
                i++;
            }
            return end;
        }

        static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        static bool IsNameChar(char c) => c == '-' || c == '_' || char.IsLetterOrDigit(c) || c > 0x7F;
    }
}

using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Selectors;

/// <summary>
/// Selectors Level 4 选择器解析（在组件值上工作），含 CSS Nesting 的相对选择器与 <c>&amp;</c>。
/// 不支持或未知的特性产生诊断（<see cref="CssDiagnosticCodes.UnsupportedSelector"/>），语法错误产生
/// <see cref="CssDiagnosticCodes.InvalidSelector"/>；按规范整条选择器（及其规则）无效，宽容列表中则只丢弃该项。
/// </summary>
public static class SelectorParser
{
    /// <summary>函数式伪类（<c>:is()</c>、<c>:not()</c>、<c>:where()</c>、<c>:nth-*(… of S)</c>）的最大嵌套层数，超过即为语法错误，避免解析时栈溢出。</summary>
    internal const int MaxFunctionNesting = 32;

    /// <summary>
    /// 匹配递归深度上限（复合选择器个数与嵌套层数之和，含 <c>&amp;</c> 引用的父规则选择器），超过时整条规则被丢弃，
    /// 保证匹配在 1 MB 线程栈上也不会溢出。
    /// </summary>
    internal const int MaxMatchDepth = 1024;

    /// <summary>解析独立的选择器列表文本（非嵌套上下文）。无效时返回 null，原因写入 <paramref name="diagnostics"/>。</summary>
    public static SelectorList? Parse(string text, List<CssDiagnostic>? diagnostics = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var d = diagnostics ?? new List<CssDiagnostic>();
        var values = new CssParser(new CssSourceText(text), d).ConsumeAllComponentValues();
        var range = values.Count > 0 ? new SourceRange(values[0].Range.Start, values[values.Count - 1].Range.End) : default;
        return ParseRulePrelude(values, null, false, d, range);
    }

    /// <summary>
    /// 解析样式规则的序言。<paramref name="nested"/> 为真时按 &lt;relative-selector-list&gt; 解析，
    /// <c>&amp;</c> 绑定到 <paramref name="parent"/>。
    /// </summary>
    internal static SelectorList? ParseRulePrelude(IReadOnlyList<CssComponentValue> prelude, SelectorList? parent, bool nested,
        List<CssDiagnostic> diagnostics, SourceRange preludeRange)
    {
        var p = new Parser(parent, diagnostics);
        var list = p.ParseList(prelude, 0, prelude.Count, forgiving: false, relative: nested, preludeRange);
        if (list is not null && list.MatchDepth > MaxMatchDepth)
        {
            diagnostics.Add(new CssDiagnostic(CssDiagnosticCodes.InvalidSelector, CssDiagnosticSeverity.Error,
                $"选择器过于复杂：复合选择器数量与嵌套层数（含父规则）之和超过 {MaxMatchDepth}，整条规则被丢弃。请拆分选择器或减少嵌套。",
                preludeRange));
            return null;
        }
        if (list is null) p.ReportFailure(CssDiagnosticSeverity.Error, "，整条规则被丢弃");
        return list;
    }

    sealed class Parser
    {
        readonly SelectorList? _parent;
        readonly List<CssDiagnostic> _d;
        int _nestingCount;
        int _functionDepth;

        // 最近一次失败的原因
        string _failCode = CssDiagnosticCodes.InvalidSelector;
        string _failMessage = "无效的选择器";
        SourceRange _failRange;

        public Parser(SelectorList? parent, List<CssDiagnostic> diagnostics)
        {
            _parent = parent;
            _d = diagnostics;
        }

        public void ReportFailure(CssDiagnosticSeverity severity, string suffix)
            => _d.Add(new CssDiagnostic(_failCode, severity, _failMessage + suffix + "。", _failRange));

        T? Fail<T>(string message, SourceRange range, string code = CssDiagnosticCodes.InvalidSelector) where T : class
        {
            _failCode = code;
            _failMessage = message;
            _failRange = range;
            return null;
        }

        bool Fail(string message, SourceRange range, string code = CssDiagnosticCodes.InvalidSelector)
        {
            Fail<object>(message, range, code);
            return false;
        }

        static bool IsWs(CssComponentValue v) => v is CssToken { Type: CssTokenType.Whitespace };

        // ---------- 列表 ----------

        public SelectorList? ParseList(IReadOnlyList<CssComponentValue> v, int start, int end, bool forgiving, bool relative, SourceRange wholeRange)
        {
            var result = new List<ComplexSelector>();
            int segStart = start;
            for (int i = start; i <= end; i++)
            {
                if (i < end && v[i] is not CssToken { Type: CssTokenType.Comma }) continue;
                var cs = ParseComplex(v, segStart, i, relative, i < end ? v[i].Range.Start : wholeRange.End, wholeRange);
                if (cs is null)
                {
                    if (!forgiving) return null;
                    ReportFailure(CssDiagnosticSeverity.Warning, "，该项在宽容选择器列表中被忽略");
                }
                else
                {
                    result.Add(cs);
                }
                segStart = i + 1;
            }
            return new SelectorList(result);
        }

        // ---------- 复杂选择器 ----------

        ComplexSelector? ParseComplex(IReadOnlyList<CssComponentValue> v, int start, int end, bool relative, SourcePosition endPos, SourceRange wholeRange)
        {
            int pos = start;
            while (pos < end && IsWs(v[pos])) pos++;
            int last = end - 1;
            while (last >= pos && IsWs(v[last])) last--;
            end = last + 1;
            if (pos >= end)
            {
                var at = start < v.Count ? v[start].Range.Start : (v.Count == 0 ? wholeRange.Start : endPos);
                return Fail<ComplexSelector>("缺少选择器", new SourceRange(at, at));
            }
            var range = new SourceRange(v[pos].Range.Start, v[end - 1].Range.End);

            int nestingBefore = _nestingCount;
            Combinator? leading = null;
            if (TryCombinator(v, pos, out var lc))
            {
                if (!relative) return Fail<ComplexSelector>($"选择器不能以组合器 '{v[pos]}' 开头", v[pos].Range);
                leading = lc;
                pos++;
                while (pos < end && IsWs(v[pos])) pos++;
            }

            var compounds = new List<CompoundSelector>();
            var combinators = new List<Combinator>();
            while (true)
            {
                var compound = ParseCompound(v, ref pos, end);
                if (compound is null) return null;
                compounds.Add(compound);
                bool sawWs = false;
                while (pos < end && IsWs(v[pos])) { pos++; sawWs = true; }
                if (pos >= end) break;
                if (TryCombinator(v, pos, out var comb))
                {
                    pos++;
                    while (pos < end && IsWs(v[pos])) pos++;
                    if (pos >= end) return Fail<ComplexSelector>("组合器后缺少选择器", v[end - 1].Range);
                    combinators.Add(comb);
                }
                else if (sawWs)
                {
                    combinators.Add(Combinator.Descendant);
                }
                else
                {
                    return Fail<ComplexSelector>($"选择器中出现意外的 '{v[pos]}'", v[pos].Range);
                }
            }

            for (int i = 0; i < compounds.Count - 1; i++)
            {
                if (compounds[i].PseudoElement is not null)
                    return Fail<ComplexSelector>("伪元素必须位于选择器末尾，其后不能再接组合器", range);
            }

            bool containsNesting = _nestingCount > nestingBefore;
            bool implicitNesting = false;
            if (relative && (leading is not null || !containsNesting))
            {
                // 嵌套规则：不含 & 或以组合器开头的选择器前补隐式的 &
                compounds.Insert(0, new CompoundSelector(new SimpleSelector[] { new NestingSelector(_parent) }, null,
                    CompoundSelector.NoSelectors, isImplicit: true));
                combinators.Insert(0, leading ?? Combinator.Descendant);
                implicitNesting = true;
            }
            return new ComplexSelector(compounds, combinators, implicitNesting, leading is not null, range);
        }

        static bool TryCombinator(IReadOnlyList<CssComponentValue> v, int pos, out Combinator combinator)
        {
            combinator = Combinator.Descendant;
            if (pos >= v.Count || v[pos] is not CssToken { Type: CssTokenType.Delim } d) return false;
            switch (d.Delim)
            {
                case '>': combinator = Combinator.Child; return true;
                case '+': combinator = Combinator.NextSibling; return true;
                case '~': combinator = Combinator.SubsequentSibling; return true;
                default: return false;
            }
        }

        // ---------- 复合选择器 ----------

        CompoundSelector? ParseCompound(IReadOnlyList<CssComponentValue> v, ref int pos, int end)
        {
            int start = pos;
            var simples = new List<SimpleSelector>();
            PseudoElementSelector? pseudoElement = null;
            List<SimpleSelector>? after = null;

            // 类型选择器 / 通配，可带命名空间前缀
            if (!ParseTypeSelector(v, ref pos, end, simples)) return null;

            while (pos < end)
            {
                var c = v[pos];
                if (c is CssToken t)
                {
                    switch (t.Type)
                    {
                        case CssTokenType.Hash:
                            if (pseudoElement is not null) return AfterPseudoElement(pseudoElement, t);
                            if (t.HashType != CssHashType.Id)
                                return Fail<CompoundSelector>($"无效的 ID 选择器 '{t}'：ID 必须是合法标识符（不能以数字开头）", t.Range);
                            simples.Add(new IdSelector(t.Value));
                            pos++;
                            continue;
                        case CssTokenType.Delim when t.Delim == '.':
                            if (pseudoElement is not null) return AfterPseudoElement(pseudoElement, t);
                            if (pos + 1 < end && v[pos + 1] is CssToken { Type: CssTokenType.Ident } cls)
                            {
                                simples.Add(new ClassSelector(cls.Value));
                                pos += 2;
                                continue;
                            }
                            return Fail<CompoundSelector>("'.' 后需要类名", t.Range);
                        case CssTokenType.Delim when t.Delim == '&':
                            if (pseudoElement is not null) return AfterPseudoElement(pseudoElement, t);
                            simples.Add(new NestingSelector(_parent));
                            _nestingCount++;
                            pos++;
                            continue;
                        case CssTokenType.Colon:
                        {
                            if (pos + 1 < end && v[pos + 1] is CssToken { Type: CssTokenType.Colon })
                            {
                                if (pseudoElement is not null)
                                    return Fail<CompoundSelector>("暂不支持在伪元素之后再接伪元素", t.Range, CssDiagnosticCodes.UnsupportedSelector);
                                pos += 2;
                                if (pos >= end) return Fail<CompoundSelector>("'::' 后需要伪元素名", t.Range);
                                pseudoElement = ParsePseudoElement(v[pos], legacy: false);
                                if (pseudoElement is null) return null;
                                pos++;
                                continue;
                            }
                            pos++;
                            if (pos >= end) return Fail<CompoundSelector>("':' 后需要伪类名", t.Range);
                            var sel = ParsePseudoClass(v[pos], t);
                            if (sel is null) return null;
                            pos++;
                            if (sel is PseudoElementSelector legacyPe)
                            {
                                if (pseudoElement is not null)
                                    return Fail<CompoundSelector>("暂不支持在伪元素之后再接伪元素", t.Range, CssDiagnosticCodes.UnsupportedSelector);
                                pseudoElement = legacyPe;
                                continue;
                            }
                            if (pseudoElement is not null)
                            {
                                if (pseudoElement.PseudoElement != PseudoElementKind.Part || sel is not PseudoClassSelector { IsStatePseudoClass: true })
                                {
                                    return Fail<CompoundSelector>(
                                        $"伪元素 '{pseudoElement}' 之后不支持 '{sel}'（只有 ::part() 之后可以接状态类伪类，如 :hover）",
                                        new SourceRange(t.Range.Start, v[pos - 1].Range.End), CssDiagnosticCodes.UnsupportedSelector);
                                }
                                (after ??= new List<SimpleSelector>()).Add(sel);
                            }
                            else
                            {
                                simples.Add(sel);
                            }
                            continue;
                        }
                        case CssTokenType.Whitespace:
                        case CssTokenType.Comma:
                            break;
                        case CssTokenType.Delim when t.Delim is '>' or '+' or '~':
                            break;
                        case CssTokenType.Ident:
                        case CssTokenType.Delim when t.Delim is '*' or '|':
                            return Fail<CompoundSelector>($"类型选择器 '{t}' 必须出现在复合选择器的开头", t.Range);
                        default:
                            return Fail<CompoundSelector>($"选择器中出现意外的 '{t}'", t.Range);
                    }
                    break;
                }
                if (c is CssSimpleBlock { Open: '[' } block)
                {
                    if (pseudoElement is not null) return AfterPseudoElement(pseudoElement, block);
                    var attr = ParseAttribute(block);
                    if (attr is null) return null;
                    simples.Add(attr);
                    pos++;
                    continue;
                }
                return Fail<CompoundSelector>($"选择器中出现意外的 '{c}'", c.Range);
            }

            if (pos == start) return Fail<CompoundSelector>("期望选择器", pos < v.Count ? v[pos].Range : default);
            return new CompoundSelector(simples, pseudoElement, (IReadOnlyList<SimpleSelector>?)after ?? CompoundSelector.NoSelectors, isImplicit: false);
        }

        CompoundSelector? AfterPseudoElement(PseudoElementSelector pe, CssComponentValue c)
            => Fail<CompoundSelector>($"伪元素 '{pe}' 之后不能再接 '{c}'", c.Range);

        bool ParseTypeSelector(IReadOnlyList<CssComponentValue> v, ref int pos, int end, List<SimpleSelector> simples)
        {
            if (pos >= end || v[pos] is not CssToken t) return true;
            bool isName = t.Type == CssTokenType.Ident || t.IsDelim('*');
            // 命名空间前缀：ns|E、*|E、|E
            if (isName && pos + 2 < end && v[pos + 1] is CssToken bar && bar.IsDelim('|')
                && v[pos + 2] is CssToken n2 && (n2.Type == CssTokenType.Ident || n2.IsDelim('*')))
            {
                if (t.Type == CssTokenType.Ident)
                    return Fail($"不支持命名空间前缀 '{t.Value}|'（AwAUI 没有 XML 命名空间）", t.Range, CssDiagnosticCodes.UnsupportedSelector);
                pos += 2; // *| 等同于不限定命名空间
                t = n2;
            }
            else if (t.IsDelim('|') && pos + 1 < end && v[pos + 1] is CssToken n1 && (n1.Type == CssTokenType.Ident || n1.IsDelim('*')))
            {
                pos += 1; // |E：无命名空间，所有元素都属于此类
                t = n1;
            }
            else if (!isName)
            {
                return true;
            }

            if (t.Type == CssTokenType.Ident) simples.Add(new TypeSelector(t.Value));
            else simples.Add(UniversalSelector.Instance);
            pos++;
            return true;
        }

        // ---------- 属性选择器 ----------

        AttributeSelector? ParseAttribute(CssSimpleBlock block)
        {
            var v = block.Values;
            int i = 0;
            void SkipWs() { while (i < v.Count && IsWs(v[i])) i++; }
            SkipWs();
            if (i >= v.Count) return Fail<AttributeSelector>("属性选择器缺少属性名", block.Range);

            // 命名空间前缀
            if (i + 2 < v.Count && v[i + 1] is CssToken bar && bar.IsDelim('|') && v[i + 2] is CssToken { Type: CssTokenType.Ident })
            {
                if (v[i] is CssToken star && star.IsDelim('*')) i += 2;
                else if (v[i] is CssToken { Type: CssTokenType.Ident } ns)
                    return Fail<AttributeSelector>($"不支持命名空间前缀 '{ns.Value}|'", ns.Range, CssDiagnosticCodes.UnsupportedSelector);
            }
            else if (v[i] is CssToken lead && lead.IsDelim('|') && i + 1 < v.Count && v[i + 1] is CssToken { Type: CssTokenType.Ident })
            {
                i += 1;
            }

            if (v[i] is not CssToken { Type: CssTokenType.Ident } nameTok)
                return Fail<AttributeSelector>($"属性选择器需要属性名，得到 '{v[i]}'", v[i].Range);
            i++;
            SkipWs();
            if (i >= v.Count) return new AttributeSelector(nameTok.Value, AttributeOperator.Exists, string.Empty, AttributeCaseSensitivity.Default);

            AttributeOperator op;
            if (v[i] is CssToken d && d.Type == CssTokenType.Delim)
            {
                if (d.Delim == '=') { op = AttributeOperator.Equals; i++; }
                else
                {
                    bool eqNext = i + 1 < v.Count && v[i + 1] is CssToken e && e.IsDelim('=');
                    op = d.Delim switch
                    {
                        '~' => AttributeOperator.Includes,
                        '|' => AttributeOperator.DashMatch,
                        '^' => AttributeOperator.Prefix,
                        '$' => AttributeOperator.Suffix,
                        '*' => AttributeOperator.Substring,
                        _ => AttributeOperator.Exists,
                    };
                    if (op == AttributeOperator.Exists || !eqNext)
                        return Fail<AttributeSelector>($"无效的属性选择器运算符 '{d}'（支持 = ~= |= ^= $= *=）", d.Range);
                    i += 2;
                }
            }
            else
            {
                return Fail<AttributeSelector>($"属性选择器中出现意外的 '{v[i]}'", v[i].Range);
            }

            SkipWs();
            if (i >= v.Count || v[i] is not CssToken { Type: CssTokenType.Ident or CssTokenType.String } valueTok)
                return Fail<AttributeSelector>("属性选择器的运算符后需要标识符或字符串", i < v.Count ? v[i].Range : block.Range);
            i++;
            SkipWs();
            var cs = AttributeCaseSensitivity.Default;
            if (i < v.Count && v[i] is CssToken { Type: CssTokenType.Ident } flag)
            {
                if (flag.IsIdent("i")) cs = AttributeCaseSensitivity.Insensitive;
                else if (flag.IsIdent("s")) cs = AttributeCaseSensitivity.Sensitive;
                else return Fail<AttributeSelector>($"未知的属性选择器标志 '{flag.Value}'（支持 i 与 s）", flag.Range);
                i++;
                SkipWs();
            }
            if (i < v.Count) return Fail<AttributeSelector>($"属性选择器中出现意外的 '{v[i]}'", v[i].Range);
            return new AttributeSelector(nameTok.Value, op, valueTok.Value, cs);
        }

        // ---------- 伪类 ----------

        static readonly string[] KnownUnsupportedPseudoClasses =
        {
            "link", "visited", "any-link", "local-link", "target", "target-within", "lang", "dir", "read-only", "read-write",
            "required", "optional", "valid", "invalid", "user-valid", "user-invalid", "in-range", "out-of-range", "indeterminate",
            "default", "defined", "host", "host-context", "state", "autofill", "modal", "fullscreen", "popover-open", "playing",
            "paused", "current", "past", "future", "nth-col", "nth-last-col", "blank", "open", "closed", "picture-in-picture",
        };

        static PseudoClass? KeywordPseudoClass(string lower) => lower switch
        {
            "hover" => PseudoClass.Hover,
            "active" => PseudoClass.Active,
            "focus" => PseudoClass.Focus,
            "focus-visible" => PseudoClass.FocusVisible,
            "focus-within" => PseudoClass.FocusWithin,
            "disabled" => PseudoClass.Disabled,
            "enabled" => PseudoClass.Enabled,
            "checked" => PseudoClass.Checked,
            "placeholder-shown" => PseudoClass.PlaceholderShown,
            "first-child" => PseudoClass.FirstChild,
            "last-child" => PseudoClass.LastChild,
            "only-child" => PseudoClass.OnlyChild,
            "first-of-type" => PseudoClass.FirstOfType,
            "last-of-type" => PseudoClass.LastOfType,
            "only-of-type" => PseudoClass.OnlyOfType,
            "empty" => PseudoClass.Empty,
            "root" => PseudoClass.Root,
            "scope" => PseudoClass.Scope,
            _ => null,
        };

        SimpleSelector? ParsePseudoClass(CssComponentValue c, CssToken colon)
        {
            var range = new SourceRange(colon.Range.Start, c.Range.End);
            if (c is CssToken { Type: CssTokenType.Ident } id)
            {
                string lower = CssSerializer.ToAsciiLower(id.Value);
                var pc = KeywordPseudoClass(lower);
                if (pc is not null) return new PseudoClassSelector(pc.Value);
                switch (lower)
                {
                    case "before": return new PseudoElementSelector(PseudoElementKind.Before, PseudoElementSelector.NoParts, legacySyntax: true);
                    case "after": return new PseudoElementSelector(PseudoElementKind.After, PseudoElementSelector.NoParts, legacySyntax: true);
                    case "first-line":
                    case "first-letter":
                        return Fail<SimpleSelector>($"伪元素 ::{lower} 暂不支持（后置）", range, CssDiagnosticCodes.UnsupportedSelector);
                }
                if (Array.IndexOf(KnownUnsupportedPseudoClasses, lower) >= 0)
                    return Fail<SimpleSelector>($"暂不支持伪类 ':{id.Value}'", range, CssDiagnosticCodes.UnsupportedSelector);
                return Fail<SimpleSelector>($"未知的伪类 ':{id.Value}'", range, CssDiagnosticCodes.UnsupportedSelector);
            }
            if (c is CssFunction fn)
            {
                if (_functionDepth >= MaxFunctionNesting)
                    return Fail<SimpleSelector>($"函数式伪类嵌套超过 {MaxFunctionNesting} 层", range);
                _functionDepth++;
                try
                {
                    return ParseFunctionalPseudoClass(fn, range);
                }
                finally
                {
                    _functionDepth--;
                }
            }
            return Fail<SimpleSelector>($"':' 后需要伪类名，得到 '{c}'", range);
        }

        SimpleSelector? ParseFunctionalPseudoClass(CssFunction fn, SourceRange range)
        {
            string lower = CssSerializer.ToAsciiLower(fn.Name);
            var args = fn.Arguments;
            switch (lower)
            {
                case "not":
                {
                    var list = ParseList(args, 0, args.Count, forgiving: false, relative: false, fn.Range);
                    if (list is null) return null;
                    if (!CheckNoPseudoElements(list, ":not()")) return null;
                    return new LogicalSelector(LogicalKind.Not, list);
                }
                case "is":
                case "where":
                {
                    var list = ParseList(args, 0, args.Count, forgiving: true, relative: false, fn.Range)!;
                    list = DropPseudoElements(list, ":" + lower + "()");
                    return new LogicalSelector(lower == "is" ? LogicalKind.Is : LogicalKind.Where, list);
                }
                case "nth-child": return ParseNth(fn, NthKind.Child, allowOf: true);
                case "nth-last-child": return ParseNth(fn, NthKind.LastChild, allowOf: true);
                case "nth-of-type": return ParseNth(fn, NthKind.OfType, allowOf: false);
                case "nth-last-of-type": return ParseNth(fn, NthKind.LastOfType, allowOf: false);
                case "has":
                    return Fail<SimpleSelector>("':has()' 暂不支持（后置）", range, CssDiagnosticCodes.UnsupportedSelector);
            }
            if (Array.IndexOf(KnownUnsupportedPseudoClasses, lower) >= 0)
                return Fail<SimpleSelector>($"暂不支持伪类 ':{fn.Name}()'", range, CssDiagnosticCodes.UnsupportedSelector);
            return Fail<SimpleSelector>($"未知的伪类 ':{fn.Name}()'", range, CssDiagnosticCodes.UnsupportedSelector);
        }

        bool CheckNoPseudoElements(SelectorList list, string where)
        {
            foreach (var s in list.Selectors)
                if (s.PseudoElement is not null)
                    return Fail($"伪元素不能出现在 {where} 中", s.Range);
            return true;
        }

        SelectorList DropPseudoElements(SelectorList list, string where)
        {
            List<ComplexSelector>? kept = null;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s.PseudoElement is null) { kept?.Add(s); continue; }
                if (kept is null)
                {
                    kept = new List<ComplexSelector>();
                    for (int j = 0; j < i; j++) kept.Add(list[j]);
                }
                Fail<object>($"伪元素不能出现在 {where} 中", s.Range);
                ReportFailure(CssDiagnosticSeverity.Warning, "，该项在宽容选择器列表中被忽略");
            }
            return kept is null ? list : new SelectorList(kept);
        }

        SimpleSelector? ParseNth(CssFunction fn, NthKind kind, bool allowOf)
        {
            var args = fn.Arguments;
            int i = 0;
            if (!AnBParser.TryParse(args, ref i, out int a, out int b))
                return Fail<SimpleSelector>($"无效的 An+B 表达式 '{CssComponentValue.ToCss(args).Trim()}'（例：odd、even、3、2n+1、-n+3）", fn.Range);
            while (i < args.Count && IsWs(args[i])) i++;
            if (i == args.Count) return new NthSelector(kind, a, b, null);
            if (allowOf && args[i] is CssToken ofTok && ofTok.IsIdent("of"))
            {
                i++;
                var list = ParseList(args, i, args.Count, forgiving: false, relative: false, fn.Range);
                if (list is null) return null;
                if (!CheckNoPseudoElements(list, "of 子句")) return null;
                return new NthSelector(kind, a, b, list);
            }
            return Fail<SimpleSelector>($"An+B 之后出现意外的 '{args[i]}'", args[i].Range);
        }

        // ---------- 伪元素 ----------

        PseudoElementSelector? ParsePseudoElement(CssComponentValue c, bool legacy)
        {
            if (c is CssToken { Type: CssTokenType.Ident } id)
            {
                string lower = CssSerializer.ToAsciiLower(id.Value);
                switch (lower)
                {
                    case "before": return new PseudoElementSelector(PseudoElementKind.Before, PseudoElementSelector.NoParts, legacy);
                    case "after": return new PseudoElementSelector(PseudoElementKind.After, PseudoElementSelector.NoParts, legacy);
                    case "placeholder": return new PseudoElementSelector(PseudoElementKind.Placeholder, PseudoElementSelector.NoParts, legacy);
                    case "selection": return new PseudoElementSelector(PseudoElementKind.Selection, PseudoElementSelector.NoParts, legacy);
                    case "first-line":
                    case "first-letter":
                        return Fail<PseudoElementSelector>($"伪元素 '::{lower}' 暂不支持（后置）", id.Range, CssDiagnosticCodes.UnsupportedSelector);
                    case "marker":
                    case "backdrop":
                    case "file-selector-button":
                    case "cue":
                    case "slotted":
                    case "grammar-error":
                    case "spelling-error":
                    case "target-text":
                    case "highlight":
                        return Fail<PseudoElementSelector>($"暂不支持伪元素 '::{id.Value}'", id.Range, CssDiagnosticCodes.UnsupportedSelector);
                }
                return Fail<PseudoElementSelector>($"未知的伪元素 '::{id.Value}'", id.Range, CssDiagnosticCodes.UnsupportedSelector);
            }
            if (c is CssFunction fn)
            {
                if (CssSerializer.EqualsIgnoreAsciiCase(fn.Name, "part"))
                {
                    var names = new List<string>();
                    foreach (var a in fn.Arguments)
                    {
                        if (IsWs(a)) continue;
                        if (a is CssToken { Type: CssTokenType.Ident } n) names.Add(n.Value);
                        else return Fail<PseudoElementSelector>("::part() 的参数必须是空白分隔的标识符", a.Range);
                    }
                    if (names.Count == 0) return Fail<PseudoElementSelector>("::part() 至少需要一个名称", fn.Range);
                    return new PseudoElementSelector(PseudoElementKind.Part, names, legacy);
                }
                return Fail<PseudoElementSelector>($"暂不支持伪元素 '::{fn.Name}()'", fn.Range, CssDiagnosticCodes.UnsupportedSelector);
            }
            return Fail<PseudoElementSelector>($"'::' 后需要伪元素名，得到 '{c}'", c.Range);
        }
    }
}

/// <summary>CSS Syntax 3 §6「An+B 微语法」。</summary>
internal static class AnBParser
{
    static bool IsWs(CssComponentValue v) => v is CssToken { Type: CssTokenType.Whitespace };

    static int Clamp(double d) => d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)d;

    /// <summary>从 <paramref name="i"/> 开始解析；成功时 <paramref name="i"/> 指向 An+B 之后（可能是空白或 <c>of</c>）。</summary>
    public static bool TryParse(IReadOnlyList<CssComponentValue> v, ref int i, out int a, out int b)
    {
        a = 0;
        b = 0;
        while (i < v.Count && IsWs(v[i])) i++;
        if (i >= v.Count || v[i] is not CssToken t) return false;

        switch (t.Type)
        {
            case CssTokenType.Number:
                if (t.NumberType != CssNumberType.Integer) return false;
                b = Clamp(t.Number);
                i++;
                return true;

            case CssTokenType.Dimension:
            {
                if (t.NumberType != CssNumberType.Integer) return false;
                a = Clamp(t.Number);
                i++;
                return ParseAfterN(v, ref i, CssSerializer.ToAsciiLower(t.Unit), 0, ref b);
            }

            case CssTokenType.Ident:
            {
                string s = CssSerializer.ToAsciiLower(t.Value);
                i++;
                if (s == "odd") { a = 2; b = 1; return true; }
                if (s == "even") { a = 2; b = 0; return true; }
                if (s.Length > 0 && s[0] == '-')
                {
                    a = -1;
                    return ParseAfterN(v, ref i, s, 1, ref b);
                }
                a = 1;
                return ParseAfterN(v, ref i, s, 0, ref b);
            }

            case CssTokenType.Delim when t.Delim == '+':
            {
                // '+' 后必须紧跟 n…（中间不能有空白）
                if (i + 1 >= v.Count || v[i + 1] is not CssToken { Type: CssTokenType.Ident } n) return false;
                string s = CssSerializer.ToAsciiLower(n.Value);
                if (s.Length == 0 || s[0] != 'n') return false;
                i += 2;
                a = 1;
                return ParseAfterN(v, ref i, s, 0, ref b);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 处理 n 部分：<paramref name="s"/>[<paramref name="at"/>..] 必须是 <c>n</c>、<c>n-</c> 或 <c>n-数字</c>，
    /// 然后按需读取后续的 B。
    /// </summary>
    static bool ParseAfterN(IReadOnlyList<CssComponentValue> v, ref int i, string s, int at, ref int b)
    {
        if (at >= s.Length || s[at] != 'n') return false;
        int rest = at + 1;
        if (rest == s.Length)
        {
            // n [ <signed-integer> | ['+'|'-'] <signless-integer> ]?
            int save = i;
            int j = i;
            while (j < v.Count && IsWs(v[j])) j++;
            if (j >= v.Count) return true;
            if (v[j] is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer, HasSign: true } signed)
            {
                b = Clamp(signed.Number);
                i = j + 1;
                return true;
            }
            if (v[j] is CssToken d && (d.IsDelim('+') || d.IsDelim('-')))
            {
                int k = j + 1;
                while (k < v.Count && IsWs(v[k])) k++;
                if (k < v.Count && v[k] is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer, HasSign: false } unsigned)
                {
                    b = Clamp(d.Delim == '-' ? -unsigned.Number : unsigned.Number);
                    i = k + 1;
                    return true;
                }
                return false;
            }
            i = save;
            return true; // 其后不是 B（可能是 of）
        }
        if (s[rest] != '-') return false;
        if (rest + 1 == s.Length)
        {
            // n- <signless-integer>
            int j = i;
            while (j < v.Count && IsWs(v[j])) j++;
            if (j < v.Count && v[j] is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer, HasSign: false } unsigned)
            {
                b = Clamp(-unsigned.Number);
                i = j + 1;
                return true;
            }
            return false;
        }
        // n-<digits>
        long value = 0;
        for (int k = rest + 1; k < s.Length; k++)
        {
            char c = s[k];
            if (c < '0' || c > '9') return false;
            if (value < int.MaxValue) value = value * 10 + (c - '0');
        }
        b = Clamp(-(double)value);
        return true;
    }
}

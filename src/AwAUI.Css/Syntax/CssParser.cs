using System;
using System.Collections.Generic;
using AwAUI.Css.Media;
using AwAUI.Css.Selectors;

namespace AwAUI.Css.Syntax;

/// <summary>
/// CSS Syntax 3 §5 的解析算法（含 CSS Nesting 的块内容算法）。在完整的记号列表上工作，以便「标记/恢复」。
/// 结构化的 at 规则（@media、@layer、@keyframes、@font-face、@import）在此处落为强类型规则。
/// </summary>
internal sealed class CssParser
{
    /// <summary>组件值嵌套深度上限：超过后把剩余内容扁平化，避免病态输入导致栈溢出。</summary>
    const int MaxComponentDepth = 256;

    /// <summary>规则嵌套深度上限：超过后整块丢弃。</summary>
    const int MaxRuleDepth = 64;

    enum BlockContext
    {
        TopLevel,
        /// <summary>顶层（或顶层分组规则内）的分组规则块：只允许规则。</summary>
        GroupTopLevel,
        /// <summary>样式规则块，或嵌套在样式规则中的分组规则块：声明与规则都允许。</summary>
        StyleRule,
        /// <summary>@keyframes 块：只允许关键帧规则。</summary>
        Keyframes,
        /// <summary>只允许声明（@font-face、关键帧、内联样式）。</summary>
        Declarations,
    }

    enum AtRuleKind { Media, Layer, Keyframes, FontFace, Import, Charset, Supports, Container, StartingStyle, Property, KnownUnsupported, Unknown }

    readonly CssSourceText _src;
    readonly List<CssDiagnostic> _d;
    readonly List<CssToken> _t;
    int _i;
    int _componentDepth;
    int _ruleDepth;

    public CssParser(CssSourceText source, List<CssDiagnostic> diagnostics)
    {
        _src = source;
        _d = diagnostics;
        _t = new CssTokenizer(source, diagnostics).ReadAll();
    }

    CssToken Peek => _t[_i];

    CssToken Consume()
    {
        var t = _t[_i];
        if (t.Type != CssTokenType.EndOfFile) _i++;
        return t;
    }

    void SkipWhitespace()
    {
        while (_t[_i].Type == CssTokenType.Whitespace) _i++;
    }

    void Report(string code, CssDiagnosticSeverity severity, string message, SourceRange range)
        => _d.Add(new CssDiagnostic(code, severity, message, range));

    void SyntaxError(string message, SourceRange range) => Report(CssDiagnosticCodes.SyntaxError, CssDiagnosticSeverity.Error, message, range);

    // ================= 入口 =================

    /// <summary>§5.4.1「解析样式表」+ §5.5.1「消费样式表内容」。</summary>
    public List<CssRule> ParseStyleSheet()
    {
        var rules = new List<CssRule>();
        bool importAllowed = true;
        while (true)
        {
            var t = Peek;
            switch (t.Type)
            {
                case CssTokenType.Whitespace:
                case CssTokenType.CDO:
                case CssTokenType.CDC:
                    _i++;
                    continue;
                case CssTokenType.EndOfFile:
                    return rules;
                case CssTokenType.AtKeyword:
                {
                    var rule = ConsumeAtRule(BlockContext.TopLevel, null);
                    if (rule is null) continue;
                    if (rule is CssImportRule)
                    {
                        if (!importAllowed)
                        {
                            Report(CssDiagnosticCodes.InvalidAtRule, CssDiagnosticSeverity.Error,
                                "@import 必须出现在除 @charset 与 @layer 语句以外的所有规则之前，该规则被忽略。", rule.Range);
                            continue;
                        }
                    }
                    else if (rule is not CssLayerStatementRule && rule is not CssUnknownAtRule { IsKnownUnsupported: false })
                    {
                        // 未知 at 规则在浏览器中被直接丢弃，不影响其后 @import 的有效性
                        importAllowed = false;
                    }
                    rules.Add(rule);
                    continue;
                }
                default:
                {
                    var rule = ConsumeQualifiedRule(BlockContext.TopLevel, null, nested: false, out _);
                    if (rule is not null)
                    {
                        rules.Add(rule);
                        importAllowed = false;
                    }
                    continue;
                }
            }
        }
    }

    /// <summary>解析声明列表（内联样式）：等价于对整段文本执行「消费块内容」，只保留声明。</summary>
    public List<CssDeclaration> ParseDeclarationList()
    {
        var decls = new List<CssDeclaration>();
        var rules = new List<CssRule>();
        ConsumeBlockContents(BlockContext.Declarations, null, decls, rules, topLevelList: true);
        return decls;
    }

    /// <summary>§5.4.9「解析组件值列表」：把整个输入消费为组件值（不含 EOF）。</summary>
    public List<CssComponentValue> ConsumeAllComponentValues()
    {
        var list = new List<CssComponentValue>();
        while (Peek.Type != CssTokenType.EndOfFile) list.Add(ConsumeComponentValue());
        return list;
    }

    // ================= 组件值 =================

    /// <summary>§5.5.7「消费组件值」。</summary>
    internal CssComponentValue ConsumeComponentValue()
    {
        var t = Consume();
        switch (t.Type)
        {
            case CssTokenType.LeftBrace:
            case CssTokenType.LeftBracket:
            case CssTokenType.LeftParen:
                return ConsumeSimpleBlock(t);
            case CssTokenType.Function:
                return ConsumeFunction(t);
            default:
                return t;
        }
    }

    static CssTokenType Mirror(CssTokenType open) => open switch
    {
        CssTokenType.LeftBrace => CssTokenType.RightBrace,
        CssTokenType.LeftBracket => CssTokenType.RightBracket,
        _ => CssTokenType.RightParen,
    };

    static char OpenChar(CssTokenType open) => open switch
    {
        CssTokenType.LeftBrace => '{',
        CssTokenType.LeftBracket => '[',
        _ => '(',
    };

    /// <summary>§5.5.8「消费简单块」（开括号已被消费）。</summary>
    CssSimpleBlock ConsumeSimpleBlock(CssToken open)
    {
        var close = Mirror(open.Type);
        var values = new List<CssComponentValue>();
        bool closed = ConsumeUntilClose(close, values, out var end);
        return new CssSimpleBlock(OpenChar(open.Type), values, new SourceRange(open.Range.Start, end), closed);
    }

    /// <summary>§5.5.9「消费函数」（函数记号已被消费）。</summary>
    CssFunction ConsumeFunction(CssToken fn)
    {
        var values = new List<CssComponentValue>();
        bool closed = ConsumeUntilClose(CssTokenType.RightParen, values, out var end);
        return new CssFunction(fn.Value, values, new SourceRange(fn.Range.Start, end), closed);
    }

    bool ConsumeUntilClose(CssTokenType close, List<CssComponentValue> values, out SourcePosition end)
    {
        if (++_componentDepth > MaxComponentDepth)
        {
            bool ok = ConsumeFlatUntilClose(close, values, out end);
            _componentDepth--;
            return ok;
        }
        while (true)
        {
            var t = Peek;
            if (t.Type == close)
            {
                _i++;
                end = t.Range.End;
                _componentDepth--;
                return true;
            }
            if (t.Type == CssTokenType.EndOfFile)
            {
                end = t.Range.Start;
                Report(CssDiagnosticCodes.SyntaxError, CssDiagnosticSeverity.Warning,
                    $"缺少 '{CloseChar(close)}'：块在文件结尾处未闭合。", t.Range);
                _componentDepth--;
                return false;
            }
            values.Add(ConsumeComponentValue());
        }
    }

    /// <summary>嵌套过深时以非递归方式消费到匹配的闭括号，内层括号作为普通记号保留。</summary>
    bool ConsumeFlatUntilClose(CssTokenType close, List<CssComponentValue> values, out SourcePosition end)
    {
        Report(CssDiagnosticCodes.SyntaxError, CssDiagnosticSeverity.Warning,
            $"括号嵌套超过 {MaxComponentDepth} 层，内层内容不再结构化。", Peek.Range);
        var expected = new List<CssTokenType> { close };
        while (true)
        {
            var t = Peek;
            if (t.Type == CssTokenType.EndOfFile) { end = t.Range.Start; return false; }
            _i++;
            switch (t.Type)
            {
                case CssTokenType.LeftBrace:
                case CssTokenType.LeftBracket:
                case CssTokenType.LeftParen:
                    expected.Add(Mirror(t.Type));
                    break;
                case CssTokenType.Function:
                    expected.Add(CssTokenType.RightParen);
                    break;
                default:
                    if (t.Type == expected[expected.Count - 1])
                    {
                        expected.RemoveAt(expected.Count - 1);
                        if (expected.Count == 0) { end = t.Range.End; return true; }
                    }
                    break;
            }
            values.Add(t);
        }
    }

    static char CloseChar(CssTokenType close) => close switch
    {
        CssTokenType.RightBrace => '}',
        CssTokenType.RightBracket => ']',
        _ => ')',
    };

    // ================= 规则 =================

    /// <summary>§5.5.3「消费限定规则」。返回 null 表示「什么也没有」；<paramref name="invalid"/> 表示「无效规则错误」。</summary>
    CssRule? ConsumeQualifiedRule(BlockContext ctx, CssStyleRule? parentStyle, bool nested, out bool invalid)
    {
        invalid = false;
        var prelude = new List<CssComponentValue>();
        var startToken = Peek;
        while (true)
        {
            var t = Peek;
            switch (t.Type)
            {
                case CssTokenType.EndOfFile:
                    SyntaxError(nested
                            ? UnparsableItem(ctx) + "，已丢弃到文件结尾。"
                            : "规则缺少声明块 '{…}'，已丢弃。",
                        Span(startToken.Range.Start, t.Range.Start));
                    return null;
                case CssTokenType.Semicolon when nested:
                    SyntaxError(UnparsableItem(ctx) + "，已丢弃到下一个 ';'。", Span(startToken.Range.Start, t.Range.Start));
                    return null;
                case CssTokenType.RightBrace:
                    if (nested)
                    {
                        SyntaxError(UnparsableItem(ctx) + "，已丢弃到块结尾。", Span(startToken.Range.Start, t.Range.Start));
                        return null;
                    }
                    _i++;
                    prelude.Add(t);
                    continue;
                case CssTokenType.LeftBrace:
                {
                    if (StartsWithCustomPropertyColon(prelude))
                    {
                        if (nested)
                        {
                            ConsumeBadDeclarationRemnants();
                        }
                        else
                        {
                            var open = Consume();
                            ConsumeSimpleBlock(open);
                            SyntaxError("顶层不能声明自定义属性，已丢弃。", Span(startToken.Range.Start, t.Range.Start));
                        }
                        return null;
                    }
                    var brace = Consume();
                    switch (ctx)
                    {
                        case BlockContext.Keyframes:
                            return BuildKeyframeRule(prelude, brace, out invalid);
                        case BlockContext.Declarations:
                        {
                            var block = ConsumeSimpleBlock(brace);
                            Report(CssDiagnosticCodes.MisplacedRule, CssDiagnosticSeverity.Error,
                                "此处只允许声明，嵌套规则被丢弃。", Span(startToken.Range.Start, block.Range.End));
                            invalid = true;
                            return null;
                        }
                        default:
                            return BuildStyleRule(prelude, brace, parentStyle, nested, out invalid);
                    }
                }
                default:
                    prelude.Add(ConsumeComponentValue());
                    continue;
            }
        }
    }

    /// <summary>块内既不是合法声明也不是规则时的诊断说明（只允许声明的上下文中直接说明为无效声明）。</summary>
    static string UnparsableItem(BlockContext ctx) => ctx switch
    {
        BlockContext.Declarations => "无效的声明（应为 '名称: 值'）",
        BlockContext.Keyframes => "无效的关键帧规则（应为 'from|to|百分比 { … }'）",
        BlockContext.StyleRule => "无法解析为声明（'名称: 值'）或嵌套规则（'选择器 { … }'）",
        _ => "无法解析为规则（缺少 '{…}' 块）",
    };

    static bool StartsWithCustomPropertyColon(List<CssComponentValue> prelude)
    {
        int i = 0;
        while (i < prelude.Count && prelude[i] is CssToken { Type: CssTokenType.Whitespace }) i++;
        if (i >= prelude.Count || prelude[i] is not CssToken { Type: CssTokenType.Ident } id || !id.Value.StartsWith("--", StringComparison.Ordinal))
            return false;
        i++;
        while (i < prelude.Count && prelude[i] is CssToken { Type: CssTokenType.Whitespace }) i++;
        return i < prelude.Count && prelude[i] is CssToken { Type: CssTokenType.Colon };
    }

    CssRule? BuildStyleRule(List<CssComponentValue> prelude, CssToken brace, CssStyleRule? parentStyle, bool nested, out bool invalid)
    {
        invalid = false;
        var preludeRange = TrimmedRange(prelude, brace.Range.Start);
        var selectors = SelectorParser.ParseRulePrelude(prelude, parentStyle?.Selectors, nested && parentStyle is not null, _d, preludeRange);
        if (selectors is null)
        {
            // 选择器无效：整条规则（含嵌套内容）按规范丢弃
            ConsumeSimpleBlock(brace);
            invalid = true;
            return null;
        }
        var rule = new CssStyleRule(prelude, _src.Slice(preludeRange.Start, preludeRange.End), selectors, parentStyle, preludeRange,
            new SourceRange(preludeRange.Start, brace.Range.End));
        var end = ConsumeBlockContents(BlockContext.StyleRule, rule, rule.DeclarationList, rule.ChildList, topLevelList: false);
        rule.Range = new SourceRange(preludeRange.Start, end);
        foreach (var child in rule.ChildList) child.Parent = rule;
        return rule;
    }

    CssRule? BuildKeyframeRule(List<CssComponentValue> prelude, CssToken brace, out bool invalid)
    {
        invalid = false;
        var preludeRange = TrimmedRange(prelude, brace.Range.Start);
        var decls = new List<CssDeclaration>();
        var end = ConsumeBlockContents(BlockContext.Declarations, null, decls, new List<CssRule>(), topLevelList: false);
        var offsets = ParseKeyframeSelectors(prelude);
        if (offsets is null)
        {
            Report(CssDiagnosticCodes.InvalidAtRule, CssDiagnosticSeverity.Error,
                "无效的关键帧选择器：只允许 from、to 与 0%–100% 的百分比，用逗号分隔。该关键帧被丢弃。", preludeRange);
            invalid = true;
            return null;
        }
        var kept = new List<CssDeclaration>(decls.Count);
        foreach (var d in decls)
        {
            if (d.Important)
            {
                Report(CssDiagnosticCodes.MisplacedDeclaration, CssDiagnosticSeverity.Warning,
                    "关键帧中不允许带 !important 的声明，按规范被忽略。请去掉 !important。", d.Range);
                continue;
            }
            kept.Add(d);
        }
        return new CssKeyframeRule(offsets, _src.Slice(preludeRange.Start, preludeRange.End), kept, new SourceRange(preludeRange.Start, end));
    }

    static List<double>? ParseKeyframeSelectors(List<CssComponentValue> prelude)
    {
        var result = new List<double>();
        CssToken? current = null;
        bool any = false;
        for (int i = 0; i <= prelude.Count; i++)
        {
            var v = i < prelude.Count ? prelude[i] : null;
            if (v is null || v is CssToken { Type: CssTokenType.Comma })
            {
                if (current is null) return null;
                if (current.Type == CssTokenType.Percentage)
                {
                    if (current.Number < 0 || current.Number > 100) return null;
                    result.Add(current.Number / 100);
                }
                else if (current.IsIdent("from")) result.Add(0);
                else if (current.IsIdent("to")) result.Add(1);
                else return null;
                current = null;
                any = true;
                continue;
            }
            if (v is CssToken { Type: CssTokenType.Whitespace }) continue;
            if (current is not null || v is not CssToken tok) return null;
            current = tok;
        }
        return any ? result : null;
    }

    /// <summary>
    /// CSS Syntax（含 Nesting）的「消费块内容」：交错的声明与规则（左花括号已被消费）。
    /// 嵌套规则之后出现的声明被包装为 <see cref="CssNestedDeclarationsRule"/>。返回块的结束位置。
    /// </summary>
    SourcePosition ConsumeBlockContents(BlockContext ctx, CssStyleRule? parentStyle, List<CssDeclaration> ownDecls, List<CssRule> rules,
        bool topLevelList)
    {
        if (++_ruleDepth > MaxRuleDepth)
        {
            _ruleDepth--;
            var flat = new List<CssComponentValue>();
            Report(CssDiagnosticCodes.SyntaxError, CssDiagnosticSeverity.Error,
                $"规则嵌套超过 {MaxRuleDepth} 层，该块被丢弃。", Peek.Range);
            ConsumeFlatUntilClose(CssTokenType.RightBrace, flat, out var flatEnd);
            return flatEnd;
        }

        bool allowDecls = ctx is BlockContext.StyleRule or BlockContext.Declarations;
        bool allowRules = ctx is not BlockContext.Declarations;
        var pending = ownDecls;
        try
        {
            while (true)
            {
                var t = Peek;
                switch (t.Type)
                {
                    case CssTokenType.Whitespace:
                    case CssTokenType.Semicolon:
                        _i++;
                        continue;
                    case CssTokenType.EndOfFile:
                        if (!topLevelList)
                        {
                            Report(CssDiagnosticCodes.SyntaxError, CssDiagnosticSeverity.Warning,
                                "缺少 '}'：块在文件结尾处未闭合。", t.Range);
                        }
                        FlushAtEnd();
                        return t.Range.Start;
                    case CssTokenType.RightBrace:
                        if (topLevelList)
                        {
                            // 内联样式中多余的 '}'：作为坏声明丢弃
                            _i++;
                            SyntaxError("多余的 '}'。", t.Range);
                            continue;
                        }
                        _i++;
                        FlushAtEnd();
                        return t.Range.End;
                    case CssTokenType.AtKeyword:
                    {
                        var rule = ConsumeAtRule(ctx, parentStyle);
                        if (rule is null) continue;
                        AddRule(rule);
                        continue;
                    }
                    default:
                    {
                        int mark = _i;
                        var decl = TryConsumeDeclaration();
                        if (decl is not null)
                        {
                            if (allowDecls) pending.Add(decl);
                            else
                            {
                                Report(CssDiagnosticCodes.MisplacedDeclaration, CssDiagnosticSeverity.Error,
                                    ctx == BlockContext.Keyframes
                                        ? "@keyframes 块中只允许关键帧规则，声明被丢弃。"
                                        : "此处不允许声明（顶层或顶层分组规则中只能写规则），声明被丢弃。",
                                    decl.Range);
                            }
                            continue;
                        }
                        _i = mark;
                        var rule = ConsumeQualifiedRule(ctx, parentStyle, nested: true, out bool invalid);
                        if (rule is not null) AddRule(rule);
                        else if (invalid && allowRules) Flush();
                        continue;
                    }
                }
            }
        }
        finally
        {
            _ruleDepth--;
        }

        // 与规范一致：块内容是「声明列表与规则」交错的序列。第一个元素若是声明列表，归为规则自身的声明；
        // 其余声明列表包装为嵌套声明规则。无效规则只截断当前声明列表。
        void Flush()
        {
            if (pending.Count == 0) return;
            if (!ReferenceEquals(pending, ownDecls)) rules.Add(NestedDeclarations(pending));
            pending = new List<CssDeclaration>();
        }

        void AddRule(CssRule rule)
        {
            if (!allowRules) return;
            Flush();
            rules.Add(rule);
            if (ReferenceEquals(pending, ownDecls)) pending = new List<CssDeclaration>();
        }

        void FlushAtEnd()
        {
            if (!ReferenceEquals(pending, ownDecls) && pending.Count > 0) rules.Add(NestedDeclarations(pending));
        }

        CssNestedDeclarationsRule NestedDeclarations(List<CssDeclaration> list)
            => new(list, parentStyle!, new SourceRange(list[0].Range.Start, list[list.Count - 1].Range.End));
    }

    /// <summary>
    /// §5.5.6「消费声明」（nested = true）。失败时返回 null 且不保证位置，调用方负责恢复标记。
    /// 对「名字 + 冒号 + 非空内容 + {}块」的情形提前失败，避免对 <c>a:hover { … }</c> 这类嵌套规则扫描到块尾。
    /// </summary>
    CssDeclaration? TryConsumeDeclaration()
    {
        var nameTok = Peek;
        if (nameTok.Type != CssTokenType.Ident) return null;
        _i++;
        SkipWhitespace();
        var colon = Peek;
        if (colon.Type != CssTokenType.Colon) return null;
        _i++;
        SkipWhitespace();

        bool custom = nameTok.Value.StartsWith("--", StringComparison.Ordinal);
        var values = new List<CssComponentValue>();
        bool sawNonWs = false;
        while (true)
        {
            var t = Peek;
            if (t.Type is CssTokenType.Semicolon or CssTokenType.RightBrace or CssTokenType.EndOfFile) break;
            if (!custom && t.Type == CssTokenType.LeftBrace && sawNonWs) return null;
            var v = ConsumeComponentValue();
            if (v is not CssToken { Type: CssTokenType.Whitespace }) sawNonWs = true;
            values.Add(v);
        }

        // 去掉尾部空白，检测 !important
        int last = values.Count - 1;
        while (last >= 0 && values[last] is CssToken { Type: CssTokenType.Whitespace }) last--;
        var declEnd = last >= 0 ? values[last].Range.End : colon.Range.End;
        bool important = false;
        if (last >= 0 && values[last] is CssToken imp && imp.IsIdent("important"))
        {
            int bang = last - 1;
            while (bang >= 0 && values[bang] is CssToken { Type: CssTokenType.Whitespace }) bang--;
            if (bang >= 0 && values[bang] is CssToken b && b.IsDelim('!'))
            {
                important = true;
                last = bang - 1;
                while (last >= 0 && values[last] is CssToken { Type: CssTokenType.Whitespace }) last--;
            }
        }
        if (last + 1 < values.Count) values.RemoveRange(last + 1, values.Count - last - 1);

        if (!custom)
        {
            // 顶层 {} 块只能作为非自定义属性的完整值
            bool hasBrace = false;
            int nonWs = 0;
            foreach (var v in values)
            {
                if (v is CssToken { Type: CssTokenType.Whitespace }) continue;
                nonWs++;
                if (v is CssSimpleBlock { Open: '{' }) hasBrace = true;
            }
            if (hasBrace && nonWs > 1) return null;
        }

        var valueRange = values.Count > 0
            ? new SourceRange(values[0].Range.Start, values[values.Count - 1].Range.End)
            : new SourceRange(colon.Range.End, colon.Range.End);
        string name = custom ? nameTok.Value : CssSerializer.ToAsciiLower(nameTok.Value);
        return new CssDeclaration(name, values, important, _src.Slice(valueRange.Start, valueRange.End),
            new SourceRange(nameTok.Range.Start, declEnd), nameTok.Range, valueRange);
    }

    /// <summary>§5.5.? 「消费坏声明的剩余部分」（nested = true）。</summary>
    void ConsumeBadDeclarationRemnants()
    {
        while (true)
        {
            var t = Peek;
            switch (t.Type)
            {
                case CssTokenType.EndOfFile:
                case CssTokenType.RightBrace:
                    return;
                case CssTokenType.Semicolon:
                    _i++;
                    return;
                default:
                    ConsumeComponentValue();
                    break;
            }
        }
    }

    // ================= at 规则 =================

    static AtRuleKind Classify(string lowerName) => lowerName switch
    {
        "media" => AtRuleKind.Media,
        "layer" => AtRuleKind.Layer,
        "keyframes" => AtRuleKind.Keyframes,
        "font-face" => AtRuleKind.FontFace,
        "import" => AtRuleKind.Import,
        "charset" => AtRuleKind.Charset,
        "supports" => AtRuleKind.Supports,
        "container" => AtRuleKind.Container,
        "starting-style" => AtRuleKind.StartingStyle,
        "property" => AtRuleKind.Property,
        "namespace" or "page" or "scope" or "counter-style"
            or "font-feature-values" or "font-palette-values" or "view-transition"
            or "position-try" or "document" => AtRuleKind.KnownUnsupported,
        _ => AtRuleKind.Unknown,
    };

    static bool IsAllowed(AtRuleKind kind, BlockContext ctx) => kind switch
    {
        AtRuleKind.Import or AtRuleKind.Charset => ctx == BlockContext.TopLevel,
        AtRuleKind.Media or AtRuleKind.Layer or AtRuleKind.Supports or AtRuleKind.Container or AtRuleKind.StartingStyle
            => ctx is BlockContext.TopLevel or BlockContext.GroupTopLevel or BlockContext.StyleRule,
        AtRuleKind.Keyframes or AtRuleKind.FontFace or AtRuleKind.Property => ctx is BlockContext.TopLevel or BlockContext.GroupTopLevel,
        _ => ctx is BlockContext.TopLevel or BlockContext.GroupTopLevel or BlockContext.StyleRule,
    };

    /// <summary>§5.5.2「消费 at 规则」，随后按规则名落为结构化规则。返回 null 表示被丢弃或忽略。</summary>
    CssRule? ConsumeAtRule(BlockContext ctx, CssStyleRule? parentStyle)
    {
        var kw = Consume();
        string name = CssSerializer.ToAsciiLower(kw.Value);
        var kind = Classify(name);
        bool nested = ctx != BlockContext.TopLevel;
        bool allowed = IsAllowed(kind, ctx);
        var prelude = new List<CssComponentValue>();

        while (true)
        {
            var t = Peek;
            switch (t.Type)
            {
                case CssTokenType.Semicolon:
                    _i++;
                    return Finish(null, null, t.Range.End);
                case CssTokenType.EndOfFile:
                    return Finish(null, null, prelude.Count > 0 ? prelude[prelude.Count - 1].Range.End : kw.Range.End);
                case CssTokenType.RightBrace:
                    if (nested) return Finish(null, null, prelude.Count > 0 ? prelude[prelude.Count - 1].Range.End : kw.Range.End);
                    _i++;
                    prelude.Add(t);
                    continue;
                case CssTokenType.LeftBrace:
                {
                    var brace = Consume();
                    if (!allowed || kind is AtRuleKind.Import or AtRuleKind.Charset or AtRuleKind.KnownUnsupported or AtRuleKind.Unknown)
                    {
                        var raw = ConsumeSimpleBlock(brace);
                        return Finish(raw, null, raw.Range.End);
                    }
                    var contents = new BlockResult();
                    switch (kind)
                    {
                        case AtRuleKind.Media:
                        case AtRuleKind.Layer:
                        case AtRuleKind.Supports:
                        case AtRuleKind.Container:
                        case AtRuleKind.StartingStyle:
                            contents.End = ConsumeBlockContents(parentStyle is null ? BlockContext.GroupTopLevel : BlockContext.StyleRule,
                                parentStyle, contents.Decls, contents.Rules, topLevelList: false);
                            break;
                        case AtRuleKind.Keyframes:
                            contents.End = ConsumeBlockContents(BlockContext.Keyframes, null, contents.Decls, contents.Rules, topLevelList: false);
                            break;
                        case AtRuleKind.FontFace:
                        case AtRuleKind.Property:
                            contents.End = ConsumeBlockContents(BlockContext.Declarations, null, contents.Decls, contents.Rules, topLevelList: false);
                            break;
                    }
                    return Finish(null, contents, contents.End);
                }
                default:
                    prelude.Add(ConsumeComponentValue());
                    continue;
            }
        }

        CssRule? Finish(CssSimpleBlock? rawBlock, BlockResult? contents, SourcePosition end)
        {
            var range = new SourceRange(kw.Range.Start, end);
            if (!allowed)
            {
                if (kind == AtRuleKind.Unknown) ReportUnknown(name, range, known: false);
                else
                {
                    Report(CssDiagnosticCodes.MisplacedRule, CssDiagnosticSeverity.Error,
                        $"@{name} 不能出现在此处，已丢弃。", range);
                }
                return null;
            }
            CssRule? rule = kind switch
            {
                AtRuleKind.Media => BuildMedia(name, prelude, contents, parentStyle, range),
                AtRuleKind.Layer => BuildLayer(prelude, contents, parentStyle, range),
                AtRuleKind.Keyframes => BuildKeyframes(prelude, contents, range),
                AtRuleKind.FontFace => BuildFontFace(prelude, contents, range),
                AtRuleKind.Import => BuildImport(prelude, rawBlock, range),
                AtRuleKind.Supports => BuildSupports(prelude, contents, parentStyle, range),
                AtRuleKind.Container => BuildContainer(prelude, contents, parentStyle, range),
                AtRuleKind.StartingStyle => BuildStartingStyle(prelude, contents, parentStyle, range),
                AtRuleKind.Property => BuildProperty(prelude, contents, range),
                AtRuleKind.Charset => null, // 编码由加载方决定，@charset 在解析层面忽略
                AtRuleKind.KnownUnsupported => Unknown(name, prelude, rawBlock, true, range),
                _ => Unknown(name, prelude, rawBlock, false, range),
            };
            if (rule is not null)
            {
                foreach (var child in rule.ChildRules) child.Parent = rule;
            }
            return rule;
        }
    }

    sealed class BlockResult
    {
        public readonly List<CssDeclaration> Decls = new();
        public readonly List<CssRule> Rules = new();
        public SourcePosition End;
    }

    void ReportUnknown(string name, SourceRange range, bool known)
    {
        string message = known
            ? $"@{name} 暂不支持（后置），其内容被保留但不会生效。"
            : $"未知的 at 规则 @{name}，其内容被忽略。";
        Report(CssDiagnosticCodes.UnsupportedAtRule, CssDiagnosticSeverity.Warning, message, range);
    }

    CssRule Unknown(string name, List<CssComponentValue> prelude, CssSimpleBlock? block, bool known, SourceRange range)
    {
        ReportUnknown(name, range, known);
        return new CssUnknownAtRule(name, prelude, block, known, range);
    }

    void InvalidAtRule(string message, SourceRange range)
        => Report(CssDiagnosticCodes.InvalidAtRule, CssDiagnosticSeverity.Error, message, range);

    /// <summary>嵌套分组规则中的声明一律包装为嵌套声明规则（按外层样式规则匹配）。</summary>
    static List<CssRule> GroupRules(BlockResult contents, CssStyleRule? parentStyle)
    {
        var rules = contents.Rules;
        if (parentStyle is not null && contents.Decls.Count > 0)
        {
            var d = contents.Decls;
            rules.Insert(0, new CssNestedDeclarationsRule(d, parentStyle, new SourceRange(d[0].Range.Start, d[d.Count - 1].Range.End)));
        }
        return rules;
    }

    CssRule? BuildMedia(string name, List<CssComponentValue> prelude, BlockResult? contents, CssStyleRule? parentStyle, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule($"@{name} 需要一个块，已丢弃。", range);
            return null;
        }
        var media = MediaQueryParser.Parse(prelude, _d);
        return new CssMediaRule(prelude, media, GroupRules(contents, parentStyle), range);
    }

    CssRule? BuildSupports(List<CssComponentValue> prelude, BlockResult? contents, CssStyleRule? parentStyle, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule("@supports 需要一个块，已丢弃。", range);
            return null;
        }
        var result = SupportsCondition.Evaluate(prelude);
        if (result is null)
        {
            InvalidAtRule("无效的 @supports 条件，规则按不成立处理。", range);
        }
        var text = CssComponentValue.ToCss(prelude).Trim();
        return new CssSupportsRule(prelude, text, result ?? false, GroupRules(contents, parentStyle), range);
    }

    CssRule? BuildContainer(List<CssComponentValue> prelude, BlockResult? contents, CssStyleRule? parentStyle, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule("@container 需要一个块，已丢弃。", range);
            return null;
        }
        // [ <container-name> ]? <container-condition>：第一个标识符若不是 not / and / or 即为名称
        int i = 0;
        SkipWs(prelude, ref i);
        string? name = null;
        if (i < prelude.Count && prelude[i] is CssToken { Type: CssTokenType.Ident } id && !id.IsIdent("not"))
        {
            if (!Values.BehaviorParsers.IsContainerName(id.Value))
            {
                InvalidAtRule($"无效的容器名 '{id.Value}'，@container 规则被丢弃。", range);
                return null;
            }
            name = id.Value;
            i++;
        }
        var condition = MediaQueryParser.ParseContainerCondition(prelude, i, _d);
        if (condition is null)
        {
            InvalidAtRule("无效的 @container 条件（例：@container card (width > 400px)），规则被丢弃。", range);
            return null;
        }
        return new CssContainerRule(prelude, name, condition, GroupRules(contents, parentStyle), range);
    }

    CssRule? BuildStartingStyle(List<CssComponentValue> prelude, BlockResult? contents, CssStyleRule? parentStyle, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule("@starting-style 需要一个块，已丢弃。", range);
            return null;
        }
        foreach (var v in prelude)
        {
            if (v is not CssToken { Type: CssTokenType.Whitespace })
            {
                InvalidAtRule("@starting-style 不接受序言，规则被丢弃。", range);
                return null;
            }
        }
        return new CssStartingStyleRule(GroupRules(contents, parentStyle), range);
    }

    CssRule? BuildProperty(List<CssComponentValue> prelude, BlockResult? contents, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule("@property 需要一个块，已丢弃。", range);
            return null;
        }
        int i = 0;
        SkipWs(prelude, ref i);
        if (i >= prelude.Count || prelude[i] is not CssToken { Type: CssTokenType.Ident } nameTok || !nameTok.Value.StartsWith("--", StringComparison.Ordinal)
            || nameTok.Value.Length <= 2)
        {
            InvalidAtRule("@property 的名称必须是自定义属性名（--name），规则被丢弃。", range);
            return null;
        }
        i++;
        SkipWs(prelude, ref i);
        if (i != prelude.Count)
        {
            InvalidAtRule("@property 的序言只能是一个自定义属性名，规则被丢弃。", range);
            return null;
        }
        Values.CssPropertySyntax? syntax = null;
        bool? inherits = null;
        IReadOnlyList<CssComponentValue>? initial = null;
        foreach (var d in contents.Decls)
        {
            switch (d.Name)
            {
                case "syntax":
                    if (d.Value.Count == 1 && d.Value[0] is CssToken { Type: CssTokenType.String } st) syntax = Values.CssPropertySyntax.Parse(st.Value);
                    else syntax = null;
                    if (syntax is null)
                    {
                        InvalidAtRule($"@property {nameTok.Value} 的 syntax 无效（应为 \"*\" 或如 \"<length> | auto\" 的字符串），规则被丢弃。", d.Range);
                        return null;
                    }
                    break;
                case "inherits":
                    if (d.Value.Count == 1 && d.Value[0] is CssToken it && (it.IsIdent("true") || it.IsIdent("false"))) inherits = it.IsIdent("true");
                    else
                    {
                        InvalidAtRule($"@property {nameTok.Value} 的 inherits 必须是 true 或 false，规则被丢弃。", d.Range);
                        return null;
                    }
                    break;
                case "initial-value":
                    initial = d.Value;
                    break;
                default:
                    Report(CssDiagnosticCodes.InvalidAtRule, CssDiagnosticSeverity.Warning, $"@property 中未知的描述符 '{d.Name}'，已忽略。", d.NameRange);
                    break;
            }
        }
        if (syntax is null || inherits is null)
        {
            InvalidAtRule($"@property {nameTok.Value} 缺少必需的 syntax 或 inherits 描述符，规则被丢弃。", range);
            return null;
        }
        if (initial is null && !syntax.IsUniversal)
        {
            InvalidAtRule($"@property {nameTok.Value} 的语法不是 \"*\" 时必须给出 initial-value，规则被丢弃。", range);
            return null;
        }
        if (initial is not null && !syntax.IsValidInitialValue(initial))
        {
            InvalidAtRule($"@property {nameTok.Value} 的 initial-value 不符合语法 {syntax.Text} 或不是计算期独立的值（不能含 em、视口单位或 var()），规则被丢弃。", range);
            return null;
        }
        return new CssPropertyRule(nameTok.Value, syntax, inherits.Value, initial, contents.Decls, range);
    }

    CssRule? BuildLayer(List<CssComponentValue> prelude, BlockResult? contents, CssStyleRule? parentStyle, SourceRange range)
    {
        var names = ParseLayerNames(prelude);
        if (names is null)
        {
            InvalidAtRule("无效的 @layer 名称：层名是用 '.' 连接的标识符，不能使用 CSS 全局关键字。规则被丢弃。", range);
            return null;
        }
        if (contents is null)
        {
            if (names.Count == 0)
            {
                InvalidAtRule("@layer 语句至少需要一个层名。", range);
                return null;
            }
            return new CssLayerStatementRule(names, range);
        }
        if (names.Count > 1)
        {
            InvalidAtRule("块形式的 @layer 只能有一个层名。", range);
            return null;
        }
        return new CssLayerBlockRule(names.Count == 1 ? names[0] : null, GroupRules(contents, parentStyle), range);
    }

    /// <summary>解析逗号分隔的 &lt;layer-name&gt; 列表；空序言返回空列表；语法错误返回 null。</summary>
    static List<string>? ParseLayerNames(List<CssComponentValue> prelude)
    {
        var names = new List<string>();
        var current = new System.Text.StringBuilder();
        bool expectIdent = true, sawAny = false, inName = false, afterWs = false;
        for (int i = 0; i <= prelude.Count; i++)
        {
            var v = i < prelude.Count ? prelude[i] : null;
            if (v is null || v is CssToken { Type: CssTokenType.Comma })
            {
                if (!inName || expectIdent) { if (v is null && !sawAny) return names; return null; }
                names.Add(current.ToString());
                current.Clear();
                inName = false;
                expectIdent = true;
                afterWs = false;
                continue;
            }
            if (v is CssToken { Type: CssTokenType.Whitespace }) { if (inName) afterWs = true; continue; }
            sawAny = true;
            if (v is not CssToken tok) return null;
            if (expectIdent)
            {
                if (tok.Type != CssTokenType.Ident || (inName && afterWs)) return null;
                if (!inName && IsCssWideKeyword(tok.Value)) return null;
                current.Append(tok.Value);
                inName = true;
                expectIdent = false;
            }
            else
            {
                if (!tok.IsDelim('.') || afterWs) return null;
                current.Append('.');
                expectIdent = true;
            }
        }
        return names;
    }

    internal static bool IsCssWideKeyword(string ident)
    {
        var lower = CssSerializer.ToAsciiLower(ident);
        return lower is "initial" or "inherit" or "unset" or "revert" or "revert-layer" or "default";
    }

    CssRule? BuildKeyframes(List<CssComponentValue> prelude, BlockResult? contents, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule("@keyframes 需要一个块，已丢弃。", range);
            return null;
        }
        CssToken? nameTok = null;
        foreach (var v in prelude)
        {
            if (v is CssToken { Type: CssTokenType.Whitespace }) continue;
            if (nameTok is not null || v is not CssToken t) { nameTok = null; goto invalid; }
            nameTok = t;
        }
        if (nameTok is { Type: CssTokenType.String }
            || (nameTok is { Type: CssTokenType.Ident } && !IsCssWideKeyword(nameTok.Value) && !nameTok.IsIdent("none")))
        {
            var frames = new List<CssKeyframeRule>(contents.Rules.Count);
            foreach (var r in contents.Rules)
                if (r is CssKeyframeRule k) frames.Add(k);
            return new CssKeyframesRule(nameTok.Value, frames, range);
        }
    invalid:
        InvalidAtRule("无效的 @keyframes 名称：需要一个标识符（不能是 none 或 CSS 全局关键字）或字符串。规则被丢弃。", range);
        return null;
    }

    CssRule? BuildFontFace(List<CssComponentValue> prelude, BlockResult? contents, SourceRange range)
    {
        if (contents is null)
        {
            InvalidAtRule("@font-face 需要一个块，已丢弃。", range);
            return null;
        }
        foreach (var v in prelude)
        {
            if (v is not CssToken { Type: CssTokenType.Whitespace })
            {
                InvalidAtRule("@font-face 不接受序言，规则被丢弃。", range);
                return null;
            }
        }
        return new CssFontFaceRule(contents.Decls, range);
    }

    CssRule? BuildImport(List<CssComponentValue> prelude, CssSimpleBlock? block, SourceRange range)
    {
        if (block is not null)
        {
            InvalidAtRule("@import 不能带块，规则被丢弃。", range);
            return null;
        }
        int i = 0;
        SkipWs(prelude, ref i);
        string? url = null;
        if (i < prelude.Count)
        {
            switch (prelude[i])
            {
                case CssToken { Type: CssTokenType.String or CssTokenType.Url } t:
                    url = t.Value;
                    break;
                case CssFunction f when CssSerializer.EqualsIgnoreAsciiCase(f.Name, "url"):
                {
                    int j = 0;
                    SkipWs(f.Arguments, ref j);
                    if (j < f.Arguments.Count && f.Arguments[j] is CssToken { Type: CssTokenType.String } s)
                    {
                        j++;
                        SkipWs(f.Arguments, ref j);
                        if (j == f.Arguments.Count) url = s.Value;
                    }
                    break;
                }
            }
        }
        if (url is null)
        {
            InvalidAtRule("@import 需要一个字符串或 url() 作为地址，规则被丢弃。", range);
            return null;
        }
        i++;
        SkipWs(prelude, ref i);

        bool hasLayer = false;
        string? layerName = null;
        if (i < prelude.Count)
        {
            if (prelude[i] is CssToken layerTok && layerTok.IsIdent("layer"))
            {
                hasLayer = true;
                i++;
                SkipWs(prelude, ref i);
            }
            else if (prelude[i] is CssFunction lf && CssSerializer.EqualsIgnoreAsciiCase(lf.Name, "layer"))
            {
                var names = ParseLayerNames(new List<CssComponentValue>(lf.Arguments));
                if (names is null || names.Count != 1)
                {
                    InvalidAtRule("@import 的 layer() 中需要恰好一个层名，规则被丢弃。", range);
                    return null;
                }
                hasLayer = true;
                layerName = names[0];
                i++;
                SkipWs(prelude, ref i);
            }
        }

        string? supports = null;
        bool supportsMatches = true;
        if (i < prelude.Count && prelude[i] is CssFunction sf && CssSerializer.EqualsIgnoreAsciiCase(sf.Name, "supports"))
        {
            supports = CssComponentValue.ToCss(sf.Arguments).Trim();
            // supports( <supports-condition> | <declaration> )
            supportsMatches = SupportsCondition.Evaluate(sf.Arguments) ?? SupportsCondition.Evaluate(new[] { new CssSimpleBlock('(', sf.Arguments, sf.Range, true) }) ?? false;
            i++;
        }

        var rest = new List<CssComponentValue>(prelude.Count - Math.Min(i, prelude.Count));
        for (; i < prelude.Count; i++) rest.Add(prelude[i]);
        var media = MediaQueryParser.Parse(rest, _d);
        return new CssImportRule(url, hasLayer, layerName, supports, media, range) { SupportsMatches = supportsMatches };
    }

    // ================= 工具 =================

    static void SkipWs(IReadOnlyList<CssComponentValue> list, ref int i)
    {
        while (i < list.Count && list[i] is CssToken { Type: CssTokenType.Whitespace }) i++;
    }

    static SourceRange Span(SourcePosition start, SourcePosition end) => new(start, end.Offset >= start.Offset ? end : start);

    /// <summary>组件值列表去掉首尾空白后的区间；全空时为 <paramref name="fallback"/> 处的空区间。</summary>
    static SourceRange TrimmedRange(IReadOnlyList<CssComponentValue> values, SourcePosition fallback)
    {
        int first = 0, last = values.Count - 1;
        while (first <= last && values[first] is CssToken { Type: CssTokenType.Whitespace }) first++;
        while (last >= first && values[last] is CssToken { Type: CssTokenType.Whitespace }) last--;
        if (first > last) return new SourceRange(fallback, fallback);
        return new SourceRange(values[first].Range.Start, values[last].Range.End);
    }
}

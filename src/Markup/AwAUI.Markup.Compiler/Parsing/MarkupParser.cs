using System;
using System.Collections.Generic;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.Compiler.Parsing;

/// <summary>
/// <c>.awaui</c> 的解析器：在标记 / 代码 / 样式三种模式间切换（design/17「词法模式」），
/// 产出带完整区间的语法树。C# 片段只经 <see cref="CSharpScanner"/> 做边界扫描。
/// 具备错误恢复：任何输入都不抛异常、必然终止，并尽量一次报出全部问题。
/// </summary>
internal sealed class MarkupParser
{
    /// <summary>
    /// 最大嵌套深度（加权）；超过后其余内容按文本吞掉，防止解析器与各个递归遍历语法树的环节栈溢出。
    /// 元素、片段、<c>@:</c> 行各计 1；代码体（控制流、<c>@{ }</c>、<c>@switch</c>）在语法树中占两到三层、遍历时帧也更多，计 <see cref="CodeBodyWeight"/>。
    /// 按此上限，最坏情况的输入在未优化构建、512 KB 线程栈上也能完成解析、检查、格式化与代码生成。
    /// </summary>
    internal const int MaxNesting = 200;

    const int CodeBodyWeight = 3;

    static readonly HashSet<string> DirectiveNames = new(StringComparer.Ordinal)
    {
        "using", "namespace", "inherits", "implements", "typeparam", "attribute",
    };

    /// <summary>Razor 中存在而 .awaui 不支持的指令：位于顶层行首时报告 AW4007。</summary>
    static readonly HashSet<string> UnsupportedDirectiveNames = new(StringComparer.Ordinal)
    {
        "page", "inject", "layout", "model", "section", "functions", "preservewhitespace",
        "rendermode", "addTagHelper", "removeTagHelper", "tagHelperPrefix", "helper",
    };

    internal static bool IsUnsupportedDirectiveName(string name) => UnsupportedDirectiveNames.Contains(name);

    /// <summary>代码模式中，其后的 <c>( … )</c> 结束时处于语句开始位置的关键字。</summary>
    static readonly HashSet<string> HeaderKeywords = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "foreach", "using", "lock", "fixed",
    };

    /// <summary>其后直接处于语句开始位置的关键字。</summary>
    static readonly HashSet<string> StatementPrefixKeywords = new(StringComparer.Ordinal)
    {
        "else", "do", "try", "finally",
    };

    enum Context
    {
        TopLevel,
        Element,
        LineText,
    }

    enum TagEnd
    {
        Open,
        SelfClose,
        Missing,
    }

    enum CodeStop
    {
        Close,
        Label,
        Eof,
    }

    readonly SourceText _source;
    readonly string _s;
    readonly DiagnosticBag _diag;
    // 当前打开的标签名（片段为 ""），null 表示代码体边界：结束标签的匹配不跨越代码体
    readonly List<string?> _openTags = new();
    // FindClose 的结果缓存：反复出现的不配对定界符不会每次都重新扫描到文件末尾（避免二次方复杂度）
    readonly DelimiterMatcher _matcher;
    int _pos;
    int _nesting;
    bool _topSeenContent;
    // 最近一个隐式表达式的结束位置：紧跟其后的 @ 不按邮箱式文本处理（@count@unit 是两个表达式）
    int _implicitEnd = -1;
    // 属性值找结束引号失败时途经的位置（位 1：双引号，位 2：单引号）
    byte[]? _valueScanFailed;
    // LineEnd / BraceInLine 的缓存
    int _lineEndFrom = -1, _lineEnd = -1;
    int _braceFrom = -1, _braceLineEnd = -1, _brace = -1;

    public MarkupParser(SourceText source, DiagnosticBag diagnostics)
    {
        _source = source;
        _s = source.Text;
        _diag = diagnostics;
        _matcher = new DelimiterMatcher(_s);
    }

    // ================================================================ 文档

    public DocumentSyntax ParseDocument()
    {
        var items = new List<SyntaxNode>();
        while (_pos < _s.Length)
        {
            int before = _pos;
            int count = items.Count;
            ParseNode(items, Context.TopLevel);
            for (int i = count; i < items.Count; i++)
                if (IsContent(items[i])) _topSeenContent = true;
            if (_pos == before) _pos++; // 保险：保证前进
        }
        return new DocumentSyntax(new TextSpan(0, _s.Length), items);
    }

    static bool IsContent(SyntaxNode n) => n switch
    {
        DirectiveSyntax => false,
        CommentSyntax => false,
        TextSyntax t => !IsWhitespace(t.Text),
        _ => true,
    };

    // ================================================================ 标记模式

    /// <summary>在标记模式下解析一个节点（结束标签由调用方处理，除顶层与单行文本中的多余结束标签）。</summary>
    void ParseNode(List<SyntaxNode> into, Context ctx)
    {
        char c = _s[_pos];
        char n = Peek(1);
        if (c == '<')
        {
            if (n == '/')
            {
                ParseStrayEndTag();
                return;
            }
            if (n == '>')
            {
                into.Add(ParseFragment());
                return;
            }
            if (IsTagNameStart(n))
            {
                into.Add(ParseElementOrStyle());
                return;
            }
            if (IsHtmlCommentStart(_pos))
            {
                into.Add(ParseHtmlComment());
                return;
            }
            int start = _pos;
            _pos++;
            _diag.Error(DiagnosticCodes.UnclosedTag, new TextSpan(start, 1), "无法识别的 <；字面量 < 请写作 &lt;");
            into.Add(new TextSyntax(new TextSpan(start, 1), "<", "<"));
            return;
        }
        if (c == '@' && n != '@' && !IsEmailAt(_pos))
        {
            ParseAt(into, ctx);
            return;
        }
        ParseText(into, ctx);
    }

    void ParseText(List<SyntaxNode> into, Context ctx)
    {
        int start = _pos;
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c == '<') break;
            if (ctx == Context.LineText && (c == '\n' || c == '\r')) break;
            if (c == '@')
            {
                if (Peek(1) == '@') { _pos += 2; continue; }
                if (IsEmailAt(_pos)) { _pos++; continue; }
                break;
            }
            _pos++;
        }
        if (_pos == start) return;
        string raw = _s.Substring(start, _pos - start);
        into.Add(new TextSyntax(TextSpan.FromBounds(start, _pos), raw, Entities.Decode(raw)));
    }

    /// <summary>
    /// 邮箱式文本：@ 前紧邻（文本中的）字母数字、其后是标识符时不视为表达式。
    /// 紧邻的字母数字若是上一个隐式表达式的结尾（<c>@count@unit</c>），则不算邮箱式。
    /// </summary>
    bool IsEmailAt(int at) =>
        at > 0 && at != _implicitEnd && char.IsLetterOrDigit(_s[at - 1]) && IsIdentStart(PeekAt(at + 1));

    void ParseAt(List<SyntaxNode> into, Context ctx)
    {
        int start = _pos;
        char n = Peek(1);
        switch (n)
        {
            case '*':
                into.Add(ParseComment());
                return;
            case '(':
                into.Add(ParseExplicitExpression());
                return;
            case '{':
                into.Add(ParseStatementBlock());
                return;
            case ':':
                into.Add(ParseLineText());
                return;
        }
        if (!IsIdentStart(n))
        {
            _pos++;
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(start, 1),
                "@ 之后应为标识符、( 或 {；字面量 @ 请写作 @@");
            into.Add(new TextSyntax(new TextSpan(start, 1), "@", "@"));
            return;
        }

        string word = ReadIdentifierAt(start + 1);
        switch (word)
        {
            case "if":
                into.Add(ParseIf());
                return;
            case "foreach":
                into.Add(ParseLoop(LoopKind.Foreach, word));
                return;
            case "for":
                into.Add(ParseLoop(LoopKind.For, word));
                return;
            case "while":
                into.Add(ParseLoop(LoopKind.While, word));
                return;
            case "switch":
                into.Add(ParseSwitch());
                return;
            case "code":
                into.Add(ParseCodeBlock());
                return;
            case "else":
            {
                _pos = start + 5;
                _diag.Error(DiagnosticCodes.InvalidDirective, new TextSpan(start, 5), "else 没有对应的 @if（else 应紧跟在 @if 的 } 之后）");
                into.Add(new TextSyntax(new TextSpan(start, 5), "@else", "@else"));
                return;
            }
        }
        if (DirectiveNames.Contains(word))
        {
            into.Add(ParseDirective(word));
            return;
        }
        if (ctx == Context.TopLevel && _nesting == 0 && UnsupportedDirectiveNames.Contains(word)
            && CSharpScanner.IsAtLineStart(_s, start) && IsBlankOrEol(start + 1 + word.Length))
        {
            into.Add(ParseDirective(word));
            return;
        }
        into.Add(ParseImplicitExpression());
    }

    bool IsBlankOrEol(int at)
    {
        char c = PeekAt(at);
        return c == '\0' || c == ' ' || c == '\t' || c == '\r' || c == '\n';
    }

    // ---------------------------------------------------------------- 注释与表达式

    CommentSyntax ParseComment()
    {
        int start = _pos;
        int end = _s.IndexOf("*@", start + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(start, 2), "注释 @* 缺少配对的 *@");
            _pos = _s.Length;
            return new CommentSyntax(TextSpan.FromBounds(start, _pos), _s.Substring(start + 2), false);
        }
        _pos = end + 2;
        return new CommentSyntax(TextSpan.FromBounds(start, _pos), _s.Substring(start + 2, end - start - 2), true);
    }

    bool IsHtmlCommentStart(int at) => PeekAt(at) == '<' && PeekAt(at + 1) == '!' && PeekAt(at + 2) == '-' && PeekAt(at + 3) == '-';

    /// <summary>
    /// HTML 注释 <c>&lt;!-- … --&gt;</c>（与 <c>@* … *@</c> 一样不产生输出）。与 HTML 解析器一致：内容到第一个 <c>--&gt;</c>
    /// （或 <c>--!&gt;</c>）为止，<c>&lt;!--&gt;</c> 与 <c>&lt;!---&gt;</c> 是空注释；不配对时报 AW4006 并吞掉文件余下的部分。
    /// 只扫描一遍，耗时与输入长度成正比。
    /// </summary>
    CommentSyntax ParseHtmlComment()
    {
        int start = _pos;
        int body = start + 4;
        if (PeekAt(body) == '>' || (PeekAt(body) == '-' && PeekAt(body + 1) == '>'))
        {
            _pos = PeekAt(body) == '>' ? body + 1 : body + 2;
            return new CommentSyntax(TextSpan.FromBounds(start, _pos), "", true, isHtml: true);
        }
        int dash = _s.IndexOf("--", body, StringComparison.Ordinal);
        while (dash >= 0)
        {
            int close = PeekAt(dash + 2) == '>' ? dash + 3 : PeekAt(dash + 2) == '!' && PeekAt(dash + 3) == '>' ? dash + 4 : -1;
            if (close >= 0)
            {
                _pos = close;
                return new CommentSyntax(TextSpan.FromBounds(start, _pos), _s.Substring(body, dash - body), true, isHtml: true);
            }
            dash = _s.IndexOf("--", dash + 1, StringComparison.Ordinal);
        }
        _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(start, 4), "注释 <!-- 缺少配对的 -->");
        _pos = _s.Length;
        return new CommentSyntax(TextSpan.FromBounds(start, _pos), _s.Substring(body), false, isHtml: true);
    }

    ExpressionSyntax ParseExplicitExpression()
    {
        int start = _pos;
        int open = start + 1;
        int close = FindClose(open);
        int codeEnd, end;
        if (close < 0)
        {
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(start, 2), "@( 缺少配对的 )");
            codeEnd = end = LineEnd(open + 1);
        }
        else
        {
            codeEnd = close;
            end = close + 1;
            if (IsWhitespace(_s, open + 1, codeEnd))
                _diag.Error(DiagnosticCodes.UnbalancedDelimiter, TextSpan.FromBounds(start, end), "@( ) 中的表达式为空");
        }
        _pos = end;
        var codeSpan = TextSpan.FromBounds(open + 1, codeEnd);
        return new ExpressionSyntax(TextSpan.FromBounds(start, end), _source.ToString(codeSpan), codeSpan, true);
    }

    /// <summary>
    /// 隐式表达式：从标识符开始，贪婪地接受 <c>.标识符</c>、<c>?.标识符</c>、<c>(…)</c>、<c>[…]</c>、
    /// 以及后接 <c>.</c> 或 <c>[</c> 的 <c>!</c>；其他任何字符结束表达式（design/17）。
    /// </summary>
    ExpressionSyntax ParseImplicitExpression()
    {
        int start = _pos;
        int i = start + 1;
        i = SkipIdentifier(i);
        while (i < _s.Length)
        {
            char c = _s[i];
            if (c == '.' && IsIdentStart(PeekAt(i + 1)))
            {
                i = SkipIdentifier(i + 1);
            }
            else if (c == '?' && PeekAt(i + 1) == '.' && IsIdentStart(PeekAt(i + 2)))
            {
                i = SkipIdentifier(i + 2);
            }
            else if (c == '(' || c == '[')
            {
                int close = FindClose(i);
                if (close < 0)
                {
                    _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(i, 1), $"表达式中的 {c} 缺少配对的 {(c == '(' ? ')' : ']')}");
                    i = LineEnd(i + 1);
                    break;
                }
                i = close + 1;
            }
            else if (c == '!' && (PeekAt(i + 1) == '.' || PeekAt(i + 1) == '['))
            {
                i++;
            }
            else
            {
                break;
            }
        }
        _pos = i;
        _implicitEnd = i;
        var codeSpan = TextSpan.FromBounds(start + 1, i);
        return new ExpressionSyntax(TextSpan.FromBounds(start, i), _source.ToString(codeSpan), codeSpan, false);
    }

    SyntaxNode ParseLineText()
    {
        int start = _pos;
        if (_nesting >= MaxNesting) return TooDeep(start); // @:@:@:… 也会递归
        _pos += 2;
        var children = new List<SyntaxNode>();
        _nesting++;
        while (_pos < _s.Length && _s[_pos] != '\n' && _s[_pos] != '\r')
        {
            int before = _pos;
            ParseNode(children, Context.LineText);
            if (_pos == before) _pos++;
        }
        _nesting--;
        return new LineTextSyntax(TextSpan.FromBounds(start, _pos), children);
    }

    // ---------------------------------------------------------------- 指令与 @code

    DirectiveSyntax ParseDirective(string word)
    {
        int start = _pos;
        var nameSpan = new TextSpan(start, 1 + word.Length);
        var kind = word switch
        {
            "using" => DirectiveKind.Using,
            "namespace" => DirectiveKind.Namespace,
            "inherits" => DirectiveKind.Inherits,
            "implements" => DirectiveKind.Implements,
            "typeparam" => DirectiveKind.TypeParam,
            "attribute" => DirectiveKind.Attribute,
            _ => DirectiveKind.Unknown,
        };
        _pos = nameSpan.End;
        while (_pos < _s.Length && (_s[_pos] == ' ' || _s[_pos] == '\t')) _pos++;
        int valueStart = _pos;
        int lineEnd = LineEnd(_pos);
        if (kind == DirectiveKind.Attribute && Peek(0) == '[')
        {
            int close = FindClose(_pos);
            if (close < 0)
            {
                _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(_pos, 1), "@attribute 的 [ 缺少配对的 ]");
                _pos = lineEnd;
            }
            else
            {
                _pos = close + 1;
            }
        }
        else if (kind == DirectiveKind.Unknown)
        {
            // @functions { … } / @section X { … }：连同代码块一起吞掉，避免级联错误
            int brace = BraceInLine(_pos);
            if (brace < 0 && lineEnd < _s.Length)
            {
                int next = SkipWhitespace(lineEnd);
                if (next < _s.Length && _s[next] == '{') brace = next;
            }
            int close = brace >= 0 ? FindClose(brace) : -1;
            _pos = close >= 0 ? close + 1 : lineEnd;
        }
        else
        {
            _pos = lineEnd;
        }

        // 去掉首尾空白与末尾分号
        int vs = valueStart, ve = _pos;
        while (ve > vs && char.IsWhiteSpace(_s[ve - 1])) ve--;
        if (kind != DirectiveKind.Unknown && ve > vs && _s[ve - 1] == ';')
        {
            ve--;
            while (ve > vs && char.IsWhiteSpace(_s[ve - 1])) ve--;
        }
        var valueSpan = TextSpan.FromBounds(vs, ve);
        string value = _source.ToString(valueSpan);
        var span = TextSpan.FromBounds(start, _pos);

        if (kind == DirectiveKind.Unknown)
            _diag.Error(DiagnosticCodes.InvalidDirective, nameSpan, $"不支持的指令 @{word}");
        else if (value.Length == 0)
            _diag.Error(DiagnosticCodes.InvalidDirective, nameSpan, $"指令 @{word} 缺少参数");

        if (kind != DirectiveKind.Unknown)
        {
            if (_nesting > 0)
                _diag.Error(DiagnosticCodes.InvalidDirective, nameSpan, $"指令 @{word} 只能出现在文件顶层");
            else if (_topSeenContent)
                _diag.Error(DiagnosticCodes.InvalidDirective, nameSpan, $"指令 @{word} 必须位于文件开头（标记、@code 与 <style> 之前）");
        }
        return new DirectiveSyntax(span, kind, word, nameSpan, value, valueSpan);
    }

    CodeBlockSyntax ParseCodeBlock()
    {
        int start = _pos;
        var keywordSpan = new TextSpan(start, 5);
        if (_nesting > 0)
            _diag.Error(DiagnosticCodes.InvalidDirective, keywordSpan, "@code 只能出现在文件顶层");
        int p = SkipWhitespace(start + 5);
        if (PeekAt(p) != '{')
        {
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, keywordSpan, "@code 之后应为 {");
            _pos = start + 5;
            return new CodeBlockSyntax(keywordSpan, "", new TextSpan(_pos, 0), false);
        }
        int close = FindClose(p);
        if (close < 0)
        {
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(p, 1), "@code 的 { 缺少配对的 }");
            _pos = _s.Length;
            var cs = TextSpan.FromBounds(p + 1, _pos);
            return new CodeBlockSyntax(TextSpan.FromBounds(start, _pos), _source.ToString(cs), cs, false);
        }
        _pos = close + 1;
        var codeSpan = TextSpan.FromBounds(p + 1, close);
        return new CodeBlockSyntax(TextSpan.FromBounds(start, _pos), _source.ToString(codeSpan), codeSpan, true);
    }

    // ---------------------------------------------------------------- 元素

    SyntaxNode ParseElementOrStyle()
    {
        int start = _pos;
        int nameStart = start + 1;
        int nameEnd = SkipTagName(nameStart);
        string name = _s.Substring(nameStart, nameEnd - nameStart);
        var nameSpan = TextSpan.FromBounds(nameStart, nameEnd);
        if (name == "style") return ParseStyle(start, nameSpan);
        if (_nesting >= MaxNesting) return TooDeep(start);

        _pos = nameEnd;
        string? typeArgs = null;
        if (ElementSyntax.Classify(name) == ElementKind.Component && Peek(0) == '<')
        {
            int close = FindTypeArgsClose(_pos);
            if (close < 0)
            {
                _diag.Error(DiagnosticCodes.UnclosedTag, new TextSpan(_pos, 1), $"<{name}< 的类型实参缺少配对的 >");
            }
            else
            {
                typeArgs = _s.Substring(_pos + 1, close - _pos - 1).Trim();
                _pos = close + 1;
            }
        }

        var attributes = ParseAttributes(out var tagEnd);
        var startTag = TextSpan.FromBounds(start, _pos);
        if (tagEnd == TagEnd.SelfClose)
            return new ElementSyntax(startTag, name, nameSpan, typeArgs, attributes, Array.Empty<SyntaxNode>(), true, startTag, null);
        if (tagEnd == TagEnd.Missing)
        {
            _diag.Error(DiagnosticCodes.UnclosedTag, nameSpan, $"开始标签 <{name}> 缺少 > 或 />");
            if (_pos >= _s.Length)
                return new ElementSyntax(startTag, name, nameSpan, typeArgs, attributes, Array.Empty<SyntaxNode>(), false, startTag, null);
        }

        if (IsVoidLike(name))
        {
            // 空元素：允许紧跟一个匹配的结束标签，否则按自闭合处理并报告
            int p = SkipWhitespace(_pos);
            if (PeekAt(p) == '<' && PeekAt(p + 1) == '/')
            {
                ReadEndTag(p, out string endName, out int endEnd, out bool wellFormed);
                if (endName == name && wellFormed)
                {
                    _pos = endEnd;
                    return new ElementSyntax(TextSpan.FromBounds(start, endEnd), name, nameSpan, typeArgs, attributes,
                        Array.Empty<SyntaxNode>(), false, startTag, TextSpan.FromBounds(p, endEnd));
                }
            }
            if (tagEnd == TagEnd.Open)
                _diag.Error(DiagnosticCodes.UnclosedTag, nameSpan, $"<{name}> 是空元素，请写作 <{name} />");
            return new ElementSyntax(startTag, name, nameSpan, typeArgs, attributes, Array.Empty<SyntaxNode>(), true, startTag, null);
        }

        var children = new List<SyntaxNode>();
        _nesting++;
        _openTags.Add(name);
        var endTag = ParseChildren(children, name);
        _openTags.RemoveAt(_openTags.Count - 1);
        _nesting--;
        if (endTag == null && tagEnd != TagEnd.Missing)
            _diag.Error(DiagnosticCodes.UnclosedTag, nameSpan, $"元素 <{name}> 缺少结束标签 </{name}>");
        return new ElementSyntax(TextSpan.FromBounds(start, _pos), name, nameSpan, typeArgs, attributes, children, false, startTag, endTag);
    }

    SyntaxNode ParseFragment()
    {
        int start = _pos;
        if (_nesting >= MaxNesting) return TooDeep(start);
        _pos += 2;
        var startTag = new TextSpan(start, 2);
        var children = new List<SyntaxNode>();
        _nesting++;
        _openTags.Add("");
        var endTag = ParseChildren(children, "");
        _openTags.RemoveAt(_openTags.Count - 1);
        _nesting--;
        if (endTag == null)
            _diag.Error(DiagnosticCodes.UnclosedTag, startTag, "片段 <> 缺少结束标签 </>");
        return new FragmentSyntax(TextSpan.FromBounds(start, _pos), children, startTag, endTag);
    }

    /// <summary>解析子节点直到匹配的结束标签（返回其区间）、祖先的结束标签或文件末尾（返回 null）。</summary>
    TextSpan? ParseChildren(List<SyntaxNode> children, string closeName)
    {
        while (_pos < _s.Length)
        {
            int before = _pos;
            if (_s[_pos] == '<' && Peek(1) == '/')
            {
                int tagStart = _pos;
                ReadEndTag(tagStart, out string name, out int end, out bool wellFormed);
                bool isFragmentClose = name.Length == 0;
                if (name == closeName && (!isFragmentClose || wellFormed))
                {
                    if (!wellFormed)
                        _diag.Error(DiagnosticCodes.UnclosedTag, TextSpan.FromBounds(tagStart, end), $"结束标签 </{name}> 缺少 >");
                    _pos = end;
                    return TextSpan.FromBounds(tagStart, end);
                }
                if ((!isFragmentClose || wellFormed) && IsOpenInSegment(name))
                    return null; // 由祖先接手；当前元素未闭合
                _pos = end;
                _diag.Error(DiagnosticCodes.UnclosedTag, TextSpan.FromBounds(tagStart, end),
                    closeName.Length == 0 && _openTags.Count > 0
                        ? $"多余的结束标签 </{name}>（当前打开的是片段 <>）"
                        : $"多余的结束标签 </{name}>（当前打开的是 <{closeName}>）");
                continue;
            }
            ParseNode(children, Context.Element);
            if (_pos == before) _pos++;
        }
        return null;
    }

    bool IsOpenInSegment(string name)
    {
        for (int i = _openTags.Count - 2; i >= 0; i--)
        {
            var t = _openTags[i];
            if (t == null) return false;
            if (t == name) return true;
        }
        return false;
    }

    void ParseStrayEndTag()
    {
        int start = _pos;
        ReadEndTag(start, out string name, out int end, out _);
        _pos = end;
        _diag.Error(DiagnosticCodes.UnclosedTag, TextSpan.FromBounds(start, end), $"多余的结束标签 </{name}>，没有对应的开始标签");
    }

    /// <summary>读取 pos 处（&lt;/）的结束标签：名称、结束位置；缺少 &gt; 时 wellFormed 为 false 且只吞到名称之后。</summary>
    void ReadEndTag(int pos, out string name, out int end, out bool wellFormed)
    {
        int p = pos + 2;
        int nameEnd = IsTagNameStart(PeekAt(p)) ? SkipTagName(p) : p;
        name = _s.Substring(p, nameEnd - p);
        int q = nameEnd;
        if (PeekAt(q) == '<' && name.Length > 0)
        {
            int close = FindTypeArgsClose(q);
            if (close >= 0) q = close + 1;
        }
        int r = SkipWhitespace(q);
        if (PeekAt(r) == '>')
        {
            end = r + 1;
            wellFormed = true;
        }
        else
        {
            end = q;
            wellFormed = false;
        }
    }

    int FindTypeArgsClose(int open)
    {
        int depth = 0;
        for (int i = open; i < _s.Length; i++)
        {
            char c = _s[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                if (--depth == 0) return i;
            }
            else if (c == '\n' || c == '\r' || c == '"' || c == '\'' || c == '/' || c == '@' || c == '=') return -1;
        }
        return -1;
    }

    static bool IsVoidLike(string name) => name == "img" || name == "input";

    List<AttributeSyntax> ParseAttributes(out TagEnd tagEnd)
    {
        var list = new List<AttributeSyntax>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            _pos = SkipWhitespace(_pos);
            if (_pos >= _s.Length)
            {
                tagEnd = TagEnd.Missing;
                return list;
            }
            char c = _s[_pos];
            char n = Peek(1);
            if (c == '/' && n == '>')
            {
                _pos += 2;
                tagEnd = TagEnd.SelfClose;
                return list;
            }
            if (c == '>')
            {
                _pos++;
                tagEnd = TagEnd.Open;
                return list;
            }
            if (c == '<')
            {
                if (IsHtmlCommentStart(_pos))
                {
                    // 与 @* *@ 相同：开始标签内不允许注释（HTML 也不允许），报告后跳过它继续解析属性
                    var comment = ParseHtmlComment();
                    _diag.Error(DiagnosticCodes.UnclosedTag, comment.Span, "开始标签内不支持 <!-- --> 注释");
                    continue;
                }
                tagEnd = TagEnd.Missing;
                return list;
            }

            int attrStart = _pos;
            string name;
            string? parameter = null;
            bool directive = false;
            if (c == '@')
            {
                if (!IsIdentStart(n))
                {
                    if (n == '*')
                    {
                        var comment = ParseComment();
                        _diag.Error(DiagnosticCodes.UnclosedTag, comment.Span, "开始标签内不支持 @* *@ 注释");
                    }
                    else
                    {
                        _pos++;
                        _diag.Error(DiagnosticCodes.UnclosedTag, new TextSpan(attrStart, 1), "开始标签中无效的 @：指令属性应为 @名称");
                    }
                    continue;
                }
                directive = true;
                int ns = _pos + 1;
                int ne = ns;
                while (ne < _s.Length && (IsIdentPart(_s[ne]) || _s[ne] == '-')) ne++;
                name = _s.Substring(ns, ne - ns);
                _pos = ne;
                if (Peek(0) == ':' && IsIdentStart(Peek(1)))
                {
                    int ps = _pos + 1;
                    int pe = ps;
                    while (pe < _s.Length && (IsIdentPart(_s[pe]) || _s[pe] == '-')) pe++;
                    parameter = _s.Substring(ps, pe - ps);
                    _pos = pe;
                }
            }
            else if (IsIdentStart(c))
            {
                int ne = _pos + 1;
                while (ne < _s.Length && (IsIdentPart(_s[ne]) || _s[ne] == '-' || _s[ne] == ':' || _s[ne] == '.')) ne++;
                name = _s.Substring(_pos, ne - _pos);
                _pos = ne;
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    int close = _s.IndexOf(c, _pos + 1);
                    int le = LineEnd(_pos + 1);
                    _pos = close >= 0 && close < le ? close + 1 : _pos + 1;
                }
                else
                {
                    _pos++;
                }
                _diag.Error(DiagnosticCodes.UnclosedTag, TextSpan.FromBounds(attrStart, _pos), $"开始标签中出现意外的字符 '{c}'");
                continue;
            }

            var nameSpan = TextSpan.FromBounds(attrStart, _pos);
            AttributeValueSyntax? value = null;
            int afterName = _pos;
            int eq = SkipWhitespace(_pos);
            if (PeekAt(eq) == '=')
            {
                _pos = SkipWhitespace(eq + 1);
                value = ParseAttributeValue(name);
            }
            else
            {
                _pos = afterName;
            }
            var attr = new AttributeSyntax(TextSpan.FromBounds(attrStart, _pos), name, nameSpan, directive, parameter, value);
            if (!seen.Add(attr.FullName))
                _diag.Error(DiagnosticCodes.DuplicateAttribute, nameSpan, $"重复的属性 {attr.FullName}");
            list.Add(attr);
        }
    }

    AttributeValueSyntax ParseAttributeValue(string attrName)
    {
        int start = _pos;
        char c = Peek(0);
        if (c == '"' || c == '\'') return ParseQuotedValue(c);
        if (c == '@' && (Peek(1) == '(' || IsIdentStart(Peek(1))))
        {
            var expr = Peek(1) == '(' ? ParseExplicitExpression() : ParseImplicitExpression();
            return new AttributeValueSyntax(expr.Span, '\0', new SyntaxNode[] { expr });
        }
        if (c == '\0' || c == '>' || c == '<' || (c == '/' && Peek(1) == '>'))
        {
            _diag.Error(DiagnosticCodes.InvalidAttributeValue, new TextSpan(start, 0), $"属性 {attrName} 的 = 之后缺少值");
            return new AttributeValueSyntax(new TextSpan(start, 0), '\0', Array.Empty<SyntaxNode>());
        }
        // 无引号的字面量（宽松接受，格式化时补上引号）。其中的 @ 与引号内的规则一致：@@ 或邮箱式才是字面量
        while (_pos < _s.Length)
        {
            char d = _s[_pos];
            if (char.IsWhiteSpace(d) || d == '>' || d == '<' || d == '"' || d == '\'' || (d == '/' && Peek(1) == '>')) break;
            if (d == '@')
            {
                if (Peek(1) == '@') { _pos += 2; continue; }
                if (!IsEmailAt(_pos))
                    _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(_pos, 1), "属性值中的 @ 之后应为标识符或 (；字面量 @ 请写作 @@");
            }
            _pos++;
        }
        if (_pos == start) _pos++;
        string raw = _s.Substring(start, _pos - start);
        var span = TextSpan.FromBounds(start, _pos);
        return new AttributeValueSyntax(span, '\0', new SyntaxNode[] { new AttributeLiteralSyntax(span, raw, Entities.Decode(raw)) });
    }

    AttributeValueSyntax ParseQuotedValue(char quote)
    {
        int open = _pos;
        int mark = _diag.Count;
        var parts = new List<SyntaxNode>();
        if (TryParseValueParts(open + 1, _s.Length, quote, parts))
            return new AttributeValueSyntax(TextSpan.FromBounds(open, _pos), quote, parts);

        // 找不到结束引号：回滚，以行尾为界重新解析
        _diag.Truncate(mark);
        parts.Clear();
        int limit = LineEnd(open + 1);
        TryParseValueParts(open + 1, limit, quote, parts);
        _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(open, 1), $"属性值缺少结束引号 {quote}");
        return new AttributeValueSyntax(TextSpan.FromBounds(open, _pos), quote, parts);
    }

    bool TryParseValueParts(int from, int limit, char quote, List<SyntaxNode> parts)
    {
        _pos = from;
        int litStart = from;
        // 一直找到文件末尾也没有结束引号的扫描，把途经的位置记下来：之后从别处出发的扫描一旦走到这些位置就必然同样失败，
        // 直接返回，避免每个出错的属性值都重新扫描到文件末尾（二次方复杂度）
        byte bit = quote == '"' ? (byte)1 : (byte)2;
        List<int>? visited = null;
        if (limit == _s.Length)
        {
            _valueScanFailed ??= new byte[_s.Length + 1];
            visited = new List<int>();
        }
        void Flush(int end)
        {
            if (end > litStart)
            {
                string raw = _s.Substring(litStart, end - litStart);
                parts.Add(new AttributeLiteralSyntax(TextSpan.FromBounds(litStart, end), raw, Entities.Decode(raw)));
            }
        }
        while (_pos < limit)
        {
            char c = _s[_pos];
            // 前面紧邻字母数字的 @ 是否按邮箱式处理取决于来路（_implicitEnd），这样的位置不记录也不查询
            if (visited != null && !(c == '@' && _pos > 0 && char.IsLetterOrDigit(_s[_pos - 1])))
            {
                if ((_valueScanFailed![_pos] & bit) != 0) break;
                visited.Add(_pos);
            }
            if (c == quote)
            {
                Flush(_pos);
                _pos++;
                return true;
            }
            if (c == '@')
            {
                char n = Peek(1);
                if (n == '@')
                {
                    _pos += 2;
                    continue;
                }
                if (n == '(' || (IsIdentStart(n) && !IsEmailAt(_pos)))
                {
                    Flush(_pos);
                    parts.Add(n == '(' ? ParseExplicitExpression() : ParseImplicitExpression());
                    litStart = _pos;
                    continue;
                }
                if (!IsIdentStart(n))
                    _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(_pos, 1), "属性值中的 @ 之后应为标识符或 (；字面量 @ 请写作 @@");
            }
            _pos++;
        }
        if (visited != null)
            foreach (int v in visited) _valueScanFailed![v] |= bit;
        if (_pos > limit) return false;
        Flush(_pos);
        return false;
    }

    StyleBlockSyntax ParseStyle(int start, TextSpan nameSpan)
    {
        _pos = nameSpan.End;
        var attributes = ParseAttributes(out var tagEnd);
        bool isGlobal = false;
        // 内联 svg 子树中的 <style> 属于 SVG 文档（随文档序列化），不是组件样式块
        bool inSvg = false;
        foreach (var t in _openTags)
            if (t == "svg") inSvg = true;
        foreach (var a in attributes)
        {
            if (inSvg)
            {
                if (a.IsDirective || a.Name == "global")
                    _diag.Error(DiagnosticCodes.InvalidStyleBlock, a.NameSpan, $"内联 <svg> 中的 <style> 属于 SVG 文档，不支持 {a.FullName}");
                continue;
            }
            if (!a.IsDirective && a.Name == "global" && a.Parameter == null)
            {
                if (a.Value != null)
                    _diag.Error(DiagnosticCodes.InvalidStyleBlock, a.Span, "<style> 的 global 不接受值，只写 global 即可");
                isGlobal = true;
            }
            else
            {
                _diag.Error(DiagnosticCodes.InvalidStyleBlock, a.NameSpan, $"<style> 不支持属性 {a.FullName}（只允许 global）");
            }
        }
        if (_nesting > 0 && !inSvg)
            _diag.Error(DiagnosticCodes.InvalidStyleBlock, nameSpan, "<style> 只能出现在文件顶层");

        if (tagEnd == TagEnd.SelfClose)
            return new StyleBlockSyntax(TextSpan.FromBounds(start, _pos), isGlobal, attributes, "", new TextSpan(_pos, 0), true, inSvg);
        if (tagEnd == TagEnd.Missing)
        {
            _diag.Error(DiagnosticCodes.UnclosedTag, nameSpan, "开始标签 <style> 缺少 >");
            return new StyleBlockSyntax(TextSpan.FromBounds(start, _pos), isGlobal, attributes, "", new TextSpan(_pos, 0), false, inSvg);
        }

        int contentStart = _pos;
        int search = _pos;
        while (true)
        {
            int idx = _s.IndexOf("</style", search, StringComparison.Ordinal);
            if (idx < 0) break;
            int after = idx + 7;
            if (!IsTagNamePart(PeekAt(after)))
            {
                int r = SkipWhitespace(after);
                var contentSpan = TextSpan.FromBounds(contentStart, idx);
                if (PeekAt(r) == '>')
                {
                    _pos = r + 1;
                }
                else
                {
                    _diag.Error(DiagnosticCodes.UnclosedTag, TextSpan.FromBounds(idx, after), "结束标签 </style> 缺少 >");
                    _pos = after;
                }
                return new StyleBlockSyntax(TextSpan.FromBounds(start, _pos), isGlobal, attributes, _source.ToString(contentSpan), contentSpan, true, inSvg);
            }
            search = after;
        }
        _diag.Error(DiagnosticCodes.UnclosedTag, nameSpan, "<style> 缺少结束标签 </style>");
        _pos = _s.Length;
        var rest = TextSpan.FromBounds(contentStart, _pos);
        return new StyleBlockSyntax(TextSpan.FromBounds(start, _pos), isGlobal, attributes, _source.ToString(rest), rest, false, inSvg);
    }

    // ================================================================ 代码模式

    StatementBlockSyntax ParseStatementBlock()
    {
        int start = _pos;
        var body = ParseCodeBody(start + 1);
        return new StatementBlockSyntax(TextSpan.FromBounds(start, body.Span.End), body);
    }

    /// <summary>从 <paramref name="bracePos"/> 处的 { 开始解析代码体直到配对的 }。</summary>
    CodeBodySyntax ParseCodeBody(int bracePos)
    {
        if (_nesting + CodeBodyWeight > MaxNesting)
        {
            TooDeep(bracePos);
            return new CodeBodySyntax(TextSpan.FromBounds(bracePos, _pos), Array.Empty<SyntaxNode>(), true, false);
        }
        _pos = bracePos + 1;
        var nodes = new List<SyntaxNode>();
        _nesting += CodeBodyWeight;
        _openTags.Add(null);
        var stop = ParseCodeNodes(nodes, false);
        _openTags.RemoveAt(_openTags.Count - 1);
        _nesting -= CodeBodyWeight;
        bool closed = stop == CodeStop.Close;
        if (!closed)
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(bracePos, 1), "{ 缺少配对的 }");
        return new CodeBodySyntax(TextSpan.FromBounds(bracePos, _pos), nodes, true, closed);
    }

    /// <summary>
    /// 代码模式的主循环：收集 C# 片段，在语句开始位置遇到标签、<c>@</c> 转换或 <c>@:</c> 时切换到标记。
    /// 遇到深度 0 的 } 时吞掉它并返回 Close；switch 模式下遇到 case/default 标签时停在标签前返回 Label。
    /// </summary>
    CodeStop ParseCodeNodes(List<SyntaxNode> nodes, bool switchMode)
    {
        int chunkStart = _pos;
        bool stmtStart = true;
        bool labelPending = false;
        int depth = 0;

        void Flush(int end)
        {
            if (end > chunkStart && !IsWhitespace(_s, chunkStart, end))
                nodes.Add(new CSharpCodeSyntax(TextSpan.FromBounds(chunkStart, end), _s.Substring(chunkStart, end - chunkStart)));
        }

        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            char n = Peek(1);
            if (char.IsWhiteSpace(c))
            {
                _pos++;
                continue;
            }

            // 注释、字符串、字符字面量、预处理行
            int atomEnd = CSharpScanner.SkipAtom(_s, _pos);
            if (atomEnd > _pos)
            {
                if (!CSharpScanner.IsTriviaAtom(_s, _pos)) stmtStart = false;
                _pos = atomEnd;
                continue;
            }

            if (c == '@')
            {
                if (n == '*')
                {
                    Flush(_pos);
                    nodes.Add(ParseComment());
                    chunkStart = _pos;
                    continue;
                }
                if (stmtStart && (n == ':' || n == '(' || n == '{' || IsIdentStart(n)))
                {
                    Flush(_pos);
                    ParseAt(nodes, Context.Element);
                    chunkStart = _pos;
                    stmtStart = true;
                    continue;
                }
                // C# 的逐字标识符等
                _pos++;
                stmtStart = false;
                continue;
            }

            if (c == '<' && stmtStart)
            {
                if (IsHtmlCommentStart(_pos))
                {
                    Flush(_pos);
                    nodes.Add(ParseHtmlComment());
                    chunkStart = _pos;
                    continue;
                }
                if (n == '>' || IsTagNameStart(n))
                {
                    Flush(_pos);
                    nodes.Add(n == '>' ? ParseFragment() : ParseElementOrStyle());
                    chunkStart = _pos;
                    stmtStart = true;
                    continue;
                }
                if (n == '/')
                {
                    Flush(_pos);
                    ParseStrayEndTag();
                    chunkStart = _pos;
                    continue;
                }
            }

            switch (c)
            {
                case '{' when labelPending:
                {
                    // case 标签中的属性模式 case Point { X: 0 }: —— 其中的 : 不结束标签
                    int close = FindClose(_pos);
                    if (close >= 0)
                    {
                        _pos = close + 1;
                        continue;
                    }
                    depth++;
                    stmtStart = true;
                    _pos++;
                    continue;
                }
                case '{':
                    depth++;
                    stmtStart = true;
                    _pos++;
                    continue;
                case '}':
                    if (depth == 0)
                    {
                        Flush(_pos);
                        _pos++;
                        return CodeStop.Close;
                    }
                    depth--;
                    stmtStart = true;
                    _pos++;
                    continue;
                case ';':
                    stmtStart = true;
                    labelPending = false;
                    _pos++;
                    continue;
                case ':' when labelPending && n != ':':
                    labelPending = false;
                    stmtStart = true;
                    _pos++;
                    continue;
                case '(':
                case '[':
                {
                    int close = FindClose(_pos);
                    if (close < 0)
                        _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(_pos, 1), $"{c} 缺少配对的 {(c == '(' ? ')' : ']')}");
                    _pos = close >= 0 ? close + 1 : _pos + 1;
                    stmtStart = false;
                    continue;
                }
            }

            if (IsIdentStart(c))
            {
                int wordEnd = SkipIdentifier(_pos);
                string word = _s.Substring(_pos, wordEnd - _pos);
                if (switchMode && depth == 0 && stmtStart && IsSwitchLabelAt(_pos, word))
                {
                    Flush(_pos);
                    return CodeStop.Label;
                }
                _pos = wordEnd;
                if (HeaderKeywords.Contains(word))
                {
                    int p = SkipWhitespace(_pos);
                    if (PeekAt(p) == '(')
                    {
                        int close = FindClose(p);
                        if (close < 0)
                        {
                            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(p, 1), $"{word} 的 ( 缺少配对的 )");
                            _pos = LineEnd(p);
                        }
                        else
                        {
                            _pos = close + 1;
                        }
                        stmtStart = true;
                        continue;
                    }
                    stmtStart = false;
                }
                else if (StatementPrefixKeywords.Contains(word))
                {
                    stmtStart = true;
                }
                else if (stmtStart && (word == "case" || word == "default"))
                {
                    labelPending = true;
                    stmtStart = false;
                }
                else
                {
                    stmtStart = false;
                }
                continue;
            }

            stmtStart = false;
            _pos++;
        }
        Flush(_pos);
        return CodeStop.Eof;
    }

    bool IsSwitchLabelAt(int pos, string word)
    {
        if (word == "case") return true;
        if (word != "default") return false;
        // 语句开始处的 default 只能是标签（缺少 : 时作为出错的标签报告），除非是 default(T)… / default.… 表达式语句
        char next = PeekAt(SkipWhitespace(pos + 7));
        return next != '(' && next != '.';
    }

    /// <summary>
    /// 解析 <c>case cs-balanced :</c> 或 <c>default :</c>（design/17）。<c>cs-balanced</c> 按定界符配对扫描到深度 0 的 <c>:</c>，
    /// 因此属性模式 <c>case Point { X: 0 }:</c>、关系模式 <c>case &lt; 0:</c>、泛型 <c>case List&lt;int&gt; l:</c> 都能正确识别；
    /// 字符串、字符字面量、注释中的 : 与三元运算符的 : 不算。缺少 : 时按行内的标记或 { 处截断并报告。
    /// </summary>
    SwitchLabelSyntax ParseSwitchLabel()
    {
        int start = _pos;
        bool isDefault = _s[start] == 'd';
        int afterKeyword = SkipIdentifier(start);
        int colon = SkipWhitespace(afterKeyword);
        int end = !isDefault ? FindLabelColon(afterKeyword) : PeekAt(colon) == ':' ? colon + 1 : -1;
        if (end < 0)
        {
            end = Math.Max(afterKeyword, TrimEndWhitespace(start, LabelRecoveryEnd(afterKeyword)));
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, TextSpan.FromBounds(start, end), isDefault ? "default 之后缺少 :" : "case 标签缺少 :");
        }
        _pos = end;
        return new SwitchLabelSyntax(TextSpan.FromBounds(start, end), _s.Substring(start, end - start), isDefault);
    }

    /// <summary>
    /// 从 <paramref name="i"/> 起按 <c>cs-balanced</c> 找到结束标签的 <c>:</c>，返回其后位置；
    /// 遇到 ; 、多余的结束符、下一个 case（或行首的 default）标签、文件末尾时返回 -1。
    /// 在下一个标签处停止保证了连续多个缺少 : 的标签总扫描量仍是线性的。
    /// </summary>
    int FindLabelColon(int i)
    {
        int pendingTernary = 0;
        while (i < _s.Length)
        {
            int atomEnd = CSharpScanner.SkipAtom(_s, i);
            if (atomEnd > i)
            {
                i = atomEnd;
                continue;
            }
            char c = _s[i];
            char n = PeekAt(i + 1);
            if (IsIdentStart(c))
            {
                int wordEnd = SkipIdentifier(i);
                bool verbatimOrMember = i > 0 && (_s[i - 1] == '@' || _s[i - 1] == '.');
                if (!verbatimOrMember && (MatchKeyword(i, "case") || (MatchKeyword(i, "default") && CSharpScanner.IsAtLineStart(_s, i))))
                    return -1;
                i = wordEnd;
                continue;
            }
            switch (c)
            {
                case ':':
                    if (n == ':') { i += 2; continue; }
                    if (pendingTernary > 0) { pendingTernary--; i++; continue; }
                    return i + 1;
                case '?':
                    // ?. ?[ ?? ??= 不是三元运算符
                    if (n == '?') { i += 2; continue; }
                    if (n != '.' && n != '[') pendingTernary++;
                    break;
                case '(':
                case '[':
                case '{':
                {
                    int close = FindClose(i);
                    if (close < 0) return -1;
                    i = close + 1;
                    continue;
                }
                case ')':
                case ']':
                case '}':
                case ';':
                    return -1;
            }
            i++;
        }
        return -1;
    }

    /// <summary>缺少 : 时标签的结束位置：行尾，或更早的 { } ; 以及空白之后的开始标签。</summary>
    int LabelRecoveryEnd(int i)
    {
        int lineEnd = LineEnd(i);
        while (i < lineEnd)
        {
            int atomEnd = CSharpScanner.SkipAtom(_s, i);
            if (atomEnd > i)
            {
                i = Math.Min(atomEnd, lineEnd);
                continue;
            }
            char c = _s[i];
            if (c == '{' || c == '}' || c == ';') return i;
            if (c == '<' && i > 0 && char.IsWhiteSpace(_s[i - 1]) && (IsTagNameStart(PeekAt(i + 1)) || PeekAt(i + 1) == '>')) return i;
            if (c == '(' || c == '[')
            {
                int close = FindClose(i);
                if (close >= 0 && close < lineEnd)
                {
                    i = close + 1;
                    continue;
                }
            }
            i++;
        }
        return lineEnd;
    }

    // ---------------------------------------------------------------- 控制流

    IfSyntax ParseIf()
    {
        int start = _pos;
        var branches = new List<IfBranchSyntax>();
        _pos = start + 3;
        string cond = ParseHeader("if", out var condSpan);
        var body = ParseBlockBody("if");
        branches.Add(new IfBranchSyntax(TextSpan.FromBounds(start, EndOf(body, condSpan)), IfBranchKind.If, cond, condSpan, body));
        while (true)
        {
            int save = _pos;
            int p = SkipWhitespace(_pos);
            if (!MatchKeyword(p, "else"))
            {
                _pos = save;
                break;
            }
            int branchStart = p;
            int q = SkipWhitespace(p + 4);
            if (MatchKeyword(q, "if"))
            {
                _pos = q + 2;
                string c2 = ParseHeader("else if", out var span2);
                var b2 = ParseBlockBody("else if");
                branches.Add(new IfBranchSyntax(TextSpan.FromBounds(branchStart, EndOf(b2, span2, q + 2)), IfBranchKind.ElseIf, c2, span2, b2));
                continue;
            }
            _pos = p + 4;
            var b3 = ParseBlockBody("else");
            branches.Add(new IfBranchSyntax(TextSpan.FromBounds(branchStart, Math.Max(p + 4, b3.Span.End)), IfBranchKind.Else, null, new TextSpan(p + 4, 0), b3));
            break;
        }
        return new IfSyntax(TextSpan.FromBounds(start, branches[branches.Count - 1].Span.End), branches);
    }

    LoopSyntax ParseLoop(LoopKind kind, string keyword)
    {
        int start = _pos;
        _pos = start + 1 + keyword.Length;
        string header = ParseHeader(keyword, out var headerSpan);
        var body = ParseBlockBody(keyword);
        return new LoopSyntax(TextSpan.FromBounds(start, EndOf(body, headerSpan, start + 1 + keyword.Length)), kind, header, headerSpan, body);
    }

    SwitchSyntax ParseSwitch()
    {
        int start = _pos;
        _pos = start + 7;
        string expr = ParseHeader("switch", out var exprSpan);
        int afterHeader = _pos;
        int p = SkipWhitespace(_pos);
        var sections = new List<SwitchSectionSyntax>();
        if (PeekAt(p) != '{')
        {
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(afterHeader, 0), "@switch 之后应为 {");
            return new SwitchSyntax(TextSpan.FromBounds(start, afterHeader), expr, exprSpan, sections, false, false);
        }
        if (_nesting + CodeBodyWeight > MaxNesting)
        {
            TooDeep(p);
            return new SwitchSyntax(TextSpan.FromBounds(start, _pos), expr, exprSpan, sections, true, false);
        }
        _pos = p + 1;
        _nesting += CodeBodyWeight;
        _openTags.Add(null);
        var labels = new List<SwitchLabelSyntax>();
        var nodes = new List<SyntaxNode>();
        void FinishSection()
        {
            if (labels.Count == 0 && nodes.Count == 0) return;
            int s = labels.Count > 0 ? labels[0].Span.Start : nodes[0].Span.Start;
            int e = nodes.Count > 0 ? nodes[nodes.Count - 1].Span.End : labels[labels.Count - 1].Span.End;
            sections.Add(new SwitchSectionSyntax(TextSpan.FromBounds(s, e), labels.ToArray(), nodes.ToArray()));
            labels.Clear();
            nodes.Clear();
        }
        CodeStop stop;
        while (true)
        {
            int before = _pos;
            stop = ParseCodeNodes(nodes, true);
            if (stop != CodeStop.Label) break;
            if (nodes.Count > 0) FinishSection();
            labels.Add(ParseSwitchLabel());
            if (_pos == before) _pos++;
        }
        FinishSection();
        _openTags.RemoveAt(_openTags.Count - 1);
        _nesting -= CodeBodyWeight;
        bool closed = stop == CodeStop.Close;
        if (!closed)
            _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(p, 1), "@switch 的 { 缺少配对的 }");
        return new SwitchSyntax(TextSpan.FromBounds(start, _pos), expr, exprSpan, sections, true, closed);
    }

    /// <summary>解析控制流关键字之后的 <c>( … )</c>，返回括号内文本。</summary>
    string ParseHeader(string keyword, out TextSpan span)
    {
        int p = SkipWhitespace(_pos);
        if (PeekAt(p) == '(')
        {
            int close = FindClose(p);
            if (close < 0)
            {
                _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(p, 1), $"{keyword} 的 ( 缺少配对的 )");
                int le = LineEnd(p + 1);
                // 尽量在同一行的 { 前收住，让语句体还能被识别
                int brace = BraceInLine(p + 1);
                int end = brace >= 0 ? brace : le;
                span = TextSpan.FromBounds(p + 1, end);
                _pos = end;
                return _source.ToString(span);
            }
            span = TextSpan.FromBounds(p + 1, close);
            _pos = close + 1;
            return _source.ToString(span);
        }
        _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(_pos, 0), $"@{keyword} 之后应为 (");
        int b = BraceInLine(_pos);
        if (b >= 0)
        {
            span = TextSpan.FromBounds(p, TrimEndWhitespace(p, b));
            _pos = b;
            return _source.ToString(span);
        }
        span = new TextSpan(_pos, 0);
        return "";
    }

    CodeBodySyntax ParseBlockBody(string keyword)
    {
        int p = SkipWhitespace(_pos);
        if (PeekAt(p) == '{') return ParseCodeBody(p);
        _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(_pos, 0), $"{keyword} 的语句体应以 {{ 开始");
        return new CodeBodySyntax(new TextSpan(_pos, 0), Array.Empty<SyntaxNode>(), false, false);
    }

    static int EndOf(CodeBodySyntax body, TextSpan header, int fallback = 0) =>
        Math.Max(Math.Max(body.Span.End, header.End), fallback);

    // ================================================================ 工具

    SyntaxNode TooDeep(int start)
    {
        _diag.Error(DiagnosticCodes.UnbalancedDelimiter, new TextSpan(start, 0), $"嵌套超过 {MaxNesting} 层，其余内容未解析");
        _pos = _s.Length;
        string raw = _s.Substring(start);
        return new TextSyntax(TextSpan.FromBounds(start, _pos), raw, raw);
    }

    int FindClose(int open) => _matcher.FindClose(open);

    char Peek(int offset) => PeekAt(_pos + offset);
    char PeekAt(int at) => at >= 0 && at < _s.Length ? _s[at] : '\0';

    bool MatchKeyword(int at, string word) =>
        at + word.Length <= _s.Length
        && string.CompareOrdinal(_s, at, word, 0, word.Length) == 0
        && !IsIdentPart(PeekAt(at + word.Length));

    int SkipWhitespace(int at)
    {
        while (at < _s.Length && char.IsWhiteSpace(_s[at])) at++;
        return at;
    }

    int TrimEndWhitespace(int start, int end)
    {
        while (end > start && char.IsWhiteSpace(_s[end - 1])) end--;
        return end;
    }

    /// <summary>
    /// <paramref name="at"/> 所在行的行尾。缓存上一次的结果：同一行内的反复查询（如一行里成千上万个出错的 <c>@if x</c>）是 O(1)，
    /// 否则会退化为二次方。
    /// </summary>
    int LineEnd(int at)
    {
        if (at >= _s.Length) return _s.Length;
        if (at >= _lineEndFrom && at <= _lineEnd) return _lineEnd;
        int i = at;
        while (i < _s.Length && _s[i] != '\n' && _s[i] != '\r') i++;
        _lineEndFrom = at;
        _lineEnd = i;
        return i;
    }

    /// <summary><paramref name="at"/> 起到行尾之间第一个 { 的位置，没有返回 -1。与 <see cref="LineEnd"/> 一样缓存同一行内的查询。</summary>
    int BraceInLine(int at)
    {
        int lineEnd = LineEnd(at);
        if (at >= lineEnd) return -1;
        if (at >= _braceFrom && lineEnd == _braceLineEnd && (_brace < 0 || at <= _brace)) return _brace;
        _braceFrom = at;
        _braceLineEnd = lineEnd;
        _brace = _s.IndexOf('{', at, lineEnd - at);
        return _brace;
    }

    int SkipIdentifier(int at)
    {
        while (at < _s.Length && IsIdentPart(_s[at])) at++;
        return at;
    }

    string ReadIdentifierAt(int at) => _s.Substring(at, SkipIdentifier(at) - at);

    int SkipTagName(int at)
    {
        while (at < _s.Length && IsTagNamePart(_s[at])) at++;
        return at;
    }

    internal static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);
    internal static bool IsIdentPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    static bool IsTagNameStart(char c) => c == '_' || char.IsLetter(c);
    static bool IsTagNamePart(char c) => c == '_' || c == '.' || c == '-' || char.IsLetterOrDigit(c);

    static bool IsWhitespace(string s) => IsWhitespace(s, 0, s.Length);

    static bool IsWhitespace(string s, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (!char.IsWhiteSpace(s[i])) return false;
        return true;
    }
}

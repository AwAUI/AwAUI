using System.Collections.Generic;
using System.Text;
using AwAUI.Markup.Compiler.Parsing;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;

namespace AwAUI.Markup.Compiler.Formatting;

/// <summary>
/// <c>awaui format</c>：语法树 → 规范的 <c>.awaui</c> 文本。只格式化没有语法错误的输入，并保证格式化结果重新解析、
/// 规范化空白之后与原树等价：
/// <list type="bullet">
/// <item>只在「两侧都不是带空白边缘的文本」的位置换行 —— 按 JSX 空白规则，这样的换行不改变界面；</item>
/// <item>只含文本、表达式与行内元素的内容写在一行；只含元素/控制流的内容每项一行；</item>
/// <item>C# 按花括号深度重新缩进，但多行字符串与块注释内部原样保留；条件、表达式、属性值、样式原样保留；</item>
/// <item>邻接会改变解析结果的隐式表达式（前面紧邻字母数字、后面紧跟 <c>. ( [</c> 等）改写为显式 <c>@( )</c>。</item>
/// </list>
/// </summary>
public static class MarkupFormatter
{
    const string IndentUnit = "    ";
    const int MaxIndent = 64;

    /// <summary>
    /// 格式化一个文件。有语法错误时原样返回源文本：错误恢复得到的树不保留被跳过的内容
    /// （多余的结束标签、开始标签内的注释等），格式化会丢失或改写用户的文本。
    /// </summary>
    public static string Format(MarkupSyntaxTree tree) => TryFormat(tree, out string formatted) ? formatted : tree.Source.Text;

    /// <summary>
    /// 格式化一个文件；有语法错误时返回 false，<paramref name="formatted"/> 为未改动的源文本。
    /// </summary>
    public static bool TryFormat(MarkupSyntaxTree tree, out string formatted)
    {
        foreach (var d in tree.Diagnostics)
        {
            if (d.IsError)
            {
                formatted = tree.Source.Text;
                return false;
            }
        }
        formatted = Format(tree.Root);
        return true;
    }

    /// <summary>不检查诊断，直接格式化语法树（用于测试错误恢复得到的树也不会抛异常）。</summary>
    internal static string Format(DocumentSyntax document)
    {
        var normalized = WhitespaceNormalizer.Normalize(document);
        var w = new Writer();
        WriteTopLevel(w, normalized.Items);
        return w.ToString();
    }

    // ================================================================ 布局

    enum Layout
    {
        /// <summary>全部写在一行。</summary>
        Inline,
        /// <summary>每个子节点一行。</summary>
        Block,
        /// <summary>文本与行内节点成行，块节点在允许处另起一行。</summary>
        Mixed,
    }

    static void WriteTopLevel(Writer w, IReadOnlyList<SyntaxNode> items)
    {
        var layout = HasText(items) ? Layout.Mixed : Layout.Block;
        for (int i = 0; i < items.Count; i++)
        {
            bool sameLine = false;
            if (i > 0)
            {
                var prev = items[i - 1];
                if (ShouldBreak(layout, prev, items[i]))
                {
                    w.NewLine();
                    if (Category(prev) != Category(items[i]) || Category(items[i]) == 2) w.NewLine();
                }
                else
                {
                    sameLine = true;
                }
            }
            WriteNode(w, items, i, sameLine);
        }
        // 文件末尾的换行会让以空白结尾的文本不再是最后一行（行尾空白将被去掉），此时不加
        if (items.Count > 0 && CanBreakAfter(items[items.Count - 1])) w.NewLine();
    }

    static bool ShouldBreak(Layout layout, SyntaxNode prev, SyntaxNode next) => layout switch
    {
        Layout.Block => true,
        Layout.Mixed => CanBreak(prev, next) && (IsBlockNode(prev) || IsBlockNode(next)),
        _ => false,
    };

    /// <summary>顶层分组：0 指令，1 标记，2 @code / style（各自用空行隔开）。</summary>
    static int Category(SyntaxNode n) => n switch
    {
        DirectiveSyntax => 0,
        CodeBlockSyntax => 2,
        StyleBlockSyntax => 2,
        _ => 1,
    };

    static void WriteChildren(Writer w, IReadOnlyList<SyntaxNode> children, Layout layout)
    {
        for (int i = 0; i < children.Count; i++)
        {
            bool sameLine = false;
            if (i > 0)
            {
                if (ShouldBreak(layout, children[i - 1], children[i])) w.NewLine();
                else sameLine = true;
            }
            WriteNode(w, children, i, sameLine);
        }
    }

    static bool HasText(IReadOnlyList<SyntaxNode> nodes)
    {
        foreach (var n in nodes)
            if (n is TextSyntax || n is ExpressionSyntax) return true;
        return false;
    }

    /// <summary>行内节点：文本、表达式，以及内容全部为行内节点的元素与片段。</summary>
    static bool IsInlineNode(SyntaxNode n) => n switch
    {
        TextSyntax => true,
        ExpressionSyntax => true,
        ElementSyntax e => e.IsSelfClosing || IsInlineContent(e.Children),
        FragmentSyntax f => IsInlineContent(f.Children),
        _ => false,
    };

    static bool IsInlineContent(IReadOnlyList<SyntaxNode> children)
    {
        if (children.Count == 0) return true;
        if (!HasText(children)) return false;
        foreach (var c in children)
            if (!IsInlineNode(c)) return false;
        return true;
    }

    static bool IsBlockNode(SyntaxNode n) => !IsInlineNode(n);

    static bool CanBreak(SyntaxNode prev, SyntaxNode next) => CanBreakAfter(prev) && CanBreakBefore(next);

    /// <summary>在节点之后换行不改变语义：节点不是以空白结尾的文本。</summary>
    static bool CanBreakAfter(SyntaxNode n) => n is not TextSyntax t || t.Text.Length == 0 || !IsSpace(t.Text[t.Text.Length - 1]);

    static bool CanBreakBefore(SyntaxNode n) => n is not TextSyntax t || t.Text.Length == 0 || !IsSpace(t.Text[0]);

    static bool IsSpace(char c) => c == ' ' || c == '\t';

    static bool IsLineBreak(char c) => c == '\n' || c == '\r';

    // ================================================================ 节点

    static void WriteNode(Writer w, IReadOnlyList<SyntaxNode> siblings, int index, bool sameLineAsPrevious)
    {
        var node = siblings[index];
        switch (node)
        {
            case TextSyntax t:
                // 文本开头的单个 @ 只因前面紧邻字母数字（邮箱式）才是字面量；换了邻居就要转义成 @@
                if (t.Text.Length > 0 && t.Text[0] == '@' && !t.Text.StartsWith("@@", System.StringComparison.Ordinal)
                    && !(sameLineAsPrevious && w.EmailAdjacent))
                    w.Write("@" + t.Text);
                else
                    w.Write(t.Text);
                break;
            case ExpressionSyntax e:
            {
                // 后面紧跟的文本（文本与表达式之间从不换行）可能延长隐式表达式；前面紧邻字母数字会被当作邮箱
                bool nextSticks = index + 1 < siblings.Count
                    && siblings[index + 1] is TextSyntax nt && nt.Text.Length > 0 && ExtendsImplicit(nt.Text);
                bool forceExplicit = (sameLineAsPrevious && w.EmailAdjacent) || nextSticks;
                WriteExpression(w, e, forceExplicit);
                break;
            }
            case ElementSyntax el:
                WriteElement(w, el);
                break;
            case FragmentSyntax f:
                w.Write("<>");
                WriteContent(w, f.Children);
                w.Write("</>");
                break;
            case CommentSyntax c:
                w.Write(c.IsHtml ? "<!--" + c.Content + (c.IsClosed ? "-->" : "") : "@*" + c.Content + (c.IsClosed ? "*@" : ""));
                break;
            case DirectiveSyntax d:
                w.Write("@" + d.Name + (d.Value.Length > 0 ? " " + d.Value : ""));
                break;
            case CodeBlockSyntax cb:
                WriteCodeBlock(w, cb);
                break;
            case StyleBlockSyntax s when s.IsSvg:
                // 内联 svg 中的 <style>：属性（type、media……）属于 SVG 文档，原样保留
                w.Write("<style");
                foreach (var a in s.Attributes)
                {
                    w.Write(" ");
                    WriteAttribute(w, a);
                }
                w.Write(">");
                w.WriteRaw(s.Content);
                w.Write("</style>");
                break;
            case StyleBlockSyntax s:
                w.Write(s.IsGlobal ? "<style global>" : "<style>");
                w.WriteRaw(s.Content);
                w.Write("</style>");
                break;
            case StatementBlockSyntax sb:
                w.Write("@");
                WriteBody(w, sb.Body);
                break;
            case IfSyntax i:
                WriteIf(w, i);
                break;
            case LoopSyntax l:
                w.Write("@" + l.Keyword + " (" + l.Header + ")");
                w.NewLine();
                WriteBody(w, l.Body);
                break;
            case SwitchSyntax s:
                WriteSwitch(w, s);
                break;
            case LineTextSyntax lt:
                w.Write("@:");
                for (int k = 0; k < lt.Children.Count; k++)
                    WriteNode(w, lt.Children, k, true);
                break;
            case CSharpCodeSyntax cs:
                w.Write(cs.Code.Trim());
                break;
        }
    }

    /// <summary>文本开头若能接在隐式表达式后面被一并吞掉（标识符字符、. ?. ( [ !）。</summary>
    static bool ExtendsImplicit(string text)
    {
        char c = text[0];
        return c == '.' || c == '(' || c == '[' || c == '!' || c == '?' || MarkupParser.IsIdentPart(c);
    }

    static void WriteExpression(Writer w, ExpressionSyntax e, bool forceExplicit)
    {
        // 与 Razor 指令同名的隐式表达式若落在行首、后接换行会被当作指令，统一写成显式形式
        if (e.IsExplicit || forceExplicit || MarkupParser.IsUnsupportedDirectiveName(e.Code)) w.Write("@(" + e.Code + ")");
        else w.WriteImplicitExpression("@" + e.Code);
    }

    static void WriteElement(Writer w, ElementSyntax e)
    {
        w.Write("<" + e.Name);
        if (e.TypeArguments != null) w.Write("<" + e.TypeArguments + ">");
        foreach (var a in e.Attributes)
        {
            w.Write(" ");
            WriteAttribute(w, a);
        }
        if (e.IsSelfClosing)
        {
            w.Write(" />");
            return;
        }
        w.Write(">");
        WriteContent(w, e.Children);
        w.Write("</" + e.Name + (e.TypeArguments != null ? "<" + e.TypeArguments + ">" : "") + ">");
    }

    /// <summary>写元素或片段的内容（开始标签之后、结束标签之前）。</summary>
    static void WriteContent(Writer w, IReadOnlyList<SyntaxNode> children)
    {
        if (children.Count == 0) return;
        if (IsInlineContent(children))
        {
            WriteChildren(w, children, Layout.Inline);
            return;
        }
        var layout = HasText(children) ? Layout.Mixed : Layout.Block;
        bool breakStart = CanBreakBefore(children[0]);
        bool breakEnd = CanBreakAfter(children[children.Count - 1]);
        w.Indent++;
        if (breakStart) w.NewLine();
        WriteChildren(w, children, layout);
        w.Indent--;
        if (breakEnd) w.NewLine();
    }

    static void WriteAttribute(Writer w, AttributeSyntax a)
    {
        w.Write(a.FullName);
        var v = a.Value;
        if (v == null) return;
        w.Write("=");
        if (v.Quote == '\0')
        {
            if (v.SingleExpression is { } expr)
            {
                WriteExpression(w, expr, false);
                return;
            }
            string literal = v.LiteralText ?? "";
            char q = literal.IndexOf('"') >= 0 && literal.IndexOf('\'') < 0 ? '\'' : '"';
            if (literal.IndexOf(q) >= 0) literal = literal.Replace("\"", "&quot;");
            w.Write(q + literal + q);
            return;
        }
        var sb = new StringBuilder();
        sb.Append(v.Quote);
        foreach (var p in v.Parts)
        {
            if (p is AttributeLiteralSyntax l) sb.Append(l.Text);
            else if (p is ExpressionSyntax e) sb.Append(e.IsExplicit ? "@(" + e.Code + ")" : "@" + e.Code);
        }
        sb.Append(v.Quote);
        w.Write(sb.ToString());
    }

    static void WriteIf(Writer w, IfSyntax i)
    {
        for (int k = 0; k < i.Branches.Count; k++)
        {
            var b = i.Branches[k];
            if (k > 0) w.NewLine();
            switch (b.BranchKind)
            {
                case IfBranchKind.If: w.Write("@if (" + b.Condition + ")"); break;
                case IfBranchKind.ElseIf: w.Write("else if (" + b.Condition + ")"); break;
                default: w.Write("else"); break;
            }
            w.NewLine();
            WriteBody(w, b.Body);
        }
    }

    static void WriteSwitch(Writer w, SwitchSyntax s)
    {
        w.Write("@switch (" + s.Expression + ")");
        w.NewLine();
        w.Write("{");
        int baseIndent = w.Indent;
        foreach (var sec in s.Sections)
        {
            w.Indent = baseIndent + 1;
            foreach (var l in sec.Labels)
            {
                w.NewLine();
                w.Write(l.Text.Trim());
            }
            WriteCodeNodes(w, sec.Nodes, sec.Labels.Count > 0 ? baseIndent + 2 : baseIndent + 1);
        }
        w.Indent = baseIndent;
        w.NewLine();
        w.Write("}");
    }

    static void WriteBody(Writer w, CodeBodySyntax body)
    {
        w.Write("{");
        int baseIndent = w.Indent;
        WriteCodeNodes(w, body.Nodes, baseIndent + 1);
        w.Indent = baseIndent;
        w.NewLine();
        w.Write("}");
    }

    /// <summary>代码模式的节点：C# 片段按花括号深度缩进，标记节点放在当前深度、各占一行。</summary>
    static void WriteCodeNodes(Writer w, IReadOnlyList<SyntaxNode> nodes, int indent)
    {
        var state = new CodeIndenter();
        foreach (var n in nodes)
        {
            if (n is CSharpCodeSyntax cs)
            {
                state.WriteCode(w, cs.Code, indent);
                continue;
            }
            w.Indent = indent + state.MarkupLevel;
            w.NewLine();
            WriteNode(w, new[] { n }, 0, false);
            state.AfterStatement();
        }
        w.Indent = indent;
    }

    static void WriteCodeBlock(Writer w, CodeBlockSyntax cb)
    {
        w.Write("@code {");
        int baseIndent = w.Indent;
        new CodeIndenter().WriteCode(w, cb.Code, baseIndent + 1);
        w.Indent = baseIndent;
        w.NewLine();
        if (cb.IsClosed) w.Write("}");
    }

    // ================================================================ C# 重新缩进

    /// <summary>
    /// 按花括号深度重新缩进 C#：以 } 开头的行先减一级；上一行没有以 <c>; { } : ]</c> 结束时视为续行再加一级。
    /// 起止都在多行字符串或块注释内部的行首、行尾保持原样。
    /// </summary>
    sealed class CodeIndenter
    {
        char _prevEnd = '{';
        bool _pendingBlank;
        bool _wroteAny;

        public int Depth { get; private set; }

        /// <summary>标记节点所在的缩进级别：花括号深度，续行（如无花括号的 if 之后）再加一级。</summary>
        public int MarkupLevel => Depth + (IsTerminator(_prevEnd) ? 0 : 1);

        static bool IsTerminator(char c) => c == ';' || c == '{' || c == '}' || c == ':' || c == ']';

        public void AfterStatement()
        {
            _prevEnd = ';';
            _pendingBlank = false;
            _wroteAny = true;
        }

        public void WriteCode(Writer w, string code, int indent)
        {
            var cls = Classify(code);
            int lineStart = 0;
            string prevTerminator = "";
            while (lineStart <= code.Length)
            {
                // 行结束符：\r\n、\n 或单独的 \r（与扫描器、SourceText 一致）
                int lineEnd = lineStart;
                while (lineEnd < code.Length && code[lineEnd] != '\n' && code[lineEnd] != '\r') lineEnd++;
                int termLen = lineEnd >= code.Length ? 0 : code[lineEnd] == '\r' && lineEnd + 1 < code.Length && code[lineEnd + 1] == '\n' ? 2 : 1;
                bool leadProtected = lineStart > 0 && cls[lineStart - 1] == CharClass.Protected;
                bool trailProtected = termLen > 0 && cls[lineEnd] == CharClass.Protected;
                string raw = code.Substring(lineStart, lineEnd - lineStart);
                // 片段的第一行接在前一个节点所在行之后，它为空不代表空行
                if (!(lineStart == 0 && raw.Trim().Length == 0))
                    WriteLine(w, raw, code, cls, lineStart, lineEnd, leadProtected, trailProtected, indent, prevTerminator);
                if (termLen == 0) break;
                prevTerminator = code.Substring(lineEnd, termLen);
                lineStart = lineEnd + termLen;
            }
        }

        void WriteLine(Writer w, string raw, string code, CharClass[] cls, int start, int end, bool leadProtected, bool trailProtected, int indent, string prevTerminator)
        {
            if (leadProtected)
            {
                // 多行字符串或块注释的内部：连同原来的换行符原样写出
                w.WriteRaw(prevTerminator);
                w.WriteRaw(trailProtected ? raw : raw.TrimEnd());
                _wroteAny = true;
                UpdateState(code, cls, start, end);
                return;
            }
            string text = trailProtected ? raw.TrimStart() : raw.Trim();
            if (text.Length == 0)
            {
                if (_wroteAny) _pendingBlank = true;
                return;
            }
            int leadingClose = 0;
            for (int i = start; i < end; i++)
            {
                if (cls[i] == CharClass.Space) continue;
                if (cls[i] == CharClass.Code && code[i] == '}') leadingClose = 1;
                break;
            }
            bool continuation = text[0] != '#' && leadingClose == 0 && text[0] != '{' && !IsTerminator(_prevEnd);
            int level = Depth - leadingClose + (continuation ? 1 : 0);
            if (level < 0) level = 0;
            if (start == 0 && text[0] == '#')
            {
                // 片段从行中间开始且以 # 开头：原文中它不是预处理指令，不能挪到行首
                w.Write(" " + text);
                _wroteAny = true;
                UpdateState(code, cls, start, end);
                return;
            }
            if (_pendingBlank) w.NewLine();
            _pendingBlank = false;
            w.Indent = indent + level;
            w.NewLine();
            w.Write(text);
            _wroteAny = true;
            UpdateState(code, cls, start, end);
        }

        void UpdateState(string code, CharClass[] cls, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var k = cls[i];
                if (k == CharClass.Code)
                {
                    char c = code[i];
                    if (c == '{') Depth++;
                    else if (c == '}' && Depth > 0) Depth--;
                    _prevEnd = c;
                }
                else if (k == CharClass.Literal)
                {
                    _prevEnd = '"';
                }
            }
        }
    }

    enum CharClass : byte
    {
        Space,
        Code,
        /// <summary>单行的字符串或字符字面量。</summary>
        Literal,
        /// <summary>注释（不影响语句结束判断）。</summary>
        Comment,
        /// <summary>可跨行的原子（字符串、块注释）内部。</summary>
        Protected,
    }

    static readonly char[] LineBreaks = { '\n', '\r' };

    static CharClass[] Classify(string code)
    {
        var cls = new CharClass[code.Length];
        // 片段从行中间截出：第一个换行之前的 # 不是预处理指令（只算一次，避免对每个 # 向前回扫）
        int firstBreak = code.IndexOfAny(LineBreaks);
        if (firstBreak < 0) firstBreak = code.Length;
        int i = 0;
        while (i < code.Length)
        {
            int end = code[i] == '#' && i < firstBreak ? i : CSharpScanner.SkipAtom(code, i);
            if (end > i)
            {
                bool comment = CSharpScanner.IsTriviaAtom(code, i);
                bool multiLine = code.IndexOfAny(LineBreaks, i, end - i) >= 0;
                var k = multiLine ? CharClass.Protected : comment ? CharClass.Comment : CharClass.Literal;
                for (int j = i; j < end; j++) cls[j] = k;
                if (multiLine && !comment)
                {
                    // 跨行字符串：首尾字符计为字面量，供语句结束判断
                    cls[end - 1] = IsLineBreak(code[end - 1]) ? CharClass.Protected : CharClass.Literal;
                }
                else if (multiLine)
                {
                    cls[end - 1] = IsLineBreak(code[end - 1]) ? CharClass.Protected : CharClass.Comment;
                }
                i = end;
                continue;
            }
            cls[i] = char.IsWhiteSpace(code[i]) ? CharClass.Space : CharClass.Code;
            i++;
        }
        return cls;
    }

    // ================================================================ 输出

    sealed class Writer
    {
        readonly StringBuilder _sb = new();
        bool _atLineStart = true;

        public int Indent { get; set; }

        bool _endsWithImplicit;

        /// <summary>
        /// 接着写 <c>@标识符</c> 会被当作邮箱式文本：上一个字符是字母数字，且不是隐式表达式的结尾
        /// （与解析器的邮箱启发式一致）。
        /// </summary>
        public bool EmailAdjacent => !_atLineStart && !_endsWithImplicit && _sb.Length > 0 && char.IsLetterOrDigit(_sb[_sb.Length - 1]);

        public void WriteImplicitExpression(string s)
        {
            Write(s);
            _endsWithImplicit = true;
        }

        public void Write(string s)
        {
            if (s.Length == 0) return;
            _endsWithImplicit = false;
            if (_atLineStart)
            {
                // 缩进封顶：上万层花括号的合法 C# 也只产生线性大小的输出（行首空白不影响语义）
                int levels = Indent < MaxIndent ? Indent : MaxIndent;
                for (int i = 0; i < levels; i++) _sb.Append(IndentUnit);
                _atLineStart = false;
            }
            _sb.Append(s);
        }

        /// <summary>原样写出（不加缩进），用于多行字符串内部与样式内容。</summary>
        public void WriteRaw(string s)
        {
            if (s.Length == 0) return;
            _endsWithImplicit = false;
            _sb.Append(s);
            _atLineStart = IsLineBreak(s[s.Length - 1]);
        }

        public void NewLine()
        {
            _sb.Append('\n');
            _atLineStart = true;
            _endsWithImplicit = false;
        }

        public override string ToString() => _sb.ToString();
    }
}

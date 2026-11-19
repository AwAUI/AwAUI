using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Css.Syntax;

/// <summary>CSS Syntax 3 §4 定义的全部记号类型。</summary>
public enum CssTokenType : byte
{
    Ident,
    Function,
    AtKeyword,
    Hash,
    String,
    BadString,
    Url,
    BadUrl,
    Delim,
    Number,
    Percentage,
    Dimension,
    Whitespace,
    CDO,
    CDC,
    Colon,
    Semicolon,
    Comma,
    LeftBracket,
    RightBracket,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    EndOfFile,
}

/// <summary>数值记号的类型标志（规范中的 type flag）。</summary>
public enum CssNumberType : byte
{
    /// <summary>整数：表示中没有小数点与指数。</summary>
    Integer,
    /// <summary>一般数值。</summary>
    Number,
}

/// <summary>hash 记号的类型标志。</summary>
public enum CssHashType : byte
{
    /// <summary>不构成合法标识符（如 <c>#123</c>），不能用作 ID 选择器。</summary>
    Unrestricted,
    /// <summary>构成合法标识符，可用作 ID 选择器。</summary>
    Id,
}

/// <summary>组件值的种类。</summary>
public enum CssComponentKind : byte
{
    /// <summary>保留记号。</summary>
    Token,
    /// <summary>函数。</summary>
    Function,
    /// <summary>简单块：<c>{}</c>、<c>[]</c> 或 <c>()</c>。</summary>
    SimpleBlock,
}

/// <summary>
/// 组件值（CSS Syntax 3 §5）：保留记号、函数或简单块。解析器保留全部组件值，供后续值解析、DevTools 与诊断使用。
/// </summary>
public abstract class CssComponentValue
{
    private protected CssComponentValue(SourceRange range) => Range = range;

    /// <summary>在原始文本中的区间。</summary>
    public SourceRange Range { get; }

    public abstract CssComponentKind Kind { get; }

    /// <summary>把该组件值序列化为 CSS 文本。</summary>
    public abstract void WriteCss(StringBuilder sb);

    public string ToCss()
    {
        var sb = new StringBuilder();
        WriteCss(sb);
        return sb.ToString();
    }

    public override string ToString() => ToCss();

    /// <summary>
    /// CSS Syntax 3「解析组件值列表」：把文本解析为组件值（值层、内联值与 DevTools 编辑使用）。
    /// </summary>
    public static IReadOnlyList<CssComponentValue> ParseList(string text, List<CssDiagnostic>? diagnostics = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return new CssParser(new CssSourceText(text), diagnostics ?? new List<CssDiagnostic>()).ConsumeAllComponentValues();
    }

    /// <summary>序列化组件值列表。相邻记号拼接后会被重新分词为不同记号时插入 <c>/**/</c>（CSS Syntax 3 §9）。</summary>
    public static string ToCss(IReadOnlyList<CssComponentValue> values)
    {
        var sb = new StringBuilder();
        WriteList(sb, values);
        return sb.ToString();
    }

    /// <summary>写出组件值列表，按 CSS Syntax 3 §9 的表在需要处插入空注释。</summary>
    internal static void WriteList(StringBuilder sb, IReadOnlyList<CssComponentValue> values)
    {
        CssComponentValue? prev = null;
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (prev is CssToken left && NeedsSeparator(left, v)) sb.Append("/**/");
            v.WriteCss(sb);
            prev = v;
        }
    }

    /// <summary>CSS Syntax 3 §9「序列化」表：<paramref name="left"/> 的文本之后紧跟 <paramref name="right"/> 时是否需要 <c>/**/</c>。</summary>
    static bool NeedsSeparator(CssToken left, CssComponentValue right)
    {
        // 右侧以哪种记号开头：函数以 function 记号开头，() 块以 '(' 开头；其他块的开头字符不在表中
        CssTokenType rt;
        char rd = '\0';
        switch (right)
        {
            case CssToken t: rt = t.Type; rd = t.Delim; break;
            case CssFunction: rt = CssTokenType.Function; break;
            case CssSimpleBlock { Open: '(' }: rt = CssTokenType.LeftParen; break;
            default: return false;
        }
        bool identLike = rt is CssTokenType.Ident or CssTokenType.Function or CssTokenType.Url or CssTokenType.BadUrl;
        bool numeric = rt is CssTokenType.Number or CssTokenType.Percentage or CssTokenType.Dimension;
        bool minus = rt == CssTokenType.Delim && rd == '-';
        switch (left.Type)
        {
            case CssTokenType.Ident:
                return identLike || minus || numeric || rt is CssTokenType.CDC or CssTokenType.LeftParen;
            case CssTokenType.AtKeyword:
            case CssTokenType.Hash:
            case CssTokenType.Dimension:
                return identLike || minus || numeric || rt == CssTokenType.CDC;
            case CssTokenType.Number:
                return identLike || numeric || (rt == CssTokenType.Delim && rd == '%');
            case CssTokenType.Delim:
                switch (left.Delim)
                {
                    case '#':
                    case '-': return identLike || minus || numeric;
                    case '@': return identLike || minus;
                    case '.':
                    case '+': return numeric;
                    case '/': return rt == CssTokenType.Delim && rd == '*';
                }
                return false;
            default:
                return false;
        }
    }
}

/// <summary>一个 CSS 记号。作为组件值时即「保留记号」。</summary>
public sealed class CssToken : CssComponentValue
{
    internal CssToken(CssTokenType type, SourceRange range) : base(range)
    {
        Type = type;
        Value = string.Empty;
        Unit = string.Empty;
    }

    public override CssComponentKind Kind => CssComponentKind.Token;

    /// <summary>构造一个不对应源文本的 dimension 记号（<c>env()</c> 替换等）。</summary>
    internal static CssToken CreateDimension(double value, string unit)
        => new(CssTokenType.Dimension, default) { Number = value, Unit = unit, NumberType = CssNumberType.Number };

    public CssTokenType Type { get; }

    /// <summary>
    /// ident、function（不含括号）、at-keyword（不含 @）、hash（不含 #）、string、url 记号的值（已处理转义）。
    /// 其他记号为空串。
    /// </summary>
    public string Value { get; internal set; }

    /// <summary>delim 记号的字符。</summary>
    public char Delim { get; internal set; }

    /// <summary>number / percentage / dimension 记号的数值（精确按规范的「把字符串转换为数」）。</summary>
    public double Number { get; internal set; }

    /// <summary>数值记号的类型标志。</summary>
    public CssNumberType NumberType { get; internal set; }

    /// <summary>数值记号的表示是否以 <c>+</c> 或 <c>-</c> 开头（An+B 微语法需要）。</summary>
    public bool HasSign { get; internal set; }

    /// <summary>dimension 记号的单位（已处理转义，保留原始大小写）。</summary>
    public string Unit { get; internal set; }

    /// <summary>hash 记号的类型标志。</summary>
    public CssHashType HashType { get; internal set; }

    /// <summary>是否为指定字符的 delim 记号。</summary>
    public bool IsDelim(char c) => Type == CssTokenType.Delim && Delim == c;

    /// <summary>是否为与 <paramref name="name"/>（小写 ASCII）大小写不敏感匹配的 ident 记号。</summary>
    public bool IsIdent(string name) => Type == CssTokenType.Ident && CssSerializer.EqualsIgnoreAsciiCase(Value, name);

    public override void WriteCss(StringBuilder sb)
    {
        switch (Type)
        {
            case CssTokenType.Ident: CssSerializer.WriteIdent(sb, Value); break;
            case CssTokenType.Function: CssSerializer.WriteIdent(sb, Value); sb.Append('('); break;
            case CssTokenType.AtKeyword: sb.Append('@'); CssSerializer.WriteIdent(sb, Value); break;
            case CssTokenType.Hash:
                sb.Append('#');
                if (HashType == CssHashType.Id) CssSerializer.WriteIdent(sb, Value);
                else CssSerializer.WriteName(sb, Value);
                break;
            case CssTokenType.String: CssSerializer.WriteString(sb, Value); break;
            case CssTokenType.BadString: sb.Append('"'); break;
            case CssTokenType.Url: sb.Append("url("); CssSerializer.WriteUrlBody(sb, Value); sb.Append(')'); break;
            case CssTokenType.BadUrl: sb.Append("url()"); break;
            case CssTokenType.Delim: sb.Append(Delim); break;
            case CssTokenType.Number: WriteNumber(sb); break;
            case CssTokenType.Percentage: WriteNumber(sb); sb.Append('%'); break;
            case CssTokenType.Dimension: WriteNumber(sb); CssSerializer.WriteUnit(sb, Unit); break;
            case CssTokenType.Whitespace: sb.Append(' '); break;
            case CssTokenType.CDO: sb.Append("<!--"); break;
            case CssTokenType.CDC: sb.Append("-->"); break;
            case CssTokenType.Colon: sb.Append(':'); break;
            case CssTokenType.Semicolon: sb.Append(';'); break;
            case CssTokenType.Comma: sb.Append(','); break;
            case CssTokenType.LeftBracket: sb.Append('['); break;
            case CssTokenType.RightBracket: sb.Append(']'); break;
            case CssTokenType.LeftParen: sb.Append('('); break;
            case CssTokenType.RightParen: sb.Append(')'); break;
            case CssTokenType.LeftBrace: sb.Append('{'); break;
            case CssTokenType.RightBrace: sb.Append('}'); break;
            case CssTokenType.EndOfFile: break;
        }
    }

    void WriteNumber(StringBuilder sb)
    {
        if (HasSign && Number >= 0 && BitConverter.DoubleToInt64Bits(Number) >= 0) sb.Append('+');
        CssSerializer.WriteNumber(sb, Number, NumberType);
    }

    /// <summary>调试用的结构化描述，如 <c>dimension(12 px, integer)</c>。</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        return Type switch
        {
            CssTokenType.Ident or CssTokenType.Function or CssTokenType.AtKeyword or CssTokenType.String or CssTokenType.Url
                => $"{Type}({Value})",
            CssTokenType.Hash => $"Hash({Value}, {HashType})",
            CssTokenType.Delim => $"Delim({Delim})",
            CssTokenType.Number => string.Format(inv, "Number({0}, {1})", Number, NumberType),
            CssTokenType.Percentage => string.Format(inv, "Percentage({0}, {1})", Number, NumberType),
            CssTokenType.Dimension => string.Format(inv, "Dimension({0} {1}, {2})", Number, Unit, NumberType),
            _ => Type.ToString(),
        };
    }
}

/// <summary>函数组件值，如 <c>rgb(0 0 0)</c>、<c>:nth-child(2n+1 of .a)</c>。</summary>
public sealed class CssFunction : CssComponentValue
{
    internal CssFunction(string name, IReadOnlyList<CssComponentValue> arguments, SourceRange range, bool closed) : base(range)
    {
        Name = name;
        Arguments = arguments;
        IsClosed = closed;
    }

    public override CssComponentKind Kind => CssComponentKind.Function;

    /// <summary>函数名（已处理转义，保留原始大小写）。</summary>
    public string Name { get; }

    /// <summary>参数组件值（含空白与逗号）。</summary>
    public IReadOnlyList<CssComponentValue> Arguments { get; }

    /// <summary>是否遇到了右括号（为假表示在文件结尾处未闭合）。</summary>
    public bool IsClosed { get; }

    public override void WriteCss(StringBuilder sb)
    {
        CssSerializer.WriteIdent(sb, Name);
        sb.Append('(');
        WriteList(sb, Arguments);
        sb.Append(')');
    }
}

/// <summary>简单块组件值：<c>{…}</c>、<c>[…]</c> 或 <c>(…)</c>。</summary>
public sealed class CssSimpleBlock : CssComponentValue
{
    internal CssSimpleBlock(char open, IReadOnlyList<CssComponentValue> values, SourceRange range, bool closed) : base(range)
    {
        Open = open;
        Values = values;
        IsClosed = closed;
    }

    public override CssComponentKind Kind => CssComponentKind.SimpleBlock;

    /// <summary>开括号字符：<c>{</c>、<c>[</c> 或 <c>(</c>。</summary>
    public char Open { get; }

    /// <summary>对应的闭括号字符。</summary>
    public char Close => Open switch { '{' => '}', '[' => ']', _ => ')' };

    public IReadOnlyList<CssComponentValue> Values { get; }

    /// <summary>是否遇到了闭括号。</summary>
    public bool IsClosed { get; }

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append(Open);
        WriteList(sb, Values);
        sb.Append(Close);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Css.Syntax;

/// <summary>
/// 符合 CSS Syntax Module Level 3 §4 的分词器。输入预处理（BOM、CRLF、FF、NUL）在 <see cref="CssSourceText"/> 中完成，
/// 每个记号都带原始文本中的区间（偏移、行、列）。注释不产生记号。
/// </summary>
public sealed class CssTokenizer
{
    const char Eof = '\0'; // 预处理后文本中不存在 NUL，可用作 EOF 哨兵

    readonly CssSourceText _src;
    readonly string _s;
    readonly List<CssDiagnostic>? _diags;
    readonly StringBuilder _sb = new();
    readonly string?[] _names = new string?[NameCacheSize];
    int _pos;

    const int NameCacheSize = 1024; // 2 的幂

    /// <summary>为 <paramref name="text"/> 创建分词器；解析错误写入 <paramref name="diagnostics"/>（可为 null）。</summary>
    public CssTokenizer(string text, List<CssDiagnostic>? diagnostics = null)
        : this(new CssSourceText(text), diagnostics)
    {
    }

    internal CssTokenizer(CssSourceText source, List<CssDiagnostic>? diagnostics)
    {
        _src = source;
        _s = source.Text;
        _diags = diagnostics;
    }

    /// <summary>把整个文本切分为记号列表，最后一个记号总是 <see cref="CssTokenType.EndOfFile"/>。</summary>
    public static List<CssToken> Tokenize(string text, List<CssDiagnostic>? diagnostics = null)
        => new CssTokenizer(text, diagnostics).ReadAll();

    internal List<CssToken> ReadAll()
    {
        var list = new List<CssToken>(Math.Max(16, _s.Length / 4));
        while (true)
        {
            var t = Next();
            list.Add(t);
            if (t.Type == CssTokenType.EndOfFile) return list;
        }
    }

    /// <summary>读取下一个记号；到达结尾后持续返回 EOF 记号。</summary>
    public CssToken Next()
    {
        ConsumeComments();
        int start = _pos;
        if (_pos >= _s.Length) return Make(CssTokenType.EndOfFile, start);
        char c = _s[_pos];
        switch (c)
        {
            case ' ':
            case '\t':
            case '\n':
                _pos++;
                while (_pos < _s.Length && IsWhitespace(_s[_pos])) _pos++;
                return Make(CssTokenType.Whitespace, start);
            case '"':
            case '\'':
                return ConsumeString(c);
            case '#':
                if (IsIdentCodePoint(At(_pos + 1)) || IsValidEscape(_pos + 1))
                {
                    _pos++;
                    var hashType = StartsIdentSequence(_pos) ? CssHashType.Id : CssHashType.Unrestricted;
                    string name = ConsumeIdentSequence();
                    var hash = Make(CssTokenType.Hash, start);
                    hash.Value = name;
                    hash.HashType = hashType;
                    return hash;
                }
                return Delim(start);
            case '(': _pos++; return Make(CssTokenType.LeftParen, start);
            case ')': _pos++; return Make(CssTokenType.RightParen, start);
            case '+':
                if (StartsNumber(_pos)) return ConsumeNumeric();
                return Delim(start);
            case ',': _pos++; return Make(CssTokenType.Comma, start);
            case '-':
                if (StartsNumber(_pos)) return ConsumeNumeric();
                if (At(_pos + 1) == '-' && At(_pos + 2) == '>') { _pos += 3; return Make(CssTokenType.CDC, start); }
                if (StartsIdentSequence(_pos)) return ConsumeIdentLike();
                return Delim(start);
            case '.':
                if (StartsNumber(_pos)) return ConsumeNumeric();
                return Delim(start);
            case ':': _pos++; return Make(CssTokenType.Colon, start);
            case ';': _pos++; return Make(CssTokenType.Semicolon, start);
            case '<':
                if (At(_pos + 1) == '!' && At(_pos + 2) == '-' && At(_pos + 3) == '-') { _pos += 4; return Make(CssTokenType.CDO, start); }
                return Delim(start);
            case '@':
                if (StartsIdentSequence(_pos + 1))
                {
                    _pos++;
                    string name = ConsumeIdentSequence();
                    var at = Make(CssTokenType.AtKeyword, start);
                    at.Value = name;
                    return at;
                }
                return Delim(start);
            case '[': _pos++; return Make(CssTokenType.LeftBracket, start);
            case ']': _pos++; return Make(CssTokenType.RightBracket, start);
            case '{': _pos++; return Make(CssTokenType.LeftBrace, start);
            case '}': _pos++; return Make(CssTokenType.RightBrace, start);
            case '\\':
                if (IsValidEscape(_pos)) return ConsumeIdentLike();
                Error(start, start + 1, "无效的转义：反斜杠后紧跟换行。");
                return Delim(start);
            default:
                if (IsDigit(c)) return ConsumeNumeric();
                if (IsIdentStart(c)) return ConsumeIdentLike();
                return Delim(start);
        }
    }

    // ---- 记号构造 ----

    CssToken Make(CssTokenType type, int start) => new(type, _src.GetRange(start, _pos));

    CssToken Delim(int start)
    {
        _pos++;
        var t = Make(CssTokenType.Delim, start);
        t.Delim = _s[start];
        return t;
    }

    void Error(int start, int end, string message, CssDiagnosticSeverity severity = CssDiagnosticSeverity.Error)
        => _diags.Report(CssDiagnosticCodes.SyntaxError, severity, message, _src.GetRange(start, end));

    // ---- 码点判定（§4.2） ----

    char At(int i) => i < _s.Length ? _s[i] : Eof;

    static bool IsDigit(char c) => c >= '0' && c <= '9';
    static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n';
    static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c >= 0x80 || c == '_';
    static bool IsIdentCodePoint(char c) => IsIdentStart(c) || IsDigit(c) || c == '-';
    static bool IsNonPrintable(char c) => c <= 0x08 || c == 0x0B || (c >= 0x0E && c <= 0x1F) || c == 0x7F;

    /// <summary>§4.3.8：i 处与其后一个码点是否构成有效转义（EOF 也算有效，结果为 U+FFFD）。</summary>
    bool IsValidEscape(int i) => i < _s.Length && _s[i] == '\\' && At(i + 1) != '\n';

    /// <summary>§4.3.9：从 i 开始的三个码点是否开始一个标识符序列。</summary>
    bool StartsIdentSequence(int i)
    {
        char c = At(i);
        if (c == '-')
        {
            char c2 = At(i + 1);
            return IsIdentStart(c2) || c2 == '-' || IsValidEscape(i + 1);
        }
        if (IsIdentStart(c)) return true;
        return c == '\\' && IsValidEscape(i);
    }

    /// <summary>§4.3.10：从 i 开始是否是一个数。</summary>
    bool StartsNumber(int i)
    {
        char c = At(i);
        if (c == '+' || c == '-')
        {
            char c2 = At(i + 1);
            return IsDigit(c2) || (c2 == '.' && IsDigit(At(i + 2)));
        }
        if (c == '.') return IsDigit(At(i + 1));
        return IsDigit(c);
    }

    // ---- 消费算法 ----

    void ConsumeComments()
    {
        while (_pos + 1 < _s.Length && _s[_pos] == '/' && _s[_pos + 1] == '*')
        {
            int end = _s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                Error(_pos, _s.Length, "注释在文件结尾处未闭合。", CssDiagnosticSeverity.Warning);
                _pos = _s.Length;
                return;
            }
            _pos = end + 2;
        }
    }

    /// <summary>§4.3.11：消费标识符序列，返回已处理转义的值。</summary>
    string ConsumeIdentSequence()
    {
        int start = _pos;
        while (_pos < _s.Length && IsIdentCodePoint(_s[_pos])) _pos++;
        if (!IsValidEscape(_pos)) return Name(start, _pos - start);

        var sb = _sb;
        sb.Clear();
        sb.Append(_s, start, _pos - start);
        while (true)
        {
            if (_pos < _s.Length && IsIdentCodePoint(_s[_pos])) { sb.Append(_s[_pos++]); continue; }
            if (IsValidEscape(_pos)) { _pos++; ConsumeEscape(sb); continue; }
            break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 标识符、单位、函数名等高度重复：用一个小的直接映射缓存复用字符串实例，减少分配。
    /// 缓存属于分词器实例，没有共享状态。
    /// </summary>
    string Name(int start, int length)
    {
        if (length > 24) return _s.Substring(start, length);
        uint h = 2166136261;
        for (int i = start; i < start + length; i++) h = (h ^ _s[i]) * 16777619;
        int slot = (int)(h & (NameCacheSize - 1));
        var cached = _names[slot];
        if (cached is not null && cached.Length == length && string.CompareOrdinal(cached, 0, _s, start, length) == 0) return cached;
        var s = _s.Substring(start, length);
        _names[slot] = s;
        return s;
    }

    /// <summary>§4.3.7：消费转义（反斜杠已被消费），把结果码点追加到 <paramref name="sb"/>。</summary>
    void ConsumeEscape(StringBuilder sb)
    {
        if (_pos >= _s.Length)
        {
            Error(_pos - 1, _pos, "转义在文件结尾处中断，按 U+FFFD 处理。", CssDiagnosticSeverity.Warning);
            sb.Append('�');
            return;
        }
        char c = _s[_pos];
        if (IsHexDigit(c))
        {
            int value = 0, n = 0;
            while (n < 6 && _pos < _s.Length && IsHexDigit(_s[_pos]))
            {
                value = value * 16 + HexValue(_s[_pos]);
                _pos++;
                n++;
            }
            if (_pos < _s.Length && IsWhitespace(_s[_pos])) _pos++;
            if (value == 0 || (value >= 0xD800 && value <= 0xDFFF) || value > 0x10FFFF) sb.Append('�');
            else if (value < 0x10000) sb.Append((char)value);
            else
            {
                value -= 0x10000;
                sb.Append((char)(0xD800 + (value >> 10)));
                sb.Append((char)(0xDC00 + (value & 0x3FF)));
            }
            return;
        }
        sb.Append(c);
        _pos++;
        if (char.IsHighSurrogate(c) && _pos < _s.Length && char.IsLowSurrogate(_s[_pos])) sb.Append(_s[_pos++]);
    }

    static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>§4.3.4：消费类标识符记号（ident、function、url 或 bad-url）。</summary>
    CssToken ConsumeIdentLike()
    {
        int start = _pos;
        string name = ConsumeIdentSequence();
        if (At(_pos) == '(')
        {
            _pos++;
            if (CssSerializer.EqualsIgnoreAsciiCase(name, "url"))
            {
                while (IsWhitespace(At(_pos)) && IsWhitespace(At(_pos + 1))) _pos++;
                char c1 = At(_pos), c2 = At(_pos + 1);
                if (c1 == '"' || c1 == '\'' || (IsWhitespace(c1) && (c2 == '"' || c2 == '\'')))
                {
                    var fn = Make(CssTokenType.Function, start);
                    fn.Value = name;
                    return fn;
                }
                return ConsumeUrl(start);
            }
            var f = Make(CssTokenType.Function, start);
            f.Value = name;
            return f;
        }
        var ident = Make(CssTokenType.Ident, start);
        ident.Value = name;
        return ident;
    }

    /// <summary>§4.3.6：消费 url 记号（<c>url(</c> 已被消费）。</summary>
    CssToken ConsumeUrl(int start)
    {
        while (IsWhitespace(At(_pos))) _pos++;
        var sb = _sb;
        sb.Clear();
        while (true)
        {
            if (_pos >= _s.Length)
            {
                Error(start, _pos, "url() 在文件结尾处未闭合。", CssDiagnosticSeverity.Warning);
                return UrlToken(start, sb.ToString());
            }
            char c = _s[_pos];
            if (c == ')') { _pos++; return UrlToken(start, sb.ToString()); }
            if (IsWhitespace(c))
            {
                while (IsWhitespace(At(_pos))) _pos++;
                if (_pos >= _s.Length)
                {
                    Error(start, _pos, "url() 在文件结尾处未闭合。", CssDiagnosticSeverity.Warning);
                    return UrlToken(start, sb.ToString());
                }
                if (_s[_pos] == ')') { _pos++; return UrlToken(start, sb.ToString()); }
                return BadUrl(start, "url() 中的地址含有未加引号的空白，请给地址加引号。");
            }
            if (c == '"' || c == '\'' || c == '(' || IsNonPrintable(c))
                return BadUrl(start, "未加引号的 url() 中不能出现引号、左括号或不可打印字符，请给地址加引号。");
            if (c == '\\')
            {
                if (IsValidEscape(_pos)) { _pos++; ConsumeEscape(sb); continue; }
                return BadUrl(start, "url() 中的反斜杠后不能紧跟换行。");
            }
            sb.Append(c);
            _pos++;
        }
    }

    CssToken UrlToken(int start, string value)
    {
        var t = Make(CssTokenType.Url, start);
        t.Value = value;
        return t;
    }

    CssToken BadUrl(int start, string message)
    {
        // §4.3.14：消费坏 url 的剩余部分
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c == ')') { _pos++; break; }
            if (IsValidEscape(_pos)) { _pos += Math.Min(2, _s.Length - _pos); continue; }
            _pos++;
        }
        Error(start, _pos, message);
        return Make(CssTokenType.BadUrl, start);
    }

    /// <summary>§4.3.5：消费字符串记号。</summary>
    CssToken ConsumeString(char quote)
    {
        int start = _pos;
        _pos++;
        int segStart = _pos;
        // 快速路径：没有转义时直接截取子串
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c == quote)
            {
                var t = Make2(CssTokenType.String, start, _pos + 1);
                t.Value = _s.Substring(segStart, _pos - segStart);
                _pos++;
                return t;
            }
            if (c == '\n' || c == '\\') break;
            _pos++;
        }

        var sb = _sb;
        sb.Clear();
        sb.Append(_s, segStart, _pos - segStart);
        while (true)
        {
            if (_pos >= _s.Length)
            {
                Error(start, _pos, "字符串在文件结尾处未闭合。", CssDiagnosticSeverity.Warning);
                var t = Make(CssTokenType.String, start);
                t.Value = sb.ToString();
                return t;
            }
            char c = _s[_pos];
            if (c == quote)
            {
                _pos++;
                var t = Make(CssTokenType.String, start);
                t.Value = sb.ToString();
                return t;
            }
            if (c == '\n')
            {
                // 不消费换行，返回坏字符串
                Error(start, _pos, "字符串中出现未转义的换行；需要换行时请写 \\A 或在行尾加反斜杠。");
                return Make(CssTokenType.BadString, start);
            }
            if (c == '\\')
            {
                if (_pos + 1 >= _s.Length) { _pos++; continue; }
                if (_s[_pos + 1] == '\n') { _pos += 2; continue; }
                _pos++;
                ConsumeEscape(sb);
                continue;
            }
            sb.Append(c);
            _pos++;
        }
    }

    CssToken Make2(CssTokenType type, int start, int end) => new(type, _src.GetRange(start, end));

    /// <summary>§4.3.3：消费数值记号（number、percentage 或 dimension）。</summary>
    CssToken ConsumeNumeric()
    {
        int start = _pos;
        double value = ConsumeNumber(out var type, out bool sign);
        CssToken t;
        if (StartsIdentSequence(_pos))
        {
            string unit = ConsumeIdentSequence();
            t = Make(CssTokenType.Dimension, start);
            t.Unit = unit;
        }
        else if (At(_pos) == '%')
        {
            _pos++;
            t = Make(CssTokenType.Percentage, start);
        }
        else
        {
            t = Make(CssTokenType.Number, start);
        }
        t.Number = value;
        t.NumberType = type;
        t.HasSign = sign;
        return t;
    }

    /// <summary>§4.3.12 / §4.3.13：消费一个数并精确转换。</summary>
    double ConsumeNumber(out CssNumberType type, out bool sign)
    {
        type = CssNumberType.Integer;
        int start = _pos;
        sign = false;
        bool negative = false;
        char c = _s[_pos];
        if (c == '+' || c == '-') { sign = true; negative = c == '-'; _pos++; }
        int intStart = _pos;
        while (IsDigit(At(_pos))) _pos++;
        int intDigits = _pos - intStart;
        if (At(_pos) == '.' && IsDigit(At(_pos + 1)))
        {
            _pos += 2;
            while (IsDigit(At(_pos))) _pos++;
            type = CssNumberType.Number;
        }
        char e = At(_pos);
        if (e == 'e' || e == 'E')
        {
            char e1 = At(_pos + 1);
            if (IsDigit(e1)) { _pos += 2; while (IsDigit(At(_pos))) _pos++; type = CssNumberType.Number; }
            else if ((e1 == '+' || e1 == '-') && IsDigit(At(_pos + 2))) { _pos += 3; while (IsDigit(At(_pos))) _pos++; type = CssNumberType.Number; }
        }

        if (type == CssNumberType.Integer && intDigits <= 15)
        {
            // 15 位以内的整数可在 long 中精确累加，结果可精确表示为 double
            long acc = 0;
            for (int i = intStart; i < _pos; i++) acc = acc * 10 + (_s[i] - '0');
            double d = acc;
            return negative ? -d : d;
        }
        return ParseDouble(start, _pos - start);
    }

    double ParseDouble(int start, int length)
    {
        try
        {
#if NET8_0_OR_GREATER
            return double.Parse(_s.AsSpan(start, length), NumberStyles.Float, CultureInfo.InvariantCulture);
#else
            return double.Parse(_s.Substring(start, length), NumberStyles.Float, CultureInfo.InvariantCulture);
#endif
        }
        catch (OverflowException)
        {
            // .NET Framework 对超范围值抛异常；现代 .NET 返回无穷大。统一为无穷大，由值层钳制
            return _s[start] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }
    }
}

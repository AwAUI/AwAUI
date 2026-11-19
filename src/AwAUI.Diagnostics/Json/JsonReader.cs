using System;
using System.Buffers;
using System.Globalization;

namespace AwAUI.Diagnostics.Json;

/// <summary>
/// 只进的 UTF-8 JSON 记号读取器（RFC 8259 严格模式）。直接在输入字节上工作，读取过程不分配
/// （嵌套超过 64 层时分配一次深度栈；取字符串值时分配结果字符串）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>任何 JSON 值都可作为顶层值；值后只允许空白（空格、\t、\n、\r）。</item>
/// <item>拒绝：注释、尾随逗号、单引号、前导零、NaN/Infinity、BOM、字符串中的控制字符与无效 UTF-8、未知转义。</item>
/// <item>\u 转义中的代理对组合为一个码点；孤立代理按原样保留为 UTF-16 码元（RFC 8259 §8.2 允许，由调用方决定如何处理）。</item>
/// <item>对象中的重复键由上层决定（<see cref="JsonObject"/> 取最后一个）。</item>
/// <item>错误以 <see cref="JsonException"/> 报告，带精确的字节偏移、行、列。</item>
/// </list>
/// </remarks>
public ref struct JsonReader
{
    readonly ReadOnlySpan<byte> _buf;
    readonly int _maxDepth;
    int _pos;
    int _depth;
    ulong _bits;          // 第 d 位（d = 深度-1，d < 64）为 1 表示该层是对象
    bool[]? _deep;        // 64 层以上的容器类型
    JsonTokenType _token;
    int _tokenStart;
    int _valueStart;
    int _valueLength;
    bool _escaped;
    bool _done;

    public JsonReader(ReadOnlySpan<byte> utf8Json, JsonReaderOptions options = default)
    {
        _buf = utf8Json;
        _maxDepth = options.MaxDepth;
        _pos = 0; _depth = 0; _bits = 0; _deep = null;
        _token = JsonTokenType.None;
        _tokenStart = 0; _valueStart = 0; _valueLength = 0;
        _escaped = false; _done = false;
    }

    public JsonReader(ReadOnlyMemory<byte> utf8Json, JsonReaderOptions options = default) : this(utf8Json.Span, options) { }

    /// <summary>当前记号类型。</summary>
    public readonly JsonTokenType TokenType => _token;
    /// <summary>处理完当前记号后所在的容器嵌套层数（StartObject 之后为进入后的层数，EndObject 之后为退出后的层数）。</summary>
    public readonly int Depth => _depth;
    /// <summary>当前记号首字节的偏移。</summary>
    public readonly int TokenStartIndex => _tokenStart;
    /// <summary>已消费的字节数。</summary>
    public readonly int BytesConsumed => _pos;
    /// <summary>当前值的原始字节：字符串与属性名为引号之间的内容（转义未处理），数字与字面量为其文本。</summary>
    public readonly ReadOnlySpan<byte> ValueSpan => _buf.Slice(_valueStart, _valueLength);
    /// <summary>当前字符串或属性名是否含转义序列。</summary>
    public readonly bool ValueIsEscaped => _escaped;

    readonly bool InObject => _depth <= 64 ? (_bits & (1UL << (_depth - 1))) != 0 : _deep![_depth - 1];

    /// <summary>读取下一个记号。输入结束（且顶层值完整）时返回 false。</summary>
    public bool Read()
    {
        if (_done) return false;
        SkipWhitespace();
        bool eof = _pos >= _buf.Length;
        switch (_token)
        {
            case JsonTokenType.None:
                if (eof) throw Error("Unexpected end of input, expected a JSON value", _pos);
                ReadValue();
                return true;
            case JsonTokenType.PropertyName:
                if (eof) throw Error("Unexpected end of input, expected a value", _pos);
                ReadValue();
                return true;
            case JsonTokenType.StartObject:
                if (eof) throw Error("Unexpected end of input, expected a property name or '}'", _pos);
                if (_buf[_pos] == (byte)'}') { EndContainer(true); return true; }
                if (_buf[_pos] == (byte)'"') { ReadPropertyName(); return true; }
                throw Error("Expected a property name or '}'", _pos);
            case JsonTokenType.StartArray:
                if (eof) throw Error("Unexpected end of input, expected a value or ']'", _pos);
                if (_buf[_pos] == (byte)']') { EndContainer(false); return true; }
                ReadValue();
                return true;
            default:
                if (_depth == 0)
                {
                    if (eof) { _done = true; return false; }
                    throw Error("Unexpected content after the top-level value", _pos);
                }
                bool inObject = InObject;
                if (eof) throw Error(inObject ? "Unexpected end of input, expected ',' or '}'" : "Unexpected end of input, expected ',' or ']'", _pos);
                byte c = _buf[_pos];
                if (c == (byte)',')
                {
                    _pos++;
                    SkipWhitespace();
                    if (_pos >= _buf.Length) throw Error("Unexpected end of input after ','", _pos);
                    if (inObject)
                    {
                        if (_buf[_pos] == (byte)'"') { ReadPropertyName(); return true; }
                        throw Error(_buf[_pos] == (byte)'}' ? "Trailing comma in object" : "Expected a property name", _pos);
                    }
                    if (_buf[_pos] == (byte)']') throw Error("Trailing comma in array", _pos);
                    ReadValue();
                    return true;
                }
                if (c == (byte)'}' && inObject) { EndContainer(true); return true; }
                if (c == (byte)']' && !inObject) { EndContainer(false); return true; }
                throw Error(inObject ? "Expected ',' or '}'" : "Expected ',' or ']'", _pos);
        }
    }

    /// <summary>
    /// 跳过当前值：在 PropertyName 上跳过其值；在 StartObject/StartArray 上跳到对应的结束记号；其他记号上无操作。
    /// </summary>
    public void Skip()
    {
        if (_token == JsonTokenType.PropertyName) Read();
        if (_token is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            int target = _depth - 1;
            do { Read(); }
            while (_depth > target || (_token is not (JsonTokenType.EndObject or JsonTokenType.EndArray)));
        }
    }

    void SkipWhitespace()
    {
        var b = _buf;
        int p = _pos;
        while (p < b.Length)
        {
            byte c = b[p];
            if (c == (byte)' ' || c == (byte)'\n' || c == (byte)'\r' || c == (byte)'\t') p++;
            else break;
        }
        _pos = p;
    }

    void ReadValue()
    {
        byte c = _buf[_pos];
        _tokenStart = _pos;
        _escaped = false;
        switch (c)
        {
            case (byte)'{':
                Push(true);
                _pos++;
                _valueStart = _tokenStart; _valueLength = 1;
                _token = JsonTokenType.StartObject;
                return;
            case (byte)'[':
                Push(false);
                _pos++;
                _valueStart = _tokenStart; _valueLength = 1;
                _token = JsonTokenType.StartArray;
                return;
            case (byte)'"':
                ReadStringBody();
                _token = JsonTokenType.String;
                return;
            case (byte)'t': ReadLiteral("true"u8, JsonTokenType.True); return;
            case (byte)'f': ReadLiteral("false"u8, JsonTokenType.False); return;
            case (byte)'n': ReadLiteral("null"u8, JsonTokenType.Null); return;
            default:
                if (c == (byte)'-' || (c >= (byte)'0' && c <= (byte)'9')) { ReadNumber(); return; }
                if (c == (byte)']' || c == (byte)'}') throw Error("Expected a value", _pos);
                throw Error(c >= 0x20 && c < 0x7F ? $"Unexpected character '{(char)c}'" : $"Unexpected byte 0x{c:X2}", _pos);
        }
    }

    void ReadPropertyName()
    {
        _tokenStart = _pos;
        _escaped = false;
        ReadStringBody();
        SkipWhitespace();
        if (_pos >= _buf.Length) throw Error("Unexpected end of input, expected ':'", _pos);
        if (_buf[_pos] != (byte)':') throw Error("Expected ':' after property name", _pos);
        _pos++;
        _token = JsonTokenType.PropertyName;
    }

    void ReadStringBody()
    {
        var b = _buf;
        int i = _pos + 1;
        bool escaped = false;
        while (true)
        {
            if (i >= b.Length) throw Error("Unterminated string", i);
            byte c = b[i];
            if (c == (byte)'"') break;
            if (c == (byte)'\\')
            {
                escaped = true;
                if (i + 1 >= b.Length) throw Error("Unterminated string", i + 1);
                byte e = b[i + 1];
                switch (e)
                {
                    case (byte)'"': case (byte)'\\': case (byte)'/': case (byte)'b':
                    case (byte)'f': case (byte)'n': case (byte)'r': case (byte)'t':
                        i += 2;
                        continue;
                    case (byte)'u':
                        for (int k = 2; k < 6; k++)
                        {
                            if (i + k >= b.Length) throw Error("Unterminated \\u escape", i + k);
                            if (HexValue(b[i + k]) < 0) throw Error("Invalid hex digit in \\u escape", i + k);
                        }
                        i += 6;
                        continue;
                    default:
                        throw Error("Invalid escape sequence", i + 1);
                }
            }
            if (c < 0x20) throw Error("Control character in string must be escaped", i);
            if (c < 0x80) { i++; continue; }
            int n = Utf8Util.ValidateSequence(b, i);
            if (n == 0) throw Error("Invalid UTF-8 in string", i);
            i += n;
        }
        _valueStart = _pos + 1;
        _valueLength = i - _valueStart;
        _escaped = escaped;
        _pos = i + 1;
    }

    void ReadLiteral(ReadOnlySpan<byte> literal, JsonTokenType token)
    {
        var b = _buf;
        for (int k = 0; k < literal.Length; k++)
        {
            int p = _pos + k;
            if (p >= b.Length) throw Error("Unexpected end of input in literal", p);
            if (b[p] != literal[k]) throw Error("Invalid literal", p);
        }
        _valueStart = _pos;
        _valueLength = literal.Length;
        _pos += literal.Length;
        _token = token;
    }

    void ReadNumber()
    {
        var b = _buf;
        int i = _pos;
        if (b[i] == (byte)'-') i++;
        if (i >= b.Length || !IsDigit(b[i])) throw Error("Invalid number: expected a digit", i);
        if (b[i] == (byte)'0')
        {
            i++;
            if (i < b.Length && IsDigit(b[i])) throw Error("Invalid number: leading zero", i);
        }
        else
        {
            while (i < b.Length && IsDigit(b[i])) i++;
        }
        if (i < b.Length && b[i] == (byte)'.')
        {
            i++;
            if (i >= b.Length || !IsDigit(b[i])) throw Error("Invalid number: expected a digit after '.'", i);
            while (i < b.Length && IsDigit(b[i])) i++;
        }
        if (i < b.Length && (b[i] == (byte)'e' || b[i] == (byte)'E'))
        {
            i++;
            if (i < b.Length && (b[i] == (byte)'+' || b[i] == (byte)'-')) i++;
            if (i >= b.Length || !IsDigit(b[i])) throw Error("Invalid number: expected a digit in exponent", i);
            while (i < b.Length && IsDigit(b[i])) i++;
        }
        if (i < b.Length)
        {
            byte c = b[i];
            if (c == (byte)'.' || c == (byte)'+' || c == (byte)'-' || (c | 0x20) is >= (byte)'a' and <= (byte)'z')
                throw Error("Invalid number", i);
        }
        _valueStart = _pos;
        _valueLength = i - _pos;
        _pos = i;
        _token = JsonTokenType.Number;
    }

    void Push(bool isObject)
    {
        if (_depth >= _maxDepth) throw Error($"Maximum nesting depth {_maxDepth} exceeded", _pos);
        if (_depth < 64)
        {
            if (isObject) _bits |= 1UL << _depth;
            else _bits &= ~(1UL << _depth);
        }
        else
        {
            _deep ??= new bool[_maxDepth];
            _deep[_depth] = isObject;
        }
        _depth++;
    }

    void EndContainer(bool isObject)
    {
        _tokenStart = _pos;
        _valueStart = _pos; _valueLength = 1;
        _pos++;
        _depth--;
        _token = isObject ? JsonTokenType.EndObject : JsonTokenType.EndArray;
    }

    static bool IsDigit(byte c) => c >= (byte)'0' && c <= (byte)'9';

    internal static int HexValue(byte c)
    {
        if (c >= (byte)'0' && c <= (byte)'9') return c - '0';
        int l = c | 0x20;
        if (l >= 'a' && l <= 'f') return l - 'a' + 10;
        return -1;
    }

    readonly JsonException Error(string message, int position)
    {
        int line = 1, lineStart = 0;
        int end = Math.Min(position, _buf.Length);
        for (int i = 0; i < end; i++)
            if (_buf[i] == (byte)'\n') { line++; lineStart = i + 1; }
        return new JsonException(message, position, line, position - lineStart + 1);
    }

    // ---- 取值 ----

    /// <summary>取当前字符串、属性名的值（处理转义）；Null 记号返回 null。</summary>
    public readonly string? GetString()
    {
        if (_token == JsonTokenType.Null) return null;
        if (_token is not (JsonTokenType.String or JsonTokenType.PropertyName))
            throw new InvalidOperationException($"Cannot get a string from token {_token}.");
        var raw = ValueSpan;
        if (!_escaped) return Utf8Util.GetString(raw);
        char[]? rented = null;
        Span<char> buf = raw.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(raw.Length));
        try
        {
            int n = Unescape(raw, buf);
            return buf.Slice(0, n).ToString();
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>把已校验的带转义字符串内容解码为 UTF-16。目标长度不小于源字节数即可。</summary>
    internal static int Unescape(ReadOnlySpan<byte> raw, Span<char> dest)
    {
        int o = 0, i = 0;
        while (i < raw.Length)
        {
            int j = raw.Slice(i).IndexOf((byte)'\\');
            int segEnd = j < 0 ? raw.Length : i + j;
            if (segEnd > i) o += Utf8Util.GetChars(raw.Slice(i, segEnd - i), dest.Slice(o));
            if (j < 0) break;
            i = segEnd + 1;
            byte e = raw[i];
            switch (e)
            {
                case (byte)'b': dest[o++] = '\b'; i++; break;
                case (byte)'f': dest[o++] = '\f'; i++; break;
                case (byte)'n': dest[o++] = '\n'; i++; break;
                case (byte)'r': dest[o++] = '\r'; i++; break;
                case (byte)'t': dest[o++] = '\t'; i++; break;
                case (byte)'u':
                    dest[o++] = (char)((HexValue(raw[i + 1]) << 12) | (HexValue(raw[i + 2]) << 8) | (HexValue(raw[i + 3]) << 4) | HexValue(raw[i + 4]));
                    i += 5;
                    break;
                default: dest[o++] = (char)e; i++; break; // " \ /
            }
        }
        return o;
    }

    /// <summary>当前字符串或属性名（转义后）是否等于给定的 UTF-8 文本。未转义时不分配。</summary>
    public readonly bool ValueTextEquals(ReadOnlySpan<byte> utf8Text)
    {
        if (_token is not (JsonTokenType.String or JsonTokenType.PropertyName)) return false;
        if (!_escaped) return ValueSpan.SequenceEqual(utf8Text);
        string? s = GetString();
        string other;
        try { other = Utf8Util.GetString(utf8Text); }
        catch (ArgumentException) { return false; }
        return string.Equals(s, other, StringComparison.Ordinal);
    }

    /// <summary>当前字符串或属性名（转义后）是否等于给定文本。</summary>
    public readonly bool ValueTextEquals(string text)
    {
        if (_token is not (JsonTokenType.String or JsonTokenType.PropertyName)) return false;
        return string.Equals(GetString(), text, StringComparison.Ordinal);
    }

    public readonly bool GetBoolean() => _token switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        _ => throw new InvalidOperationException($"Cannot get a boolean from token {_token}."),
    };

    /// <summary>数字是整数语法且在 Int64 范围内时成功。</summary>
    public readonly bool TryGetInt64(out long value)
    {
        if (_token != JsonTokenType.Number) throw new InvalidOperationException($"Cannot get a number from token {_token}.");
        return TryParseInt64(ValueSpan, out value);
    }

    public readonly long GetInt64() =>
        TryGetInt64(out long v) ? v : throw new FormatException("The JSON number is not an Int64.");

    public readonly double GetDouble()
    {
        if (_token != JsonTokenType.Number) throw new InvalidOperationException($"Cannot get a number from token {_token}.");
        return ParseDouble(ValueSpan);
    }

    internal static bool TryParseInt64(ReadOnlySpan<byte> s, out long value)
    {
        value = 0;
        int i = 0;
        bool neg = false;
        if (s.Length > 0 && s[0] == (byte)'-') { neg = true; i = 1; }
        if (i >= s.Length) return false;
        ulong acc = 0;
        ulong limit = neg ? 9223372036854775808UL : 9223372036854775807UL;
        for (; i < s.Length; i++)
        {
            uint d = (uint)(s[i] - '0');
            if (d > 9) return false;
            if (acc > (limit - d) / 10) return false;
            acc = acc * 10 + d;
        }
        value = neg ? (long)(0UL - acc) : (long)acc;
        return true;
    }

    /// <summary>解析已校验语法的 JSON 数字。超出范围时返回 ±∞（各运行时统一）。</summary>
    internal static double ParseDouble(ReadOnlySpan<byte> s)
    {
#if NET8_0_OR_GREATER
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
#else
        Span<char> chars = s.Length <= 128 ? stackalloc char[128] : new char[s.Length];
        for (int i = 0; i < s.Length; i++) chars[i] = (char)s[i];
        string str = chars.Slice(0, s.Length).ToString();
        try { return double.Parse(str, NumberStyles.Float, CultureInfo.InvariantCulture); }
        catch (OverflowException) { return s[0] == (byte)'-' ? double.NegativeInfinity : double.PositiveInfinity; }
#endif
    }
}

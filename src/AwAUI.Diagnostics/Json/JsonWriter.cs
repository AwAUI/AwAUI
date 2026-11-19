using System;
using System.Buffers;
using System.Globalization;

namespace AwAUI.Diagnostics.Json;

/// <summary>
/// 流式 UTF-8 JSON 写入器，直接写进 <see cref="IBufferWriter{T}"/>，输出紧凑格式。
/// 校验结构（属性名与值交替、容器配对、只有一个顶层值），结构错误抛 <see cref="InvalidOperationException"/>。
/// </summary>
/// <remarks>
/// 字符串转义：<c>"</c>、<c>\</c> 与 U+0000–U+001F 一律转义（常见的用短形式 \n \t 等），孤立代理写为 \uXXXX，
/// 其余字符按 UTF-8 原样输出。NaN 与 ±∞ 无法用 JSON 表示，写入时抛 <see cref="ArgumentException"/>。
/// </remarks>
public sealed class JsonWriter
{
    const int MaxDepth = 1000;

    readonly IBufferWriter<byte> _out;
    int _depth;
    ulong _bits;          // 第 d 位为 1 表示第 d+1 层是对象
    bool[]? _deep;
    bool _needComma;      // 当前容器中已有元素
    bool _afterName;      // 刚写完属性名，等待值
    bool _rootDone;

    public JsonWriter(IBufferWriter<byte> output)
    {
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>当前嵌套深度。</summary>
    public int Depth => _depth;

    /// <summary>是否已写完一个完整的顶层值。</summary>
    public bool IsComplete => _rootDone && _depth == 0;

    bool InObject => _depth > 0 && (_depth <= 64 ? (_bits & (1UL << (_depth - 1))) != 0 : _deep![_depth - 1]);

    /// <summary>重置状态，以便向同一输出写下一个独立的 JSON 文档。</summary>
    public void Reset()
    {
        _depth = 0; _bits = 0; _needComma = false; _afterName = false; _rootDone = false;
    }

    // ---- 结构 ----

    public void WriteStartObject() { BeforeValue(); Push(true); WriteByte((byte)'{'); }
    public void WriteStartArray() { BeforeValue(); Push(false); WriteByte((byte)'['); }
    public void WriteStartObject(string propertyName) { WritePropertyName(propertyName); WriteStartObject(); }
    public void WriteStartArray(string propertyName) { WritePropertyName(propertyName); WriteStartArray(); }

    public void WriteEndObject()
    {
        if (_depth == 0 || !InObject || _afterName) throw new InvalidOperationException("No object to end.");
        Pop();
        WriteByte((byte)'}');
    }

    public void WriteEndArray()
    {
        if (_depth == 0 || InObject) throw new InvalidOperationException("No array to end.");
        Pop();
        WriteByte((byte)']');
    }

    public void WritePropertyName(string name) => WritePropertyName(name.AsSpan());

    public void WritePropertyName(ReadOnlySpan<char> name)
    {
        BeforeName();
        WriteQuoted(name);
        WriteByte((byte)':');
        _afterName = true;
    }

    /// <summary>写属性名（已是合法 UTF-8 的文本，例如 <c>"id"u8</c>）。</summary>
    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        BeforeName();
        WriteQuotedUtf8(utf8Name);
        WriteByte((byte)':');
        _afterName = true;
    }

    // ---- 值 ----

    public void WriteString(string? value)
    {
        if (value is null) { WriteNull(); return; }
        BeforeValue();
        WriteQuoted(value.AsSpan());
    }

    public void WriteString(ReadOnlySpan<char> value) { BeforeValue(); WriteQuoted(value); }

    /// <summary>写字符串值（已是合法 UTF-8 的文本）。</summary>
    public void WriteStringUtf8(ReadOnlySpan<byte> utf8Value) { BeforeValue(); WriteQuotedUtf8(utf8Value); }

    public void WriteNumber(long value)
    {
        BeforeValue();
        var span = _out.GetSpan(20);
        if (!System.Buffers.Text.Utf8Formatter.TryFormat(value, span, out int n)) throw new InvalidOperationException();
        _out.Advance(n);
    }

    public void WriteNumber(int value) => WriteNumber((long)value);

    public void WriteNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("NaN and Infinity cannot be represented in JSON.", nameof(value));
        BeforeValue();
        WriteDoubleText(value);
    }

    public void WriteBoolean(bool value)
    {
        BeforeValue();
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    public void WriteNull()
    {
        BeforeValue();
        WriteRaw("null"u8);
    }

    /// <summary>
    /// 写入一段已经序列化好的 JSON 值（调用方保证其合法；用于嵌入缓存的片段）。
    /// </summary>
    public void WriteRawValue(ReadOnlySpan<byte> utf8Json)
    {
        BeforeValue();
        WriteRaw(utf8Json);
    }

    /// <summary>写入数字的原始文本（调用方保证其符合 JSON 数字语法）。</summary>
    internal void WriteNumberRaw(string text)
    {
        BeforeValue();
        var span = _out.GetSpan(text.Length);
        for (int i = 0; i < text.Length; i++) span[i] = (byte)text[i];
        _out.Advance(text.Length);
    }

    // 便捷：属性 + 值
    public void WriteString(string name, string? value) { WritePropertyName(name); WriteString(value); }
    public void WriteNumber(string name, long value) { WritePropertyName(name); WriteNumber(value); }
    public void WriteNumber(string name, double value) { WritePropertyName(name); WriteNumber(value); }
    public void WriteBoolean(string name, bool value) { WritePropertyName(name); WriteBoolean(value); }
    public void WriteNull(string name) { WritePropertyName(name); WriteNull(); }

    // ---- 内部 ----

    void BeforeName()
    {
        if (!InObject || _afterName) throw new InvalidOperationException("A property name can only be written inside an object, before a value.");
        if (_needComma) WriteByte((byte)',');
    }

    void BeforeValue()
    {
        if (_depth == 0)
        {
            if (_rootDone) throw new InvalidOperationException("A JSON document can only contain one top-level value.");
            _rootDone = true;
            return;
        }
        if (InObject)
        {
            if (!_afterName) throw new InvalidOperationException("A value inside an object must follow a property name.");
            _afterName = false;
            _needComma = true;
            return;
        }
        if (_needComma) WriteByte((byte)',');
        _needComma = true;
    }

    void Push(bool isObject)
    {
        if (_depth >= MaxDepth) throw new InvalidOperationException("Maximum depth exceeded.");
        if (_depth < 64)
        {
            if (isObject) _bits |= 1UL << _depth; else _bits &= ~(1UL << _depth);
        }
        else
        {
            _deep ??= new bool[MaxDepth];
            _deep[_depth] = isObject;
        }
        _depth++;
        _needComma = false;
    }

    void Pop()
    {
        _depth--;
        _needComma = true;  // 父容器中刚完成一个元素（顶层时无意义）
    }

    void WriteByte(byte b)
    {
        var s = _out.GetSpan(1);
        s[0] = b;
        _out.Advance(1);
    }

    void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        var s = _out.GetSpan(bytes.Length);
        bytes.CopyTo(s);
        _out.Advance(bytes.Length);
    }

    void WriteDoubleText(double value)
    {
#if NET8_0_OR_GREATER
        var span = _out.GetSpan(32);
        if (!value.TryFormat(span, out int n, default, CultureInfo.InvariantCulture)) throw new InvalidOperationException();
        _out.Advance(n);
#else
        string s = value.ToString("R", CultureInfo.InvariantCulture);
        var span = _out.GetSpan(s.Length);
        for (int i = 0; i < s.Length; i++) span[i] = (byte)s[i];
        _out.Advance(s.Length);
#endif
    }

    static readonly byte[] s_hex = "0123456789ABCDEF"u8.ToArray();

    static int EscapeShort(int c) => c switch
    {
        '"' => '"',
        '\\' => '\\',
        '\n' => 'n',
        '\r' => 'r',
        '\t' => 't',
        '\b' => 'b',
        '\f' => 'f',
        _ => 0,
    };

    void WriteQuoted(ReadOnlySpan<char> s)
    {
        WriteByte((byte)'"');
        const int Chunk = 256;
        int i = 0;
        while (i < s.Length)
        {
            int end = Math.Min(s.Length, i + Chunk);
            // 不在代理对中间切开
            if (end < s.Length && char.IsHighSurrogate(s[end - 1])) end++;
            var span = _out.GetSpan((end - i) * 6);
            int o = 0;
            while (i < end)
            {
                char c = s[i];
                if (c < 0x80)
                {
                    if (c >= 0x20 && c != '"' && c != '\\') { span[o++] = (byte)c; i++; continue; }
                    int e = EscapeShort(c);
                    if (e != 0) { span[o++] = (byte)'\\'; span[o++] = (byte)e; }
                    else o += WriteU(span.Slice(o), c);
                    i++;
                    continue;
                }
                if (c < 0x800)
                {
                    span[o++] = (byte)(0xC0 | (c >> 6));
                    span[o++] = (byte)(0x80 | (c & 0x3F));
                    i++;
                    continue;
                }
                if (char.IsSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        int cp = char.ConvertToUtf32(c, s[i + 1]);
                        span[o++] = (byte)(0xF0 | (cp >> 18));
                        span[o++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                        span[o++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                        span[o++] = (byte)(0x80 | (cp & 0x3F));
                        i += 2;
                        continue;
                    }
                    o += WriteU(span.Slice(o), c);  // 孤立代理无法编码为 UTF-8
                    i++;
                    continue;
                }
                span[o++] = (byte)(0xE0 | (c >> 12));
                span[o++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                span[o++] = (byte)(0x80 | (c & 0x3F));
                i++;
            }
            _out.Advance(o);
        }
        WriteByte((byte)'"');
    }

    static int WriteU(Span<byte> dest, int c)
    {
        dest[0] = (byte)'\\';
        dest[1] = (byte)'u';
        dest[2] = s_hex[(c >> 12) & 0xF];
        dest[3] = s_hex[(c >> 8) & 0xF];
        dest[4] = s_hex[(c >> 4) & 0xF];
        dest[5] = s_hex[c & 0xF];
        return 6;
    }

    void WriteQuotedUtf8(ReadOnlySpan<byte> s)
    {
        WriteByte((byte)'"');
        int i = 0;
        while (i < s.Length)
        {
            // 找下一个需要转义的字节，中间的整段复制
            int start = i;
            while (i < s.Length && s[i] >= 0x20 && s[i] != (byte)'"' && s[i] != (byte)'\\') i++;
            if (i > start) WriteRaw(s.Slice(start, i - start));
            if (i >= s.Length) break;
            byte c = s[i++];
            var span = _out.GetSpan(6);
            int e = EscapeShort(c);
            if (e != 0) { span[0] = (byte)'\\'; span[1] = (byte)e; _out.Advance(2); }
            else _out.Advance(WriteU(span, c));
        }
        WriteByte((byte)'"');
    }
}

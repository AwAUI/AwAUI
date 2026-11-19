using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.Svg.Xml;

// 为什么不用 System.Xml：
// 1. 体积与裁剪：System.Xml 在 NativeAOT / WASM 下会带进数百 KB 的代码（XmlReader 设置、DTD 处理、编码探测），
//    而 SVG 只需要元素、属性、文本与 CDATA。
// 2. 安全：XmlReader 的 DTD 处理（实体展开、外部实体）是 XXE 与 billion-laughs 的来源；不同目标框架
//    （netstandard2.0 / .NET Framework）的默认值还不一样。这里根本不实现 DTD：DOCTYPE 整体跳过，
//    只认 5 个预定义实体与数字字符引用，没有任何展开会放大输入。
// 3. 可控的上限：深度、元素数、属性数在解析时就检查，超限立即以 SvgParseException 失败，且解析是迭代的，
//    不会因深嵌套耗尽栈。

/// <summary>一个属性。名称已按前缀拆分并驻留。</summary>
internal readonly struct XmlAttr
{
    public XmlAttr(string? prefix, string name, string value, int offset)
    {
        Prefix = prefix;
        Name = name;
        Value = value;
        Offset = offset;
    }

    public string? Prefix { get; }
    public string Name { get; }
    public string Value { get; }
    public int Offset { get; }
}

/// <summary>解析得到的元素（只保留 SVG 需要的信息）。</summary>
internal sealed class XmlElement
{
    public XmlElement(string? prefix, string name, XmlAttr[] attributes, int offset)
    {
        Prefix = prefix;
        Name = name;
        Attributes = attributes;
        Offset = offset;
    }

    public string? Prefix { get; }
    public string Name { get; }
    public XmlAttr[] Attributes { get; }
    public int Offset { get; }
    public XmlElement? Parent { get; internal set; }
    public List<XmlElement>? Children { get; internal set; }

    /// <summary>文本内容（含 CDATA）。只为 &lt;style&gt; 元素收集，其余为 null。</summary>
    public string? Text { get; internal set; }

    public string? GetAttribute(string name)
    {
        foreach (var a in Attributes)
            if (a.Prefix == null && (ReferenceEquals(a.Name, name) || a.Name == name)) return a.Value;
        return null;
    }
}

/// <summary>名称驻留表：同名的元素与属性名只分配一次。哈希带每进程随机种子，链长有上限以避免碰撞攻击退化。</summary>
internal sealed class NameTable
{
    sealed class Entry
    {
        public Entry(string value, int hash, Entry? next) { Value = value; Hash = hash; Next = next; }
        public readonly string Value;
        public readonly int Hash;
        public Entry? Next;
    }

    static readonly uint Seed = (uint)Environment.TickCount * 2654435761u ^ (uint)Guid.NewGuid().GetHashCode();
    const int MaxChain = 16;

    Entry?[] _buckets = new Entry?[64];
    int _count;

    public string Get(string source, int start, int length)
    {
        uint h = Seed ^ 2166136261u;
        for (int i = 0; i < length; i++) h = (h ^ source[start + i]) * 16777619u;
        h ^= h >> 15;
        int hash = (int)h;
        int b = hash & (_buckets.Length - 1);
        int chain = 0;
        for (var e = _buckets[b]; e != null; e = e.Next, chain++)
        {
            if (e.Hash == hash && e.Value.Length == length && string.CompareOrdinal(e.Value, 0, source, start, length) == 0)
                return e.Value;
        }
        var s = source.Substring(start, length);
        if (chain >= MaxChain) return s; // 退化的桶不再增长
        _buckets[b] = new Entry(s, hash, _buckets[b]);
        if (++_count > _buckets.Length * 2) Grow();
        return s;
    }

    void Grow()
    {
        var next = new Entry?[_buckets.Length * 2];
        foreach (var head in _buckets)
        {
            for (var e = head; e != null;)
            {
                var n = e.Next;
                int b = e.Hash & (next.Length - 1);
                e.Next = next[b];
                next[b] = e;
                e = n;
            }
        }
        _buckets = next;
    }
}

/// <summary>
/// XML 子集解析器：元素、属性、文本、CDATA、注释、处理指令；DOCTYPE 被跳过。
/// 不做命名空间校验（前缀原样保留，由 SVG 层解释）。全部过程对输入长度线性。
/// </summary>
internal sealed class XmlLiteParser
{
    readonly string _s;
    readonly SvgParseOptions _options;
    readonly DiagnosticBag _diag;
    readonly SourceLocator _locator;
    readonly NameTable _names = new();
    readonly List<XmlAttr> _attrs = new();
    readonly HashSet<string> _attrNames = new(StringComparer.Ordinal);
    readonly StringBuilder _sb = new();
    int _pos;
    int _elementCount;

    XmlLiteParser(string s, SvgParseOptions options, DiagnosticBag diag, SourceLocator locator)
    {
        _s = s;
        _options = options;
        _diag = diag;
        _locator = locator;
    }

    public static XmlElement Parse(string text, SvgParseOptions options, DiagnosticBag diag, SourceLocator locator) =>
        new XmlLiteParser(text, options, diag, locator).Run();

    SvgParseException Error(string message, int offset)
    {
        var (line, col) = _locator.Locate(offset);
        return new SvgParseException(message, line, col);
    }

    XmlElement Run()
    {
        if (_s.Length > 0 && _s[0] == '﻿') _pos = 1;
        XmlElement? root = null;
        bool rootClosed = false;
        var stack = new List<XmlElement>();
        var textBuilders = new Dictionary<XmlElement, StringBuilder>();
        int maxDepth = _options.EffectiveMaxDepth;

        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c != '<')
            {
                int end = _s.IndexOf('<', _pos);
                if (end < 0) end = _s.Length;
                if (stack.Count == 0)
                {
                    for (int i = _pos; i < end; i++)
                        if (!IsWhitespace(_s[i])) throw Error("Text is not allowed outside the root element.", i);
                }
                else
                {
                    var top = stack[stack.Count - 1];
                    if (textBuilders.TryGetValue(top, out var sb)) DecodeInto(sb, _pos, end, false);
                }
                _pos = end;
                continue;
            }

            if (StartsWith("<?"))
            {
                int end = _s.IndexOf("?>", _pos + 2, StringComparison.Ordinal);
                if (end < 0) throw Error("Unterminated processing instruction.", _pos);
                _pos = end + 2;
            }
            else if (StartsWith("<!--"))
            {
                int end = _s.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
                if (end < 0) throw Error("Unterminated comment.", _pos);
                _pos = end + 3;
            }
            else if (StartsWith("<![CDATA["))
            {
                if (stack.Count == 0) throw Error("CDATA is not allowed outside the root element.", _pos);
                int start = _pos + 9;
                int end = _s.IndexOf("]]>", start, StringComparison.Ordinal);
                if (end < 0) throw Error("Unterminated CDATA section.", _pos);
                if (textBuilders.TryGetValue(stack[stack.Count - 1], out var sb)) sb.Append(_s, start, end - start);
                _pos = end + 3;
            }
            else if (StartsWith("<!DOCTYPE"))
            {
                if (root != null) throw Error("DOCTYPE must precede the root element.", _pos);
                _diag.Add(SvgDiagnosticCode.DoctypeIgnored, "DOCTYPE was skipped; entity declarations are not expanded.", _pos);
                SkipDoctype();
            }
            else if (StartsWith("<!"))
            {
                throw Error("Unsupported markup declaration.", _pos);
            }
            else if (StartsWith("</"))
            {
                int tagStart = _pos;
                _pos += 2;
                var (prefix, name) = ReadQName();
                SkipWhitespace();
                Expect('>');
                if (stack.Count == 0) throw Error("Unexpected end tag.", tagStart);
                var top = stack[stack.Count - 1];
                if (!ReferenceEquals(top.Name, name) && top.Name != name || top.Prefix != prefix)
                    throw Error($"End tag '{name}' does not match start tag '{top.Name}'.", tagStart);
                if (textBuilders.TryGetValue(top, out var sb))
                {
                    top.Text = sb.ToString();
                    textBuilders.Remove(top);
                }
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) rootClosed = true;
            }
            else
            {
                if (rootClosed) throw Error("Only one root element is allowed.", _pos);
                var el = ReadStartTag(out bool selfClosing);
                if (++_elementCount > _options.MaxElements)
                    throw Error($"The document has more than {_options.MaxElements} elements.", el.Offset);
                if (stack.Count + 1 > maxDepth)
                    throw Error($"Elements are nested deeper than {maxDepth} levels.", el.Offset);
                if (stack.Count == 0) root = el;
                else
                {
                    var parent = stack[stack.Count - 1];
                    el.Parent = parent;
                    (parent.Children ??= new List<XmlElement>()).Add(el);
                }
                if (selfClosing)
                {
                    if (stack.Count == 0) rootClosed = true;
                    if (el.Name == "style") el.Text = string.Empty;
                }
                else
                {
                    stack.Add(el);
                    if (el.Name == "style") textBuilders[el] = new StringBuilder();
                }
            }
        }

        if (stack.Count > 0) throw Error($"Element '{stack[stack.Count - 1].Name}' is not closed.", stack[stack.Count - 1].Offset);
        if (root == null) throw Error("The document has no root element.", _pos);
        return root;
    }

    bool StartsWith(string token) => string.CompareOrdinal(_s, _pos, token, 0, token.Length) == 0;

    static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';

    void SkipWhitespace()
    {
        while (_pos < _s.Length && IsWhitespace(_s[_pos])) _pos++;
    }

    void Expect(char c)
    {
        if (_pos >= _s.Length || _s[_pos] != c) throw Error($"Expected '{c}'.", _pos);
        _pos++;
    }

    static bool IsNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or ':' || c >= 0x80;
    static bool IsNameChar(char c) => IsNameStart(c) || c is >= '0' and <= '9' or '-' or '.';

    (string? Prefix, string Name) ReadQName()
    {
        int start = _pos;
        if (_pos >= _s.Length || !IsNameStart(_s[_pos])) throw Error("Expected a name.", _pos);
        int colon = -1;
        while (_pos < _s.Length && IsNameChar(_s[_pos]))
        {
            if (_s[_pos] == ':' && colon < 0) colon = _pos;
            _pos++;
        }
        if (colon > start && colon < _pos - 1)
            return (_names.Get(_s, start, colon - start), _names.Get(_s, colon + 1, _pos - colon - 1));
        return (null, _names.Get(_s, start, _pos - start));
    }

    XmlElement ReadStartTag(out bool selfClosing)
    {
        int tagStart = _pos;
        _pos++; // '<'
        var (prefix, name) = ReadQName();
        _attrs.Clear();
        _attrNames.Clear();
        selfClosing = false;
        while (true)
        {
            int before = _pos;
            SkipWhitespace();
            if (_pos >= _s.Length) throw Error("Unterminated start tag.", tagStart);
            char c = _s[_pos];
            if (c == '>') { _pos++; break; }
            if (c == '/')
            {
                _pos++;
                Expect('>');
                selfClosing = true;
                break;
            }
            if (_pos == before) throw Error("Expected whitespace between attributes.", _pos);
            int attrStart = _pos;
            var (aPrefix, aName) = ReadQName();
            SkipWhitespace();
            Expect('=');
            SkipWhitespace();
            if (_pos >= _s.Length || _s[_pos] is not ('"' or '\'')) throw Error("Expected a quoted attribute value.", _pos);
            char q = _s[_pos++];
            int vEnd = _s.IndexOf(q, _pos);
            if (vEnd < 0) throw Error("Unterminated attribute value.", attrStart);
            string value = DecodeAttribute(_pos, vEnd);
            _pos = vEnd + 1;

            if (_attrs.Count >= _options.MaxAttributesPerElement)
                throw Error($"Element '{name}' has more than {_options.MaxAttributesPerElement} attributes.", attrStart);
            string key = aPrefix == null ? aName : aPrefix + ":" + aName;
            if (!_attrNames.Add(key)) throw Error($"Duplicate attribute '{key}'.", attrStart);
            _attrs.Add(new XmlAttr(aPrefix, aName, value, attrStart));
        }
        return new XmlElement(prefix, name, _attrs.Count == 0 ? Array.Empty<XmlAttr>() : _attrs.ToArray(), tagStart);
    }

    string DecodeAttribute(int start, int end)
    {
        bool plain = true;
        for (int i = start; i < end; i++)
        {
            char c = _s[i];
            if (c == '<') throw Error("'<' is not allowed in attribute values.", i);
            if (c is '&' or '\t' or '\n' or '\r') plain = false;
        }
        if (plain) return start == end ? string.Empty : _s.Substring(start, end - start);
        _sb.Clear();
        DecodeInto(_sb, start, end, true);
        return _sb.ToString();
    }

    /// <summary>解码实体与字符引用。属性值中的空白字符按 XML 规范规范化为空格。</summary>
    void DecodeInto(StringBuilder sb, int start, int end, bool attribute)
    {
        int i = start;
        while (i < end)
        {
            char c = _s[i];
            if (c == '&')
            {
                int semi = _s.IndexOf(';', i + 1, Math.Min(end - i - 1, 12));
                if (semi > i + 1 && TryDecodeReference(sb, i + 1, semi))
                {
                    i = semi + 1;
                    continue;
                }
                int len = semi > i ? semi - i + 1 : 1;
                _diag.Add(SvgDiagnosticCode.UndefinedEntity,
                    $"Undefined or malformed entity reference '{_s.Substring(i, Math.Min(len, 16))}' was kept as text.", i);
                sb.Append('&');
                i++;
                continue;
            }
            if (attribute && c is '\t' or '\n' or '\r') c = ' ';
            sb.Append(c);
            i++;
        }
    }

    bool TryDecodeReference(StringBuilder sb, int start, int end)
    {
        int len = end - start;
        if (_s[start] == '#')
        {
            bool hex = len > 1 && (_s[start + 1] == 'x');
            int i = start + (hex ? 2 : 1);
            if (i >= end) return false;
            int cp = 0;
            for (; i < end; i++)
            {
                char c = _s[i];
                int d = c is >= '0' and <= '9' ? c - '0'
                    : hex && c is >= 'a' and <= 'f' ? c - 'a' + 10
                    : hex && c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                if (d < 0) return false;
                cp = cp * (hex ? 16 : 10) + d;
                if (cp > 0x10FFFF) return false;
            }
            if (cp == 0 || cp is >= 0xD800 and <= 0xDFFF) return false;
            if (cp < 0x10000) sb.Append((char)cp);
            else sb.Append(char.ConvertFromUtf32(cp));
            return true;
        }
        string? rep = len switch
        {
            2 when Is(start, "lt") => "<",
            2 when Is(start, "gt") => ">",
            3 when Is(start, "amp") => "&",
            4 when Is(start, "quot") => "\"",
            4 when Is(start, "apos") => "'",
            _ => null,
        };
        if (rep == null) return false;
        sb.Append(rep);
        return true;
    }

    bool Is(int start, string token) => string.CompareOrdinal(_s, start, token, 0, token.Length) == 0;

    /// <summary>跳过整个 DOCTYPE（含内部子集中的字符串、注释与方括号），线性扫描。</summary>
    void SkipDoctype()
    {
        int start = _pos;
        _pos += 9;
        int bracket = 0;
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c is '"' or '\'')
            {
                int end = _s.IndexOf(c, _pos + 1);
                if (end < 0) break;
                _pos = end + 1;
                continue;
            }
            if (c == '<' && StartsWith("<!--"))
            {
                int end = _s.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
                if (end < 0) break;
                _pos = end + 3;
                continue;
            }
            if (c == '[') bracket++;
            else if (c == ']') bracket = Math.Max(0, bracket - 1);
            else if (c == '>' && bracket == 0) { _pos++; return; }
            _pos++;
        }
        throw Error("Unterminated DOCTYPE.", start);
    }
}

/// <summary>字节输入 → 字符串：识别 BOM 与 XML 声明中的 encoding。</summary>
internal static class XmlEncoding
{
    public static unsafe string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return GetString(Encoding.UTF8, bytes.Slice(3));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return GetString(Encoding.Unicode, bytes.Slice(2));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return GetString(Encoding.BigEndianUnicode, bytes.Slice(2));
        if (DeclaresLatin1(bytes))
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
        return GetString(Encoding.UTF8, bytes);
    }

    static unsafe string GetString(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        fixed (byte* p = bytes) return encoding.GetString(p, bytes.Length);
    }

    /// <summary>XML 声明是否指定了单字节的 Latin-1 系编码（只看前 128 字节）。</summary>
    static bool DeclaresLatin1(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || bytes[0] != '<' || bytes[1] != '?') return false;
        int n = Math.Min(bytes.Length, 128);
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            if (bytes[i] == '>') break;
            sb.Append(char.ToLowerInvariant((char)bytes[i]));
        }
        var decl = sb.ToString();
        int e = decl.IndexOf("encoding", StringComparison.Ordinal);
        if (e < 0) return false;
        var rest = decl.Substring(e + 8);
        return rest.Contains("iso-8859-1") || rest.Contains("latin1") || rest.Contains("windows-1252") ||
               rest.Contains("us-ascii") || rest.Contains("latin-1");
    }
}

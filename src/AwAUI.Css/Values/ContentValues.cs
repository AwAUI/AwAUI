using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary><c>content</c> 的种类。</summary>
public enum CssContentKind : byte
{
    /// <summary><c>normal</c>（对 <c>::before / ::after</c> 等同 none）。</summary>
    Normal,
    None,
    /// <summary>内容列表（或替换图像），见 <see cref="CssContent.Items"/>。</summary>
    List,
}

/// <summary><c>content</c> 列表项的种类。</summary>
public enum CssContentItemKind : byte
{
    String,
    Image,
    Counter,
    Counters,
    OpenQuote,
    CloseQuote,
    NoOpenQuote,
    NoCloseQuote,
}

/// <summary><c>content</c> 的一项。<c>attr()</c> 在计算值阶段已替换为字符串（取原始元素的属性）。</summary>
public readonly struct CssContentItem : IEquatable<CssContentItem>
{
    public CssContentItem(CssContentItemKind kind, string? text, string? separator = null, string? counterStyle = null, CssImage? image = null)
    {
        Kind = kind;
        Text = text;
        Separator = separator;
        CounterStyle = counterStyle;
        Image = image;
    }

    public CssContentItemKind Kind { get; }

    /// <summary>字符串内容，或计数器名。</summary>
    public string? Text { get; }

    /// <summary><c>counters()</c> 的分隔字符串。</summary>
    public string? Separator { get; }

    /// <summary><c>counter()/counters()</c> 的计数器样式（省略为 decimal）。</summary>
    public string? CounterStyle { get; }

    public CssImage? Image { get; }

    public bool Equals(CssContentItem o) => Kind == o.Kind && Text == o.Text && Separator == o.Separator && CounterStyle == o.CounterStyle && Equals(Image, o.Image);
    public override bool Equals(object? obj) => obj is CssContentItem i && Equals(i);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Text?.GetHashCode() ?? 0, Image?.GetHashCode() ?? 0);

    public override string ToString() => Kind switch
    {
        CssContentItemKind.String => CssSerializer.SerializeString(Text ?? string.Empty),
        CssContentItemKind.Image => Image?.ToString() ?? "none",
        CssContentItemKind.Counter => "counter(" + Text + (CounterStyle is null ? "" : ", " + CounterStyle) + ")",
        CssContentItemKind.Counters => "counters(" + Text + ", " + CssSerializer.SerializeString(Separator ?? string.Empty) + (CounterStyle is null ? "" : ", " + CounterStyle) + ")",
        CssContentItemKind.OpenQuote => "open-quote",
        CssContentItemKind.CloseQuote => "close-quote",
        CssContentItemKind.NoOpenQuote => "no-open-quote",
        _ => "no-close-quote",
    };
}

/// <summary><c>content</c> 的计算值。</summary>
public sealed class CssContent : IEquatable<CssContent>
{
    public static readonly CssContent Normal = new(CssContentKind.Normal, Array.Empty<CssContentItem>(), null);
    public static readonly CssContent None = new(CssContentKind.None, Array.Empty<CssContentItem>(), null);

    internal CssContent(CssContentKind kind, CssContentItem[] items, string? altText)
    {
        Kind = kind;
        _items = items;
        AltText = altText;
    }

    readonly CssContentItem[] _items;

    public CssContentKind Kind { get; }

    public IReadOnlyList<CssContentItem> Items => _items;

    /// <summary><c>/</c> 之后的替代文本（无障碍）；没有则为 null。</summary>
    public string? AltText { get; }

    /// <summary>
    /// 只含字符串（与已替换的 <c>attr()</c>）时连接后的文本，否则为 null。Tailwind 的 <c>content: var(--tw-content)</c> 属于这种情况。
    /// </summary>
    public string? PlainText
    {
        get
        {
            if (Kind != CssContentKind.List) return null;
            var sb = new StringBuilder();
            foreach (var i in _items)
            {
                if (i.Kind != CssContentItemKind.String) return null;
                sb.Append(i.Text);
            }
            return sb.ToString();
        }
    }

    public bool Equals(CssContent? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Kind != o.Kind || AltText != o.AltText || _items.Length != o._items.Length) return false;
        for (int i = 0; i < _items.Length; i++) if (!_items[i].Equals(o._items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssContent c && Equals(c);

    public override int GetHashCode()
    {
        int h = (int)Kind;
        foreach (var i in _items) h = HashCodeCompat.Combine(h, i.GetHashCode());
        return h;
    }

    public override string ToString()
    {
        if (Kind == CssContentKind.Normal) return "normal";
        if (Kind == CssContentKind.None) return "none";
        var s = string.Join(" ", (object[])Array.ConvertAll(_items, i => (object)i));
        return AltText is null ? s : s + " / " + CssSerializer.SerializeString(AltText);
    }
}

/// <summary><c>quotes</c> 的计算值：<c>auto</c>、<c>none</c> 或成对的引号字符串。</summary>
public sealed class CssQuotes : IEquatable<CssQuotes>
{
    public static readonly CssQuotes Auto = new(true, Array.Empty<string>());
    public static readonly CssQuotes None = new(false, Array.Empty<string>());

    internal CssQuotes(bool auto, string[] strings)
    {
        IsAuto = auto;
        _strings = strings;
    }

    readonly string[] _strings;

    public bool IsAuto { get; }

    /// <summary>按「开、闭」成对排列的引号。</summary>
    public IReadOnlyList<string> Strings => _strings;

    public bool Equals(CssQuotes? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || IsAuto != o.IsAuto || _strings.Length != o._strings.Length) return false;
        for (int i = 0; i < _strings.Length; i++) if (_strings[i] != o._strings[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssQuotes q && Equals(q);
    public override int GetHashCode() => HashCodeCompat.Combine(IsAuto ? 1 : 0, _strings.Length);

    public override string ToString()
    {
        if (IsAuto) return "auto";
        if (_strings.Length == 0) return "none";
        return string.Join(" ", Array.ConvertAll(_strings, CssSerializer.SerializeString));
    }
}

/// <summary><c>list-style-type</c> 的计算值：<c>none</c>、计数器样式名（如 <c>disc</c>、<c>decimal</c>），或字符串标记。</summary>
public readonly struct CssListStyleType : IEquatable<CssListStyleType>
{
    public CssListStyleType(string? counterStyle, string? text)
    {
        CounterStyle = counterStyle;
        Text = text;
    }

    public static CssListStyleType None => default;

    /// <summary>计数器样式名（ASCII 小写的预定义名或原样的自定义名）；字符串标记或 none 时为 null。</summary>
    public string? CounterStyle { get; }

    /// <summary>字符串标记（<c>list-style-type: "→ "</c>）。</summary>
    public string? Text { get; }

    public bool IsNone => CounterStyle is null && Text is null;

    public bool Equals(CssListStyleType o) => CounterStyle == o.CounterStyle && Text == o.Text;
    public override bool Equals(object? obj) => obj is CssListStyleType t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine(CounterStyle?.GetHashCode() ?? 0, Text?.GetHashCode() ?? 0);
    public static bool operator ==(CssListStyleType a, CssListStyleType b) => a.Equals(b);
    public static bool operator !=(CssListStyleType a, CssListStyleType b) => !a.Equals(b);
    public override string ToString() => Text is not null ? CssSerializer.SerializeString(Text) : CounterStyle ?? "none";
}

/// <summary><c>content</c>、<c>quotes</c>、<c>list-style-*</c> 的解析与计算。</summary>
internal static class ContentParsers
{
    internal sealed class ContentValue : CssValue
    {
        public ContentValue(CssContentKind kind, SpecifiedItem[] items, string? alt, CssContent? computed)
        {
            Kind = kind;
            Items = items;
            Alt = alt;
            Computed = computed;
        }

        public CssContentKind Kind { get; }
        public SpecifiedItem[] Items { get; }
        public string? Alt { get; }
        public CssContent? Computed { get; }
    }

    internal readonly struct SpecifiedItem
    {
        public SpecifiedItem(CssContentItemKind kind, string? text, string? separator, string? style, SpecifiedImage? image, string? attr, string? attrFallback)
        {
            Kind = kind;
            Text = text;
            Separator = separator;
            Style = style;
            Image = image;
            Attr = attr;
            AttrFallback = attrFallback;
        }

        public CssContentItemKind Kind { get; }
        public string? Text { get; }
        public string? Separator { get; }
        public string? Style { get; }
        public SpecifiedImage? Image { get; }

        /// <summary><c>attr()</c> 的属性名；非 attr 项为 null。</summary>
        public string? Attr { get; }
        public string? AttrFallback { get; }
    }

    static readonly CssEnvironment s_env = new();

    public static CssValue? Content(ref ValueReader r)
    {
        if (r.TryIdent("normal")) return new ContentValue(CssContentKind.Normal, Array.Empty<SpecifiedItem>(), null, CssContent.Normal);
        if (r.TryIdent("none")) return new ContentValue(CssContentKind.None, Array.Empty<SpecifiedItem>(), null, CssContent.None);
        var items = new List<SpecifiedItem>();
        while (!r.AtEnd && !(r.Peek() is CssToken d && d.IsDelim('/')))
        {
            if (items.Count >= 1024) return null;
            var item = Item(r.Next()!);
            if (item is null) return null;
            items.Add(item.Value);
        }
        if (items.Count == 0) return null;
        string? alt = null;
        if (r.TryDelim('/'))
        {
            var sb = new StringBuilder();
            bool any = false;
            while (!r.AtEnd)
            {
                if (r.Next() is CssToken { Type: CssTokenType.String } s) { sb.Append(s.Value); any = true; }
                else return null; // counter() / attr() 作替代文本：只解析字符串
            }
            if (!any) return null;
            alt = sb.ToString();
        }
        var arr = items.ToArray();
        bool dependent = false;
        foreach (var i in arr) if (i.Attr is not null || i.Image is { IsContextDependent: true }) dependent = true;
        return new ContentValue(CssContentKind.List, arr, alt, dependent ? null : Compute(arr, alt, null, new LengthContext(16, 16, s_env)));
    }

    static SpecifiedItem? Item(CssComponentValue v)
    {
        switch (v)
        {
            case CssToken { Type: CssTokenType.String } s:
                return new SpecifiedItem(CssContentItemKind.String, s.Value, null, null, null, null, null);
            case CssToken { Type: CssTokenType.Ident } id:
                return CssSerializer.ToAsciiLower(id.Value) switch
                {
                    "open-quote" => new SpecifiedItem(CssContentItemKind.OpenQuote, null, null, null, null, null, null),
                    "close-quote" => new SpecifiedItem(CssContentItemKind.CloseQuote, null, null, null, null, null, null),
                    "no-open-quote" => new SpecifiedItem(CssContentItemKind.NoOpenQuote, null, null, null, null, null, null),
                    "no-close-quote" => new SpecifiedItem(CssContentItemKind.NoCloseQuote, null, null, null, null, null, null),
                    _ => null,
                };
            case CssFunction f:
            {
                var name = CssSerializer.ToAsciiLower(f.Name);
                var r = new ValueReader(f.Arguments);
                switch (name)
                {
                    case "counter":
                    {
                        if (r.Next() is not CssToken { Type: CssTokenType.Ident } c || IsReserved(c.Value)) return null;
                        string? style = null;
                        if (r.TryComma())
                        {
                            if (r.Next() is not CssToken { Type: CssTokenType.Ident } st) return null;
                            style = st.Value;
                        }
                        return r.AtEnd ? new SpecifiedItem(CssContentItemKind.Counter, c.Value, null, style, null, null, null) : null;
                    }
                    case "counters":
                    {
                        if (r.Next() is not CssToken { Type: CssTokenType.Ident } c || IsReserved(c.Value)) return null;
                        if (!r.TryComma() || r.Next() is not CssToken { Type: CssTokenType.String } sep) return null;
                        string? style = null;
                        if (r.TryComma())
                        {
                            if (r.Next() is not CssToken { Type: CssTokenType.Ident } st) return null;
                            style = st.Value;
                        }
                        return r.AtEnd ? new SpecifiedItem(CssContentItemKind.Counters, c.Value, sep.Value, style, null, null, null) : null;
                    }
                    case "attr":
                    {
                        // attr( <attr-name> <attr-type>? , <declaration-value>? )：只支持字符串结果（CSS 2 的 content 语义）
                        if (r.Next() is not CssToken { Type: CssTokenType.Ident } a) return null;
                        if (r.PeekIdentLower() is "string" or "raw-string") r.Advance();
                        string? fallback = null;
                        if (r.TryComma())
                        {
                            if (r.AtEnd) fallback = string.Empty;
                            else if (r.Next() is CssToken { Type: CssTokenType.String } fb) fallback = fb.Value;
                            else return null;
                        }
                        return r.AtEnd ? new SpecifiedItem(CssContentItemKind.String, null, null, null, null, a.Value, fallback) : null;
                    }
                    default:
                    {
                        var img = ValueParsers.Image(f);
                        return img is null ? null : new SpecifiedItem(CssContentItemKind.Image, null, null, null, img, null, null);
                    }
                }
            }
            case CssToken { Type: CssTokenType.Url }:
            {
                var img = ValueParsers.Image(v);
                return img is null ? null : new SpecifiedItem(CssContentItemKind.Image, null, null, null, img, null, null);
            }
            default:
                return null;
        }

        static bool IsReserved(string n)
        {
            var l = CssSerializer.ToAsciiLower(n);
            return l == "none" || CssWideKeywordValue.TryParse(l, out _);
        }
    }

    internal static CssContent Compute(SpecifiedItem[] items, string? alt, Selectors.StyleElement? element, in LengthContext ctx)
    {
        var result = new CssContentItem[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (it.Attr is not null)
            {
                var value = element?.GetAttribute(it.Attr);
                result[i] = new CssContentItem(CssContentItemKind.String, value ?? it.AttrFallback ?? string.Empty);
            }
            else if (it.Kind == CssContentItemKind.Image) result[i] = new CssContentItem(CssContentItemKind.Image, null, image: it.Image!.Compute(ctx));
            else result[i] = new CssContentItem(it.Kind, it.Text, it.Separator, it.Style);
        }
        return new CssContent(CssContentKind.List, result, alt);
    }

    public static CssContent ComputeContent(CssValue v, Selectors.StyleElement? element, in LengthContext ctx)
    {
        var c = (ContentValue)v;
        return c.Computed ?? Compute(c.Items, c.Alt, element, ctx);
    }

    /// <summary><c>quotes: auto | none | [ &lt;string&gt; &lt;string&gt; ]+</c></summary>
    public static CssValue? Quotes(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return new PrecomputedValue<CssQuotes>(CssQuotes.Auto);
        if (r.TryIdent("none")) return new PrecomputedValue<CssQuotes>(CssQuotes.None);
        var list = new List<string>();
        while (!r.AtEnd)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.String } s || list.Count > 2048) return null;
            list.Add(s.Value);
        }
        if (list.Count == 0 || list.Count % 2 != 0) return null;
        return new PrecomputedValue<CssQuotes>(new CssQuotes(false, list.ToArray()));
    }

    static readonly HashSet<string> PredefinedCounterStyles = new(StringComparer.Ordinal)
    {
        "disc", "circle", "square", "decimal", "decimal-leading-zero", "lower-roman", "upper-roman", "lower-greek", "lower-alpha", "lower-latin",
        "upper-alpha", "upper-latin", "armenian", "georgian", "lower-armenian", "upper-armenian", "hebrew", "cjk-decimal", "cjk-ideographic",
        "hiragana", "katakana", "hiragana-iroha", "katakana-iroha", "disclosure-open", "disclosure-closed", "arabic-indic", "bengali",
        "devanagari", "japanese-informal", "japanese-formal", "korean-hangul-formal", "simp-chinese-informal", "simp-chinese-formal",
        "trad-chinese-informal", "trad-chinese-formal", "ethiopic-numeric", "persian", "thai", "tamil",
    };

    /// <summary><c>list-style-type: &lt;counter-style&gt; | &lt;string&gt; | none</c></summary>
    public static CssValue? ListStyleType(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.String } s) return new PrecomputedValue<CssListStyleType>(new CssListStyleType(null, s.Value));
        if (v is not CssToken { Type: CssTokenType.Ident } id) return null;
        var lower = CssSerializer.ToAsciiLower(id.Value);
        if (lower == "none") return new PrecomputedValue<CssListStyleType>(CssListStyleType.None);
        if (lower == "default" || CssWideKeywordValue.TryParse(lower, out _)) return null;
        return new PrecomputedValue<CssListStyleType>(new CssListStyleType(PredefinedCounterStyles.Contains(lower) ? lower : id.Value, null));
    }
}

/// <summary><c>list-style</c> 简写。</summary>
internal static partial class Shorthands
{
    /// <summary>
    /// <c>list-style: &lt;'list-style-position'&gt; || &lt;'list-style-image'&gt; || &lt;'list-style-type'&gt;</c>。
    /// <c>none</c> 给未出现的 image 与 type（都未出现时两者都是 none）。长属性顺序：type、position、image。
    /// </summary>
    public static bool ListStyle(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        CssValue? type = null, position = null, image = null;
        int nones = 0;
        if (r.AtEnd) return false;
        while (!r.AtEnd)
        {
            if (r.TryIdent("none")) { nones++; continue; }
            CssValue? v;
            if (position is null && (v = Parse(longhands[1], ref r)) is not null) { position = v; continue; }
            if (image is null && (v = Parse(longhands[2], ref r)) is not null) { image = v; continue; }
            if (type is null && (v = Parse(longhands[0], ref r)) is not null) { type = v; continue; }
            return false;
        }
        var noneType = new PrecomputedValue<CssListStyleType>(CssListStyleType.None);
        var noneImage = PropertyParsers.ParseLonghand(longhands[2], CssComponentValue.ParseList("none"))!;
        if (nones > 0)
        {
            if (type is null && image is null) { if (nones > 2) return false; type = noneType; image = noneImage; }
            else if (type is null) { if (nones > 1) return false; type = noneType; }
            else if (image is null) { if (nones > 1) return false; image = noneImage; }
            else return false;
        }
        output.Add(new PropertyValue(longhands[0], type ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[1], position ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[2], image ?? CssWideKeywordValue.Initial));
        return true;
    }
}

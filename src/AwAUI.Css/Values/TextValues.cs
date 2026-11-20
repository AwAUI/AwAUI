using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary><c>vertical-align</c> 的关键字；<see cref="Length"/> 表示长度百分比。</summary>
public enum CssVerticalAlignKind : byte
{
    Baseline,
    Sub,
    Super,
    TextTop,
    TextBottom,
    Middle,
    Top,
    Bottom,
    Length,
}

/// <summary><c>vertical-align</c> 的计算值（百分比相对 line-height，保留到布局）。</summary>
public readonly struct CssVerticalAlign : IEquatable<CssVerticalAlign>
{
    public CssVerticalAlign(CssVerticalAlignKind kind, LengthPercentage length = default)
    {
        Kind = kind;
        Length = length;
    }

    public CssVerticalAlignKind Kind { get; }
    public LengthPercentage Length { get; }

    public bool Equals(CssVerticalAlign o) => Kind == o.Kind && Length.Equals(o.Length);
    public override bool Equals(object? obj) => obj is CssVerticalAlign v && Equals(v);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Length.GetHashCode());
    public static bool operator ==(CssVerticalAlign a, CssVerticalAlign b) => a.Equals(b);
    public static bool operator !=(CssVerticalAlign a, CssVerticalAlign b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        CssVerticalAlignKind.Baseline => "baseline",
        CssVerticalAlignKind.Sub => "sub",
        CssVerticalAlignKind.Super => "super",
        CssVerticalAlignKind.TextTop => "text-top",
        CssVerticalAlignKind.TextBottom => "text-bottom",
        CssVerticalAlignKind.Middle => "middle",
        CssVerticalAlignKind.Top => "top",
        CssVerticalAlignKind.Bottom => "bottom",
        _ => Length.ToString(),
    };
}

/// <summary><c>text-indent</c> 的计算值：长度百分比（相对包含块宽度）+ <c>hanging</c> / <c>each-line</c>。</summary>
public readonly struct CssTextIndent : IEquatable<CssTextIndent>
{
    public CssTextIndent(LengthPercentage length, bool hanging, bool eachLine)
    {
        Length = length;
        Hanging = hanging;
        EachLine = eachLine;
    }

    public LengthPercentage Length { get; }
    public bool Hanging { get; }
    public bool EachLine { get; }

    public bool Equals(CssTextIndent o) => Length.Equals(o.Length) && Hanging == o.Hanging && EachLine == o.EachLine;
    public override bool Equals(object? obj) => obj is CssTextIndent t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine(Length.GetHashCode(), Hanging ? 1 : 0, EachLine ? 1 : 0);
    public static bool operator ==(CssTextIndent a, CssTextIndent b) => a.Equals(b);
    public static bool operator !=(CssTextIndent a, CssTextIndent b) => !a.Equals(b);
    public override string ToString() => Length + (Hanging ? " hanging" : "") + (EachLine ? " each-line" : "");
}

/// <summary><c>tab-size</c> 的计算值：空格数（<see cref="IsLength"/> 为假）或长度（px）。</summary>
public readonly struct CssTabSize : IEquatable<CssTabSize>
{
    public CssTabSize(float value, bool isLength)
    {
        Value = value;
        IsLength = isLength;
    }

    public float Value { get; }
    public bool IsLength { get; }

    public bool Equals(CssTabSize o) => Value == o.Value && IsLength == o.IsLength;
    public override bool Equals(object? obj) => obj is CssTabSize t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine(Value.GetHashCode(), IsLength ? 1 : 0);
    public static bool operator ==(CssTabSize a, CssTabSize b) => a.Equals(b);
    public static bool operator !=(CssTabSize a, CssTabSize b) => !a.Equals(b);
    public override string ToString() => IsLength ? ValueText.Px(Value) : ValueText.Number(Value);
}

/// <summary><c>text-decoration-thickness</c> 的种类。</summary>
public enum CssDecorationThicknessKind : byte
{
    Auto,
    FromFont,
    Length,
}

/// <summary><c>text-decoration-thickness</c> 的计算值（百分比相对 1em，保留）。</summary>
public readonly struct CssDecorationThickness : IEquatable<CssDecorationThickness>
{
    public CssDecorationThickness(CssDecorationThicknessKind kind, LengthPercentage length = default)
    {
        Kind = kind;
        Length = length;
    }

    public CssDecorationThicknessKind Kind { get; }
    public LengthPercentage Length { get; }

    public bool Equals(CssDecorationThickness o) => Kind == o.Kind && Length.Equals(o.Length);
    public override bool Equals(object? obj) => obj is CssDecorationThickness t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Length.GetHashCode());
    public static bool operator ==(CssDecorationThickness a, CssDecorationThickness b) => a.Equals(b);
    public static bool operator !=(CssDecorationThickness a, CssDecorationThickness b) => !a.Equals(b);
    public override string ToString() => Kind switch { CssDecorationThicknessKind.Auto => "auto", CssDecorationThicknessKind.FromFont => "from-font", _ => Length.ToString() };
}

/// <summary><c>text-decoration-line</c>。</summary>
[Flags]
public enum CssTextDecorationLine : byte
{
    None = 0,
    Underline = 1,
    Overline = 2,
    LineThrough = 4,
    Blink = 8,
    SpellingError = 16,
    GrammarError = 32,
}

/// <summary><c>font-variant-numeric</c>。</summary>
[Flags]
public enum CssFontVariantNumeric : byte
{
    Normal = 0,
    LiningNums = 1,
    OldstyleNums = 2,
    ProportionalNums = 4,
    TabularNums = 8,
    DiagonalFractions = 16,
    StackedFractions = 32,
    Ordinal = 64,
    SlashedZero = 128,
}

/// <summary><c>text-underline-position</c>。</summary>
[Flags]
public enum CssTextUnderlinePosition : byte
{
    Auto = 0,
    FromFont = 1,
    Under = 2,
    Left = 4,
    Right = 8,
}

/// <summary><c>color-scheme</c>（自定义标识符被接受但忽略）。</summary>
[Flags]
public enum CssColorScheme : byte
{
    Normal = 0,
    Light = 1,
    Dark = 2,
    Only = 4,
}

/// <summary><c>font-feature-settings</c> 的一项：OpenType 特性标签（4 个 ASCII 字符）与值。</summary>
public readonly struct CssFontFeature : IEquatable<CssFontFeature>
{
    public CssFontFeature(string tag, int value)
    {
        Tag = tag;
        Value = value;
    }

    public string Tag { get; }
    public int Value { get; }

    public bool Equals(CssFontFeature o) => Tag == o.Tag && Value == o.Value;
    public override bool Equals(object? obj) => obj is CssFontFeature f && Equals(f);
    public override int GetHashCode() => HashCodeCompat.Combine(Tag.GetHashCode(), Value);
    public override string ToString() => CssSerializer.SerializeString(Tag) + (Value == 1 ? "" : " " + Value.ToString(CultureInfo.InvariantCulture));
}

/// <summary><c>font-variation-settings</c> 的一项：变体轴标签与值。</summary>
public readonly struct CssFontVariation : IEquatable<CssFontVariation>
{
    public CssFontVariation(string tag, float value)
    {
        Tag = tag;
        Value = value;
    }

    public string Tag { get; }
    public float Value { get; }

    public bool Equals(CssFontVariation o) => Tag == o.Tag && Value == o.Value;
    public override bool Equals(object? obj) => obj is CssFontVariation f && Equals(f);
    public override int GetHashCode() => HashCodeCompat.Combine(Tag.GetHashCode(), Value.GetHashCode());
    public override string ToString() => CssSerializer.SerializeString(Tag) + " " + ValueText.Number(Value);
}

/// <summary><c>border-spacing</c> 的计算值（px）。</summary>
public readonly struct CssBorderSpacing : IEquatable<CssBorderSpacing>
{
    public CssBorderSpacing(float horizontal, float vertical)
    {
        Horizontal = horizontal;
        Vertical = vertical;
    }

    public float Horizontal { get; }
    public float Vertical { get; }

    public bool Equals(CssBorderSpacing o) => Horizontal == o.Horizontal && Vertical == o.Vertical;
    public override bool Equals(object? obj) => obj is CssBorderSpacing s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Horizontal.GetHashCode(), Vertical.GetHashCode());
    public static bool operator ==(CssBorderSpacing a, CssBorderSpacing b) => a.Equals(b);
    public static bool operator !=(CssBorderSpacing a, CssBorderSpacing b) => !a.Equals(b);
    public override string ToString() => Horizontal == Vertical ? ValueText.Px(Horizontal) : ValueText.Px(Horizontal) + " " + ValueText.Px(Vertical);
}

/// <summary>SVG 的 <c>fill</c> / <c>stroke</c> 的种类。</summary>
public enum CssSvgPaintKind : byte
{
    None,
    Color,
    Url,
    ContextFill,
    ContextStroke,
}

/// <summary><c>fill</c> / <c>stroke</c> 的计算值：<c>none</c>、颜色、<c>url()</c>（带回退颜色）或 context-fill / context-stroke。</summary>
public readonly struct CssSvgPaint : IEquatable<CssSvgPaint>
{
    public CssSvgPaint(CssSvgPaintKind kind, CssColor color = default, string? url = null)
    {
        Kind = kind;
        Color = color;
        Url = url;
    }

    public CssSvgPaintKind Kind { get; }

    /// <summary>颜色（<c>url()</c> 的回退颜色；没有回退为透明）。</summary>
    public CssColor Color { get; }

    public string? Url { get; }

    public bool Equals(CssSvgPaint o) => Kind == o.Kind && Color.Equals(o.Color) && Url == o.Url;
    public override bool Equals(object? obj) => obj is CssSvgPaint p && Equals(p);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Color.GetHashCode(), Url?.GetHashCode() ?? 0);
    public static bool operator ==(CssSvgPaint a, CssSvgPaint b) => a.Equals(b);
    public static bool operator !=(CssSvgPaint a, CssSvgPaint b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        CssSvgPaintKind.None => "none",
        CssSvgPaintKind.Color => Color.ToString(),
        CssSvgPaintKind.Url => "url(" + CssSerializer.SerializeString(Url ?? string.Empty) + ")",
        CssSvgPaintKind.ContextFill => "context-fill",
        _ => "context-stroke",
    };
}

/// <summary><c>auto | &lt;color&gt;</c>（<c>accent-color</c>、<c>caret-color</c>）。</summary>
public readonly struct CssAutoColor : IEquatable<CssAutoColor>
{
    public CssAutoColor(CssColor color)
    {
        IsSpecified = true;
        Color = color;
    }

    public static CssAutoColor Auto => default;

    readonly bool IsSpecified;

    public bool IsAuto => !IsSpecified;

    public CssColor Color { get; }

    public bool Equals(CssAutoColor o) => IsSpecified == o.IsSpecified && Color.Equals(o.Color);
    public override bool Equals(object? obj) => obj is CssAutoColor c && Equals(c);
    public override int GetHashCode() => IsSpecified ? Color.GetHashCode() : 0;
    public static bool operator ==(CssAutoColor a, CssAutoColor b) => a.Equals(b);
    public static bool operator !=(CssAutoColor a, CssAutoColor b) => !a.Equals(b);
    public override string ToString() => IsAuto ? "auto" : Color.ToString();
}

/// <summary><c>scrollbar-color: auto | &lt;color&gt;{2}</c>（滑块、轨道）。</summary>
public readonly struct CssScrollbarColor : IEquatable<CssScrollbarColor>
{
    public CssScrollbarColor(CssColor thumb, CssColor track)
    {
        IsSpecified = true;
        Thumb = thumb;
        Track = track;
    }

    public static CssScrollbarColor Auto => default;

    readonly bool IsSpecified;

    public bool IsAuto => !IsSpecified;
    public CssColor Thumb { get; }
    public CssColor Track { get; }

    public bool Equals(CssScrollbarColor o) => IsSpecified == o.IsSpecified && Thumb.Equals(o.Thumb) && Track.Equals(o.Track);
    public override bool Equals(object? obj) => obj is CssScrollbarColor c && Equals(c);
    public override int GetHashCode() => HashCodeCompat.Combine(Thumb.GetHashCode(), Track.GetHashCode());
    public static bool operator ==(CssScrollbarColor a, CssScrollbarColor b) => a.Equals(b);
    public static bool operator !=(CssScrollbarColor a, CssScrollbarColor b) => !a.Equals(b);
    public override string ToString() => IsAuto ? "auto" : Thumb + " " + Track;
}

/// <summary>文本、字体与 SVG 绘制属性的解析与计算。</summary>
internal static class TextParsers
{
    static readonly CssEnvironment s_env = new();

    internal sealed class LengthKeywordValue : CssValue
    {
        public LengthKeywordValue(int keyword, CalcNode? node, int flags = 0)
        {
            Keyword = keyword;
            Node = node;
            Flags = flags;
        }

        public int Keyword { get; }
        public CalcNode? Node { get; }
        public int Flags { get; }
    }

    /// <summary><c>vertical-align</c>。</summary>
    public static CssValue? VerticalAlign(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            int k = CssSerializer.ToAsciiLower(id.Value) switch
            {
                "baseline" => 0, "sub" => 1, "super" => 2, "text-top" => 3, "text-bottom" => 4, "middle" => 5, "top" => 6, "bottom" => 7, _ => -1,
            };
            return k < 0 ? null : new LengthKeywordValue(k, null);
        }
        var node = ValueParsers.LengthPercentageNode(v, nonNegative: false);
        return node is null ? null : new LengthKeywordValue((int)CssVerticalAlignKind.Length, node);
    }

    public static CssVerticalAlign ComputeVerticalAlign(CssValue v, in LengthContext ctx)
    {
        var x = (LengthKeywordValue)v;
        return new CssVerticalAlign((CssVerticalAlignKind)x.Keyword, x.Node is null ? default : x.Node.Compute(ctx).ToLengthPercentage());
    }

    /// <summary><c>letter-spacing / word-spacing: normal | &lt;length&gt;</c>（计算值 px，normal 为 0）。</summary>
    public static CssValue? Spacing(ref ValueReader r)
    {
        if (r.TryIdent("normal")) return new NumericValue(CalcLeaf.Length(CssLength.Px(0)));
        var node = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: false, allowPercent: false);
        return node is null ? null : new NumericValue(node);
    }

    /// <summary><c>text-indent: &lt;length-percentage&gt; &amp;&amp; hanging? &amp;&amp; each-line?</c></summary>
    public static CssValue? TextIndent(ref ValueReader r)
    {
        CalcNode? node = null;
        int flags = 0;
        while (!r.AtEnd)
        {
            if ((flags & 1) == 0 && r.TryIdent("hanging")) { flags |= 1; continue; }
            if ((flags & 2) == 0 && r.TryIdent("each-line")) { flags |= 2; continue; }
            if (node is null && (node = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: false)) is not null) continue;
            return null;
        }
        return node is null ? null : new LengthKeywordValue(0, node, flags);
    }

    public static CssTextIndent ComputeTextIndent(CssValue v, in LengthContext ctx)
    {
        var x = (LengthKeywordValue)v;
        return new CssTextIndent(x.Node!.Compute(ctx).ToLengthPercentage(), (x.Flags & 1) != 0, (x.Flags & 2) != 0);
    }

    /// <summary><c>tab-size: &lt;number [0,∞]&gt; | &lt;length [0,∞]&gt;</c></summary>
    public static CssValue? TabSize(ref ValueReader r)
    {
        var v = r.Next();
        var n = ValueParsers.NumberNode(v, nonNegative: true);
        if (n is not null) return new LengthKeywordValue(0, n);
        var l = ValueParsers.LengthPercentageNode(v, nonNegative: true, allowPercent: false);
        return l is null ? null : new LengthKeywordValue(1, l);
    }

    public static CssTabSize ComputeTabSize(CssValue v, in LengthContext ctx)
    {
        var x = (LengthKeywordValue)v;
        var res = x.Node!.Compute(ctx);
        return x.Keyword == 0 ? new CssTabSize(Math.Max(0, CalcMath.Finite(res.Number)), false) : new CssTabSize(Math.Max(0, CalcMath.Finite(res.Px)), true);
    }

    /// <summary><c>text-decoration-thickness: auto | from-font | &lt;length-percentage&gt;</c></summary>
    public static CssValue? DecorationThickness(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return new LengthKeywordValue(0, null);
        if (r.TryIdent("from-font")) return new LengthKeywordValue(1, null);
        var node = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: false);
        return node is null ? null : new LengthKeywordValue(2, node);
    }

    public static CssDecorationThickness ComputeDecorationThickness(CssValue v, in LengthContext ctx)
    {
        var x = (LengthKeywordValue)v;
        return new CssDecorationThickness((CssDecorationThicknessKind)x.Keyword, x.Node is null ? default : x.Node.Compute(ctx).ToLengthPercentage());
    }

    /// <summary><c>border-spacing: &lt;length&gt; &lt;length&gt;?</c>（非负）。</summary>
    public static CssValue? BorderSpacing(ref ValueReader r)
    {
        var h = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: true, allowPercent: false);
        if (h is null) return null;
        CalcNode? vNode = null;
        if (!r.AtEnd)
        {
            vNode = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: true, allowPercent: false);
            if (vNode is null) return null;
        }
        return new PairValue(h, vNode ?? h);
    }

    internal sealed class PairValue : CssValue
    {
        public PairValue(CalcNode a, CalcNode b)
        {
            A = a;
            B = b;
        }

        public CalcNode A { get; }
        public CalcNode B { get; }
    }

    public static CssBorderSpacing ComputeBorderSpacing(CssValue v, in LengthContext ctx)
    {
        var x = (PairValue)v;
        float h = Math.Max(0, CalcMath.Finite(x.A.Compute(ctx).Px));
        float vert = ReferenceEquals(x.A, x.B) ? h : Math.Max(0, CalcMath.Finite(x.B.Compute(ctx).Px));
        return new CssBorderSpacing(h, vert);
    }

    /// <summary><c>text-decoration-line: none | [ underline || overline || line-through || blink ]</c></summary>
    public static CssValue? DecorationLine(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new PrecomputedValue<CssTextDecorationLine>(CssTextDecorationLine.None);
        var flags = CssTextDecorationLine.None;
        while (!r.AtEnd)
        {
            var f = r.PeekIdentLower() switch
            {
                "underline" => CssTextDecorationLine.Underline,
                "overline" => CssTextDecorationLine.Overline,
                "line-through" => CssTextDecorationLine.LineThrough,
                "blink" => CssTextDecorationLine.Blink,
                "spelling-error" => CssTextDecorationLine.SpellingError,
                "grammar-error" => CssTextDecorationLine.GrammarError,
                _ => CssTextDecorationLine.None,
            };
            if (f == CssTextDecorationLine.None || (flags & f) != 0) return null;
            if ((f is CssTextDecorationLine.SpellingError or CssTextDecorationLine.GrammarError) && flags != 0) return null;
            if (flags is CssTextDecorationLine.SpellingError or CssTextDecorationLine.GrammarError) return null;
            flags |= f;
            r.Advance();
        }
        return flags == CssTextDecorationLine.None ? null : new PrecomputedValue<CssTextDecorationLine>(flags);
    }

    internal static string DecorationLineText(CssTextDecorationLine f)
    {
        if (f == CssTextDecorationLine.None) return "none";
        var parts = new List<string>(4);
        if ((f & CssTextDecorationLine.Underline) != 0) parts.Add("underline");
        if ((f & CssTextDecorationLine.Overline) != 0) parts.Add("overline");
        if ((f & CssTextDecorationLine.LineThrough) != 0) parts.Add("line-through");
        if ((f & CssTextDecorationLine.Blink) != 0) parts.Add("blink");
        if ((f & CssTextDecorationLine.SpellingError) != 0) parts.Add("spelling-error");
        if ((f & CssTextDecorationLine.GrammarError) != 0) parts.Add("grammar-error");
        return string.Join(" ", parts);
    }

    /// <summary><c>font-variant-numeric</c>（每组至多一个关键字）。</summary>
    public static CssValue? FontVariantNumeric(ref ValueReader r)
    {
        if (r.TryIdent("normal")) return new PrecomputedValue<CssFontVariantNumeric>(CssFontVariantNumeric.Normal);
        var flags = CssFontVariantNumeric.Normal;
        while (!r.AtEnd)
        {
            var (f, group) = r.PeekIdentLower() switch
            {
                "lining-nums" => (CssFontVariantNumeric.LiningNums, CssFontVariantNumeric.LiningNums | CssFontVariantNumeric.OldstyleNums),
                "oldstyle-nums" => (CssFontVariantNumeric.OldstyleNums, CssFontVariantNumeric.LiningNums | CssFontVariantNumeric.OldstyleNums),
                "proportional-nums" => (CssFontVariantNumeric.ProportionalNums, CssFontVariantNumeric.ProportionalNums | CssFontVariantNumeric.TabularNums),
                "tabular-nums" => (CssFontVariantNumeric.TabularNums, CssFontVariantNumeric.ProportionalNums | CssFontVariantNumeric.TabularNums),
                "diagonal-fractions" => (CssFontVariantNumeric.DiagonalFractions, CssFontVariantNumeric.DiagonalFractions | CssFontVariantNumeric.StackedFractions),
                "stacked-fractions" => (CssFontVariantNumeric.StackedFractions, CssFontVariantNumeric.DiagonalFractions | CssFontVariantNumeric.StackedFractions),
                "ordinal" => (CssFontVariantNumeric.Ordinal, CssFontVariantNumeric.Ordinal),
                "slashed-zero" => (CssFontVariantNumeric.SlashedZero, CssFontVariantNumeric.SlashedZero),
                _ => (CssFontVariantNumeric.Normal, CssFontVariantNumeric.Normal),
            };
            if (f == CssFontVariantNumeric.Normal || (flags & group) != 0) return null;
            flags |= f;
            r.Advance();
        }
        return flags == CssFontVariantNumeric.Normal ? null : new PrecomputedValue<CssFontVariantNumeric>(flags);
    }

    internal static string FontVariantNumericText(CssFontVariantNumeric f)
    {
        if (f == CssFontVariantNumeric.Normal) return "normal";
        var parts = new List<string>(4);
        if ((f & CssFontVariantNumeric.LiningNums) != 0) parts.Add("lining-nums");
        if ((f & CssFontVariantNumeric.OldstyleNums) != 0) parts.Add("oldstyle-nums");
        if ((f & CssFontVariantNumeric.ProportionalNums) != 0) parts.Add("proportional-nums");
        if ((f & CssFontVariantNumeric.TabularNums) != 0) parts.Add("tabular-nums");
        if ((f & CssFontVariantNumeric.DiagonalFractions) != 0) parts.Add("diagonal-fractions");
        if ((f & CssFontVariantNumeric.StackedFractions) != 0) parts.Add("stacked-fractions");
        if ((f & CssFontVariantNumeric.Ordinal) != 0) parts.Add("ordinal");
        if ((f & CssFontVariantNumeric.SlashedZero) != 0) parts.Add("slashed-zero");
        return string.Join(" ", parts);
    }

    /// <summary><c>text-underline-position: auto | from-font | [ under || [ left | right ] ]</c></summary>
    public static CssValue? UnderlinePosition(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return new PrecomputedValue<CssTextUnderlinePosition>(CssTextUnderlinePosition.Auto);
        if (r.TryIdent("from-font")) return new PrecomputedValue<CssTextUnderlinePosition>(CssTextUnderlinePosition.FromFont);
        var flags = CssTextUnderlinePosition.Auto;
        while (!r.AtEnd)
        {
            var k = r.PeekIdentLower();
            if (k == "under" && (flags & CssTextUnderlinePosition.Under) == 0) flags |= CssTextUnderlinePosition.Under;
            else if (k is "left" or "right" && (flags & (CssTextUnderlinePosition.Left | CssTextUnderlinePosition.Right)) == 0)
                flags |= k == "left" ? CssTextUnderlinePosition.Left : CssTextUnderlinePosition.Right;
            else return null;
            r.Advance();
        }
        return flags == CssTextUnderlinePosition.Auto ? null : new PrecomputedValue<CssTextUnderlinePosition>(flags);
    }

    internal static string UnderlinePositionText(CssTextUnderlinePosition f)
    {
        if (f == CssTextUnderlinePosition.Auto) return "auto";
        if (f == CssTextUnderlinePosition.FromFont) return "from-font";
        var parts = new List<string>(2);
        if ((f & CssTextUnderlinePosition.Under) != 0) parts.Add("under");
        if ((f & CssTextUnderlinePosition.Left) != 0) parts.Add("left");
        if ((f & CssTextUnderlinePosition.Right) != 0) parts.Add("right");
        return string.Join(" ", parts);
    }

    /// <summary><c>color-scheme: normal | [ light | dark | &lt;custom-ident&gt; ]+ &amp;&amp; only?</c></summary>
    public static CssValue? ColorScheme(ref ValueReader r)
    {
        if (r.TryIdent("normal")) return new PrecomputedValue<CssColorScheme>(CssColorScheme.Normal);
        var flags = CssColorScheme.Normal;
        bool any = false;
        while (!r.AtEnd)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.Ident } id) return null;
            var k = CssSerializer.ToAsciiLower(id.Value);
            if (k == "only") { if ((flags & CssColorScheme.Only) != 0) return null; flags |= CssColorScheme.Only; continue; }
            if (k is "normal" or "none" or "default" || CssWideKeywordValue.TryParse(k, out _)) return null;
            any = true;
            if (k == "light") flags |= CssColorScheme.Light;
            else if (k == "dark") flags |= CssColorScheme.Dark;
        }
        return any ? new PrecomputedValue<CssColorScheme>(flags) : null;
    }

    internal static string ColorSchemeText(CssColorScheme f)
    {
        if ((f & (CssColorScheme.Light | CssColorScheme.Dark)) == 0) return (f & CssColorScheme.Only) != 0 ? "only" : "normal";
        var parts = new List<string>(3);
        if ((f & CssColorScheme.Light) != 0) parts.Add("light");
        if ((f & CssColorScheme.Dark) != 0) parts.Add("dark");
        if ((f & CssColorScheme.Only) != 0) parts.Add("only");
        return string.Join(" ", parts);
    }

    /// <summary><c>font-feature-settings: normal | [ &lt;string&gt; [ &lt;integer [0,∞]&gt; | on | off ]? ]#</c></summary>
    public static CssValue? FontFeatureSettings(ref ValueReader r)
    {
        if (r.TryIdent("normal")) return new PrecomputedValue<CssList<CssFontFeature>>(CssList<CssFontFeature>.Empty);
        var list = new List<CssFontFeature>();
        while (true)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.String } tag || !IsTag(tag.Value) || list.Count > 1024) return null;
            int value = 1;
            var v = r.Peek();
            if (v is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer } n)
            {
                if (n.Number < 0) return null;
                value = (int)Math.Min(n.Number, int.MaxValue);
                r.Advance();
            }
            else if (v is CssToken t && t.IsIdent("on")) r.Advance();
            else if (v is CssToken f && f.IsIdent("off")) { value = 0; r.Advance(); }
            list.Add(new CssFontFeature(tag.Value, value));
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<CssFontFeature>>(new CssList<CssFontFeature>(list.ToArray()));
    }

    /// <summary><c>font-variation-settings: normal | [ &lt;string&gt; &lt;number&gt; ]#</c></summary>
    public static CssValue? FontVariationSettings(ref ValueReader r)
    {
        if (r.TryIdent("normal")) return new PrecomputedValue<CssList<CssFontVariation>>(CssList<CssFontVariation>.Empty);
        var list = new List<CssFontVariation>();
        while (true)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.String } tag || !IsTag(tag.Value) || list.Count > 1024) return null;
            var node = ValueParsers.NumberNode(r.Next(), nonNegative: false);
            if (node is null || node.IsContextDependent) return null;
            list.Add(new CssFontVariation(tag.Value, CalcMath.Finite(node.Compute(new LengthContext(16, 16, s_env)).Number)));
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<CssFontVariation>>(new CssList<CssFontVariation>(list.ToArray()));
    }

    /// <summary>OpenType 标签：恰好 4 个 U+20–U+7E 的字符。</summary>
    static bool IsTag(string s)
    {
        if (s.Length != 4) return false;
        foreach (char c in s) if (c < 0x20 || c > 0x7E) return false;
        return true;
    }

    /// <summary><c>fill / stroke: none | &lt;color&gt; | &lt;url&gt; [ none | &lt;color&gt; ]? | context-fill | context-stroke</c></summary>
    public static CssValue? SvgPaint(ref ValueReader r)
    {
        var v = r.Next();
        string? url = null;
        if (v is CssToken { Type: CssTokenType.Url } u) url = u.Value;
        else if (v is CssFunction f && CssSerializer.EqualsIgnoreAsciiCase(f.Name, "url"))
        {
            var ur = new ValueReader(f.Arguments);
            if (ur.Next() is not CssToken { Type: CssTokenType.String } s || !ur.AtEnd) return null;
            url = s.Value;
        }
        if (url is not null)
        {
            var fallback = CssColor.Transparent;
            if (!r.AtEnd)
            {
                var fb = r.Next()!;
                if (fb is CssToken n && n.IsIdent("none")) fallback = CssColor.Transparent;
                else if (!CssColorParser.TryParse(fb, out fallback)) return null;
            }
            return new PrecomputedValue<CssSvgPaint>(new CssSvgPaint(CssSvgPaintKind.Url, fallback, url));
        }
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            switch (CssSerializer.ToAsciiLower(id.Value))
            {
                case "none": return new PrecomputedValue<CssSvgPaint>(new CssSvgPaint(CssSvgPaintKind.None));
                case "context-fill": return new PrecomputedValue<CssSvgPaint>(new CssSvgPaint(CssSvgPaintKind.ContextFill));
                case "context-stroke": return new PrecomputedValue<CssSvgPaint>(new CssSvgPaint(CssSvgPaintKind.ContextStroke));
            }
        }
        return v is not null && CssColorParser.TryParse(v, out var c) ? new PrecomputedValue<CssSvgPaint>(new CssSvgPaint(CssSvgPaintKind.Color, c)) : null;
    }

    /// <summary><c>auto | &lt;color&gt;</c></summary>
    public static CssValue? AutoColor(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken t && t.IsIdent("auto")) return new PrecomputedValue<CssAutoColor>(CssAutoColor.Auto);
        return v is not null && CssColorParser.TryParse(v, out var c) ? new PrecomputedValue<CssAutoColor>(new CssAutoColor(c)) : null;
    }

    /// <summary><c>scrollbar-color: auto | &lt;color&gt;{2}</c></summary>
    public static CssValue? ScrollbarColor(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken t && t.IsIdent("auto")) return new PrecomputedValue<CssScrollbarColor>(CssScrollbarColor.Auto);
        if (v is null || !CssColorParser.TryParse(v, out var thumb)) return null;
        var w = r.Next();
        if (w is null || !CssColorParser.TryParse(w, out var track)) return null;
        return new PrecomputedValue<CssScrollbarColor>(new CssScrollbarColor(thumb, track));
    }

    /// <summary><c>stroke-width: &lt;length-percentage&gt; | &lt;number&gt;</c>（数按 px）。</summary>
    public static CssValue? StrokeWidth(ref ValueReader r)
    {
        var v = r.Next();
        var n = ValueParsers.NumberNode(v, nonNegative: true);
        if (n is CalcLeaf leaf) return new NumericValue(CalcLeaf.Length(CssLength.Px(leaf.Value)));
        var node = ValueParsers.LengthPercentageNode(v, nonNegative: true);
        return node is null ? null : new NumericValue(node);
    }

    /// <summary><c>text-shadow: none | [ &lt;color&gt;? &amp;&amp; &lt;length&gt;{2,3} ]#</c>（没有 spread 与 inset）。</summary>
    public static CssValue? TextShadow(ref ValueReader r)
    {
        var v = ValueParsers.ShadowList(ref r);
        if (v is ShadowListValue list)
            foreach (var s in list.Shadows) if (s.Inset || s.Spread is not null) return null;
        return v;
    }

    /// <summary><c>text-shadow</c> 的计算值文本：与 <c>box-shadow</c> 不同，没有扩展半径与 inset（否则序列化结果不能再解析）。</summary>
    public static string TextShadowText(ShadowList list)
    {
        if (list.IsNone) return "none";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < list.Shadows.Count; i++)
        {
            var s = list.Shadows[i];
            if (i > 0) sb.Append(", ");
            sb.Append(s.Color.ToString()).Append(' ')
              .Append(ValueText.Px(s.OffsetX)).Append(' ').Append(ValueText.Px(s.OffsetY)).Append(' ')
              .Append(ValueText.Px(s.Blur));
        }
        return sb.ToString();
    }

    /// <summary><c>-webkit-line-clamp: none | &lt;integer [1,∞]&gt;</c>（none 计算为 0）。</summary>
    public static CssValue? LineClamp(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken t && t.IsIdent("none")) return new NumericValue(CalcLeaf.Number(0));
        if (v is not CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer } n || n.Number < 1) return null;
        return new NumericValue(CalcLeaf.Number(Math.Min(n.Number, int.MaxValue)));
    }
}

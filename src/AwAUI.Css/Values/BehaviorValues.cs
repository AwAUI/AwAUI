using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary><c>touch-action</c>（Pointer Events 3）。<see cref="Auto"/> 为 0；<c>none</c> 不与其他值组合。</summary>
[Flags]
public enum CssTouchAction : ushort
{
    Auto = 0,
    None = 1,
    PanX = 2,
    PanLeft = 4,
    PanRight = 8,
    PanY = 16,
    PanUp = 32,
    PanDown = 64,
    PinchZoom = 128,
    Manipulation = 256,
}

/// <summary><c>contain</c>（CSS Containment 2）。<c>strict</c> 与 <c>content</c> 已展开。</summary>
[Flags]
public enum CssContain : byte
{
    None = 0,
    Size = 1,
    InlineSize = 2,
    Layout = 4,
    Style = 8,
    Paint = 16,
}

/// <summary><c>scroll-snap-type</c> 的轴。</summary>
public enum CssScrollSnapAxis : byte
{
    None,
    X,
    Y,
    Block,
    Inline,
    Both,
}

/// <summary><c>scroll-snap-type</c> 的计算值。</summary>
public readonly struct CssScrollSnapType : IEquatable<CssScrollSnapType>
{
    public CssScrollSnapType(CssScrollSnapAxis axis, bool mandatory)
    {
        Axis = axis;
        Mandatory = mandatory;
    }

    public CssScrollSnapAxis Axis { get; }

    /// <summary>mandatory（否则 proximity）。</summary>
    public bool Mandatory { get; }

    public bool Equals(CssScrollSnapType o) => Axis == o.Axis && Mandatory == o.Mandatory;
    public override bool Equals(object? obj) => obj is CssScrollSnapType t && Equals(t);
    public override int GetHashCode() => (int)Axis * 2 + (Mandatory ? 1 : 0);
    public static bool operator ==(CssScrollSnapType a, CssScrollSnapType b) => a.Equals(b);
    public static bool operator !=(CssScrollSnapType a, CssScrollSnapType b) => !a.Equals(b);

    public override string ToString()
    {
        if (Axis == CssScrollSnapAxis.None) return "none";
        string a = Axis switch { CssScrollSnapAxis.X => "x", CssScrollSnapAxis.Y => "y", CssScrollSnapAxis.Block => "block", CssScrollSnapAxis.Inline => "inline", _ => "both" };
        return Mandatory ? a + " mandatory" : a;
    }
}

/// <summary><c>scroll-snap-align</c> 的一个轴。</summary>
public enum CssScrollSnapAlignment : byte
{
    None,
    Start,
    End,
    Center,
}

/// <summary><c>scroll-snap-align</c> 的计算值（块轴、行内轴）。</summary>
public readonly struct CssScrollSnapAlign : IEquatable<CssScrollSnapAlign>
{
    public CssScrollSnapAlign(CssScrollSnapAlignment block, CssScrollSnapAlignment inline)
    {
        Block = block;
        Inline = inline;
    }

    public CssScrollSnapAlignment Block { get; }
    public CssScrollSnapAlignment Inline { get; }

    public bool Equals(CssScrollSnapAlign o) => Block == o.Block && Inline == o.Inline;
    public override bool Equals(object? obj) => obj is CssScrollSnapAlign a && Equals(a);
    public override int GetHashCode() => (int)Block * 4 + (int)Inline;
    public static bool operator ==(CssScrollSnapAlign a, CssScrollSnapAlign b) => a.Equals(b);
    public static bool operator !=(CssScrollSnapAlign a, CssScrollSnapAlign b) => !a.Equals(b);

    public override string ToString() => Block == Inline ? Name(Block) : Name(Block) + " " + Name(Inline);

    static string Name(CssScrollSnapAlignment a) => a switch { CssScrollSnapAlignment.Start => "start", CssScrollSnapAlignment.End => "end", CssScrollSnapAlignment.Center => "center", _ => "none" };
}

/// <summary>交互与滚动属性的解析。</summary>
internal static class BehaviorParsers
{
    public static CssValue? TouchAction(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return new PrecomputedValue<CssTouchAction>(CssTouchAction.Auto);
        if (r.TryIdent("none")) return new PrecomputedValue<CssTouchAction>(CssTouchAction.None);
        if (r.TryIdent("manipulation")) return new PrecomputedValue<CssTouchAction>(CssTouchAction.Manipulation);
        var flags = CssTouchAction.Auto;
        const CssTouchAction X = CssTouchAction.PanX | CssTouchAction.PanLeft | CssTouchAction.PanRight;
        const CssTouchAction Y = CssTouchAction.PanY | CssTouchAction.PanUp | CssTouchAction.PanDown;
        while (!r.AtEnd)
        {
            var (f, group) = r.PeekIdentLower() switch
            {
                "pan-x" => (CssTouchAction.PanX, X),
                "pan-left" => (CssTouchAction.PanLeft, X),
                "pan-right" => (CssTouchAction.PanRight, X),
                "pan-y" => (CssTouchAction.PanY, Y),
                "pan-up" => (CssTouchAction.PanUp, Y),
                "pan-down" => (CssTouchAction.PanDown, Y),
                "pinch-zoom" => (CssTouchAction.PinchZoom, CssTouchAction.PinchZoom),
                _ => (CssTouchAction.Auto, CssTouchAction.Auto),
            };
            if (f == CssTouchAction.Auto || (flags & group) != 0) return null;
            flags |= f;
            r.Advance();
        }
        return flags == CssTouchAction.Auto ? null : new PrecomputedValue<CssTouchAction>(flags);
    }

    internal static string TouchActionText(CssTouchAction f)
    {
        if (f == CssTouchAction.Auto) return "auto";
        if (f == CssTouchAction.None) return "none";
        if (f == CssTouchAction.Manipulation) return "manipulation";
        var parts = new List<string>(3);
        if ((f & CssTouchAction.PanX) != 0) parts.Add("pan-x");
        if ((f & CssTouchAction.PanLeft) != 0) parts.Add("pan-left");
        if ((f & CssTouchAction.PanRight) != 0) parts.Add("pan-right");
        if ((f & CssTouchAction.PanY) != 0) parts.Add("pan-y");
        if ((f & CssTouchAction.PanUp) != 0) parts.Add("pan-up");
        if ((f & CssTouchAction.PanDown) != 0) parts.Add("pan-down");
        if ((f & CssTouchAction.PinchZoom) != 0) parts.Add("pinch-zoom");
        return string.Join(" ", parts);
    }

    public static CssValue? Contain(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new PrecomputedValue<CssContain>(CssContain.None);
        if (r.TryIdent("strict")) return new PrecomputedValue<CssContain>(CssContain.Size | CssContain.Layout | CssContain.Paint | CssContain.Style);
        if (r.TryIdent("content")) return new PrecomputedValue<CssContain>(CssContain.Layout | CssContain.Paint | CssContain.Style);
        var flags = CssContain.None;
        while (!r.AtEnd)
        {
            var f = r.PeekIdentLower() switch
            {
                "size" => CssContain.Size,
                "inline-size" => CssContain.InlineSize,
                "layout" => CssContain.Layout,
                "style" => CssContain.Style,
                "paint" => CssContain.Paint,
                _ => CssContain.None,
            };
            if (f == CssContain.None || (flags & f) != 0) return null;
            if (f is CssContain.Size or CssContain.InlineSize && (flags & (CssContain.Size | CssContain.InlineSize)) != 0) return null;
            flags |= f;
            r.Advance();
        }
        return flags == CssContain.None ? null : new PrecomputedValue<CssContain>(flags);
    }

    internal static string ContainText(CssContain f)
    {
        if (f == CssContain.None) return "none";
        if (f == (CssContain.Size | CssContain.Layout | CssContain.Paint | CssContain.Style)) return "strict";
        if (f == (CssContain.Layout | CssContain.Paint | CssContain.Style)) return "content";
        var parts = new List<string>(4);
        if ((f & CssContain.Size) != 0) parts.Add("size");
        if ((f & CssContain.InlineSize) != 0) parts.Add("inline-size");
        if ((f & CssContain.Layout) != 0) parts.Add("layout");
        if ((f & CssContain.Style) != 0) parts.Add("style");
        if ((f & CssContain.Paint) != 0) parts.Add("paint");
        return string.Join(" ", parts);
    }

    /// <summary><c>scroll-snap-type: none | [ x | y | block | inline | both ] [ mandatory | proximity ]?</c></summary>
    public static CssValue? ScrollSnapType(ref ValueReader r)
    {
        var axis = r.PeekIdentLower() switch
        {
            "none" => CssScrollSnapAxis.None,
            "x" => CssScrollSnapAxis.X,
            "y" => CssScrollSnapAxis.Y,
            "block" => CssScrollSnapAxis.Block,
            "inline" => CssScrollSnapAxis.Inline,
            "both" => CssScrollSnapAxis.Both,
            _ => (CssScrollSnapAxis)255,
        };
        if ((byte)axis == 255) return null;
        r.Advance();
        bool mandatory = false;
        if (axis != CssScrollSnapAxis.None && !r.AtEnd)
        {
            if (r.TryIdent("mandatory")) mandatory = true;
            else if (!r.TryIdent("proximity")) return null;
        }
        return new PrecomputedValue<CssScrollSnapType>(new CssScrollSnapType(axis, mandatory));
    }

    /// <summary><c>scroll-snap-align: [ none | start | end | center ]{1,2}</c></summary>
    public static CssValue? ScrollSnapAlign(ref ValueReader r)
    {
        static CssScrollSnapAlignment? A(string? k) => k switch
        {
            "none" => CssScrollSnapAlignment.None,
            "start" => CssScrollSnapAlignment.Start,
            "end" => CssScrollSnapAlignment.End,
            "center" => CssScrollSnapAlignment.Center,
            _ => null,
        };
        var a = A(r.PeekIdentLower());
        if (a is null) return null;
        r.Advance();
        var b = a;
        if (!r.AtEnd)
        {
            b = A(r.PeekIdentLower());
            if (b is null) return null;
            r.Advance();
        }
        return new PrecomputedValue<CssScrollSnapAlign>(new CssScrollSnapAlign(a.Value, b.Value));
    }

    /// <summary><c>scrollbar-gutter: auto | stable &amp;&amp; both-edges?</c></summary>
    public static CssValue? ScrollbarGutter(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return KeywordValue.Of(0);
        bool stable = false, both = false;
        while (!r.AtEnd)
        {
            if (!stable && r.TryIdent("stable")) { stable = true; continue; }
            if (!both && r.TryIdent("both-edges")) { both = true; continue; }
            return null;
        }
        if (!stable) return null;
        return KeywordValue.Of(both ? 2 : 1);
    }

    /// <summary><c>scrollbar-gutter</c> 的计算值文本（<c>stable both-edges</c> 是两个关键字）。</summary>
    public static string ScrollbarGutterText(CssScrollbarGutter value) => value switch
    {
        CssScrollbarGutter.Stable => "stable",
        CssScrollbarGutter.StableBothEdges => "stable both-edges",
        _ => "auto",
    };

    /// <summary><c>will-change: auto | [ scroll-position | contents | &lt;custom-ident&gt; ]#</c>（auto 为空列表）。</summary>
    public static CssValue? WillChange(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return r.AtEnd ? new PrecomputedValue<CssList<string>>(CssList<string>.Empty) : null;
        var list = new List<string>();
        while (true)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.Ident } id || list.Count > 1024) return null;
            var k = id.Value.StartsWith("--", StringComparison.Ordinal) ? id.Value : CssSerializer.ToAsciiLower(id.Value);
            if (k is "will-change" or "none" or "all" or "auto" or "default" || CssWideKeywordValue.TryParse(k, out _)) return null;
            list.Add(k);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<string>>(new CssList<string>(list.ToArray()));
    }

    /// <summary><c>container-name: none | &lt;custom-ident&gt;+</c>（none 为空列表；名称区分大小写）。</summary>
    public static CssValue? ContainerName(ref ValueReader r)
    {
        if (r.TryIdent("none")) return r.AtEnd ? new PrecomputedValue<CssList<string>>(CssList<string>.Empty) : null;
        var list = new List<string>();
        while (!r.AtEnd)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.Ident } id || !IsContainerName(id.Value) || list.Count > 1024) return null;
            list.Add(id.Value);
        }
        return list.Count == 0 ? null : new PrecomputedValue<CssList<string>>(new CssList<string>(list.ToArray(), " "));
    }

    internal static bool IsContainerName(string name)
    {
        var l = CssSerializer.ToAsciiLower(name);
        return l is not ("none" or "and" or "or" or "not" or "default") && !CssWideKeywordValue.TryParse(l, out _);
    }
}

/// <summary><c>container</c> 与 <c>text-decoration</c>、<c>text-wrap</c> 等简写。</summary>
internal static partial class Shorthands
{
    /// <summary><c>container: &lt;'container-name'&gt; [ / &lt;'container-type'&gt; ]?</c>（长属性顺序：name、type）。</summary>
    public static bool Container(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        int slash = -1;
        for (int i = 0; i < value.Count; i++)
            if (value[i] is CssToken t && t.IsDelim('/')) { slash = i; break; }
        var nameTokens = new List<CssComponentValue>();
        var typeTokens = new List<CssComponentValue>();
        for (int i = 0; i < value.Count; i++)
        {
            if (i == slash) continue;
            (slash >= 0 && i > slash ? typeTokens : nameTokens).Add(value[i]);
        }
        var name = PropertyParsers.ParseLonghand(longhands[0], nameTokens);
        if (name is null) return false;
        CssValue type = CssWideKeywordValue.Initial;
        if (slash >= 0)
        {
            var t = PropertyParsers.ParseLonghand(longhands[1], typeTokens);
            if (t is null) return false;
            type = t;
        }
        output.Add(new PropertyValue(longhands[0], name));
        output.Add(new PropertyValue(longhands[1], type));
        return true;
    }

    /// <summary>
    /// <c>text-decoration: &lt;'text-decoration-line'&gt; || &lt;'text-decoration-style'&gt; || &lt;'text-decoration-color'&gt; || &lt;'text-decoration-thickness'&gt;</c>
    /// （长属性顺序：line、style、color、thickness）。
    /// </summary>
    public static bool TextDecoration(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var lineTokens = new List<CssComponentValue>();
        CssValue? style = null, color = null, thickness = null;
        if (r.AtEnd) return false;
        while (!r.AtEnd)
        {
            var v = r.Peek()!;
            if (v is CssToken { Type: CssTokenType.Ident } id && CssSerializer.ToAsciiLower(id.Value) is "none" or "underline" or "overline" or "line-through" or "blink")
            {
                r.Advance();
                lineTokens.Add(v);
                lineTokens.Add(CssToken.CreateWhitespace());
                continue;
            }
            CssValue? x;
            if (style is null && (x = Parse(longhands[1], ref r)) is not null) { style = x; continue; }
            if (color is null && (x = Parse(longhands[2], ref r)) is not null) { color = x; continue; }
            if (thickness is null && (x = Parse(longhands[3], ref r)) is not null) { thickness = x; continue; }
            return false;
        }
        CssValue line = CssWideKeywordValue.Initial;
        if (lineTokens.Count > 0)
        {
            var parsed = PropertyParsers.ParseLonghand(longhands[0], lineTokens);
            if (parsed is null) return false;
            line = parsed;
        }
        output.Add(new PropertyValue(longhands[0], line));
        output.Add(new PropertyValue(longhands[1], style ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[2], color ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[3], thickness ?? CssWideKeywordValue.Initial));
        return true;
    }

    /// <summary>两个长属性各取一个值、任意顺序（<c>text-wrap: &lt;mode&gt; || &lt;style&gt;</c>），省略的取初始值。</summary>
    public static bool AnyOrderPair(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        CssValue? a = null, b = null;
        if (r.AtEnd) return false;
        while (!r.AtEnd)
        {
            CssValue? x;
            if (a is null && (x = Parse(longhands[0], ref r)) is not null) { a = x; continue; }
            if (b is null && (x = Parse(longhands[1], ref r)) is not null) { b = x; continue; }
            return false;
        }
        output.Add(new PropertyValue(longhands[0], a ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[1], b ?? CssWideKeywordValue.Initial));
        return true;
    }
}

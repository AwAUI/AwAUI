using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 简写展开（design/16「简写」）：级联前展开为长属性。省略的子属性取其初始值（以 <c>initial</c> 关键字表示，计算时取初始计算值）。
/// 每个方法的 <c>longhands</c> 参数即清单中该简写的长属性列表。
/// </summary>
internal static partial class Shorthands
{
    static CssValue? Parse(CssPropertyId id, ref ValueReader r)
    {
        // 逐个长属性解析一个值：复用生成的长属性分派，但只消费一个组件值
        int save = r.Position;
        var v = r.Next();
        if (v is null) return null;
        var single = PropertyParsers.ParseLonghand(id, new[] { v });
        if (single is null) r.Position = save;
        return single;
    }

    /// <summary>上、右、下、左：1–4 个值按 CSS 的盒子规则补齐。</summary>
    public static bool Box4(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var vals = new CssValue[4];
        int n = 0;
        while (!r.AtEnd)
        {
            if (n == 4) return false;
            var v = Parse(longhands[n], ref r);
            if (v is null) return false;
            vals[n++] = v;
        }
        if (n == 0) return false;
        var top = vals[0];
        var right = n > 1 ? vals[1] : top;
        var bottom = n > 2 ? vals[2] : top;
        var left = n > 3 ? vals[3] : right;
        output.Add(new PropertyValue(longhands[0], top));
        output.Add(new PropertyValue(longhands[1], right));
        output.Add(new PropertyValue(longhands[2], bottom));
        output.Add(new PropertyValue(longhands[3], left));
        return true;
    }

    /// <summary>1–2 个值：第二个省略时等于第一个（<c>gap</c>、<c>overflow</c>）。</summary>
    public static bool Pair(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var a = Parse(longhands[0], ref r);
        if (a is null) return false;
        var b = a;
        if (!r.AtEnd)
        {
            b = Parse(longhands[1], ref r);
            if (b is null || !r.AtEnd) return false;
        }
        output.Add(new PropertyValue(longhands[0], a));
        output.Add(new PropertyValue(longhands[1], b));
        return true;
    }

    /// <summary>&lt;line-width&gt; || &lt;line-style&gt; || &lt;color&gt;，任意顺序、各至多一次。</summary>
    static bool LineParts(IReadOnlyList<CssComponentValue> value, CssPropertyId widthId, CssPropertyId styleId, CssPropertyId colorId,
        out CssValue width, out CssValue style, out CssValue color)
    {
        var r = new ValueReader(value);
        CssValue? w = null, s = null, c = null;
        width = style = color = CssWideKeywordValue.Initial;
        if (r.AtEnd) return false;
        while (!r.AtEnd)
        {
            CssValue? v;
            if (w is null && (v = Parse(widthId, ref r)) is not null) { w = v; continue; }
            if (s is null && (v = Parse(styleId, ref r)) is not null) { s = v; continue; }
            if (c is null && (v = Parse(colorId, ref r)) is not null) { c = v; continue; }
            return false;
        }
        width = w ?? CssWideKeywordValue.Initial;
        style = s ?? CssWideKeywordValue.Initial;
        color = c ?? CssWideKeywordValue.Initial;
        return true;
    }

    /// <summary><c>border-top</c> 等：长属性顺序为 width、style、color。</summary>
    public static bool BorderSide(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        if (!LineParts(value, longhands[0], longhands[1], longhands[2], out var w, out var s, out var c)) return false;
        output.Add(new PropertyValue(longhands[0], w));
        output.Add(new PropertyValue(longhands[1], s));
        output.Add(new PropertyValue(longhands[2], c));
        return true;
    }

    /// <summary><c>border</c>：四边相同（长属性顺序：4 个宽度、4 个样式、4 个颜色）。</summary>
    public static bool Border(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        if (!LineParts(value, longhands[0], longhands[4], longhands[8], out var w, out var s, out var c)) return false;
        for (int i = 0; i < 4; i++) output.Add(new PropertyValue(longhands[i], w));
        for (int i = 4; i < 8; i++) output.Add(new PropertyValue(longhands[i], s));
        for (int i = 8; i < 12; i++) output.Add(new PropertyValue(longhands[i], c));
        return true;
    }

    /// <summary><c>border-radius: &lt;lp&gt;{1,4} [ / &lt;lp&gt;{1,4} ]?</c>（左上、右上、右下、左下）。</summary>
    public static bool BorderRadius(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var h = new CalcNode[4];
        var v = new CalcNode[4];
        int nh = ReadRadii(ref r, h);
        if (nh == 0) return false;
        int nv;
        if (r.TryDelim('/'))
        {
            nv = ReadRadii(ref r, v);
            if (nv == 0) return false;
        }
        else
        {
            nv = nh;
            for (int i = 0; i < nh; i++) v[i] = h[i];
        }
        if (!r.AtEnd) return false;
        Expand(h, nh);
        Expand(v, nv);
        for (int i = 0; i < 4; i++) output.Add(new PropertyValue(longhands[i], new RadiusValue(h[i], v[i])));
        return true;

        static int ReadRadii(ref ValueReader r, CalcNode[] into)
        {
            int n = 0;
            while (n < 4 && !r.AtEnd)
            {
                int save = r.Position;
                var node = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: true);
                if (node is null) { r.Position = save; break; }
                into[n++] = node;
            }
            return n;
        }

        static void Expand(CalcNode[] a, int n)
        {
            // 左上、右上、右下、左下：缺右下取左上，缺左下取右上
            if (n < 2) a[1] = a[0];
            if (n < 3) a[2] = a[0];
            if (n < 4) a[3] = a[1];
        }
    }

    /// <summary>
    /// <c>flex: none | [ &lt;flex-grow&gt; &lt;flex-shrink&gt;? || &lt;flex-basis&gt; ]</c>。
    /// 省略 grow 时为 1，省略 shrink 时为 1，省略 basis 时为 0（CSS Flexbox §7.2）。
    /// </summary>
    public static bool Flex(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        CssValue? grow = null, shrink = null, basis = null;
        if (r.TryIdent("none"))
        {
            if (!r.AtEnd) return false;
            grow = Num(0);
            shrink = Num(0);
            basis = SizeSpecifiedValue.Keyword(SizeKind.Auto);
        }
        else
        {
            if (r.AtEnd) return false;
            while (!r.AtEnd)
            {
                int save = r.Position;
                var v = r.Next();
                if (grow is null && ValueParsers.NumberNode(v, nonNegative: true) is { } g)
                {
                    grow = new NumericValue(g);
                    int save2 = r.Position;
                    var s = r.AtEnd ? null : ValueParsers.NumberNode(r.Next(), nonNegative: true);
                    if (s is null) r.Position = save2;
                    else shrink = new NumericValue(s);
                    continue;
                }
                r.Position = save;
                if (basis is null && Parse(longhands[2], ref r) is { } b)
                {
                    basis = b;
                    continue;
                }
                return false;
            }
            grow ??= Num(1);
            shrink ??= Num(1);
            basis ??= SizeSpecifiedValue.Length(CalcLeaf.Length(CssLength.Px(0)));
        }
        output.Add(new PropertyValue(longhands[0], grow));
        output.Add(new PropertyValue(longhands[1], shrink));
        output.Add(new PropertyValue(longhands[2], basis));
        return true;

        static CssValue Num(double v) => new NumericValue(CalcLeaf.Number(v));
    }

    /// <summary><c>flex-flow: &lt;flex-direction&gt; || &lt;flex-wrap&gt;</c>。</summary>
    public static bool FlexFlow(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        CssValue? dir = null, wrap = null;
        if (r.AtEnd) return false;
        while (!r.AtEnd)
        {
            CssValue? v;
            if (dir is null && (v = Parse(longhands[0], ref r)) is not null) { dir = v; continue; }
            if (wrap is null && (v = Parse(longhands[1], ref r)) is not null) { wrap = v; continue; }
            return false;
        }
        output.Add(new PropertyValue(longhands[0], dir ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[1], wrap ?? CssWideKeywordValue.Initial));
        return true;
    }

    /// <summary>
    /// <c>background</c>：第 1 期只落地 <c>background-color</c> 与 <c>background-image</c>。各层中的
    /// <c>repeat / attachment / box / position [/ size]</c> 被识别并忽略（对应长属性计划在第 2 期支持），颜色只能出现在最后一层。
    /// </summary>
    public static bool Background(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var layers = new List<SpecifiedImage?>();
        CssColor? color = null;
        bool dependent = false;
        while (true)
        {
            SpecifiedImage? image = null;
            bool imageSet = false, positionSet = false, any = false;
            while (!r.AtEnd && r.Peek() is not CssToken { Type: CssTokenType.Comma })
            {
                var v = r.Peek()!;
                if (!imageSet)
                {
                    if (v is CssToken n && n.IsIdent("none")) { r.Advance(); imageSet = any = true; continue; }
                    var img = ValueParsers.Image(v);
                    if (img is not null) { r.Advance(); image = img; imageSet = any = true; continue; }
                }
                if (color is null && CssColorParser.TryParse(v, out var c)) { r.Advance(); color = c; any = true; continue; }
                if (v is CssToken { Type: CssTokenType.Ident } id && IsIgnoredBackgroundKeyword(CssSerializer.ToAsciiLower(id.Value)))
                {
                    r.Advance();
                    any = true;
                    continue;
                }
                if (!positionSet)
                {
                    int save = r.Position;
                    if (ValueParsers.Position(ref r, out _, out _))
                    {
                        positionSet = any = true;
                        if (r.TryDelim('/') && !SkipBackgroundSize(ref r)) return false;
                        continue;
                    }
                    r.Position = save;
                }
                return false;
            }
            if (!any) return false;
            dependent |= image?.IsContextDependent ?? false;
            layers.Add(image);
            if (r.AtEnd) break;
            if (!r.TryComma()) return false;
            // 颜色只能出现在最后一层
            if (color is not null) return false;
        }
        ImageList? computed = null;
        if (!dependent)
        {
            var arr = new CssImage?[layers.Count];
            var ctx = new LengthContext(16, 16, new CssEnvironment());
            for (int i = 0; i < arr.Length; i++) arr[i] = layers[i]?.Compute(ctx);
            computed = arr.Length == 1 && arr[0] is null ? ImageList.None : new ImageList(arr);
        }
        output.Add(new PropertyValue(longhands[0], color is { } col ? new ColorValue(col) : CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[1], new ImageListValue(layers.ToArray(), computed)));
        return true;
    }

    static bool IsIgnoredBackgroundKeyword(string k)
        => k is "repeat" or "repeat-x" or "repeat-y" or "no-repeat" or "space" or "round"
            or "scroll" or "fixed" or "local" or "border-box" or "padding-box" or "content-box" or "text";

    /// <summary>&lt;bg-size&gt; = [ &lt;lp&gt; | auto ]{1,2} | cover | contain。</summary>
    static bool SkipBackgroundSize(ref ValueReader r)
    {
        if (r.TryIdent("cover") || r.TryIdent("contain")) return true;
        int n = 0;
        while (n < 2 && !r.AtEnd)
        {
            int save = r.Position;
            var v = r.Next();
            if (v is CssToken t && t.IsIdent("auto") || ValueParsers.LengthPercentageNode(v, nonNegative: true) is not null) { n++; continue; }
            r.Position = save;
            break;
        }
        return n > 0;
    }

    /// <summary>
    /// <c>font: [ &lt;font-style&gt; || &lt;font-variant-css2&gt; || &lt;font-weight&gt; || &lt;font-stretch-css3&gt; ]?
    /// &lt;font-size&gt; [ / &lt;line-height&gt; ]? &lt;font-family&gt;</c>。系统字体关键字不支持。
    /// 长属性顺序：style、weight、stretch、size、line-height、family。
    /// </summary>
    public static bool Font(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        CssValue? style = null, weight = null, stretch = null;
        int normals = 0;
        CssValue? size = null;
        for (int guard = 0; guard < 5 && !r.AtEnd; guard++)
        {
            int save = r.Position;
            var v = r.Next();
            if (v is CssToken t && t.IsIdent("normal")) { normals++; continue; }
            if (v is CssToken sc && sc.IsIdent("small-caps")) continue;
            r.Position = save;
            CssValue? x;
            if (style is null && (x = Parse(longhands[0], ref r)) is not null) { style = x; continue; }
            if (stretch is null && v is CssToken { Type: CssTokenType.Ident } st && ValueParsers.StretchKeyword(CssSerializer.ToAsciiLower(st.Value)) > 0)
            {
                stretch = Parse(longhands[2], ref r);
                continue;
            }
            // font-weight 的数值与 font-size 不冲突（后者需要单位）；bolder/lighter 也允许
            if (weight is null && (x = Parse(longhands[1], ref r)) is not null) { weight = x; continue; }
            break;
        }
        if (normals + (style is null ? 0 : 1) + (weight is null ? 0 : 1) + (stretch is null ? 0 : 1) > 4) return false;
        size = Parse(longhands[3], ref r);
        if (size is null) return false;
        CssValue lineHeight = CssWideKeywordValue.Initial;
        if (r.TryDelim('/'))
        {
            var lh = Parse(longhands[4], ref r);
            if (lh is null) return false;
            lineHeight = lh;
        }
        var family = ValueParsers.FontFamily(ref r);
        if (family is null || !r.AtEnd) return false;
        output.Add(new PropertyValue(longhands[0], style ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[1], weight ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[2], stretch ?? CssWideKeywordValue.Initial));
        output.Add(new PropertyValue(longhands[3], size));
        output.Add(new PropertyValue(longhands[4], lineHeight));
        output.Add(new PropertyValue(longhands[5], family));
        return true;
    }

    /// <summary><c>all</c> 只接受 CSS 全局关键字，由声明解析器在识别全局关键字时处理；走到这里说明值无效。</summary>
    public static bool All(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands) => false;
}

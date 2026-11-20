using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 长属性的值解析器（手写部分：语法不规则，按类型复用）。每个方法从 <see cref="ValueReader"/> 消费一个值，
/// 失败返回 null（调用方回溯）；是否已消费完由生成的分派代码检查。
/// </summary>
internal static class ValueParsers
{
    // ================= 基本类型 =================

    /// <summary>长度/百分比（含 calc），不接受数（除 0）。</summary>
    public static CalcNode? LengthPercentageNode(CssComponentValue? v, bool nonNegative, bool allowPercent = true)
    {
        if (v is null) return null;
        var node = CalcParser.ParseComponent(v, allowPercent ? PercentMode.Length : PercentMode.None, allowUnitlessZero: true);
        if (node is null) return null;
        if (node.Category is not (CalcCategory.Length or CalcCategory.Percent or CalcCategory.LengthPercent)) return null;
        if (nonNegative && node is CalcLeaf { Value: < 0 }) return null;
        return node;
    }

    /// <summary>数（含 calc）。</summary>
    public static CalcNode? NumberNode(CssComponentValue? v, bool nonNegative)
    {
        if (v is null) return null;
        var node = CalcParser.ParseComponent(v, PercentMode.None, allowUnitlessZero: false);
        if (node is null || node.Category != CalcCategory.Number) return null;
        if (nonNegative && node is CalcLeaf { Value: < 0 }) return null;
        return node;
    }

    /// <summary>角度（度）。单独的 0 视为 0deg。</summary>
    public static bool TryAngle(CssComponentValue? v, out double degrees)
    {
        degrees = 0;
        if (v is CssToken { Type: CssTokenType.Number, Number: 0 }) return true;
        if (v is null) return false;
        var node = CalcParser.ParseComponent(v, PercentMode.None, allowUnitlessZero: false);
        if (node is null || node.Category != CalcCategory.Angle) return false;
        degrees = CalcMath.FiniteDouble(node.Compute(new LengthContext(16, 16, s_env)).Number);
        return true;
    }

    /// <summary>时间（毫秒）。</summary>
    public static bool TryTime(CssComponentValue? v, out double milliseconds)
    {
        milliseconds = 0;
        if (v is null) return false;
        var node = CalcParser.ParseComponent(v, PercentMode.None, allowUnitlessZero: false);
        if (node is null || node.Category != CalcCategory.Time) return false;
        milliseconds = CalcMath.FiniteDouble(node.Compute(new LengthContext(16, 16, s_env)).Number);
        return true;
    }

    static readonly CssEnvironment s_env = new();

    static CssValue? Numeric(CalcNode? node) => node is null ? null : new NumericValue(node);

    // ================= 按属性类型 =================

    public static CssValue? Keyword(ref ValueReader r, KeywordTable table)
    {
        if (r.Next() is CssToken { Type: CssTokenType.Ident } t && table.TryGet(t.Value, out int v)) return KeywordValue.Of(v);
        return null;
    }

    /// <summary><c>cursor: [ &lt;url&gt; [&lt;x&gt; &lt;y&gt;]? , ]* &lt;keyword&gt;</c>：图像光标暂不支持，只保留回退关键字。</summary>
    public static CssValue? Cursor(ref ValueReader r, KeywordTable table)
    {
        while (true)
        {
            var v = r.Peek();
            if (v is CssToken { Type: CssTokenType.Url } || v is CssFunction f && CssSerializer.EqualsIgnoreAsciiCase(f.Name, "url"))
            {
                r.Advance();
                if (r.Peek() is CssToken { Type: CssTokenType.Number })
                {
                    r.Advance();
                    if (r.Next() is not CssToken { Type: CssTokenType.Number }) return null;
                }
                if (!r.TryComma()) return null;
                continue;
            }
            return Keyword(ref r, table);
        }
    }

    public static CssValue? Size(ref ValueReader r, ValueFlags flags)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            switch (CssSerializer.ToAsciiLower(id.Value))
            {
                case "auto" when (flags & ValueFlags.AllowAuto) != 0: return SizeSpecifiedValue.Keyword(SizeKind.Auto);
                case "none" when (flags & ValueFlags.AllowNone) != 0: return SizeSpecifiedValue.Keyword(SizeKind.None);
                case "normal" when (flags & ValueFlags.AllowNormal) != 0: return SizeSpecifiedValue.Keyword(SizeKind.Normal);
                case "content" when (flags & ValueFlags.AllowContent) != 0: return SizeSpecifiedValue.Keyword(SizeKind.Content);
                case "min-content" when (flags & ValueFlags.AllowIntrinsic) != 0: return SizeSpecifiedValue.Keyword(SizeKind.MinContent);
                case "max-content" when (flags & ValueFlags.AllowIntrinsic) != 0: return SizeSpecifiedValue.Keyword(SizeKind.MaxContent);
                case "fit-content" when (flags & ValueFlags.AllowIntrinsic) != 0: return SizeSpecifiedValue.Keyword(SizeKind.FitContent);
                default: return null;
            }
        }
        bool nonNegative = (flags & ValueFlags.NonNegative) != 0;
        var node = LengthPercentageNode(v, nonNegative);
        return node is null ? null : SizeSpecifiedValue.Length(node, nonNegative);
    }

    /// <summary>&lt;length&gt;（可为负，不接受百分比），如 <c>scroll-margin</c>。</summary>
    public static CssValue? SignedLength(ref ValueReader r) => Numeric(LengthPercentageNode(r.Next(), nonNegative: false, allowPercent: false));

    public static CssValue? LengthPercentage(ref ValueReader r, ValueFlags flags)
        => Numeric(LengthPercentageNode(r.Next(), (flags & ValueFlags.NonNegative) != 0));

    /// <summary>&lt;line-width&gt; = &lt;length [0,∞]&gt; | thin | medium | thick。</summary>
    public static CssValue? LineWidth(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            switch (CssSerializer.ToAsciiLower(id.Value))
            {
                case "thin": return new NumericValue(CalcLeaf.Length(CssLength.Px(1)));
                case "medium": return new NumericValue(CalcLeaf.Length(CssLength.Px(3)));
                case "thick": return new NumericValue(CalcLeaf.Length(CssLength.Px(5)));
                default: return null;
            }
        }
        return Numeric(LengthPercentageNode(v, nonNegative: true, allowPercent: false));
    }

    public static CssValue? Color(ref ValueReader r)
    {
        var v = r.Next();
        return v is not null && CssColorParser.TryParse(v, out var c) ? new ColorValue(c) : null;
    }

    public static CssValue? Number(ref ValueReader r, ValueFlags flags) => Numeric(NumberNode(r.Next(), (flags & ValueFlags.NonNegative) != 0));

    /// <summary>&lt;alpha-value&gt; = &lt;number&gt; | &lt;percentage&gt;，计算值钳制到 [0, 1]。</summary>
    public static CssValue? Alpha(ref ValueReader r)
    {
        var v = r.Next();
        if (v is null) return null;
        var node = CalcParser.ParseComponent(v, PercentMode.Number, allowUnitlessZero: false);
        return node is { Category: CalcCategory.Number } ? new NumericValue(node) : null;
    }

    public static CssValue? IntegerOrAuto(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken t && t.IsIdent("auto")) return IntegerOrAutoValue.Auto;
        if (v is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Number }) return null;
        var node = NumberNode(v, nonNegative: false);
        return node is null ? null : new IntegerOrAutoValue(node);
    }

    /// <summary>单个角的半径：&lt;length-percentage [0,∞]&gt;{1,2}。</summary>
    public static CssValue? Radius(ref ValueReader r)
    {
        var h = LengthPercentageNode(r.Next(), nonNegative: true);
        if (h is null) return null;
        int save = r.Position;
        var vNode = r.AtEnd ? null : LengthPercentageNode(r.Next(), nonNegative: true);
        if (vNode is null) r.Position = save;
        return new RadiusValue(h, vNode ?? h);
    }

    // ================= 字体 =================

    public static CssValue? FontFamily(ref ValueReader r)
    {
        var list = new List<FontFamilyName>();
        while (true)
        {
            var name = FamilyName(ref r);
            if (name is null) return null;
            list.Add(name.Value);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new FontFamilyValue(new FontFamilyList(list.ToArray()));
    }

    /// <summary>一个字体族：字符串，或空白分隔的标识符序列（单个通用族关键字识别为通用族）。</summary>
    internal static FontFamilyName? FamilyName(ref ValueReader r)
    {
        var first = r.Next();
        if (first is CssToken { Type: CssTokenType.String } s) return new FontFamilyName(s.Value, GenericFontFamily.None);
        if (first is not CssToken { Type: CssTokenType.Ident } id) return null;
        var lower = CssSerializer.ToAsciiLower(id.Value);
        if (CssWideKeywordValue.TryParse(lower, out _) || lower == "default") return null;
        string? joined = null;
        while (r.Peek() is CssToken { Type: CssTokenType.Ident } more)
        {
            // <custom-ident>+ 中的每个标识符都不能是全局关键字或 default（CSS Values 4 §4.2）
            var moreLower = CssSerializer.ToAsciiLower(more.Value);
            if (CssWideKeywordValue.TryParse(moreLower, out _) || moreLower == "default") return null;
            r.Advance();
            joined = (joined ?? id.Value) + " " + more.Value;
        }
        if (joined is not null) return new FontFamilyName(joined, GenericFontFamily.None);
        var generic = FontFamilyName.ParseGeneric(lower);
        return generic != GenericFontFamily.None ? new FontFamilyName(lower, generic) : new FontFamilyName(id.Value, GenericFontFamily.None);
    }

    public static CssValue? FontSize(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            var lower = CssSerializer.ToAsciiLower(id.Value);
            if (lower == "larger") return new FontSizeValue(FontSizeValue.SizeKind.Larger, 1.2, null);
            if (lower == "smaller") return new FontSizeValue(FontSizeValue.SizeKind.Smaller, 1 / 1.2, null);
            double f = AbsoluteSizeFactor(lower);
            return f > 0 ? new FontSizeValue(FontSizeValue.SizeKind.Absolute, f, null) : null;
        }
        var node = LengthPercentageNode(v, nonNegative: true);
        return node is null ? null : new FontSizeValue(FontSizeValue.SizeKind.Length, 0, node);
    }

    /// <summary>&lt;absolute-size&gt; 相对 medium 的倍数（CSS Fonts 4 §2.5 的比例表）；未知返回 0。</summary>
    internal static double AbsoluteSizeFactor(string lower) => lower switch
    {
        "xx-small" => 3.0 / 5,
        "x-small" => 3.0 / 4,
        "small" => 8.0 / 9,
        "medium" => 1,
        "large" => 6.0 / 5,
        "x-large" => 3.0 / 2,
        "xx-large" => 2,
        "xxx-large" => 3,
        _ => 0,
    };

    public static CssValue? FontWeight(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            switch (CssSerializer.ToAsciiLower(id.Value))
            {
                case "normal": return new FontWeightValue(CalcLeaf.Number(400), 0);
                case "bold": return new FontWeightValue(CalcLeaf.Number(700), 0);
                case "bolder": return FontWeightValue.Bolder;
                case "lighter": return FontWeightValue.Lighter;
                default: return null;
            }
        }
        var node = NumberNode(v, nonNegative: true);
        if (node is null) return null;
        if (node is CalcLeaf leaf && (leaf.Value < 1 || leaf.Value > 1000)) return null;
        return new FontWeightValue(node, 0);
    }

    public static CssValue? FontStretch(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            double p = StretchKeyword(CssSerializer.ToAsciiLower(id.Value));
            return p > 0 ? new NumericValue(CalcLeaf.Percent(p)) : null;
        }
        if (v is null) return null;
        var node = CalcParser.ParseComponent(v, PercentMode.Length, allowUnitlessZero: false);
        if (node is null || node.Category != CalcCategory.Percent || node is CalcLeaf { Value: < 0 }) return null;
        return new NumericValue(node);
    }

    /// <summary>&lt;font-stretch-css3&gt; 关键字对应的百分比；未知返回 0。</summary>
    internal static double StretchKeyword(string lower) => lower switch
    {
        "ultra-condensed" => 50,
        "extra-condensed" => 62.5,
        "condensed" => 75,
        "semi-condensed" => 87.5,
        "normal" => 100,
        "semi-expanded" => 112.5,
        "expanded" => 125,
        "extra-expanded" => 150,
        "ultra-expanded" => 200,
        _ => 0,
    };

    public static CssValue? LineHeight(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken t && t.IsIdent("normal")) return LineHeightValue.Normal;
        if (v is null) return null;
        var node = CalcParser.ParseComponent(v, PercentMode.Length, allowUnitlessZero: false);
        if (node is null || node is CalcLeaf { Value: < 0 }) return null;
        return node.Category switch
        {
            CalcCategory.Number => new LineHeightValue(LineHeightKind.Number, node),
            CalcCategory.Length or CalcCategory.Percent or CalcCategory.LengthPercent => new LineHeightValue(LineHeightKind.Length, node),
            _ => null,
        };
    }

    // ================= 图像与渐变 =================

    public static CssValue? ImageList(ref ValueReader r)
    {
        var layers = new List<SpecifiedImage?>();
        bool dependent = false;
        while (true)
        {
            var v = r.Next();
            if (v is CssToken t && t.IsIdent("none")) layers.Add(null);
            else
            {
                var img = Image(v);
                if (img is null) return null;
                dependent |= img.IsContextDependent;
                layers.Add(img);
            }
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        ImageList? computed = null;
        if (!dependent)
        {
            var arr = new CssImage?[layers.Count];
            var ctx = new LengthContext(16, 16, s_env);
            for (int i = 0; i < arr.Length; i++) arr[i] = layers[i]?.Compute(ctx);
            computed = arr.Length == 1 && arr[0] is null ? Values.ImageList.None : new ImageList(arr);
        }
        return new ImageListValue(layers.ToArray(), computed);
    }

    /// <summary>&lt;image&gt;：<c>url()</c> 或渐变函数。</summary>
    internal static SpecifiedImage? Image(CssComponentValue? v)
    {
        switch (v)
        {
            case CssToken { Type: CssTokenType.Url } u:
                return new SpecifiedUrlImage(u.Value);
            case CssFunction f:
                switch (CssSerializer.ToAsciiLower(f.Name))
                {
                    case "url":
                    {
                        var r = new ValueReader(f.Arguments);
                        if (r.Next() is CssToken { Type: CssTokenType.String } s && r.AtEnd) return new SpecifiedUrlImage(s.Value);
                        return null;
                    }
                    case "linear-gradient": return LinearGradient(f.Arguments, repeating: false);
                    case "repeating-linear-gradient": return LinearGradient(f.Arguments, repeating: true);
                    case "radial-gradient": return RadialGradient(f.Arguments, repeating: false);
                    case "repeating-radial-gradient": return RadialGradient(f.Arguments, repeating: true);
                    case "conic-gradient": return ConicGradient(f.Arguments, repeating: false);
                    case "repeating-conic-gradient": return ConicGradient(f.Arguments, repeating: true);
                    default: return null;
                }
            default:
                return null;
        }
    }

    /// <summary>&lt;color-interpolation-method&gt; = <c>in &lt;colorspace&gt; [&lt;hue-interpolation-method&gt; hue]?</c>。</summary>
    static bool TryInterpolation(ref ValueReader r, ref CssColorSpace? space, ref CssHueInterpolation hue, out bool error)
    {
        error = false;
        if (space is not null || !r.TryIdent("in")) return false;
        var name = r.PeekIdentLower();
        if (name is null || !CssColorParser.TryColorSpace(name, allowPolar: true, out var s)) { error = true; return false; }
        r.Advance();
        var method = r.PeekIdentLower();
        if (method is "shorter" or "longer" or "increasing" or "decreasing")
        {
            if (s is not (CssColorSpace.Hsl or CssColorSpace.Hwb or CssColorSpace.Lch or CssColorSpace.Oklch)) { error = true; return false; }
            r.Advance();
            if (!r.TryIdent("hue")) { error = true; return false; }
            hue = method switch
            {
                "longer" => CssHueInterpolation.Longer,
                "increasing" => CssHueInterpolation.Increasing,
                "decreasing" => CssHueInterpolation.Decreasing,
                _ => CssHueInterpolation.Shorter,
            };
        }
        space = s;
        return true;
    }

    static SpecifiedImage? LinearGradient(IReadOnlyList<CssComponentValue> args, bool repeating)
    {
        var r = new ValueReader(args);
        double angle = 180;
        var corner = GradientCorner.None;
        CssColorSpace? space = null;
        var hue = CssHueInterpolation.Shorter;
        bool interp = TryInterpolation(ref r, ref space, ref hue, out bool bad);
        if (bad) return null;
        if (interp && r.TryComma())
        {
            var s0 = ColorStops(ref r);
            return s0 is null ? null : new SpecifiedLinearGradient(angle, corner, repeating, s0) { Interpolation = space, Hue = hue };
        }
        int save = r.Position;
        if (r.TryIdent("to"))
        {
            int h = 0, v = 0;
            for (int i = 0; i < 2; i++)
            {
                switch (r.PeekIdentLower())
                {
                    case "left" when h == 0: h = -1; break;
                    case "right" when h == 0: h = 1; break;
                    case "top" when v == 0: v = -1; break;
                    case "bottom" when v == 0: v = 1; break;
                    default: goto done;
                }
                r.Advance();
            }
        done:
            if (h == 0 && v == 0) return null;
            if (h != 0 && v != 0)
                corner = v < 0 ? (h < 0 ? GradientCorner.TopLeft : GradientCorner.TopRight) : (h < 0 ? GradientCorner.BottomLeft : GradientCorner.BottomRight);
            else angle = v < 0 ? 0 : v > 0 ? 180 : h > 0 ? 90 : 270;
            if (!interp) { TryInterpolation(ref r, ref space, ref hue, out bad); if (bad) return null; }
            if (!r.TryComma()) return null;
        }
        else if (TryAngle(r.Next(), out var deg))
        {
            angle = deg;
            if (!interp) { TryInterpolation(ref r, ref space, ref hue, out bad); if (bad) return null; }
            if (!r.TryComma()) return null;
        }
        else
        {
            r.Position = save;
            if (interp) return null;
        }
        var stops = ColorStops(ref r);
        return stops is null ? null : new SpecifiedLinearGradient(angle, corner, repeating, stops) { Interpolation = space, Hue = hue };
    }

    /// <summary><c>conic-gradient( [ [ from &lt;angle&gt; ]? [ at &lt;position&gt; ]? ] || &lt;color-interpolation-method&gt; , &lt;angular-color-stop-list&gt; )</c></summary>
    static SpecifiedImage? ConicGradient(IReadOnlyList<CssComponentValue> args, bool repeating)
    {
        var r = new ValueReader(args);
        double from = 0;
        CalcNode cx = CalcLeaf.Percent(50), cy = CalcLeaf.Percent(50);
        CssColorSpace? space = null;
        var hue = CssHueInterpolation.Shorter;
        bool any = false;
        if (TryInterpolation(ref r, ref space, ref hue, out bool bad)) any = true;
        if (bad) return null;
        if (r.TryIdent("from"))
        {
            if (!TryAngle(r.Next(), out from)) return null;
            any = true;
        }
        if (r.TryIdent("at"))
        {
            if (!Position(ref r, out cx, out cy)) return null;
            any = true;
        }
        if (space is null && TryInterpolation(ref r, ref space, ref hue, out bad)) any = true;
        if (bad) return null;
        if (any && !r.TryComma()) return null;
        var stops = ColorStops(ref r, angular: true);
        return stops is null ? null : new SpecifiedConicGradient(from, cx, cy, repeating, stops) { Interpolation = space, Hue = hue };
    }

    static SpecifiedImage? RadialGradient(IReadOnlyList<CssComponentValue> args, bool repeating)
    {
        var r = new ValueReader(args);
        CssColorSpace? space = null;
        var hue = CssHueInterpolation.Shorter;
        bool interp = TryInterpolation(ref r, ref space, ref hue, out bool bad);
        if (bad) return null;
        RadialShape? shape = null;
        var size = RadialSizeKeyword.FarthestCorner;
        bool sizeSet = false;
        CalcNode? rx = null, ry = null;
        CalcNode cx = CalcLeaf.Percent(50), cy = CalcLeaf.Percent(50);
        bool any = false;
        // [ <radial-shape> || <radial-size> ]?
        for (int round = 0; round < 2; round++)
        {
            var ident = r.PeekIdentLower();
            if (shape is null && (ident is "circle" or "ellipse"))
            {
                shape = ident == "circle" ? RadialShape.Circle : RadialShape.Ellipse;
                r.Advance();
                any = true;
                continue;
            }
            if (!sizeSet)
            {
                var kw = ident switch
                {
                    "closest-side" => RadialSizeKeyword.ClosestSide,
                    "closest-corner" => RadialSizeKeyword.ClosestCorner,
                    "farthest-side" => RadialSizeKeyword.FarthestSide,
                    "farthest-corner" => RadialSizeKeyword.FarthestCorner,
                    _ => RadialSizeKeyword.Explicit,
                };
                if (kw != RadialSizeKeyword.Explicit)
                {
                    size = kw;
                    sizeSet = true;
                    any = true;
                    r.Advance();
                    continue;
                }
                int save = r.Position;
                var a = LengthPercentageNode(r.Next(), nonNegative: true);
                if (a is not null)
                {
                    int save2 = r.Position;
                    var b = LengthPercentageNode(r.Next(), nonNegative: true);
                    if (b is null) r.Position = save2;
                    rx = a;
                    ry = b;
                    size = RadialSizeKeyword.Explicit;
                    sizeSet = true;
                    any = true;
                    continue;
                }
                r.Position = save;
            }
            break;
        }
        if (size == RadialSizeKeyword.Explicit)
        {
            // 圆：只能是一个长度（不能是百分比）；椭圆：两个长度百分比
            if (ry is null)
            {
                if (shape == RadialShape.Ellipse || rx!.Category != CalcCategory.Length) return null;
                shape = RadialShape.Circle;
            }
            else
            {
                if (shape == RadialShape.Circle) return null;
                shape = RadialShape.Ellipse;
            }
        }
        if (r.TryIdent("at"))
        {
            if (!Position(ref r, out cx, out cy)) return null;
            any = true;
        }
        if (!interp && TryInterpolation(ref r, ref space, ref hue, out bad)) interp = true;
        if (bad) return null;
        if ((any || interp) && !r.TryComma()) return null;
        var stops = ColorStops(ref r);
        if (stops is null) return null;
        return new SpecifiedRadialGradient(shape ?? RadialShape.Ellipse, size, rx, ry, cx, cy, repeating, stops) { Interpolation = space, Hue = hue };
    }

    /// <summary>&lt;color-stop-list&gt;：至少两个色标；插值提示只能出现在两个色标之间。</summary>
    static SpecifiedStop[]? ColorStops(ref ValueReader r, bool angular = false)
    {
        var stops = new List<SpecifiedStop>();
        int colorCount = 0;
        bool lastWasHint = true;
        while (true)
        {
            var v = r.Next();
            if (v is null) return null;
            if (CssColorParser.TryParse(v, out var color))
            {
                colorCount++;
                int save = r.Position;
                var p1 = r.AtEnd ? null : StopPosition(r.Next(), angular);
                if (p1 is null)
                {
                    r.Position = save;
                    stops.Add(new SpecifiedStop(color, null, false));
                }
                else
                {
                    stops.Add(new SpecifiedStop(color, p1, false));
                    save = r.Position;
                    var p2 = r.AtEnd ? null : StopPosition(r.Next(), angular);
                    if (p2 is null) r.Position = save;
                    else stops.Add(new SpecifiedStop(color, p2, false));
                }
                lastWasHint = false;
            }
            else
            {
                if (lastWasHint) return null;
                var hint = StopPosition(v, angular);
                if (hint is null) return null;
                stops.Add(new SpecifiedStop(default, hint, true));
                lastWasHint = true;
            }
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        if (colorCount < 2 || lastWasHint) return null;
        return stops.ToArray();
    }

    /// <summary>色标位置：长度百分比；锥形渐变为角度或百分比（角度换算为整圈的百分比）。</summary>
    static CalcNode? StopPosition(CssComponentValue? v, bool angular)
    {
        if (!angular) return LengthPercentageNode(v, nonNegative: false);
        if (v is CssToken { Type: CssTokenType.Percentage } p) return CalcLeaf.Percent(p.Number);
        if (v is CssToken { Type: CssTokenType.Number, Number: 0 }) return CalcLeaf.Percent(0);
        return TryAngle(v, out var deg) ? CalcLeaf.Percent(deg / 3.6) : null;
    }

    /// <summary><c>&lt;position&gt;</c> 作为属性值（<c>object-position</c>）：1、2、4 值形式（CSS Values 4 不含 3 值形式）。</summary>
    public static CssValue? Position(ref ValueReader r)
        => Position(ref r, out var x, out var y) ? new PositionSpecifiedValue(x, y) : null;

    /// <summary>
    /// <c>aspect-ratio: auto || &lt;ratio&gt;</c>，<c>&lt;ratio&gt; = &lt;number [0,∞]&gt; [ / &lt;number [0,∞]&gt; ]?</c>（数可为 <c>calc()</c>）。
    /// <c>auto</c> 可在比值之前或之后；退化比值（含 0）在语法上有效。
    /// </summary>
    public static CssValue? AspectRatio(ref ValueReader r)
    {
        bool auto = false;
        CalcNode? w = null, h = null;
        for (int i = 0; i < 2 && !r.AtEnd; i++)
        {
            if (!auto && r.TryIdent("auto"))
            {
                auto = true;
                continue;
            }
            if (w is not null) return null;
            w = NumberNode(r.Next(), nonNegative: true);
            if (w is null) return null;
            if (r.TryDelim('/'))
            {
                h = NumberNode(r.Next(), nonNegative: true);
                if (h is null) return null;
            }
        }
        if (w is null) return auto ? AspectRatioSpecifiedValue.Auto : null;
        return new AspectRatioSpecifiedValue(auto, w, h);
    }

    /// <summary>
    /// &lt;position&gt;（CSS Values 4 的 &lt;bg-position&gt; 子集）：1 值、2 值与 4 值形式。百分比相对定位区域。
    /// </summary>
    internal static bool Position(ref ValueReader r, out CalcNode x, out CalcNode y)
    {
        x = y = CalcLeaf.Percent(50);
        var items = new List<(string? Keyword, CalcNode? Length)>(4);
        while (items.Count < 4 && !r.AtEnd)
        {
            int save = r.Position;
            var v = r.Next();
            if (v is CssToken { Type: CssTokenType.Ident } id)
            {
                var k = CssSerializer.ToAsciiLower(id.Value);
                if (k is "left" or "right" or "top" or "bottom" or "center") { items.Add((k, null)); continue; }
                r.Position = save;
                break;
            }
            var node = LengthPercentageNode(v, nonNegative: false);
            if (node is null) { r.Position = save; break; }
            items.Add((null, node));
        }
        switch (items.Count)
        {
            case 1:
            {
                var (k, l) = items[0];
                if (l is not null) { x = l; return true; }
                if (k is "top" or "bottom") { y = Edge(k, null); return true; }
                x = Edge(k!, null);
                return true;
            }
            case 2:
            {
                var (k1, l1) = items[0];
                var (k2, l2) = items[1];
                // 关键字可交换顺序：top left / left top
                if (k1 is "top" or "bottom" || k2 is "left" or "right")
                {
                    if (l1 is not null || l2 is not null) return false;
                    if (k1 is "left" or "right" || k2 is "top" or "bottom") return false;
                    (k1, k2) = (k2, k1);
                }
                x = l1 ?? Edge(k1!, null);
                if (k1 is "top" or "bottom") return false;
                y = l2 ?? Edge(k2!, null);
                if (k2 is "left" or "right") return false;
                return true;
            }
            case 4:
            {
                var (k1, l1) = items[0];
                var (k2, l2) = items[1];
                var (k3, l3) = items[2];
                var (k4, l4) = items[3];
                if (k1 is null || l1 is not null || k2 is not null || l2 is null || k3 is null || l3 is not null || k4 is not null || l4 is null)
                    return false;
                if (k1 == "center" || k3 == "center") return false;
                if (k1 is "top" or "bottom")
                {
                    (k1, k3) = (k3, k1);
                    (l2, l4) = (l4, l2);
                }
                if (k1 is not ("left" or "right") || k3 is not ("top" or "bottom")) return false;
                x = Edge(k1, l2);
                y = Edge(k3, l4);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>边缘关键字 + 偏移：<c>right 10px</c> = <c>calc(100% - 10px)</c>。</summary>
    static CalcNode Edge(string keyword, CalcNode? offset)
    {
        switch (keyword)
        {
            case "left":
            case "top":
                return offset ?? CalcLeaf.Percent(0);
            case "right":
            case "bottom":
                if (offset is null) return CalcLeaf.Percent(100);
                var neg = new CalcOperation(CalcOperation.Op.Negate, offset.Category, new[] { offset });
                return new CalcOperation(CalcOperation.Op.Sum, CalcCategory.LengthPercent, new CalcNode[] { CalcLeaf.Percent(100), neg });
            default:
                return CalcLeaf.Percent(50);
        }
    }

    // ================= 阴影 =================

    public static CssValue? ShadowList(ref ValueReader r)
    {
        if (r.TryIdent("none")) return ShadowListValue.None;
        var list = new List<SpecifiedShadow>();
        while (true)
        {
            var s = Shadow(ref r);
            if (s is null) return null;
            list.Add(s.Value);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new ShadowListValue(list.ToArray());
    }

    /// <summary>&lt;shadow&gt; = &lt;color&gt;? &amp;&amp; &lt;length&gt;{2,4} &amp;&amp; inset?（长度必须连续，模糊半径非负）。</summary>
    static SpecifiedShadow? Shadow(ref ValueReader r)
    {
        bool inset = false, insetSet = false;
        CssColor? color = null;
        CalcNode?[]? lengths = null;
        while (true)
        {
            var v = r.Peek();
            if (v is null || v is CssToken { Type: CssTokenType.Comma }) break;
            if (!insetSet && v is CssToken t && t.IsIdent("inset"))
            {
                r.Advance();
                inset = insetSet = true;
                continue;
            }
            if (color is null && CssColorParser.TryParse(v, out var c))
            {
                r.Advance();
                color = c;
                continue;
            }
            if (lengths is null)
            {
                lengths = new CalcNode?[4];
                int n = 0;
                while (n < 4)
                {
                    int save = r.Position;
                    var node = r.AtEnd ? null : LengthPercentageNode(r.Next(), nonNegative: false, allowPercent: false);
                    if (node is null) { r.Position = save; break; }
                    lengths[n++] = node;
                }
                if (n < 2) return null;
                if (lengths[2] is CalcLeaf { Value: < 0 }) return null;
                continue;
            }
            return null;
        }
        if (lengths is null) return null;
        return new SpecifiedShadow(lengths[0]!, lengths[1]!, lengths[2], lengths[3], color ?? CssColor.CurrentColor, inset);
    }
}

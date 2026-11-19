using System;
using AwAUI.Css.Values;

namespace AwAUI.Css.Cascade;

/// <summary>
/// 指定值 → 计算值（CSS Cascade 4 §4.4）：相对长度换算为 px（百分比与含百分比的 calc 保留到布局），
/// <c>font-size</c> 相对父元素，<c>line-height</c> 的数保留为数，<c>currentColor</c> 保留为关键字。
/// 同一实例在一次解析中复用，不是线程安全的。
/// </summary>
internal sealed class StyleComputer
{
    public StyleComputer(CssEnvironment environment) => Environment = environment;

    public CssEnvironment Environment { get; }

    /// <summary>父元素样式；构造初始样式时为 null。</summary>
    public ComputedStyle? Parent { get; private set; }

    /// <summary>本元素当前的字体大小（em 的基准）：计算 <c>font-size</c> 之前等于父元素的字体大小。</summary>
    public double FontSize { get; set; }

    /// <summary>rem 的基准。</summary>
    public double RootFontSize { get; set; }

    /// <summary>计算 font-size 本身时 rem 的基准（根元素上为初始字体大小）。</summary>
    double _rootFontSizeForFontSize;

    public ComputedStyleFlags Flags { get; set; }

    public void Begin(ComputedStyle? parent, bool isRoot)
    {
        Parent = parent;
        double initial = Environment.DefaultFontSize;
        FontSize = parent?.FontSize ?? initial;
        if (isRoot || parent is null)
        {
            _rootFontSizeForFontSize = initial;
            RootFontSize = initial;
        }
        else
        {
            _rootFontSizeForFontSize = parent.RootFontSize;
            RootFontSize = parent.RootFontSize;
        }
        Flags = ComputedStyleFlags.None;
    }

    LengthContext Context => new(FontSize, RootFontSize, Environment);

    CalcResult Eval(CalcNode node, in LengthContext context)
    {
        if (node.UsesViewport) Flags |= ComputedStyleFlags.DependsOnViewport;
        return node.Compute(context);
    }

    LengthPercentage LengthPercentage(CalcNode node, bool nonNegative)
    {
        var r = Eval(node, Context);
        var lp = r.ToLengthPercentage();
        // 负值钳制：calc() 的结果超出属性允许范围时按规范钳制（只对不含百分比的值在此处处理，其余在布局时处理）
        if (nonNegative && lp.IsFixed && lp.Length < 0) return Values.LengthPercentage.Zero;
        return lp;
    }

    // ================= 生成代码调用的计算函数 =================

    public int Keyword(CssValue v) => ((KeywordValue)v).Value;

    public SizeValue ComputeSize(CssValue v)
    {
        var s = (SizeSpecifiedValue)v;
        if (s.Kind != SizeKind.LengthPercentage) return new SizeValue(s.Kind, default);
        return SizeValue.From(LengthPercentage(s.Node!, s.NonNegative));
    }

    public LengthPercentage ComputeLengthPercentage(CssValue v, bool nonNegative) => LengthPercentage(((NumericValue)v).Node, nonNegative);

    /// <summary>绝对长度（px，非负），如边框宽度。</summary>
    public float ComputeLength(CssValue v)
    {
        var r = Eval(((NumericValue)v).Node, Context);
        return Math.Max(0, CalcMath.Finite(r.Px));
    }

    public CssColor ComputeColor(CssValue v) => ((ColorValue)v).Color;

    /// <summary>数。<paramref name="nonNegative"/> 时 calc() 的负结果钳制为 0（CSS Values 4 §10.12：超出属性允许范围的计算结果被钳制）。</summary>
    public float ComputeNumber(CssValue v, bool nonNegative)
    {
        var node = ((NumericValue)v).Node;
        float n = CalcMath.Finite(Eval(node, Context).Number);
        return nonNegative && n < 0 ? 0 : n;
    }

    public float ComputeAlpha(CssValue v) => (float)CalcMath.Clamp(ComputeNumber(v, nonNegative: false), 0, 1);

    public IntegerOrAuto ComputeIntegerOrAuto(CssValue v)
    {
        var node = ((IntegerOrAutoValue)v).Node;
        if (node is null) return IntegerOrAuto.Auto;
        double d = Math.Round(CalcMath.FiniteDouble(Eval(node, Context).Number), MidpointRounding.AwayFromZero);
        return new IntegerOrAuto((int)CalcMath.Clamp(d, int.MinValue, int.MaxValue));
    }

    public CornerRadius ComputeRadius(CssValue v)
    {
        var r = (RadiusValue)v;
        var h = LengthPercentage(r.Horizontal, nonNegative: true);
        var vert = ReferenceEquals(r.Horizontal, r.Vertical) ? h : LengthPercentage(r.Vertical, nonNegative: true);
        return new CornerRadius(h, vert);
    }

    public ImageList ComputeImageList(CssValue v)
    {
        var list = (ImageListValue)v;
        if (list.Computed is not null) return list.Computed;
        var ctx = Context;
        var layers = new CssImage?[list.Layers.Count];
        for (int i = 0; i < layers.Length; i++)
        {
            var l = list.Layers[i];
            if (l is null) continue;
            if (l.IsContextDependent) Flags |= ComputedStyleFlags.DependsOnViewport;
            layers[i] = l.Compute(ctx);
        }
        return new ImageList(layers);
    }

    public ShadowList ComputeShadowList(CssValue v)
    {
        var list = (ShadowListValue)v;
        if (list.Shadows.Length == 0) return ShadowList.None;
        var ctx = Context;
        var result = new Shadow[list.Shadows.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var s = list.Shadows[i];
            float Px(CalcNode? n) => n is null ? 0 : CalcMath.Finite(Eval(n, ctx).Px);
            result[i] = new Shadow(Px(s.X), Px(s.Y), Math.Max(0, Px(s.Blur)), Px(s.Spread), s.Color, s.Inset);
        }
        return new ShadowList(result);
    }

    public FontFamilyList ComputeFontFamily(CssValue v) => ((FontFamilyValue)v).List;

    /// <summary><c>&lt;position&gt;</c>：相对长度换算为 px，百分比保留。</summary>
    public PositionValue ComputePosition(CssValue v)
    {
        var p = (PositionSpecifiedValue)v;
        var ctx = Context;
        return new PositionValue(Eval(p.X, ctx).ToLengthPercentage(), Eval(p.Y, ctx).ToLengthPercentage());
    }

    /// <summary><c>aspect-ratio</c>：比值的两项算成数（calc 的负结果钳制为 0，即退化比值）；省略的后项为 1。</summary>
    public AspectRatioValue ComputeAspectRatio(CssValue v)
    {
        var a = (AspectRatioSpecifiedValue)v;
        if (a.Width is null) return AspectRatioValue.Auto;
        var ctx = Context;
        float w = Math.Max(0, CalcMath.Finite(Eval(a.Width, ctx).Number));
        float h = a.Height is null ? 1 : Math.Max(0, CalcMath.Finite(Eval(a.Height, ctx).Number));
        return new AspectRatioValue(a.IsAuto, w, h);
    }

    /// <summary><c>&lt;integer&gt;</c>（<c>order</c>）：calc() 的结果四舍五入（远离零）并钳制到 int 范围。</summary>
    public int ComputeInteger(CssValue v)
    {
        double d = Math.Round(CalcMath.FiniteDouble(Eval(((NumericValue)v).Node, Context).Number), MidpointRounding.AwayFromZero);
        return (int)CalcMath.Clamp(d, int.MinValue, int.MaxValue);
    }

    /// <summary>轨道列表：不依赖上下文的值在解析时已算好，其余按本元素的字体大小与视口换算长度。</summary>
    public CssGridTrackList ComputeGridTrackList(CssValue v)
    {
        var t = (GridTrackListValue)v;
        if (t.Computed is not null) return t.Computed;
        if (t.UsesViewport) Flags |= ComputedStyleFlags.DependsOnViewport;
        return t.Compute(Context);
    }

    public CssGridLine ComputeGridLine(CssValue v) => ((GridLineSpecifiedValue)v).Value;

    public CssGridTemplateAreas ComputeGridAreas(CssValue v) => ((GridAreasSpecifiedValue)v).Value;

    /// <summary>font-size：em 与百分比相对父元素字体大小，rem 相对根元素（根元素自身相对初始字体大小）。</summary>
    public float ComputeFontSize(CssValue v)
    {
        var f = (FontSizeValue)v;
        double parent = Parent?.FontSize ?? Environment.DefaultFontSize;
        double size;
        switch (f.Kind)
        {
            case FontSizeValue.SizeKind.Absolute: size = f.Factor * Environment.DefaultFontSize; break;
            case FontSizeValue.SizeKind.Larger:
            case FontSizeValue.SizeKind.Smaller: size = parent * f.Factor; break;
            default:
            {
                var ctx = new LengthContext(parent, _rootFontSizeForFontSize, Environment);
                var lp = Eval(f.Node!, ctx).ToLengthPercentage();
                size = lp.Resolve((float)parent);
                break;
            }
        }
        return Math.Max(0, CalcMath.Finite(size));
    }

    /// <summary>font-weight：bolder/lighter 按 CSS Fonts 4 §2.2 的表相对父元素。</summary>
    public float ComputeFontWeight(CssValue v)
    {
        var w = (FontWeightValue)v;
        if (w.Relative == 0) return (float)CalcMath.Clamp(Eval(w.Node!, Context).Number, 1, 1000);
        double p = Parent?.FontWeight ?? 400;
        if (w.Relative > 0) return p < 350 ? 400 : p < 550 ? 700 : p < 900 ? 900 : (float)p;
        return p < 100 ? (float)p : p < 550 ? 100 : p < 750 ? 400 : 700;
    }

    public float ComputeFontStretch(CssValue v)
    {
        var r = Eval(((NumericValue)v).Node, Context);
        return Math.Max(0, CalcMath.Finite(r.Pct));
    }

    /// <summary>line-height：数保留为数（子元素按各自字体大小相乘）；长度与百分比换算为 px。</summary>
    public LineHeight ComputeLineHeight(CssValue v)
    {
        var l = (LineHeightValue)v;
        switch (l.Kind)
        {
            case LineHeightKind.Number: return Values.LineHeight.FromNumber(Math.Max(0, CalcMath.Finite(Eval(l.Node!, Context).Number)));
            case LineHeightKind.Length:
            {
                var lp = Eval(l.Node!, Context).ToLengthPercentage();
                return Values.LineHeight.FromPx(Math.Max(0, lp.Resolve((float)FontSize)));
            }
            default: return Values.LineHeight.Normal;
        }
    }
}

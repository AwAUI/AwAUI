using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>CSS 全局关键字。</summary>
public enum CssWideKeyword : byte
{
    Initial,
    Inherit,
    Unset,
    Revert,
    RevertLayer,
}

/// <summary>值解析的选项。</summary>
[Flags]
internal enum ValueFlags
{
    None = 0,
    AllowAuto = 1 << 0,
    AllowNone = 1 << 1,
    AllowNormal = 1 << 2,
    /// <summary><c>min-content / max-content / fit-content</c>。</summary>
    AllowIntrinsic = 1 << 3,
    /// <summary><c>content</c>（<c>flex-basis</c>）。</summary>
    AllowContent = 1 << 4,
    NonNegative = 1 << 5,
}

/// <summary>
/// 声明值的指定值（每条规则解析一次并缓存，design/08）。计算值阶段由级联换算为 <see cref="ComputedStyle"/> 中的强类型字段。
/// </summary>
public abstract class CssValue
{
    private protected CssValue() { }
}

/// <summary>一个长属性及其指定值（简写展开的结果）。</summary>
public readonly struct PropertyValue
{
    public PropertyValue(CssPropertyId id, CssValue value)
    {
        Id = id;
        Value = value;
    }

    public CssPropertyId Id { get; }

    public CssValue Value { get; }
}

/// <summary><c>initial / inherit / unset / revert / revert-layer</c>。</summary>
public sealed class CssWideKeywordValue : CssValue
{
    public static readonly CssWideKeywordValue Initial = new(CssWideKeyword.Initial);
    public static readonly CssWideKeywordValue Inherit = new(CssWideKeyword.Inherit);
    public static readonly CssWideKeywordValue Unset = new(CssWideKeyword.Unset);
    public static readonly CssWideKeywordValue Revert = new(CssWideKeyword.Revert);
    public static readonly CssWideKeywordValue RevertLayer = new(CssWideKeyword.RevertLayer);

    CssWideKeywordValue(CssWideKeyword keyword) => Keyword = keyword;

    public CssWideKeyword Keyword { get; }

    public static CssWideKeywordValue Of(CssWideKeyword k) => k switch
    {
        CssWideKeyword.Initial => Initial,
        CssWideKeyword.Inherit => Inherit,
        CssWideKeyword.Unset => Unset,
        CssWideKeyword.Revert => Revert,
        _ => RevertLayer,
    };

    /// <summary>识别全局关键字（ASCII 大小写不敏感）。</summary>
    public static bool TryParse(string ident, out CssWideKeyword keyword)
    {
        switch (CssSerializer.ToAsciiLower(ident))
        {
            case "initial": keyword = CssWideKeyword.Initial; return true;
            case "inherit": keyword = CssWideKeyword.Inherit; return true;
            case "unset": keyword = CssWideKeyword.Unset; return true;
            case "revert": keyword = CssWideKeyword.Revert; return true;
            case "revert-layer": keyword = CssWideKeyword.RevertLayer; return true;
            default: keyword = default; return false;
        }
    }

    // 不用 Enum.ToString()：它依赖反射元数据，且每次分配（运行时库必须对 NativeAOT/裁剪友好）
    public override string ToString() => Keyword switch
    {
        CssWideKeyword.Initial => "initial",
        CssWideKeyword.Inherit => "inherit",
        CssWideKeyword.Unset => "unset",
        CssWideKeyword.Revert => "revert",
        _ => "revert-layer",
    };
}

/// <summary>
/// 含 <c>var()</c> 或 <c>env()</c> 的值：解析期只校验括号结构，替换与按属性语法解析推迟到计算值阶段
/// （失败即「计算值时无效」，按 unset 处理）。
/// </summary>
public sealed class UnparsedValue : CssValue
{
    internal UnparsedValue(IReadOnlyList<CssComponentValue> tokens) => Tokens = tokens;

    public IReadOnlyList<CssComponentValue> Tokens { get; }

    public override string ToString() => CssComponentValue.ToCss(Tokens);
}

/// <summary>含 <c>var()</c> 的简写展开到某个长属性的占位值：替换后整体按简写解析，再取该长属性。</summary>
public sealed class PendingSubstitutionValue : CssValue
{
    internal PendingSubstitutionValue(CssPropertyId shorthand, UnparsedValue value)
    {
        Shorthand = shorthand;
        Value = value;
    }

    public CssPropertyId Shorthand { get; }

    /// <summary>简写的原始值（同一简写的各长属性共享同一实例）。</summary>
    public UnparsedValue Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>自定义属性（<c>--*</c>）的指定值：原样保留的组件值。</summary>
public sealed class CustomPropertyValue : CssValue
{
    internal CustomPropertyValue(IReadOnlyList<CssComponentValue> tokens, bool hasReferences)
    {
        Tokens = tokens;
        HasReferences = hasReferences;
    }

    public IReadOnlyList<CssComponentValue> Tokens { get; }

    /// <summary>是否含 <c>var()</c> / <c>env()</c>。</summary>
    public bool HasReferences { get; }

    int _weight = -1;

    /// <summary>组件值总数（递归计数，封顶于替换上限 + 1），用于限制替换膨胀。</summary>
    internal int Weight => _weight >= 0 ? _weight : _weight = Count(Tokens, 0);

    static int Count(IReadOnlyList<CssComponentValue> list, int acc)
    {
        foreach (var c in list)
        {
            if (++acc > Cascade.VariableResolver.MaxTokens) return acc;
            if (c is CssFunction f) acc = Count(f.Arguments, acc);
            else if (c is CssSimpleBlock b) acc = Count(b.Values, acc);
        }
        return acc;
    }

    public override string ToString() => CssComponentValue.ToCss(Tokens);
}

// ================= 按类型的指定值（内部） =================

/// <summary>关键字枚举值。</summary>
internal sealed class KeywordValue : CssValue
{
    static readonly KeywordValue[] s_cache = CreateCache();

    KeywordValue(int value) => Value = value;

    public int Value { get; }

    static KeywordValue[] CreateCache()
    {
        var a = new KeywordValue[64];
        for (int i = 0; i < a.Length; i++) a[i] = new KeywordValue(i);
        return a;
    }

    public static KeywordValue Of(int value) => (uint)value < (uint)s_cache.Length ? s_cache[value] : new KeywordValue(value);
}

/// <summary>数值类：长度、长度百分比、数（均可为 calc 表达式）。</summary>
internal sealed class NumericValue : CssValue
{
    public NumericValue(CalcNode node) => Node = node;

    public CalcNode Node { get; }

    public override string ToString() => Node.ToString();
}

/// <summary>尺寸类：关键字或长度百分比。</summary>
internal sealed class SizeSpecifiedValue : CssValue
{
    static readonly SizeSpecifiedValue[] s_keywords =
    {
        new(SizeKind.LengthPercentage, null), new(SizeKind.Auto, null), new(SizeKind.None, null), new(SizeKind.Normal, null),
        new(SizeKind.MinContent, null), new(SizeKind.MaxContent, null), new(SizeKind.FitContent, null), new(SizeKind.Content, null),
    };

    SizeSpecifiedValue(SizeKind kind, CalcNode? node, bool nonNegative = false)
    {
        Kind = kind;
        Node = node;
        NonNegative = nonNegative;
    }

    public static SizeSpecifiedValue Keyword(SizeKind kind) => s_keywords[(int)kind];

    public static SizeSpecifiedValue Length(CalcNode node, bool nonNegative = false) => new(SizeKind.LengthPercentage, node, nonNegative);

    public SizeKind Kind { get; }

    public CalcNode? Node { get; }

    /// <summary>属性只允许非负值（calc 结果为负时钳制为 0）。</summary>
    public bool NonNegative { get; }
}

internal sealed class ColorValue : CssValue
{
    public ColorValue(CssColor color) => Color = color;

    public CssColor Color { get; }

    public override string ToString() => Color.ToString();
}

internal sealed class IntegerOrAutoValue : CssValue
{
    public static readonly IntegerOrAutoValue Auto = new(null);

    public IntegerOrAutoValue(CalcNode? node) => Node = node;

    /// <summary>null 表示 auto。</summary>
    public CalcNode? Node { get; }
}

internal sealed class RadiusValue : CssValue
{
    public RadiusValue(CalcNode horizontal, CalcNode vertical)
    {
        Horizontal = horizontal;
        Vertical = vertical;
    }

    public CalcNode Horizontal { get; }

    public CalcNode Vertical { get; }
}

internal sealed class FontFamilyValue : CssValue
{
    public FontFamilyValue(FontFamilyList list) => List = list;

    public FontFamilyList List { get; }
}

/// <summary><c>font-size</c>：绝对关键字（倍数）、<c>larger/smaller</c> 或长度百分比。</summary>
internal sealed class FontSizeValue : CssValue
{
    public enum SizeKind : byte { Absolute, Larger, Smaller, Length }

    public FontSizeValue(SizeKind kind, double factor, CalcNode? node)
    {
        Kind = kind;
        Factor = factor;
        Node = node;
    }

    public SizeKind Kind { get; }

    /// <summary>绝对关键字相对 medium 的倍数。</summary>
    public double Factor { get; }

    public CalcNode? Node { get; }
}

/// <summary><c>font-weight</c>：数值或 <c>bolder/lighter</c>。</summary>
internal sealed class FontWeightValue : CssValue
{
    public static readonly FontWeightValue Bolder = new(null, +1);
    public static readonly FontWeightValue Lighter = new(null, -1);

    public FontWeightValue(CalcNode? node, int relative)
    {
        Node = node;
        Relative = relative;
    }

    public CalcNode? Node { get; }

    /// <summary>+1 = bolder，-1 = lighter，0 = 绝对值。</summary>
    public int Relative { get; }
}

internal sealed class LineHeightValue : CssValue
{
    public static readonly LineHeightValue Normal = new(LineHeightKind.Normal, null);

    public LineHeightValue(LineHeightKind kind, CalcNode? node)
    {
        Kind = kind;
        Node = node;
    }

    public LineHeightKind Kind { get; }

    /// <summary>Number 种类为数表达式；Length 种类为长度百分比表达式。</summary>
    public CalcNode? Node { get; }
}

/// <summary>图层列表的指定值。</summary>
internal sealed class ImageListValue : CssValue
{
    public ImageListValue(IReadOnlyList<SpecifiedImage?> layers, ImageList? computed)
    {
        Layers = layers;
        Computed = computed;
    }

    public IReadOnlyList<SpecifiedImage?> Layers { get; }

    /// <summary>不依赖上下文时预先计算好的值（大多数情况）。</summary>
    public ImageList? Computed { get; }
}

/// <summary>图像的指定值。</summary>
internal abstract class SpecifiedImage
{
    /// <summary>是否含有需要上下文（em、视口单位）的长度。</summary>
    public abstract bool IsContextDependent { get; }

    public abstract CssImage Compute(in LengthContext context);
}

internal sealed class SpecifiedUrlImage : SpecifiedImage
{
    readonly UrlImage _image;

    public SpecifiedUrlImage(string url) => _image = new UrlImage(url);

    public override bool IsContextDependent => false;

    public override CssImage Compute(in LengthContext context) => _image;
}

internal readonly struct SpecifiedStop
{
    public SpecifiedStop(CssColor color, CalcNode? position, bool hint)
    {
        Color = color;
        Position = position;
        IsHint = hint;
    }

    public CssColor Color { get; }
    public CalcNode? Position { get; }
    public bool IsHint { get; }
}

internal abstract class SpecifiedGradient : SpecifiedImage
{
    protected SpecifiedGradient(bool repeating, SpecifiedStop[] stops)
    {
        Repeating = repeating;
        Stops = stops;
        foreach (var s in stops)
            if (s.Position is { IsContextDependent: true }) _contextDependent = true;
    }

    readonly bool _contextDependent;

    public bool Repeating { get; }

    public SpecifiedStop[] Stops { get; }

    public override bool IsContextDependent => _contextDependent || ExtraContextDependent;

    protected virtual bool ExtraContextDependent => false;

    /// <summary>书写的颜色插值方法（<c>in oklab</c>）。</summary>
    public CssColorSpace? Interpolation { get; set; }

    public CssHueInterpolation Hue { get; set; }

    protected T WithInterpolation<T>(T g) where T : Gradient
    {
        g.Interpolation = Interpolation;
        g.HueInterpolation = Hue;
        return g;
    }

    protected ColorStop[] ComputeStops(in LengthContext context)
    {
        var result = new ColorStop[Stops.Length];
        for (int i = 0; i < Stops.Length; i++)
        {
            var s = Stops[i];
            LengthPercentage? pos = s.Position is null ? null : s.Position.Compute(context).ToLengthPercentage();
            result[i] = s.IsHint ? ColorStop.Hint(pos!.Value) : new ColorStop(s.Color, pos);
        }
        return result;
    }
}

internal sealed class SpecifiedLinearGradient : SpecifiedGradient
{
    public SpecifiedLinearGradient(double angle, GradientCorner corner, bool repeating, SpecifiedStop[] stops) : base(repeating, stops)
    {
        Angle = angle;
        Corner = corner;
    }

    public double Angle { get; }

    public GradientCorner Corner { get; }

    public override CssImage Compute(in LengthContext context)
        => WithInterpolation(new LinearGradient(Angle, Corner, Repeating, ComputeStops(context)));
}

internal sealed class SpecifiedRadialGradient : SpecifiedGradient
{
    public SpecifiedRadialGradient(RadialShape shape, RadialSizeKeyword size, CalcNode? rx, CalcNode? ry, CalcNode cx, CalcNode cy,
        bool repeating, SpecifiedStop[] stops) : base(repeating, stops)
    {
        Shape = shape;
        Size = size;
        RadiusX = rx;
        RadiusY = ry;
        CenterX = cx;
        CenterY = cy;
    }

    public RadialShape Shape { get; }
    public RadialSizeKeyword Size { get; }
    public CalcNode? RadiusX { get; }
    public CalcNode? RadiusY { get; }
    public CalcNode CenterX { get; }
    public CalcNode CenterY { get; }

    protected override bool ExtraContextDependent
        => CenterX.IsContextDependent || CenterY.IsContextDependent || RadiusX is { IsContextDependent: true } || RadiusY is { IsContextDependent: true };

    public override CssImage Compute(in LengthContext context)
    {
        var rx = RadiusX?.Compute(context).ToLengthPercentage() ?? default;
        var ry = RadiusY?.Compute(context).ToLengthPercentage() ?? rx;
        return WithInterpolation(new RadialGradient(Shape, Size, rx, ry, CenterX.Compute(context).ToLengthPercentage(),
            CenterY.Compute(context).ToLengthPercentage(), Repeating, ComputeStops(context)));
    }
}

internal sealed class SpecifiedConicGradient : SpecifiedGradient
{
    public SpecifiedConicGradient(double from, CalcNode cx, CalcNode cy, bool repeating, SpecifiedStop[] stops) : base(repeating, stops)
    {
        From = from;
        CenterX = cx;
        CenterY = cy;
    }

    public double From { get; }
    public CalcNode CenterX { get; }
    public CalcNode CenterY { get; }

    protected override bool ExtraContextDependent => CenterX.IsContextDependent || CenterY.IsContextDependent;

    public override CssImage Compute(in LengthContext context)
        => WithInterpolation(new ConicGradient(From, CenterX.Compute(context).ToLengthPercentage(), CenterY.Compute(context).ToLengthPercentage(), Repeating, ComputeStops(context)));
}

internal readonly struct SpecifiedShadow
{
    public SpecifiedShadow(CalcNode x, CalcNode y, CalcNode? blur, CalcNode? spread, CssColor color, bool inset)
    {
        X = x;
        Y = y;
        Blur = blur;
        Spread = spread;
        Color = color;
        Inset = inset;
    }

    public CalcNode X { get; }
    public CalcNode Y { get; }
    public CalcNode? Blur { get; }
    public CalcNode? Spread { get; }
    public CssColor Color { get; }
    public bool Inset { get; }
}

internal sealed class ShadowListValue : CssValue
{
    public static readonly ShadowListValue None = new(Array.Empty<SpecifiedShadow>());

    public ShadowListValue(SpecifiedShadow[] shadows) => Shadows = shadows;

    public SpecifiedShadow[] Shadows { get; }
}

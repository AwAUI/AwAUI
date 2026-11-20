using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>图像计算值：<c>url()</c> 或渐变（<c>background-image</c> 的一层）。</summary>
public abstract class CssImage : IEquatable<CssImage>
{
    private protected CssImage() { }

    public abstract bool Equals(CssImage? other);
    public override bool Equals(object? obj) => obj is CssImage i && Equals(i);
    public abstract override int GetHashCode();
}

/// <summary><c>url(...)</c> 图像。加载由上层负责，这里只保留 URL 文本作为计算值。</summary>
public sealed class UrlImage : CssImage
{
    public UrlImage(string url) => Url = url ?? throw new ArgumentNullException(nameof(url));

    public string Url { get; }

    public override bool Equals(CssImage? other) => other is UrlImage u && u.Url == Url;
    public override int GetHashCode() => Url.GetHashCode();
    public override string ToString() => "url(" + CssSerializer.SerializeString(Url) + ")";
}

/// <summary>渐变色标：颜色 + 可选位置；<see cref="IsHint"/> 为真表示颜色插值提示（只有位置）。</summary>
public readonly struct ColorStop : IEquatable<ColorStop>
{
    public ColorStop(CssColor color, LengthPercentage? position)
    {
        Color = color;
        Position = position;
        IsHint = false;
    }

    ColorStop(LengthPercentage hint)
    {
        Color = default;
        Position = hint;
        IsHint = true;
    }

    public static ColorStop Hint(LengthPercentage position) => new(position);

    public CssColor Color { get; }

    /// <summary>位置；未指定时为 null（按规范在相邻色标间均分）。百分比相对渐变线长度。</summary>
    public LengthPercentage? Position { get; }

    public bool IsHint { get; }

    public bool Equals(ColorStop other) => IsHint == other.IsHint && Color.Equals(other.Color) && Nullable.Equals(Position, other.Position);
    public override bool Equals(object? obj) => obj is ColorStop s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Color.GetHashCode(), Position.GetHashCode(), IsHint ? 1 : 0);

    public override string ToString()
    {
        if (IsHint) return Position!.Value.ToString();
        return Position is { } p ? Color + " " + p : Color.ToString();
    }
}

/// <summary><c>to &lt;corner&gt;</c> 方向（角度取决于盒子宽高比，绘制时计算）。</summary>
public enum GradientCorner : byte
{
    None,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

/// <summary>渐变公共部分。</summary>
public abstract class Gradient : CssImage
{
    private protected Gradient(bool repeating, IReadOnlyList<ColorStop> stops)
    {
        Repeating = repeating;
        Stops = stops;
    }

    /// <summary><c>repeating-*-gradient()</c>。</summary>
    public bool Repeating { get; }

    /// <summary>色标与插值提示，按书写顺序（双位置色标已展开为两个色标）。</summary>
    public IReadOnlyList<ColorStop> Stops { get; }

    /// <summary>
    /// 书写的颜色插值空间（<c>linear-gradient(in oklab, …)</c>）；省略为 null —— 按 CSS Images 4，此时全部色标为旧式颜色语法
    /// 则在 sRGB 中插值，否则在 OkLab 中插值（由绘制一侧决定）。
    /// </summary>
    public CssColorSpace? Interpolation { get; internal set; }

    /// <summary>极坐标插值空间的色相插值方法。</summary>
    public CssHueInterpolation HueInterpolation { get; internal set; }

    private protected bool StopsEqual(Gradient other)
    {
        if (Repeating != other.Repeating || Stops.Count != other.Stops.Count || Interpolation != other.Interpolation || HueInterpolation != other.HueInterpolation) return false;
        for (int i = 0; i < Stops.Count; i++)
            if (!Stops[i].Equals(other.Stops[i])) return false;
        return true;
    }

    private protected int StopsHash()
    {
        int h = Repeating ? 1 : 0;
        for (int i = 0; i < Stops.Count; i++) h = HashCodeCompat.Combine(h, Stops[i].GetHashCode());
        return h;
    }

    private protected void WriteStops(StringBuilder sb)
    {
        for (int i = 0; i < Stops.Count; i++)
        {
            sb.Append(", ");
            sb.Append(Stops[i].ToString());
        }
    }
}

/// <summary><c>linear-gradient()</c> / <c>repeating-linear-gradient()</c>。</summary>
public sealed class LinearGradient : Gradient
{
    public LinearGradient(double angleDegrees, GradientCorner corner, bool repeating, IReadOnlyList<ColorStop> stops) : base(repeating, stops)
    {
        AngleDegrees = angleDegrees;
        Corner = corner;
    }

    /// <summary>渐变线角度（度，0 = 向上，90 = 向右）。<see cref="Corner"/> 非 None 时无意义。</summary>
    public double AngleDegrees { get; }

    /// <summary><c>to top left</c> 等对角方向。</summary>
    public GradientCorner Corner { get; }

    public override bool Equals(CssImage? other)
        => other is LinearGradient g && g.AngleDegrees.Equals(AngleDegrees) && g.Corner == Corner && StopsEqual(g);

    public override int GetHashCode() => HashCodeCompat.Combine(AngleDegrees.GetHashCode(), (int)Corner, StopsHash());

    public override string ToString()
    {
        var sb = new StringBuilder(Repeating ? "repeating-linear-gradient(" : "linear-gradient(");
        sb.Append(Corner switch
        {
            GradientCorner.TopLeft => "to top left",
            GradientCorner.TopRight => "to top right",
            GradientCorner.BottomRight => "to bottom right",
            GradientCorner.BottomLeft => "to bottom left",
            _ => CssSerializer.FormatNumber(AngleDegrees) + "deg",
        });
        WriteStops(sb);
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>径向渐变的形状。</summary>
public enum RadialShape : byte
{
    Ellipse,
    Circle,
}

/// <summary>径向渐变的尺寸关键字；<see cref="Explicit"/> 表示显式半径。</summary>
public enum RadialSizeKeyword : byte
{
    FarthestCorner,
    ClosestSide,
    ClosestCorner,
    FarthestSide,
    Explicit,
}

/// <summary><c>radial-gradient()</c> / <c>repeating-radial-gradient()</c>。</summary>
public sealed class RadialGradient : Gradient
{
    public RadialGradient(RadialShape shape, RadialSizeKeyword size, LengthPercentage radiusX, LengthPercentage radiusY,
        LengthPercentage centerX, LengthPercentage centerY, bool repeating, IReadOnlyList<ColorStop> stops) : base(repeating, stops)
    {
        Shape = shape;
        Size = size;
        RadiusX = radiusX;
        RadiusY = radiusY;
        CenterX = centerX;
        CenterY = centerY;
    }

    public RadialShape Shape { get; }

    public RadialSizeKeyword Size { get; }

    /// <summary>显式水平半径（圆形时两半径相同）。</summary>
    public LengthPercentage RadiusX { get; }

    public LengthPercentage RadiusY { get; }

    /// <summary>圆心，百分比相对背景定位区域。</summary>
    public LengthPercentage CenterX { get; }

    public LengthPercentage CenterY { get; }

    public override bool Equals(CssImage? other)
        => other is RadialGradient g && g.Shape == Shape && g.Size == Size && g.RadiusX.Equals(RadiusX) && g.RadiusY.Equals(RadiusY)
           && g.CenterX.Equals(CenterX) && g.CenterY.Equals(CenterY) && StopsEqual(g);

    public override int GetHashCode()
        => HashCodeCompat.Combine(HashCodeCompat.Combine((int)Shape, (int)Size, RadiusX.GetHashCode(), RadiusY.GetHashCode()),
            CenterX.GetHashCode(), CenterY.GetHashCode(), StopsHash());

    public override string ToString()
    {
        var sb = new StringBuilder(Repeating ? "repeating-radial-gradient(" : "radial-gradient(");
        sb.Append(Shape == RadialShape.Circle ? "circle" : "ellipse");
        switch (Size)
        {
            case RadialSizeKeyword.Explicit:
                sb.Append(' ').Append(RadiusX.ToString());
                if (Shape == RadialShape.Ellipse) sb.Append(' ').Append(RadiusY.ToString());
                break;
            case RadialSizeKeyword.ClosestSide: sb.Append(" closest-side"); break;
            case RadialSizeKeyword.ClosestCorner: sb.Append(" closest-corner"); break;
            case RadialSizeKeyword.FarthestSide: sb.Append(" farthest-side"); break;
        }
        sb.Append(" at ").Append(CenterX.ToString()).Append(' ').Append(CenterY.ToString());
        WriteStops(sb);
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// <c>conic-gradient()</c> / <c>repeating-conic-gradient()</c>。色标位置是相对整圈的百分比（角度已换算：90deg = 25%）。
/// </summary>
public sealed class ConicGradient : Gradient
{
    public ConicGradient(double fromDegrees, LengthPercentage centerX, LengthPercentage centerY, bool repeating, IReadOnlyList<ColorStop> stops) : base(repeating, stops)
    {
        FromDegrees = fromDegrees;
        CenterX = centerX;
        CenterY = centerY;
    }

    /// <summary>起始角（度，0 = 向上，顺时针）。</summary>
    public double FromDegrees { get; }

    public LengthPercentage CenterX { get; }

    public LengthPercentage CenterY { get; }

    public override bool Equals(CssImage? other)
        => other is ConicGradient g && g.FromDegrees.Equals(FromDegrees) && g.CenterX.Equals(CenterX) && g.CenterY.Equals(CenterY) && StopsEqual(g);

    public override int GetHashCode() => HashCodeCompat.Combine(FromDegrees.GetHashCode(), CenterX.GetHashCode(), CenterY.GetHashCode(), StopsHash());

    public override string ToString()
    {
        var sb = new StringBuilder(Repeating ? "repeating-conic-gradient(" : "conic-gradient(");
        sb.Append("from ").Append(CssSerializer.FormatNumber(FromDegrees)).Append("deg at ").Append(CenterX.ToString()).Append(' ').Append(CenterY.ToString());
        WriteStops(sb);
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>图层列表（<c>background-image</c> 的计算值）。<c>none</c> 层以 null 表示。</summary>
public sealed class ImageList : IEquatable<ImageList>
{
    /// <summary>单层 <c>none</c>（初始值）。</summary>
    public static readonly ImageList None = new(new CssImage?[] { null });

    public ImageList(IReadOnlyList<CssImage?> layers)
    {
        if (layers is null || layers.Count == 0) throw new ArgumentException("至少需要一层。", nameof(layers));
        Layers = layers;
    }

    /// <summary>图层，第一个在最上面。</summary>
    public IReadOnlyList<CssImage?> Layers { get; }

    /// <summary>是否所有层都是 none。</summary>
    public bool IsNone
    {
        get
        {
            foreach (var l in Layers)
                if (l is not null) return false;
            return true;
        }
    }

    public bool Equals(ImageList? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Layers.Count != Layers.Count) return false;
        for (int i = 0; i < Layers.Count; i++)
        {
            var a = Layers[i];
            var b = other.Layers[i];
            if (a is null ? b is not null : !a.Equals(b)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ImageList l && Equals(l);

    public override int GetHashCode()
    {
        int h = Layers.Count;
        foreach (var l in Layers) h = HashCodeCompat.Combine(h, l?.GetHashCode() ?? 0);
        return h;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Layers.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Layers[i]?.ToString() ?? "none");
        }
        return sb.ToString();
    }
}

/// <summary>一个阴影（<c>box-shadow</c> 的一项）。长度为 px。</summary>
public readonly struct Shadow : IEquatable<Shadow>
{
    public Shadow(float offsetX, float offsetY, float blur, float spread, CssColor color, bool inset)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        Blur = blur;
        Spread = spread;
        Color = color;
        Inset = inset;
    }

    public float OffsetX { get; }
    public float OffsetY { get; }
    public float Blur { get; }
    public float Spread { get; }

    /// <summary>颜色；未指定时为 <c>currentColor</c>。</summary>
    public CssColor Color { get; }

    public bool Inset { get; }

    public bool Equals(Shadow o)
        => OffsetX.Equals(o.OffsetX) && OffsetY.Equals(o.OffsetY) && Blur.Equals(o.Blur) && Spread.Equals(o.Spread)
           && Color.Equals(o.Color) && Inset == o.Inset;

    public override bool Equals(object? obj) => obj is Shadow s && Equals(s);

    public override int GetHashCode()
        => HashCodeCompat.Combine(HashCodeCompat.Combine(OffsetX.GetHashCode(), OffsetY.GetHashCode(), Blur.GetHashCode(), Spread.GetHashCode()),
            Color.GetHashCode(), Inset ? 1 : 0);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Color.ToString()).Append(' ')
          .Append(ValueText.Px(OffsetX)).Append(' ').Append(ValueText.Px(OffsetY)).Append(' ')
          .Append(ValueText.Px(Blur)).Append(' ').Append(ValueText.Px(Spread));
        if (Inset) sb.Append(" inset");
        return sb.ToString();
    }
}

/// <summary>阴影列表（<c>box-shadow</c> 的计算值）；<see cref="None"/> 表示 none。</summary>
public sealed class ShadowList : IEquatable<ShadowList>
{
    public static readonly ShadowList None = new(Array.Empty<Shadow>());

    public ShadowList(IReadOnlyList<Shadow> shadows) => Shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));

    public IReadOnlyList<Shadow> Shadows { get; }

    public bool IsNone => Shadows.Count == 0;

    public bool Equals(ShadowList? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Shadows.Count != Shadows.Count) return false;
        for (int i = 0; i < Shadows.Count; i++)
            if (!Shadows[i].Equals(other.Shadows[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ShadowList l && Equals(l);

    public override int GetHashCode()
    {
        int h = Shadows.Count;
        foreach (var s in Shadows) h = HashCodeCompat.Combine(h, s.GetHashCode());
        return h;
    }

    public override string ToString()
    {
        if (IsNone) return "none";
        var sb = new StringBuilder();
        for (int i = 0; i < Shadows.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Shadows[i].ToString());
        }
        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary><c>clip-path</c> 的种类。</summary>
public enum CssClipPathKind : byte
{
    None,
    /// <summary>基本形状（可带参考盒）。</summary>
    Shape,
    /// <summary>只有参考盒（按该盒的圆角矩形裁剪）。</summary>
    Box,
    /// <summary><c>url()</c> 引用（只解析，不渲染）。</summary>
    Url,
}

/// <summary>&lt;geometry-box&gt;（CSS Masking 1）。</summary>
public enum CssGeometryBox : byte
{
    BorderBox,
    PaddingBox,
    ContentBox,
    MarginBox,
    FillBox,
    StrokeBox,
    ViewBox,
}

/// <summary>基本形状的种类。<c>xywh()</c> 与 <c>rect()</c> 的计算值是等价的 <c>inset()</c>（CSS Shapes 1）。</summary>
public enum CssBasicShapeKind : byte
{
    Inset,
    Circle,
    Ellipse,
    Polygon,
    Path,
}

/// <summary>&lt;fill-rule&gt;。</summary>
public enum CssFillRule : byte
{
    NonZero,
    EvenOdd,
}

/// <summary>圆与椭圆的半径种类。</summary>
public enum CssShapeRadiusKind : byte
{
    Length,
    ClosestSide,
    FarthestSide,
}

/// <summary>
/// <c>clip-path</c> 的计算值。长度已换算为 px，百分比保留：<c>inset()</c> 的四边与圆角相对参考盒，圆的半径百分比相对
/// 参考盒宽高的 √((w²+h²)/2)，椭圆半径分别相对宽、高，多边形顶点相对宽、高。
/// </summary>
public sealed class CssClipPath : IEquatable<CssClipPath>
{
    public static readonly CssClipPath None = new() { Kind = CssClipPathKind.None };

    CssClipPath() { }

    public CssClipPathKind Kind { get; private set; }

    public CssBasicShapeKind ShapeKind { get; private set; }

    /// <summary>参考盒（未指定时为 border-box）。</summary>
    public CssGeometryBox GeometryBox { get; private set; }

    /// <summary><c>inset()</c> 的上、右、下、左（相对参考盒的高、宽、高、宽）。</summary>
    public LengthPercentage InsetTop { get; private set; }
    public LengthPercentage InsetRight { get; private set; }
    public LengthPercentage InsetBottom { get; private set; }
    public LengthPercentage InsetLeft { get; private set; }

    /// <summary><c>inset()</c> 的圆角：左上、右上、右下、左下。</summary>
    public IReadOnlyList<CornerRadius> InsetRadii { get; private set; } = Array.Empty<CornerRadius>();

    public CssShapeRadiusKind RadiusXKind { get; private set; }
    public LengthPercentage RadiusX { get; private set; }
    public CssShapeRadiusKind RadiusYKind { get; private set; }
    public LengthPercentage RadiusY { get; private set; }

    /// <summary>圆与椭圆的圆心（默认 50% 50%）。</summary>
    public PositionValue Center { get; private set; }

    public CssFillRule FillRule { get; private set; }

    /// <summary>多边形顶点。</summary>
    public IReadOnlyList<(LengthPercentage X, LengthPercentage Y)> Points { get; private set; } = Array.Empty<(LengthPercentage, LengthPercentage)>();

    /// <summary><c>path()</c> 的 SVG 路径数据。</summary>
    public string? PathData { get; private set; }

    /// <summary><c>url()</c> 的地址。</summary>
    public string? Url { get; private set; }

    internal static CssClipPath FromBox(CssGeometryBox box) => new() { Kind = CssClipPathKind.Box, GeometryBox = box };

    internal static CssClipPath FromUrl(string url) => new() { Kind = CssClipPathKind.Url, Url = url };

    internal static CssClipPath Inset(LengthPercentage t, LengthPercentage r, LengthPercentage b, LengthPercentage l, CornerRadius[] radii, CssGeometryBox box)
        => new() { Kind = CssClipPathKind.Shape, ShapeKind = CssBasicShapeKind.Inset, InsetTop = t, InsetRight = r, InsetBottom = b, InsetLeft = l, InsetRadii = radii, GeometryBox = box };

    internal static CssClipPath Ellipse(bool circle, CssShapeRadiusKind kx, LengthPercentage rx, CssShapeRadiusKind ky, LengthPercentage ry, PositionValue center, CssGeometryBox box)
        => new()
        {
            Kind = CssClipPathKind.Shape, ShapeKind = circle ? CssBasicShapeKind.Circle : CssBasicShapeKind.Ellipse, RadiusXKind = kx, RadiusX = rx,
            RadiusYKind = ky, RadiusY = ry, Center = center, GeometryBox = box,
        };

    internal static CssClipPath Polygon(CssFillRule rule, (LengthPercentage, LengthPercentage)[] points, CssGeometryBox box)
        => new() { Kind = CssClipPathKind.Shape, ShapeKind = CssBasicShapeKind.Polygon, FillRule = rule, Points = points, GeometryBox = box };

    internal static CssClipPath Path(CssFillRule rule, string data, CssGeometryBox box)
        => new() { Kind = CssClipPathKind.Shape, ShapeKind = CssBasicShapeKind.Path, FillRule = rule, PathData = data, GeometryBox = box };

    public bool Equals(CssClipPath? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Kind != o.Kind || ShapeKind != o.ShapeKind || GeometryBox != o.GeometryBox || Url != o.Url || PathData != o.PathData || FillRule != o.FillRule)
            return false;
        if (!InsetTop.Equals(o.InsetTop) || !InsetRight.Equals(o.InsetRight) || !InsetBottom.Equals(o.InsetBottom) || !InsetLeft.Equals(o.InsetLeft)) return false;
        if (RadiusXKind != o.RadiusXKind || RadiusYKind != o.RadiusYKind || !RadiusX.Equals(o.RadiusX) || !RadiusY.Equals(o.RadiusY) || !Center.Equals(o.Center)) return false;
        if (InsetRadii.Count != o.InsetRadii.Count || Points.Count != o.Points.Count) return false;
        for (int i = 0; i < InsetRadii.Count; i++) if (!InsetRadii[i].Equals(o.InsetRadii[i])) return false;
        for (int i = 0; i < Points.Count; i++) if (!Points[i].X.Equals(o.Points[i].X) || !Points[i].Y.Equals(o.Points[i].Y)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssClipPath c && Equals(c);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, (int)ShapeKind, InsetTop.GetHashCode(), Points.Count);

    public override string ToString()
    {
        switch (Kind)
        {
            case CssClipPathKind.None: return "none";
            case CssClipPathKind.Url: return "url(" + CssSerializer.SerializeString(Url ?? string.Empty) + ")";
            case CssClipPathKind.Box: return BoxName(GeometryBox);
        }
        var sb = new StringBuilder();
        switch (ShapeKind)
        {
            case CssBasicShapeKind.Inset:
                sb.Append("inset(").Append(InsetTop).Append(' ').Append(InsetRight).Append(' ').Append(InsetBottom).Append(' ').Append(InsetLeft);
                bool anyRadius = false;
                foreach (var r in InsetRadii) if (!r.Equals(default(CornerRadius))) anyRadius = true;
                if (anyRadius)
                {
                    sb.Append(" round");
                    foreach (var r in InsetRadii) sb.Append(' ').Append(r.Horizontal);
                    sb.Append(" /");
                    foreach (var r in InsetRadii) sb.Append(' ').Append(r.Vertical);
                }
                sb.Append(')');
                break;
            case CssBasicShapeKind.Circle:
                sb.Append("circle(").Append(Radius(RadiusXKind, RadiusX)).Append(" at ").Append(Center).Append(')');
                break;
            case CssBasicShapeKind.Ellipse:
                sb.Append("ellipse(").Append(Radius(RadiusXKind, RadiusX)).Append(' ').Append(Radius(RadiusYKind, RadiusY)).Append(" at ").Append(Center).Append(')');
                break;
            case CssBasicShapeKind.Polygon:
                sb.Append("polygon(");
                if (FillRule == CssFillRule.EvenOdd) sb.Append("evenodd, ");
                for (int i = 0; i < Points.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Points[i].X).Append(' ').Append(Points[i].Y);
                }
                sb.Append(')');
                break;
            default:
                sb.Append("path(");
                if (FillRule == CssFillRule.EvenOdd) sb.Append("evenodd, ");
                sb.Append(CssSerializer.SerializeString(PathData ?? string.Empty)).Append(')');
                break;
        }
        if (GeometryBox != CssGeometryBox.BorderBox) sb.Append(' ').Append(BoxName(GeometryBox));
        return sb.ToString();

        static string Radius(CssShapeRadiusKind k, LengthPercentage v) => k switch
        {
            CssShapeRadiusKind.ClosestSide => "closest-side",
            CssShapeRadiusKind.FarthestSide => "farthest-side",
            _ => v.ToString(),
        };
    }

    internal static string BoxName(CssGeometryBox b) => b switch
    {
        CssGeometryBox.PaddingBox => "padding-box",
        CssGeometryBox.ContentBox => "content-box",
        CssGeometryBox.MarginBox => "margin-box",
        CssGeometryBox.FillBox => "fill-box",
        CssGeometryBox.StrokeBox => "stroke-box",
        CssGeometryBox.ViewBox => "view-box",
        _ => "border-box",
    };
}

/// <summary><c>clip-path</c> 的解析与计算。</summary>
internal static class ClipPathParsers
{
    internal sealed class ClipPathValue : CssValue
    {
        public CssClipPathKind Kind;
        public CssBasicShapeKind Shape;
        public CssGeometryBox Box;
        public CalcNode[]? Insets; // 上右下左
        public RadiusValue[]? Radii;
        public CssShapeRadiusKind Kx, Ky;
        public CalcNode? Rx, Ry, Cx, Cy;
        public CssFillRule FillRule;
        public CalcNode[]? Points; // x0 y0 x1 y1 …
        public string? Text;
        public CssClipPath? Computed;
    }

    const int MaxPoints = 10000;

    public static CssValue? ClipPath(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new ClipPathValue { Kind = CssClipPathKind.None, Computed = CssClipPath.None };
        var first = r.Peek();
        if (first is CssToken { Type: CssTokenType.Url } u)
        {
            r.Advance();
            return new ClipPathValue { Kind = CssClipPathKind.Url, Computed = CssClipPath.FromUrl(u.Value) };
        }
        if (first is CssFunction uf && CssSerializer.EqualsIgnoreAsciiCase(uf.Name, "url"))
        {
            var ur = new ValueReader(uf.Arguments);
            if (ur.Next() is not CssToken { Type: CssTokenType.String } s || !ur.AtEnd) return null;
            r.Advance();
            return new ClipPathValue { Kind = CssClipPathKind.Url, Computed = CssClipPath.FromUrl(s.Value) };
        }
        ClipPathValue? shape = null;
        CssGeometryBox? box = null;
        for (int i = 0; i < 2 && !r.AtEnd; i++)
        {
            var v = r.Peek()!;
            if (box is null && v is CssToken { Type: CssTokenType.Ident } id && TryBox(CssSerializer.ToAsciiLower(id.Value), out var b)) { r.Advance(); box = b; continue; }
            if (shape is null && v is CssFunction f)
            {
                shape = Shape(f);
                if (shape is null) return null;
                r.Advance();
                continue;
            }
            return null;
        }
        if (shape is null && box is null) return null;
        if (shape is null) return new ClipPathValue { Kind = CssClipPathKind.Box, Box = box!.Value, Computed = CssClipPath.FromBox(box.Value) };
        shape.Box = box ?? CssGeometryBox.BorderBox;
        return shape;
    }

    static bool TryBox(string k, out CssGeometryBox b)
    {
        switch (k)
        {
            case "border-box": b = CssGeometryBox.BorderBox; return true;
            case "padding-box": b = CssGeometryBox.PaddingBox; return true;
            case "content-box": b = CssGeometryBox.ContentBox; return true;
            case "margin-box": b = CssGeometryBox.MarginBox; return true;
            case "fill-box": b = CssGeometryBox.FillBox; return true;
            case "stroke-box": b = CssGeometryBox.StrokeBox; return true;
            case "view-box": b = CssGeometryBox.ViewBox; return true;
            default: b = default; return false;
        }
    }

    static CalcNode? Lp(CssComponentValue? v, bool nonNegative = false) => ValueParsers.LengthPercentageNode(v, nonNegative);

    static CalcNode Sub(CalcNode a, params CalcNode[] minus)
    {
        var ops = new CalcNode[minus.Length + 1];
        ops[0] = a;
        for (int i = 0; i < minus.Length; i++) ops[i + 1] = new CalcOperation(CalcOperation.Op.Negate, minus[i].Category, new[] { minus[i] });
        return new CalcOperation(CalcOperation.Op.Sum, CalcCategory.LengthPercent, ops);
    }

    static ClipPathValue? Shape(CssFunction f)
    {
        var name = CssSerializer.ToAsciiLower(f.Name);
        var r = new ValueReader(f.Arguments);
        switch (name)
        {
            case "inset":
            {
                var vals = new List<CalcNode>(4);
                while (!r.AtEnd && vals.Count < 4 && !(r.Peek() is CssToken t && t.IsIdent("round")))
                {
                    var n = Lp(r.Next());
                    if (n is null) return null;
                    vals.Add(n);
                }
                if (vals.Count == 0) return null;
                var top = vals[0];
                var right = vals.Count > 1 ? vals[1] : top;
                var bottom = vals.Count > 2 ? vals[2] : top;
                var left = vals.Count > 3 ? vals[3] : right;
                var radii = Round(ref r);
                if (radii is null || !r.AtEnd) return null;
                return new ClipPathValue { Kind = CssClipPathKind.Shape, Shape = CssBasicShapeKind.Inset, Insets = new[] { top, right, bottom, left }, Radii = radii };
            }
            case "xywh":
            {
                var x = Lp(r.Next());
                var y = Lp(r.Next());
                var w = Lp(r.Next(), nonNegative: true);
                var h = Lp(r.Next(), nonNegative: true);
                if (x is null || y is null || w is null || h is null) return null;
                var radii = Round(ref r);
                if (radii is null || !r.AtEnd) return null;
                var hundred = CalcLeaf.Percent(100);
                return new ClipPathValue
                {
                    Kind = CssClipPathKind.Shape, Shape = CssBasicShapeKind.Inset,
                    Insets = new[] { y, Sub(hundred, x, w), Sub(hundred, y, h), x }, Radii = radii,
                };
            }
            case "rect":
            {
                var v = new CalcNode?[4];
                for (int i = 0; i < 4; i++)
                {
                    var c = r.Next();
                    if (c is CssToken t && t.IsIdent("auto")) { v[i] = null; continue; }
                    v[i] = Lp(c);
                    if (v[i] is null) return null;
                }
                var radii = Round(ref r);
                if (radii is null || !r.AtEnd) return null;
                var zero = CalcLeaf.Percent(0);
                var hundred = CalcLeaf.Percent(100);
                return new ClipPathValue
                {
                    Kind = CssClipPathKind.Shape, Shape = CssBasicShapeKind.Inset,
                    Insets = new[] { v[0] ?? zero, v[1] is null ? zero : Sub(hundred, v[1]!), v[2] is null ? zero : Sub(hundred, v[2]!), v[3] ?? zero },
                    Radii = radii,
                };
            }
            case "circle":
            case "ellipse":
            {
                bool circle = name == "circle";
                var res = new ClipPathValue { Kind = CssClipPathKind.Shape, Shape = circle ? CssBasicShapeKind.Circle : CssBasicShapeKind.Ellipse };
                res.Kx = res.Ky = CssShapeRadiusKind.ClosestSide;
                int radii = 0;
                while (!r.AtEnd && !(r.Peek() is CssToken at && at.IsIdent("at")))
                {
                    if (radii == (circle ? 1 : 2)) return null;
                    var c = r.Next();
                    CssShapeRadiusKind kind;
                    CalcNode? node = null;
                    if (c is CssToken t && t.IsIdent("closest-side")) kind = CssShapeRadiusKind.ClosestSide;
                    else if (c is CssToken t2 && t2.IsIdent("farthest-side")) kind = CssShapeRadiusKind.FarthestSide;
                    else
                    {
                        node = Lp(c, nonNegative: true);
                        if (node is null) return null;
                        kind = CssShapeRadiusKind.Length;
                    }
                    if (radii == 0) { res.Kx = kind; res.Rx = node; }
                    else { res.Ky = kind; res.Ry = node; }
                    radii++;
                }
                if (!circle && radii == 1) return null;
                if (circle) { res.Ky = res.Kx; res.Ry = res.Rx; }
                res.Cx = res.Cy = CalcLeaf.Percent(50);
                if (r.TryIdent("at"))
                {
                    if (!ValueParsers.Position(ref r, out var cx, out var cy)) return null;
                    res.Cx = cx;
                    res.Cy = cy;
                }
                return r.AtEnd ? res : null;
            }
            case "polygon":
            {
                var res = new ClipPathValue { Kind = CssClipPathKind.Shape, Shape = CssBasicShapeKind.Polygon };
                var rule = r.PeekIdentLower();
                if (rule is "nonzero" or "evenodd")
                {
                    r.Advance();
                    res.FillRule = rule == "evenodd" ? CssFillRule.EvenOdd : CssFillRule.NonZero;
                    if (!r.TryComma()) return null;
                }
                var pts = new List<CalcNode>();
                while (true)
                {
                    var x = Lp(r.Next());
                    var y = Lp(r.Next());
                    if (x is null || y is null || pts.Count >= MaxPoints * 2) return null;
                    pts.Add(x);
                    pts.Add(y);
                    if (r.AtEnd) break;
                    if (!r.TryComma()) return null;
                }
                res.Points = pts.ToArray();
                return res;
            }
            case "path":
            {
                var res = new ClipPathValue { Kind = CssClipPathKind.Shape, Shape = CssBasicShapeKind.Path };
                var rule = r.PeekIdentLower();
                if (rule is "nonzero" or "evenodd")
                {
                    r.Advance();
                    res.FillRule = rule == "evenodd" ? CssFillRule.EvenOdd : CssFillRule.NonZero;
                    if (!r.TryComma()) return null;
                }
                if (r.Next() is not CssToken { Type: CssTokenType.String } s || !r.AtEnd) return null;
                res.Text = s.Value;
                return res;
            }
        }
        return null;
    }

    /// <summary>可选的 <c>round &lt;'border-radius'&gt;</c>；没有时为四个 0。语法错误返回 null。</summary>
    static RadiusValue[]? Round(ref ValueReader r)
    {
        var zero = new RadiusValue(CalcLeaf.Length(CssLength.Px(0)), CalcLeaf.Length(CssLength.Px(0)));
        if (!r.TryIdent("round")) return new[] { zero, zero, zero, zero };
        var rest = new List<CssComponentValue>();
        while (!r.AtEnd) rest.Add(r.Next()!);
        var output = new List<PropertyValue>(4);
        var ids = CssProperties.Get(CssPropertyId.BorderRadius).LonghandIds;
        if (!Shorthands.BorderRadius(rest, output, ids) || output.Count != 4) return null;
        var result = new RadiusValue[4];
        for (int i = 0; i < 4; i++) result[i] = (RadiusValue)output[i].Value;
        return result;
    }

    public static CssClipPath Compute(CssValue v, in LengthContext ctx)
    {
        var c = (ClipPathValue)v;
        if (c.Computed is not null) return c.Computed;
        LengthPercentage L(CalcNode? n, in LengthContext x) => n is null ? default : n.Compute(x).ToLengthPercentage();
        switch (c.Shape)
        {
            case CssBasicShapeKind.Inset:
            {
                var radii = new CornerRadius[4];
                for (int i = 0; i < 4; i++) radii[i] = new CornerRadius(NonNeg(L(c.Radii![i].Horizontal, ctx)), NonNeg(L(c.Radii[i].Vertical, ctx)));
                return CssClipPath.Inset(L(c.Insets![0], ctx), L(c.Insets[1], ctx), L(c.Insets[2], ctx), L(c.Insets[3], ctx), radii, c.Box);
            }
            case CssBasicShapeKind.Circle:
            case CssBasicShapeKind.Ellipse:
                return CssClipPath.Ellipse(c.Shape == CssBasicShapeKind.Circle, c.Kx, L(c.Rx, ctx), c.Ky, L(c.Ry, ctx), new PositionValue(L(c.Cx, ctx), L(c.Cy, ctx)), c.Box);
            case CssBasicShapeKind.Polygon:
            {
                var pts = new (LengthPercentage, LengthPercentage)[c.Points!.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = (L(c.Points[2 * i], ctx), L(c.Points[2 * i + 1], ctx));
                return CssClipPath.Polygon(c.FillRule, pts, c.Box);
            }
            default:
                return CssClipPath.Path(c.FillRule, c.Text!, c.Box);
        }

        static LengthPercentage NonNeg(LengthPercentage lp) => lp.IsFixed && lp.Length < 0 ? LengthPercentage.Zero : lp;
    }
}

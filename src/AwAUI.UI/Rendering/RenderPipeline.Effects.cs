using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;

namespace AwAUI.UI;

/// <summary>
/// 命中测试的坐标空间：每个带变换或 <c>clip-path</c> 的层叠上下文一个。表项的形状与裁剪位于所属空间（该元素未变换的布局坐标）；
/// 空间记录从父空间到本空间的逆变换、进入本空间之前（父空间中）累积的裁剪，以及本空间中的裁剪路径。
/// 一次命中测试中每个空间只把点变换一次（按查询序号缓存）。
/// </summary>
internal sealed class HitSpace
{
    public HitSpace(HitSpace? parent, in Matrix3x2 inverse, bool hasInverse, Rect parentClip, in RoundRect parentClipShape, VectorPath? clipPath)
    {
        Parent = parent;
        Inverse = inverse;
        HasInverse = hasInverse;
        ParentClip = parentClip;
        ParentClipShape = parentClipShape;
        ClipPath = clipPath;
    }

    public HitSpace? Parent { get; }

    /// <summary>父空间 → 本空间。</summary>
    public Matrix3x2 Inverse { get; }
    public bool HasInverse { get; }

    /// <summary>进入本空间之前（父空间中）全部祖先裁剪的轴对齐交集与最内层的圆角裁剪。</summary>
    public Rect ParentClip { get; }
    public RoundRect ParentClipShape { get; }

    /// <summary>本空间中的 <c>clip-path</c>（没有时为 null）。</summary>
    public VectorPath? ClipPath { get; }

    // 每次查询的缓存
    internal long QueryId;
    internal Point LocalPoint;
    internal bool Inside;
}

public sealed partial class RenderPipeline
{
    /// <summary>
    /// 曾经出现过几何效果（变换、滤镜）：之后损伤矩形与几何查询要沿祖先链映射到根空间。没有任何效果的界面不付出这部分代价。
    /// </summary>
    bool _effectsSeen;

    /// <summary>当前录制所在的命中空间（null 为根空间）。</summary>
    HitSpace? _hitSpace;

    /// <summary>当前所在的变换上下文层数：大于 0 时录制单元按绝对坐标录制（见 <see cref="PaintUnit"/>）。</summary>
    int _transformDepth;

    long _hitQuery;

    /// <summary>沿祖先链映射损伤 / 几何时的步数上限；超出时整帧重画（极深的树）。</summary>
    const int MaxMapSteps = 100_000;

    /// <summary>本轮绘制中的背景滤镜：根空间中的读取范围（形状外扩滤镜范围）。用于把损伤扩大到整个读取范围。</summary>
    readonly List<Rect> _backdrops = new();

    // ------------------------------------------------------------ 样式判定

    /// <summary>元素的变换属性（transform / translate / rotate / scale）是否不为 none。</summary>
    static bool HasTransformStyle(ComputedStyle s) => !s.Transform.IsNone || !s.Translate.IsNone || !s.Rotate.IsNone || !s.Scale.IsNone;

    /// <summary>
    /// 除 z-index、opacity、fixed / sticky 之外建立层叠上下文的属性（CSS 2.1 附录 E 之后各规范的补充）：变换、perspective、
    /// transform-style: preserve-3d、filter、backdrop-filter、clip-path、mix-blend-mode、isolation: isolate、contain: layout / paint，
    /// 以及 will-change 中提到其中任何一个。
    /// </summary>
    internal static bool CreatesEffectContext(ComputedStyle s) =>
        HasTransformStyle(s) || s.Perspective >= 0 || s.TransformStyle == CssTransformStyle.Preserve3d
        || !s.Filter.IsNone || !s.BackdropFilter.IsNone || s.ClipPath.Kind != CssClipPathKind.None
        || s.MixBlendMode != CssBlendMode.Normal || s.Isolation == CssIsolation.Isolate
        || (s.Contain & (CssContain.Layout | CssContain.Paint)) != 0 || WillChangeCreatesContext(s.WillChange);

    static bool WillChangeCreatesContext(CssList<string> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case "transform": case "translate": case "rotate": case "scale": case "perspective": case "opacity":
                case "filter": case "backdrop-filter": case "clip-path": case "mix-blend-mode": case "isolation":
                    return true;
            }
        }
        return false;
    }

    /// <summary>元素的几何效果（变换、clip-path）是否把它变成一个独立的裁剪作用域：这样的上下文不按「逃逸」处理（见 <see cref="ComputeClipEscapes"/>）。</summary>
    static bool HasGeometryEffects(ComputedStyle s) =>
        HasTransformStyle(s) || s.ClipPath.Kind != CssClipPathKind.None || !s.Filter.IsNone || !s.BackdropFilter.IsNone;

    static BlendMode ToBlendMode(CssBlendMode m) => m switch
    {
        CssBlendMode.Multiply => BlendMode.Multiply,
        CssBlendMode.Screen => BlendMode.Screen,
        CssBlendMode.Overlay => BlendMode.Overlay,
        CssBlendMode.Darken => BlendMode.Darken,
        CssBlendMode.Lighten => BlendMode.Lighten,
        CssBlendMode.ColorDodge => BlendMode.ColorDodge,
        CssBlendMode.ColorBurn => BlendMode.ColorBurn,
        CssBlendMode.HardLight => BlendMode.HardLight,
        CssBlendMode.SoftLight => BlendMode.SoftLight,
        CssBlendMode.Difference => BlendMode.Difference,
        CssBlendMode.Exclusion => BlendMode.Exclusion,
        CssBlendMode.Hue => BlendMode.Hue,
        CssBlendMode.Saturation => BlendMode.Saturation,
        CssBlendMode.Color => BlendMode.Color,
        CssBlendMode.Luminosity => BlendMode.Luminosity,
        CssBlendMode.PlusLighter => BlendMode.Plus,
        _ => BlendMode.SrcOver,
    };

    // ------------------------------------------------------------ 效果缓存

    /// <summary>
    /// 按当前计算样式与几何（重新）计算盒的效果缓存（<see cref="RenderBox.Transform"/>、<see cref="RenderBox.Filter"/> 等）。
    /// 样式与几何都没变时直接返回。
    /// </summary>
    internal void EnsureEffects(RenderBox b)
    {
        var s = b.Kind == BoxKind.AnonymousText ? null : b.Style;
        if (ReferenceEquals(b.EffectsStyle, s) && b.EffectsBorder.Equals(b.Border) && b.EffectsScale == _scale) return;
        b.EffectsStyle = s;
        b.EffectsBorder = b.Border;
        b.EffectsScale = _scale;
        b.HasTransform = false;
        b.Transform = b.InverseTransform = Matrix3x2.Identity;
        b.EffectsInvisible = false;
        b.Filter = b.BackdropFilter = null;
        b.ClipPath = null;
        b.HasEffects = false;
        // display: contents 没有盒，变换、滤镜与裁剪都不适用（CSS Transforms 1 的「可变换元素」）
        if (s is null || !b.HasBox || b.Kind == BoxKind.Inline || s.Display == CssDisplay.Contents) return;

        if (HasTransformStyle(s))
        {
            var m4 = BoxTransform(b, s);
            // 3D 变换按正交投影压平到 2D（丢弃 z 与透视）；背面朝前且 backface-visibility: hidden 时不绘制
            var m = CssTransforms.To2D(m4);
            if (!IsFinite(m) || s.BackfaceVisibility == CssBackfaceVisibility.Hidden && m4.M33 < 0)
            {
                b.EffectsInvisible = true;
            }
            else if (!m.IsIdentity)
            {
                b.HasTransform = true;
                b.Transform = m;
                if (Matrix3x2.Invert(m, out var inv) && IsFinite(inv)) b.InverseTransform = inv;
                else b.EffectsInvisible = true;
            }
        }
        if (!s.Filter.IsNone && !s.Filter.HasUrl) b.Filter = ToFilter(s, s.Filter);
        if (!s.BackdropFilter.IsNone && !s.BackdropFilter.HasUrl) b.BackdropFilter = ToFilter(s, s.BackdropFilter);
        if (s.ClipPath.Kind is CssClipPathKind.Shape or CssClipPathKind.Box)
        {
            b.ClipPath = BuildClipPath(b, s, s.ClipPath);
            // 无法构造的形状（path() 等）不裁剪
        }
        b.HasEffects = b.HasTransform || b.EffectsInvisible || b.Filter is not null || b.BackdropFilter is not null || b.ClipPath is not null;
        if (b.HasEffects) _effectsSeen = true;
    }

    static Matrix4x4 BoxTransform(RenderBox b, ComputedStyle s)
    {
        var reference = s.TransformBox is CssTransformBox.ContentBox or CssTransformBox.FillBox
            ? new Rect(b.ContentX, b.ContentY, b.ContentWidth, b.ContentHeight)
            : b.Border;
        return CssTransforms.GetTransform(s, reference);
    }

    static bool IsFinite(in Matrix3x2 m) =>
        IsFinite(m.M11) && IsFinite(m.M12) && IsFinite(m.M21) && IsFinite(m.M22) && IsFinite(m.M31) && IsFinite(m.M32);

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>CSS 滤镜函数 → 渲染器的滤镜链。drop-shadow 的模糊半径是标准差的两倍；颜色按 currentColor 解析。</summary>
    static Filter? ToFilter(ComputedStyle s, CssFilterList list)
    {
        var ops = new List<FilterOperation>(list.Functions.Count);
        foreach (var f in list.Functions)
        {
            switch (f.Kind)
            {
                case CssFilterKind.Blur: ops.Add(FilterOperation.Blur(f.Amount)); break;
                case CssFilterKind.Brightness: ops.Add(FilterOperation.Brightness(f.Amount)); break;
                case CssFilterKind.Contrast: ops.Add(FilterOperation.Contrast(f.Amount)); break;
                case CssFilterKind.Grayscale: ops.Add(FilterOperation.Grayscale(f.Amount)); break;
                case CssFilterKind.HueRotate: ops.Add(FilterOperation.HueRotate(f.Amount)); break;
                case CssFilterKind.Invert: ops.Add(FilterOperation.Invert(f.Amount)); break;
                case CssFilterKind.Opacity: ops.Add(FilterOperation.Opacity(f.Amount)); break;
                case CssFilterKind.Saturate: ops.Add(FilterOperation.Saturate(f.Amount)); break;
                case CssFilterKind.Sepia: ops.Add(FilterOperation.Sepia(f.Amount)); break;
                case CssFilterKind.DropShadow:
                    ops.Add(FilterOperation.DropShadow(f.OffsetX, f.OffsetY, f.Amount / 2, s.ResolveColor(f.Color)));
                    break;
            }
        }
        return ops.Count == 0 ? null : new Filter(ops);
    }

    /// <summary>
    /// 滤镜让内容扩展的保守距离（逻辑像素）：与渲染器的实际半径（3σ，加上降采样的对齐与双线性放大）相比留有余量，
    /// 墨迹与损伤据此计算。
    /// </summary>
    Rect InflateForFilter(Filter f, Rect r)
    {
        if (r.Width <= 0 && r.Height <= 0) return r;
        float slack = 4f / Math.Max(0.25f, _scale) + 1;
        float l = 0, t = 0, ri = 0, bo = 0;
        foreach (var op in f.Operations)
        {
            if (op.Kind == FilterKind.ColorMatrix) continue;
            float ex = op.StdDeviationX > 0 ? 4 * op.StdDeviationX + slack : 0;
            float ey = op.StdDeviationY > 0 ? 4 * op.StdDeviationY + slack : 0;
            if (op.Kind == FilterKind.Blur)
            {
                l += ex; ri += ex; t += ey; bo += ey;
            }
            else
            {
                l += Math.Max(0, ex - op.OffsetX) + 1;
                ri += Math.Max(0, ex + op.OffsetX) + 1;
                t += Math.Max(0, ey - op.OffsetY) + 1;
                bo += Math.Max(0, ey + op.OffsetY) + 1;
            }
        }
        const float max = 1e6f;
        return Rect.FromLTRB(r.Left - Math.Min(l, max), r.Top - Math.Min(t, max), r.Right + Math.Min(ri, max), r.Bottom + Math.Min(bo, max));
    }

    /// <summary>
    /// 由内容墨迹（元素自己的未变换坐标）得到 <see cref="RenderBox.Ink"/>：按滤镜外扩、按变换映射；不可见（奇异变换 / 背面隐藏）时为空。
    /// </summary>
    void FinishInk(RenderBox b, Rect contentInk)
    {
        b.ContentInkLocal = contentInk.Width <= 0 && contentInk.Height <= 0 ? default : contentInk.Offset(-b.Border.X, -b.Border.Y);
        EnsureEffects(b);
        if (!b.HasEffects)
        {
            b.Ink = contentInk;
            return;
        }
        if (b.EffectsInvisible)
        {
            b.Ink = default;
            return;
        }
        var r = contentInk;
        if (b.Filter is { } f) r = InflateForFilter(f, r);
        if (b.HasTransform && (r.Width > 0 || r.Height > 0)) r = r.TransformBounds(b.Transform);
        if (!r.IsFinite) r = Rect.FromLTRB(-1e7f, -1e7f, 1e7f, 1e7f);
        b.Ink = r;
    }

    // ------------------------------------------------------------ clip-path

    /// <summary>clip-path 的参考盒（绝对、未变换的布局坐标）及其圆角形状。</summary>
    RoundRect ReferenceBox(RenderBox b, ComputedStyle s, CssGeometryBox box)
    {
        var outer = BoxPainter.OuterShape(s, b.Border);
        switch (box)
        {
            case CssGeometryBox.PaddingBox:
                return BoxPainter.InnerShape(outer, BoxPainter.GetBorderWidths(s, _scale));
            case CssGeometryBox.ContentBox:
            case CssGeometryBox.FillBox:
            {
                var bw = BoxPainter.GetBorderWidths(s, _scale);
                var r = b.Border;
                float l = b.ContentX - r.Left, t = b.ContentY - r.Top;
                float rr = r.Right - (b.ContentX + b.ContentWidth), bb = r.Bottom - (b.ContentY + b.ContentHeight);
                return BoxPainter.InnerShape(outer, new BoxPainter.BorderWidths(Math.Max(bw.Top, t), Math.Max(bw.Right, rr), Math.Max(bw.Bottom, bb), Math.Max(bw.Left, l)));
            }
            case CssGeometryBox.MarginBox:
            {
                var m = b.Layout?.Margin;
                if (m is null) return outer;
                var r = b.Border;
                var grown = Rect.FromLTRB(r.Left - Math.Max(0, m.Value.Left), r.Top - Math.Max(0, m.Value.Top), r.Right + Math.Max(0, m.Value.Right), r.Bottom + Math.Max(0, m.Value.Bottom));
                return new RoundRect(grown, outer.TopLeft, outer.TopRight, outer.BottomRight, outer.BottomLeft);
            }
            default:
                return outer;
        }
    }

    /// <summary>clip-path 的基本形状 → 路径（绝对坐标）。<c>path()</c> 与无效的形状返回 null（不裁剪）。</summary>
    VectorPath? BuildClipPath(RenderBox b, ComputedStyle s, CssClipPath cp)
    {
        var refShape = ReferenceBox(b, s, cp.GeometryBox);
        var box = refShape.Rect;
        if (cp.Kind == CssClipPathKind.Box)
            return refShape.IsRect ? VectorPath.FromRect(box) : VectorPath.FromRoundRect(refShape);
        float w = box.Width, h = box.Height;
        switch (cp.ShapeKind)
        {
            case CssBasicShapeKind.Inset:
            {
                float t = cp.InsetTop.Resolve(h), r = cp.InsetRight.Resolve(w), bo = cp.InsetBottom.Resolve(h), l = cp.InsetLeft.Resolve(w);
                // 两侧之和超过盒的尺寸时按比例缩小（CSS Shapes 1）
                if (l + r > w && l + r > 0) { float k = w / (l + r); l *= k; r *= k; }
                if (t + bo > h && t + bo > 0) { float k = h / (t + bo); t *= k; bo *= k; }
                var rect = Rect.FromLTRB(box.Left + l, box.Top + t, Math.Max(box.Left + l, box.Right - r), Math.Max(box.Top + t, box.Bottom - bo));
                if (!rect.IsFinite) return null;
                var radii = cp.InsetRadii;
                if (radii.Count == 4)
                {
                    Size Corner(CornerRadius c)
                    {
                        float x = c.Horizontal.Resolve(rect.Width), y = c.Vertical.Resolve(rect.Height);
                        return x > 0 && y > 0 && IsFinite(x) && IsFinite(y) ? new Size(x, y) : default;
                    }
                    var rr = new RoundRect(rect, Corner(radii[0]), Corner(radii[1]), Corner(radii[2]), Corner(radii[3]));
                    if (!rr.IsRect) return VectorPath.FromRoundRect(rr);
                }
                return VectorPath.FromRect(rect);
            }
            case CssBasicShapeKind.Circle:
            {
                float cx = box.Left + cp.Center.X.Resolve(w), cy = box.Top + cp.Center.Y.Resolve(h);
                float dl = cx - box.Left, dr = box.Right - cx, dt = cy - box.Top, db = box.Bottom - cy;
                float radius = cp.RadiusXKind switch
                {
                    CssShapeRadiusKind.ClosestSide => Math.Min(Math.Min(Math.Abs(dl), Math.Abs(dr)), Math.Min(Math.Abs(dt), Math.Abs(db))),
                    CssShapeRadiusKind.FarthestSide => Math.Max(Math.Max(Math.Abs(dl), Math.Abs(dr)), Math.Max(Math.Abs(dt), Math.Abs(db))),
                    _ => cp.RadiusX.Resolve((float)Math.Sqrt((w * w + h * h) / 2)),
                };
                if (!(radius > 0) || !IsFinite(radius) || !IsFinite(cx) || !IsFinite(cy)) return EmptyClip(box);
                return VectorPath.FromEllipse(new Rect(cx - radius, cy - radius, 2 * radius, 2 * radius));
            }
            case CssBasicShapeKind.Ellipse:
            {
                float cx = box.Left + cp.Center.X.Resolve(w), cy = box.Top + cp.Center.Y.Resolve(h);
                float dl = Math.Abs(cx - box.Left), dr = Math.Abs(box.Right - cx), dt = Math.Abs(cy - box.Top), db = Math.Abs(box.Bottom - cy);
                float rx = cp.RadiusXKind switch
                {
                    CssShapeRadiusKind.ClosestSide => Math.Min(dl, dr),
                    CssShapeRadiusKind.FarthestSide => Math.Max(dl, dr),
                    _ => cp.RadiusX.Resolve(w),
                };
                float ry = cp.RadiusYKind switch
                {
                    CssShapeRadiusKind.ClosestSide => Math.Min(dt, db),
                    CssShapeRadiusKind.FarthestSide => Math.Max(dt, db),
                    _ => cp.RadiusY.Resolve(h),
                };
                if (!(rx > 0) || !(ry > 0) || !IsFinite(rx) || !IsFinite(ry) || !IsFinite(cx) || !IsFinite(cy)) return EmptyClip(box);
                return VectorPath.FromEllipse(new Rect(cx - rx, cy - ry, 2 * rx, 2 * ry));
            }
            case CssBasicShapeKind.Polygon:
            {
                var pts = cp.Points;
                if (pts.Count < 3) return EmptyClip(box);
                var pb = new PathBuilder();
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = new Point(box.Left + pts[i].X.Resolve(w), box.Top + pts[i].Y.Resolve(h));
                    if (!IsFinite(p.X) || !IsFinite(p.Y)) return EmptyClip(box);
                    if (i == 0) pb.MoveTo(p); else pb.LineTo(p);
                }
                pb.Close();
                var path = pb.ToPath();
                return cp.FillRule == CssFillRule.EvenOdd ? path.WithFillRule(FillRule.EvenOdd) : path;
            }
            default:
                return null;
        }
    }

    /// <summary>面积为 0 的形状：裁掉全部内容（与浏览器一致：半径为 0 的圆什么都不显示）。</summary>
    static VectorPath EmptyClip(Rect box) => VectorPath.FromRect(new Rect(box.Left, box.Top, 0, 0));

    // ------------------------------------------------------------ 坐标映射

    /// <summary>
    /// 以当前墨迹使用的变换和滤镜，把损伤从 <paramref name="from"/> 的未变换空间映射到根。
    /// </summary>
    Rect MapDamageToRoot(Element? from, Rect r)
    {
        int steps = 0;
        for (var x = from; x is not null; x = x.Parent)
        {
            if (++steps > MaxMapSteps) return Rect.FromLTRB(-1e7f, -1e7f, 1e7f, 1e7f);
            var xb = x.Render;
            if (xb is null) continue;
            if (!xb.HasEffects) continue;
            if (xb.Filter is { } f) r = InflateForFilter(f, r);
            if (xb.HasTransform && (r.Width > 0 || r.Height > 0)) r = r.TransformBounds(xb.Transform);
        }
        return r;
    }

    /// <summary>位于 <paramref name="space"/>（某元素的子元素坐标空间）中的损伤矩形计入根空间的损伤。</summary>
    void AddDamageIn(Element? space, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        if (_effectsSeen && space is not null) r = MapDamageToRoot(space, r);
        AddDamage(r);
    }

    /// <summary>盒的墨迹（位于其父元素的空间）映射到根空间。</summary>
    Rect InkInRoot(RenderBox b) => _effectsSeen ? MapDamageToRoot(b.Element.Parent, b.Ink) : b.Ink;

    /// <summary>
    /// 元素边框盒（逻辑像素）在窗口中的包围盒：计入自身与祖先的 CSS 变换（transform / translate / rotate / scale）与滚动偏移。
    /// 没有变换时等于 <see cref="Element.BoundingRect"/>。供 <c>getBoundingClientRect</c>、浮层定位与 DevTools 使用；
    /// 在 <see cref="UIRoot.LayoutCompleted"/> 中调用得到本帧的结果（变换按当前计算样式即时求出）。
    /// 3D 变换按正交投影压平；奇异变换下得到退化的矩形。
    /// </summary>
    public Rect GetTransformedBounds(Element element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        // 合成后再取一次包围盒：相邻的旋转可以互相抵消。
        return element.BoundingRect.TransformBounds(GetElementTransform(element));
    }

    /// <summary>
    /// 从元素自己未变换的布局坐标（与 <see cref="Element.BoundingRect"/> 同一坐标系）到窗口坐标的变换（自身与祖先的变换之积）。
    /// 没有变换时为单位矩阵。
    /// </summary>
    public Matrix3x2 GetElementTransform(Element element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        var m = Matrix3x2.Identity;
        int steps = 0;
        for (var x = element; x is not null && ++steps <= MaxMapSteps; x = x.Parent)
        {
            if (x.Render is not { HasBox: true, Style: { } s } box || box.Kind is BoxKind.Inline or BoxKind.AnonymousText
                || s.Display == CssDisplay.Contents || !HasTransformStyle(s)) continue;
            // 布局完成回调需要本帧的变换；绘制仍需要上一帧的效果缓存来映射旧墨迹。
            var local = CssTransforms.To2D(BoxTransform(box, s));
            if (IsFinite(local)) m *= local;
        }
        return m;
    }

    /// <summary>
    /// 把窗口坐标的点映射到元素自己未变换的布局坐标（<see cref="GetElementTransform"/> 的逆）。变换不可逆时返回 false。
    /// </summary>
    public bool TryMapToElement(Element element, Point windowPoint, out Point local)
    {
        var m = GetElementTransform(element);
        if (!Matrix3x2.Invert(m, out var inv))
        {
            local = default;
            return false;
        }
        local = Transform(windowPoint, inv);
        return true;
    }

    static Point Transform(Point p, in Matrix3x2 m) => new(p.X * m.M11 + p.Y * m.M21 + m.M31, p.X * m.M12 + p.Y * m.M22 + m.M32);

    // ------------------------------------------------------------ 绘制

    /// <summary>
    /// 进入层叠上下文的几何效果（在外部裁剪链之后、录制单元之外）：变换、clip-path、背景滤镜。命中空间与剔除矩形随之切换。
    /// 返回 false 表示元素不可见（奇异变换、背面隐藏），调用方跳过它与它的后代。
    /// </summary>
    bool EnterEffects(RenderBox ctx, ref EffectsScope scope)
    {
        scope = new EffectsScope(_hitSpace, _cull, _hitClip, _hitClipShape, entered: false, transformed: false);
        EnsureEffects(ctx);
        if (!ctx.HasEffects) return true;
        if (ctx.EffectsInvisible) return false;
        var s = ctx.Style!;
        if (ctx.HasTransform)
        {
            _transformDepth++;
            scope = new EffectsScope(scope.Space, scope.Cull, scope.HitClip, scope.HitClipShape, entered: false, transformed: true);
            _dc?.Transform(ctx.Transform);
            var c = _cull.TransformBounds(ctx.InverseTransform);
            _cull = c.IsFinite ? c : new Rect(-1e7f, -1e7f, 2e7f, 2e7f);
        }
        if (ctx.ClipPath is { } clip) _dc?.ClipPath(clip);
        if (ctx.HasTransform || ctx.ClipPath is not null)
        {
            _hitSpace = new HitSpace(_hitSpace, ctx.InverseTransform, true, _hitClip, _hitClipShape, ctx.ClipPath);
            _hitClip = s_noClip;
            _hitClipShape = default;
            scope = new EffectsScope(scope.Space, scope.Cull, scope.HitClip, scope.HitClipShape, entered: true, scope.Transformed);
        }
        if (ctx.BackdropFilter is { } backdrop && s.Opacity > 0 && _suppress == 0 && s.Visibility == CssVisibility.Visible)
        {
            var shape = BoxPainter.OuterShape(s, ctx.Border);
            _dc?.DrawBackdrop(shape, backdrop, Math.Min(1, s.Opacity));
            // 读取范围（根空间）：损伤一旦触及就扩大到整个范围（见 ExpandBackdropDamage）
            var read = InflateForFilter(backdrop, shape.Rect);
            if (ctx.ClipPath is { } cp) read = read.Intersect(InflateForFilter(backdrop, cp.Bounds));
            _backdrops.Add(MapDamageToRoot(ctx.Element, read));
        }
        return true;
    }

    void LeaveEffects(in EffectsScope scope)
    {
        _cull = scope.Cull;
        if (scope.Transformed) _transformDepth--;
        if (!scope.Entered) return;
        _hitSpace = scope.Space;
        _hitClip = scope.HitClip;
        _hitClipShape = scope.HitClipShape;
    }

    readonly struct EffectsScope
    {
        public EffectsScope(HitSpace? space, Rect cull, Rect hitClip, in RoundRect hitClipShape, bool entered, bool transformed)
        {
            Space = space;
            Cull = cull;
            HitClip = hitClip;
            HitClipShape = hitClipShape;
            Entered = entered;
            Transformed = transformed;
        }

        /// <summary>进入时增加了 <see cref="_transformDepth"/>。</summary>
        public bool Transformed { get; }

        public HitSpace? Space { get; }
        public Rect Cull { get; }
        public Rect HitClip { get; }
        public RoundRect HitClipShape { get; }
        public bool Entered { get; }
    }

    /// <summary>
    /// 背景滤镜读取它下面已经画好的像素（不受裁剪限制）：损伤触及某个背景滤镜的读取范围时，整个读取范围都要重画，
    /// 否则局部重绘会读到上一帧（含元素自身）的像素。反复扩大直到稳定（损伤只增不减，至多每个背景滤镜一次）。
    /// </summary>
    void ExpandBackdropDamage()
    {
        if (_backdrops.Count == 0 || _damageAll) return;
        if (_backdrops.Count > 4096)
        {
            _damageAll = true;
            return;
        }
        for (int round = 0; round <= _backdrops.Count; round++)
        {
            bool changed = false;
            foreach (var r in _backdrops)
            {
                if (_damage.Width <= 0 && _damage.Height <= 0) return;
                if (!r.Intersects(_damage) || ContainsRect(_damage, r)) continue;
                _damage = Union(_damage, r);
                changed = true;
            }
            if (!changed) return;
        }
    }

    // ------------------------------------------------------------ 只改合成属性的样式变化

    /// <summary>本帧只改了合成属性（变换、不透明度、滤镜……）的盒：绘制前更新它们的墨迹（见 <see cref="OnCompositeOnlyChange"/>）。</summary>
    readonly List<RenderBox> _compositeChanged = new();

    /// <summary>
    /// 样式变化只涉及合成属性（变换类、opacity、filter、backdrop-filter、clip-path、mix-blend-mode）时：元素自己的录制单元不受影响
    /// （这些效果在单元之外施加，见 <see cref="PaintStackingContext"/>），所以不给元素打绘制脏标记，而是只作废外层的缓存（重新合成）、
    /// 把旧墨迹计入损伤，并在绘制前按新效果更新墨迹。返回 false 表示不适用（调用方按普通的绘制变化处理）。
    /// 不需要重新布局：<see cref="FrameTimings.LayoutRan"/> 为 false，元素的 DisplayList 被复用。
    /// </summary>
    bool OnCompositeOnlyChange(Element e, RenderBox box, ComputedStyle? old, ComputedStyle next)
    {
        if (!_incremental || old is null || e.Parent is null || !box.HasBox || box.Kind is BoxKind.Inline or BoxKind.AnonymousText) return false;
        if (!CompositeOnlyChange(old, next)) return false;
        DamageBox(box);
        if (e.Parent.Render is { } pb) InvalidateCache(pb);
        _compositeChanged.Add(box);
        _scrollRepaint = true;   // 没有脏标记也要重画（与滚动的几何平移相同的机制；样式阶段在帧内，本帧就会绘制）
        return true;
    }

    /// <summary>
    /// 两个计算样式是否只在合成属性上不同：其余分组的值相等（分组对象不一定共享，按值比较）、效果分组中的其他属性相等，
    /// 且变换分组或效果分组确实变了。
    /// </summary>
    static bool CompositeOnlyChange(ComputedStyle a, ComputedStyle b)
    {
        static bool Same<T>(T x, T y) where T : class, IEquatable<T> => ReferenceEquals(x, y) || x.Equals(y);
        if (!Same(a.BoxGroup, b.BoxGroup) || !Same(a.PositionGroup, b.PositionGroup) || !Same(a.FlexGroup, b.FlexGroup)
            || !Same(a.BorderGroup, b.BorderGroup) || !Same(a.BackgroundGroup, b.BackgroundGroup)
            || !Same(a.AnimationGroup, b.AnimationGroup) || !Same(a.BehaviorGroup, b.BehaviorGroup)
            || !Same(a.FontGroup, b.FontGroup) || !Same(a.TextGroup, b.TextGroup)
            || !Same(a.InteractionGroup, b.InteractionGroup) || !ReferenceEquals(a.CustomProperties, b.CustomProperties))
            return false;
        bool transform = !Same(a.TransformGroup, b.TransformGroup);
        var x = a.EffectsGroup;
        var y = b.EffectsGroup;
        if (Same(x, y)) return transform;
        return x.BoxShadow.Equals(y.BoxShadow) && x.TextOverflow == y.TextOverflow && x.OutlineWidth.Equals(y.OutlineWidth)
               && x.OutlineStyle == y.OutlineStyle && x.OutlineColor.Equals(y.OutlineColor) && x.OutlineOffset.Equals(y.OutlineOffset)
               && x.ObjectFit == y.ObjectFit && x.ObjectPosition.Equals(y.ObjectPosition)
               && x.TextDecorationLine == y.TextDecorationLine && x.TextDecorationStyle == y.TextDecorationStyle
               && x.TextDecorationColor.Equals(y.TextDecorationColor) && x.TextDecorationThickness.Equals(y.TextDecorationThickness)
               && x.MaskImage.Equals(y.MaskImage) && x.MaskMode.Equals(y.MaskMode) && x.MaskRepeat == y.MaskRepeat && x.MaskPosition == y.MaskPosition
               && x.MaskClip.Equals(y.MaskClip) && x.MaskOrigin.Equals(y.MaskOrigin) && x.MaskSize == y.MaskSize
               && x.MaskComposite.Equals(y.MaskComposite) && x.MaskType == y.MaskType;
    }

    /// <summary>绘制前：只改了合成属性的盒按新效果更新墨迹（内容墨迹不变），祖先的墨迹重新合并。</summary>
    void ApplyCompositeChanges()
    {
        if (_compositeChanged.Count == 0) return;
        foreach (var b in _compositeChanged)
        {
            var e = b.Element;
            if (!e.IsAttached || !ReferenceEquals(e.Root, _root) || !b.HasBox) continue;
            FinishInk(b, b.ContentInk);
            if (e.Parent is { } p) RecomputeInkUpward(p);
        }
        _compositeChanged.Clear();
    }

    // ------------------------------------------------------------ 命中测试

    readonly List<HitSpace> _spaceChain = new();

    /// <summary>
    /// 点是否通过命中表项所在空间链上的全部裁剪；<paramref name="local"/> 为点在表项空间中的坐标。
    /// 从最外层未求过的空间开始逐层求（显式列表，不递归：嵌套的变换可以很深），每个空间按查询序号缓存结果。
    /// </summary>
    bool PassesSpaces(HitSpace space, Point root, out Point local)
    {
        var chain = _spaceChain;
        chain.Clear();
        for (var s = space; s is not null && s.QueryId != _hitQuery; s = s.Parent) chain.Add(s);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var s = chain[i];
            bool inside = true;
            var p = root;
            if (s.Parent is { } parent)
            {
                inside = parent.Inside;
                p = parent.LocalPoint;
            }
            if (inside)
            {
                if (!s.ParentClip.Contains(p)) inside = false;
                else if (!s.ParentClipShape.IsRect && !Contains(s.ParentClipShape, p)) inside = false;
            }
            var lp = s.HasInverse ? Transform(p, s.Inverse) : p;
            if (inside && s.ClipPath is { } cp && !PathContains(cp, lp)) inside = false;
            s.QueryId = _hitQuery;
            s.LocalPoint = lp;
            s.Inside = inside;
        }
        chain.Clear();
        local = space.LocalPoint;
        return space.Inside;
    }

    /// <summary>点是否在路径内（按填充规则；曲线展平为折线）。</summary>
    internal static bool PathContains(VectorPath path, Point p)
    {
        if (!path.Bounds.Contains(p)) return false;
        int winding = 0;
        var verbs = path.Verbs;
        var pts = path.Points;
        int pi = 0;
        Point start = default, cur = default;
        bool open = false;
        void Edge(Point a, Point b)
        {
            if (a.Y <= p.Y)
            {
                if (b.Y > p.Y && Cross(a, b, p) > 0) winding++;
            }
            else if (b.Y <= p.Y && Cross(a, b, p) < 0) winding--;
        }
        for (int i = 0; i < verbs.Length; i++)
        {
            switch (verbs[i])
            {
                case PathVerb.Move:
                    if (open) Edge(cur, start);
                    start = cur = pts[pi++];
                    open = true;
                    break;
                case PathVerb.Line:
                {
                    var n = pts[pi++];
                    Edge(cur, n);
                    cur = n;
                    break;
                }
                case PathVerb.Quad:
                {
                    Point c = pts[pi], e = pts[pi + 1];
                    pi += 2;
                    var prev = cur;
                    for (int k = 1; k <= 16; k++)
                    {
                        float t = k / 16f, u = 1 - t;
                        var q = new Point(u * u * cur.X + 2 * u * t * c.X + t * t * e.X, u * u * cur.Y + 2 * u * t * c.Y + t * t * e.Y);
                        Edge(prev, q);
                        prev = q;
                    }
                    cur = e;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Point c1 = pts[pi], c2 = pts[pi + 1], e = pts[pi + 2];
                    pi += 3;
                    var prev = cur;
                    for (int k = 1; k <= 16; k++)
                    {
                        float t = k / 16f, u = 1 - t;
                        float a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
                        var q = new Point(a * cur.X + b * c1.X + c * c2.X + d * e.X, a * cur.Y + b * c1.Y + c * c2.Y + d * e.Y);
                        Edge(prev, q);
                        prev = q;
                    }
                    cur = e;
                    break;
                }
                case PathVerb.Close:
                    if (open) Edge(cur, start);
                    cur = start;
                    open = false;
                    break;
            }
        }
        if (open) Edge(cur, start);
        return path.FillRule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
    }

    static float Cross(Point a, Point b, Point p) => (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);

    // ------------------------------------------------------------ 输入相关的计算值

    /// <summary>
    /// 元素实际生效的 <c>user-select</c>（CSS UI 4）：<c>auto</c> 在可编辑元素（输入框）上为 <c>contain</c>，
    /// 否则取父元素的实际值为 <c>all</c> / <c>none</c> 时的值，其余为 <c>text</c>。文本选择与拖选的起点据此判断（<c>none</c> 不开始选择）。
    /// </summary>
    public CssUserSelect GetUsedUserSelect(Element element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        int steps = 0;
        for (var e = element.IsTextNode ? element.Parent : element; e is not null && ++steps <= MaxMapSteps; e = e.Parent)
        {
            var s = e.Render?.Style;
            if (s is null) continue;
            var v = s.UserSelect;
            if (v != CssUserSelect.Auto) return v;
            if (e is InputElement) return CssUserSelect.Contain;
            var parent = e.Parent?.Render?.Style;
            if (parent is null || parent.UserSelect is not (CssUserSelect.All or CssUserSelect.None or CssUserSelect.Auto)) return CssUserSelect.Text;
        }
        return CssUserSelect.Text;
    }

    /// <summary>
    /// 元素上触摸手势允许的操作（CSS <c>touch-action</c> 的实际值）：从元素到最近的滚动容器（含），各元素的取值求交
    /// （Pointer Events 3 §「Determining supported touch behavior」）。<c>auto</c> 为全部允许；结果含 <see cref="CssTouchAction.None"/> 时不做任何默认手势。
    /// </summary>
    public CssTouchAction GetEffectiveTouchAction(Element element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        const CssTouchAction all = CssTouchAction.PanX | CssTouchAction.PanY | CssTouchAction.PinchZoom;
        var allowed = all;
        int steps = 0;
        for (var e = element.IsTextNode ? element.Parent : element; e is not null && ++steps <= MaxMapSteps; e = e.Parent)
        {
            var s = e.Render?.Style;
            if (s is not null) allowed &= Normalize(s.TouchAction);
            if (allowed == 0) return CssTouchAction.None;
            if (ScrollAxes(e) != 0) break;
        }
        return allowed == all ? CssTouchAction.Auto : allowed;

        static CssTouchAction Normalize(CssTouchAction v)
        {
            if (v == CssTouchAction.Auto) return all;
            if ((v & CssTouchAction.None) != 0) return 0;
            if ((v & CssTouchAction.Manipulation) != 0) return all;
            var r = (CssTouchAction)0;
            if ((v & (CssTouchAction.PanX | CssTouchAction.PanLeft | CssTouchAction.PanRight)) != 0) r |= CssTouchAction.PanX;
            if ((v & (CssTouchAction.PanY | CssTouchAction.PanUp | CssTouchAction.PanDown)) != 0) r |= CssTouchAction.PanY;
            if ((v & CssTouchAction.PinchZoom) != 0) r |= CssTouchAction.PinchZoom;
            return r;
        }
    }
}

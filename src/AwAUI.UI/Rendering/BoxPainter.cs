using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using CssShadow = AwAUI.Css.Values.Shadow;

namespace AwAUI.UI;

/// <summary>
/// 盒绘制器（design/05「绘制与层」）：按计算样式与布局结果录制一个元素的背景、边框、圆角、盒阴影与轮廓。
/// 所有矩形为绝对逻辑坐标，已对齐设备像素；边框宽度按 CSS 规则向下对齐到设备像素（非零时至少 1 个设备像素）。
/// </summary>
internal static class BoxPainter
{
    /// <summary>四边（上、右、下、左）对齐到设备像素后的边框宽度；样式为 none / hidden 时为 0。</summary>
    public readonly struct BorderWidths
    {
        public BorderWidths(float top, float right, float bottom, float left)
        {
            Top = top;
            Right = right;
            Bottom = bottom;
            Left = left;
        }

        public float Top { get; }
        public float Right { get; }
        public float Bottom { get; }
        public float Left { get; }
        public bool IsZero => Top <= 0 && Right <= 0 && Bottom <= 0 && Left <= 0;
    }

    public static float SnapWidth(float w, float scale)
    {
        if (!(w > 0) || float.IsInfinity(w)) return 0;
        float device = (float)Math.Floor(w * scale + 1e-3f);
        return Math.Max(1, device) / scale;
    }

    static bool Visible(CssLineStyle s) => s is not (CssLineStyle.None or CssLineStyle.Hidden);

    public static BorderWidths GetBorderWidths(ComputedStyle s, float scale) => new(
        Visible(s.BorderTopStyle) ? SnapWidth(s.BorderTopWidth, scale) : 0,
        Visible(s.BorderRightStyle) ? SnapWidth(s.BorderRightWidth, scale) : 0,
        Visible(s.BorderBottomStyle) ? SnapWidth(s.BorderBottomWidth, scale) : 0,
        Visible(s.BorderLeftStyle) ? SnapWidth(s.BorderLeftWidth, scale) : 0);

    static Size Corner(CornerRadius r, Rect rect)
    {
        float h = r.Horizontal.Resolve(rect.Width), v = r.Vertical.Resolve(rect.Height);
        if (!(h > 0) || !(v > 0) || float.IsInfinity(h) || float.IsInfinity(v)) return default;
        return new Size(h, v);
    }

    /// <summary>边框盒的圆角矩形（百分比半径相对边框盒；重叠时按 CSS 等比缩小）。</summary>
    public static RoundRect OuterShape(ComputedStyle s, Rect r) =>
        new(r, Corner(s.BorderTopLeftRadius, r), Corner(s.BorderTopRightRadius, r),
            Corner(s.BorderBottomRightRadius, r), Corner(s.BorderBottomLeftRadius, r));

    /// <summary>内边距盒（边框内缘）的圆角矩形：内缘半径 = 外缘半径 − 相邻边框宽度（不小于 0）。</summary>
    public static RoundRect InnerShape(in RoundRect outer, in BorderWidths b)
    {
        var r = outer.Rect;
        var rect = Rect.FromLTRB(r.Left + b.Left, r.Top + b.Top, Math.Max(r.Left + b.Left, r.Right - b.Right), Math.Max(r.Top + b.Top, r.Bottom - b.Bottom));
        if (outer.IsRect) return new RoundRect(rect, 0);
        static Size Shrink(Size s, float w, float h) => new(Math.Max(0, s.Width - w), Math.Max(0, s.Height - h));
        return new RoundRect(rect, Shrink(outer.TopLeft, b.Left, b.Top), Shrink(outer.TopRight, b.Right, b.Top),
            Shrink(outer.BottomRight, b.Right, b.Bottom), Shrink(outer.BottomLeft, b.Left, b.Bottom));
    }

    /// <summary>外阴影 → 背景色 → 背景图层（渐变）→ 内阴影 → 边框。</summary>
    public static void PaintBox(DrawingContext dc, ComputedStyle s, Rect border, float scale, bool skipBackgroundColor)
    {
        if (border.Width <= 0 && border.Height <= 0) return;
        var outer = OuterShape(s, border);
        var widths = GetBorderWidths(s, scale);
        var shadows = s.BoxShadow;

        // 1. 外阴影：列表中的第一个在最上面，所以倒序绘制
        if (!shadows.IsNone)
        {
            for (int i = shadows.Shadows.Count - 1; i >= 0; i--)
            {
                var sh = shadows.Shadows[i];
                if (sh.Inset) continue;
                dc.DrawBoxShadow(outer, ToBoxShadow(s, sh));
            }
        }

        // 2. 背景色（background-clip: border-box）
        if (!skipBackgroundColor)
        {
            var bg = s.ResolveColor(s.BackgroundColor);
            if (!bg.IsTransparent) dc.FillRoundRect(outer, bg);
        }

        // 3. 背景图层：第一层在最上面
        var images = s.BackgroundImage;
        if (!images.IsNone)
        {
            var padding = InnerShape(outer, widths).Rect;
            for (int i = images.Layers.Count - 1; i >= 0; i--)
            {
                if (images.Layers[i] is not Gradient g) continue; // url() 图片：UI 层不解码图片（第 2 期）
                var brush = CreateGradientBrush(s, g, padding);
                if (brush is not null) dc.FillRoundRect(outer, new Paint(brush));
            }
        }

        // 4. 内阴影：画在内边距盒内
        if (!shadows.IsNone)
        {
            RoundRect? inner = null;
            for (int i = shadows.Shadows.Count - 1; i >= 0; i--)
            {
                var sh = shadows.Shadows[i];
                if (!sh.Inset) continue;
                inner ??= InnerShape(outer, widths);
                dc.DrawBoxShadow(inner.Value, ToBoxShadow(s, sh));
            }
        }

        // 5. 边框
        if (!widths.IsZero) PaintBorder(dc, s, outer, widths);
    }

    static BoxShadow ToBoxShadow(ComputedStyle s, in CssShadow sh) =>
        new(sh.OffsetX, sh.OffsetY, sh.Blur, sh.Spread, s.ResolveColor(sh.Color), sh.Inset);

    // ------------------------------------------------------------ 边框

    static void PaintBorder(DrawingContext dc, ComputedStyle s, in RoundRect outer, in BorderWidths w)
    {
        var inner = InnerShape(outer, w);
        var ct = s.ResolveColor(s.BorderTopColor);
        var cr = s.ResolveColor(s.BorderRightColor);
        var cb = s.ResolveColor(s.BorderBottomColor);
        var cl = s.ResolveColor(s.BorderLeftColor);
        var st = s.BorderTopStyle;
        bool uniformStyle = st == s.BorderRightStyle && st == s.BorderBottomStyle && st == s.BorderLeftStyle;
        bool uniformColor = ct == cr && ct == cb && ct == cl;
        bool uniformWidth = w.Top == w.Right && w.Top == w.Bottom && w.Top == w.Left;

        if (uniformStyle && uniformColor && uniformWidth && st is CssLineStyle.Dashed or CssLineStyle.Dotted)
        {
            // 虚线 / 点线：沿边框中线描边
            float width = w.Top;
            var mid = new RoundRect(outer.Rect.Inflate(-width / 2), Half(outer.TopLeft, inner.TopLeft), Half(outer.TopRight, inner.TopRight),
                Half(outer.BottomRight, inner.BottomRight), Half(outer.BottomLeft, inner.BottomLeft));
            var stroke = st == CssLineStyle.Dashed
                ? new Stroke(width, StrokeCap.Butt, StrokeJoin.Miter, 4, new[] { width * 3, width * 3 })
                : new Stroke(width, StrokeCap.Round, StrokeJoin.Round, 4, new[] { 0f, width * 2 });
            if (mid.IsRect) dc.StrokeRect(mid.Rect, ct, stroke);
            else dc.StrokeRoundRect(mid, ct, stroke);
            return;
        }

        // 其余样式（solid；double / groove / ridge / inset / outset 第 1 期按 solid 绘制）
        if (uniformColor)
        {
            if (ct.IsTransparent) return;
            dc.FillPath(Ring(outer, inner), ct);
            return;
        }

        // 各边颜色不同：每条边是从外角到内角的梯形
        var o = outer.Rect;
        var i = inner.Rect;
        var ring = outer.IsRect && inner.IsRect ? null : Ring(outer, inner);
        PaintSide(dc, ring, ct, w.Top, new Point(o.Left, o.Top), new Point(o.Right, o.Top), new Point(i.Right, i.Top), new Point(i.Left, i.Top));
        PaintSide(dc, ring, cr, w.Right, new Point(o.Right, o.Top), new Point(o.Right, o.Bottom), new Point(i.Right, i.Bottom), new Point(i.Right, i.Top));
        PaintSide(dc, ring, cb, w.Bottom, new Point(o.Right, o.Bottom), new Point(o.Left, o.Bottom), new Point(i.Left, i.Bottom), new Point(i.Right, i.Bottom));
        PaintSide(dc, ring, cl, w.Left, new Point(o.Left, o.Bottom), new Point(o.Left, o.Top), new Point(i.Left, i.Top), new Point(i.Left, i.Bottom));
    }

    static Size Half(Size a, Size b) => a.IsEmpty ? a : new Size((a.Width + b.Width) / 2, (a.Height + b.Height) / 2);

    static void PaintSide(DrawingContext dc, VectorPath? ring, Color color, float width, Point a, Point b, Point c, Point d)
    {
        if (width <= 0 || color.IsTransparent) return;
        var trapezoid = new PathBuilder().MoveTo(a).LineTo(b).LineTo(c).LineTo(d).Close().ToPath();
        if (ring is null)
        {
            dc.FillPath(trapezoid, color);
            return;
        }
        // 圆角：用梯形裁剪圆环
        dc.Save();
        dc.ClipPath(trapezoid);
        dc.FillPath(ring, color);
        dc.Restore();
    }

    /// <summary>外圆角矩形减去内圆角矩形（奇偶规则）。</summary>
    static VectorPath Ring(in RoundRect outer, in RoundRect inner)
    {
        var pb = new PathBuilder { FillRule = FillRule.EvenOdd };
        pb.AddRoundRect(outer);
        if (inner.Rect.Width > 0 && inner.Rect.Height > 0) pb.AddRoundRect(inner);
        return pb.ToPath();
    }

    // ------------------------------------------------------------ 轮廓

    /// <summary>
    /// <c>outline</c>：画在边框盒之外 <c>outline-offset</c> 处，不占布局空间；圆角随边框盒的圆角外扩。
    /// </summary>
    public static void PaintOutline(DrawingContext dc, ComputedStyle s, Rect border, float scale)
    {
        var style = s.OutlineStyle;
        if (!Visible(style)) return;
        float width = SnapWidth(s.OutlineWidth, scale);
        if (width <= 0) return;
        var color = s.ResolveColor(s.OutlineColor);
        if (color.IsTransparent) return;
        float offset = s.OutlineOffset.Resolve(0);
        if (float.IsNaN(offset) || float.IsInfinity(offset)) offset = 0;
        offset = (float)Math.Round(offset * scale) / scale;
        var shape = OuterShape(s, border);
        var inner = Grow(shape, offset);
        var outer = Grow(shape, offset + width);
        if (style is CssLineStyle.Dashed or CssLineStyle.Dotted)
        {
            var mid = Grow(shape, offset + width / 2);
            var stroke = style == CssLineStyle.Dashed
                ? new Stroke(width, StrokeCap.Butt, StrokeJoin.Miter, 4, new[] { width * 3, width * 3 })
                : new Stroke(width, StrokeCap.Round, StrokeJoin.Round, 4, new[] { 0f, width * 2 });
            dc.StrokeRoundRect(mid, color, stroke);
            return;
        }
        dc.FillPath(Ring(outer, inner), color);
    }

    static RoundRect Grow(in RoundRect r, float d)
    {
        var rect = r.Rect.Inflate(d);
        if (rect.Width <= 0 || rect.Height <= 0) return new RoundRect(new Rect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height)), 0);
        static Size G(Size s, float d) => s.IsEmpty ? s : new Size(Math.Max(0, s.Width + d), Math.Max(0, s.Height + d));
        return new RoundRect(rect, G(r.TopLeft, d), G(r.TopRight, d), G(r.BottomRight, d), G(r.BottomLeft, d));
    }

    // ------------------------------------------------------------ 渐变

    /// <summary>CSS 渐变 → 画刷。<paramref name="area"/> 是背景定位区域（内边距盒）。退化的渐变返回纯色画刷或 null。</summary>
    internal static Brush? CreateGradientBrush(ComputedStyle s, Gradient g, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        switch (g)
        {
            case LinearGradient lin:
            {
                double angle = lin.Corner switch
                {
                    GradientCorner.TopRight => Math.Atan2(area.Height, area.Width) * 180 / Math.PI,
                    GradientCorner.BottomRight => 180 - Math.Atan2(area.Height, area.Width) * 180 / Math.PI,
                    GradientCorner.BottomLeft => 180 + Math.Atan2(area.Height, area.Width) * 180 / Math.PI,
                    GradientCorner.TopLeft => 360 - Math.Atan2(area.Height, area.Width) * 180 / Math.PI,
                    _ => lin.AngleDegrees,
                };
                if (double.IsNaN(angle) || double.IsInfinity(angle)) angle = 180;
                double rad = angle * Math.PI / 180;
                float dx = (float)Math.Sin(rad), dy = (float)-Math.Cos(rad);
                float length = Math.Abs(area.Width * dx) + Math.Abs(area.Height * dy);
                if (!(length > 0)) return null;
                var c = area.Center;
                var start = new Vector2(c.X - dx * length / 2, c.Y - dy * length / 2);
                var dir = new Vector2(dx, dy);
                var stops = ResolveStops(s, g, length, out float lo, out float hi);
                if (stops is null) return null;
                if (stops.Length == 1) return new SolidColorBrush(stops[0].Color);
                var p0 = start + dir * lo;
                var p1 = start + dir * hi;
                return new LinearGradientBrush(new Point(p0.X, p0.Y), new Point(p1.X, p1.Y), stops, g.Repeating ? SpreadMode.Repeat : SpreadMode.Pad);
            }
            case RadialGradient radial:
            {
                float cx = area.X + radial.CenterX.Resolve(area.Width);
                float cy = area.Y + radial.CenterY.Resolve(area.Height);
                if (!IsFinite(cx) || !IsFinite(cy)) return null;
                RadialRadii(radial, area, cx, cy, out float rx, out float ry);
                if (!(rx > 0) || !(ry > 0) || !IsFinite(rx) || !IsFinite(ry))
                {
                    // 退化的椭圆：按规范画成最后一个色标的颜色
                    var last = LastColor(s, g);
                    return last is { } lc ? new SolidColorBrush(lc) : null;
                }
                var list = ResolveStopList(s, g, rx);
                if (list is null) return null;
                if (list.Count == 1) return new SolidColorBrush(list[0].Color);
                // 渐变空间：以圆心为原点的圆，y 方向按 ry/rx 拉伸得到椭圆。径向渐变的半径不能为负：
                // 非重复时按 [0, max(rx, 末色标)] 归一化（负位置截到 0）；重复时周期为 [首色标, 末色标]，
                // 首色标大于 0 时近似为从 0 开始的周期（相位差未补偿）
                float firstPos = Math.Max(0, list[0].Pos), lastPos = list[list.Count - 1].Pos;
                float radius = g.Repeating ? lastPos - firstPos : Math.Max(rx, lastPos);
                if (!(radius > 1e-4f)) return new SolidColorBrush(list[list.Count - 1].Color);
                float origin = g.Repeating ? firstPos : 0;
                var stops = new GradientStop[list.Count];
                for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(Math.Max(0, (list[i].Pos - origin) / radius), list[i].Color);
                var transform = Matrix3x2.CreateScale(1, ry / rx) * Matrix3x2.CreateTranslation(cx, cy);
                return new RadialGradientBrush(new Point(0, 0), radius, stops, g.Repeating ? SpreadMode.Repeat : SpreadMode.Pad, transform);
            }
            default:
                return null;
        }
    }

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    static Color? LastColor(ComputedStyle s, Gradient g)
    {
        for (int i = g.Stops.Count - 1; i >= 0; i--)
            if (!g.Stops[i].IsHint) return s.ResolveColor(g.Stops[i].Color);
        return null;
    }

    static void RadialRadii(RadialGradient g, Rect area, float cx, float cy, out float rx, out float ry)
    {
        float left = Math.Abs(cx - area.Left), right = Math.Abs(area.Right - cx);
        float top = Math.Abs(cy - area.Top), bottom = Math.Abs(area.Bottom - cy);
        bool circle = g.Shape == RadialShape.Circle;
        switch (g.Size)
        {
            case RadialSizeKeyword.Explicit:
                rx = g.RadiusX.Resolve(area.Width);
                ry = circle ? rx : g.RadiusY.Resolve(area.Height);
                return;
            case RadialSizeKeyword.ClosestSide:
            {
                float sx = Math.Min(left, right), sy = Math.Min(top, bottom);
                if (circle) rx = ry = Math.Min(sx, sy);
                else { rx = sx; ry = sy; }
                return;
            }
            case RadialSizeKeyword.FarthestSide:
            {
                float sx = Math.Max(left, right), sy = Math.Max(top, bottom);
                if (circle) rx = ry = Math.Max(sx, sy);
                else { rx = sx; ry = sy; }
                return;
            }
            case RadialSizeKeyword.ClosestCorner:
            {
                float sx = Math.Min(left, right), sy = Math.Min(top, bottom);
                if (circle) rx = ry = (float)Math.Sqrt(sx * sx + sy * sy);
                else { rx = sx * 1.41421356f; ry = sy * 1.41421356f; }
                return;
            }
            default: // farthest-corner
            {
                float sx = Math.Max(left, right), sy = Math.Max(top, bottom);
                if (circle) rx = ry = (float)Math.Sqrt(sx * sx + sy * sy);
                else { rx = sx * 1.41421356f; ry = sy * 1.41421356f; }
                return;
            }
        }
    }

    /// <summary>
    /// 色标位置的 CSS 修正（css-images-4 §3.5.3）：首尾缺省为 0% / 100%，位置不小于之前的最大值，缺省的中间色标均匀分布；
    /// 插值提示近似为该位置上两侧颜色各半的色标。返回按 [lo, hi] 归一化的色标（lo/hi 为渐变线上的像素位置）。
    /// </summary>
    static GradientStop[]? ResolveStops(ComputedStyle s, Gradient g, float length, out float lo, out float hi)
    {
        lo = hi = 0;
        var list = ResolveStopList(s, g, length);
        if (list is null) return null;
        if (list.Count == 1) return new[] { new GradientStop(0, list[0].Color) };

        float firstPos = list[0].Pos, lastPos = list[list.Count - 1].Pos;
        if (g.Repeating)
        {
            lo = firstPos;
            hi = lastPos;
        }
        else
        {
            lo = Math.Min(0, firstPos);
            hi = Math.Max(length, lastPos);
        }
        if (!(hi - lo > 1e-4f)) return new[] { new GradientStop(0, list[list.Count - 1].Color) };
        var stops = new GradientStop[list.Count];
        float span = hi - lo;
        for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop((list[i].Pos - lo) / span, list[i].Color);
        return stops;
    }

    /// <summary>色标修正后的（像素位置, 颜色）列表；没有颜色色标时为 null。</summary>
    static List<(float Pos, Color Color)>? ResolveStopList(ComputedStyle s, Gradient g, float length)
    {
        var src = g.Stops;
        int n = src.Count;
        if (n == 0) return null;
        var pos = new float[n];
        var known = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (src[i].Position is { } p)
            {
                float v = p.Resolve(length);
                if (IsFinite(v)) { pos[i] = v; known[i] = true; }
            }
        }
        // 首尾颜色色标
        int first = -1, last = -1;
        for (int i = 0; i < n; i++)
        {
            if (src[i].IsHint) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return null;
        if (!known[first]) { pos[first] = 0; known[first] = true; }
        if (!known[last]) { pos[last] = length; known[last] = true; }
        // 单调不减
        float max = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (!known[i]) continue;
            if (pos[i] < max) pos[i] = max;
            max = pos[i];
        }
        // 缺省位置均匀分布
        for (int i = first; i <= last;)
        {
            if (known[i]) { i++; continue; }
            int a = i - 1, b = i;
            while (b <= last && !known[b]) b++;
            int count = b - a;
            for (int k = a + 1; k < b; k++) { pos[k] = pos[a] + (pos[b] - pos[a]) * (k - a) / count; known[k] = true; }
            i = b;
        }

        var list = new List<(float Pos, Color Color)>(n);
        for (int i = first; i <= last; i++)
        {
            if (!src[i].IsHint)
            {
                list.Add((pos[i], s.ResolveColor(src[i].Color)));
                continue;
            }
            // 插值提示：找前后颜色色标，放一个各半颜色的色标
            int prev = i - 1, next = i + 1;
            while (prev >= first && src[prev].IsHint) prev--;
            while (next <= last && src[next].IsHint) next++;
            if (prev < first || next > last) continue;
            var c0 = s.ResolveColor(src[prev].Color);
            var c1 = s.ResolveColor(src[next].Color);
            float h = Math.Max(pos[prev], Math.Min(pos[next], pos[i]));
            list.Add((h, Color.Lerp(c0, c1, 0.5f)));
        }
        return list.Count == 0 ? null : list;
    }
}
